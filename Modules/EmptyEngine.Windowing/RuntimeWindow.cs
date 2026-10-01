namespace EmptyEngine.Windowing;

/// <summary>実行中のプラットフォームに合ったウィンドウの生成</summary>
/// <remarks>デスクトップとブラウザに対応する。iOS では利用側で <c>IosRuntimeWindow</c> を直接作成する。</remarks>
public static class RuntimeWindow
{
    /// <summary>ゲーム毎に異なるウィンドウ設定からの生成</summary>
    /// <param name="settings">初期サイズ・タイトル・位置の保存先。</param>
    public static IRuntimeWindow Create(WindowSettings settings) =>
        OperatingSystem.IsBrowser() ? new WebRuntimeWindow(settings) : new SilkRuntimeWindow(settings);
}
