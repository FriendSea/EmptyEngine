using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmptyEngine.Serialization;
using EmptyEngine.Serialization.Generator;
using MessagePack;
using Microsoft.CodeAnalysis;

if (args.Length < 3)
{
    Console.Error.WriteLine(
        "usage: CatalogTool <game-dll-path> <output-artifacts-dir> <compile-references-file>");
    return 2;
}

if (Environment.GetEnvironmentVariable(RoslynLoader.DirectoryVariable) is not { Length: > 0 } roslynDirectory
    || !Directory.Exists(roslynDirectory))
{
    Console.Error.WriteLine(
        $"[catalog] {RoslynLoader.DirectoryVariable} does not point to the SDK's Roslyn/bincore. "
        + "Type shapes are read with the same Roslyn as the compiler, so nothing can be written without it.");
    return 2;
}

const string CoreAssemblyName = "EmptyEngine.Core";

string gameDll = Path.GetFullPath(args[0]);
string outputDir = Path.GetFullPath(args[1]);
string referencesFile = Path.GetFullPath(args[2]);
string gameDir = Path.GetDirectoryName(gameDll)!;

SymbolWorld.Open(gameDll, referencesFile);

foreach (string dll in Directory.GetFiles(gameDir, "*.dll"))
{
    try { Assembly.LoadFrom(dll); }
    catch { }
}

List<Assembly> targetAssemblies = AppDomain.CurrentDomain.GetAssemblies()
    .Where(ReferencesCore)
    .ToList();

// LoadFrom は初期化を遅延するため、既定値の取得前にシリアライザを登録する。
foreach (Assembly assembly in targetAssemblies)
{
    try { RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle); }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[catalog] module initializer failed for {assembly.GetName().Name}: {ex.GetBaseException().Message}");
    }
}

// 既定値とソース位置はシンボルだけでは取得できないため、IL と PDB から補う。
var runtimeTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
foreach (Assembly assembly in targetAssemblies)
{
    foreach (Type type in GetTypesSafe(assembly))
    {
        if (type.FullName is { } name)
            runtimeTypes.TryAdd(name, type);
    }
}

var model = new CatalogModel(SymbolWorld.Compilation, SymbolWorld.Game);
var authoringOrder = new List<string>();
var assetOrder = new List<string>();

foreach ((INamedTypeSymbol symbol, bool attachable, bool asset) in SymbolWorld.Roots())
{
    if (model.AddRoot(symbol, attachable) is not { } id)
    {
        Console.Error.WriteLine($"[catalog] no shape for {ModelBuilder.WireNameOf(symbol)}");
        continue;
    }

    if (runtimeTypes.TryGetValue(id, out Type? runtimeType))
    {
        model.Table[id].Default = TryBuildDefault(runtimeType);
        model.Table[id].SourceLocation = PdbSourceLocator.Find(runtimeType);
    }
    else
    {
        Console.Error.WriteLine($"[catalog] '{id}' is in the compilation but not in the loaded assemblies");
    }

    authoringOrder.Add(id);
    if (asset)
        assetOrder.Add(id);
}

Dictionary<string, TypeEntry> table = model.Table;
List<string> referencedOrder = table.Keys
    .Where(id => !authoringOrder.Contains(id))
    .OrderBy(id => id, StringComparer.Ordinal)
    .ToList();

WriteCatalog(outputDir, table, authoringOrder.Concat(referencedOrder).ToList(), assetOrder);
Console.WriteLine(
    $"[catalog] wrote {table.Count} type(s) ({table.Values.Count(e => e.Attachable)} attachable, " +
    $"{assetOrder.Count} asset) to {Path.Combine(outputDir, CatalogFormat.FileName)}");
return 0;

static bool ReferencesCore(Assembly assembly)
{
    string? name = assembly.GetName().Name;
    if (name == CoreAssemblyName)
        return true;

    try
    {
        return assembly.GetReferencedAssemblies().Any(r => r.Name == CoreAssemblyName);
    }
    catch
    {
        return false;
    }
}

// DI 必須の型でもフィールド初期値を取得するため、参照型の引数には null を渡す。
static byte[]? TryBuildDefault(Type type)
{
    try
    {
        return Encoding.UTF8.GetBytes(MessagePackSerializer.ConvertToJson(
            TypeSerializers.Serialize(type, Construct(type))));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[catalog] no default for {type.FullName}: {ex.GetBaseException().Message}");
        return null;
    }
}

static object Construct(Type type)
{
    ConstructorInfo? ctor = type.GetConstructors().MinBy(c => c.GetParameters().Length);
    return ctor is null
        ? Activator.CreateInstance(type)!
        : ctor.Invoke(ctor.GetParameters()
            .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray());
}

static void WriteCatalog(string outputDir, Dictionary<string, TypeEntry> table, List<string> order, List<string> assetTypes)
{
    Directory.CreateDirectory(outputDir);
    File.WriteAllBytes(
        Path.Combine(outputDir, CatalogFormat.FileName),
        Write(json =>
        {
            json.WriteString("$schema", CatalogFormat.MetaSchema);
            json.WriteString("title", CatalogFormat.CatalogTitle);
            json.WriteNumber("x-formatVersion", CatalogFormat.Version);

            json.WriteStartObject("$defs");
            foreach (string id in order)
                WriteType(json, table[id]);
            json.WriteEndObject();
        }));

    File.WriteAllBytes(
        Path.Combine(outputDir, CatalogFormat.AssetSchemaFileName),
        Write(json => WriteAssetSchema(json, assetTypes)));
}

static void WriteAssetSchema(Utf8JsonWriter json, List<string> assetTypes)
{
    json.WriteString("$schema", CatalogFormat.MetaSchema);
    json.WriteString("title", CatalogFormat.AssetTitle);
    json.WriteString("type", "object");
    json.WriteStartArray("required");
    json.WriteStringValue("TypeName");
    json.WriteEndArray();

    json.WriteStartObject("properties");
    json.WriteStartObject("$schema");
    json.WriteString("type", "string");
    json.WriteEndObject();
    json.WriteStartObject("TypeName");
    json.WriteString("type", "string");
    json.WriteStartArray("enum");
    foreach (string id in assetTypes)
        json.WriteStringValue(id);
    json.WriteEndArray();
    json.WriteEndObject();
    json.WriteStartObject("Data");
    json.WriteString("type", "object");
    json.WriteEndObject();
    json.WriteEndObject();

    json.WriteStartArray("allOf");
    foreach (string id in assetTypes)
    {
        json.WriteStartObject();
        json.WriteStartObject("if");
        json.WriteStartArray("required");
        json.WriteStringValue("TypeName");
        json.WriteEndArray();
        json.WriteStartObject("properties");
        json.WriteStartObject("TypeName");
        json.WriteString("const", id);
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteStartObject("then");
        json.WriteStartObject("properties");
        json.WriteStartObject("Data");
        json.WriteString("$ref", SchemaRef(id, CatalogFormat.FileName));
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    json.WriteEndArray();
}

static byte[] Write(Action<Utf8JsonWriter> body)
{
    var buffer = new ArrayBufferWriter<byte>();
    var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    using (var json = new Utf8JsonWriter(buffer, options))
    {
        json.WriteStartObject();
        body(json);
        json.WriteEndObject();
    }

    return buffer.WrittenSpan.ToArray();
}

static void WriteType(Utf8JsonWriter json, TypeEntry entry)
{
    json.WriteStartObject(entry.Id);
    WriteValueShape(json, entry.Kind);
    json.WriteString("x-kind", entry.Kind);
    if (entry.DisplayName is { } displayName)
        json.WriteString("title", displayName);
    if (entry.Attachable)
        json.WriteBoolean("x-attachable", true);
    if (entry.SourceLocation is { } source)
    {
        json.WriteString("x-sourcePath", source.Path);
        json.WriteNumber("x-sourceLine", source.Line);
    }
    if (entry.Target is { } target)
        json.WriteString("x-target", target);

    if (entry.ColorChannels.Count > 0)
    {
        json.WriteStartArray("x-color");
        foreach (string channel in entry.ColorChannels)
            json.WriteStringValue(channel);
        json.WriteEndArray();
    }

    if (entry.Element is { } element)
    {
        json.WriteStartObject("items");
        json.WriteString("$ref", SchemaRef(element));
        json.WriteEndObject();
    }

    if (entry.Default is { } defaultJson)
    {
        json.WritePropertyName("default");
        json.WriteRawValue(defaultJson);
    }

    if (entry.Values.Count > 0)
    {
        json.WriteStartArray("anyOf");
        foreach ((string name, long value) in entry.Values)
        {
            json.WriteStartObject();
            json.WriteNumber("const", value);
            json.WriteString("title", name);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    if (entry.AssignableTo.Count > 0)
    {
        json.WriteStartArray("x-assignableTo");
        foreach (string id in entry.AssignableTo)
            json.WriteStringValue(id);
        json.WriteEndArray();
    }

    if (entry.Kind == CatalogFormat.StructKind)
    {
        json.WriteStartObject("properties");
        foreach (FieldEntry field in entry.Fields)
        {
            json.WriteStartObject(field.Name);
            json.WriteString("$ref", SchemaRef(field.TypeId));
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }

    json.WriteEndObject();
}

static void WriteValueShape(Utf8JsonWriter json, string kind)
{
    switch (kind)
    {
        case CatalogFormat.FloatKind:
        case CatalogFormat.DoubleKind:
            json.WriteString("type", "number");
            json.WriteString("format", kind == CatalogFormat.FloatKind ? "float" : "double");
            break;

        case CatalogFormat.IntKind:
        case CatalogFormat.EnumKind:
            json.WriteString("type", "integer");
            break;

        case CatalogFormat.UIntKind:
            json.WriteString("type", "integer");
            json.WriteNumber("minimum", 0);
            break;

        case CatalogFormat.BoolKind:
            json.WriteString("type", "boolean");
            break;

        case CatalogFormat.StringKind:
        case CatalogFormat.AssetRefKind:
        case CatalogFormat.ObjectRefKind:
            json.WriteString("type", "string");
            break;

        case CatalogFormat.BinaryKind:
            json.WriteString("type", "null");
            break;

        case CatalogFormat.ArrayKind:
            json.WriteString("type", "array");
            break;

        default:
            json.WriteString("type", "object");
            break;
    }
}

static string SchemaRef(string typeId, string document = "") => document + "#/$defs/" + PointerToken(typeId);

static string PointerToken(string typeId)
{
    string escaped = typeId
        .Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    var token = new StringBuilder(escaped.Length);
    foreach (byte b in Encoding.UTF8.GetBytes(escaped))
    {
        if (IsFragmentSafe(b))
            token.Append((char)b);
        else
            token.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
    }

    return token.ToString();

    static bool IsFragmentSafe(byte c) =>
        c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
        || c is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~'
        || c is (byte)'!' or (byte)'$' or (byte)'&' or (byte)'\'' or (byte)'(' or (byte)')' or (byte)'*'
            or (byte)'+' or (byte)',' or (byte)';' or (byte)'='
        || c is (byte)':' or (byte)'@';
}

static IEnumerable<Type> GetTypesSafe(Assembly assembly)
{
    try { return assembly.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
}

/// <summary>形式の定数</summary>
internal static class CatalogFormat
{
    /// <summary>型の表（<c>$defs</c>）</summary>
    public const string FileName = "TypeCatalog.json";

    /// <summary><c>.asset</c> のルート（判別子の定型）</summary>
    public const string AssetSchemaFileName = "asset.schema.json";

    public const int Version = 7;

    /// <summary>この文書自身が従う JSON Schema の版</summary>
    public const string MetaSchema = "https://json-schema.org/draft/2020-12/schema";

    public const string CatalogTitle = "EmptyEngine type catalog";
    public const string AssetTitle = "EmptyEngine asset";

    public const string FloatKind = "float";
    public const string DoubleKind = "double";
    public const string IntKind = "int";
    public const string UIntKind = "uint";
    public const string BoolKind = "bool";
    public const string StringKind = "string";

    /// <summary>JSON の値としては表さない本体データ（<c>IAssetBinary</c>）</summary>
    public const string BinaryKind = "binary";
    public const string StructKind = "struct";
    public const string ArrayKind = "array";
    public const string EnumKind = "enum";
    public const string AssetRefKind = "assetRef";
    public const string ObjectRefKind = "objectRef";

    /// <summary>シーンアセットを指す <see cref="AssetRefKind"/> の参照先</summary>
    public const string SceneTarget = "@scene";
}

/// <summary>型 1 個の記述（出力では <c>$defs</c> の 1 エントリになる）</summary>
internal sealed class TypeEntry(string id, string kind)
{
    public string Id { get; } = id;

    /// <summary>種類（<see cref="CatalogFormat"/>）</summary>
    public string Kind { get; } = kind;

    public string? DisplayName { get; set; }

    public bool Attachable { get; set; }

    /// <summary>Portable PDB から取得した型の最初の有効なソース位置</summary>
    public SourceLocation? SourceLocation { get; set; }

    /// <summary>既定値（JSON の生バイト）</summary>
    public byte[]? Default { get; set; }

    /// <summary><c>struct</c> のフィールド（宣言順・型は id 参照）</summary>
    public List<FieldEntry> Fields { get; init; } = new();

    /// <summary><c>enum</c> の (メンバ名, 格納値)</summary>
    public List<(string Name, long Value)> Values { get; init; } = new();

    /// <summary><c>array</c> の要素型 id</summary>
    public string? Element { get; init; }

    /// <summary>参照が指せる型（制約）の id</summary>
    public string? Target { get; init; }

    /// <summary>RGBA チャンネルにあたるメンバ名（色として扱える型だけが 4 つ持つ）</summary>
    public List<string> ColorChannels { get; init; } = new();

    /// <summary>この型を代入できる型の id 集合</summary>
    public List<string> AssignableTo { get; set; } = new();
}

/// <summary>フィールド 1 個＝名前と型 id だけ（型の記述は <c>$defs</c> が持つ）</summary>
internal readonly record struct FieldEntry(string Name, string TypeId);

internal readonly record struct SourceLocation(string Path, int Line);

internal static class PdbSourceLocator
{
    public static SourceLocation? Find(Type type)
    {
        try
        {
            string assemblyPath = type.Assembly.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath)) return null;

            string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (!File.Exists(pdbPath)) return null;

            using FileStream assemblyStream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(assemblyStream);
            MetadataReader assemblyMetadata = pe.GetMetadataReader();

            EntityHandle entity = MetadataTokens.EntityHandle(type.MetadataToken);
            if (entity.Kind != HandleKind.TypeDefinition) return null;
            TypeDefinition definition = assemblyMetadata.GetTypeDefinition((TypeDefinitionHandle)entity);

            using FileStream pdbStream = File.OpenRead(pdbPath);
            using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            MetadataReader pdbMetadata = provider.GetMetadataReader();

            foreach (MethodDefinitionHandle method in definition.GetMethods())
            {
                MethodDebugInformation debug = pdbMetadata.GetMethodDebugInformation(method);
                foreach (SequencePoint point in debug.GetSequencePoints())
                {
                    if (point.IsHidden || point.StartLine <= 0) continue;

                    DocumentHandle documentHandle = point.Document.IsNil ? debug.Document : point.Document;
                    if (documentHandle.IsNil) continue;

                    string path = pdbMetadata.GetString(pdbMetadata.GetDocument(documentHandle).Name);
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    string fullPath = Path.GetFullPath(path);
                    if (File.Exists(fullPath))
                        return new SourceLocation(fullPath, FindDeclarationLine(type, fullPath, point.StartLine));
                }
            }
        }
        catch
        {
            // Catalog generation must remain useful when symbols are absent or not portable.
        }

        return null;
    }

    private static int FindDeclarationLine(Type type, string path, int fallbackLine)
    {
        string typeName = type.Name;
        int genericMarker = typeName.IndexOf('`');
        if (genericMarker >= 0) typeName = typeName[..genericMarker];

        var declaration = new Regex(
            $@"\b(?:class|struct|enum|record(?:\s+(?:class|struct))?)\s+{Regex.Escape(typeName)}\b",
            RegexOptions.CultureInvariant);

        int bestLine = fallbackLine;
        int bestDistance = int.MaxValue;
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (!declaration.IsMatch(line)) continue;

            int distance = Math.Abs(lineNumber - fallbackLine);
            if (distance >= bestDistance) continue;
            bestLine = lineNumber;
            bestDistance = distance;
        }

        return bestLine;
    }
}
