using System.Globalization;
using System.Linq;
using System.Text;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;

namespace BevyRuntimeSample.Editor;

/// <summary><c>bevy_scene</c> の RON と <see cref="HierarchyNode"/> の相互変換</summary>
public static class BevySceneText
{
    // シーン適用で Bevy の Entity が変わるため、ObjectId を別に保持して同一性を保つ。
    private const string ObjectIdComponentTypeName = "emptyengine::ObjectId";
    private const string NameComponentTypeName = "bevy_core::name::Name";
    private const string VisibilityComponentTypeName = "bevy_render::view::visibility::Visibility";
    private const string ParentComponentTypeName = "bevy_hierarchy::components::parent::Parent";

    /// <summary><see cref="VisibilityComponentTypeName"/> の非アクティブを表すバリアント</summary>
    /// <remarks>アクティブ側は既定の <c>Inherited</c>（親の可視性を継ぐ）＝書かない</remarks>
    private const string HiddenVariant = "Hidden";

    /// <summary>シーンインスタンス識別の付随セクション</summary>
    /// <remarks>ランタイムは中身を解釈せず poll の返答でそのまま返す。ワイヤだけで、ソースにもアーティファクトにも書かない</remarks>
    private const string SceneIdDelimiter = "\n@@scene_id@@\n";

    /// <summary>GUID 画像アセット参照の付随セクション</summary>
    private const string ImageSourcesDelimiter = "\n@@image_sources@@\n";

    /// <summary>シーンインスタンス識別を運ばない読み書き（ソース・アーティファクト）での既定</summary>
    private const string DefaultSceneName = "Scene";

    /// <summary>1 シーンから RON テキストへの書き出し</summary>
    public static string WriteScene(HierarchyNode root) => WriteScene(root, sceneId: null);

    private static string WriteScene(HierarchyNode root, string? sceneId)
    {
        var flat = new List<HierarchyNode>();
        var parentOf = new Dictionary<HierarchyNode, HierarchyNode>(ReferenceEqualityComparer.Instance);
        CollectFlat(root.Children, parent: null, flat, parentOf);

        var indexOf = new Dictionary<HierarchyNode, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < flat.Count; i++) indexOf[flat[i]] = i;

        var entities = new RonMap();
        var imageSources = new RonMap();
        for (int i = 0; i < flat.Count; i++)
        {
            HierarchyNode node = flat[i];
            var components = new RonMap();

            components.Entries.Add(new RonMapEntry
            {
                Key = new RonScalar { Kind = RonScalarKind.String, Text = ObjectIdComponentTypeName },
                Value = BuildTupleValue(new RonScalar { Kind = RonScalarKind.String, Text = node.ObjectId }),
            });

            components.Entries.Add(new RonMapEntry
            {
                Key = new RonScalar { Kind = RonScalarKind.String, Text = NameComponentTypeName },
                Value = BuildNameValue(node.Name),
            });

            if (!node.Active)
            {
                components.Entries.Add(new RonMapEntry
                {
                    Key = new RonScalar { Kind = RonScalarKind.String, Text = VisibilityComponentTypeName },
                    Value = new RonScalar { Kind = RonScalarKind.Ident, Text = HiddenVariant, RawText = HiddenVariant },
                });
            }

            if (parentOf.TryGetValue(node, out HierarchyNode? parent) && indexOf.TryGetValue(parent, out int parentIndex))
            {
                components.Entries.Add(new RonMapEntry
                {
                    Key = new RonScalar { Kind = RonScalarKind.String, Text = ParentComponentTypeName },
                    Value = BuildParentValue(EntityBits(parentIndex)),
                });
            }

            ulong entityBits = EntityBits(i);

            foreach (AuthoringObject component in node.Components)
            {
                (RonValue value, IReadOnlyList<string> assetKeys) = BevyComponent.ToRon(component);

                components.Entries.Add(new RonMapEntry
                {
                    Key = new RonScalar { Kind = RonScalarKind.String, Text = component.TypeName },
                    Value = value,
                });

                foreach (string guid in assetKeys)
                {
                    imageSources.Entries.Add(new RonMapEntry
                    {
                        Key = new RonScalar { Kind = RonScalarKind.Number, Number = entityBits, RawText = entityBits.ToString(CultureInfo.InvariantCulture) },
                        Value = new RonScalar { Kind = RonScalarKind.String, Text = guid },
                    });
                }
            }

            entities.Entries.Add(new RonMapEntry
            {
                Key = new RonScalar
                {
                    Kind = RonScalarKind.Number,
                    Number = entityBits,
                    RawText = entityBits.ToString(CultureInfo.InvariantCulture),
                },
                Value = new RonCompound { Fields = { new RonField { Key = "components", Value = components } } },
            });
        }

        var scene = new RonCompound
        {
            Fields =
            {
                new RonField { Key = "resources", Value = new RonMap() },
                new RonField { Key = "entities", Value = entities },
            },
        };

        var text = new StringBuilder(RonText.Write(scene));
        if (sceneId is not null) text.Append(SceneIdDelimiter).Append(RonText.QuoteAndEscape(sceneId));
        if (imageSources.Entries.Count > 0) text.Append(ImageSourcesDelimiter).Append(RonText.Write(imageSources));
        return text.ToString();
    }

    /// <summary>RON テキストから 1 シーンへの復元</summary>
    /// <remarks>オブジェクトの Id は <paramref name="sceneName"/> とエンティティ順序から決まる安定値を採番する。
    /// シーンインスタンスの Id は付随セクションが運んでいればそれ、無ければ <paramref name="sceneName"/></remarks>
    public static HierarchyNode ReadScene(
        string text, ISchemaSource schemas, string sceneName = DefaultSceneName)
    {
        var roots = new List<HierarchyNode>();
        (string sceneText, string? sceneIdText, string? imageSourcesText) = SplitSections(text);
        string sceneId = ParseSceneId(sceneIdText) ?? sceneName;
        Dictionary<ulong, string> imageSources = ParseImageSources(imageSourcesText);

        var ordered = new List<(ulong Entity, HierarchyNode Node, ulong? Parent)>();
        var positionOf = new Dictionary<ulong, int>();

        if (!string.IsNullOrWhiteSpace(sceneText)
            && RonText.Parse(sceneText) is RonCompound scene
            && FindField(scene, "entities") is RonMap entities)
        {
            int index = 0;
            foreach (RonMapEntry entry in entities.Entries)
            {
                ulong key = entry.Key is RonScalar { Kind: RonScalarKind.Number } number
                    ? (ulong)number.Number
                    : 0;
                string? guid = imageSources.GetValueOrDefault(key);

                if (BuildNodeFromEntity(entry.Value, sceneName, index, guid, schemas) is { } built)
                {
                    positionOf.TryAdd(key, ordered.Count);
                    ordered.Add((key, built.Node, built.Parent));
                }

                index++;
            }
        }

        // 親子は Parent が指すエンティティ番号で組み直す。エディタは常に親を先に書く（CollectFlat が
        // 深さ優先）ので、後ろを指す Parent は壊れた入力＝ルート扱いにして輪を作らない。
        var nodeOf = new Dictionary<ulong, HierarchyNode>();
        foreach ((ulong entity, HierarchyNode node, _) in ordered) nodeOf.TryAdd(entity, node);

        for (int i = 0; i < ordered.Count; i++)
        {
            (_, HierarchyNode node, ulong? parent) = ordered[i];
            if (parent is { } bits
                && positionOf.TryGetValue(bits, out int parentAt)
                && parentAt < i
                && nodeOf.TryGetValue(bits, out HierarchyNode? parentNode))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        return new HierarchyNode(sceneName, sceneName, children: roots, components: null, sceneId: sceneId);
    }

    /// <summary>本体 RON と付随セクションへの切り分け</summary>
    /// <remarks>セクションは本体の後ろへこの順で並ぶ</remarks>
    private static (string SceneText, string? SceneIdText, string? ImageSourcesText) SplitSections(string text)
    {
        int sceneIdAt = text.IndexOf(SceneIdDelimiter, StringComparison.Ordinal);
        int imagesAt = text.IndexOf(ImageSourcesDelimiter, StringComparison.Ordinal);
        int bodyEnd = sceneIdAt < 0 ? (imagesAt < 0 ? text.Length : imagesAt) : sceneIdAt;
        int sceneIdEnd = imagesAt < 0 ? text.Length : imagesAt;

        return (
            text[..bodyEnd],
            sceneIdAt < 0 ? null : text[(sceneIdAt + SceneIdDelimiter.Length)..sceneIdEnd],
            imagesAt < 0 ? null : text[(imagesAt + ImageSourcesDelimiter.Length)..]);
    }

    private static string? ParseSceneId(string? sceneIdText) =>
        !string.IsNullOrWhiteSpace(sceneIdText)
            && RonText.Parse(sceneIdText) is RonScalar { Kind: RonScalarKind.String, Text.Length: > 0 } id
                ? id.Text
                : null;

    private static Dictionary<ulong, string> ParseImageSources(string? imageSourcesText)
    {
        var result = new Dictionary<ulong, string>();
        if (string.IsNullOrWhiteSpace(imageSourcesText) || RonText.Parse(imageSourcesText) is not RonMap map)
            return result;

        foreach (RonMapEntry entry in map.Entries)
        {
            if (entry.Key is RonScalar { Kind: RonScalarKind.Number } key
                && entry.Value is RonScalar { Kind: RonScalarKind.String } value)
            {
                result[(ulong)key.Number] = value.Text;
            }
        }

        return result;
    }

    /// <summary>ランタイムへ渡すバイト列への書き出し</summary>
    /// <remarks>このサンプルが扱うのは 1 シーンまで。ロード無しは空バイト列（＝エンティティ 0 個のシーンとは別物）</remarks>
    public static byte[] WriteScenes(IReadOnlyList<HierarchyNode> roots) =>
        roots.Count == 0
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(WriteScene(roots[0], roots[0].SceneId));

    /// <summary>ランタイムが返したバイト列からの復元</summary>
    public static IReadOnlyList<HierarchyNode> ReadScenes(byte[] bytes, ISchemaSource schemas)
    {
        string text = Encoding.UTF8.GetString(bytes);
        return string.IsNullOrWhiteSpace(text)
            ? Array.Empty<HierarchyNode>()
            : new[] { ReadScene(text, schemas) };
    }

    /// <summary>エンティティをヒエラルキーノードに変換し、オブジェクトの属性とコンポーネントを分ける</summary>
    private static (HierarchyNode Node, ulong? Parent)? BuildNodeFromEntity(
        RonValue entityValue, string pathPrefix, int index, string? imageGuid, ISchemaSource schemas)
    {
        if (entityValue is not RonCompound entity) return null;
        if (FindField(entity, "components") is not RonMap components) return null;

        string name = $"Entity{index}";
        // Id を運んでいないシーン（手書きの .bscene）だけ並び位置から採る。
        string objectId = pathPrefix + "/" + index;
        bool active = true;
        ulong? parent = null;
        var attachables = new List<AuthoringObject>();

        foreach (RonMapEntry entry in components.Entries)
        {
            if (entry.Key is not RonScalar { Kind: RonScalarKind.String } key) continue;

            switch (key.Text)
            {
                case ObjectIdComponentTypeName:
                    objectId = ExtractObjectId(entry.Value) ?? objectId;
                    continue;
                case NameComponentTypeName:
                    name = ExtractName(entry.Value) ?? name;
                    continue;
                case VisibilityComponentTypeName:
                    active = ExtractVisibility(entry.Value) ?? active;
                    continue;
                case ParentComponentTypeName:
                    parent = ExtractParent(entry.Value) ?? parent;
                    continue;
            }

            attachables.Add(BevyComponent.FromRon(
                entry.Value, schemas.Get(key.Text), imageGuid));
        }

        var node = new HierarchyNode(
            objectId, name, children: null, components: attachables, active: active);
        return (node, parent);
    }

    /// <summary>オブジェクト識別（<c>ObjectId</c> は文字列 1 個の tuple struct）</summary>
    private static string? ExtractObjectId(RonValue objectIdValue) =>
        objectIdValue is RonCompound { Fields: [{ Value: RonScalar { Kind: RonScalarKind.String, Text.Length: > 0 } id }] }
            ? id.Text
            : null;

    /// <summary>アクティブか（<c>Hidden</c> は非アクティブ、<c>Inherited</c>/<c>Visible</c> は表示）</summary>
    private static bool? ExtractVisibility(RonValue visibilityValue) =>
        visibilityValue is RonScalar { Kind: RonScalarKind.Ident } variant ? variant.Text != HiddenVariant : null;

    /// <summary>親のエンティティ番号（<c>Parent</c> は番号 1 個の tuple struct）</summary>
    private static ulong? ExtractParent(RonValue parentValue) =>
        parentValue is RonCompound { Fields: [{ Value: RonScalar { Kind: RonScalarKind.Number } entity }] }
            ? (ulong)entity.Number
            : null;

    private static string? ExtractName(RonValue nameComponentValue) =>
        nameComponentValue is RonCompound compound
            ? compound.Fields.FirstOrDefault(f => f.Key == "name")?.Value is RonScalar { Kind: RonScalarKind.String } s
                ? s.Text
                : null
            : null;

    private static RonValue? FindField(RonCompound compound, string key) =>
        compound.Fields.FirstOrDefault(f => f.Key == key)?.Value;

    private static RonCompound BuildNameValue(string name) => new()
    {
        Fields =
        {
            new RonField { Key = "hash", Value = new RonScalar { Kind = RonScalarKind.Number, Number = 0, RawText = "0" } },
            new RonField { Key = "name", Value = new RonScalar { Kind = RonScalarKind.String, Text = name } },
        },
    };

    /// <summary>親子関係を保持して、ツリーを深さ優先順に並べる。</summary>
    private static void CollectFlat(
        IEnumerable<HierarchyNode> nodes,
        HierarchyNode? parent,
        List<HierarchyNode> into,
        Dictionary<HierarchyNode, HierarchyNode> parentOf)
    {
        foreach (HierarchyNode node in nodes)
        {
            into.Add(node);
            if (parent is not null) parentOf[node] = parent;
            CollectFlat(node.Children, node, into, parentOf);
        }
    }

    /// <summary>並び位置 <paramref name="index"/> のエンティティ番号（世代 1 の packed id）</summary>
    private static ulong EntityBits(int index) => (1UL << 32) | (uint)index;

    private static RonCompound BuildParentValue(ulong entityBits) =>
        BuildTupleValue(new RonScalar
        {
            Kind = RonScalarKind.Number,
            Number = entityBits,
            RawText = entityBits.ToString(CultureInfo.InvariantCulture),
        });

    /// <summary>値 1 個の tuple struct（<c>("x")</c>）</summary>
    private static RonCompound BuildTupleValue(RonValue value) =>
        new() { Fields = { new RonField { Value = value } } };
}
