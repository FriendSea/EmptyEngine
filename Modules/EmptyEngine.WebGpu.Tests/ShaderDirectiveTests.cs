using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor;
using EmptyEngine.Storage;
using EmptyEngine.Tests;
using EmptyEngine.WebGpu.Editor;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.WebGpu.Tests;

/// <summary>WGSL の <c>//!</c> ディレクティブが取り込み時に値へ変わり、ランタイムまで値のまま届くことの確認</summary>
public sealed class ShaderDirectiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ee-shader-directives-" + Guid.NewGuid().ToString("N"));

    public ShaderDirectiveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("//! blend: invert", ShaderBlendMode.Invert)]
    [InlineData("//! blend: alpha", ShaderBlendMode.Alpha)]
    [InlineData("//! blend: dst-alpha-mask", ShaderBlendMode.DstAlphaMask)]
    [InlineData("//! BLEND : Invert", ShaderBlendMode.Invert)]
    [InlineData("// blend: invert", ShaderBlendMode.Unspecified)]
    [InlineData("", ShaderBlendMode.Unspecified)]
    public void Blend_directive_becomes_a_value(string directive, ShaderBlendMode expected)
        => Assert.Equal(expected, WgslShaderDirectives.ParseBlend(directive));

    [Theory]
    [InlineData("//! render: opaque", ShaderRenderMode.Opaque)]
    [InlineData("//! render: transparent", ShaderRenderMode.Transparent)]
    [InlineData("", ShaderRenderMode.Unspecified)]
    public void Render_directive_becomes_a_value(string directive, ShaderRenderMode expected)
        => Assert.Equal(expected, WgslShaderDirectives.ParseRender(directive));

    [Theory]
    [InlineData("//! queue: background", ShaderRenderQueue.Background)]
    [InlineData("//! queue: geometry", ShaderRenderQueue.Geometry)]
    [InlineData("//! queue: alpha-test", ShaderRenderQueue.AlphaTest)]
    [InlineData("//! queue: transparent", ShaderRenderQueue.Transparent)]
    [InlineData("//! queue: overlay", ShaderRenderQueue.Overlay)]
    [InlineData("//! queue: 2999", 2999)]
    [InlineData("", 0)]
    public void Queue_directive_becomes_a_number(string directive, int expected)
        => Assert.Equal(expected, WgslShaderDirectives.ParseQueue(directive));

    [Theory]
    [InlineData("//! zwrite: on", ShaderDepthWrite.On)]
    [InlineData("//! zwrite: off", ShaderDepthWrite.Off)]
    [InlineData("", ShaderDepthWrite.Unspecified)]
    public void ZWrite_directive_becomes_a_value(string directive, ShaderDepthWrite expected)
        => Assert.Equal(expected, WgslShaderDirectives.ParseDepthWrite(directive));

    [Theory]
    [InlineData("//! ztest: less", ShaderDepthCompare.Less)]
    [InlineData("//! ztest: less-equal", ShaderDepthCompare.LessEqual)]
    [InlineData("//! ztest: lequal", ShaderDepthCompare.LessEqual)]
    [InlineData("//! ztest: always", ShaderDepthCompare.Always)]
    [InlineData("//! ztest: off", ShaderDepthCompare.Always)]
    [InlineData("", ShaderDepthCompare.Unspecified)]
    public void ZTest_directive_becomes_a_value(string directive, ShaderDepthCompare expected)
        => Assert.Equal(expected, WgslShaderDirectives.ParseDepthCompare(directive));

    [Fact]
    public async Task Importer_bakes_every_directive_onto_the_asset()
    {
        ShaderAsset asset = await ImportAsync("""
            //! blend: invert
            //! queue: overlay
            //! zwrite: on
            //! ztest: always
            @vertex fn vs_main() -> @builtin(position) vec4<f32> { return vec4<f32>(0.0); }
            """);

        Assert.Equal(ShaderBlendMode.Invert, asset.Blend);
        Assert.Equal(ShaderRenderQueue.Overlay, asset.Queue);
        Assert.Equal(ShaderDepthWrite.On, asset.DepthWrite);
        Assert.Equal(ShaderDepthCompare.Always, asset.DepthCompare);
    }

    [Fact]
    public async Task A_shader_without_directives_leaves_every_slot_unspecified()
    {
        ShaderAsset asset = await ImportAsync(
            "@vertex fn vs_main() -> @builtin(position) vec4<f32> { return vec4<f32>(0.0); }");

        Assert.Equal(ShaderBlendMode.Unspecified, asset.Blend);
        Assert.Equal(ShaderRenderMode.Unspecified, asset.Render);
        Assert.Equal(0, asset.Queue);
        Assert.Equal(ShaderDepthWrite.Unspecified, asset.DepthWrite);
        Assert.Equal(ShaderDepthCompare.Unspecified, asset.DepthCompare);
    }

    [Fact]
    public async Task Baked_directives_survive_the_artifact_round_trip()
    {
        ShaderAsset imported = await ImportAsync("""
            //! render: opaque
            //! blend: alpha
            //! zwrite: off
            //! ztest: less
            @vertex fn vs_main() -> @builtin(position) vec4<f32> { return vec4<f32>(0.0); }
            """);

        string store = Path.Combine(_root, "Artifacts");
        Directory.CreateDirectory(store);
        const string key = "Shaders/Directives.asset";
        await TestArtifacts.At(store).SaveAssetAsync(new AssetKey(key), ImporterUtils.FromClr(imported, CatalogStub.Schemas));

        var resolver = new WorldAssetResolver(AssetStorage.FromDirectory(store));
        Assert.True(resolver.TryLoad(new AssetReference<ShaderAsset>(key), out ShaderAsset? loaded));

        Assert.Equal(ShaderRenderMode.Opaque, loaded!.Render);
        Assert.Equal(ShaderBlendMode.Alpha, loaded.Blend);
        Assert.Equal(ShaderDepthWrite.Off, loaded.DepthWrite);
        Assert.Equal(ShaderDepthCompare.Less, loaded.DepthCompare);
    }

    [Fact]
    public void Vec4_parameter_is_imported_as_a_color_with_hex_default()
    {
        ShaderParam[] parameters = WgslShaderParams.Parse("""
            struct Params {
                exposure : f32, // default 1.25
                tint : vec4<f32>, // default #ff8040cc
            };
            @group(0) @binding(3) var<uniform> params : Params;
            """);

        Assert.Equal(2, parameters.Length);
        Assert.Equal(ShaderParamKind.Float, parameters[0].Kind);
        Assert.Equal(1.25f, parameters[0].Default);
        Assert.Equal(ShaderParamKind.Color, parameters[1].Kind);
        Assert.Equal(1f, parameters[1].Default);
        Assert.Equal(128f / 255f, parameters[1].DefaultG, precision: 5);
        Assert.Equal(64f / 255f, parameters[1].DefaultB, precision: 5);
        Assert.Equal(204f / 255f, parameters[1].DefaultA, precision: 5);
    }

    [Fact]
    public void Globals_are_read_from_the_group_one_declaration_only()
    {
        const string wgsl = """
            struct Params {
                cutoff : f32, // default 0.5
            };
            @group(0) @binding(3) var<uniform> params : Params;

            struct Globals {
                fogColor : vec4<f32>, // default #8fb4c8ff
                fogDensity : f32, // default 0.02
            };
            @group(1) @binding(0) var<uniform> globals : Globals;
            """;

        ShaderParam[] parameters = WgslShaderParams.Parse(wgsl);
        Assert.Equal("cutoff", Assert.Single(parameters).Name);

        ShaderParam[] globals = WgslShaderParams.ParseGlobals(wgsl);
        Assert.Equal(2, globals.Length);
        Assert.Equal("fogColor", globals[0].Name);
        Assert.Equal(ShaderParamKind.Color, globals[0].Kind);
        Assert.Equal(143f / 255f, globals[0].Default, precision: 5);
        Assert.Equal("fogDensity", globals[1].Name);
        Assert.Equal(ShaderParamKind.Float, globals[1].Kind);
        Assert.Equal(0.02f, globals[1].Default);
    }

    [Fact]
    public void A_shader_without_a_group_one_declaration_has_no_globals()
        => Assert.Empty(WgslShaderParams.ParseGlobals("""
            struct Params {
                cutoff : f32,
            };
            @group(0) @binding(3) var<uniform> params : Params;
            """));

    [Fact]
    public async Task Baked_globals_survive_the_artifact_round_trip()
    {
        ShaderAsset imported = await ImportAsync("""
            struct Globals {
                fogColor : vec4<f32>, // default #8fb4c8ff
            };
            @group(1) @binding(0) var<uniform> globals : Globals;
            @vertex fn vs_main() -> @builtin(position) vec4<f32> { return vec4<f32>(0.0); }
            """);

        string store = Path.Combine(_root, "Artifacts");
        Directory.CreateDirectory(store);
        const string key = "Shaders/Globals.asset";
        await TestArtifacts.At(store).SaveAssetAsync(new AssetKey(key), ImporterUtils.FromClr(imported, CatalogStub.Schemas));

        var resolver = new WorldAssetResolver(AssetStorage.FromDirectory(store));
        Assert.True(resolver.TryLoad(new AssetReference<ShaderAsset>(key), out ShaderAsset? loaded));

        ShaderParam declaration = Assert.Single(loaded!.Globals);
        Assert.Equal("fogColor", declaration.Name);
        Assert.Equal(ShaderParamKind.Color, declaration.Kind);
        Assert.Equal(1f, declaration.DefaultA);
    }

    private async Task<ShaderAsset> ImportAsync(string wgsl)
    {
        string assets = Path.Combine(_root, "Assets");
        Directory.CreateDirectory(assets);
        string source = Path.Combine(assets, Guid.NewGuid().ToString("N") + ".wgsl");
        await File.WriteAllTextAsync(source, wgsl);

        AssetImportResult result = await new ShaderImporter(CatalogStub.Schemas).ImportAsync(
            new AssetImportRequest(source, Path.GetFileName(source)));

        return await AuthoringTestHelpers.ResolveAsync<ShaderAsset>(
            assets, AuthoringTestHelpers.AssetOf(result));
    }
}
