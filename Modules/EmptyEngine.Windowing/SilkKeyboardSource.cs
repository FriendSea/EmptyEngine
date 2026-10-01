using Silk.NET.Input;

namespace EmptyEngine.Windowing;

/// <summary>desktop 向けのキー供給元</summary>
internal sealed class SilkKeyboardSource : IKeyboardSource
{
    private static readonly Key[] Map = BuildMap();

    private readonly IKeyboard? _keyboard;

    internal SilkKeyboardSource(IInputContext context)
    {
        _keyboard = context.Keyboards.Count > 0 ? context.Keyboards[0] : null;
    }

    public bool IsKeyDown(KeyboardKey key)
    {
        Key silk = Map[(int)key];
        return silk != Key.Unknown && (_keyboard?.IsKeyPressed(silk) ?? false);
    }

    private static Key[] BuildMap()
    {
        KeyboardKey[] keys = Enum.GetValues<KeyboardKey>();
        var map = new Key[keys.Length];
        foreach (KeyboardKey key in keys)
        {
            map[(int)key] = Enum.TryParse(key.ToString(), out Key silk) ? silk : Key.Unknown;
        }

        return map;
    }
}
