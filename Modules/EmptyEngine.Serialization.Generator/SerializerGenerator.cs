using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace EmptyEngine.Serialization.Generator;

/// <summary>blob に載りうる型ごとの復元手順の生成</summary>
[Generator(LanguageNames.CSharp)]
public sealed class SerializerGenerator : IIncrementalGenerator
{
    private const string RegistryMetadataName = "EmptyEngine.Serialization.TypeSerializers";

    /// <summary>生成テキストの段の名前（増分が効いているかをテストが見る）</summary>
    internal const string SourceStep = "serializerSource";

    /// <summary>診断の段の名前</summary>
    internal const string DiagnosticsStep = "serializerDiagnostics";

    private static readonly DiagnosticDescriptor ComponentSkipped = new(
        "EEMPS001", "Cannot generate a deserializer for the component",
        "Cannot generate a deserializer for '{0}' ({1}). This type is unavailable in hosts that use generated serializers.",
        "EmptyEngine.Serialization.Generator", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor TypeSkipped = new(
        "EEMPS002", "Cannot generate a deserializer for the type",
        "Cannot generate a deserializer for '{0}' ({1}). This type is unavailable in hosts that use generated serializers.",
        "EmptyEngine.Serialization.Generator", DiagnosticSeverity.Info, isEnabledByDefault: true);

    /// <summary>コンパイル対象に応じたシリアライザの生成と診断を登録する</summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<GeneratorOutput> output =
            context.CompilationProvider.Select(static (compilation, _) => Build(compilation));

        context.RegisterSourceOutput(
            output.Select(static (o, _) => o.Source).WithTrackingName(SourceStep),
            static (spc, source) =>
            {
                if (source.Length != 0)
                    spc.AddSource("EmptyEngineSerializers.g.cs", SourceText.From(source, Encoding.UTF8));
            });

        context.RegisterSourceOutput(
            output.Select(static (o, _) => o.Diagnostics).WithTrackingName(DiagnosticsStep),
            static (spc, diagnostics) =>
            {
                foreach (DiagnosticInfo info in diagnostics)
                    spc.ReportDiagnostic(info.ToDiagnostic());
            });
    }

    private static GeneratorOutput Build(Compilation compilation)
    {
        var empty = new GeneratorOutput(string.Empty, new EquatableArray<DiagnosticInfo>(Array.Empty<DiagnosticInfo>()));
        if (compilation.GetTypeByMetadataName(RegistryMetadataName) is null)
            return empty;

        List<INamedTypeSymbol> roots =
            SerializationDiscovery.Roots(compilation, out HashSet<INamedTypeSymbol> attachables);
        if (roots.Count == 0)
            return empty;

        var builder = new ModelBuilder(compilation);
        var queue = new Queue<INamedTypeSymbol>(roots);
        var processed = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var plans = new List<TypePlan>();

        while (queue.Count > 0)
        {
            INamedTypeSymbol type = queue.Dequeue();
            if (!processed.Add(type))
                continue;

            if (builder.BuildType(type, attachables.Contains(type)) is { } plan)
                plans.Add(plan);
        }

        var diagnostics = new List<DiagnosticInfo>();
        foreach (KeyValuePair<ITypeSymbol, string> skipped in builder.Skipped)
        {
            bool isComponent = skipped.Key is INamedTypeSymbol named && attachables.Contains(named);
            diagnostics.Add(new DiagnosticInfo(
                isComponent ? ComponentSkipped : TypeSkipped,
                skipped.Key.Locations.FirstOrDefault(l => l.IsInSource),
                skipped.Key.ToDisplayString(),
                skipped.Value));
        }

        plans.Sort((a, b) => System.StringComparer.Ordinal.Compare(a.WireName, b.WireName));

        return new GeneratorOutput(
            new SerializerEmitter().Emit(plans, builder.Plans),
            new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
    }

}
