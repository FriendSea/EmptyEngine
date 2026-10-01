using EmptyEngine.Modules.Testing;
using System.Text;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Catalog;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.World.Editor;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>スキーマと保存データの差異が、他の型やシーンの読み込みを妨げないことを検証する。</summary>
public sealed class SchemaDriftTests
{
    private static readonly FieldTypeInfo AssetRef = new("Probe.Texture", FieldKind.AssetReference);

    private static readonly ObjectSchema Holder = new()
    {
        TypeName = "Probe.Holder",
        DisplayName = "Holder",
        Fields = new Dictionary<string, FieldTypeInfo>
        {
            ["Icon"] = AssetRef,
            ["Count"] = new("System.Int32", FieldKind.Int),
        },
    };

    /// <summary>鍵の葉を失った参照 map への代入</summary>
    /// <summary>nil で届いた参照への代入</summary>
    /// <summary>整数欄のスクラブは最近接の整数へ丸める。</summary>
    [Theory]
    [InlineData(2.5, 2L)]
    [InlineData(3.5, 4L)]
    [InlineData(-3.5, -4L)]
    public void Integer_scrubbing_rounds_to_the_nearest_integer(double written, long expected)
    {
        var data = new FieldValue();
        data.Add("Icon", new FieldValue());
        data.Add("Count", new FieldValue { Integer = 0 });

        var count = new FieldViewModel("Count", Holder.Fields["Count"], data.Get("Count"));

        count.NumericValue = written;

        Assert.Equal(expected, count.Capture().Integer);
    }

    /// <summary>引けないメンバが 1 つあっても、他の型は引ける</summary>
    /// <summary>自己参照する型がカタログ全体を落とさない</summary>
    [Fact]
    public void A_self_referencing_type_still_resolves()
    {
        TypeCatalogDocument document = TypeCatalogDocument.Parse(Encoding.UTF8.GetBytes("""
        {
          "x-formatVersion": 5,
          "$defs": {
            "Game.Node": {
              "type": "object", "x-kind": "struct", "title": "Node", "x-attachable": true,
              "properties": {
                "Name": { "$ref": "#/$defs/System.String" },
                "Self": { "$ref": "#/$defs/Game.Node" }
              }
            },
            "System.String": { "x-kind": "string" }
          }
        }
        """), NullLogger.Instance);

        ObjectSchema node = Assert.Single(document.Types, entry => entry.Attachable).Schema;

        Assert.True(node.Root.TryMember("Name", out _));
    }

    /// <summary>カタログに無い型のコンポーネントは読み飛ばし、残りのシーンは通す</summary>
    [Fact]
    public void An_unknown_component_type_does_not_take_the_whole_scene_down()
    {
        // 送り手だけが知っている型。受け手のカタログ（実型の反射）では引けない。
        var ghostSchema = new ObjectSchema
        {
            TypeName = "Ghost.Deleted.Component",
            DisplayName = "Ghost",
            Fields = new Dictionary<string, FieldTypeInfo>(),
        };

        ObjectSchema known = CatalogStub.Schema(typeof(TestTransform));
        var knownData = new FieldValue();
        knownData.Add("X", new FieldValue { Real = 1 });
        knownData.Add("Y", new FieldValue { Real = 2 });
        knownData.Add("Z", new FieldValue { Real = 3 });

        byte[] blob = AuthoringBlobCodec.Encode([
            new HierarchyNode("obj-1", "O", [], [
                new AuthoringObject(ghostSchema, new FieldValue()),
                new AuthoringObject(known, knownData),
            ]),
        ]);

        HierarchyNode root = Assert.Single(AuthoringBlobCodec.Decode(blob, CatalogStub.Schemas));
        AuthoringObject survivor = Assert.Single(root.Components);

        Assert.Equal(known.TypeName, survivor.TypeName);
        Assert.Equal(2d, survivor.Data.Get("Y")!.Real, precision: 5);
    }
}
