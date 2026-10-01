using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Distribution;
using EmptyEngine.Storage;

namespace BevyRuntimeSample.Editor;

/// <summary>シーンを Bevy ランタイムが読むテキスト形式で読み書きするアーティファクト永続化境界</summary>
public sealed class BevySceneArtifactStore : ISceneArtifactStore
{
    private readonly string _root;
    private readonly ISchemaSource _schemas;

    /// <summary>編集セッション用のシーン保存先を使用する。</summary>
    public BevySceneArtifactStore(ISchemaSource schemas)
        : this(ImportedAssetsLayout.ResolveEditorRoot(), schemas) { }

    /// <summary>配布デプロイの書き先</summary>
    public BevySceneArtifactStore(DistributionRoot distribution, ISchemaSource schemas)
        : this(ImportedAssetsLayout.ResolveDistributionRoot(distribution.ExeDir), schemas) { }

    private BevySceneArtifactStore(string root, ISchemaSource schemas)
    {
        _root = root;
        _schemas = schemas;
    }

    public Task SaveAsync(AssetKey scene, IReadOnlyList<HierarchyNode> roots, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, scene.Value), BevySceneText.WriteScene(roots[0]));
        return Task.CompletedTask;
    }

    public void Delete(AssetKey scene)
    {
        string path = Path.Combine(_root, scene.Value);
        if (File.Exists(path)) File.Delete(path);
    }

    public IReadOnlyList<HierarchyNode>? Load(AssetKey scene)
    {
        string path = Path.Combine(_root, scene.Value);
        if (!File.Exists(path)) return null;

        try { return new[] { BevySceneText.ReadScene(File.ReadAllText(path), _schemas) }; }
        catch { return null; }
    }
}

/// <summary>非シーンアセットを Bevy の AssetServer が読める素のファイルとして置くアーティファクト永続化境界</summary>
/// <remarks>非シーンアセットは専用ディレクトリへ <c>{guid}.{ext}</c> の名前で保存する。</remarks>
public sealed class BevyAssetArtifactStore : IAssetArtifactStore
{
    private const string ExtensionField = "Extension";
    private const string BodyField = "Body";

    // Rust 側に対応する型は無い（カタログの x-target と揃えた合成 id）。
    private const string AuthoringTypeName = "BevyRuntimeSample.Editor.BevyRawAsset";

    private static readonly ObjectSchema RawAssetSchema = new()
    {
        TypeName = AuthoringTypeName, DisplayName = "Bevy asset",
        Fields = new Dictionary<string, FieldTypeInfo>
        {
            [ExtensionField] = new("System.String", FieldKind.String),
            [BodyField] = new("EmptyEngine.Core.IAssetBinary", FieldKind.Binary),
        },
        BinaryFields = [BodyField],
    };

    private readonly string _root;

    /// <summary>編集セッション用のアセット保存先を使用する。</summary>
    public BevyAssetArtifactStore() : this(ImportedAssetsLayout.ResolveEditorRoot()) { }

    /// <summary>配布デプロイの書き先</summary>
    public BevyAssetArtifactStore(DistributionRoot distribution)
        : this(ImportedAssetsLayout.ResolveDistributionRoot(distribution.ExeDir)) { }

    private BevyAssetArtifactStore(string root) => _root = root;

    private string AssetsDir => Path.Combine(Path.GetDirectoryName(_root)!, "assets");

    /// <summary>本体と拡張子からの authoring 表現の組み立て</summary>
    public static AuthoringObject RawAsset(IAssetBinary body, string extension)
    {
        var data = new FieldValue();
        data.Add(ExtensionField, new FieldValue { Text = extension });
        data.Add(BodyField, new FieldValue { Binary = body });
        return new AuthoringObject(RawAssetSchema, data);
    }

    public async Task SaveAsync(AssetKey asset, AuthoringObject value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        string extension = value.Data.Get(ExtensionField)?.Text
            ?? throw new NotSupportedException("Bevy raw asset authoring data is missing Extension.");
        IAssetBinary body = value.Data.Get(BodyField)?.Binary
            ?? throw new NotSupportedException("Bevy raw asset authoring data is missing body bytes.");

        Directory.CreateDirectory(AssetsDir);
        string destPath = Path.Combine(AssetsDir, $"{asset.Value}.{extension}");

        await using Stream source = body.OpenRead();
        await using FileStream destination = File.Create(destPath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    public void Delete(AssetKey asset)
    {
        if (!Directory.Exists(AssetsDir)) return;

        // 古い拡張子のファイルが再読み込みされないよう、同じキーのファイルをすべて除く。
        foreach (string path in Directory.GetFiles(AssetsDir, $"{asset.Value}.*"))
        {
            File.Delete(path);
        }
    }

    public AuthoringObject? Load(AssetKey asset)
    {
        if (!Directory.Exists(AssetsDir)) return null;

        string? path = Directory.EnumerateFiles(AssetsDir, $"{asset.Value}.*").FirstOrDefault();
        if (path is null) return null;

        return RawAsset(new FileAssetBinary(path), Path.GetExtension(path).TrimStart('.'));
    }
}

/// <summary>ランタイム通信形式（シーン blob）と <see cref="HierarchyNode"/> の相互変換</summary>
public sealed class BevyBlobSerializer(ISchemaSource schemas) : IHierarchyBlobSerializer
{
    public byte[] Serialize(IReadOnlyList<HierarchyNode> roots) => BevySceneText.WriteScenes(roots);

    public IReadOnlyList<HierarchyNode> Deserialize(byte[] sceneBlob) =>
        BevySceneText.ReadScenes(sceneBlob, schemas);
}

/// <summary>配布 exe 隣への起動シーン一覧の書き出し</summary>
public sealed class BevyDistributionBuilder : IDistributionBuilder
{
    public void Build(string exeDir, IReadOnlyList<AssetKey> scenes)
    {
        string root = ImportedAssetsLayout.ResolveDistributionRoot(exeDir);
        Directory.CreateDirectory(root);
        string manifest = Path.Combine(root, "startup.json");

        if (scenes.Count == 0)
        {
            if (File.Exists(manifest)) File.Delete(manifest);
            return;
        }

        string json = "[" + string.Join(",", scenes.Select(s => "\"" + s.Value + "\"")) + "]";
        File.WriteAllText(manifest, json);
    }
}
