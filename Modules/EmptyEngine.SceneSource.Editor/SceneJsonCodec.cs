using System.Text.Json;
using System.Text.Json.Nodes;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.SceneSource.Editor;

/// <summary>.scene ソースの JSON 表現と単一ルートの <see cref="HierarchyNode"/> の相互変換</summary>
internal static class SceneJsonCodec
{
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    public static string ToJson(HierarchyNode root) => ToJson(root, foldChild: null);

    /// <summary>子を畳めるツリーの JSON への書き出し</summary>
    public static string ToJson(HierarchyNode root, Func<HierarchyNode, JsonObject?>? foldChild)
        => ToJsonObject(root, foldChild).ToJsonString(PrettyOptions);

    private static JsonObject ToJsonObject(HierarchyNode node, Func<HierarchyNode, JsonObject?>? foldChild)
    {
        var components = new JsonArray();
        foreach (AuthoringObject component in node.Components)
        {
            components.Add(new JsonObject
            {
                ["TypeName"] = component.TypeName,
                ["Data"] = FieldJson.Encode(component.Data, component.Schema.Root),
            });
        }

        return new JsonObject
        {
            ["Id"] = node.ObjectId,
            ["Name"] = node.Name,
            ["Active"] = node.Active,
            ["Components"] = components,
            ["Children"] = ToJsonArray(node.Children, foldChild),
        };
    }

    private static JsonArray ToJsonArray(IReadOnlyList<HierarchyNode> nodes, Func<HierarchyNode, JsonObject?>? foldChild)
    {
        var array = new JsonArray();
        foreach (HierarchyNode node in nodes)
            array.Add(foldChild?.Invoke(node) ?? ToJsonObject(node, foldChild));
        return array;
    }

    public static HierarchyNode FromJson(string json, ISchemaSource schemas) =>
        FromJson(json, schemas, expandLeaf: null);

    /// <summary>葉を実体化できる JSON からのツリーの読み取り</summary>
    public static HierarchyNode FromJson(
        string json, ISchemaSource schemas, Func<NestedPrefabCodec.PrefabLeaf, HierarchyNode>? expandLeaf)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return ReadNode(doc.RootElement, schemas, expandLeaf);
    }

    private static List<HierarchyNode> ReadNodes(
        JsonElement parent,
        string propertyName,
        ISchemaSource schemas,
        Func<NestedPrefabCodec.PrefabLeaf, HierarchyNode>? expandLeaf)
    {
        var nodes = new List<HierarchyNode>();
        if (parent.TryGetProperty(propertyName, out JsonElement arrayEl) && arrayEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement o in arrayEl.EnumerateArray())
            {
                if (NestedPrefabCodec.IsLeaf(o))
                {
                    if (expandLeaf is null)
                        throw new FormatException("Scene contains a nested prefab leaf but no leaf resolver was provided.");
                    nodes.Add(expandLeaf(NestedPrefabCodec.ReadLeaf(o, schemas)));
                }
                else
                {
                    nodes.Add(ReadNode(o, schemas, expandLeaf));
                }
            }
        }
        return nodes;
    }

    private static HierarchyNode ReadNode(
        JsonElement o, ISchemaSource schemas, Func<NestedPrefabCodec.PrefabLeaf, HierarchyNode>? expandLeaf)
    {
        string id = o.TryGetProperty("Id", out JsonElement idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        string name = o.TryGetProperty("Name", out JsonElement nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
        bool active = !o.TryGetProperty("Active", out JsonElement activeEl) || activeEl.ValueKind != JsonValueKind.False;

        var components = new List<AuthoringObject>();
        if (o.TryGetProperty("Components", out JsonElement compsEl) && compsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in compsEl.EnumerateArray())
                components.Add(ReadComponent(c, schemas));
        }

        return new HierarchyNode(id, name, ReadNodes(o, "Children", schemas, expandLeaf), components, active: active);
    }

    private static AuthoringObject ReadComponent(JsonElement c, ISchemaSource schemas)
    {
        string typeName = c.TryGetProperty("TypeName", out JsonElement typeEl) ? typeEl.GetString() ?? string.Empty : string.Empty;

        ObjectSchema schema = schemas.Get(typeName);
        FieldValue? data = c.TryGetProperty("Data", out JsonElement dataEl) && dataEl.ValueKind != JsonValueKind.Null
            ? FieldJson.Decode(dataEl, schema.Root)
            : null;

        FieldValue defaults = schemas.CreateDefault(typeName)?.Data ?? new FieldValue();
        FieldValue merged = schema.Root.Overlay(defaults, data);

        return new AuthoringObject(schema, merged);
    }
}
