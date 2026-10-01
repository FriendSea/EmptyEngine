using NVorbis;

namespace EmptyEngine.Audio;

/// <summary>クリップ本体（.ogg）の 16bit PCM への展開</summary>
internal static class VorbisDecoder
{
    public static bool TryDecode(byte[] file, out byte[] pcm, out int channels, out int sampleRate)
    {
        pcm = [];
        channels = 0;
        sampleRate = 0;

        try
        {
            using var input = new MemoryStream(file, writable: false);
            using var ogg = new VorbisReader(input, closeOnDispose: false);
            channels = ogg.Channels;
            sampleRate = ogg.SampleRate;
            if (channels <= 0 || sampleRate <= 0)
            {
                return false;
            }

            using var output = new MemoryStream();
            float[] buffer = new float[channels * 4096];
            int read;
            while ((read = ogg.ReadSamples(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    int s = (int)(buffer[i] * 32767f);
                    s = Math.Clamp(s, short.MinValue, short.MaxValue);
                    output.WriteByte((byte)(s & 0xFF));
                    output.WriteByte((byte)((s >> 8) & 0xFF));
                }
            }

            pcm = output.ToArray();
            return pcm.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
