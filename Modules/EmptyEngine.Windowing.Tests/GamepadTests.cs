using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Silk.NET.Input;
using Xunit;

namespace EmptyEngine.Windowing.Tests;

public sealed class GamepadTests
{
    [Fact]
    public void DeviceCopiesSnapshotsAndClearsDisconnectedPads()
    {
        var device = new GamepadDevice();
        Assert.True(device.Gamepads.IsEmpty);
        GamepadState[] states = [new(7, GamepadButtons.South, Vector2.UnitY, Vector2.Zero, 0, 1)];
        device.Set(states);
        states[0] = default;
        Assert.Equal(7, device.Gamepads[0].Id);
        Assert.True(device.Gamepads[0].IsButtonDown(GamepadButtons.South));
        Assert.False(device.Gamepads[0].IsButtonDown(GamepadButtons.None));
        Assert.False(device.Gamepads[0].IsButtonDown(GamepadButtons.South | GamepadButtons.East));
        device.Set([]);
        Assert.True(device.Gamepads.IsEmpty);
    }

    [Fact]
    public void NativeLayoutMatchesBrowserBridge()
    {
        Assert.Equal(32, Marshal.SizeOf<GamepadState>());
        GamepadState[] states = [new(7, GamepadButtons.Start, new(0.5f, -1f), new(1f, -0.5f), 0.25f, 1f)];
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(states.AsSpan());
        Assert.Equal(7, BitConverter.ToInt32(bytes[..4]));
        Assert.Equal((uint)GamepadButtons.Start, BitConverter.ToUInt32(bytes[4..8]));
        Assert.Equal(0.5f, BitConverter.ToSingle(bytes[8..12]));
        Assert.Equal(-1f, BitConverter.ToSingle(bytes[12..16]));
        Assert.Equal(0.25f, BitConverter.ToSingle(bytes[24..28]));
        Assert.Equal(1f, BitConverter.ToSingle(bytes[28..32]));
    }

    [Fact]
    public void DesktopNormalizesAxesAndTracksHotplugWithoutRenumbering()
    {
        var pads = new List<IGamepad>();
        IInputContext context = Stub<IInputContext>(name => name == "get_Gamepads" ? pads : throw new NotSupportedException(name));
        bool connected = true;
        IGamepad pad = Stub<IGamepad>(name => name switch
        {
            "get_IsConnected" => connected,
            "get_Index" => 4,
            "get_Buttons" => new Button[] { new(ButtonName.A, 0, true), new(ButtonName.DPadUp, 11, true) },
            "get_Thumbsticks" => new Thumbstick[] { new(0, 0.5f, -1f), new(1, -0.5f, 1f) },
            "get_Triggers" => new Trigger[] { new(0, -1f), new(1, 1f) },
            _ => throw new NotSupportedException(name),
        });
        var source = new SilkGamepadSource(context);
        var device = new GamepadDevice();
        source.Update(device);
        Assert.True(device.Gamepads.IsEmpty);
        pads.Add(pad);
        source.Update(device);
        GamepadState state = device.Gamepads[0];
        Assert.Equal(4, state.Id);
        Assert.Equal(GamepadButtons.South | GamepadButtons.DPadUp, state.Buttons);
        Assert.Equal(new Vector2(0.5f, 1f), state.LeftStick);
        Assert.Equal(new Vector2(-0.5f, -1f), state.RightStick);
        Assert.Equal(0f, state.LeftTrigger);
        Assert.Equal(1f, state.RightTrigger);
        connected = false;
        source.Update(device);
        Assert.True(device.Gamepads.IsEmpty);
        connected = true;
        source.Update(device);
        Assert.Equal(4, device.Gamepads[0].Id);
    }

    private static T Stub<T>(Func<string, object> get) where T : class
    {
        T stub = DispatchProxy.Create<T, InputProxy>();
        ((InputProxy)(object)stub).Get = get;
        return stub;
    }

    public class InputProxy : DispatchProxy
    {
        public Func<string, object> Get { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Get(targetMethod!.Name);
    }
}
