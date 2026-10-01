using System.Security.Cryptography;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Timing;
using EmptyEngine.Editor.ViewModels.Inspector;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.Catalog;

/// <summary>authoring 対象の型（コンポーネントとアセット）の供給元</summary>
public sealed class TypeCatalog : ISchemaSource
{
    private readonly Cooldown _recheck = new(TimeSpan.FromSeconds(1));
    private readonly object _sync = new();
    private readonly Dictionary<string, ObjectSchema> _schemas = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private readonly string _path;

    private IReadOnlyList<ComponentTypeOption>? _attachable;
    private IReadOnlyDictionary<string, TypeCatalogEntry>? _types;
    private (DateTime WriteTimeUtc, long Length)? _loadedStamp;
    private (DateTime WriteTimeUtc, long Length)? _failedStamp;
    private byte[]? _loadedContent;

    /// <param name="path">読む型カタログのファイル</param>
    public TypeCatalog(string path, ILogger<TypeCatalog> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>「Add Component」候補＝アタッチ可能な型だけ（カタログの全型ではない）</summary>
    public IReadOnlyList<ComponentTypeOption> GetComponentTypes()
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _attachable!;
        }
    }

    /// <summary>型名からのスキーマの引き当て</summary>
    public ObjectSchema? Find(string typeName) => FindEntry(typeName)?.Schema;

    /// <summary>型名からの既定値 <see cref="AuthoringObject"/> の生成</summary>
    public AuthoringObject? CreateDefault(string typeName) =>
        FindEntry(typeName)?.CreateAuthoringObject();

    /// <summary>型 ID に対応するソース位置（カタログに有効な位置がなければ <c>null</c>）</summary>
    public TypeSourceLocation? GetSourceLocation(string typeName) => FindEntry(typeName)?.SourceLocation;

    /// <summary>型 ID にカタログの既定値があるか（カタログにない型は <c>true</c>）</summary>
    public bool HasDefault(string typeName) => FindEntry(typeName)?.HasDefault ?? true;

    private TypeCatalogEntry? FindEntry(string typeName)
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _types!.TryGetValue(typeName, out TypeCatalogEntry? entry) ? entry : null;
        }
    }

    /// <summary>カタログの用意（必要なら読み直し）</summary>
    /// <remarks>宣言が変わった型は、既に配ってあるスキーマの中身を差し替える＝配った先がそのまま追従する</remarks>
    private void EnsureLoaded()
    {
        // 頻繁に呼ばれるため、ファイルへのアクセスは再確認間隔を空ける。
        if (!_recheck.TryEnter() && _attachable is not null) return;
        _recheck.Mark();

        var info = new FileInfo(_path);
        if (!info.Exists)
        {
            if (_attachable is null) SetEmpty();
            return;
        }

        (DateTime WriteTimeUtc, long Length) stamp = (info.LastWriteTimeUtc, info.Length);
        if (_attachable is not null && _loadedStamp == stamp)
            return;

        try
        {
            byte[] bytes = File.ReadAllBytes(_path);
            byte[] content = SHA256.HashData(bytes);

            // 焼き直されただけで中身が同じなら、宣言は変わっていない＝配った先を揺らさない。
            if (_attachable is null || _loadedContent is not { } loaded || !loaded.AsSpan().SequenceEqual(content))
            {
                Load(bytes);
                _loadedContent = content;
            }

            _loadedStamp = stamp;
            _failedStamp = null;
        }
        catch (Exception e)
        {
            if (_failedStamp != stamp)
            {
                _failedStamp = stamp;
                _logger.LogError(
                    "'{Path}' could not be read, so no type can be looked up (the Add Component list will be empty): {Error}",
                    _path, e.Message);
            }

            if (_attachable is null) SetEmpty();
        }
    }

    /// <summary>カタログ 1 本の取り込み</summary>
    /// <remarks>宣言の差し替えは読み切ってから＝途中で落ちた読み取りが、配ってある型を半分だけ書き換えない</remarks>
    private void Load(ReadOnlyMemory<byte> utf8Json)
    {
        var adopted = new List<(ObjectSchema Kept, ObjectSchema Declared)>();

        TypeCatalogDocument document = TypeCatalogDocument.Parse(utf8Json, _logger, declaration =>
        {
            ObjectSchema kept = Canonical(declaration.TypeName);
            adopted.Add((kept, declaration));
            return kept;
        });

        foreach ((ObjectSchema kept, ObjectSchema declared) in adopted) kept.Adopt(declared);
        Apply(document);
    }

    private void Apply(TypeCatalogDocument document)
    {
        var options = new List<ComponentTypeOption>();
        var types = new Dictionary<string, TypeCatalogEntry>(StringComparer.Ordinal);

        foreach (TypeCatalogEntry entry in document.Types)
        {
            types[entry.Schema.TypeName] = entry;
            if (entry.Attachable)
                options.Add(new ComponentTypeOption(
                    entry.Schema.TypeName, entry.Schema.DisplayName, entry.CreateAuthoringObject));
        }

        _attachable = options;
        _types = types;
    }

    /// <summary>型 id ごとに配り続けるスキーマの実体</summary>
    /// <remarks>カタログから消えた型も最後の宣言のまま残す＝掴んでいる側の欄が消えない</remarks>
    private ObjectSchema Canonical(string typeName)
    {
        if (!_schemas.TryGetValue(typeName, out ObjectSchema? schema))
            _schemas[typeName] = schema = new ObjectSchema { TypeName = typeName, DisplayName = typeName };
        return schema;
    }

    private void SetEmpty()
    {
        _attachable = Array.Empty<ComponentTypeOption>();
        _types = new Dictionary<string, TypeCatalogEntry>(StringComparer.Ordinal);
    }
}
