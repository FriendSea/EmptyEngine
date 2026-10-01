using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Storage;
using EmptyEngine.Storage.Editor;

namespace EmptyEngine.World.Editor;

/// <summary>起動シーン一覧とアセットを配布用アーカイブにまとめる</summary>
public sealed class DistributionBuilder : IDistributionBuilder
{
    public void Build(string exeDir, IReadOnlyList<AssetKey> scenes)
    {
        string staging = ImportedAssetsLayout.ResolveDistributionRoot(exeDir);
        var store = EditorAssetStorage.AtDirectory(staging);

        if (scenes.Count == 0)
            store.Delete(StartupScene.ManifestKey);
        else
            store.Write(StartupScene.ManifestKey, StartupScene.SerializeManifest(scenes));

        if (!Directory.Exists(staging)) return;

        EditorAssetStorage.PackDirectory(staging, ImportedAssetsLayout.ResolveDistributionArchive(exeDir));
        Directory.Delete(staging, recursive: true);
    }
}
