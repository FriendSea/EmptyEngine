# EmptyEngine.Audio

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

Audio playback. Plays through OpenAL on desktop and Web Audio on the web, and decodes Ogg Vorbis.

```cs
// Play a one-shot sound
GameAudio.Play(clip);

// To loop, create a voice
IAudioVoice? voice = GameAudio.CreateVoice(clip);
voice?.Play(loop: true);
// ...
voice?.Dispose();
```

## Attribution in distributed games

Depending on the platforms you build for, include the copyright notices and license texts of the following libraries in your game's credits or in a bundled text file.

- **Desktop, Web, iOS**: NVorbis (MIT)
- **Desktop, iOS**: Silk.NET (MIT), Microsoft.DotNet.PlatformAbstractions and Microsoft.Extensions.DependencyModel (MIT)
- **Desktop**: OpenAL Soft (LGPL-2.1)

For OpenAL Soft, in addition to the notice, state where the source can be obtained (<https://github.com/kcat/openal-soft>), and distribute the native library next to the executable so that it can be replaced (for single-file publishing, set `IncludeNativeLibrariesForSelfExtract=false`).
