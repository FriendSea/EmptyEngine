using System.Globalization;
using EmptyEngine.Editor.Authoring;

namespace BevyRuntimeSample.Editor;

internal static class RonFieldCodec
{
    public static FieldValue Decode(RonValue node, FieldTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var value = new FieldValue();
        switch (type.Kind)
        {
            case FieldKind.Nil:
                if (node is not RonScalar { Kind: RonScalarKind.Ident, Text: "None" }) throw Mismatch(type);
                return FieldValue.Nil();
            case FieldKind.Union:
                if (node is RonScalar { Kind: RonScalarKind.Ident } unit)
                {
                    if (type.Member(unit.Text).Kind != FieldKind.Nil) throw Mismatch(type);
                    value.Add(unit.Text, FieldValue.Nil());
                }
                else if (node is RonCompound { Name: { } tag, Fields: [{ Key: null } payload] })
                    value.Add(tag, Decode(payload.Value, type.Member(tag)));
                else throw Mismatch(type);
                break;
            case FieldKind.Map:
                if (node is not RonCompound compound) throw Mismatch(type);
                foreach (RonField field in compound.Fields)
                {
                    string key = field.Key ?? throw Mismatch(type);
                    value.Add(key, Decode(field.Value, type.Member(key)));
                }
                break;
            case FieldKind.Array:
                if (node is not RonSeq seq) throw Mismatch(type);
                foreach (RonValue item in seq.Items) value.Items.Add(Decode(item, type.Element!));
                break;
            default:
                if (node is not RonScalar scalar) throw Mismatch(type);
                switch (type.Kind)
                {
                    case FieldKind.Bool when scalar.Kind == RonScalarKind.Bool: value.Bool = scalar.Bool; break;
                    case FieldKind.String when scalar.Kind == RonScalarKind.String: value.Text = scalar.Text; break;
                    case FieldKind.Enum when scalar.Kind == RonScalarKind.Ident && type.EnumMembers.Any(m => m.Name == scalar.Text):
                        value.Integer = type.EnumValueOf(scalar.Text); break;
                    case FieldKind.Int when scalar.Kind == RonScalarKind.Number:
                        value.Integer = long.Parse(scalar.RawText, CultureInfo.InvariantCulture); break;
                    case FieldKind.UInt when scalar.Kind == RonScalarKind.Number:
                        value.Unsigned = ulong.Parse(scalar.RawText, CultureInfo.InvariantCulture); break;
                    case FieldKind.Float32 when scalar.Kind == RonScalarKind.Number: value.Real = (float)scalar.Number; break;
                    case FieldKind.Float64 when scalar.Kind == RonScalarKind.Number: value.Real = scalar.Number; break;
                    default: throw Mismatch(type);
                }
                break;
        }
        return value;
    }

    public static RonValue Encode(FieldValue value, FieldTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (value.IsNull) return new RonScalar { Kind = RonScalarKind.Ident, Text = "None", RawText = "None" };
        if (type.Kind == FieldKind.Union)
        {
            if (value.Entries is not [var entry]) throw Mismatch(type);
            FieldTypeInfo variant = type.Member(entry.Key);
            return variant.Kind == FieldKind.Nil
                ? new RonScalar { Kind = RonScalarKind.Ident, Text = entry.Key, RawText = entry.Key }
                : new RonCompound { Name = entry.Key, Fields = { new RonField { Value = Encode(entry.Value, variant) } } };
        }
        if (type.Kind == FieldKind.Map)
        {
            var compound = new RonCompound();
            foreach (FieldEntry entry in value.Entries)
                compound.Fields.Add(new RonField { Key = entry.Key, Value = Encode(entry.Value, type.Member(entry.Key)) });
            return compound;
        }
        if (type.Kind == FieldKind.Array)
        {
            var seq = new RonSeq();
            foreach (FieldValue item in value.Items) seq.Items.Add(Encode(item, type.Element!));
            return seq;
        }
        var scalar = new RonScalar();
        switch (type.Kind)
        {
            case FieldKind.Bool: scalar.SetBool(value.Bool); break;
            case FieldKind.String: scalar.SetString(value.Text ?? string.Empty); break;
            case FieldKind.Enum: scalar.SetIdent(type.EnumNameOf(value.Integer)); break;
            case FieldKind.Int:
                scalar.Kind = RonScalarKind.Number; scalar.Number = value.Integer;
                scalar.RawText = value.Integer.ToString(CultureInfo.InvariantCulture); break;
            case FieldKind.UInt:
                scalar.Kind = RonScalarKind.Number; scalar.Number = value.Unsigned;
                scalar.RawText = value.Unsigned.ToString(CultureInfo.InvariantCulture); break;
            case FieldKind.Float32: scalar.SetNumber((float)value.Real); break;
            case FieldKind.Float64: scalar.SetNumber(value.Real); break;
            default: throw Mismatch(type);
        }
        return scalar;
    }

    private static InvalidDataException Mismatch(FieldTypeInfo type) => new($"RON value does not match '{type.TypeName}' ({type.Kind}).");
}
