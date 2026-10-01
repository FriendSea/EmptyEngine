using EmptyEngine.Core;

namespace EmptyEngine.Editor;

/// <summary>起動シーンの指定と配布物の仕上げを担当する拡張契約</summary>
public interface IDistributionBuilder
{
    /// <summary>起動順のシーン一覧を書き出し、配布物を仕上げる</summary>
    /// <param name="exeDir">配布実行ファイルのディレクトリ。</param>
    /// <param name="scenes">起動順のシーン参照。空なら既存マニフェストを削除する。</param>
    void Build(string exeDir, IReadOnlyList<AssetKey> scenes);
}
