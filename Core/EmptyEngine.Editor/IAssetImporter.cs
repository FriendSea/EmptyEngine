namespace EmptyEngine.Editor;

public interface IAssetImporter
{
    IReadOnlyCollection<string> SupportedExtensions { get; }

    Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>インスペクタで編集したアセット実体をソースファイルへ書き戻せるか</summary>
    bool IsSaveSupported => false;

    /// <summary>編集済みアセット実体のソース形式への書き戻し</summary>
    Task SaveAsync(AuthoringObject asset, string sourcePath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not support saving assets.");
}

/// <summary>シーンとしてランタイムに送信できる双方向インポーター</summary>
public interface ISceneImporter : IAssetImporter
{
    /// <summary>シーン構造のソースファイルへの書き戻し</summary>
    Task SaveAsync(HierarchyNode scene, string filePath, CancellationToken cancellationToken = default);
}

/// <summary>オリジナルから派生するバリアントソースを新規作成できるシーンインポーター</summary>
public interface IVariantImporter : ISceneImporter
{
    /// <summary>差分の無いバリアントソースの書き出し</summary>
    Task CreateAsync(string originalKey, string filePath, CancellationToken cancellationToken = default);
}

/// <summary>別のシーンアセットをネストした子として配置できるシーンインポーター</summary>
public interface INestedPrefabImporter : ISceneImporter
{
    /// <summary>プレハブを実体化した配置インスタンスのサブツリーの取得</summary>
    Task<HierarchyNode> InstantiateNestedAsync(string sourceKey, string sourcePath, CancellationToken cancellationToken = default);

    /// <summary><paramref name="objectId"/> のノードがネスト配置インスタンスのルートかの判定</summary>
    bool IsNestedInstanceRoot(string objectId, string? parentObjectId);
}

public sealed record AssetImportRequest(
    string SourcePath,
    string RelativePath,
    string AssetsRootPath);

/// <summary>取り込み結果 1 件の身元（アセットでもシーンでも共通）</summary>
public abstract record ImportedSource(string RelativePath, string SourcePath)
{
    /// <summary>アーティファクト／参照キー</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>ファイル内でのローカル id</summary>
    public string LocalId { get; init; } = string.Empty;
}

/// <summary>取り込まれた非シーンアセット。</summary>
public sealed record ImportedAsset(string RelativePath, AuthoringObject Value, string SourcePath)
    : ImportedSource(RelativePath, SourcePath);

/// <summary>取り込まれたシーン（＝1 ルートオブジェクト）</summary>
public sealed record ImportedScene(string RelativePath, HierarchyNode Root, string SourcePath)
    : ImportedSource(RelativePath, SourcePath);

/// <summary>1 つのソースファイルの取り込み結果</summary>
public sealed record AssetImportResult(bool Success, string Message, IReadOnlyList<ImportedSource> Assets)
{
    /// <summary>主（先頭）アセット</summary>
    public ImportedSource? Asset => Assets.Count > 0 ? Assets[0] : null;

    /// <summary>取込時に結果へ焼き込んだ他アセットのアーティファクトキー</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    public static AssetImportResult Succeeded(string message, ImportedSource asset) =>
        new(true, message, new[] { asset });

    public static AssetImportResult Succeeded(string message, IReadOnlyList<ImportedSource> assets) =>
        new(true, message, assets);

    public static AssetImportResult Failed(string message) =>
        new(false, message, Array.Empty<ImportedSource>());
}
