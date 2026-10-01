using EmptyEngine.Modules.Testing;
using System.Text;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Serialization.Editor;
using EmptyEngine.ObjectModel;
using Xunit;

namespace EmptyEngine.Serialization.Tests;

/// <summary>インポータが返した実体の、カタログ驅動での authoring 表現への写し取り</summary>
public sealed class ImporterUtilsTests
{
    /// <summary>写し取りの対象にする、各種別を 1 つずつ持つアセット</summary>
    [Asset]
    public sealed class ProbeAsset
    {
        public int Width;
        public byte Level;
        public float Scale;
        public string Label = string.Empty;
        public ProbeMode Mode;
        public int[] Steps = [];
        public AssetReference<TestAsset> Source = new();
        public IAssetBinary Payload = null!;
    }

    public enum ProbeMode
    {
        Off = 0,
        On = 7,
    }

    [Fact]
    public void Capture_reads_every_kind_the_schema_declares()
    {
        var asset = new ProbeAsset
        {
            Width = 640,
            Level = 200,
            Scale = 1.5f,
            Label = "probe",
            Mode = ProbeMode.On,
            Steps = [1, 2, 3],
            Source = AssetReference<TestAsset>.FromKey("inner.txt"),
            Payload = new BytesAssetBinary([1, 2, 3, 4]),
        };

        AuthoringObject captured = ImporterUtils.FromClr(asset, CatalogStub.Schemas);
        FieldValue data = captured.Data;

        Assert.Equal(typeof(ProbeAsset).FullName, captured.TypeName);
        Assert.Equal(640, data.Get(nameof(ProbeAsset.Width))!.Integer);
        Assert.Equal(200UL, data.Get(nameof(ProbeAsset.Level))!.Unsigned);
        Assert.Equal(1.5, data.Get(nameof(ProbeAsset.Scale))!.Real);
        Assert.Equal("probe", data.Get(nameof(ProbeAsset.Label))!.Text);
        Assert.Equal(7, data.Get(nameof(ProbeAsset.Mode))!.Integer);
        Assert.Equal([1L, 2L, 3L], data.Get(nameof(ProbeAsset.Steps))!.Items.Select(i => i.Integer));
        Assert.Equal("inner.txt", data.Get(nameof(ProbeAsset.Source))!.ReferenceKey);
        Assert.Equal(4, data.Get(nameof(ProbeAsset.Payload))!.Binary!.Length);
    }

    /// <summary>本体は封筒の外へ行く――メタデータの map には現れない</summary>
    [Fact]
    public void Binary_members_stay_out_of_the_metadata_map()
    {
        var asset = new ProbeAsset { Width = 1, Payload = new BytesAssetBinary([9]) };
        AuthoringObject captured = ImporterUtils.FromClr(asset, CatalogStub.Schemas);

        string metadata = Encoding.UTF8.GetString(
            FieldValueCodec.EncodeBytes(captured.Data, captured.Schema.Root));

        Assert.DoesNotContain(nameof(ProbeAsset.Payload), metadata, StringComparison.Ordinal);
        Assert.Contains(nameof(ProbeAsset.Width), metadata, StringComparison.Ordinal);
    }
}
