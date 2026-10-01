using System.Text.Json;

namespace EmptyEngine.Editor.Distribution;

/// <summary>デプロイ対象シーンの順序付きリストの読み書き</summary>
public static class BuildSceneList
{
    /// <summary>ソース設定ファイル名</summary>
    public const string FileName = "BuildScenes.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private sealed class Document
    {
        public List<string> Scenes { get; set; } = new();
    }

    /// <summary>アセットルートからのソース設定ファイル絶対パスの導出</summary>
    public static string ResolvePath(string assetsRoot)
    {
        string root = Path.GetFullPath(assetsRoot);
        string parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? root;
        return Path.Combine(parent, FileName);
    }

    /// <summary>デプロイ対象シーンキーの順序どおりの読み出し</summary>
    public static IReadOnlyList<string> Read(string assetsRoot)
    {
        string path = ResolvePath(assetsRoot);
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            Document? doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options);
            return doc?.Scenes
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToArray()
                ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>デプロイ対象シーンキーの順序どおりの書き込み</summary>
    public static void Write(string assetsRoot, IReadOnlyList<string> sceneKeys)
    {
        string path = ResolvePath(assetsRoot);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var doc = new Document { Scenes = sceneKeys.ToList() };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, Options));
        }
        catch
        {
        }
    }
}
