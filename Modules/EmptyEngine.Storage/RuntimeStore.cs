using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using EmptyEngine.Core.RuntimeLink;

namespace EmptyEngine.Storage;

/// <summary>実行環境に応じたアーティファクトの読み込み元を提供する</summary>
/// <remarks>
/// エディタ接続時は <c>EmptyEngineAssetServerUrl</c> が宣言されていればその場所を、無ければ編集セッションの共有の置き場を読む。
/// 単体起動では exe 隣のアーカイブを、開発ビルドでアーカイブが無ければ共有の置き場を読む。
/// </remarks>
public static class RuntimeStore
{
    private const string AssetServerUrlKey = "EmptyEngine.AssetServerUrl";

    /// <summary>共有の置き場のページからの相対位置</summary>
    private const string PageRelativeAssetsPath = "assets/";

    /// <summary>開発用アセットを読み込めるか（配布ビルドでは <c>false</c>）</summary>
    [FeatureSwitchDefinition("EmptyEngine.DevelopmentAssets")]
    public static bool DevelopmentAssetsEnabled =>
        AppContext.TryGetSwitch("EmptyEngine.DevelopmentAssets", out bool enabled) && enabled;

    /// <summary>このプラットフォームのランタイムストア</summary>
    public static AssetStorage Open()
    {
        if (!RuntimeMode.IsEditorAttached)
        {
            return AssetStorage.FromArchive(
                ImportedAssetsLayout.ResolveDistributionArchive(AppContext.BaseDirectory),
                DevelopmentAssetsEnabled ? ImportedAssetsLayout.ResolveEditorRoot() : null);
        }

        string? server = AppContext.GetData(AssetServerUrlKey) as string;
        if (string.IsNullOrWhiteSpace(server)) server = null;

        if (OperatingSystem.IsBrowser())
            return AssetStorage.FromHttp(server ?? ResolvePageRelativeAssetsUrl(), Path.Combine(CacheParent, "Browser"));

        return server is not null
            ? AssetStorage.FromHttp(server, ProcessCacheDirectory.Value)
            : AssetStorage.FromDirectory(ImportedAssetsLayout.ResolveEditorRoot());
    }

    private static string CacheParent => Path.Combine(Path.GetTempPath(), "EmptyEngine", "AssetCache");

    /// <summary>このプロセスだけが使う読み出し済みの内容の置き場</summary>
    private static readonly Lazy<string> ProcessCacheDirectory = new(() =>
    {
        string own = Path.Combine(CacheParent, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (Directory.Exists(CacheParent))
        {
            foreach (string directory in Directory.EnumerateDirectories(CacheParent))
            {
                if (int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
                    && (directory == own || !IsRunning(processId)))
                {
                    TryDeleteDirectory(directory);
                }
            }
        }

        return own;
    });

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [SupportedOSPlatform("browser")]
    private static string ResolvePageRelativeAssetsUrl()
    {
        using JSObject document = JSHost.GlobalThis.GetPropertyAsJSObject("document")
            ?? throw new InvalidOperationException("The page document is not available.");
        string baseUri = document.GetPropertyAsString("baseURI")
            ?? throw new InvalidOperationException("The page base URI is not available.");
        return new Uri(new Uri(baseUri), PageRelativeAssetsPath).AbsoluteUri;
    }
}
