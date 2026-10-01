using EmptyEngine.Core;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.World;

/// <summary>部品の解決フック（<see cref="IAssetResolutionHook"/>）の呼び出し</summary>
internal static class AssetResolution
{
    /// <summary>並んだ部品・アセットのフックの一斉の呼び出しとすべての完了の待ち合わせ</summary>
    /// <remarks>各部品の読み込みは並行に進む。失敗は <paramref name="failed"/> へ渡して残りを続け、キャンセルは黙る。</remarks>
    public static async ValueTask ResolveAllAsync(
        IEnumerable<(object Target, string OwnerName)> targets,
        IAssetResolver resolver,
        Action<object, string, Exception> failed)
    {
        var pending = new List<(object Target, string OwnerName, ValueTask Resolving)>();
        foreach ((object attachable, string ownerName) in targets)
        {
            if (attachable is not IAssetResolutionHook hook)
                continue;

            try
            {
                ValueTask resolving = hook.OnResolveAssetsAsync(resolver);
                if (!resolving.IsCompletedSuccessfully)
                    pending.Add((attachable, ownerName, resolving));
            }
            catch (Exception e) when (!IsCancellation(e))
            {
                failed(attachable, ownerName, e);
            }
            catch (Exception)
            {
            }
        }

        foreach ((object attachable, string ownerName, ValueTask resolving) in pending)
        {
            try
            {
                await resolving;
            }
            catch (Exception e) when (!IsCancellation(e))
            {
                failed(attachable, ownerName, e);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>木の全部品と持ち主の名前の列挙（有効・無効を問わない）</summary>
    public static IEnumerable<(object Target, string OwnerName)> EnumerateTree(GameObject root)
    {
        foreach (IAttachable attachable in root.Attachables)
            yield return (attachable, root.Name);
        foreach (GameObject child in root.ChildList)
            foreach ((object Target, string OwnerName) entry in EnumerateTree(child))
                yield return entry;
    }

    /// <summary>復号しただけの部品と持ち主の名前の列挙</summary>
    public static IEnumerable<(object Target, string OwnerName)> EnumerateTree(ObjectData root)
    {
        foreach (ComponentData component in root.Components)
            if (component.Component is { } attachable)
                yield return (attachable, root.Name);
        foreach (ObjectData child in root.Children)
            foreach ((object Target, string OwnerName) entry in EnumerateTree(child))
                yield return entry;
    }

    internal static bool IsCancellation(Exception e) => e switch
    {
        OperationCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.All(IsCancellation),
        _ => false,
    };
}
