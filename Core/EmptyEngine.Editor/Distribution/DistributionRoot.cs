namespace EmptyEngine.Editor.Distribution;

/// <summary>配布デプロイの書き先を配布 exe ディレクトリで表すマーカー</summary>
/// <remarks>この引数を取る ctor があれば、配布デプロイではそちらが選ばれる</remarks>
public sealed record DistributionRoot(string ExeDir);
