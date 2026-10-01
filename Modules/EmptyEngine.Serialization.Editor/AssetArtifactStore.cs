using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Distribution;
using EmptyEngine.Storage;
using EmptyEngine.Storage.Editor;

namespace EmptyEngine.Serialization.Editor;

/// <summary>非シーンアセットを MessagePack 形式の成果物として保存・復元する</summary>
public sealed class AssetArtifactStore : IAssetArtifactStore
{
    private readonly EditorAssetStorage _store;
    private readonly ISchemaSource _schemas;

    /// <summary>編集セッションの共有ルートへ書く既定の構築</summary>
    public AssetArtifactStore(ISchemaSource schemas) : this(EditorAssetStorage.Shared(), schemas) { }

    /// <summary>配布デプロイの書き先</summary>
    public AssetArtifactStore(DistributionRoot distribution, ISchemaSource schemas)
        : this(EditorAssetStorage.AtDirectory(ImportedAssetsLayout.ResolveDistributionRoot(distribution.ExeDir)), schemas) { }

    /// <param name="outputRootPath">アーティファクトの書き先ルート。</param>
    /// <param name="schemas">復元に使う型カタログ</param>
    public AssetArtifactStore(string outputRootPath, ISchemaSource schemas)
        : this(EditorAssetStorage.AtDirectory(outputRootPath), schemas) { }

    private AssetArtifactStore(EditorAssetStorage store, ISchemaSource schemas)
    {
        _store = store;
        _schemas = schemas;
    }

    public Task SaveAsync(AssetKey asset, AuthoringObject value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _store.WriteAtomically(asset.Value, file => AuthoringAssetCodec.Encode(file, value));

        return Task.CompletedTask;
    }

    public void Delete(AssetKey asset) => _store.Delete(asset.Value);

    public AuthoringObject? Load(AssetKey asset)
    {
        if (!_store.Exists(asset.Value)) return null;

        try
        {
            return AuthoringAssetCodec.Decode(() => Open(asset.Value), _schemas);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>本体の遅延読みが読むたびに呼び直す再オープン口</summary>
    /// <remarks>復元後に消えていれば投げる（黙って空のバイト列を返さない）</remarks>
    private Stream Open(string key) =>
        _store.OpenRead(key) ?? throw new FileNotFoundException($"Artifact '{key}' is not present under {_store.Root}.", key);
}
