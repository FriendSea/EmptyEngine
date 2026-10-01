namespace EmptyEngine.Editor.State;

/// <summary>undo 履歴の 1 段とそれを指す安定なキー</summary>
/// <remarks>同じキーは同じ中身を表す（上書きした段には新しいキーが振られる）。保存はキーごとに 1 回だけ書く</remarks>
public readonly record struct EditHistoryStep(ulong Key, SceneSnapshot Snapshot);

/// <summary>undo 履歴まるごとの値</summary>
/// <remarks>生きている履歴は <c>EditHistoryViewModel</c> が持ち、これはその凍った写し。建て直しを跨がせるときに使う</remarks>
public sealed record EditHistory(IReadOnlyList<EditHistoryStep> Steps, int Cursor);
