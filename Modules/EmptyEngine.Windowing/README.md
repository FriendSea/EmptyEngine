# EmptyEngine.Windowing

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

Windows and input. Handles the window with GLFW on desktop and with the browser on the web, and reads keyboard, mouse, gamepad, and touch input.

```cs
IRuntimeWindow window = RuntimeWindow.Create(new WindowSettings(960, 540, "MyGame"));
window.Update += delta =>
{
    bool jump = window.Keyboard.IsKeyDown(KeyboardKey.Space);
};
// Blocks until the process exits
window.Run();
```

## Attribution in distributed games

Depending on the platforms you build for, include the copyright notices and license texts of the following libraries in your game's credits or in a bundled text file.

- **Desktop, iOS**: Silk.NET (MIT), Microsoft.DotNet.PlatformAbstractions and Microsoft.Extensions.DependencyModel (MIT)
