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

/// <summary>WGSL が宣言するインターフェースが取り込み時に値へ変わり、ランタイムまで値のまま届くことの確認</summary>
public sealed class ShaderInterfaceTests : IDisposable
{
    private const string FullShader = """
        struct VertexParams {
            sway : f32, // default 0.25
        };
        @group(0) @binding(3) var<uniform> vertexParams : VertexParams;

        struct Params {
            cutoff : f32, // default 0.5
            tint : vec4<f32>, // default #ff8040cc
        };
        @group(2) @binding(0) var<uniform> params : Params;
        @group(2) @binding(1) var extraSampler : sampler;
        @group(2) @binding(3) var noise : texture_2d<f32>;
        @group(2) @binding(2) var mask : texture_2d<f32>;

        @group(3) @binding(0) var tex : texture_2d<f32>;
        @group(3) @binding(1) var samp : sampler;

        @vertex
        fn vs_main(@location(0) position : vec3<f32>, @location(2) uv : vec2f) -> @builtin(position) vec4<f32> {
            return vec4<f32>(position, 1.0);
        }

        @fragment
        fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(1.0); }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ee-shader-interface-" + Guid.NewGuid().ToString("N"));

    public ShaderInterfaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Component_side_and_material_side_params_are_read_from_their_own_groups()
    {
        ShaderParam vertex = Assert.Single(WgslShaderParams.ParseVertexParams(FullShader));
        Assert.Equal("sway", vertex.Name);
        Assert.Equal(0.25f, vertex.Default);

        ShaderParam[] fragment = WgslShaderParams.ParseFragmentParams(FullShader);
        Assert.Equal(["cutoff", "tint"], fragment.Select(p => p.Name));
        Assert.Equal(0.5f, fragment[0].Default);
    }

    [Fact]
    public void Vec4_parameter_is_imported_as_a_color_with_hex_default()
    {
        ShaderParam tint = WgslShaderParams.ParseFragmentParams(FullShader)[1];

        Assert.Equal(ShaderParamKind.Color, tint.Kind);
        Assert.Equal(1f, tint.Default);
        Assert.Equal(128f / 255f, tint.DefaultG, precision: 5);
        Assert.Equal(64f / 255f, tint.DefaultB, precision: 5);
        Assert.Equal(204f / 255f, tint.DefaultA, precision: 5);
    }

    [Fact]
    public void Extra_texture_slots_are_the_group_two_textures_in_binding_order()
    {
        ShaderTextureSlot[] slots = WgslShaderParams.ParseTextureSlots(FullShader);

        // group(3) のメインテクスチャは枠に入らない。
        Assert.Equal(["mask", "noise"], slots.Select(s => s.Name));
        Assert.Equal([2, 3], slots.Select(s => s.Binding));
    }

    [Fact]
    public void Vertex_inputs_are_read_from_direct_arguments()
    {
        ShaderVertexInput[] inputs = WgslShaderParams.ParseVertexInputs(FullShader);

        Assert.Equal([0, 2], inputs.Select(i => i.Location));
        Assert.Equal(["vec3<f32>", "vec2<f32>"], inputs.Select(i => i.Type));
    }

    [Fact]
    public void Vertex_inputs_are_read_through_a_struct_argument()
    {
        ShaderVertexInput[] inputs = WgslShaderParams.ParseVertexInputs("""
            struct VsIn {
                @location(1) uv : vec2<f32>,
                @builtin(vertex_index) index : u32,
                @location(0) position : vec2<f32>,
            };
            @vertex fn vs_main(in : VsIn) -> @builtin(position) vec4<f32> { return vec4<f32>(0.0); }
            """);

        Assert.Equal([0, 1], inputs.Select(i => i.Location));
        Assert.All(inputs, input => Assert.Equal("vec2<f32>", input.Type));
    }

    [Fact]
    public void A_vertex_shader_built_from_the_vertex_index_takes_no_inputs()
        => Assert.Empty(WgslShaderParams.ParseVertexInputs(
            "@vertex fn vs_main(@builtin(vertex_index) index : u32) -> @builtin(position) vec4<f32> { return vec4<f32>(0.0); }"));

    [Theory]
    [InlineData("@vertex fn vs_main() -> @builtin(position) vec4<f32> { return vec4<f32>(0.0); }", true, false)]
    [InlineData("@fragment fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(1.0); }", false, true)]
    [InlineData("// @vertex fn vs_main() {}\n@fragment fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(1.0); }", false, true)]
    [InlineData("@vertex fn other() -> @builtin(position) vec4<f32> { return vec4<f32>(0.0); }", false, false)]
    public void Stages_are_the_entry_points_the_source_declares(string wgsl, bool vertex, bool fragment)
    {
        Assert.Equal(vertex, WgslShaderParams.HasVertexEntry(wgsl));
        Assert.Equal(fragment, WgslShaderParams.HasFragmentEntry(wgsl));
    }

    [Fact]
    public async Task The_whole_interface_survives_the_artifact_round_trip()
    {
        ShaderAsset imported = await ImportAsync(FullShader);

        string store = Path.Combine(_root, "Artifacts");
        Directory.CreateDirectory(store);
        const string key = "Shaders/Interface.asset";
        await TestArtifacts.At(store).SaveAssetAsync(new AssetKey(key), ImporterUtils.FromClr(imported, CatalogStub.Schemas));

        var resolver = new WorldAssetResolver(AssetStorage.FromDirectory(store));
        Assert.True(resolver.TryLoad(new AssetReference<ShaderAsset>(key), out ShaderAsset? loaded));

        Assert.True(loaded!.HasVertex);
        Assert.True(loaded.HasFragment);
        Assert.Equal(["0:vec3<f32>", "2:vec2<f32>"], loaded.VertexInputs.Select(i => $"{i.Location}:{i.Type}"));
        Assert.Equal("sway", Assert.Single(loaded.VertexParams).Name);
        Assert.Equal(["cutoff", "tint"], loaded.FragmentParams.Select(p => p.Name));
        Assert.Equal([2, 3], loaded.TextureSlots.Select(s => s.Binding));
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
