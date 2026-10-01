namespace EmptyEngine.Editor.ViewModels.Utils;

/// <summary>モデルの並びへの表示側コレクションの寄せ直し</summary>
/// <remarks>同じキーの表示オブジェクトを再利用し、モデルの順序と個数に合わせる。</remarks>
internal static class Reconcile
{
    /// <param name="into">寄せ直す先。抜けたところで <paramref name="models"/> と同じ並び・同じ数になる</param>
    /// <param name="models">正となる並び</param>
    /// <param name="viewKey">表示物のキー</param>
    /// <param name="modelKey">モデルのキー。同じキーが複数あってよく、出てきた順に対応づける</param>
    /// <param name="update">残った表示物へのモデルの反映</param>
    /// <param name="create">対応する表示物が無かったときの生成</param>
    public static void Into<TView, TModel>(
        SnapshotCollection<TView> into,
        IReadOnlyList<TModel> models,
        Func<TView, string> viewKey,
        Func<TModel, string> modelKey,
        Action<TView, TModel> update,
        Func<TModel, TView> create)
        where TView : class
    {
        var spare = new Dictionary<string, Queue<TView>>(StringComparer.Ordinal);
        foreach (TView view in into)
        {
            string key = viewKey(view);
            if (!spare.TryGetValue(key, out Queue<TView>? queue))
                spare[key] = queue = new Queue<TView>();
            queue.Enqueue(view);
        }

        var desired = new List<TView>(models.Count);
        foreach (TModel model in models)
        {
            if (spare.TryGetValue(modelKey(model), out Queue<TView>? queue) && queue.Count > 0)
            {
                TView existing = queue.Dequeue();
                update(existing, model);
                desired.Add(existing);
            }
            else
            {
                desired.Add(create(model));
            }
        }

        // 並べ替え途中の重複参照を表示側へ公開しないよう、完成した並びを一括反映する。
        if (!into.SequenceEqual(desired, ReferenceEqualityComparer.Instance))
            into.ReplaceAll(desired);
    }
}
