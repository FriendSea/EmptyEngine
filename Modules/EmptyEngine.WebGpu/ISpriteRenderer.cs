using System.Numerics;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.WebGpu;

/// <summary>レンダーパスへ描画コマンドを記録できるコンポーネントの契約</summary>
public interface ISpriteRenderer : IAttachable
{
    void Render(in RenderContext context);

    /// <summary>このレンダラが属するオブジェクト</summary>
    IObject? Owner { get; }

    /// <summary>描画順レイヤ（小さいほど先＝下に描く）</summary>
    int RenderLayer => SpriteRenderSupport.GetRenderLayer(Owner);

    /// <summary>半透明として奥から手前へ描画するか（不透明の場合は深度を書き込む）</summary>
    bool IsTransparent => true;

    /// <summary>深度書き込み設定とは独立した描画順のキュー番号</summary>
    int RenderQueue => IsTransparent ? ShaderRenderQueue.Transparent : ShaderRenderQueue.Geometry;

    /// <summary>半透明ソートに使う代表ワールド座標</summary>
    Vector3 SortPosition => SpriteRenderSupport.ReadTransform(Owner).Translation;
}
