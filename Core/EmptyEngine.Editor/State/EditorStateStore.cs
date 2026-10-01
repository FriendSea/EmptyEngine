using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmptyEngine.Editor.State;

/// <summary><see cref="EditorState"/> の JSON ファイルとしての読み書き</summary>
public sealed class EditorStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger _logger;

    /// <param name="path">控えの JSON ファイル</param>
    public EditorStateStore(string path, ILogger<EditorStateStore>? logger = null)
    {
        _path = Path.GetFullPath(path);
        _logger = logger ?? NullLogger<EditorStateStore>.Instance;
        State = Load();
    }

    public EditorState State { get; }

    /// <summary>ペイン名ごとの幅（px）</summary>
    public IReadOnlyDictionary<string, double> PaneWidths => State.PaneWidths;

    /// <summary>ペイン幅の控え</summary>
    /// <param name="widths">ペイン名ごとの幅。渡した分だけを差し替える</param>
    public void SetPaneWidths(IReadOnlyDictionary<string, double> widths)
    {
        foreach ((string name, double width) in widths) State.PaneWidths[name] = width;
        Save();
    }

    /// <summary>そのペインを畳んでいるか（記録が無ければ <c>null</c>）</summary>
    public bool? PaneCollapsed(string name) =>
        State.CollapsedPanes.TryGetValue(name, out bool collapsed) ? collapsed : null;

    /// <summary>1 ペインの畳み状態の控え</summary>
    public void SetPaneCollapsed(string name, bool collapsed)
    {
        State.CollapsedPanes[name] = collapsed;
        Save();
    }

    /// <summary>そのシーンで展開されている ObjectId</summary>
    public IReadOnlySet<string> ExpandedObjects(string sceneKey) =>
        State.ExpandedHierarchyObjects.TryGetValue(sceneKey, out List<string>? ids)
            ? new HashSet<string>(ids, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>1 ノードの展開状態の控え</summary>
    /// <remarks>畳んだ結果そのシーンの展開が空になったら、シーンごと落とす（畳んだだけの記録は残さない）</remarks>
    public void SetExpanded(string sceneKey, string objectId, bool expanded)
    {
        Dictionary<string, List<string>> byScene = State.ExpandedHierarchyObjects;
        if (!byScene.TryGetValue(sceneKey, out List<string>? objectIds))
        {
            if (!expanded) return;
            objectIds = new List<string>();
            byScene[sceneKey] = objectIds;
        }

        if (expanded)
        {
            if (!objectIds.Contains(objectId, StringComparer.Ordinal)) objectIds.Add(objectId);
        }
        else
        {
            objectIds.RemoveAll(id => string.Equals(id, objectId, StringComparison.Ordinal));
            if (objectIds.Count == 0) byScene.Remove(sceneKey);
        }

        Save();
    }

    private EditorState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                EditorState? loaded = JsonSerializer.Deserialize<EditorState>(json, Options);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Editor state load failed (using defaults): {Error}", ex.Message);
        }

        return new EditorState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(State, Options));
        }
        catch (Exception ex)
        {
            _logger.LogError("Editor state save failed: {Error}", ex.Message);
        }
    }
}
