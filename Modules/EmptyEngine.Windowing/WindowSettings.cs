namespace EmptyEngine.Windowing;

/// <summary>ゲーム毎に異なるウィンドウの初期設定</summary>
/// <param name="Width">ウィンドウの初期幅(px)。<paramref name="LockAspectRatio"/> 有効時は縦横比の基準にもなる。</param>
/// <param name="Height">ウィンドウの初期高さ(px)。</param>
/// <param name="Title">ウィンドウタイトル。</param>
/// <param name="LockAspectRatio">true ならリサイズ時に Width:Height の縦横比へ拘束する（desktop のみ）。</param>
/// <param name="PlacementPath">前回の位置とサイズを保存する、実行ファイルのディレクトリ基準の相対パス。</param>
public readonly record struct WindowSettings(
    int Width,
    int Height,
    string Title,
    bool LockAspectRatio = true,
    string PlacementPath = "window-placement.save");
