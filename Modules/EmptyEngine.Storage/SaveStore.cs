using System.Runtime.InteropServices.JavaScript;

namespace EmptyEngine.Storage;

/// <summary>文字列のセーブデータを永続化する。</summary>
/// <remarks>desktop は実行ファイル隣の <c>&lt;slot&gt;.save</c>、iOS は <c>Library/Application Support</c>、web は <c>localStorage</c> に保存する。</remarks>
public static partial class SaveStore
{
    private const string WebKeyPrefix = "emptyengine.save.";

    private static string? _directory;

    private static void Log(Action<string>? log, string message)
    {
        if (log is not null)
            log($"[SaveStore] {message}");
        else
            Console.Error.WriteLine($"[SaveStore] {message}");
    }

    /// <summary>指定スロットの読み出し</summary>
    public static string? Read(string slot, Action<string>? log = null)
    {
        try
        {
            if (OperatingSystem.IsBrowser())
                return LocalStorageGetItem(WebKeyPrefix + slot);

            string path = ResolvePath(slot);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e)
        {
            Log(log, $"read '{slot}': {e.Message}");
            return null;
        }
    }

    /// <summary>指定スロットの書き込み</summary>
    public static void Write(string slot, string text, Action<string>? log = null)
    {
        try
        {
            if (OperatingSystem.IsBrowser())
            {
                LocalStorageSetItem(WebKeyPrefix + slot, text);
                return;
            }

            string path = ResolvePath(slot);
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            Log(log, $"write '{slot}': {e.Message}");
        }
    }

    private static string ResolvePath(string slot) =>
        Path.Combine(_directory ??= ResolveDirectory(), slot + ".save");

    /// <summary>スロットのファイルを置くディレクトリの解決</summary>
    /// <remarks>デスクトップでは実行ファイルの隣、iOS ではアプリの <c>Library/Application Support</c> を返す。</remarks>
    private static string ResolveDirectory()
    {
        if (!OperatingSystem.IsIOS())
            return AppContext.BaseDirectory;

        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (root.Length == 0)
            root = Path.Combine(
                Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory,
                "Library",
                "Application Support");

        // 初回起動時には Application Support が存在しない。
        Directory.CreateDirectory(root);
        return root;
    }

    // 他のプラットフォームでは呼び出せないため、IsBrowser の分岐内で使用する。
    [JSImport("globalThis.localStorage.getItem")]
    private static partial string? LocalStorageGetItem(string key);

    [JSImport("globalThis.localStorage.setItem")]
    private static partial void LocalStorageSetItem(string key, string value);
}
