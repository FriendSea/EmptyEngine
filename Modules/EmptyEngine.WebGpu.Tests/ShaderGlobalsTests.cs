using Xunit;
using GraphicsColor = EmptyEngine.Graphics.Color;

namespace EmptyEngine.WebGpu.Tests;

public sealed class ShaderGlobalsTests
{
    private static ShaderParam[] Declarations =>
    [
        new ShaderParam { Name = "fogDensity", Kind = ShaderParamKind.Float, Default = 0.25f },
        new ShaderParam
        {
            Name = "fogColor",
            Kind = ShaderParamKind.Color,
            Default = 0.1f,
            DefaultG = 0.2f,
            DefaultB = 0.3f,
            DefaultA = 1f,
        },
    ];

    [Fact]
    public void Values_land_on_the_offsets_the_shader_declaration_implies()
    {
        var globals = new ShaderGlobals();
        globals.SetFloat("fogDensity", 0.75f);
        globals.SetColor("fogColor", new GraphicsColor(1f, 0.5f, 0.25f, 0.5f));

        // vec4 は 16 byte 境界へ寄るので、f32 1 つのあとに 3 float 分の穴が空く。
        Span<float> packed = stackalloc float[8];
        globals.Pack(Declarations, packed);

        Assert.Equal(0.75f, packed[0]);
        Assert.Equal(0f, packed[1]);
        Assert.Equal(0f, packed[2]);
        Assert.Equal(0f, packed[3]);
        Assert.Equal(new[] { 1f, 0.5f, 0.25f, 0.5f }, packed[4..8].ToArray());
    }

    [Fact]
    public void Unset_names_fall_back_to_the_declared_defaults()
    {
        var globals = new ShaderGlobals();
        globals.SetFloat("fogDensity", 0.75f);

        Span<float> packed = stackalloc float[8];
        globals.Pack(Declarations, packed);

        Assert.Equal(0.75f, packed[0]);
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f, 1f }, packed[4..8].ToArray());
    }

    [Fact]
    public void Names_the_shader_never_declared_do_not_reach_the_buffer()
    {
        var globals = new ShaderGlobals();
        globals.SetFloat("windSpeed", 3f);

        Span<float> packed = stackalloc float[8];
        globals.Pack(Declarations, packed);

        Assert.Equal(0.25f, packed[0]);
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f, 1f }, packed[4..8].ToArray());
    }

    [Fact]
    public void Writing_the_same_value_again_does_not_advance_the_version()
    {
        var globals = new ShaderGlobals();
        globals.SetColor("fogColor", GraphicsColor.White);
        int version = globals.Version;

        globals.SetColor("fogColor", GraphicsColor.White);
        Assert.Equal(version, globals.Version);

        globals.SetColor("fogColor", GraphicsColor.Black);
        Assert.NotEqual(version, globals.Version);
    }

    [Fact]
    public void Removing_a_value_advances_the_version_and_restores_the_default()
    {
        var globals = new ShaderGlobals();
        globals.SetFloat("fogDensity", 0.75f);
        int version = globals.Version;

        Assert.True(globals.Remove("fogDensity"));
        Assert.NotEqual(version, globals.Version);
        Assert.False(globals.Remove("fogDensity"));

        Span<float> packed = stackalloc float[8];
        globals.Pack(Declarations, packed);
        Assert.Equal(0.25f, packed[0]);
    }

    [Fact]
    public void Set_values_read_back_by_name()
    {
        var globals = new ShaderGlobals();
        globals.SetColor("fogColor", new GraphicsColor(0.2f, 0.4f, 0.6f, 0.8f));

        Assert.True(globals.TryGetColor("fogColor", out GraphicsColor color));
        Assert.Equal(new GraphicsColor(0.2f, 0.4f, 0.6f, 0.8f), color);
        Assert.False(globals.TryGetFloat("fogDensity", out float density));
        Assert.Equal(0f, density);
    }

    [Fact]
    public void Worlds_keep_their_own_values()
    {
        var left = new RenderWorld();
        var right = new RenderWorld();

        left.Globals.SetFloat("fogDensity", 0.5f);

        Assert.True(left.Globals.TryGetFloat("fogDensity", out float density));
        Assert.Equal(0.5f, density);
        Assert.False(right.Globals.TryGetFloat("fogDensity", out _));
    }
}
