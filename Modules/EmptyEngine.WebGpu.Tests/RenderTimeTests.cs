using Xunit;

namespace EmptyEngine.WebGpu.Tests;

public sealed class RenderTimeTests
{
    [Fact]
    public void Zero_steps_freeze_time_and_resume_without_catching_up()
    {
        var world = new RenderWorld();
        world.AdvanceTime(0.25);

        for (int frame = 0; frame < 600; frame++)
            world.AdvanceTime(0);

        Assert.Equal(0.25, world.Time);
        world.AdvanceTime(0.125);
        Assert.Equal(0.375, world.Time);
    }

    [Fact]
    public void Time_is_isolated_per_world_and_starts_at_zero()
    {
        var left = new RenderWorld();
        left.AdvanceTime(5);
        var right = new RenderWorld();

        Assert.Equal(0, right.Time);
        right.AdvanceTime(0.5);
        Assert.Equal(5, left.Time);
        Assert.Equal(0.5, right.Time);
    }

    [Fact]
    public void Fixed_steps_preserve_precision_over_long_sessions()
    {
        var world = new RenderWorld();
        const int frames = 60 * 60 * 60;

        for (int frame = 0; frame < frames; frame++)
            world.AdvanceTime(1.0 / 60.0);

        Assert.Equal(3600.0, world.Time, precision: 6);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Invalid_steps_do_not_corrupt_animation_time(double deltaSeconds)
    {
        var world = new RenderWorld();
        world.AdvanceTime(0.5);

        Assert.Throws<ArgumentOutOfRangeException>(() => world.AdvanceTime(deltaSeconds));
        Assert.Equal(0.5, world.Time);
    }
}
