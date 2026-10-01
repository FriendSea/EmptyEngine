using EmptyEngine.Core;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Modules.Testing;

/// <summary>本体フィールドとメタデータを併せ持つテスト用アセット</summary>
[Asset]
public sealed class BinaryTestAsset
{
    public int Width;
    public int Height;
    public IAssetBinary Payload = null!;
}
