using System.Collections;
using System.Globalization;
using System.Reflection;
using EmptyEngine.Core;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.Editor.Assets;

/// <summary>.NET のインポータ向けの補助</summary>
public static class ImporterUtils
{
    private const BindingFlags Published = BindingFlags.Instance | BindingFlags.Public;

    /// <summary>CLR オブジェクトのスキーマ対象メンバを <see cref="AuthoringObject"/> に変換する。</summary>
    /// <exception cref="InvalidDataException">型カタログに対応する型がない。</exception>
    public static AuthoringObject FromClr(object value, ISchemaSource schemas)
    {
        ArgumentNullException.ThrowIfNull(value);
        Type type = value.GetType();
        ObjectSchema schema = schemas.Get(type.FullName ?? type.Name);
        return new AuthoringObject(schema, FromClr(value, schema.Root));
    }

    private static FieldValue FromClr(object? value, FieldTypeInfo type)
    {
        if (value is null) return FieldValue.Nil();

        var result = new FieldValue();
        switch (type.Kind)
        {
            case FieldKind.AssetReference:
            case FieldKind.ObjectReference:
                result.Text = PublicMembers(value)
                    .Select(member => member.Value as string)
                    .FirstOrDefault(text => text is not null) ?? string.Empty;
                break;
            case FieldKind.Map:
            case FieldKind.Union:
                foreach ((string name, object? member) in PublicMembers(value))
                {
                    if (result.Get(name) is not null) continue;
                    if (type.TryMember(name, out FieldTypeInfo? memberType))
                        result.Add(name, FromClr(member, memberType));
                }

                break;
            case FieldKind.Array:
                foreach (object? item in (IEnumerable)value) result.Items.Add(FromClr(item, type.Element!));
                break;
            case FieldKind.Bool: result.Bool = Convert.ToBoolean(value, CultureInfo.InvariantCulture); break;
            case FieldKind.Int:
            case FieldKind.Enum: result.Integer = Convert.ToInt64(value, CultureInfo.InvariantCulture); break;
            case FieldKind.UInt: result.Unsigned = Convert.ToUInt64(value, CultureInfo.InvariantCulture); break;
            case FieldKind.Float32:
            case FieldKind.Float64: result.Real = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case FieldKind.String: result.Text = value as string; break;
            case FieldKind.Binary: result.Binary = value as IAssetBinary; break;
            case FieldKind.Nil: return FieldValue.Nil();
            default: throw new InvalidDataException($"Unsupported schema kind '{type.Kind}' for '{type.TypeName}'.");
        }

        return result;
    }

    /// <summary>公開フィールドと、添字を取らない読める公開プロパティ</summary>
    /// <remarks>getter が投げたら、その旨を値にして先へ進む。読めない 1 つで残りを捨てない</remarks>
    private static IEnumerable<(string Name, object? Value)> PublicMembers(object owner)
    {
        Type type = owner.GetType();

        foreach (FieldInfo field in type.GetFields(Published))
            yield return (field.Name, Read(() => field.GetValue(owner)));

        foreach (PropertyInfo property in type.GetProperties(Published))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;

            yield return (property.Name, Read(() => property.GetValue(owner)));
        }
    }

    private static object? Read(Func<object?> get)
    {
        try { return get(); }
        catch (Exception ex) { return $"(error: {ex.Message})"; }
    }
}
