using System.Diagnostics;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EmptyEngine.Graphics.Editor;

/// <summary>Editor-side texture encoding.</summary>
public static class TextureEncoding
{
    private const string WindowsEncoderResource = "EmptyEngine.Graphics.Editor.basisu.win-x64.exe";
    private const string MacEncoderResource = "EmptyEngine.Graphics.Editor.basisu.osx-arm64";
    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private static readonly object EncoderGate = new();

    /// <summary>Encodes one image as single-level UASTC KTX2 without Zstandard.</summary>
    public static byte[] EncodeBasisUniversal(Image<Rgba32> image)
    {
        string encoder = EnsureEncoder();
        string stem = Path.Combine(Path.GetTempPath(), $"ee-basis-{Guid.NewGuid():N}");
        string input = stem + ".png";
        string output = stem + ".ktx2";

        try
        {
            image.SaveAsPng(input);

            var start = new ProcessStartInfo(encoder)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-uastc");
            start.ArgumentList.Add("-uastc_level");
            start.ArgumentList.Add("2");
            start.ArgumentList.Add("-ktx2");
            start.ArgumentList.Add("-ktx2_no_zstandard");
            start.ArgumentList.Add("-file");
            start.ArgumentList.Add(input);
            start.ArgumentList.Add("-output_file");
            start.ArgumentList.Add(output);

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the Basis Universal encoder.");
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                string details = string.Join(Environment.NewLine, new[] { stdout, stderr }
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));
                throw new InvalidOperationException($"Basis Universal encoding failed ({process.ExitCode}).{Environment.NewLine}{details}");
            }

            return File.ReadAllBytes(output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static string EnsureEncoder()
    {
        (string resource, string fileName) = SelectEncoder();

        string directory = Path.Combine(Path.GetTempPath(), "EmptyEngine", "basisu-v2.50");
        string path = Path.Combine(directory, fileName);
        lock (EncoderGate)
        {
            if (File.Exists(path))
                return path;

            Directory.CreateDirectory(directory);
            // Stage before renaming so another editor cannot execute an incomplete or non-executable encoder.
            string staging = $"{path}.{Environment.ProcessId}.tmp";
            using (Stream source = typeof(TextureEncoding).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing embedded encoder resource '{resource}'."))
            using (FileStream destination = File.Create(staging))
                source.CopyTo(destination);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staging, ExecutableMode);

            try
            {
                File.Move(staging, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(staging);
            }
        }
        return path;
    }

    /// <summary>Selects the embedded encoder build for the running editor host.</summary>
    private static (string Resource, string FileName) SelectEncoder()
    {
        if (OperatingSystem.IsWindows())
            return (WindowsEncoderResource, "basisu.exe");

        // OSArchitecture, not ProcessArchitecture: an x64 .NET under Rosetta still runs the arm64 encoder.
        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
            return (MacEncoderResource, "basisu");

        throw new PlatformNotSupportedException(
            "The bundled Basis Universal editor encoder targets Windows x64 and macOS arm64. " +
            "Build one for this host with tools/basis_universal/build-encoder.sh and embed it alongside the others.");
    }
}
