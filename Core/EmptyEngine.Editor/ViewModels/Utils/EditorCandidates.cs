using EmptyEngine.Core;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Catalog;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.Editor.ViewModels.Inspector;

namespace EmptyEngine.Editor.ViewModels.Utils;

/// <summary>ピッカーへ出す候補の集め方</summary>
/// <remarks>型の制約と呼び出し側が指定した条件に合う候補を返す。</remarks>
internal static class EditorCandidates
{
    /// <summary>アセット参照フィールドの候補</summary>
    /// <param name="constraintTypeName">フィールドが要求する型の完全修飾名。<c>null</c> なら型を問わない。</param>
    public static IReadOnlyList<AssetCandidate> ForAsset(AssetCatalog catalog, string? constraintTypeName)
    {
        var result = new List<AssetCandidate>();
        foreach (CatalogEntry entry in catalog.EnumerateAssets())
        {
            if (!Matches(catalog, entry.Key, entry.IsScene, constraintTypeName)) continue;
            result.Add(new AssetCandidate(entry.DisplayPath, entry.Key));
        }
        return result;
    }

    /// <summary>シーンアセットだけの候補</summary>
    /// <param name="accept">キーごとの追加の絞り込み。<c>null</c> なら全てのシーン。</param>
    public static IReadOnlyList<AssetCandidate> ForScene(AssetCatalog catalog, Func<string, bool>? accept = null)
    {
        var result = new List<AssetCandidate>();
        foreach (CatalogEntry entry in catalog.EnumerateAssets())
        {
            if (!entry.IsScene) continue;
            if (accept is not null && !accept(entry.Key.Value)) continue;

            result.Add(new AssetCandidate(entry.DisplayPath, entry.Key));
        }
        return result;
    }

    /// <summary>シーン 1 本の中のオブジェクト候補（ルートからの道を表示名にする）</summary>
    /// <param name="componentTypeName">型付き参照が要求するコンポーネント型の完全修飾名。<c>null</c> なら型を問わない。</param>
    public static IReadOnlyList<ObjectCandidate> ForObject(HierarchyNodeViewModel root, string? componentTypeName)
    {
        var result = new List<ObjectCandidate>();
        Collect(root, componentTypeName, result);
        return result;
    }

    /// <summary>このアセットをその型を要求する参照フィールドへ入れられるか</summary>
    public static bool Matches(
        AssetCatalog catalog,
        AssetKey reference,
        bool isScene,
        string? constraintTypeName)
    {
        if (constraintTypeName is null) return true;
        if (constraintTypeName == TypeKind.SceneTarget) return isScene;
        if (isScene) return false;

        return IsAssignable(catalog.GetAsset(reference.Value), constraintTypeName);
    }

    /// <summary>アセット実体が <paramref name="constraintTypeName"/> へ代入できるか</summary>
    /// <remarks>代入可能な型の記述がない場合は候補に残す。</remarks>
    private static bool IsAssignable(AuthoringObject? value, string constraintTypeName)
    {
        if (value is null) return false;

        ObjectSchema schema = value.Schema;
        return schema.AssignableTypeNames.Count == 0 || schema.IsAssignableTo(constraintTypeName);
    }

    private static void Collect(
        HierarchyNodeViewModel node, string? componentTypeName, List<ObjectCandidate> into, string? parentPath = null)
    {
        string path = parentPath is null ? node.Name : $"{parentPath}/{node.Name}";

        if (componentTypeName is null || node.Components.Any(c => HasComponentType(c.Schema, componentTypeName)))
            into.Add(new ObjectCandidate(path, node.ObjectId));

        foreach (HierarchyNodeViewModel child in node.Children)
            Collect(child, componentTypeName, into, path);
    }

    /// <summary>コンポーネントが <paramref name="componentTypeName"/> 型として取り出せるか</summary>
    private static bool HasComponentType(ObjectSchema component, string componentTypeName) =>
        component.TypeName == componentTypeName || component.IsAssignableTo(componentTypeName);
}
