using EmptyEngine.Serialization.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>ゲームのアセンブリをシンボルとして開いたもの</summary>
internal static class SymbolWorld
{
    private static Compilation _compilation = null!;
    private static List<INamedTypeSymbol> _roots = new();
    private static HashSet<INamedTypeSymbol> _attachables = new(SymbolEqualityComparer.Default);

    /// <param name="gameDll">解析対象のゲームアセンブリ。</param>
    /// <param name="referencesFile">ゲームのコンパイルに使われた参照アセンブリの一覧。1 行に 1 つのパスを指定する。</param>
    public static void Open(string gameDll, string referencesFile)
    {
        var references = new List<MetadataReference>();
        foreach (string path in File.ReadAllLines(referencesFile))
        {
            if (path.Length > 0 && File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        PortableExecutableReference game = MetadataReference.CreateFromFile(gameDll);
        references.Add(game);

        _compilation = CSharpCompilation.Create("EmptyEngineCatalog", references: references);
        Game = _compilation.GetAssemblyOrModuleSymbol(game) as IAssemblySymbol;
        _roots = SerializationDiscovery.Roots(_compilation, out _attachables);
    }

    /// <summary>解析対象のコンパイル</summary>
    public static Compilation Compilation => _compilation;

    /// <summary>シリアライザの生成対象となるゲームアセンブリ。</summary>
    public static IAssemblySymbol? Game { get; private set; }

    /// <summary>authoring の根（生成器と同じ集合・同じ順）</summary>
    public static List<(INamedTypeSymbol Symbol, bool Attachable, bool Asset)> Roots()
    {
        INamedTypeSymbol? asset = _compilation.GetTypeByMetadataName(SerializationDiscovery.AssetMetadataName);
        return _roots.ConvertAll(t => (
            t,
            _attachables.Contains(t),
            asset is not null && SerializationDiscovery.HasAttribute(t, asset)));
    }
}
