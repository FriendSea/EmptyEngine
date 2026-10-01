using EmptyEngine.Core;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Text;

/// <summary>インポート済みフォント</summary>
[Asset]
public sealed class FontAsset
{
    /// <summary>フォントファイル（TTF/OTF）の生バイト本体</summary>
    public IAssetBinary Data = null!;
}
