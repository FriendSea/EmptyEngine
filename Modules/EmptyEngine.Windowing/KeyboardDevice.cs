namespace EmptyEngine.Windowing;

/// <summary>プラットフォーム非依存のキー識別子</summary>
public enum KeyboardKey
{
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    Number0, Number1, Number2, Number3, Number4,
    Number5, Number6, Number7, Number8, Number9,

    Left, Right, Up, Down,

    Space, Enter, Escape, Tab, Backspace,

    ShiftLeft, ShiftRight,
    ControlLeft, ControlRight,
    AltLeft, AltRight,
}

/// <summary>キー状態の供給元</summary>
public interface IKeyboardSource
{
    /// <summary>指定キーが現在押されているか</summary>
    bool IsKeyDown(KeyboardKey key);
}

/// <summary>ウィンドウが持つキーボード</summary>
/// <remarks>ウィンドウが開く前は、すべてのキーが押されていない状態を返す。ウィンドウと同じ寿命を持ち、モード切り替え後も利用できる。前フレームとの差や押し続けた時間は利用側で管理する。</remarks>
public sealed class KeyboardDevice : IKeyboardSource
{
    private IKeyboardSource? _source;

    /// <summary>指定キーが現在押されているか</summary>
    public bool IsKeyDown(KeyboardKey key) => _source?.IsKeyDown(key) ?? false;

    /// <summary>プラットフォーム固有の供給元の差し込み</summary>
    public void Attach(IKeyboardSource source) => _source = source;
}
