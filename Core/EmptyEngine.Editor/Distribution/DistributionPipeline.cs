using System.Diagnostics;
using EmptyEngine.Core;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Hosting;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.Distribution;

/// <summary>配布ビルド 1 つ分の宣言</summary>
/// <param name="Name">選ぶときの名前（<c>Windows</c> / <c>Web</c> など）。</param>
/// <param name="Command">実行するコマンド。<c>{output}</c> と <c>{assets}</c> がエンジンによって置き換わる。</param>
/// <param name="DeployBeforeBuild"><c>true</c> なら署名・パッケージ化より前に一時領域へアセットを作る。</param>
public sealed record BuildTarget(string Name, string Command, bool DeployBeforeBuild = false);

/// <summary>宣言されたビルドコマンドの実行、アセットの配置、配布物の仕上げを管理する</summary>
public sealed class DistributionPipeline
{
    /// <summary>コマンドの中で配布先に置き換わる印</summary>
    public const string OutputPlaceholder = "{output}";

    /// <summary>事前配布ターゲットで、生成済みアセットの一時ディレクトリに置き換わる印</summary>
    public const string AssetsPlaceholder = "{assets}";

    private readonly string _assetsRoot;
    private readonly EditorArtifacts _source;
    private readonly Func<string, EditorArtifacts> _createTarget;
    private readonly IDistributionBuilder _builder;
    private readonly ILogger _logger;

    private readonly string _workingDirectory;

    /// <param name="assetsRoot">ソースアセットのルート（デプロイ対象シーンの解決に使う）。</param>
    public DistributionPipeline(
        string assetsRoot,
        EditorArtifacts source,
        Func<string, EditorArtifacts> createTarget,
        IDistributionBuilder builder,
        ILogger<DistributionPipeline> logger)
    {
        _assetsRoot = assetsRoot;
        _source = source;
        _createTarget = createTarget;
        _builder = builder;
        _logger = logger;

        _workingDirectory = EditorEnvironment.BuildWorkingDirectory;
        Targets = EditorEnvironment.ReadBuilds(logger);
    }

    /// <summary>宣言されたビルド（宣言順）</summary>
    public IReadOnlyList<BuildTarget> Targets { get; }

    /// <summary>ビルドコマンドを走らせる場所（コマンド中の相対パスはここ基準）</summary>
    public string WorkingDirectory => _workingDirectory;

    /// <summary>配布ビルドできるか（宣言が 1 つでもあるか）</summary>
    public bool CanBuild => Targets.Count > 0;

    /// <summary>名前での引き当て（大文字小文字は無視）</summary>
    public BuildTarget? FindTarget(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Targets.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>到達アーティファクトの配布 exe ディレクトリへの配布と仕上げ</summary>
    public void Deploy(string exeDirectory)
    {
        List<AssetKey> scenes = [.. BuildSceneList.Read(_assetsRoot).Select(k => new AssetKey(k))];
        EditorArtifacts target = _createTarget(exeDirectory);
        IReadOnlyList<string> deployed = DeployReachable(_source, target, scenes);
        _builder.Build(exeDirectory, scenes);
        _logger.LogInformation("Deployed {Count} reachable artifact(s) to {Directory}", deployed.Count, exeDirectory);
    }

    /// <summary><paramref name="deployScenes"/> から到達した scene/asset の書き出し</summary>
    /// <returns>配ったキー一覧（起動シーン自身を含む）</returns>
    internal static IReadOnlyList<string> DeployReachable(
        EditorArtifacts source,
        EditorArtifacts destination,
        IReadOnlyList<AssetKey> deployScenes)
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (AssetKey scene in deployScenes) Enqueue(scene.Value, reachable, queue);

        while (queue.Count > 0)
        {
            string key = queue.Dequeue();
            IReadOnlyList<HierarchyNode>? roots = source.LoadScene(new AssetKey(key));
            if (roots is not null)
            {
                foreach (AssetKey reference in EnumerateSceneReferences(roots))
                    Enqueue(reference.Value, reachable, queue);
            }
            else if (source.LoadAsset(new AssetKey(key)) is { } asset)
            {
                foreach (AssetKey reference in asset.EnumerateAssetReferences())
                    Enqueue(reference.Value, reachable, queue);
            }
        }

        var copied = new List<string>();
        foreach (string key in reachable.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<HierarchyNode>? roots = source.LoadScene(new AssetKey(key));
            if (roots is not null)
            {
                destination.SaveSceneAsync(new AssetKey(key), roots).GetAwaiter().GetResult();
                copied.Add(key);
            }
            else if (source.LoadAsset(new AssetKey(key)) is { } asset)
            {
                destination.SaveAssetAsync(new AssetKey(key), asset).GetAwaiter().GetResult();
                copied.Add(key);
            }
        }

        return copied;
    }

    /// <summary>宣言されたビルドコマンドを実行し、到達可能なアセットを配布先へ出力する。</summary>
    /// <param name="target">実行するビルド（<see cref="Targets"/> のいずれか）。</param>
    /// <param name="destination">配布先。<see cref="WorkingDirectory"/> の下に限る。</param>
    public async Task BuildAsync(
        BuildTarget target, string destination, CancellationToken cancellationToken = default)
    {
        string output = ResolveDestination(destination, _workingDirectory);

        string? assets = null;
        try
        {
            if (target.DeployBeforeBuild)
            {
                assets = Path.Combine(Path.GetTempPath(), $"emptyengine-build-{Guid.NewGuid():N}");
                Directory.CreateDirectory(assets);
                Deploy(assets);
            }

            string command = target.Command
                .Replace(OutputPlaceholder, Quote(output), StringComparison.Ordinal)
                .Replace(AssetsPlaceholder, assets is null ? string.Empty : Quote(assets), StringComparison.Ordinal);

            _logger.LogInformation("Build '{Name}': {Command}", target.Name, command);
            await RunAsync(command, _workingDirectory, cancellationToken);
            if (!target.DeployBeforeBuild) Deploy(output);
        }
        finally
        {
            if (assets is not null && Directory.Exists(assets)) Directory.Delete(assets, recursive: true);
        }
        _logger.LogInformation("Build complete: {Output}", output);
    }

    private async Task RunAsync(string command, string workingDirectory, CancellationToken cancellationToken)
    {
        (string fileName, string arguments) = Split(command);
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        string standardOutput = await stdout;
        string standardError = await stderr;
        foreach (string text in (string[])[standardOutput, standardError])
        {
            string[] lines = [.. Lines(text)];
            if (lines.Length > 0) _logger.LogInformation("{Output}", string.Join(Environment.NewLine, lines));
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"`{command}` failed (exit {process.ExitCode}).");
    }

    private static IEnumerable<AssetKey> EnumerateSceneReferences(IReadOnlyList<HierarchyNode> roots)
    {
        foreach (HierarchyNode root in roots)
            foreach (HierarchyNode node in Tree.Flatten(root, n => n.Children))
                foreach (AuthoringObject component in node.Components)
                    foreach (AssetKey reference in component.EnumerateAssetReferences())
                        yield return reference;
    }

    private static void Enqueue(string assetKey, HashSet<string> reachable, Queue<string> queue)
    {
        string normalized = Normalize(assetKey);
        if (normalized.Length == 0) return;
        if (reachable.Add(normalized)) queue.Enqueue(normalized);
    }

    private static string Normalize(string assetKey) =>
        string.IsNullOrWhiteSpace(assetKey) ? string.Empty : assetKey.Replace('\\', '/').TrimStart('/');

    /// <summary>配布先として受け取れるディレクトリの確定（絶対パス）</summary>
    /// <param name="destination">指定された配布先。相対パスは <paramref name="workingDirectory"/> を基準とする。</param>
    /// <remarks>宛先はビルドコマンドの一部として実行されるので、受け取る形を作業ディレクトリの下だけに絞る。</remarks>
    /// <exception cref="ArgumentException">空、引用符を含む、作業ディレクトリの下ではない。</exception>
    internal static string ResolveDestination(string? destination, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("A distribution build needs a destination directory.", nameof(destination));

        // 引用符はコマンド行の区切りを壊す＝宛先の形で引数を足せてしまう。
        if (destination.Contains('"'))
            throw new ArgumentException(
                $"The distribution destination '{destination}' contains a quote.", nameof(destination));

        string root = Path.GetFullPath(workingDirectory);
        string output = Path.GetFullPath(destination.Trim(), root);

        // publish によるソースの除外・削除を防ぐため、また外のファイルを書かせないため、下だけを配布先にする。
        if (!IsUnder(root, output))
            throw new ArgumentException(
                $"The distribution destination '{output}' is not inside the project directory '{root}'. " +
                "Choose a directory below it (e.g. a 'dist' subdirectory).",
                nameof(destination));

        return output;
    }

    /// <summary><paramref name="path"/> が <paramref name="root"/> より下か（<paramref name="root"/> 自身は外）</summary>
    /// <remarks>パスの大文字・小文字と区切り文字は OS の規則に従って比較する。</remarks>
    private static bool IsUnder(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == "." || Path.IsPathRooted(relative)) return false;
        return relative.Split(Path.DirectorySeparatorChar)[0] != "..";
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (string FileName, string Arguments) Split(string command)
    {
        string trimmed = command.Trim();
        int space = trimmed.IndexOf(' ');
        return space < 0 ? (trimmed, string.Empty) : (trimmed[..space], trimmed[(space + 1)..].TrimStart());
    }

    private static string Quote(string path) =>
        path.Contains(' ') && !path.StartsWith('"') ? $"\"{path}\"" : path;
}
