using System.Text.Json;
using EmptyEngine.Editor.Assets;

namespace EmptyEngine.Modules.Testing;

/// <summary>インポータ単体テスト用の、ソースを引けるカタログ</summary>
internal static class TestSources
{
    /// <summary>ディレクトリ以下の <c>.meta</c> を読み、guid からソースを引けるカタログを作る</summary>
    public static AssetCatalog In(string directory)
    {
        var sourceByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string meta in Directory.EnumerateFiles(directory, "*.meta", SearchOption.AllDirectories))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(meta));
            if (document.RootElement.GetProperty("guid").GetString() is { } guid)
                sourceByGuid[guid] = meta[..^".meta".Length];
        }

        var catalog = new AssetCatalog();
        catalog.SetSources(sourceByGuid);
        return catalog;
    }
}
