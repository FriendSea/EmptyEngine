namespace EmptyEngine.Windowing;

/// <summary>ウィンドウ、入力、フレーム更新を提供するプラットフォーム共通の契約</summary>
public interface IRuntimeWindow : IDisposable
{
    /// <summary>このウィンドウのキーボード</summary>
    /// <remarks><see cref="Loaded"/> より前は、すべてのキーが押されていない状態を返す。</remarks>
    KeyboardDevice Keyboard { get; }

    /// <summary>このウィンドウのマウス。未対応のプラットフォームでは非アクティブ。</summary>
    MouseDevice Mouse { get; }

    /// <summary>このウィンドウのゲームパッド入力。</summary>
    GamepadDevice Gamepad { get; }

    /// <summary>このウィンドウのタッチ面</summary>
    /// <remarks>タッチを持たないプラットフォームでは常に空。</remarks>
    TouchDevice Touch { get; }

    /// <summary>ウィンドウのロード完了時の発火</summary>
    /// <remarks><see cref="NativeHandle"/> と <see cref="FramebufferSize"/> が有効になるのはこの時点から。</remarks>
    event Action? Loaded;

    /// <summary>フレームバッファのリサイズ時の発火（新しい幅・高さ）</summary>
    event Action<int, int>? Resized;

    /// <summary>ロジック更新の毎フレームフック</summary>
    event Action<double>? Update;

    /// <summary>描画の毎フレームフック</summary>
    event Action<double>? Render;

    /// <summary>メインループが終了したときの発火</summary>
    event Action? Closed;

    /// <summary>提示先(surface)を作るためのネイティブハンドル</summary>
    /// <remarks>ブラウザでは取得できず、例外を投げる。</remarks>
    (nint Window, nint Instance) NativeHandle { get; }

    /// <summary>現在のフレームバッファサイズ</summary>
    /// <remarks>ブラウザでは取得できず、例外を投げる。</remarks>
    (int Width, int Height) FramebufferSize { get; }

    /// <summary>ウィンドウを前面へ出して入力フォーカスを与える口</summary>
    void Focus();

    /// <summary>ウィンドウを開いてフレーム駆動を始める口</summary>
    /// <remarks>desktop はループを回してウィンドウが閉じるまで返らない。web はブラウザへフレームを預けて即座に返る。</remarks>
    void Run();
}
