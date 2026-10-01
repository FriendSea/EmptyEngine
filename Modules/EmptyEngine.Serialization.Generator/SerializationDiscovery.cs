using Microsoft.CodeAnalysis;

namespace EmptyEngine.Serialization.Generator;

/// <summary>シリアライズの対象になる型の探索</summary>
internal static class SerializationDiscovery
{
    public const string CoreAssemblyName = "EmptyEngine.Core";
    public const string AttachableMetadataName = "EmptyEngine.ObjectModel.IAttachable";
    public const string AssetMetadataName = "EmptyEngine.ObjectModel.AssetAttribute";

    /// <summary>authoring の根＝コンポーネント（<c>IAttachable</c>）とアセット（<c>[Asset]</c>）</summary>
    /// <remarks>順序はワイヤ名の序数順。カタログの「Add Component」メニュー順もこれに従う</remarks>
    /// <param name="attachables">根のうちコンポーネントであるもの</param>
    /// <returns>語彙の型（<c>IAttachable</c> / <c>AssetAttribute</c>）が引けなければ空</returns>
    public static List<INamedTypeSymbol> Roots(Compilation compilation, out HashSet<INamedTypeSymbol> attachables)
    {
        attachables = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        if (compilation.GetTypeByMetadataName(AttachableMetadataName) is not { } attachable
            || compilation.GetTypeByMetadataName(AssetMetadataName) is not { } assetAttribute)
        {
            return new List<INamedTypeSymbol>();
        }

        var roots = new List<INamedTypeSymbol>();
        foreach (INamedTypeSymbol type in Universe(compilation))
        {
            bool isAttachable = Implements(type, attachable);
            if (isAttachable)
                attachables.Add(type);
            if (isAttachable || HasAttribute(type, assetAttribute))
                roots.Add(type);
        }

        roots.Sort((a, b) => string.CompareOrdinal(ModelBuilder.WireNameOf(a), ModelBuilder.WireNameOf(b)));
        return roots;
    }

    /// <summary>走査対象＝自分のアセンブリと <c>EmptyEngine.Core</c> を参照しているアセンブリ</summary>
    public static IEnumerable<INamedTypeSymbol> Universe(Compilation compilation)
    {
        var assemblies = new List<IAssemblySymbol> { compilation.Assembly };
        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (assembly.Name == CoreAssemblyName
                || assembly.Name.StartsWith("EmptyEngine.", StringComparison.Ordinal)
                || assembly.Modules.Any(m => m.ReferencedAssemblies.Any(r => r.Name == CoreAssemblyName)))
            {
                assemblies.Add(assembly);
            }
        }

        foreach (IAssemblySymbol assembly in assemblies)
        foreach (INamedTypeSymbol type in Types(assembly.GlobalNamespace))
        {
            if (IsCandidate(type))
                yield return type;
        }
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol scope)
    {
        foreach (ISymbol member in scope.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol ns:
                    foreach (INamedTypeSymbol nested in Types(ns))
                        yield return nested;
                    break;
                case INamedTypeSymbol type:
                    yield return type;
                    foreach (INamedTypeSymbol nested in Types(type))
                        yield return nested;
                    break;
            }
        }
    }

    /// <summary>実体を持ちうる具象型か</summary>
    public static bool IsCandidate(INamedTypeSymbol type)
        => type.TypeKind is TypeKind.Class or TypeKind.Struct
           && !type.IsAbstract
           && !type.IsStatic
           && type.TypeParameters.Length == 0
           && !type.Name.Contains("<")
           && type.ContainingType is null or { TypeParameters.Length: 0 };

    public static bool Implements(INamedTypeSymbol type, INamedTypeSymbol contract)
        => type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, contract));

    public static bool HasAttribute(INamedTypeSymbol type, INamedTypeSymbol attribute)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            if (t.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute)))
                return true;
        }

        return false;
    }
}
