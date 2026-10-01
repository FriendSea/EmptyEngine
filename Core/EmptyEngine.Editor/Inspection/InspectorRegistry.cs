using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Components.Inspector;

namespace EmptyEngine.Editor.Inspection;

/// <summary><see cref="InspectorForAttribute"/> の付いたコンポーネントの、オーサリング対象への割り当て</summary>
/// <remarks>型名の完全一致を優先し、該当しなければスキーマ上の派生関係から選ぶ。同じ優先度で複数の候補がある場合はエラーとし、候補がなければ既定のインスペクタを使う。</remarks>
public sealed class InspectorRegistry
{
    private readonly Dictionary<string, Type> _byTypeName;

    internal InspectorRegistry(IEnumerable<(Type Component, InspectorForAttribute[] Attributes)> annotated)
    {
        _byTypeName = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach ((Type component, InspectorForAttribute[] attributes) in annotated)
        {
            foreach (InspectorForAttribute attribute in attributes)
            {
                if (_byTypeName.TryGetValue(attribute.TypeName, out Type? taken) && taken != component)
                {
                    throw new InvalidOperationException(
                        $"Two inspectors claim '{attribute.TypeName}': '{taken.FullName}' and " +
                        $"'{component.FullName}'. Remove one [InspectorFor] or narrow it to a different type.");
                }

                _byTypeName[attribute.TypeName] = component;
            }
        }
    }

    /// <summary>この対象を描くコンポーネントの型</summary>
    public Type Resolve(ObjectSchema schema)
    {
        if (_byTypeName.TryGetValue(schema.TypeName, out Type? exact)) return exact;

        Type? found = null;
        string? foundTypeName = null;

        foreach ((string typeName, Type inspector) in _byTypeName)
        {
            if (!schema.IsAssignableTo(typeName)) continue;

            if (found is not null && found != inspector)
            {
                throw new InvalidOperationException(
                    $"'{schema.TypeName}' matches more than one inspector: '{found.FullName}' via " +
                    $"'{foundTypeName}' and '{inspector.FullName}' via '{typeName}'. " +
                    "Claim the concrete type from one of them so the choice is not left to registration order.");
            }

            found = inspector;
            foundTypeName = typeName;
        }

        return found ?? typeof(DefaultInspector);
    }
}
