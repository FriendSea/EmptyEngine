using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;

namespace EmptyEngine.WebGpu.Editor;

/// <summary>シェーダの宣言から組む、<c>Params</c>（float の並び）と <c>Textures</c>（参照の並び）の編集欄</summary>
/// <remarks>コンポーネント用とマテリアル用のインスペクタが共有する。</remarks>
internal static class ShaderFieldEditors
{
    public const string ParamsField = "Params";
    public const string TexturesField = "Textures";
    public const string ShaderField = "Shader";

    private const string ShaderAssetTypeId = "EmptyEngine.WebGpu.ShaderAsset";

    private static readonly string[] ColorChannels = ["R", "G", "B", "A"];

    private static readonly FieldTypeInfo ColorType = new("EmptyEngine.Graphics.Color", FieldKind.Map)
    {
        Members = ColorChannels.ToDictionary(key => key, _ => new FieldTypeInfo("System.Single", FieldKind.Float32)),
        ColorChannels = ColorChannels,
    };

    /// <summary>宣言ごとの編集欄。編集は持ち主の <c>Params</c> 全体を書き換える</summary>
    /// <remarks>編集対象が差し替わった後も <paramref name="owner"/> の現在の値へ書き込む。</remarks>
    public static FieldViewModel[] BuildParams(AuthoringObjectViewModel owner, ShaderParam[] defs)
    {
        if (defs.Length == 0) return [];
        float[] current = CurrentParams(owner, defs);
        return defs.Select((definition, index) =>
        {
            int offset = ShaderParamsHost.ValueOffset(defs, index);
            bool color = definition.Kind == ShaderParamKind.Color;
            var value = new FieldValue { Real = current[offset] };
            if (color)
                for (int channel = 0; channel < ColorChannels.Length; channel++)
                    value.Add(ColorChannels[channel], new FieldValue { Real = current[offset + channel] });
            FieldViewModel? editor = null;
            editor = new FieldViewModel(owner, definition.Name,
                color ? ColorType : new FieldTypeInfo("float", FieldKind.Float32), value,
                _ =>
                {
                    float[] values = CurrentParams(owner, defs);
                    for (int channel = 0; channel < (color ? 4 : 1); channel++)
                        values[offset + channel] = (float)(color ? editor!.Children[channel].NumericValue : editor!.NumericValue);
                    var snapshot = new FieldValue();
                    snapshot.Items.AddRange(values.Select(number => new FieldValue { Real = number }));
                    owner.Edit($"param.{index}", () => owner.Root.Get(ParamsField)!.Replace(snapshot));
                });
            return editor;
        }).ToArray();
    }

    private static float[] CurrentParams(AuthoringObjectViewModel owner, ShaderParam[] defs) => ShaderParamsHost.Reconcile(defs,
        owner.Root.Get(ParamsField)?.Children.Select(item => (float)item.NumericValue).ToArray() ?? []);

    /// <summary>スロットごとの編集欄。編集は持ち主の <c>Textures</c> 全体を書き換える</summary>
    public static FieldViewModel[] BuildTextureSlots(AuthoringObjectViewModel owner, ShaderTextureSlot[] slots) =>
        slots.Select((slot, index) =>
        {
            FieldValue? initial = owner.Root.Get(TexturesField)?.Children.ElementAtOrDefault(index)?.Capture();
            FieldViewModel? editor = null;
            editor = new FieldViewModel(owner, slot.Name,
                new FieldTypeInfo("EmptyEngine.Graphics.TextureAsset", FieldKind.AssetReference), initial,
                _ =>
                {
                    var snapshot = new FieldValue();
                    for (int i = 0; i < slots.Length; i++)
                        snapshot.Items.Add(i == index ? editor!.Capture()
                            : owner.Root.Get(TexturesField)?.Children.ElementAtOrDefault(i)?.Capture() ?? EmptyAssetRef());
                    owner.Edit($"texture.{index}", () => owner.Root.Get(TexturesField)!.Replace(snapshot));
                });
            return editor;
        }).ToArray();

    /// <summary>キーの指すシェーダのオーサリング値（シェーダでなければ <c>null</c>）</summary>
    public static AuthoringObject? ResolveShader(string? shaderKey, Func<AssetKey, AuthoringObject?> tryGetAssetValue)
    {
        if (string.IsNullOrEmpty(shaderKey)) return null;

        // Use persisted authoring data so cached reloads work without an importer instance.
        return tryGetAssetValue(new AssetKey(shaderKey)) is { TypeName: ShaderAssetTypeId } shader ? shader : null;
    }

    /// <summary>シェーダが <c>vs_main</c> を持つか</summary>
    public static bool HasVertex(AuthoringObject shader) => shader.Data.Get("HasVertex")?.Bool ?? false;

    /// <summary>シェーダの <c>vs_main</c> が受け取る頂点属性</summary>
    public static ShaderVertexInput[] ReadVertexInputs(AuthoringObject shader)
    {
        if (shader.Data.Get("VertexInputs") is not { IsNull: false } array) return [];

        var result = new List<ShaderVertexInput>(array.Items.Count);
        foreach (FieldValue item in array.Items)
        {
            if (item.IsNull) continue;
            result.Add(new ShaderVertexInput
            {
                Location = (int)(item.Get("Location")?.Integer ?? 0),
                Type = ReadString(item, "Type"),
            });
        }

        return result.ToArray();
    }

    /// <summary>シェーダの <paramref name="field"/>（<c>VertexParams</c> か <c>FragmentParams</c>）の宣言</summary>
    public static ShaderParam[] ReadParams(AuthoringObject? shader, string field)
    {
        if (shader?.Data.Get(field) is not { IsNull: false } array) return [];

        var result = new List<ShaderParam>(array.Items.Count);
        foreach (FieldValue item in array.Items)
        {
            if (item.IsNull) continue;
            result.Add(new ShaderParam
            {
                Name = ReadString(item, "Name"),
                Default = (float)ReadNumber(item, "Default"),
                Kind = (ShaderParamKind)(item.Get("Kind")?.Integer ?? 0),
                DefaultG = (float)ReadNumber(item, "DefaultG"),
                DefaultB = (float)ReadNumber(item, "DefaultB"),
                DefaultA = ReadOptionalNumber(item, "DefaultA", 1f),
            });
        }

        return result.ToArray();
    }

    /// <summary>シェーダの追加テクスチャスロットの宣言</summary>
    public static ShaderTextureSlot[] ReadTextureSlots(AuthoringObject? shader)
    {
        if (shader?.Data.Get("TextureSlots") is not { IsNull: false } array) return [];

        var result = new List<ShaderTextureSlot>(array.Items.Count);
        foreach (FieldValue item in array.Items)
        {
            if (item.IsNull) continue;
            result.Add(new ShaderTextureSlot
            {
                Name = ReadString(item, "Name"),
                Binding = (int)(item.Get("Binding")?.Integer ?? 0),
            });
        }

        return result.ToArray();
    }

    private static string ReadString(FieldValue map, string key) =>
        map.Get(key) is { IsNull: false } scalar
            ? scalar.Text ?? string.Empty
            : string.Empty;

    private static double ReadNumber(FieldValue map, string key) =>
        map.Get(key)?.Real ?? 0d;

    private static float ReadOptionalNumber(FieldValue map, string key, float fallback) =>
        map.Get(key) is { IsNull: false } scalar ? (float)scalar.Real : fallback;

    private static FieldValue EmptyAssetRef() => new() { Text = string.Empty };
}
