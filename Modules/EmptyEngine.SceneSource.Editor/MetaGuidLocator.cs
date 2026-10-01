using System.Text.Json;

namespace EmptyEngine.SceneSource.Editor;

/// <summary>guid または相対パスでのソースファイル実パスの解決</summary>
internal static class MetaGuidLocator
{
    private const string MetaExtension = ".meta";

    /// <summary>参照 <paramref name="reference"/> の実パスへの解決</summary>
    public static bool TryResolve(string? preferredRoot, string referencingFilePath, string reference, out string? resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        foreach (string root in CandidateRoots(preferredRoot, referencingFilePath))
        {
            if (TryFindByGuid(root, reference, out resolvedPath))
                return true;

            string asPath = Path.GetFullPath(Path.Combine(root, reference));
            if (File.Exists(asPath))
            {
                resolvedPath = asPath;
                return true;
            }
        }

        return false;
    }

    /// <summary>ソースファイル隣の <c>.meta</c> からの guid の読み取り</summary>
    public static string? TryReadGuidOf(string sourceFilePath) => TryReadGuid(sourceFilePath + MetaExtension);

    private static IEnumerable<string> CandidateRoots(string? preferredRoot, string referencingFilePath)
    {
        if (!string.IsNullOrEmpty(preferredRoot))
        {
            yield return preferredRoot;
            yield break;
        }

        for (DirectoryInfo? dir = Directory.GetParent(Path.GetFullPath(referencingFilePath)); dir is not null; dir = dir.Parent)
            yield return dir.FullName;
    }

    private static bool TryFindByGuid(string root, string guid, out string? resolvedPath)
    {
        resolvedPath = null;
        if (!Directory.Exists(root))
            return false;

        foreach (string metaPath in Directory.EnumerateFiles(root, "*" + MetaExtension, SearchOption.AllDirectories))
        {
            if (!string.Equals(TryReadGuid(metaPath), guid, StringComparison.OrdinalIgnoreCase))
                continue;

            string sibling = metaPath[..^MetaExtension.Length];
            if (File.Exists(sibling))
            {
                resolvedPath = sibling;
                return true;
            }
        }

        return false;
    }

    private static string? TryReadGuid(string metaPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            return doc.RootElement.TryGetProperty("guid", out JsonElement g) ? g.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
