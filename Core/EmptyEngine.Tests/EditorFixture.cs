using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Catalog;
using EmptyEngine.Editor.State;
using EmptyEngine.Editor.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmptyEngine.Tests;

/// <summary>テストが使う <see cref="EditorViewModel"/> の組み立て</summary>
/// <remarks>依存はすべて必須なので、そのテストが見ないものは当たり障りのない実体で埋める。</remarks>
internal static class EditorFixture
{
    private static readonly string ScratchRoot = Path.Combine(
        Path.GetTempPath(), "ee-editor-fixture-" + Guid.NewGuid().ToString("N"));

    public static EditorViewModel NewEditor(
        AssetCatalog? assets = null,
        AssetImportService? imports = null,
        IHierarchyBlobSerializer? serializer = null,
        EditorStateStore? state = null,
        EditHistoryViewModel? history = null,
        TypeCatalog? catalog = null,
        ILogger<EditorViewModel>? logger = null,
        ProjectAssetLayout? layout = null)
    {
        if ((assets is null) != (imports is null) || (imports is null) != (layout is null))
            throw new ArgumentException(
                "Pass the asset catalog, the import service that writes into it and the layout it imports from together.");

        assets ??= new AssetCatalog();
        layout ??= new ProjectAssetLayout(Scratch());
        return new EditorViewModel(
            assets,
            imports ?? new AssetImportService(assets, layout.Sources, [], Path.Combine(Scratch(), "import-stamps")),
            layout,
            serializer ?? new CloneBlobSerializer(),
            state ?? new EditorStateStore(Path.Combine(Scratch(), "editor-state.json")),
            history ?? new EditHistoryViewModel(),
            // 実在しないファイル＝空のカタログ（Add Component 候補なし・既定値なし）
            catalog ?? new TypeCatalog(Path.Combine(Scratch(), "TypeCatalog.json"), NullLogger<TypeCatalog>.Instance),
            logger ?? NullLogger<EditorViewModel>.Instance);
    }

    /// <summary>この呼び出しだけが使う空ディレクトリ</summary>
    private static string Scratch() =>
        Directory.CreateDirectory(Path.Combine(ScratchRoot, Guid.NewGuid().ToString("N"))).FullName;
}

/// <summary>バイト列の代わりに写しを控えるテスト用シリアライザ</summary>
/// <remarks>具象 blob 形式を持たないテストで、undo と権威ツリー送信の写し取りだけを成立させる。</remarks>
internal sealed class CloneBlobSerializer : IHierarchyBlobSerializer
{
    private readonly List<IReadOnlyList<HierarchyNode>> _kept = new();

    public byte[] Serialize(IReadOnlyList<HierarchyNode> roots)
    {
        lock (_kept)
        {
            _kept.Add(roots.Select(Clone).ToArray());
            return BitConverter.GetBytes(_kept.Count - 1);
        }
    }

    public IReadOnlyList<HierarchyNode> Deserialize(byte[] sceneBlob)
    {
        lock (_kept) return _kept[BitConverter.ToInt32(sceneBlob)].Select(Clone).ToArray();
    }

    private static HierarchyNode Clone(HierarchyNode node) => new(
        node.ObjectId,
        node.Name,
        node.Children.Select(Clone).ToArray(),
        node.Components.Select(component => component.Clone()).ToArray(),
        node.SceneId,
        node.Active);
}
