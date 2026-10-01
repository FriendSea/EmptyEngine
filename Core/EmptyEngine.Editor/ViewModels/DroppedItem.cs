namespace EmptyEngine.Editor.ViewModels;

/// <summary>エディタへ落とされたファイル 1 件の指し先</summary>
internal readonly record struct DroppedItem(string? FullPath, string FileName)
{
    /// <summary>ブラウザから来た 1 行の指し先としての解釈</summary>
    public static DroppedItem? Parse(string? raw)
    {
        string text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;

        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
        {
            return uri.IsFile ? FromPath(uri.LocalPath) : null;
        }

        if (Path.IsPathRooted(text)) return FromPath(text);

        string name = NameOf(text);
        return name.Length > 0 ? new DroppedItem(null, name) : null;
    }

    private static DroppedItem? FromPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(NormalizeDriveRoot(path));
            string name = Path.GetFileName(full);
            return name.Length > 0 ? new DroppedItem(full, name) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string NormalizeDriveRoot(string path) =>
        OperatingSystem.IsWindows()
        && path.Length >= 3
        && path[0] is '/' or '\\'
        && char.IsAsciiLetter(path[1])
        && path[2] == ':'
            ? path[1..]
            : path;

    private static string NameOf(string text)
    {
        try
        {
            return Path.GetFileName(text);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
