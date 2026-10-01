using System.Text.Json;
using System.Text.Json.Nodes;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.SceneSource.Editor;

/// <summary>プレハブバリアントソースの JSON 表現と適用／差分抽出</summary>
internal static class PrefabVariantCodec
{
    private const int ObjectStateSlot = -1;
    private const string ActiveField = "Active";
    private const string NameField = "Name";

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>1 フィールドの上書き</summary>
    public sealed record FieldOverride(string ObjectId, int ComponentIndex, string TypeName, string Field, JsonNode? Value);

    public sealed record ComponentAddition(string ObjectId, string TypeName, JsonNode? Data);

    /// <summary>A child object added below an object in the original prefab.</summary>
    public sealed record ChildAddition(string ParentObjectId, HierarchyNode Child);

    /// <summary>バリアントソースの中身</summary>
    public sealed record VariantData(
        string Original,
        IReadOnlyList<FieldOverride> Overrides,
        IReadOnlyList<ComponentAddition>? AddedComponents = null,
        IReadOnlyList<ChildAddition>? AddedChildren = null);

    public static string ToJson(VariantData variant)
    {
        var root = new JsonObject
        {
            ["Original"] = variant.Original,
            ["Overrides"] = WriteOverrides(variant.Overrides),
            ["AddedComponents"] = WriteAddedComponents(variant.AddedComponents ?? []),
            ["AddedChildren"] = WriteAddedChildren(variant.AddedChildren ?? []),
        };
        return root.ToJsonString(PrettyOptions);
    }

    /// <summary>差分列の JSON 配列への書き出し</summary>
    public static JsonArray WriteOverrides(IReadOnlyList<FieldOverride> overrides)
    {
        var array = new JsonArray();
        foreach (FieldOverride o in overrides)
        {
            array.Add(new JsonObject
            {
                ["Object"] = o.ObjectId,
                ["ComponentIndex"] = o.ComponentIndex,
                ["TypeName"] = o.TypeName,
                ["Field"] = o.Field,
                ["Value"] = o.Value?.DeepClone(),
            });
        }
        return array;
    }

    /// <summary>JSON 配列からの差分列の読み取り</summary>
    public static List<FieldOverride> ReadOverrides(JsonNode? node)
    {
        var overrides = new List<FieldOverride>();
        if (node is not JsonArray array)
            return overrides;

        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject o) continue;
            overrides.Add(new FieldOverride(
                ObjectId: o["Object"]?.GetValue<string>() ?? string.Empty,
                ComponentIndex: o["ComponentIndex"]?.GetValue<int>() ?? 0,
                TypeName: o["TypeName"]?.GetValue<string>() ?? string.Empty,
                Field: o["Field"]?.GetValue<string>() ?? string.Empty,
                Value: o["Value"]?.DeepClone()));
        }

        return overrides;
    }

    public static JsonArray WriteAddedComponents(IReadOnlyList<ComponentAddition> additions)
    {
        var array = new JsonArray();
        foreach (ComponentAddition addition in additions)
        {
            array.Add(new JsonObject
            {
                ["Object"] = addition.ObjectId,
                ["TypeName"] = addition.TypeName,
                ["Data"] = addition.Data?.DeepClone(),
            });
        }
        return array;
    }

    public static List<ComponentAddition> ReadAddedComponents(JsonNode? node)
    {
        var additions = new List<ComponentAddition>();
        if (node is not JsonArray array)
            return additions;

        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject o) continue;
            additions.Add(new ComponentAddition(
                ObjectId: o["Object"]?.GetValue<string>() ?? string.Empty,
                TypeName: o["TypeName"]?.GetValue<string>() ?? string.Empty,
                Data: o["Data"]?.DeepClone()));
        }

        return additions;
    }

    public static JsonArray WriteAddedChildren(IReadOnlyList<ChildAddition> additions)
    {
        var array = new JsonArray();
        foreach (ChildAddition addition in additions)
        {
            array.Add(new JsonObject
            {
                ["Parent"] = addition.ParentObjectId,
                ["Child"] = JsonNode.Parse(SceneJsonCodec.ToJson(addition.Child)),
            });
        }
        return array;
    }

    public static List<ChildAddition> ReadAddedChildren(JsonNode? node, ISchemaSource schemas)
    {
        var additions = new List<ChildAddition>();
        if (node is not JsonArray array)
            return additions;

        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject o || o["Child"] is not { } childNode)
                continue;

            additions.Add(new ChildAddition(
                ParentObjectId: o["Parent"]?.GetValue<string>() ?? string.Empty,
                Child: SceneJsonCodec.FromJson(childNode.ToJsonString(), schemas)));
        }

        return additions;
    }

    public static VariantData FromJson(string json, ISchemaSource schemas)
    {
        JsonNode? root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        if (root is not JsonObject obj)
            throw new FormatException("Variant source must be a JSON object.");

        string original = obj["Original"]?.GetValue<string>() ?? string.Empty;
        return new VariantData(
            original,
            ReadOverrides(obj["Overrides"]),
            ReadAddedComponents(obj["AddedComponents"]),
            ReadAddedChildren(obj["AddedChildren"], schemas));
    }

    /// <summary>オリジナルの木へ差分を焼いた完全な木</summary>
    public static HierarchyNode Merge(
        HierarchyNode original,
        ISchemaSource schemas,
        IReadOnlyList<FieldOverride> overrides,
        IReadOnlyList<ComponentAddition>? addedComponents = null,
        IReadOnlyList<ChildAddition>? addedChildren = null)
    {
        var bySlot = new Dictionary<(string, int), Dictionary<string, JsonNode?>>();
        foreach (FieldOverride o in overrides)
        {
            var key = (o.ObjectId, o.ComponentIndex);
            if (!bySlot.TryGetValue(key, out Dictionary<string, JsonNode?>? fields))
                bySlot[key] = fields = new Dictionary<string, JsonNode?>();
            fields[o.Field] = o.Value;
        }

        var additionsByObject = (addedComponents ?? [])
            .GroupBy(x => x.ObjectId, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<ComponentAddition>)x.ToList(),
                StringComparer.Ordinal);

        var childrenByParent = (addedChildren ?? [])
            .GroupBy(x => x.ParentObjectId, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<ChildAddition>)x.ToList(),
                StringComparer.Ordinal);

        return MergeNode(original, schemas, bySlot, additionsByObject, childrenByParent);
    }

    private static HierarchyNode MergeNode(
        HierarchyNode node,
        ISchemaSource schemas,
        Dictionary<(string, int), Dictionary<string, JsonNode?>> bySlot,
        IReadOnlyDictionary<string, IReadOnlyList<ComponentAddition>> additionsByObject,
        IReadOnlyDictionary<string, IReadOnlyList<ChildAddition>> childrenByParent)
    {
        bool active = node.Active;
        string name = node.Name;
        if (bySlot.TryGetValue((node.ObjectId, ObjectStateSlot), out Dictionary<string, JsonNode?>? objectState)
            && objectState.TryGetValue(ActiveField, out JsonNode? activeNode)
            && activeNode is JsonValue activeValue
            && activeValue.TryGetValue(out bool overriddenActive))
        {
            active = overriddenActive;
        }
        if (objectState is not null
            && objectState.TryGetValue(NameField, out JsonNode? nameNode)
            && nameNode is JsonValue nameValue
            && nameValue.TryGetValue(out string? overriddenName))
        {
            name = overriddenName ?? string.Empty;
        }

        var components = new List<AuthoringObject>(node.Components.Count);
        for (int i = 0; i < node.Components.Count; i++)
        {
            AuthoringObject component = node.Components[i];
            components.Add(bySlot.TryGetValue((node.ObjectId, i), out Dictionary<string, JsonNode?>? fields)
                ? ApplyOverrides(component, fields)
                : component);
        }
        if (additionsByObject.TryGetValue(node.ObjectId, out IReadOnlyList<ComponentAddition>? additions))
        {
            foreach (ComponentAddition addition in additions)
            {
                ObjectSchema schema = schemas.Get(addition.TypeName);
                components.Add(new AuthoringObject(schema, FieldJson.DecodeNode(addition.Data, schema.Root)));
            }
        }

        var children = new List<HierarchyNode>(node.Children.Count);
        foreach (HierarchyNode child in node.Children)
            children.Add(MergeNode(child, schemas, bySlot, additionsByObject, childrenByParent));
        if (childrenByParent.TryGetValue(node.ObjectId, out IReadOnlyList<ChildAddition>? childAdditions))
        {
            foreach (ChildAddition addition in childAdditions)
                children.Add(addition.Child);
        }

        return new HierarchyNode(node.ObjectId, name, children, components, active: active);
    }

    private static AuthoringObject ApplyOverrides(AuthoringObject component, Dictionary<string, JsonNode?> fields)
    {
        if (FieldJson.Encode(component.Data, component.Schema.Root) is not JsonObject json)
            return component;

        foreach ((string field, JsonNode? value) in fields)
            json[field] = value?.DeepClone();

        return new AuthoringObject(component.Schema, FieldJson.DecodeNode(json, component.Schema.Root));
    }

    /// <summary>編集後の木とオリジナルの突き合わせによる差分の抽出</summary>
    public static IReadOnlyList<FieldOverride> Diff(HierarchyNode original, HierarchyNode variant)
    {
        var variantById = new Dictionary<string, HierarchyNode>();
        Index(variant, variantById);

        var overrides = new List<FieldOverride>();
        DiffNode(original, variantById, overrides);
        return overrides;
    }

    private static void Index(HierarchyNode node, Dictionary<string, HierarchyNode> map)
    {
        map.TryAdd(node.ObjectId, node);
        foreach (HierarchyNode child in node.Children)
            Index(child, map);
    }

    private static void DiffNode(HierarchyNode original, Dictionary<string, HierarchyNode> variantById, List<FieldOverride> overrides)
    {
        if (variantById.TryGetValue(original.ObjectId, out HierarchyNode? variant))
        {
            if (original.Active != variant.Active)
            {
                overrides.Add(new FieldOverride(
                    original.ObjectId,
                    ObjectStateSlot,
                    typeof(HierarchyNode).FullName!,
                    ActiveField,
                    JsonValue.Create(variant.Active)));
            }
            if (!string.Equals(original.Name, variant.Name, StringComparison.Ordinal))
            {
                overrides.Add(new FieldOverride(
                    original.ObjectId,
                    ObjectStateSlot,
                    typeof(HierarchyNode).FullName!,
                    NameField,
                    JsonValue.Create(variant.Name)));
            }

            int count = Math.Min(original.Components.Count, variant.Components.Count);
            for (int i = 0; i < count; i++)
                DiffComponent(original.ObjectId, i, original.Components[i], variant.Components[i], overrides);
        }

        foreach (HierarchyNode child in original.Children)
            DiffNode(child, variantById, overrides);
    }

    public static IReadOnlyList<ComponentAddition> DiffAddedComponents(HierarchyNode original, HierarchyNode variant)
    {
        var variantById = new Dictionary<string, HierarchyNode>(StringComparer.Ordinal);
        Index(variant, variantById);

        var additions = new List<ComponentAddition>();
        DiffAddedComponentsNode(original, variantById, additions);
        return additions;
    }

    public static IReadOnlyList<ChildAddition> DiffAddedChildren(HierarchyNode original, HierarchyNode variant)
    {
        var originalIds = new HashSet<string>(StringComparer.Ordinal);
        IndexIds(original, originalIds);

        var additions = new List<ChildAddition>();
        DiffAddedChildrenNode(variant, originalIds, additions);
        return additions;
    }

    private static void IndexIds(HierarchyNode node, ISet<string> ids)
    {
        ids.Add(node.ObjectId);
        foreach (HierarchyNode child in node.Children)
            IndexIds(child, ids);
    }

    private static void DiffAddedChildrenNode(
        HierarchyNode parent,
        IReadOnlySet<string> originalIds,
        List<ChildAddition> additions)
    {
        foreach (HierarchyNode child in parent.Children)
        {
            if (!originalIds.Contains(child.ObjectId))
            {
                additions.Add(new ChildAddition(parent.ObjectId, child));
                continue;
            }

            DiffAddedChildrenNode(child, originalIds, additions);
        }
    }

    private static void DiffAddedComponentsNode(
        HierarchyNode original,
        IReadOnlyDictionary<string, HierarchyNode> variantById,
        List<ComponentAddition> additions)
    {
        if (variantById.TryGetValue(original.ObjectId, out HierarchyNode? variant))
        {
            for (int i = original.Components.Count; i < variant.Components.Count; i++)
            {
                AuthoringObject component = variant.Components[i];
                additions.Add(new ComponentAddition(
                    original.ObjectId,
                    component.TypeName,
                    FieldJson.Encode(component.Data, component.Schema.Root)));
            }
        }

        foreach (HierarchyNode child in original.Children)
            DiffAddedComponentsNode(child, variantById, additions);
    }

    private static void DiffComponent(string objectId, int index, AuthoringObject original, AuthoringObject variant, List<FieldOverride> overrides)
    {
        if (!string.Equals(original.TypeName, variant.TypeName, StringComparison.Ordinal))
            return;
        if (FieldJson.Encode(original.Data, original.Schema.Root) is not JsonObject baseJson ||
            FieldJson.Encode(variant.Data, variant.Schema.Root) is not JsonObject variantJson)
            return;

        string typeName = original.TypeName;
        foreach (KeyValuePair<string, JsonNode?> pair in baseJson)
        {
            JsonNode? variantValue = variantJson[pair.Key];
            if (!JsonNode.DeepEquals(pair.Value, variantValue))
                overrides.Add(new FieldOverride(objectId, index, typeName, pair.Key, variantValue?.DeepClone()));
        }
    }
}
