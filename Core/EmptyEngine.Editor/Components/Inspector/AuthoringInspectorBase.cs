using EmptyEngine.Editor.ViewModels.Inspector;
using Microsoft.AspNetCore.Components;

namespace EmptyEngine.Editor.Components.Inspector;

/// <summary>オーサリング対象を表示・編集する専用インスペクタの基底。</summary>
/// <remarks>対象の初期化には <see cref="OnAuthoringBound"/> を使う。独自の編集欄は <see cref="AuthoringObjectViewModel"/> を所有者とする <see cref="FieldViewModel"/> と <c>FieldEditor</c> で構成する。</remarks>
public abstract class AuthoringInspectorBase : ObservingComponentBase
{
    private AuthoringObjectViewModel? _bound;

    /// <summary>オーサリング対象</summary>
    [Parameter, EditorRequired] public AuthoringObjectViewModel Authoring { get; set; } = null!;

    protected sealed override void OnParametersSet()
    {
        if (ReferenceEquals(_bound, Authoring)) return;

        Unbind();
        _bound = Authoring;
        Authoring.Refreshed += OnRefreshed;

        OnAuthoringBound();
    }

    /// <summary>受け持つオーサリング対象が決まった（差し替わった）とき</summary>
    protected virtual void OnAuthoringBound()
    {
    }

    /// <summary>スナップショット反映後に表示値を更新できることを通知する。</summary>
    /// <remarks>ユーザーの編集操作中と編集直後は通知しない。</remarks>
    protected virtual void OnAuthoringRefreshed()
    {
    }

    private void OnRefreshed()
    {
        if (!Authoring.IsUserEditing) OnAuthoringRefreshed();
        Rerender();
    }

    private void Unbind()
    {
        if (_bound is not null) _bound.Refreshed -= OnRefreshed;
        _bound = null;
        Unobserve();
    }

    public override void Dispose()
    {
        Unbind();
        base.Dispose();
    }
}
