// NuGet パッケージの検証と公開。build は生成と検証、publish は検証済み成果物の公開を行う。

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

const string NuGetSource = "https://api.nuget.org/v3/index.json";
const string FlatContainer = "https://api.nuget.org/v3-flatcontainer";

var command = "";
var only = new List<string>();
var packageDirectory = "";

for (var i = 0; i < args.Length; i++)
{
    if (i == 0 && !args[i].StartsWith('-'))
    {
        command = args[i];
    }
    else if (args[i] == "--only")
    {
        if (++i >= args.Length) { Error("--only には値が要ります。"); return 1; }
        only.AddRange(args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
    else if (args[i] is "--out" or "--in")
    {
        var flag = args[i];
        if (++i >= args.Length) { Error($"{flag} には値が要ります。"); return 1; }
        packageDirectory = Path.GetFullPath(args[i]);
    }
    else
    {
        Error($"知らない引数です: {args[i]}");
        return 1;
    }
}

if (packageDirectory.Length == 0)
{
    Error("パッケージの置き場を --out（build）または --in（publish）で指定してください。");
    return 1;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("EmptyEngine-release/1.0");

// 失敗時にも原因を確認できるよう、要約を必ず出力する。
var summary = new StringBuilder()
    .AppendLine("| パッケージ | 版 | この run での扱い |")
    .AppendLine("|---|---|---|");

try
{
    switch (command)
    {
        case "build": return Build();
        case "publish": return Publish();
        default:
            Error("最初の引数は build か publish です（build＝焼いて確かめる／publish＝タグを打って押す）。");
            return 1;
    }
}
finally
{
    var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (!string.IsNullOrEmpty(summaryPath)) File.AppendAllText(summaryPath, summary.ToString());
    else Console.WriteLine(summary.ToString());
}

int Build()
{
    var root = RepositoryRoot();
    var temp = Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } runnerTemp
        ? runnerTemp
        : Path.Combine(Path.GetTempPath(), "emptyengine-release");
    Directory.CreateDirectory(temp);

    Reset(packageDirectory);

    var packages = ReadReport(root, packageDirectory);
    Console.WriteLine($"配るパッケージ: {packages.Count} 件");

    // 存在しない名前で何も生成せず成功することを防ぐ。
    var known = packages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var unknown = only.Where(n => !known.Contains(n)).ToArray();
    if (unknown.Length > 0)
    {
        Error($"--only に書かれた次の名前は配るパッケージの一覧にありません: {string.Join(", ", unknown)}");
        return 1;
    }

    var plan = new List<Package>();
    var failed = false;

    foreach (var p in packages)
    {
        var tag = $"{p.Id}/{p.Version}";
        var published = IsPublished(p.Id, p.Version);
        var tagged = TagExists(root, tag);

        if (published && !tagged)
        {
            // 公開元のコミットが不明なため、欠けたタグを HEAD に補完しない。
            Error($"{p.Id} {p.Version} は nuget.org に在りますが、タグ {tag} がありません。どのコミットで出たものか分からないため bump 忘れの検査ができません。該当のコミットへ手でタグを打ってください。");
            Row(p.Id, p.Version, "❌ 公開済みだがタグが無い");
            failed = true;
            continue;
        }

        // タグ作成後に公開が失敗している場合も、同じ版のソース変更を検出する。
        if (tagged)
        {
            var (paths, missing) = Relativize(root, p.Watch);
            if (missing.Length > 0)
            {
                Error($"{p.Id} の監視集合に実在しないパスがあります: {string.Join(", ", missing)}");
                Row(p.Id, p.Version, "❌ 監視集合が壊れている");
                failed = true;
                continue;
            }
            if (paths.Length == 0)
            {
                // 空の pathspec はリポジトリ全体を比較するため拒否する。
                Error($"{p.Id} の監視集合が空です。一覧の出し方（build/PackageReport.proj）を疑ってください。");
                Row(p.Id, p.Version, "❌ 監視集合が空");
                failed = true;
                continue;
            }

            if (Differs(root, tag, paths))
            {
                if (published)
                {
                    Error($"{p.Id} はソースが {tag} 以降に変わっていますが、csproj の版が {p.Version} のまま据え置きです。版を上げてください。");
                    Row(p.Id, p.Version, $"❌ bump 忘れ（ソースが {tag} 以降に変化）");
                }
                else
                {
                    Error($"{p.Id} には未公開のタグ {tag} が在ります（前の run がタグを打った後で落ちた状態）。その後ソースが変わっているので、この版のまま出すとタグが別のコミットを指したままになります。版を上げてください。");
                    Row(p.Id, p.Version, $"❌ 未公開のタグ {tag} 以降にソースが変化");
                }
                failed = true;
                continue;
            }

            if (published)
            {
                Row(p.Id, p.Version, "公開済み・変化なし");
                continue;
            }
        }

        if (only.Count > 0 && !only.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
        {
            Row(p.Id, p.Version, "未公開だが --only の外");
            continue;
        }

        plan.Add(p);
        Row(p.Id, p.Version, tagged ? $"✅ 焼く（{tag} を打った run の続き）" : "✅ 焼く");
    }

    if (failed) return 1;

    Console.WriteLine($"焼くパッケージ: {plan.Count} 件");
    foreach (var p in plan) Console.WriteLine($"  {p.Id} {p.Version}");

    // iOS 用アセンブリの梱包パスが bin 配下を参照するため、artifacts 出力へ切り替えない。
    var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var p in plan)
    {
        var before = Directory.GetFiles(packageDirectory, "*.nupkg").ToHashSet(StringComparer.Ordinal);
        if (!Group($"pack {p.Id} {p.Version}", () => Stream("dotnet",
                ["pack", p.Project, "-c", "Release", "-o", packageDirectory, "--nologo"], root)))
        {
            Error($"{p.Id} の pack に失敗しました。");
            return 1;
        }

        var produced = Directory.GetFiles(packageDirectory, "*.nupkg").Where(f => !before.Contains(f)).ToArray();
        if (produced.Length != 1)
        {
            Error($"{p.Id} の pack が .nupkg を {produced.Length} 個作りました（1 個のはずです）。");
            return 1;
        }
        artifacts[p.Id] = produced[0];
    }

    if (!VerifyFromFeed(temp, packageDirectory, [.. plan.Where(p => !p.IsTemplate && !p.IsTool)]))
    {
        Error("焼いたパッケージだけでは組めませんでした。");
        return 1;
    }

    foreach (var t in plan.Where(p => p.IsTool))
    {
        if (!VerifyTool(temp, packageDirectory, t, artifacts[t.Id])) return 1;
    }

    // テンプレートの版が変わらなくても、エンジン変更で生成物が壊れうるため毎回検証する。
    foreach (var t in packages.Where(p => p.IsTemplate))
    {
        if (!VerifyTemplate(temp, packageDirectory, t, artifacts.GetValueOrDefault(t.Id))) return 1;
    }

    Console.WriteLine(plan.Count == 0
        ? "焼くものはありません（押す段は何もしません）。"
        : $"{plan.Count} 件を {packageDirectory} へ焼きました。");
    return 0;
}

// 検証済みのバイト列を公開するため、ここでは再ビルドしない。
int Publish()
{
    var root = RepositoryRoot();

    var sha = Environment.GetEnvironmentVariable("GITHUB_SHA") is { Length: > 0 } githubSha
        ? githubSha
        : Capture("git", ["rev-parse", "HEAD"], root).Trim();

    if (!Directory.Exists(packageDirectory))
    {
        Error($"パッケージの置き場がありません: {packageDirectory}");
        return 1;
    }

    // 空のビルド結果と誤った入力先を区別するため、一覧ファイルを確認する。
    if (!File.Exists(Path.Combine(packageDirectory, "packages.tsv")))
    {
        Error($"{packageDirectory} に packages.tsv がありません。build を先に回しましたか（--out と --in が同じ場所を指しているか確かめてください）。");
        return 1;
    }

    var artifacts = new List<Artifact>();
    foreach (var path in Directory.GetFiles(packageDirectory, "*.nupkg").OrderBy(f => f, StringComparer.Ordinal))
    {
        var (id, version) = ReadIdentity(path);
        artifacts.Add(new Artifact(id, version, path));
    }

    if (artifacts.Count == 0)
    {
        Console.WriteLine("出すものはありません。");
        return 0;
    }

    Console.WriteLine($"出すパッケージ: {artifacts.Count} 件");
    foreach (var a in artifacts) Console.WriteLine($"  {a.Id} {a.Version}");

    // 公開失敗後も生成元を追跡できるよう、パッケージ公開より先にタグを作る。
    if (!Tag(root, sha, artifacts)) return 1;

    var key = Environment.GetEnvironmentVariable("NUGET_API_KEY");
    if (string.IsNullOrEmpty(key))
    {
        Error("NUGET_API_KEY が空です。CI なら NuGet/login@v1 が失敗しているか、job に id-token: write が無い（トークン要求は黙って失敗する）。");
        return 1;
    }

    foreach (var a in artifacts)
    {
        if (!Group($"push {a.Id} {a.Version}", () => Stream("dotnet",
                ["nuget", "push", a.Path, "--source", NuGetSource, "--api-key", key, "--skip-duplicate"], root)))
        {
            Error($"{a.Id} の push に失敗しました。タグは既に打たれているので、直して回し直せば残りから拾い直します。");
            Row(a.Id, a.Version, "❌ push に失敗");
            return 1;
        }
        Row(a.Id, a.Version, "✅ 出した");
    }

    Console.WriteLine($"{artifacts.Count} 件を nuget.org へ出しました。");
    return 0;
}

// ファイル名では ID と版の区切りが曖昧なため、nuspec から取得する。
(string Id, string Version) ReadIdentity(string nupkg)
{
    using var archive = ZipFile.OpenRead(nupkg);
    var entry = archive.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"{nupkg} に .nuspec がありません。");

    using var stream = entry.Open();
    var metadata = XDocument.Load(stream).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")
        ?? throw new InvalidOperationException($"{nupkg} の nuspec に metadata がありません。");

    // schema の版による名前空間の違いを許容する。
    string Value(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value
        ?? throw new InvalidOperationException($"{nupkg} の nuspec から {name} を読めません。");

    return (Value("id"), Value("version"));
}

// SDK の既定値や条件を反映するため、パッケージ情報は MSBuild で評価する。
List<Package> ReadReport(string root, string directory)
{
    var tsv = Path.Combine(directory, "packages.tsv");
    if (!Group("配るパッケージの一覧を採る", () => Stream("dotnet",
            ["msbuild", Path.Combine(root, "build", "PackageReport.proj"), "-t:Report", $"-p:ReportPath={tsv}", "-v:m", "-nologo"], root)))
    {
        throw new InvalidOperationException("パッケージの一覧を採れませんでした。");
    }

    var list = new List<Package>();
    foreach (var line in File.ReadAllLines(tsv))
    {
        var fields = line.TrimEnd('\r').Split('\t');
        if (fields[0].Length == 0) continue;
        if (fields.Length < 4) throw new InvalidOperationException($"一覧の行を読めません: {line}");

        // 末尾の空列が省略されたレポートにも対応する。
        var kind = fields.Length > 4 ? fields[4] : "";
        var content = fields.Length > 5 ? fields[5] : "";

        list.Add(new Package(fields[0], fields[1], fields[2],
            fields[3].Split('|', StringSplitOptions.RemoveEmptyEntries), kind, content));
    }
    return [.. list.OrderBy(p => p.Id, StringComparer.Ordinal)];
}

// 非公開化された版も再利用できないため、flat container で確認する。
bool IsPublished(string id, string version)
{
    var url = $"{FlatContainer}/{id.ToLowerInvariant()}/index.json";
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(response.Content.ReadAsStream());
            return document.RootElement.GetProperty("versions").EnumerateArray()
                .Any(v => string.Equals(v.GetString(), version, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (attempt < 3)
        {
            Console.WriteLine($"{id} の照会に失敗しました（{attempt} 回目）: {ex.Message}");
            Thread.Sleep(2000);
        }
    }
}

(string[] Paths, string[] Missing) Relativize(string root, string[] watch)
{
    var paths = new List<string>();
    var missing = new List<string>();
    foreach (var entry in watch)
    {
        var absolute = entry.Replace('\\', '/').TrimEnd('/');
        if (absolute.Length == 0) continue;

        // 存在しないパスを「変更なし」と誤判定しないよう、比較前に検査する。
        if (!File.Exists(absolute) && !Directory.Exists(absolute)) { missing.Add(absolute); continue; }

        paths.Add(Path.GetRelativePath(root, absolute).Replace('\\', '/'));
    }
    return ([.. paths], [.. missing]);
}

bool Differs(string root, string tag, string[] paths)
{
    var (code, output) = Run("git", ["diff", "--quiet", tag, "HEAD", "--", .. paths], root);
    return code switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidOperationException($"git diff が異常終了しました (exit {code}):\n{output}"),
    };
}

bool TagExists(string root, string tag) => Run("git", ["rev-parse", "-q", "--verify", $"refs/tags/{tag}"], root).Code == 0;

bool VerifyFromFeed(string temp, string feed, List<Package> plan)
{
    if (plan.Count == 0) return true;

    // リポジトリのビルド設定が混入しないよう、外部でパッケージを検証する。
    var consumer = Path.Combine(temp, "consumer");
    Reset(consumer);
    var assets = Path.Combine(consumer, "Assets");
    Directory.CreateDirectory(assets);

    WriteNuGetConfig(consumer, feed);

    var project = new StringBuilder()
        .AppendLine("""<Project Sdk="Microsoft.NET.Sdk">""")
        .AppendLine("  <PropertyGroup>")
        .AppendLine("    <TargetFramework>net10.0</TargetFramework>")
        .AppendLine("    <Nullable>enable</Nullable>")
        // エディタ宣言の必須条件を満たすため、空のアセットディレクトリを指定する。
        .AppendLine($"    <AssetsPath>{assets}</AssetsPath>")
        .AppendLine($"    <BuildWorkingPath>{consumer}</BuildWorkingPath>")
        .AppendLine("  </PropertyGroup>")
        .AppendLine("  <ItemGroup>");

    // 今回の成果物を検証するため、版の範囲を許可しない。
    foreach (var p in plan) project.AppendLine($"""    <PackageReference Include="{p.Id}" Version="[{p.Version}]" />""");

    project.AppendLine("  </ItemGroup>").AppendLine("</Project>");

    var path = Path.Combine(consumer, "Consumer.csproj");
    File.WriteAllText(path, project.ToString());
    Console.WriteLine(project.ToString());

    return Group("ローカルフィードから組めることを確かめる",
        () => Stream("dotnet", ["build", path, "-c", "Release", "--packages", Path.Combine(temp, "pkgs"), "--nologo"], consumer));
}

// パッケージ参照ではツールを検証できないため、インストールして実行する。
bool VerifyTool(string temp, string feed, Package package, string nupkg)
{
    string command;
    using (var archive = ZipFile.OpenRead(nupkg))
    {
        var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("DotnetToolSettings.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Error($"{package.Id} は tool として焼かれていません（.nupkg に DotnetToolSettings.xml がありません）。PackAsTool を疑ってください。");
            return false;
        }

        using var stream = entry.Open();
        var name = XDocument.Load(stream).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Command")?.Attribute("Name")?.Value;

        if (string.IsNullOrEmpty(name))
        {
            Error($"{package.Id} の DotnetToolSettings.xml から叩く名前を読めませんでした。");
            return false;
        }
        command = name;
    }

    var probe = Path.Combine(temp, "tool-probe");
    Reset(probe);
    WriteNuGetConfig(probe, feed);

    // 既存ツールとの衝突を避け、今回の成果物を検証するため、保存先と版を固定する。
    var tools = Path.Combine(probe, "tools");

    if (!Group($"{package.Id} を dotnet tool として入れる", () => Stream("dotnet",
            ["tool", "install", package.Id, "--version", package.Version, "--tool-path", tools], probe)))
    {
        Error($"{package.Id} を dotnet tool として入れられませんでした。");
        return false;
    }

    var executable = Path.Combine(tools, OperatingSystem.IsWindows() ? $"{command}.exe" : command);
    if (!File.Exists(executable))
    {
        Error($"{package.Id} は入ったのに {command} が現れませんでした: {executable}");
        return false;
    }

    // 起動時のフレームワーク解決も検証するため、コマンドを実行する。
    if (!Group($"{command} --version を叩く", () => Stream(executable, ["--version"], probe)))
    {
        Error($"入れた {command} が動きませんでした。");
        return false;
    }

    return true;
}

// パッケージ参照ではテンプレートを検証できないため、生成したプロジェクトをビルドする。
bool VerifyTemplate(string temp, string feed, Package package, string? nupkg)
{
    if (package.TemplateContent.Length == 0)
    {
        Error($"{package.Id} は PackageType=Template ですが、中身の場所（EmptyEngineTemplateContent）が宣言されていません。");
        return false;
    }

    var source = nupkg ?? package.TemplateContent;

    var declaration = Path.Combine(package.TemplateContent, ".template.config", "template.json");
    if (!File.Exists(declaration))
    {
        Error($"テンプレートの宣言が見つかりません: {declaration}");
        return false;
    }

    string? shortName;
    using (var document = JsonDocument.Parse(File.ReadAllText(declaration),
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
    {
        var value = document.RootElement.GetProperty("shortName");
        shortName = value.ValueKind == JsonValueKind.Array ? value[0].GetString() : value.GetString();
    }

    if (string.IsNullOrEmpty(shortName))
    {
        Error($"{declaration} から shortName を読めませんでした。");
        return false;
    }

    var probe = Path.Combine(temp, "template-probe");
    Reset(probe);
    WriteNuGetConfig(probe, feed);

    // 公開版との衝突で検証対象が入れ替わらないよう、テンプレートの保存先を隔離する。
    var cliHome = Path.Combine(probe, "cli-home");
    Directory.CreateDirectory(cliHome);
    var isolated = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DOTNET_CLI_HOME"] = cliHome,
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
    };

    if (!Group($"{package.Id} を dotnet new へ入れる", () => Stream("dotnet", ["new", "install", source], probe, isolated)))
    {
        Error($"{package.Id} をテンプレートとして入れられませんでした。");
        return false;
    }

    var created = Path.Combine(probe, "TemplateProbe");

    if (!Group($"{shortName} からプロジェクトを作る",
            () => Stream("dotnet", ["new", shortName, "-n", "TemplateProbe", "-o", created], probe, isolated)))
    {
        Error($"{package.Id} からプロジェクトを作れませんでした。");
        return false;
    }

    if (!Group("作ったプロジェクトを建てる", () => Stream("dotnet",
            ["build", created, "-c", "Release", "--packages", Path.Combine(temp, "pkgs"), "--nologo"], probe)))
    {
        Error($"{package.Id} から作ったプロジェクトが建ちませんでした。");
        return false;
    }

    return true;
}

// 今回生成しない依存パッケージも取得するため、nuget.org を残す。
void WriteNuGetConfig(string directory, string feed) => File.WriteAllText(Path.Combine(directory, "nuget.config"), $"""
    <?xml version="1.0" encoding="utf-8"?>
    <configuration>
      <packageSources>
        <clear />
        <add key="local" value="{feed}" />
        <add key="nuget.org" value="{NuGetSource}" />
      </packageSources>
    </configuration>
    """);

bool Tag(string root, string sha, List<Artifact> artifacts)
{
    // 手元で回したときに利用者の git 設定を書き換えない。CI では未設定なのでここで名乗る。
    if (Run("git", ["config", "user.name"], root).Code != 0)
    {
        Capture("git", ["config", "user.name", "github-actions[bot]"], root);
        Capture("git", ["config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com"], root);
    }

    var tags = new List<string>();
    foreach (var p in artifacts)
    {
        var tag = $"{p.Id}/{p.Version}";
        if (TagExists(root, tag))
        {
            Console.WriteLine($"{tag} は既に在ります（位置は動かしません）。");
            continue;
        }
        Capture("git", ["tag", "-a", tag, "-m", tag, sha], root);
        tags.Add(tag);
    }

    if (tags.Count == 0)
    {
        Console.WriteLine("新しく打つタグはありません。");
        return true;
    }

    // 一部のタグだけが公開されないよう、まとめて push する。
    if (Group($"タグを打つ（{tags.Count} 件）", () => Stream("git", ["push", "--atomic", "origin", .. tags], root))) return true;

    Error("タグの push に失敗しました。押しません。");
    return false;
}

bool Group(string title, Func<int> body)
{
    Console.WriteLine($"::group::{title}");
    Console.Out.Flush();
    var code = body();
    Console.WriteLine("::endgroup::");
    return code == 0;
}

void Reset(string directory)
{
    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    Directory.CreateDirectory(directory);
}

void Error(string message) => Console.WriteLine($"::error::{message}");

void Row(string id, string version, string state) => summary.AppendLine($"| {id} | {version} | {state} |");

// API キーが引数に含まれるため、コマンド行を出力しない。
int Stream(string exe, string[] arguments, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null)
{
    using var process = Process.Start(Info(exe, arguments, workingDirectory, environment))
        ?? throw new InvalidOperationException($"{exe} を起動できませんでした。");
    process.WaitForExit();
    return process.ExitCode;
}

(int Code, string Output) Run(string exe, string[] arguments, string? workingDirectory = null)
{
    var info = Info(exe, arguments, workingDirectory, null);
    info.RedirectStandardOutput = true;
    info.RedirectStandardError = true;
    using var process = Process.Start(info) ?? throw new InvalidOperationException($"{exe} を起動できませんでした。");
    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output);
}

string Capture(string exe, string[] arguments, string? workingDirectory = null)
{
    var (code, output) = Run(exe, arguments, workingDirectory);
    if (code != 0) throw new InvalidOperationException($"{exe} {string.Join(' ', arguments)} が失敗しました (exit {code}):\n{output}");
    return output;
}

ProcessStartInfo Info(string exe, string[] arguments, string? workingDirectory, IReadOnlyDictionary<string, string>? environment)
{
    var info = new ProcessStartInfo(exe) { UseShellExecute = false };
    foreach (var a in arguments) info.ArgumentList.Add(a);
    if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
    if (environment is not null) foreach (var (name, value) in environment) info.Environment[name] = value;
    return info;
}

// このスクリプトの親ディレクトリ。
static string RepositoryRoot([CallerFilePath] string script = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(script)!, ".."));

/// <param name="Path">焼かれた .nupkg の場所。</param>
record Artifact(string Id, string Version, string Path);

/// <param name="Watch">パッケージの内容に影響するソースの絶対パス。</param>
/// <param name="Kind">検証するパッケージ種別。<c>Template</c>、<c>Tool</c>、またはライブラリを表す空文字。</param>
/// <param name="TemplateContent">テンプレートの内容を持つフォルダの絶対パス。テンプレート以外では空文字。</param>
record Package(string Id, string Version, string Project, string[] Watch, string Kind, string TemplateContent)
{
    public bool IsTemplate => string.Equals(Kind, "Template", StringComparison.OrdinalIgnoreCase);

    public bool IsTool => string.Equals(Kind, "Tool", StringComparison.OrdinalIgnoreCase);
}
