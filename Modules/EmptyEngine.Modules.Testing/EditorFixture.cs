using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Catalog;
using EmptyEngine.Editor.State;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.World.Editor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmptyEngine.Modules.Testing;

/// <summary>シーンのシリアライズに対応したテスト用エディタを作成する。</summary>
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
        ILogger<EditorViewModel>? logger = null)
    {
        if ((assets is null) != (imports is null))
            throw new ArgumentException("Pass the asset catalog together with the import service that writes into it.");

        assets ??= new AssetCatalog();
        return new EditorViewModel(
            assets,
            imports ?? new AssetImportService(assets, Scratch(), []),
            serializer ?? new HierarchyBlobSerializer(CatalogStub.Schemas),
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
