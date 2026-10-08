using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using EmptyEngine.Core.RuntimeLink;

namespace EmptyEngine.Host;

/// <summary>ライブ編集セッションの起動・再起動・終了を管理する</summary>
internal sealed class HostSession : IAsyncDisposable
{
    private const int HubWebSocketPort = SceneWire.DefaultHubWebSocketPort;

    /// <summary>エディタが listen し終えたときに stdout へ出す 1 行</summary>
    private static readonly Regex EditorListening =
        new(@"^Editor UI listening on (http://\S+?)/?$", RegexOptions.Compiled);

    private readonly string _manifestPath;
    private readonly string _configuration;
    private readonly int _uiPortOverride;
    /// <summary>起動時にブラウザを開かないか</summary>
    private readonly bool _noBrowser;

    private readonly HostConsole _console;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ChildProcessJob? _childJob;
    private ChildProcessLedger? _childLedger;

    private EmptyEngineProject? _project;
    private EditorRuntimeHub? _hub;
    private Process? _runtimeProcess;
    private Process? _editorProcess;
    private int _uiAnnounced;

    /// <param name="manifestPath">開くプロジェクト（*.emptyengine）。</param>
    public HostSession(string manifestPath, string configuration, int uiPortOverride, bool noBrowser)
    {
        _manifestPath = manifestPath;
        _configuration = configuration;
        _uiPortOverride = uiPortOverride;
        _noBrowser = noBrowser;
        _console = new HostConsole();
    }

    /// <summary>この Host のログと接続状態を表示するコンソール</summary>
    internal HostConsole Console => _console;

    /// <summary>マニフェストの読み込みとハブ・ランタイム・エディタの起動</summary>
    public async Task<bool> StartAsync()
    {
        if (!TryLoadProject())
        {
            return false;
        }

        await WithLockAsync(StartAllAsync);

        _ = WarnIfUiNeverAnnouncedAsync();
        return true;
    }

    /// <summary>管理する子プロセスとハブを停止する</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _lifetime.Cancel();
            AppendLog("[Host] Shutting down managed processes...");
            await StopAllProcessesAsync();
            _hub?.Dispose();
            _hub = null;
        }
        catch (Exception ex)
        {
            AppendLog($"[Host] Shutdown error: {ex.Message}", HostLogSeverity.Error);
        }
        finally
        {
            _lifetime.Dispose();
            _console.Release();
        }
    }

    private bool TryLoadProject()
    {
        try
        {
            _project = EmptyEngineProject.Load(_manifestPath);
            StartFileLogging(_project);
            AppendLog($"[Host] Project: {_project.ManifestPath}");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"[Host] Failed to load project manifest '{_manifestPath}': {ex.Message}", HostLogSeverity.Error);
            return false;
        }
    }

    private void StartFileLogging(EmptyEngineProject project)
    {
        string path = Path.Combine(project.ProjectDirectory, ".artifacts", "logs", "host.log");
        try
        {
            _console.StartFileLogging(path);
            AppendLog($"[Host] Session log: {path}");
        }
        catch (Exception ex)
        {
            AppendLog($"[Host] Failed to create session log '{path}': {ex.Message}", HostLogSeverity.Warning);
        }
    }

    /// <summary>指定のスコープの建て直し</summary>
    public async Task RestartAsync(RestartScope scope)
    {
        await WithLockAsync(async () =>
        {
            AppendLog($"[Host] Restart requested ({scope}).");

            if (_project is null)
            {
                AppendLog("[Host] No project loaded. Cannot restart.", HostLogSeverity.Error);
                return;
            }

            switch (scope)
            {
                case RestartScope.Runtime:
                    await StopProcessAsync(ProcessSlot.Runtime, "Runtime");
                    StartRuntimeWatch();
                    break;

                case RestartScope.Editor:
                    await StopProcessAsync(ProcessSlot.Editor, "Editor");
                    await EnsureEditorProjectGeneratedAsync();
                    StartEditorWatch();
                    break;

                default:
                    await StopAllProcessesAsync();
                    await StartAllAsync();
                    break;
            }

            PublishStatus();
        });
    }

    private async Task StartAllAsync()
    {
        if (_project is null)
        {
            AppendLog("[Host] No project loaded. Nothing to start.", HostLogSeverity.Error);
            return;
        }

        await EnsureNoStaleProcessesAsync();

        StartHub();

        await EnsureEditorProjectGeneratedAsync();

        StartEditorWatch();
        StartRuntimeWatch();
        PublishStatus();
    }

    private void StartHub()
    {
        if (_hub is not null)
        {
            return;
        }

        _hub = new EditorRuntimeHub(HubWebSocketPort, AppendLog);
        _hub.StateChanged += PublishStatus;
        _hub.Start();
    }

    private async Task EnsureEditorProjectGeneratedAsync()
    {
        await EditorProjectGenerator.GenerateAsync(RequireProject(), line => AppendLog(line));
    }

    private void StartRuntimeWatch()
    {
        if (IsRunning(_runtimeProcess))
        {
            AppendLog("[Host] Runtime watcher is already running.");
            return;
        }

        EmptyEngineProject project = RequireProject();

        (string fileName, string arguments) = project.SplitRuntimeCommand();
        var runtimeEnv = new Dictionary<string, string>
        {
            // C# は MSBuild プロパティとして拾って runtimeconfig へ焼き、他言語ランタイムは環境変数のまま読む。
            [SceneWire.EditorWebSocketProperty] =
                $"ws://{SceneWire.DefaultHubHost}:{HubWebSocketPort}{SceneWire.RuntimeWebSocketPath}"
        };
        AddWatchEnvironment(runtimeEnv);
        _runtimeProcess = StartManagedCommand(fileName, arguments, project.ProjectDirectory, "Runtime", runtimeEnv);
    }

    private void StartEditorWatch()
    {
        if (IsRunning(_editorProcess))
        {
            AppendLog("[Host] Editor watcher is already running.");
            return;
        }

        string editorProject = Path.GetFullPath(ResolveEditorProjectPath());
        string artifactsPath = EditorProjectGenerator.ResolveEditorArtifactsPath(RequireProject(), "editor-watch");

        var editorEnv = new Dictionary<string, string>();
        AddWatchEnvironment(editorEnv);

        var appArgs = new List<string>();
        if (_uiPortOverride > 0) appArgs.Add($"--ui-port {_uiPortOverride}");

        string arguments = $"watch --no-hot-reload --project {Quote(editorProject)} --artifacts-path {Quote(artifactsPath)} run --configuration {_configuration}"
            + (appArgs.Count == 0 ? string.Empty : " -- " + string.Join(' ', appArgs));
        _editorProcess = StartManagedCommand(
            "dotnet", arguments, Path.GetDirectoryName(editorProject)!, "Editor", editorEnv, AnnounceUiIfListening);
    }

    internal static void AddWatchEnvironment(Dictionary<string, string> environment)
    {
        environment["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "1";
        environment["DOTNET_CLI_FORCE_UTF8_ENCODING"] = "true";
        environment["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "1";
        // dotnet watch otherwise leaves reusable MSBuild worker nodes alive after each build.
        environment["MSBUILDDISABLENODEREUSE"] = "1";

        if (Environment.GetEnvironmentVariable("NO_COLOR") is null)
        {
            environment["MSBUILDTERMINALLOGGER"] = "true";
            environment["CARGO_TERM_COLOR"] = "always";
            environment["CLICOLOR_FORCE"] = "1";
            environment["FORCE_COLOR"] = "1";
        }
    }

    private async Task StopAllProcessesAsync()
    {
        await StopProcessAsync(ProcessSlot.Runtime, "Runtime");
        await StopProcessAsync(ProcessSlot.Editor, "Editor");
        await EnsureNoStaleProcessesAsync();
        PublishStatus();
    }

    private async Task WithLockAsync(Func<Task> action)
    {
        await _operationLock.WaitAsync();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppendLog($"[Host] Operation failed: {ex.Message}", HostLogSeverity.Error);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <param name="onLine">子の 1 行ごとに覗く（合図の拾い上げ）。行そのものは素通しする。</param>
    private Process StartManagedCommand(
        string fileName,
        string arguments,
        string workingDirectory,
        string label,
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? onLine = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        AppendLog($"[Host] Starting {label}: {fileName} {arguments}".TrimEnd());
        return StartManaged(startInfo, label, $"{label}: {fileName} {arguments}", environment, onLine);
    }

    private Process StartManaged(
        ProcessStartInfo startInfo,
        string label,
        string failureDescription,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onLine)
    {
        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        (Process process, StreamReader output, StreamReader error) = StartBoundToHostLifetime(startInfo, label, failureDescription);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            AppendLog($"[{label}] Watcher exited with code {process.ExitCode}",
                process.ExitCode == 0 ? HostLogSeverity.Normal : HostLogSeverity.Error);
            PublishStatus();
        };

        _ = PipeProcessOutputAsync(output, error, label, onLine);
        AppendLog($"[Host] Started {label} watcher PID={process.Id}");
        return process;
    }

    /// <summary>Host が後始末なしに死んでも子のツリーが残らない形での起動</summary>
    private (Process Process, StreamReader Output, StreamReader Error) StartBoundToHostLifetime(
        ProcessStartInfo startInfo, string label, string failureDescription)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _childJob ??= new ChildProcessJob();
                return _childJob.Start(startInfo);
            }
            catch (Exception ex)
            {
                // 縛れなくても起動は試みる。その子は下で控えに回す。
                AppendLog($"[Host] Could not start the {label} watcher bound to the Host lifetime ({ex.Message}). Starting it unbound.", HostLogSeverity.Warning);
            }
        }

        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {failureDescription}");
        try
        {
            // OS に縛らせていない子は控えておき、次回起動時に残りを片付ける。
            ChildLedger().Record(process);
        }
        catch (Exception ex)
        {
            AppendLog($"[Host] Could not record the {label} watcher for cleanup: {ex.Message}", HostLogSeverity.Warning);
        }

        return (process, process.StandardOutput, process.StandardError);
    }

    private async Task PipeProcessOutputAsync(StreamReader output, StreamReader error, string label, Action<string>? onLine)
    {
        await Task.WhenAll(
            Task.Run(() => DrainAsync(output, label, onLine)),
            Task.Run(() => DrainAsync(error, label, onLine)));
    }

    private async Task DrainAsync(StreamReader reader, string label, Action<string>? onLine)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            onLine?.Invoke(line);
            _console.WriteFromChild($"[{label}] {line}");
        }
    }

    private EmptyEngineProject RequireProject() =>
        _project ?? throw new InvalidOperationException("Project manifest is not loaded.");

    private string ResolveEditorProjectPath() =>
        EditorProjectGenerator.ResolveEditorProjectPath(RequireProject());

    private static bool IsRunning(Process? process)
    {
        return process is { HasExited: false };
    }

    private async Task EnsureNoStaleProcessesAsync()
    {
        if (_project is null)
        {
            return;
        }

        KillLedgeredChildren();
        await KillStaleWatchersAsync();
    }

    private ChildProcessLedger ChildLedger() =>
        _childLedger ??= new ChildProcessLedger(
            Path.Combine(RequireProject().ProjectDirectory, ".artifacts", "host-children.txt"));

    /// <summary>前回の Host が後始末なしに死んで残した子の片付け。管理中の子が居ないときにだけ呼ぶ</summary>
    private void KillLedgeredChildren()
    {
        try
        {
            IReadOnlyList<int> killed = ChildLedger().KillSurvivors();
            if (killed.Count > 0)
            {
                AppendLog($"[Host] Cleaned up {killed.Count} watcher(s) left by a previous Host: {string.Join(", ", killed)}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Host] Leftover watcher cleanup warning: {ex.Message}", HostLogSeverity.Warning);
        }
    }

    private async Task KillStaleWatchersAsync()
    {
        if (_project is null)
        {
            return;
        }

        string editorProject = ResolveEditorProjectPath();
        if (string.IsNullOrWhiteSpace(editorProject))
        {
            return;
        }

        try
        {
            IReadOnlyList<int> stale = await FindWatchersOfAsync(Path.GetFullPath(editorProject));
            if (stale.Count > 0)
            {
                AppendLog($"[Host] Cleaning up {stale.Count} stale editor watcher(s): {string.Join(", ", stale)}");
            }

            foreach (int pid in stale)
            {
                KillTree(pid);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Host] Stale watcher cleanup warning: {ex.Message}", HostLogSeverity.Warning);
        }
    }

    /// <summary>コマンドラインに <paramref name="editorProject"/> を含む dotnet プロセスの PID</summary>
    private static async Task<IReadOnlyList<int>> FindWatchersOfAsync(string editorProject)
    {
        if (OperatingSystem.IsWindows())
        {
            string script =
                "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | " +
                "Where-Object { $_.CommandLine -and $_.CommandLine.Contains(" + QuoteForPowerShell(editorProject) + ") } | " +
                "ForEach-Object { $_.ProcessId }";
            string pids = await ReadProcessOutputAsync(
                "powershell", "-NoProfile -NonInteractive -Command " + Quote(script));
            return ParsePids(pids, _ => true);
        }

        return SelectWatcherPids(
            await ReadProcessOutputAsync("/bin/ps", "-axww -o pid=,command="), editorProject);
    }

    /// <summary><c>ps</c> の出力からのエディタの watch に当たる PID の選び出し</summary>
    internal static List<int> SelectWatcherPids(string processTable, string editorProject) =>
        ParsePids(processTable, command =>
            command.Contains("dotnet", StringComparison.Ordinal)
            && command.Contains(editorProject, StringComparison.Ordinal));

    internal static List<int> ParsePids(string output, Func<string, bool> matches)
    {
        var pids = new List<int>();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int separator = line.IndexOf(' ', StringComparison.Ordinal);
            string head = separator < 0 ? line : line[..separator];
            string rest = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (int.TryParse(head, out int pid) && pid != Environment.ProcessId && matches(rest))
            {
                pids.Add(pid);
            }
        }

        return pids;
    }

    private static async Task<string> ReadProcessOutputAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}' to list processes.");
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    private static void KillTree(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
        }
    }

    private static string QuoteForPowerShell(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private enum ProcessSlot
    {
        Runtime,
        Editor,
    }

    private async Task StopProcessAsync(ProcessSlot slot, string label)
    {
        Process? process = slot switch
        {
            ProcessSlot.Runtime => _runtimeProcess,
            ProcessSlot.Editor => _editorProcess,
            _ => null
        };

        if (process is null)
        {
            return;
        }

        if (slot == ProcessSlot.Runtime)
        {
            _runtimeProcess = null;
        }
        else
        {
            _editorProcess = null;
        }

        try
        {
            if (!process.HasExited)
            {
                AppendLog($"[Host] Stopping {label} watcher PID={process.Id}...");
                process.Kill(entireProcessTree: true);
                bool exited = await WaitForExitAsync(process, TimeSpan.FromSeconds(5));
                if (!exited)
                {
                    AppendLog($"[Host] {label} watcher did not exit in time. Forcing cleanup.", HostLogSeverity.Warning);
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    await WaitForExitAsync(process, TimeSpan.FromSeconds(2));
                }

                AppendLog($"[Host] Stopped {label} watcher PID={process.Id}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Host] Failed to stop {label}: {ex.Message}", HostLogSeverity.Error);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return true;
        }

        Task completed = await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(timeout));
        return completed.IsCompletedSuccessfully && process.HasExited;
    }

    private void PublishStatus() =>
        _console.SetStatus(
            DescribeLink(IsRunning(_runtimeProcess), _hub?.RuntimeConnected ?? false),
            DescribeLink(IsRunning(_editorProcess), _hub?.EditorConnected ?? false));

    private static HostLinkState DescribeLink(bool watcherAlive, bool connected) => (watcherAlive, connected) switch
    {
        (true, true) => HostLinkState.Running,
        (true, false) => HostLinkState.BuildingOrRestarting,
        (false, true) => HostLinkState.ConnectedWatcherStopped,
        (false, false) => HostLinkState.Stopped
    };

    private void AppendLog(string message, HostLogSeverity severity = HostLogSeverity.Normal) =>
        _console.Write(severity, message);

    /// <summary>エディタが名乗った行を拾ってからの画面の知らせ</summary>
    private void AnnounceUiIfListening(string line)
    {
        if (Volatile.Read(ref _uiAnnounced) != 0) return;
        if (ParseEditorListening(line) is not { } uri) return;
        if (Interlocked.Exchange(ref _uiAnnounced, 1) != 0) return;

        string url = uri.GetLeftPart(UriPartial.Authority) + "/";
        AppendLog($"[Host] Editor UI is ready: {url}");

        if (!_noBrowser)
        {
            OpenBrowser(url);
        }
    }

    /// <summary>エディタの 1 行からの UI アドレスの読み取り</summary>
    /// <remarks>ログに ANSI 制御列が含まれていても UI アドレスを読み取る。</remarks>
    internal static Uri? ParseEditorListening(string line)
    {
        Match match = EditorListening.Match(HostLog.StripAnsi(line).Trim());
        return match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out Uri? uri) ? uri : null;
    }

    private async Task WarnIfUiNeverAnnouncedAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Volatile.Read(ref _uiAnnounced) == 0)
        {
            AppendLog("[Host] The editor never reported a UI address. See the Editor log above.",
                HostLogSeverity.Error);
        }
    }

    private void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            AppendLog($"[Host] Failed to open a browser ({ex.Message}). Open {url} manually.", HostLogSeverity.Warning);
        }
    }

    private static string Quote(string value)
    {
        var builder = new StringBuilder();
        builder.Append('"');
        builder.Append(value.Replace("\"", "\\\"", StringComparison.Ordinal));
        builder.Append('"');
        return builder.ToString();
    }
}
