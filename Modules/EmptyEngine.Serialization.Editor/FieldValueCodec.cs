using System.Buffers;
using EmptyEngine.Editor.Authoring;
using MessagePack;

namespace EmptyEngine.Serialization.Editor;

/// <summary>MessagePack conversion using the declared schema.</summary>
public static class FieldValueCodec
{
    public static FieldValue Decode(ref MessagePackReader reader, FieldTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (reader.TryReadNil()) return FieldValue.Nil();
        var value = new FieldValue();
        switch (type.Kind)
        {
            case FieldKind.Map:
            case FieldKind.Union:
                int fields = reader.ReadMapHeader();
                for (int i = 0; i < fields; i++)
                {
                    string key = reader.ReadString() ?? throw new MessagePackSerializationException("Null field name.");
                    if (type.TryMember(key, out FieldTypeInfo? member) && member.Kind != FieldKind.Binary)
                        value.Add(key, Decode(ref reader, member));
                    else reader.Skip();
                }
                if (type.Kind == FieldKind.Union && value.Entries.Count != 1)
                    throw new MessagePackSerializationException($"Union '{type.TypeName}' requires one variant.");
                break;
            case FieldKind.Array:
                int count = reader.ReadArrayHeader();
                for (int i = 0; i < count; i++) value.Items.Add(Decode(ref reader, type.Element!));
                break;
            case FieldKind.Bool: value.Bool = reader.ReadBoolean(); break;
            case FieldKind.Int:
            case FieldKind.Enum: value.Integer = reader.ReadInt64(); break;
            case FieldKind.UInt: value.Unsigned = reader.ReadUInt64(); break;
            case FieldKind.Float32: value.Real = reader.ReadSingle(); break;
            case FieldKind.Float64: value.Real = reader.ReadDouble(); break;
            case FieldKind.String:
            case FieldKind.AssetReference:
            case FieldKind.ObjectReference: value.Text = reader.ReadString(); break;
            default: throw new MessagePackSerializationException($"Expected nil for '{type.TypeName}'.");
        }
        ValidateInteger(value, type);
        return value;
    }

    public static void Encode(ref MessagePackWriter writer, FieldValue value, FieldTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (value.IsNull) { writer.WriteNil(); return; }
        ValidateInteger(value, type);
        switch (type.Kind)
        {
            case FieldKind.Map:
            case FieldKind.Union:
                if (type.Kind == FieldKind.Union && value.Entries.Count != 1)
                    throw new MessagePackSerializationException($"Union '{type.TypeName}' requires one variant.");
                int written = 0;
                foreach (FieldEntry entry in value.Entries)
                    if (type.Member(entry.Key).Kind != FieldKind.Binary) written++;
                writer.WriteMapHeader(written);
                foreach (FieldEntry entry in value.Entries)
                {
                    FieldTypeInfo member = type.Member(entry.Key);
                    if (member.Kind == FieldKind.Binary) continue;
                    writer.Write(entry.Key);
                    Encode(ref writer, entry.Value, member);
                }
                break;
            case FieldKind.Array:
                writer.WriteArrayHeader(value.Items.Count);
                foreach (FieldValue item in value.Items) Encode(ref writer, item, type.Element!);
                break;
            case FieldKind.Bool: writer.Write(value.Bool); break;
            // 生成シリアライザと同じバイト列にするため、整数は最短形式で書く。
            case FieldKind.Int:
            case FieldKind.Enum: writer.Write(value.Integer); break;
            case FieldKind.UInt: writer.Write(value.Unsigned); break;
            case FieldKind.Float32: writer.Write((float)value.Real); break;
            case FieldKind.Float64: writer.Write(value.Real); break;
            case FieldKind.String:
            case FieldKind.AssetReference:
            case FieldKind.ObjectReference: writer.Write(value.Text); break;
            case FieldKind.Nil: writer.WriteNil(); break;
            default: throw new MessagePackSerializationException($"Unsupported schema kind '{type.Kind}'.");
        }
    }

    public static FieldValue DecodeBytes(ReadOnlyMemory<byte> data, FieldTypeInfo type)
    {
        var reader = new MessagePackReader(data);
        FieldValue value = Decode(ref reader, type);
        if (!reader.End) throw new MessagePackSerializationException("Trailing field data.");
        return value;
    }

    public static byte[] EncodeBytes(FieldValue value, FieldTypeInfo type)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        Encode(ref writer, value, type);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void ValidateInteger(FieldValue value, FieldTypeInfo type)
    {
        bool valid = type.TypeName switch
        {
            "System.SByte" => value.Integer is >= sbyte.MinValue and <= sbyte.MaxValue,
            "System.Int16" => value.Integer is >= short.MinValue and <= short.MaxValue,
            "System.Int32" => value.Integer is >= int.MinValue and <= int.MaxValue,
            "System.Byte" => value.Unsigned <= byte.MaxValue,
            "System.UInt16" => value.Unsigned <= ushort.MaxValue,
            "System.UInt32" => value.Unsigned <= uint.MaxValue,
            _ => true,
        };
        if (!valid) throw new MessagePackSerializationException($"Value is outside the range of {type.TypeName}.");
    }
}
