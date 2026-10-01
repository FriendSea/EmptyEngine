using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.World;

namespace EmptyEngine.Modules.Testing;

/// <summary>契約テスト共通のサンプルプレハブ</summary>
internal static class SceneFixture
{
    public static RootInstanceData Scene() => new("test-instance",
        new ObjectData("obj-root", "TestScene",
        [
            new ComponentData(TypeName<TestTransform>(), new TestTransform { X = 9f, Y = 9f, Z = 9f }),
        ],
        [
            new ObjectData("obj-player", "Player",
            [
                new ComponentData(TypeName<TestTransform>(), new TestTransform { X = 1f, Y = 2f, Z = 3f }),
                new ComponentData(TypeName<TestHealth>(), new TestHealth { Max = 100, Current = 100 }),
                new ComponentData(TypeName<TestSprite>(), new TestSprite { Asset = new AssetReference<TestAsset>("sprite.png") }),
            ],
            [
                new ObjectData("obj-weapon", "Weapon",
                [
                    new ComponentData(TypeName<TestTransform>(), new TestTransform { X = 0f, Y = 1f, Z = 0f }),
                ],
                []),
            ]),
            new ObjectData("obj-camera", "MainCamera",
            [
                new ComponentData(TypeName<TestCamera>(), new TestCamera { Fov = 60f }),
            ],
            []),
        ]));

    public static byte[] Blob() => SceneBlob.Encode([Scene()]);

    /// <summary>同じシーンのエディタ表現</summary>
    public static HierarchyNode Hierarchy()
    {
        var weapon = new HierarchyNode("obj-weapon", "Weapon", null,
        [
            Component<TestTransform>(("X", 0f), ("Y", 1f), ("Z", 0f)),
        ]);

        var player = new HierarchyNode("obj-player", "Player", [weapon],
        [
            Component<TestTransform>(("X", 1f), ("Y", 2f), ("Z", 3f)),
            Component<TestHealth>(("Max", 100), ("Current", 100)),
            Component<TestSprite>(("Asset", AssetReferenceValue("sprite.png"))),
        ]);

        var camera = new HierarchyNode("obj-camera", "MainCamera", null,
        [
            Component<TestCamera>(("Fov", 60f)),
        ]);

        return new HierarchyNode("obj-root", "TestScene", [player, camera],
        [
            Component<TestTransform>(("X", 9f), ("Y", 9f), ("Z", 9f)),
        ])
        {
            SceneId = "test-instance",
        };
    }

    private static AuthoringObject Component<T>(params (string Name, object Value)[] fields)
    {
        var data = new FieldValue();
        foreach ((string name, object value) in fields)
            data.Add(name, Scalar(value));

        return new AuthoringObject(CatalogStub.Schema(typeof(T)), data);
    }

    private static FieldValue AssetReferenceValue(string key) => new() { Text = key };

    private static FieldValue Scalar(object value) => value switch
    {
        FieldValue tree => tree,
        float f => new FieldValue()
        {
            Real = f },
        int i => new FieldValue()
        {
            Integer = i },
        string s => new FieldValue()
        {
            Text = s },
        _ => FieldValue.Nil(),
    };

    private static string TypeName<T>() => typeof(T).FullName!;
}
