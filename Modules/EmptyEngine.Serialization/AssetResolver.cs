using EmptyEngine.Core;
using EmptyEngine.Storage;

namespace EmptyEngine.Serialization;

/// <summary>アーティファクト 1 件＝アセット実体 1 つの復元とキャッシュ</summary>
/// <remarks>アーティファクトの読み出しと内包参照の解決（<see cref="IAssetResolutionHook"/>）は呼び出し側が行う。</remarks>
public sealed class AssetResolver
{
    private readonly AssetStorage _store;
    private readonly IServiceProvider? _services;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, object> _resolvedCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="RuntimeStore"/> をアセットの読み込み元として使う構築</summary>
    /// <param name="services">アセット実体のコンストラクタ引数を解決する DI コンテナ。</param>
    /// <param name="log">復元に失敗したアセットの報告先。省略で標準エラー。</param>
    public AssetResolver(IServiceProvider? services = null, Action<string>? log = null)
        : this(RuntimeStore.Open(), services, log) { }

    /// <summary>明示ストアでの構築</summary>
    internal AssetResolver(AssetStorage store, IServiceProvider? services = null, Action<string>? log = null)
    {
        _store = store;
        _services = services;
        _log = log;
    }

    /// <summary>アセットキーの正規化規約</summary>
    public static string NormalizeKey(string assetKey) =>
        string.IsNullOrWhiteSpace(assetKey) ? string.Empty : assetKey.Replace('\\', '/').TrimStart('/');

    /// <summary>復元済みのアセット実体の取得</summary>
    /// <returns>未復元か型が合わなければ <c>false</c></returns>
    public bool TryGetLoaded<TAsset>(string normalizedKey, out TAsset? asset) where TAsset : class
    {
        asset = _resolvedCache.TryGetValue(normalizedKey, out object? cached) ? cached as TAsset : null;
        return asset is not null;
    }

    /// <summary>復元済みか（型は問わない）</summary>
    public bool IsLoaded(string normalizedKey) => _resolvedCache.ContainsKey(normalizedKey);

    /// <summary>アーティファクトからのアセット実体の復元とキャッシュへの登録</summary>
    /// <param name="artifact">ストアから読み出したキーのアーティファクト</param>
    /// <returns>復元に失敗すれば <c>null</c></returns>
    public object? Materialize(string normalizedKey, Stream artifact)
    {
        object? loaded = LoadFromArtifact(normalizedKey, artifact);
        if (loaded is not null)
            _resolvedCache[normalizedKey] = loaded;
        return loaded;
    }

    private void Log(string message)
    {
        if (_log is not null)
            _log($"[AssetResolver] {message}");
        else
            Console.Error.WriteLine($"[AssetResolver] {message}");
    }

    private object? LoadFromArtifact(string key, Stream artifact)
    {
        try
        {
            return AssetBlob.Deserialize(_store, key, artifact, _services);
        }
        catch (Exception e)
        {
            Log($"Failed to deserialize asset key={key}: {e}");
            return null;
        }
    }

    /// <summary>復元済みのアセット実体の同じインスタンスのままの読み直し</summary>
    /// <param name="artifact">ストアから読み出したキーのアーティファクト。無くなっていれば <c>null</c></param>
    /// <returns>読み直した実体。未復元なら <c>null</c>、読み直せなければキャッシュから外して <c>null</c></returns>
    internal object? ApplyUpdate(string normalizedKey, Stream? artifact)
    {
        if (!_resolvedCache.TryGetValue(normalizedKey, out object? cached))
            return null;

        if (artifact is null || !UpdateFromArtifact(cached, normalizedKey, artifact))
        {
            _resolvedCache.Remove(normalizedKey);
            return null;
        }

        return cached;
    }

    private bool UpdateFromArtifact(object target, string key, Stream artifact)
    {
        try
        {
            return AssetBlob.UpdateInto(target, _store, key, artifact);
        }
        catch (Exception e)
        {
            Log($"Failed to reload asset key={key}: {e}");
            return false;
        }
    }
}
