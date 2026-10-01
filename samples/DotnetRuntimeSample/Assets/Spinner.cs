using EmptyEngine.ObjectModel;
using EmptyEngine.PlayerLoop;

namespace DotnetRuntimeSample;

/// <summary>Z 軸まわりに一定速度で回すコンポーネント</summary>
/// <remarks><see cref="DegreesPerSecond"/> はシーンに保存され、インスペクタで編集できる。</remarks>
public sealed class Spinner : UpdatableComponent
{
    // 型カタログ作成時は null が渡るが、UpdatableComponent は OnCreated まで検証を遅らせる。
    public Spinner(DefaultPlayerLoopRegistry registry) : base(registry)
    {
    }

    /// <summary>1 秒あたりの回転角（度）</summary>
    public float DegreesPerSecond { get; set; } = 90f;

    protected override void Update(IObject owner, in UpdateContext context)
    {
        if (owner.GetAttachable<TransformComponent>() is not { } transform) return;

        transform.Rotate(0f, 0f, DegreesPerSecond * (float)context.DeltaTime.TotalSeconds);
    }
}
