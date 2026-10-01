using System.Runtime.InteropServices;

namespace EmptyEngine.Core.RuntimeLink;

/// <summary>エディタとランタイムの間でシーンとアセットの更新を中継する。</summary>
/// <remarks>受信した更新は <see cref="Pump"/> の呼び出しスレッドで適用する。適用先の生成と破棄は呼び出し側が管理する。</remarks>
public sealed class RuntimeEditorLink : ISceneSerializer, IAssetUpdater, IDisposable
{
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private WebSocketSceneRuntimeSession? _session;
    private Task? _task;

    public RuntimeEditorLink(Action<string> log) => _log = log;

    /// <summary>最初の scene blob 受信時と、以後 Edit/Play が切り替わったときに発火する</summary>
    /// <remarks>新しい mode の blob を <see cref="Scene"/> へ適用する直前、<see cref="Pump"/> の呼び出し thread で同期発火する。</remarks>
    public event Action<bool>? ModeChanged;

    /// <summary>シーンデータの適用先（生成と破棄は呼び出し側が管理する）</summary>
    public ISceneSerializer? Scene { get; set; }

    /// <summary>アセット更新通知の適用先（通知が不要なら <c>null</c>）</summary>
    public IAssetUpdater? Assets { get; set; }

    /// <summary>最後に受信してメインスレッドで適用したモード（未受信なら <c>null</c>）</summary>
    public bool? IsPlaying { get; private set; }

    /// <summary>Host の hub への接続をバックグラウンドで開始する</summary>
    public void Start()
    {
        Uri endpoint = RuntimeMode.EditorWebSocketEndpoint
            ?? throw new InvalidOperationException("Runtime is not configured for an editor WebSocket.");
        _session = new WebSocketSceneRuntimeSession(this, endpoint, _log, OnModeChanged, this);
        _task = RunSessionAsync();

        async Task RunSessionAsync()
        {
            try
            {
                await _session.RunAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log($"Editor link stopped unexpectedly: {ex}");
            }
        }
    }

    /// <summary>受信済み処理を main thread で適用する</summary>
    public void Pump() => _session?.Pump();

    public void Dispose()
    {
        _cts.Cancel();
        _session?.Dispose();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER")))
        {
            _cts.Dispose();
            return;
        }

        try
        {
            _task?.Wait();
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }

    byte[] ISceneSerializer.SerializeScene() =>
        (Scene ?? throw new InvalidOperationException("RuntimeEditorLink.Scene is not set."))
        .SerializeScene();

    Task ISceneSerializer.DeserializeSceneAsync(byte[] blob, bool isPlaying, CancellationToken cancellationToken) =>
        (Scene ?? throw new InvalidOperationException("RuntimeEditorLink.Scene is not set."))
        .DeserializeSceneAsync(blob, isPlaying, cancellationToken);

    void IAssetUpdater.RequestUpdate(AssetKey key, CancellationToken cancellationToken) =>
        Assets?.RequestUpdate(key, cancellationToken);

    private void OnModeChanged(bool isPlaying)
    {
        IsPlaying = isPlaying;
        ModeChanged?.Invoke(isPlaying);
    }
}
