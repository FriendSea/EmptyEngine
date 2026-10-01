using EmptyEngine.Modules.Testing;
using EmptyEngine.Serialization;
using System.IO.Compression;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Serialization.Editor;
using EmptyEngine.Storage.Editor;
using EmptyEngine.Tests;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.Storage.Tests;

/// <summary>単一具象 <see cref="AssetStorage"/> の検証</summary>
public sealed class AssetStorageTests
{
    [Fact]
    public void DirectoryAssetStore_reads_each_key_from_its_bin_file_with_seek()
    {
        string root = CreateTempDir();
        try
        {
            byte[] payload = { 10, 20, 30, 40, 50, 60 };
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllBytes(Path.Combine(root, "nested", "a.bin"), payload);
            File.WriteAllBytes(Path.Combine(root, "b.bin"), new byte[] { 1, 2 });

            var store = AssetStorage.FromDirectory(root);

            Assert.True(store.CanOpenNow("b"));
            Assert.True(store.CanOpenNow("nested/a"));
            Assert.True(store.CanOpenNow("nested\\a"));
            Assert.False(store.CanOpenNow("missing"));
            Assert.False(store.CanOpenNow("b.bin"));

            using Stream stream = store.OpenNow("nested/a")!;
            Assert.True(stream.CanSeek);
            stream.Seek(2, SeekOrigin.Begin);
            byte[] tail = new byte[4];
            stream.ReadExactly(tail);
            Assert.Equal(new byte[] { 30, 40, 50, 60 }, tail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ArchiveAssetStore_reads_only_bin_entries_by_their_key()
    {
        string root = CreateTempDir();
        try
        {
            string archive = Path.Combine(root, ImportedAssetsLayout.ArchiveFileName);
            using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using (Stream entry = zip.CreateEntry("nested/a.bin").Open()) entry.WriteByte(1);
                using (Stream entry = zip.CreateEntry("plain").Open()) entry.WriteByte(2);
            }

            using var store = AssetStorage.FromArchive(archive);

            Assert.True(store.CanOpenNow("nested/a"));
            Assert.False(store.CanOpenNow("nested/a.bin"));
            Assert.False(store.CanOpenNow("plain"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DirectoryAssetStore_rejects_keys_outside_its_root()
    {
        string parent = CreateTempDir();
        string root = Path.Combine(parent, "artifacts");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(parent, "outside.bin"), "outside");

        try
        {
            using var store = AssetStorage.FromDirectory(root);

            Assert.Throws<ArgumentException>(() => store.TryOpen("../outside.bin", out _));
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.OpenAsync("../outside.bin"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void EditorAssetStore_cannot_write_or_delete_outside_its_root()
    {
        string parent = CreateTempDir();
        string root = Path.Combine(parent, "artifacts");
        Directory.CreateDirectory(root);
        string outside = Path.Combine(parent, "outside.bin");
        File.WriteAllText(outside, "keep");

        try
        {
            var store = EditorAssetStorage.AtDirectory(root);

            Assert.Throws<ArgumentException>(() => store.Write("../outside.bin", [1, 2, 3]));
            Assert.Throws<ArgumentException>(() => store.Delete("../outside.bin"));
            Assert.Equal("keep", File.ReadAllText(outside));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Resolver_resolves_against_a_non_filesystem_store()
    {
        using var blob = new MemoryStream();
        AssetBlob.SerializeTo(blob, new TestAsset("from-memory"));

        var store = AssetStorage.InMemory();
        store.Add("mem/asset.dat", blob.ToArray());

        var resolver = WorldAssetResolver.FromStore(store);

        bool resolved = resolver.TryLoad(new AssetReference<TestAsset>("mem/asset.dat"), out TestAsset? actual);

        Assert.True(resolved);
        Assert.Equal("from-memory", actual!.Text);
        Assert.False(resolver.TryLoad(new AssetReference<TestAsset>("mem/missing.dat"), out TestAsset? _));
    }

    [Fact]
    public void Resolver_caches_until_updated()
    {
        var store = AssetStorage.InMemory();
        store.Add("mem/asset.dat", Serialize("v1"));

        var resolver = WorldAssetResolver.FromStore(store);
        var key = new AssetKey("mem/asset.dat");

        Assert.True(resolver.TryLoad(new AssetReference<TestAsset>(key), out TestAsset? first));
        Assert.Equal("v1", first!.Text);

        store.Add("mem/asset.dat", Serialize("v2"));
        Assert.True(resolver.TryResolve(new AssetReference<TestAsset>(key), out TestAsset? cached));
        Assert.Equal("v1", cached!.Text);

        resolver.RequestUpdate(key);
        Assert.True(resolver.TryResolve(new AssetReference<TestAsset>(key), out TestAsset? fresh));
        Assert.Same(first, fresh);
        Assert.Equal("v2", first.Text);

        store.Add("mem/asset.dat", Serialize("v3"));
        resolver.RequestUpdate(key);
        Assert.True(resolver.TryResolve(new AssetReference<TestAsset>(key), out TestAsset? again));
        Assert.Same(first, again);
        Assert.Equal("v3", first.Text);
    }

    [Fact]
    public void Update_leaves_unloaded_keys_alone()
    {
        var store = AssetStorage.InMemory();
        store.Add("mem/asset.dat", Serialize("v1"));

        var resolver = WorldAssetResolver.FromStore(store);
        var key = new AssetKey("mem/asset.dat");

        resolver.RequestUpdate(key);
        store.Add("mem/asset.dat", Serialize("v2"));
        Assert.True(resolver.TryLoad(new AssetReference<TestAsset>(key), out TestAsset? resolved));
        Assert.Equal("v2", resolved!.Text);
    }

    [Fact]
    public async Task Failed_save_does_not_truncate_the_existing_artifact()
    {
        string root = CreateTempDir();
        try
        {
            var artifacts = new AssetArtifactStore(root, CatalogStub.Schemas);
            var key = new AssetKey("asset.dat");
            await artifacts.SaveAsync(key, ImporterUtils.FromClr(new TestAsset("before"), CatalogStub.Schemas));
            byte[] before = File.ReadAllBytes(Path.Combine(root, ImportedAssetsLayout.ArtifactPath(key.Value)));

            // 本体の読み出しで落ちる＝封筒を一時ファイルへ書いたあとで失敗する。
            AuthoringObject broken = ImporterUtils.FromClr(new BinaryTestAsset
            {
                Width = 1,
                Height = 1,
                Payload = new UnreadableBinary(),
            }, CatalogStub.Schemas);

            await Assert.ThrowsAsync<IOException>(() => artifacts.SaveAsync(key, broken));

            Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, ImportedAssetsLayout.ArtifactPath(key.Value))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>長さだけ名乗って中身を読ませない本体ハンドル</summary>
    private sealed class UnreadableBinary : IAssetBinary
    {
        public long Length => 4;

        public Stream OpenRead() => throw new IOException("payload is unreadable");
    }

    private static byte[] Serialize(string text)
    {
        using var blob = new MemoryStream();
        AssetBlob.SerializeTo(blob, new TestAsset(text));
        return blob.ToArray();
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
