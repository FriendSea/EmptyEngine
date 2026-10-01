using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Distribution;
using EmptyEngine.Storage;
using EmptyEngine.Storage.Editor;

namespace EmptyEngine.World.Editor;

/// <summary>シーンをランタイムで読み込める成果物として保存・復元する</summary>
public sealed class SceneArtifactStore : ISceneArtifactStore
{
    private readonly EditorAssetStorage _store;
    private readonly IHierarchyBlobSerializer _blobs;

    /// <summary>編集セッションの共有ルートへ書く既定の構築</summary>
    public SceneArtifactStore(IHierarchyBlobSerializer blobSerializer)
        : this(EditorAssetStorage.Shared(), blobSerializer) { }

    /// <summary>配布デプロイの書き先</summary>
    public SceneArtifactStore(DistributionRoot distribution, IHierarchyBlobSerializer blobSerializer)
        : this(
            EditorAssetStorage.AtDirectory(ImportedAssetsLayout.ResolveDistributionRoot(distribution.ExeDir)),
            blobSerializer) { }

    /// <param name="outputRootPath">アーティファクトの書き先ルート。</param>
    /// <param name="blobSerializer">ツリーと blob の相互変換</param>
    public SceneArtifactStore(string outputRootPath, IHierarchyBlobSerializer blobSerializer)
        : this(EditorAssetStorage.AtDirectory(outputRootPath), blobSerializer) { }

    private SceneArtifactStore(EditorAssetStorage store, IHierarchyBlobSerializer blobSerializer)
    {
        _store = store;
        _blobs = blobSerializer;
    }

    public Task SaveAsync(AssetKey scene, IReadOnlyList<HierarchyNode> roots, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _store.Write(scene.Value, _blobs.Serialize(roots));
        return Task.CompletedTask;
    }

    public void Delete(AssetKey scene) => _store.Delete(scene.Value);

    public IReadOnlyList<HierarchyNode>? Load(AssetKey scene)
    {
        try
        {
            using Stream? stream = _store.OpenRead(scene.Value);
            if (stream is null) return null;

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return _blobs.Deserialize(memory.ToArray());
        }
        catch
        {
            return null;
        }
    }
}
