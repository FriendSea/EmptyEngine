namespace EmptyEngine.Editor.ViewModels.Inspector;

/// <summary>オブジェクト選択ダイアログの 1 候補</summary>
/// <param name="DisplayName">シーンルートからのパス</param>
public sealed record ObjectCandidate(string DisplayName, string TargetId);
