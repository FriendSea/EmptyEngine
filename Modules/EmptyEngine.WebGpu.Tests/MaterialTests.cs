using Xunit;

namespace EmptyEngine.WebGpu.Tests;

/// <summary>マテリアルの描画状態の解決と、頂点シェーダの決め方の確認</summary>
public sealed class MaterialTests
{
    private static ShaderAsset Shader(bool vertex, params (int Location, string Type)[] inputs) => new()
    {
        HasVertex = vertex,
        HasFragment = true,
        VertexInputs = inputs.Select(i => new ShaderVertexInput { Location = i.Location, Type = i.Type }).ToArray(),
    };

    private static ShaderAsset QuadShader() => Shader(true, (0, "vec2<f32>"), (1, "vec2<f32>"));

    private static ShaderAsset MeshShader() => Shader(true, (0, "vec3<f32>"), (1, "vec3<f32>"), (2, "vec2<f32>"));

    private static ShaderAsset EffectShader() => Shader(true);

    private static ShaderAsset FragmentOnly() => Shader(false);

    [Fact]
    public void An_opaque_material_does_not_blend()
        => Assert.Null(ShaderBlend.Resolve(MaterialBlend.Opaque));

    [Fact]
    public void Alpha_blend_weights_colour_by_source_alpha()
    {
        (BlendComponent color, BlendComponent alpha) = Assert.NotNull(ShaderBlend.Resolve(MaterialBlend.Alpha));

        Assert.Equal(BlendFactor.SrcAlpha, color.SrcFactor);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, color.DstFactor);
        Assert.Equal(BlendFactor.One, alpha.SrcFactor);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, alpha.DstFactor);
    }

    [Theory]
    [InlineData(MaterialBlend.Invert, BlendFactor.OneMinusDst, BlendFactor.Zero)]
    [InlineData(MaterialBlend.DstAlphaMask, BlendFactor.OneMinusDstAlpha, BlendFactor.DstAlpha)]
    public void Special_blends_resolve_to_their_colour_factors(MaterialBlend blend, BlendFactor source, BlendFactor destination)
    {
        (BlendComponent color, _) = Assert.NotNull(ShaderBlend.Resolve(blend));

        Assert.Equal(source, color.SrcFactor);
        Assert.Equal(destination, color.DstFactor);
    }

    [Theory]
    [InlineData(MaterialDepthCompare.LessEqual, CompareFunction.LessEqual)]
    [InlineData(MaterialDepthCompare.Less, CompareFunction.Less)]
    [InlineData(MaterialDepthCompare.Always, CompareFunction.Always)]
    public void Depth_compare_resolves_to_the_matching_function(MaterialDepthCompare compare, CompareFunction expected)
        => Assert.Equal(expected, ShaderBlend.Resolve(compare));

    [Fact]
    public void A_new_material_is_an_opaque_depth_writing_geometry_material()
    {
        var material = new MaterialAsset();

        Assert.Equal(MaterialBlend.Opaque, material.Blend);
        Assert.True(material.DepthWrite);
        Assert.Equal(MaterialDepthCompare.LessEqual, material.DepthCompare);
        Assert.Equal(ShaderRenderQueue.Geometry, material.Queue);
    }

    [Fact]
    public void The_components_own_shader_wins_over_the_material_and_the_built_in()
    {
        ShaderAsset component = QuadShader();
        ShaderAsset material = QuadShader();
        ShaderAsset builtin = QuadShader();

        Assert.Same(component, MaterialRenderSupport.SelectVertexShader(MaterialVertexKind.Quad, component, material, builtin));
    }

    [Fact]
    public void Without_a_component_shader_the_material_shaders_vertex_stage_is_used()
    {
        ShaderAsset material = QuadShader();

        Assert.Same(material, MaterialRenderSupport.SelectVertexShader(MaterialVertexKind.Quad, null, material, QuadShader()));
    }

    [Fact]
    public void A_fragment_only_material_shader_falls_through_to_the_built_in_without_a_warning()
    {
        ShaderAsset builtin = QuadShader();
        var warnings = new List<string>();

        ShaderAsset? selected = MaterialRenderSupport.SelectVertexShader(
            MaterialVertexKind.Quad, null, FragmentOnly(), builtin, warnings.Add);

        Assert.Same(builtin, selected);
        Assert.Empty(warnings);
    }

    [Fact]
    public void A_component_shader_whose_inputs_do_not_fit_is_skipped_with_a_warning()
    {
        ShaderAsset material = QuadShader();
        var warnings = new List<string>();

        ShaderAsset? selected = MaterialRenderSupport.SelectVertexShader(
            MaterialVertexKind.Quad, MeshShader(), material, QuadShader(), warnings.Add);

        Assert.Same(material, selected);
        Assert.Single(warnings);
    }

    [Fact]
    public void A_material_shader_whose_inputs_do_not_fit_is_skipped_with_a_warning()
    {
        ShaderAsset builtin = EffectShader();
        var warnings = new List<string>();

        // スプライト用に書かれた VS+FS のシェーダを Effect のマテリアルに使うと、FS だけが使われる。
        ShaderAsset? selected = MaterialRenderSupport.SelectVertexShader(
            MaterialVertexKind.Effect, null, QuadShader(), builtin, warnings.Add);

        Assert.Same(builtin, selected);
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData((int)MaterialVertexKind.Quad, true)]
    [InlineData((int)MaterialVertexKind.Mesh, false)]
    [InlineData((int)MaterialVertexKind.Line, false)]
    [InlineData((int)MaterialVertexKind.Effect, false)]
    public void A_quad_vertex_shader_fits_only_the_quad_layout(int kind, bool fits)
        => Assert.Equal(fits, MaterialRenderSupport.Fits((MaterialVertexKind)kind, QuadShader()));

    [Fact]
    public void A_vertex_shader_may_take_only_some_of_the_attributes()
    {
        ShaderAsset positionOnly = Shader(true, (0, "vec3<f32>"));

        Assert.True(MaterialRenderSupport.Fits(MaterialVertexKind.Line, positionOnly));
        Assert.True(MaterialRenderSupport.Fits(MaterialVertexKind.Mesh, positionOnly));
        Assert.False(MaterialRenderSupport.Fits(MaterialVertexKind.Quad, positionOnly));
    }

    [Fact]
    public void An_effect_takes_only_a_vertex_shader_without_attributes()
    {
        Assert.True(MaterialRenderSupport.Fits(MaterialVertexKind.Effect, EffectShader()));
        Assert.False(MaterialRenderSupport.Fits(MaterialVertexKind.Quad, EffectShader()));
        Assert.False(MaterialRenderSupport.Fits(MaterialVertexKind.Effect, FragmentOnly()));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 16)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    public void Param_buffers_round_up_to_sixteen_bytes(int valueCount, int expected)
        => Assert.Equal(expected, MaterialRenderSupport.ParamBufferSize(valueCount));
}
