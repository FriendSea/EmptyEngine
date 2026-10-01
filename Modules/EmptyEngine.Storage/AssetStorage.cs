using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;

namespace EmptyEngine.Storage;

/// <summary>インポート済みアーティファクトのバイトの出所</summary>
/// <remarks>
/// キーは <c>/</c> 区切りの正規化済み相対パス。ディレクトリ・アーカイブ・HTTP ではキーの成果物を <see cref="ImportedAssetsLayout.ArtifactPath"/> の名前で探す。
/// 読み出しが返すストリームはシーク可能
/// </remarks>
public sealed class AssetStorage : IDisposable
{
    private readonly string? _root;
    private readonly Dictionary<string, byte[]>? _memory;

    private readonly ConcurrentDictionary<string, string> _pathByKey = new(StringComparer.Ordinal);

    private readonly ZipArchive? _archive;
    private readonly Dictionary<string, ZipArchiveEntry>? _entries;
    private readonly object _archiveGate = new();

    private static readonly HttpRequestOptionsKey<IDictionary<string, object>> BrowserFetchOptions = new("WebAssemblyFetchOptions");

    private readonly HttpClient? _http;
    private readonly string? _cacheRoot;

    private AssetStorage(string root) => _root = Path.GetFullPath(root);

    private AssetStorage(Dictionary<string, byte[]> memory) => _memory = memory;

    private AssetStorage(Uri baseAddress, string cacheRoot)
    {
        _http = new HttpClient { BaseAddress = baseAddress };
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    private AssetStorage(ZipArchive archive)
    {
        _archive = archive;
        _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!string.IsNullOrEmpty(entry.Name) && ImportedAssetsLayout.TryGetArtifactKey(entry.FullName, out string? key))
                _entries.Add(key, entry);
        }
    }

    /// <summary>ディレクトリを読み込むストアのルート（それ以外では <c>null</c>）</summary>
    public string? DirectoryRoot => _root;

    /// <summary>ディスク上のディレクトリを供給元にする構築</summary>
    public static AssetStorage FromDirectory(string root) => new(root);

    /// <summary>HTTP サーバーを供給元にする構築</summary>
    /// <param name="baseUrl">アーティファクトを配る場所の絶対 http(s) URL。キーはこの下の相対パスとして引く（末尾の <c>/</c> は補う）</param>
    /// <param name="cacheDirectory">読み出した内容を置く場所。同じ場所を指すストア同士は読み出した内容を共有する。</param>
    public static AssetStorage FromHttp(string baseUrl, string cacheDirectory)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseAddress)
            || baseAddress.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                $"Asset server base address must be an absolute http(s) URL: '{baseUrl}'.", nameof(baseUrl));
        }

        if (!baseAddress.AbsolutePath.EndsWith('/'))
            baseAddress = new UriBuilder(baseAddress) { Path = baseAddress.AbsolutePath + "/" }.Uri;

        return new AssetStorage(baseAddress, cacheDirectory);
    }

    /// <summary>単一アーカイブ（zip）を供給元にする構築</summary>
    public static AssetStorage FromArchive(string archivePath) => FromArchive(archivePath, fallbackDirectory: null);

    /// <summary>アーカイブが存在しなければ、指定のディレクトリを直接読む</summary>
    public static AssetStorage FromArchive(string archivePath, string? fallbackDirectory) =>
        File.Exists(archivePath)
            ? new AssetStorage(new ZipArchive(File.OpenRead(archivePath), ZipArchiveMode.Read))
            : fallbackDirectory is not null ? FromDirectory(fallbackDirectory) : InMemory();

    /// <summary>メモリを供給元にする構築</summary>
    internal static AssetStorage InMemory() => new(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));

    /// <summary>メモリ供給へのキーとバイト列の登録</summary>
    internal void Add(string key, byte[] bytes)
    {
        if (_memory is null)
            throw new InvalidOperationException("Add is only available on an in-memory AssetStorage.");
        _memory[AssetPath.NormalizeKey(key)] = bytes;
    }

    /// <summary>キーのアーティファクトの読み出し</summary>
    /// <returns>シーク可能な読み取り専用ストリーム。無ければ <c>null</c></returns>
    public ValueTask<Stream?> OpenAsync(string key, CancellationToken cancellationToken = default)
    {
        string k = AssetPath.NormalizeKey(key);
        if (_http is null) return new(OpenLocal(k));
        return OpenFile(CachedPath(k)) is { } cached ? new(cached) : new(DownloadAsync(k, cancellationToken));
    }

    /// <summary>待たずに開けるアーティファクトの読み出し</summary>
    /// <returns>無いか読み出しを待つ必要があれば <c>false</c>。<see cref="OpenAsync"/> で読み出したキーは <see cref="ClearCache"/> まで開ける</returns>
    public bool TryOpen(string key, [NotNullWhen(true)] out Stream? stream)
    {
        string k = AssetPath.NormalizeKey(key);
        stream = _http is null ? OpenLocal(k) : OpenFile(CachedPath(k));
        return stream is not null;
    }

    /// <summary>キーの読み出し済みの内容の破棄</summary>
    /// <remarks>次の <see cref="OpenAsync"/> は最新の内容を読む。</remarks>
    public void ClearCache(string key)
    {
        if (_cacheRoot is null) return;

        string path = CachedPath(AssetPath.NormalizeKey(key));
        if (File.Exists(path)) File.Delete(path);
    }

    private Stream? OpenLocal(string normalizedKey)
    {
        if (_entries is not null)
            return ReadArchiveEntry(normalizedKey) is { } entry ? new MemoryStream(entry, writable: false) : null;

        if (_memory is not null)
            return _memory.TryGetValue(normalizedKey, out byte[]? bytes) ? new MemoryStream(bytes, writable: false) : null;

        return OpenFile(ResolvePath(normalizedKey));
    }

    private async Task<Stream?> DownloadAsync(string normalizedKey, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(RequestPath(normalizedKey));
        using HttpResponseMessage response = await _http!.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        WriteAtomically(CachedPath(normalizedKey), bytes);
        return new MemoryStream(bytes, writable: false);
    }

    /// <summary>置き換えや削除を妨げない読み取り</summary>
    /// <returns>ファイルが無ければ <c>null</c></returns>
    private static FileStream? OpenFile(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private byte[]? ReadArchiveEntry(string normalizedKey)
    {
        lock (_archiveGate)
        {
            if (!_entries!.TryGetValue(normalizedKey, out ZipArchiveEntry? entry))
                return null;

            using Stream source = entry.Open();
            using var memory = new MemoryStream(entry.Length > 0 ? checked((int)entry.Length) : 0);
            source.CopyTo(memory);
            return memory.ToArray();
        }
    }

    /// <summary>HTTP キャッシュに再検証させる要求</summary>
    private static HttpRequestMessage CreateRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Options.Set(BrowserFetchOptions, new Dictionary<string, object> { ["cache"] = "no-cache" });
        return request;
    }

    /// <summary>キーからのサーバー上の相対パスの組み立て</summary>
    private static string RequestPath(string normalizedKey) =>
        string.Join('/', ImportedAssetsLayout.ArtifactPath(normalizedKey).Split('/').Select(Uri.EscapeDataString));

    /// <summary>キーからの読み出し済みの内容の置き場の解決</summary>
    private string CachedPath(string normalizedKey) =>
        AssetPath.ResolveUnderRoot(_cacheRoot!, ImportedAssetsLayout.ArtifactPath(normalizedKey));

    /// <summary>キーからの物理パスの解決</summary>
    private string ResolvePath(string key)
        => _pathByKey.GetOrAdd(key, static (k, root) => AssetPath.ResolveUnderRoot(root, ImportedAssetsLayout.ArtifactPath(k)), _root!);

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>保持中のアーカイブハンドルと HTTP クライアントの解放</summary>
    public void Dispose()
    {
        _http?.Dispose();
        _archive?.Dispose();
    }
}
