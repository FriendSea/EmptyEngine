using System.Text;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.Graphics;

namespace EmptyEngine.WebGpu.Editor;

/// <summary>マテリアル 1 つをプレビューに描くための、ブラウザへ渡す内容</summary>
/// <param name="Fragment"><c>fs_main</c> を持つ WGSL（シェーダが無い・フラグメントを持たないなら <c>null</c>）</param>
/// <param name="Blend"><see cref="MaterialBlend"/> の名前</param>
/// <param name="DepthCompare"><see cref="MaterialDepthCompare"/> の名前</param>
/// <param name="Params"><c>group(2) binding(0)</c> の uniform buffer の中身</param>
/// <param name="Textures"><c>group(2)</c> の追加テクスチャ</param>
/// <param name="MainTexture"><c>group(3)</c> のメインテクスチャのアセットキー（空なら白）</param>
/// <param name="ComponentParams"><c>group(0) binding(3)</c> の代わりに渡す、シェーダの既定値</param>
/// <param name="OwnVertex">シェーダが自分の <c>vs_main</c> を持つか（プレビューはそれを使わない）</param>
internal sealed record MaterialPreviewSpec(
    string? Fragment,
    string Blend,
    bool DepthWrite,
    string DepthCompare,
    float[] Params,
    MaterialPreviewTexture[] Textures,
    string MainTexture,
    float[] ComponentParams,
    bool OwnVertex)
{
    private const string TextureAssetTypeId = "EmptyEngine.Graphics.TextureAsset";

    /// <summary>その時点のマテリアルの値からの組み立て</summary>
    public static MaterialPreviewSpec Build(
        AuthoringObjectViewModel material,
        Func<AssetKey, AuthoringObject?> tryGetAssetValue)
    {
        FieldValue data = material.Capture().Data;
        AuthoringObject? shader = ShaderFieldEditors.ResolveShader(
            data.Get(ShaderFieldEditors.ShaderField)?.ReferenceKey, tryGetAssetValue);
        bool drawable = shader?.Data.Get("HasFragment")?.Bool ?? false;

        ShaderParam[] defs = ShaderFieldEditors.ReadParams(shader, "FragmentParams");
        float[] values = ShaderParamsHost.Reconcile(defs,
            data.Get(ShaderFieldEditors.ParamsField)?.Items.Select(item => (float)item.Real).ToArray() ?? []);
        // コンポーネントが居ないので、コンポーネント側の Params は既定値で代える。
        float[] componentValues = ShaderParamsHost.Reconcile(ShaderFieldEditors.ReadParams(shader, "VertexParams"), []);

        List<FieldValue> references = data.Get(ShaderFieldEditors.TexturesField)?.Items ?? [];
        MaterialPreviewTexture[] textures = ShaderFieldEditors.ReadTextureSlots(shader)
            .Select((slot, index) => new MaterialPreviewTexture(
                slot.Binding, references.ElementAtOrDefault(index)?.ReferenceKey ?? string.Empty))
            .ToArray();

        return new MaterialPreviewSpec(
            drawable ? ReadSource(shader!) : null,
            ((MaterialBlend)(data.Get("Blend")?.Integer ?? 0)).ToString(),
            data.Get("DepthWrite")?.Bool ?? true,
            ((MaterialDepthCompare)(data.Get("DepthCompare")?.Integer ?? 0)).ToString(),
            UniformBuffer(values),
            textures,
            data.Get("MainTexture")?.ReferenceKey ?? string.Empty,
            UniformBuffer(componentValues),
            shader is not null && ShaderFieldEditors.HasVertex(shader));
    }

    // uniform buffer は 16 バイト単位。値が無くても束縛は要る。
    private static float[] UniformBuffer(float[] values)
    {
        float[] buffer = new float[Math.Max(4, (values.Length + 3) & ~3)];
        values.CopyTo(buffer, 0);
        return buffer;
    }

    /// <summary>テクスチャアセットの RGBA8 のピクセル（テクスチャでない・本体が無いなら <c>null</c>）</summary>
    public static (int Width, int Height, byte[] Pixels)? ReadPixels(AuthoringObject? texture)
    {
        if (texture is not { TypeName: TextureAssetTypeId }) return null;
        if (texture.Data.Get("Pixels")?.Binary is not { } binary) return null;

        var asset = new TextureAsset
        {
            Width = (int)(texture.Data.Get("Width")?.Integer ?? 0),
            Height = (int)(texture.Data.Get("Height")?.Integer ?? 0),
            Format = (TexturePixelFormat)(texture.Data.Get("Format")?.Integer ?? 0),
            Pixels = binary,
        };
        if (asset.Width <= 0 || asset.Height <= 0) return null;

        byte[] pixels;
        if (asset.Format == TexturePixelFormat.BasisUniversalKtx2)
        {
            pixels = asset.Transcode(BasisTranscodeTarget.Rgba8).Pixels;
        }
        else
        {
            using Stream stream = binary.OpenRead();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            pixels = memory.ToArray();
        }

        return pixels.Length == asset.Width * asset.Height * 4 ? (asset.Width, asset.Height, pixels) : null;
    }

    private static string? ReadSource(AuthoringObject shader)
    {
        if (shader.Data.Get("Source")?.Binary is not { } binary) return null;

        using Stream stream = binary.OpenRead();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>追加テクスチャスロット 1 つ分</summary>
/// <param name="Binding"><c>group(2)</c> の binding 番号</param>
/// <param name="Key">貼るテクスチャのアセットキー（空なら白）</param>
internal sealed record MaterialPreviewTexture(int Binding, string Key);

/// <summary>プレビューを描いた結果</summary>
/// <param name="Error">描けなかった理由（描けたなら <c>null</c>）</param>
/// <param name="StandIn">シェーダがコンポーネント側の値（<c>group(0)</c>）を読むので、代わりの値を渡したか</param>
internal sealed record MaterialPreviewResult(string? Error, bool StandIn);
