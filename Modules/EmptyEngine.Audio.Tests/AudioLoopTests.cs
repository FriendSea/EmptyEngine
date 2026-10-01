using EmptyEngine.Modules.Testing;
using EmptyEngine.Audio.Editor;
using EmptyEngine.Editor;
using Xunit;

namespace EmptyEngine.Audio.Tests;

/// <summary>WAV のループ指定が取り込み・保存・多層再生を通して保たれることの検証</summary>
public sealed class AudioLoopTests : IDisposable
{
    private const int SampleRate = 44100;
    private const int FrameCount = 8192;
    private const int LoopStart = 1024;
    private const int LoopEnd = 4096;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ee-audio-loop-" + Guid.NewGuid().ToString("N"));

    public AudioLoopTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Import_and_artifact_round_trip_preserve_loop_points_and_layers()
    {
        AudioClipAsset clip = await Import();

        Assert.Equal(LoopStart, clip.LoopStartFrame);
        Assert.Equal(LoopEnd, clip.LoopEndFrame);
        Assert.Equal(3, clip.LayerLengths.Length);
        using LayeredAudioStream stream = LayeredAudioStream.TryOpen(clip)!;
        Assert.NotNull(stream);
        Assert.Equal(LoopStart, stream.LoopStartFrame);
        Assert.Equal(LoopEnd, stream.LoopEndFrame);
    }

    [Fact]
    public async Task Import_rescales_loop_points_when_resampling()
    {
        AudioClipAsset clip = await Import(sampleRate: 22050, channels: 2);

        Assert.Equal(LoopStart * 2, clip.LoopStartFrame);
        Assert.Equal(LoopEnd * 2, clip.LoopEndFrame);
        using LayeredAudioStream stream = LayeredAudioStream.TryOpen(clip)!;
        Assert.NotNull(stream);
        Assert.Equal(SampleRate, stream.SampleRate);
        Assert.Equal(LoopEnd * 2, stream.LoopEndFrame);
    }

    [Theory]
    [InlineData(false, 1024, 4095, 0)]
    [InlineData(true, 5000, 4095, 0)]
    [InlineData(true, 1024, 9000, 0)]
    [InlineData(true, 1024, 4095, 1)]
    public async Task Missing_or_unsupported_loop_falls_back_to_the_whole_clip(bool hasLoop, int start, int end, int type)
    {
        AudioClipAsset clip = await Import(hasLoop: hasLoop, start: start, end: end, loopType: type);

        Assert.Equal(0, clip.LoopStartFrame);
        Assert.Equal(0, clip.LoopEndFrame);
        using LayeredAudioStream stream = LayeredAudioStream.TryOpen(clip)!;
        Assert.NotNull(stream);
        Assert.Equal(0, stream.LoopStartFrame);
        Assert.Equal(FrameCount, stream.LoopEndFrame);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Every_layer_wraps_to_the_loop_start_without_replaying_the_intro(int layer)
    {
        AudioClipAsset clip = await Import();
        using LayeredAudioStream stream = LayeredAudioStream.TryOpen(clip)!;
        using LayeredAudioStream expected = LayeredAudioStream.TryOpen(clip)!;
        for (int i = 0; i < stream.LayerCount; i++)
        {
            stream.SetLayerVolume(i, i == layer ? 1f : 0f);
            expected.SetLayerVolume(i, i == layer ? 1f : 0f);
        }

        const int beforeEnd = 128;
        const int afterEnd = 256;
        stream.Seek((LoopEnd - beforeEnd) / (double)SampleRate);
        float[] actual = new float[(beforeEnd + afterEnd) * 2];
        stream.Read(actual);
        Assert.Equal(LoopStart + afterEnd, stream.Position);

        expected.Seek(LoopStart / (double)SampleRate);
        float[] nextLoop = new float[afterEnd * 2];
        expected.Read(nextLoop);
        Assert.Equal(nextLoop, actual[(beforeEnd * 2)..]);

        // 大きいバッファの中で複数回ループしてもイントロやループ後の末尾へ進まない。
        stream.Read(new float[(3 * (LoopEnd - LoopStart) + 17) * 2]);
        Assert.Equal(LoopStart + afterEnd + 17, stream.Position);
    }

    [Fact]
    public async Task Explicit_start_at_zero_keeps_the_intro_and_time_wraps_at_the_loop_end()
    {
        AudioClipAsset clip = await Import();
        using LayeredAudioStream stream = LayeredAudioStream.TryOpen(clip)!;
        stream.Seek(0);
        stream.Read(new float[256 * 2]);
        Assert.Equal(256, stream.Position);
        Assert.Equal(256, stream.WrapFrame(256));
        Assert.Equal(LoopStart, stream.WrapFrame(LoopEnd));
        Assert.Equal(LoopStart + 75, stream.WrapFrame(LoopEnd + 75));
        Assert.Equal(LoopStart + 75, stream.WrapFrame(LoopEnd + 2 * stream.LoopFrames + 75));
        stream.Seek((LoopEnd + 75) / (double)SampleRate);
        Assert.Equal(LoopStart + 75, stream.Position);
    }

    [Fact]
    public async Task Without_loop_the_stream_passes_the_loop_end_and_stops_at_the_clip_end()
    {
        AudioClipAsset clip = await Import();
        using LayeredAudioStream stream = LayeredAudioStream.TryOpen(clip)!;
        using LayeredAudioStream expected = LayeredAudioStream.TryOpen(clip)!;
        stream.Loop = false;
        expected.Loop = false;

        const int beforeEnd = 128;
        stream.Seek((LoopEnd - beforeEnd) / (double)SampleRate);
        float[] acrossLoopEnd = new float[beforeEnd * 2 * 2];
        Assert.Equal(beforeEnd * 2, stream.Read(acrossLoopEnd));
        Assert.Equal(LoopEnd + beforeEnd, stream.Position);
        expected.Seek(LoopEnd / (double)SampleRate);
        float[] afterLoopEnd = new float[beforeEnd * 2];
        expected.Read(afterLoopEnd);
        Assert.Equal(afterLoopEnd, acrossLoopEnd[(beforeEnd * 2)..]);

        const int tail = 100;
        stream.Seek((FrameCount - tail) / (double)SampleRate);
        float[] pastEnd = new float[(tail + 50) * 2];
        Assert.Equal(tail, stream.Read(pastEnd));
        Assert.True(stream.Ended);
        Assert.Equal(FrameCount, stream.Position);
        Assert.All(pastEnd[(tail * 2)..], sample => Assert.Equal(0f, sample));
        Assert.Equal(0, stream.Read(new float[64]));
        Assert.Equal(FrameCount, stream.WrapFrame(FrameCount + 75));
    }

    private async Task<AudioClipAsset> Import(int sampleRate = SampleRate, int channels = 6,
        bool hasLoop = true, int start = LoopStart, int end = LoopEnd - 1, int loopType = 0)
    {
        string path = Path.Combine(_root, "music.wav");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            int dataLength = FrameCount * channels * sizeof(short);
            writer.Write("RIFF"u8);
            writer.Write(36 + dataLength + (hasLoop ? 68 : 0));
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * sizeof(short));
            writer.Write((short)(channels * sizeof(short)));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataLength);
            for (int frame = 0; frame < FrameCount; frame++)
                for (int channel = 0; channel < channels; channel++)
                    writer.Write((short)(10000 * Math.Sin(frame * (channel + 1) * 0.025)));

            // 実際の BGM と同様、音声データより後ろに smpl を置く。
            if (hasLoop)
            {
                writer.Write("smpl"u8);
                writer.Write(60);
                foreach (int value in new[] { 0, 0, 0, 60, 0, 0, 0, 1, 0, 0, loopType, start, end, 0, 0 })
                    writer.Write(value);
            }
        }

        var result = await new WavAudioImporter(CatalogStub.Schemas)
            .ImportAsync(new AssetImportRequest(path, "music.wav", _root));
        Assert.True(result.Success, result.Message);
        return await AuthoringTestHelpers.ResolveAsync<AudioClipAsset>(_root, AuthoringTestHelpers.AssetOf(result));
    }
}
