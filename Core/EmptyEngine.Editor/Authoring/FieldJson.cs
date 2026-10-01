using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmptyEngine.Editor.Authoring;

/// <summary>スキーマに従って JSON とフィールド値を相互変換する。</summary>
public static class FieldJson
{
    public static FieldValue Decode(JsonElement value, FieldTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (value.ValueKind == JsonValueKind.Null) return FieldValue.Nil();
        var result = new FieldValue();
        switch (type.Kind)
        {
            case FieldKind.Map:
            case FieldKind.Union:
                foreach (JsonProperty property in value.EnumerateObject())
                    if (type.TryMember(property.Name, out FieldTypeInfo? member) && member.Kind != FieldKind.Binary)
                        result.Add(property.Name, Decode(property.Value, member));
                if (type.Kind == FieldKind.Union && result.Entries.Count != 1)
                    throw new InvalidDataException($"Union '{type.TypeName}' requires one variant.");
                break;
            case FieldKind.Array:
                foreach (JsonElement item in value.EnumerateArray()) result.Items.Add(Decode(item, type.Element!));
                break;
            case FieldKind.Bool: result.Bool = value.GetBoolean(); break;
            case FieldKind.Int:
            case FieldKind.Enum: result.Integer = ReadInt64(value); break;
            case FieldKind.UInt: result.Unsigned = ReadUInt64(value); break;
            case FieldKind.Float32: result.Real = value.GetSingle(); break;
            case FieldKind.Float64: result.Real = value.GetDouble(); break;
            case FieldKind.String:
            case FieldKind.AssetReference:
            case FieldKind.ObjectReference: result.Text = value.GetString(); break;
            default: throw new InvalidDataException($"Expected null for '{type.TypeName}'.");
        }
        return result;
    }

    /// <summary>小数点付きで保存された整数トークンの切り捨て読み</summary>
    private static long ReadInt64(JsonElement value) =>
        value.TryGetInt64(out long result) ? result : (long)value.GetDouble();

    private static ulong ReadUInt64(JsonElement value) =>
        value.TryGetUInt64(out ulong result) ? result : (ulong)value.GetDouble();

    public static FieldValue DecodeNode(JsonNode? node, FieldTypeInfo type) =>
        Decode(JsonSerializer.SerializeToElement(node), type);

    public static JsonNode? Encode(FieldValue value, FieldTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (value.IsNull) return null;
        switch (type.Kind)
        {
            case FieldKind.Map:
            case FieldKind.Union:
                if (type.Kind == FieldKind.Union && value.Entries.Count != 1)
                    throw new InvalidDataException($"Union '{type.TypeName}' requires one variant.");
                var obj = new JsonObject();
                foreach (FieldEntry entry in value.Entries)
                {
                    FieldTypeInfo member = type.Member(entry.Key);
                    if (member.Kind != FieldKind.Binary) obj.Add(entry.Key, Encode(entry.Value, member));
                }
                return obj;
            case FieldKind.Array:
                var array = new JsonArray();
                foreach (FieldValue item in value.Items) array.Add(Encode(item, type.Element!));
                return array;
            case FieldKind.Bool: return JsonValue.Create(value.Bool);
            case FieldKind.Int:
            case FieldKind.Enum: return JsonValue.Create(value.Integer);
            case FieldKind.UInt: return JsonValue.Create(value.Unsigned);
            case FieldKind.Float32: return JsonValue.Create((float)value.Real);
            case FieldKind.Float64: return JsonValue.Create(value.Real);
            case FieldKind.String:
            case FieldKind.AssetReference:
            case FieldKind.ObjectReference: return JsonValue.Create(value.Text);
            case FieldKind.Nil: return null;
            default: throw new InvalidDataException($"Unsupported schema kind '{type.Kind}'.");
        }
    }
}
