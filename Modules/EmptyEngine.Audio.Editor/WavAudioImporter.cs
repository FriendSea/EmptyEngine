using System.Buffers.Binary;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OggVorbisEncoder;

namespace EmptyEngine.Audio.Editor;

/// <summary>WAV 音声ファイルの取り込みと Ogg Vorbis での焼き込み</summary>
public sealed class WavAudioImporter(ISchemaSource schemas) : IAssetImporter
{
    /// <summary>VBR 品質（-0.1〜1.0）</summary>
    private const float VorbisQuality = 0.4f;

    /// <summary>先頭の層の論理ビットストリームの通し番号</summary>
    private const int StreamSerial = 1;

    /// <summary>1 回の <see cref="ProcessingState.WriteData"/> へ渡すフレーム数</summary>
    private const int WriteBlockFrames = 1024;

    /// <summary>ステレオ経路が健全に動く下限サンプルレート</summary>
    private const int MinStereoSampleRate = 32000;

    /// <summary>低レート素材の引き上げ先</summary>
    private const int TargetSampleRate = 44100;

    public IReadOnlyCollection<string> SupportedExtensions => [".wav"];

    public Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default)
    {
        (float[][] samples, int sampleRate, long loopStart, long loopEnd) = ReadSamples(request.SourcePath);
        byte[][] layers = EncodeToOggVorbis(samples, sampleRate);
        byte[] ogg = [.. layers.SelectMany(layer => layer)];
        var asset = new AudioClipAsset
        {
            Source = new EncodedAssetBinary(ogg),
            LayerLengths = layers.Length > 1 ? [.. layers.Select(layer => layer.Length)] : [],
            LoopStartFrame = loopStart,
            LoopEndFrame = loopEnd,
        };

        var imported = new ImportedAsset(request.RelativePath, ImporterUtils.FromClr(asset, schemas), request.SourcePath);
        return Task.FromResult(AssetImportResult.Succeeded(
            $"Imported audio {request.RelativePath} (wav→ogg {ogg.Length} bytes, {layers.Length} layer(s))",
            imported));
    }

    /// <summary>チャンネルを 2 つずつ組にした層ごとの Ogg Vorbis への焼き込み</summary>
    /// <remarks>2 チャンネル以下は 1 層。組の余った 1 チャンネルは両耳に同じ音を置く</remarks>
    private static byte[][] EncodeToOggVorbis(float[][] samples, int sampleRate)
    {
        int layerCount = Math.Max(1, (samples.Length + 1) / 2);
        var layers = new byte[layerCount][];
        for (int layer = 0; layer < layerCount; layer++)
        {
            float[] left = samples[2 * layer];
            float[] right = 2 * layer + 1 < samples.Length ? samples[(2 * layer) + 1] : left;
            layers[layer] = Encode([left, right], sampleRate, StreamSerial + layer);
        }

        return layers;
    }

    private static (float[][] Samples, int SampleRate, long LoopStart, long LoopEnd) ReadSamples(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        int sampleRate = reader.WaveFormat.SampleRate;

        ISampleProvider source = reader.ToSampleProvider();
        if (sampleRate < MinStereoSampleRate)
        {
            source = new WdlResamplingSampleProvider(source, TargetSampleRate);
            sampleRate = TargetSampleRate;
        }

        int channels = source.WaveFormat.Channels;
        var interleaved = new List<float>((int)(reader.Length / 2));
        float[] chunk = new float[channels * WriteBlockFrames];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            interleaved.AddRange(chunk.AsSpan(0, read));
        }

        int frames = channels > 0 ? interleaved.Count / channels : 0;
        var samples = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            samples[c] = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                samples[c][i] = interleaved[(i * channels) + c];
            }
        }

        (long loopStart, long loopEnd) = ReadLoop(reader, sampleRate, frames);
        return (samples, sampleRate, loopStart, loopEnd);
    }

    /// <summary>WAV の smpl チャンクにある最初の有効な順方向ループを取り込む</summary>
    private static (long Start, long End) ReadLoop(WaveFileReader reader, int sampleRate, int frames)
    {
        foreach (RiffChunk chunk in reader.ExtraChunks)
        {
            if (chunk.IdentifierAsString != "smpl" || chunk.Length < 60)
                continue;

            byte[] data = reader.GetChunkData(chunk);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28));
            for (int offset = 36; count > 0 && offset <= data.Length - 24; offset += 24, count--)
            {
                ReadOnlySpan<byte> loop = data.AsSpan(offset, 24);
                if (BinaryPrimitives.ReadUInt32LittleEndian(loop[4..]) != 0)
                    continue;

                long start = BinaryPrimitives.ReadUInt32LittleEndian(loop[8..]);
                // smpl の終端は最後に鳴らすフレームを含む。
                long end = (long)BinaryPrimitives.ReadUInt32LittleEndian(loop[12..]) + 1;
                if (start >= end || end > reader.Length / reader.WaveFormat.BlockAlign)
                    continue;

                double ratio = sampleRate / (double)reader.WaveFormat.SampleRate;
                start = (long)Math.Round(start * ratio);
                end = Math.Min(frames, (long)Math.Round(end * ratio));
                if (start < end)
                    return (start, end);
            }
        }

        return (0, 0);
    }

    /// <remarks>エンコーダは先頭の半ブロックを出力しないので、その分の無音を前に足して頭とループの継ぎ目を合わせる</remarks>
    private static byte[] Encode(float[][] samples, int sampleRate, int serial)
    {
        int channels = samples.Length;
        int frames = channels > 0 ? samples[0].Length : 0;

        VorbisInfo info = VorbisInfo.InitVariableBitRate(channels, sampleRate, VorbisQuality);

        int lapFrames = info.CodecSetup.BlockSizes[1] / 2;
        var padded = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            padded[c] = new float[lapFrames + frames];
            samples[c].CopyTo(padded[c], lapFrames);
        }

        samples = padded;
        frames += lapFrames;

        var stream = new OggStream(serial);
        using var output = new MemoryStream();

        stream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        stream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
        stream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        while (stream.PageOut(out OggPage headerPage, true))
        {
            WritePage(output, headerPage);
        }

        ProcessingState state = ProcessingState.Create(info);
        int position = 0;
        bool endOfStreamWritten = false;

        while (!stream.Finished)
        {
            if (position >= frames)
            {
                if (endOfStreamWritten)
                {
                    break;
                }

                state.WriteEndOfStream();
                endOfStreamWritten = true;
            }
            else
            {
                int count = Math.Min(WriteBlockFrames, frames - position);
                var block = new float[channels][];
                for (int c = 0; c < channels; c++)
                {
                    block[c] = samples[c][position..(position + count)];
                }

                state.WriteData(block, count);
                position += count;
            }

            while (state.PacketOut(out OggPacket packet))
            {
                stream.PacketIn(packet);
                while (stream.PageOut(out OggPage page, false))
                {
                    WritePage(output, page);
                }
            }
        }

        while (stream.PageOut(out OggPage tailPage, true))
        {
            WritePage(output, tailPage);
        }

        return output.ToArray();
    }

    private static void WritePage(Stream output, OggPage page)
    {
        output.Write(page.Header, 0, page.Header.Length);
        output.Write(page.Body, 0, page.Body.Length);
    }
}
