namespace EmptyEngine.Editor.ViewModels.Inspector;

/// <summary>操作中の表示値の更新を抑制する。</summary>
/// <remarks><see cref="Hold"/> から <see cref="Release"/> までは表示値の更新を許可しない。書き込み先の参照更新は妨げない。複数スレッドから操作できる。</remarks>
internal sealed class EditGate
{
    private EditGate? _parent;

    private int _holds;

    /// <summary>上位の門への接続</summary>
    /// <remarks>この門の更新抑制を親にも伝える。<see cref="Hold"/> を呼ぶ前に接続すること。</remarks>
    /// <param name="parent">同時に開閉する上位の門</param>
    public void LinkTo(EditGate parent) => _parent = parent;

    /// <summary>いま値を押し戻してはいけないか</summary>
    public bool IsEditing => Volatile.Read(ref _holds) > 0;

    /// <summary>掴んだ（フォーカス・ドラッグの開始）</summary>
    /// <remarks><see cref="Release"/> と対で呼ぶこと。掴んだ側が消えるときは自分で離す</remarks>
    public void Hold()
    {
        Interlocked.Increment(ref _holds);
        _parent?.Hold();
    }

    /// <summary>離した</summary>
    /// <remarks><see cref="Hold"/> より多く呼ばれても、次の更新抑制には影響しない。</remarks>
    public void Release()
    {
        int current = Volatile.Read(ref _holds);
        while (current > 0)
        {
            int seen = Interlocked.CompareExchange(ref _holds, current - 1, current);
            if (seen == current)
            {
                _parent?.Release();
                return;
            }

            current = seen;
        }
    }
}
