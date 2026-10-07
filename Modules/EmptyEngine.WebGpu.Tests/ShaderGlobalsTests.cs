using System.Numerics;
using Xunit;
using GraphicsColor = EmptyEngine.Graphics.Color;

namespace EmptyEngine.WebGpu.Tests;

public sealed class ShaderGlobalsTests
{
    [Fact]
    public void Values_land_on_the_slot_they_were_set_to()
    {
        var globals = new ShaderGlobals();
        globals.SetFloat(1, 0.75f);
        globals.SetColor(0, new GraphicsColor(1f, 0.5f, 0.25f, 0.5f));

        Assert.Equal(new Vector4(1f, 0.5f, 0.25f, 0.5f), globals.Values[0]);
        Assert.Equal(new Vector4(0.75f, 0f, 0f, 0f), globals.Values[1]);
    }

    [Fact]
    public void Unset_slots_read_as_zero()
    {
        var globals = new ShaderGlobals();
        globals.SetFloat(3, 2f);

        Assert.Equal(ShaderGlobals.SlotCount, globals.Values.Length);
        Assert.Equal(Vector4.Zero, globals.Values[0]);
        Assert.Equal(Vector4.Zero, globals.GetVector(ShaderGlobals.SlotCount - 1));
    }

    [Fact]
    public void Writing_the_same_value_again_does_not_advance_the_version()
    {
        var globals = new ShaderGlobals();
        globals.SetColor(0, GraphicsColor.White);
        int version = globals.Version;

        globals.SetColor(0, GraphicsColor.White);
        Assert.Equal(version, globals.Version);

        globals.SetColor(0, GraphicsColor.Black);
        Assert.NotEqual(version, globals.Version);
    }

    [Fact]
    public void Clearing_a_slot_advances_the_version_and_restores_zero()
    {
        var globals = new ShaderGlobals();
        globals.SetFloat(1, 0.75f);
        int version = globals.Version;

        globals.Clear(1);

        Assert.NotEqual(version, globals.Version);
        Assert.Equal(Vector4.Zero, globals.GetVector(1));
    }

    [Fact]
    public void A_slot_outside_the_range_is_rejected()
    {
        var globals = new ShaderGlobals();

        Assert.Throws<IndexOutOfRangeException>(() => globals.SetFloat(ShaderGlobals.SlotCount, 1f));
    }

    [Fact]
    public void Worlds_keep_their_own_values()
    {
        var left = new RenderWorld();
        var right = new RenderWorld();

        left.Globals.SetFloat(1, 0.5f);

        Assert.Equal(0.5f, left.Globals.GetVector(1).X);
        Assert.Equal(0f, right.Globals.GetVector(1).X);
    }
}
