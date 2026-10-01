using EmptyEngine.Serialization;
using EmptyEngine.Storage;
using Xunit;

namespace EmptyEngine.Modules.Testing;

/// <summary>同期に完了するストアを使うテストのための読み出し</summary>
internal static class AssetStorageTestExtensions
{
    /// <summary>待たずに開けるか</summary>
    public static bool CanOpenNow(this AssetStorage store, string key)
    {
        if (!store.TryOpen(key, out Stream? stream)) return false;
        stream.Dispose();
        return true;
    }

    /// <summary>読み出しの完了まで済ませる <see cref="AssetStorage.OpenAsync"/></summary>
    public static Stream? OpenNow(this AssetStorage store, string key)
    {
        ValueTask<Stream?> opening = store.OpenAsync(key);
        Assert.True(opening.IsCompleted, "The test store is expected to open synchronously.");
        return opening.GetAwaiter().GetResult();
    }

    /// <summary>キーのアーティファクトからのアセットの復元</summary>
    public static object? DeserializeNow(this AssetStorage store, string key)
    {
        using Stream artifact = store.OpenNow(key) ?? throw new FileNotFoundException(key);
        return AssetBlob.Deserialize(store, key, artifact);
    }
}
