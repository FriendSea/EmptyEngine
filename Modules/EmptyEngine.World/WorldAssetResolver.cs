using EmptyEngine.Core;
using EmptyEngine.Serialization;
using EmptyEngine.ObjectModel;
using EmptyEngine.Storage;

namespace EmptyEngine.World;

/// <summary>シーンとアセットの参照を解決し、ロード済みアセットを更新する</summary>
/// <remarks>読み込みと読み直しは 1 回ずつ通す。読み込み途中の実体は、その回が終わるまで外からは見えない。</remarks>
public sealed class WorldAssetResolver : IAssetResolver, IAssetUpdater, IDisposable
{
    private static readonly AsyncLocal<WorldAssetResolver?> s_running = new();

    private readonly AssetStorage _storage;
    private readonly AssetResolver _artifacts;
    private readonly Dictionary<string, GameObject> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unsettled = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Action<string>? _log;
    private readonly IServiceProvider? _services;

    /// <summary>実行環境のアセットストアを使うリゾルバを作成する</summary>
    public WorldAssetResolver() : this(RuntimeStore.Open(), services: null) { }

    /// <summary>DI コンテナを与える構築</summary>
    /// <param name="log">復元に失敗したアセットの報告先。省略で標準エラー。</param>
    public WorldAssetResolver(IServiceProvider? services, Action<string>? log = null)
        : this(RuntimeStore.Open(), services, log) { }

    /// <summary>明示ストアでの構築</summary>
    internal WorldAssetResolver(AssetStorage store, IServiceProvider? services = null, Action<string>? log = null)
    {
        _storage = store;
        _log = log;
        _services = services;
        _artifacts = new AssetResolver(store, services, log);
    }

    /// <summary>アセットの読み込み元を解放する</summary>
    public void Dispose()
    {
        _turn.Dispose();
        _storage.Dispose();
    }

    /// <summary><see cref="AssetStorage"/> からの resolver の生成</summary>
    internal static WorldAssetResolver FromStore(AssetStorage store) => new(store);

    private bool InTurn => s_running.Value == this;

    public bool TryResolve<TAsset>(AssetReference<TAsset> assetReference, out TAsset? asset) where TAsset : class
        => TryGetSettled(AssetResolver.NormalizeKey(assetReference.AssetKey), out asset);

    public ValueTask<TAsset?> ResolveAsync<TAsset>(
        AssetReference<TAsset> assetReference, CancellationToken cancellationToken = default) where TAsset : class
    {
        string key = AssetResolver.NormalizeKey(assetReference.AssetKey);
        if (key.Length == 0)
            return new((TAsset?)null);
        if (TryGetSettled(key, out TAsset? loaded))
            return new(loaded);
        return new(InTurn
            ? LoadAsync<TAsset>(key, cancellationToken)
            : RunTurnAsync(() => LoadAsync<TAsset>(key, cancellationToken), cancellationToken));
    }

    /// <summary>読み込みの 1 回分としての実行</summary>
    /// <remarks>
    /// 他の回が終わるのを待ってから始める。回の中では、読み込み途中の実体も互いに見える。
    /// 既に回の中なら、そのまま実行する。
    /// </remarks>
    internal async Task<TResult> RunTurnAsync<TResult>(Func<Task<TResult>> turn, CancellationToken cancellationToken = default)
    {
        if (InTurn)
            return await turn();

        await _turn.WaitAsync(cancellationToken);
        try
        {
            s_running.Value = this;
            return await turn();
        }
        finally
        {
            _unsettled.Clear();
            _turn.Release();
        }
    }

    /// <inheritdoc cref="RunTurnAsync{TResult}(Func{Task{TResult}}, CancellationToken)"/>
    internal Task RunTurnAsync(Func<Task> turn, CancellationToken cancellationToken = default)
        => RunTurnAsync(async () =>
        {
            await turn();
            return true;
        }, cancellationToken);

    private bool TryGetSettled<TAsset>(string key, out TAsset? asset) where TAsset : class
    {
        if (!InTurn && _unsettled.Contains(key))
        {
            asset = null;
            return false;
        }

        if (typeof(TAsset) == typeof(IObject))
        {
            asset = _templates.TryGetValue(key, out GameObject? template) ? template as TAsset : null;
            return asset is not null;
        }

        return _artifacts.TryGetLoaded(key, out asset);
    }

    private async Task<TAsset?> LoadAsync<TAsset>(string key, CancellationToken cancellationToken) where TAsset : class
    {
        bool template = typeof(TAsset) == typeof(IObject);
        if (!_loading.TryGetValue(key, out Task? load) && !(template ? _templates.ContainsKey(key) : _artifacts.IsLoaded(key)))
        {
            load = template ? LoadTemplateAsync(key) : LoadAssetAsync(key);
            if (!load.IsCompleted)
                _loading[key] = load;
        }

        if (load is not null)
            await load.WaitAsync(cancellationToken);

        return TryGetSettled(key, out TAsset? asset) ? asset : null;
    }

    private async Task LoadAssetAsync(string key)
    {
        try
        {
            object? asset;
            using (Stream? artifact = await _storage.OpenAsync(key))
                asset = artifact is null ? null : _artifacts.Materialize(key, artifact);
            if (asset is null)
                return;

            _unsettled.Add(key);
            if (asset is IAssetResolutionHook hook)
                await AssetResolution.ResolveAllAsync([(hook, key)], this, OnHookFailed);
        }
        catch (Exception e)
        {
            Log($"Failed to load asset key={key}: {e}");
        }
        finally
        {
            _loading.Remove(key);
        }
    }

    private async Task LoadTemplateAsync(string key)
    {
        try
        {
            ObjectData? data;
            using (Stream? artifact = await _storage.OpenAsync(key))
                data = artifact is null ? null : LoadTemplateData(key, artifact);
            if (data is null)
                return;

            GameObject template = BuildTemplate(data);
            _templates[key] = template;
            _unsettled.Add(key);
            await AssetResolution.ResolveAllAsync(AssetResolution.EnumerateTree(template), this, OnHookFailed);
        }
        catch (Exception e)
        {
            Log($"Failed to load scene template key={key}: {e}");
        }
        finally
        {
            _loading.Remove(key);
        }
    }

    private ObjectData? LoadTemplateData(string key, Stream artifact)
    {
        try
        {
            using var bytes = new MemoryStream();
            artifact.CopyTo(bytes);
            IReadOnlyList<RootInstanceData> roots = SceneBlob.Decode(
                bytes.GetBuffer().AsMemory(0, (int)bytes.Length), message => Log($"key={key}: {message}"), _services);
            return roots.Count == 0 ? null : roots[0].Root;
        }
        catch (Exception e)
        {
            Log($"Failed to deserialize scene template key={key}: {e}");
            return null;
        }
    }

    private void OnHookFailed(object target, string ownerName, Exception e)
        => Log($"OnResolveAssetsAsync threw on '{ownerName}' ({target.GetType().Name}): {e}");

    private void Log(string message)
    {
        if (_log is not null)
            _log($"[WorldAssetResolver] {message}");
        else
            Console.Error.WriteLine($"[WorldAssetResolver] {message}");
    }

    private static GameObject BuildTemplate(ObjectData data)
    {
        var attachables = data.Components.Where(c => c.Component is not null).Select(c => c.Component!).ToList();
        var children = data.Children.Select(BuildTemplate).ToList();
        return new GameObject(data.Id, data.Name, attachables, children, data.Active);
    }

    /// <summary>ロード済みの実体の同じインスタンスのままの読み直し</summary>
    public void RequestUpdate(AssetKey key, CancellationToken cancellationToken = default)
        => _ = UpdateAsync(key, cancellationToken);

    /// <summary>読み直しの 1 回分</summary>
    internal Task UpdateAsync(AssetKey key, CancellationToken cancellationToken = default)
        => RunTurnAsync(() => ReloadAsync(AssetResolver.NormalizeKey(key.Value), cancellationToken), cancellationToken);

    private async Task ReloadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            _storage.ClearCache(key);
            bool asset = _artifacts.IsLoaded(key);
            bool template = _templates.ContainsKey(key);
            if (!asset && !template)
                return;

            object? updated = null;
            ObjectData? data = null;
            using (Stream? artifact = await _storage.OpenAsync(key, cancellationToken))
            {
                if (asset)
                    updated = _artifacts.ApplyUpdate(key, artifact);
                if (template && artifact is not null)
                {
                    artifact.Position = 0;
                    data = LoadTemplateData(key, artifact);
                }
            }

            if (updated is IAssetResolutionHook hook)
                await AssetResolution.ResolveAllAsync([(hook, key)], this, OnHookFailed);
            if (template)
                await UpdateTemplateAsync(key, data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Log($"Failed to update asset '{key}': {e}");
        }
    }

    /// <summary>起動シーンとその参照するアセットの読み込みと適用</summary>
    public Task<IReadOnlyList<AssetKey>> LoadStartupScenesAsync(
        ISceneSerializer serializer, Action<string>? log = null, CancellationToken cancellationToken = default)
        => serializer.LoadAllIntoAsync(_storage, log, cancellationToken);

    private async Task UpdateTemplateAsync(string normalizedKey, ObjectData? data)
    {
        if (!_templates.TryGetValue(normalizedKey, out GameObject? template))
            return;

        if (data is null || !string.Equals(data.Id, template.Id, StringComparison.Ordinal))
        {
            _templates.Remove(normalizedKey);
            return;
        }

        template.SetName(data.Name);
        template.Active = data.Active;
        template.SetAttachables(data.Components.Where(c => c.Component is not null).Select(c => c.Component!).ToList());
        template.SetChildren(data.Children.Select(BuildTemplate).ToList());
        await AssetResolution.ResolveAllAsync(AssetResolution.EnumerateTree(template), this, OnHookFailed);
    }
}
