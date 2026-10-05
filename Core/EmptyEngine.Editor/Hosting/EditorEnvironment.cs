using EmptyEngine.Core.RuntimeLink;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Distribution;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.Hosting;

/// <summary>起動時に確定するエディタのパスと設定。</summary>
internal static class EditorEnvironment
{
    /// <summary>ソースアセット（インポート元）のルートの宣言キー</summary>
    public const string AssetsRootKey = "EmptyEngine.AssetsRoot";

    /// <summary>アセットを同梱するパッケージの名前を <c>;</c> で並べたものの宣言キー</summary>
    public const string AssetPackagesKey = "EmptyEngine.AssetPackages";

    /// <summary>アセットを同梱するパッケージ 1 つ分の宣言キーの前置き</summary>
    public const string AssetPackageKeyPrefix = "EmptyEngine.AssetPackage.";

    /// <summary>配布ビルドのコマンドを走らせる場所の宣言キー</summary>
    public const string BuildWorkingDirectoryKey = "EmptyEngine.BuildWorkingDirectory";

    /// <summary>配布ビルドの名前を宣言順に <c>;</c> で並べたものの宣言キー</summary>
    public const string BuildsKey = "EmptyEngine.Builds";

    /// <summary>この UI を iframe に入れてよい相手を <c>;</c> で並べたものの宣言キー</summary>
    public const string FrameAncestorsKey = "EmptyEngine.FrameAncestors";

    /// <summary>ビルド 1 つ分の宣言キーの前置き</summary>
    public const string BuildKeyPrefix = "EmptyEngine.Build.";

    /// <summary>エディタ UI の既定ポート</summary>
    public const int DefaultUiPort = 5170;

    private const string ArtifactsDirName = ".artifacts";

    /// <summary>編集セッションの控え／状態を置く <c>.artifacts</c> ルート</summary>
    /// <remarks>実行ファイルの場所から親へ遡って最初に見つかったもの。無ければ実行ファイルの場所</remarks>
    public static string ArtifactsRoot { get; } = FindArtifactsDir(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

    /// <summary>ゲームのビルドが焼く型カタログ</summary>
    public static string TypeCatalogPath => Path.Combine(ArtifactsRoot, "TypeCatalog.json");

    /// <summary>ペイン幅・展開状態・配布先など UI 状態の控え</summary>
    public static string EditorStatePath => Path.Combine(ArtifactsRoot, "editor-state.json");

    /// <summary>建て直しを跨がせる undo 履歴の置き場</summary>
    public static string EditHistoryDirectory => Path.Combine(ArtifactsRoot, "editor-session");

    /// <summary>ソースアセットごとの取り込みスタンプの置き場</summary>
    public static string ImportStampsDirectory => Path.Combine(ArtifactsRoot, "import-stamps");

    /// <summary>エディタ UI が listen する URL</summary>
    public static string UiUrl(int port) => $"http://127.0.0.1:{port}";

    /// <summary>エディタがつなぐハブの WebSocket</summary>
    public static Uri EditorHubEndpoint { get; } = HubEndpoint(SceneWire.EditorWebSocketPath);

    /// <summary>同じプロセスで起こすランタイムがつなぐハブの WebSocket</summary>
    public static Uri RuntimeHubEndpoint { get; } = HubEndpoint(SceneWire.RuntimeWebSocketPath);

    /// <summary>宣言されたソースアセットルート（絶対パス）</summary>
    public static string? AssetsRoot =>
        Read(AssetsRootKey) is { Length: > 0 } assets ? Path.GetFullPath(assets) : null;

    /// <summary>参照先のパッケージが宣言した、同梱ソースアセットの置き場</summary>
    /// <remarks>パスの無い宣言と、存在しないディレクトリはその 1 つだけ落として残りを使う</remarks>
    public static IReadOnlyList<AssetSource> ReadPackageAssets(ILogger logger)
    {
        string? names = Read(AssetPackagesKey);
        if (string.IsNullOrEmpty(names)) return [];

        var packages = new List<AssetSource>();
        foreach (string name in names.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? path = Read($"{AssetPackageKeyPrefix}{name}.Path");
            if (path is null || !Directory.Exists(path))
            {
                logger.LogWarning(
                    "Package assets '{Name}' are declared in {PackagesKey} but their directory is missing ({Path}). Skipped.",
                    name, AssetPackagesKey, path ?? "no path");
                continue;
            }

            packages.Add(ProjectAssetLayout.Package(name, path));
        }

        return packages;
    }

    /// <summary>配布ビルドを走らせる場所</summary>
    public static string BuildWorkingDirectory =>
        Read(BuildWorkingDirectoryKey) is { Length: > 0 } directory
            ? Path.GetFullPath(directory)
            : Environment.CurrentDirectory;

    /// <summary>この UI を iframe に入れてよいと宣言された相手</summary>
    public static string? FrameAncestors => Read(FrameAncestorsKey);

    /// <summary>宣言された配布ビルドの宣言順の読み出し</summary>
    /// <remarks>書き損じ（コマンドの無いビルド等）はそのビルドだけ落として残りを使う</remarks>
    public static IReadOnlyList<BuildTarget> ReadBuilds(ILogger logger)
    {
        string? names = Read(BuildsKey);
        if (string.IsNullOrEmpty(names)) return [];

        var targets = new List<BuildTarget>();
        foreach (string name in names.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? command = Read($"{BuildKeyPrefix}{name}.Command");
            if (command is null)
            {
                logger.LogWarning(
                    "Build '{Name}' is declared in {BuildsKey} but {CommandKey} is missing. Skipped.",
                    name, BuildsKey, $"{BuildKeyPrefix}{name}.Command");
                continue;
            }

            bool deployBeforeBuild = bool.TryParse(
                Read($"{BuildKeyPrefix}{name}.DeployBeforeBuild"), out bool parsed) && parsed;
            targets.Add(new BuildTarget(name, command, deployBeforeBuild));
        }

        return targets;
    }

    private static string? Read(string key)
    {
        string? value = AppContext.GetData(key)?.ToString()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static Uri HubEndpoint(string path) =>
        new($"ws://{SceneWire.DefaultHubHost}:{SceneWire.DefaultHubWebSocketPort}{path}");

    private static string? FindArtifactsDir(string startDir)
    {
        for (DirectoryInfo? dir = new(startDir); dir is not null; dir = dir.Parent)
        {
            if (string.Equals(dir.Name, ArtifactsDirName, StringComparison.OrdinalIgnoreCase))
                return dir.FullName;

            string candidate = Path.Combine(dir.FullName, ArtifactsDirName);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
