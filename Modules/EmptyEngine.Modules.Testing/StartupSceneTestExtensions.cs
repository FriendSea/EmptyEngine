using EmptyEngine.Core;
using EmptyEngine.Storage;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.Modules.Testing;

/// <summary>同期に完了するストアを使うテストのための起動シーンの適用</summary>
internal static class StartupSceneTestExtensions
{
    /// <summary>適用の完了まで済ませる <see cref="StartupScene.LoadAllIntoAsync(ISceneSerializer, AssetStorage, Action{string}, CancellationToken)"/></summary>
    public static IReadOnlyList<AssetKey> LoadAllIntoNow(this ISceneSerializer serializer, AssetStorage store)
    {
        Task<IReadOnlyList<AssetKey>> loading = serializer.LoadAllIntoAsync(store);
        Assert.True(loading.IsCompleted, "The test scene is expected to apply synchronously.");
        return loading.GetAwaiter().GetResult();
    }

    /// <summary>読み出しの完了まで済ませる <see cref="StartupScene.ReadStartupScenesAsync"/></summary>
    public static IReadOnlyList<AssetKey> ReadStartupScenesNow(this AssetStorage store)
    {
        Task<IReadOnlyList<AssetKey>> reading = StartupScene.ReadStartupScenesAsync(store);
        Assert.True(reading.IsCompleted, "The test store is expected to read synchronously.");
        return reading.GetAwaiter().GetResult();
    }

    /// <summary>読み出しの完了まで済ませる <see cref="StartupScene.FindFirstAsync"/></summary>
    public static AssetKey? FindFirstNow(this AssetStorage store)
    {
        Task<AssetKey?> finding = StartupScene.FindFirstAsync(store);
        Assert.True(finding.IsCompleted, "The test store is expected to read synchronously.");
        return finding.GetAwaiter().GetResult();
    }
}
