using EmptyEngine.Editor.ViewModels.Inspector;

namespace EmptyEngine.Editor.ViewModels.Utils;

/// <summary>編集欄 1 つをどのコントロールで描くかの区分</summary>
public static class EditorFieldKinds
{
    public const string Bool = "bool";
    public const string Number = "number";
    public const string Enum = "enum";
    public const string Color = "color";
    public const string AssetReference = "asset-reference";
    public const string ObjectReference = "object-reference";
    public const string Array = "array";
    public const string Group = "group";
    public const string Binary = "binary";
    public const string Text = "text";

    /// <summary><paramref name="field"/> をどのコントロールで描くか</summary>
    public static string Of(FieldViewModel field)
    {
        if (field.IsBool) return Bool;
        if (field.IsNumeric) return Number;
        if (field.IsEnum) return Enum;
        if (field.IsColor) return Color;
        if (field.IsAssetReference) return AssetReference;
        if (field.IsObjectReference) return ObjectReference;
        if (field.IsArray) return Array;
        if (field.IsComposite) return Group;
        if (field.IsBinary) return Binary;
        return Text;
    }
}
