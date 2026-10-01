using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.Serialization.Editor;
using EmptyEngine.ObjectModel;
using EmptyEngine.Storage;
using EmptyEngine.Tests;
using EmptyEngine.World;
using EmptyEngine.World.Editor;
using Xunit;

namespace EmptyEngine.Modules.Testing;

/// <summary><see cref="AuthoringObject"/> を扱うテスト用の補助</summary>
internal static class AuthoringTestHelpers
{
    /// <summary>取り込み結果 1 件の authoring 表現</summary>
    public static AuthoringObject AssetOf(AssetImportResult result, int index = 0) =>
        Assert.IsType<ImportedAsset>(result.Assets[index]).Value;

    /// <summary>取り込み結果 1 件のシーンルート</summary>
    public static HierarchyNode SceneOf(AssetImportResult result, int index = 0) =>
        Assert.IsType<ImportedScene>(result.Assets[index]).Root;

    /// <summary>カタログから引いた 1 件のシーンルート</summary>
    public static HierarchyNode RootOf(ImportedSource? source) =>
        Assert.IsType<ImportedScene>(source).Root;

    /// <summary>authoring 表現を成果物へ書いて、ランタイムの実体として読み直す</summary>
    /// <remarks><paramref name="root"/> は本体データを読み終えるまで削除しないこと。</remarks>
    public static async Task<T> ResolveAsync<T>(string root, AuthoringObject asset, string key = "probe")
        where T : class
    {
        await new AssetArtifactStore(root, CatalogStub.Schemas).SaveAsync(new AssetKey(key), asset);
        Assert.True(new WorldAssetResolver(AssetStorage.FromDirectory(root))
            .TryLoad(new AssetReference<T>(key), out T? resolved));
        return resolved!;
    }

    /// <summary>型名一致の <see cref="AuthoringObject"/> か</summary>
    public static bool IsType<T>(object? component) =>
        component is AuthoringObject authoring && authoring.TypeName == typeof(T).FullName;

    /// <summary>スカラー葉の CLR の数値としての読み取り</summary>
    public static double GetNumber(object? component, string field) => Member(component, field).NumericValue;

    /// <summary>スカラー葉の <see cref="int"/> としての読み取り</summary>
    public static int GetInt(object? component, string field) => (int)GetNumber(component, field);

    /// <summary>文字列葉の読み取り</summary>
    public static string GetString(object? component, string field) => Scalar(component, field).Text ?? string.Empty;

    /// <summary>スカラー葉を種別を保ったまま書き換える（インスペクタのフィールド編集と同じ経路）</summary>
    public static void SetNumber(object? component, string field, double value)
    {
        FieldViewModel member = Member(component, field);
        member.NumericValue = value;
        Scalar(component, field).CopyFrom(member.Capture());
    }

    /// <summary>アセット参照フィールドの GUID の読み取り</summary>
    public static string GetAssetKey(object? component, string field) =>
        Member(component, field).AssetKey ?? string.Empty;

    /// <summary>アセット参照フィールドへの GUID の書き込み</summary>
    public static void SetAssetKey(object? component, string field, string assetKey)
    {
        FieldViewModel member = Member(component, field);
        member.AssignAssetReference(new AssetKey(assetKey));
        Scalar(component, field).CopyFrom(member.Capture());
    }

    /// <summary>インスペクタが組み立てる編集メンバを名前で引く</summary>
    private static FieldViewModel Member(object? component, string field)
    {
        AuthoringObject authoring = Assert.IsType<AuthoringObject>(component);
        return new FieldViewModel(field, authoring.Schema.Fields[field], authoring.Data.Get(field));
    }

    /// <summary>オブジェクト参照ノードからの Id の読み取り</summary>
    public static string TargetIdOf(FieldValue node) => node.Text ?? string.Empty;

    /// <summary>オブジェクト参照フィールドからの Id の読み取り</summary>
    public static string GetTargetId(object? component, string field)
    {
        var reference = Assert.IsType<FieldValue>(Map(component).Get(field));
        return reference.Text ?? string.Empty;
    }

    /// <summary>オブジェクト参照の配列フィールドからの TargetId 列の読み取り</summary>
    public static IReadOnlyList<string> GetTargetIds(object? component, string field)
    {
        var array = Assert.IsType<FieldValue>(Map(component).Get(field));
        return array.Items
            .Select(item => Assert.IsType<FieldValue>(item).Text ?? string.Empty)
            .ToList();
    }

    /// <summary>CLR コンポーネントの <see cref="AuthoringObject"/> への変換</summary>
    public static AuthoringObject Authoring(IAttachable component)
    {
        var scene = new RootInstanceData(
            "_",
            new ObjectData("_", "_", [new ComponentData(component.GetType().FullName!, component)], []));

        return AuthoringBlobCodec.Decode(SceneBlob.Encode([scene]), CatalogStub.Schemas)[0].Components[0];
    }

    /// <summary>コンポーネント列のまとめての <see cref="AuthoringObject"/> 化</summary>
    public static AuthoringObject[] Components(params IAttachable[] components) =>
        components.Select(Authoring).ToArray();

    private static FieldValue Map(object? component) =>
        Assert.IsType<FieldValue>(Assert.IsType<AuthoringObject>(component).Data);

    private static FieldValue Scalar(object? component, string field) =>
        Assert.IsType<FieldValue>(Map(component).Get(field));
}
