namespace EmptyEngine.Editor.Authoring;

/// <summary>型名からスキーマと既定値を引く口</summary>
public interface ISchemaSource
{
    /// <summary>型名に対応するスキーマ</summary>
    /// <returns>カタログにない型は <c>null</c></returns>
    ObjectSchema? Find(string typeName);

    /// <summary>型名に対応する既定値の実体</summary>
    /// <returns>カタログにない型は <c>null</c></returns>
    AuthoringObject? CreateDefault(string typeName);
}

/// <summary><see cref="ISchemaSource"/> の引き当ての補助</summary>
public static class SchemaSourceExtensions
{
    /// <summary>型名に対応するスキーマ</summary>
    /// <exception cref="InvalidDataException">カタログにその型が無い</exception>
    public static ObjectSchema Get(this ISchemaSource source, string typeName) => source.Find(typeName)
        ?? throw new InvalidDataException($"Authoring schema '{typeName}' is not registered.");
}
