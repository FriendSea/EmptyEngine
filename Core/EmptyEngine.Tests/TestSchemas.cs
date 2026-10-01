using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.Tests;

public static class TestSchemas
{
    public static FieldTypeInfo Scalar(string name) => new(name, name switch
    {
        "System.Single" => FieldKind.Float32, "System.Double" => FieldKind.Float64,
        "System.Boolean" => FieldKind.Bool, "System.String" => FieldKind.String,
        "System.Byte" or "System.UInt16" or "System.UInt32" or "System.UInt64" => FieldKind.UInt,
        "System.SByte" or "System.Int16" or "System.Int32" or "System.Int64" => FieldKind.Int,
        _ => FieldKind.Map,
    });
    public static ObjectSchema Object(string name, params (string Name, FieldTypeInfo Type)[] fields) => new()
    {
        TypeName = name, DisplayName = name,
        Fields = fields.ToDictionary(field => field.Name, field => field.Type),
    };
    public static FieldTypeInfo Floats(params string[] names) => new("Test.Floats", FieldKind.Map)
    {
        Members = names.ToDictionary(name => name, _ => Scalar("System.Single")),
    };

    /// <summary>RGBA チャンネルを名乗る float の組（カタログの <c>x-color</c> つきの型に相当）</summary>
    public static FieldTypeInfo Color(params string[] channels) => new("Test.Color", FieldKind.Map)
    {
        Members = channels.ToDictionary(name => name, _ => Scalar("System.Single")),
        ColorChannels = channels,
    };
}
