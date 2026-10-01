using System.Numerics;
using EmptyEngine.Core;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Modules.Testing;

public sealed class TestTransform : IAttachable
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public sealed class TestHealth : IAttachable
{
    public int Max { get; set; }
    public int Current { get; set; }
}

public sealed class TestEmission : IAttachable
{
    public bool Emit { get; set; } = true;
    public int Count { get; set; } = 1;
}

public sealed class TestCamera : IAttachable
{
    public float Fov { get; set; }
}

public sealed class TestSprite : IAttachable
{
    public AssetReference<TestAsset> Asset { get; set; }
}

[Asset]
public sealed record TestAsset
{
    public TestAsset()
    {
    }

    public TestAsset(string text) => Text = text;

    public string Text { get; set; } = string.Empty;
}

[Asset]
public sealed class TestStandaloneAsset
{
    public int Value { get; set; }
}

public sealed class TestUnmarkedAsset
{
    public int Value { get; set; }
}

public sealed class TestUnmarkedAssetReferenceHolder : IAttachable
{
    public AssetReference<TestUnmarkedAsset> Asset { get; set; }
}

[Asset]
public sealed class TestSpriteHolder
{
    public AssetReference<TestAsset> idle;
    public AssetReference<TestAsset> run;
}

/// <summary>配列フィールドのインスペクタ検証用コンポーネント</summary>
public sealed class TestArrayHolder : IAttachable
{
    public ComponentReference<TestSprite>[] Targets { get; set; } = [];

    public float[] Speeds { get; set; } = [];

    public TestStage[] Stages { get; set; } = [];
}

/// <summary>入れ子 struct のインスペクタ検証用コンポーネント</summary>
public sealed class TestNestedHolder : IAttachable
{
    public Vector3 Offset { get; set; }

    public TestTint Tint { get; set; }
}

/// <summary>書き込める float が R/G/B/A の 4 つだけの struct</summary>
[ColorChannels(nameof(R), nameof(G), nameof(B), nameof(A))]
public struct TestTint
{
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public float A { get; set; }
}

/// <summary>配列の要素になる struct</summary>
public struct TestStage
{
    public AssetReference<TestAsset> Scene { get; set; }
    public string Title { get; set; }
    public int Par { get; set; }
}

/// <summary>飛び番・非連番の列挙</summary>
public enum TestMode
{
    Idle = 0,
    Loop = 5,
    PingPong = 9,
}

/// <summary>列挙・型付きオブジェクト参照を持つコンポーネント</summary>
public sealed class TestModeHolder : IAttachable
{
    public TestMode Mode { get; set; } = TestMode.Loop;

    public ComponentReference<TestSprite> Target { get; set; }
}

public sealed class TestNumerics : IAttachable
{
    public Matrix4x4 Matrix { get; set; }

    public Vector3 Position
    {
        get => Matrix.Translation;
        set
        {
            Matrix4x4 m = Matrix;
            m.Translation = value;
            Matrix = m;
        }
    }
}

#pragma warning disable CS0649

/// <summary>メンバ列挙規約の検証用：拾うのは public フィールドと auto-property だけ</summary>
public class TestMemberProbe : IAttachable
{
    public int PublicField;
    public readonly int ReadOnlyField;
    private int _runtimeState;
    public static int StaticField;

    public int AutoProperty { get; set; }

    public int GetOnly { get; }

    public int InitOnly { get; init; }

    public int PrivateSetter { get; private set; }

    public int Computed => PublicField + _runtimeState;
}

/// <summary>基底の auto-property も拾うことの検証用</summary>
public sealed class TestDerivedMemberProbe : TestMemberProbe
{
    public int DerivedValue { get; set; }
}

/// <summary>本体（<see cref="IAssetBinary"/>）はメンバに数えないことの検証用</summary>
[Asset]
public sealed class TestBinaryProbe
{
    public IAssetBinary? Body;
    public long Length;
}

#pragma warning restore CS0649
