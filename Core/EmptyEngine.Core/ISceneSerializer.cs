namespace EmptyEngine.Core;

/// <summary>シーン状態のシリアライザ抽象</summary>
public interface ISceneSerializer
{
    /// <summary>現在のシーン状態をシリアライズして取得</summary>
    byte[] SerializeScene();

    /// <summary>受け取ったデータのデシリアライズとシーンへの適用</summary>
    Task DeserializeSceneAsync(byte[] blob, bool isPlaying, CancellationToken cancellationToken = default);
}

/// <summary>ロード済みアセットを更新後のアーティファクトから読み直す。</summary>
public interface IAssetUpdater
{
    /// <summary>指定キーのアセット更新の要求</summary>
    /// <remarks>
    /// シーン編集の適用と同じスレッドから、同じ順序で呼ばれる。
    /// その場で読み直してよい。非同期に終える実装は、ロード済みの実体に触る前に呼び出し元のスレッドへ戻る。
    /// </remarks>
    void RequestUpdate(AssetKey key, CancellationToken cancellationToken = default);
}
