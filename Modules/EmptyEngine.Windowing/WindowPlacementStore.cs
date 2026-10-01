using System.Text.Json;

namespace EmptyEngine.Windowing;

internal readonly record struct WindowPlacement(int X, int Y, int Width, int Height)
{
    private const int MaximumDimension = 100_000;

    public bool IsValid =>
        Width > 0 && Width <= MaximumDimension &&
        Height > 0 && Height <= MaximumDimension;

    public bool HasVisibleArea(int areaX, int areaY, int areaWidth, int areaHeight)
    {
        const int minimumVisibleLength = 32;
        long intersectionWidth = Math.Min((long)X + Width, (long)areaX + areaWidth) -
                                 Math.Max(X, areaX);
        long intersectionHeight = Math.Min((long)Y + Height, (long)areaY + areaHeight) -
                                  Math.Max(Y, areaY);
        return intersectionWidth >= Math.Min(Width, minimumVisibleLength) &&
               intersectionHeight >= Math.Min(Height, minimumVisibleLength);
    }
}

internal sealed class WindowPlacementStore
{
    private readonly string _path;

    internal WindowPlacementStore(string path) => _path = path;
    internal string FilePath => _path;

    public static WindowPlacementStore ForWindow(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (System.IO.Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                "Window placement path must be relative to the executable directory.",
                nameof(relativePath));
        }

        return new WindowPlacementStore(System.IO.Path.GetFullPath(relativePath, AppContext.BaseDirectory));
    }

    public WindowPlacement? Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            WindowPlacement placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(_path));
            return placement.IsValid ? placement : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            PlatformLog.Write($"[Windowing] could not read window placement: {e.Message}");
            return null;
        }
    }

    public void Write(WindowPlacement placement)
    {
        if (!placement.IsValid)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(_path);
        string temporaryPath = _path + "." + Environment.ProcessId + ".tmp";

        try
        {
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(placement));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PlatformLog.Write($"[Windowing] could not save window placement: {e.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The placement itself was already saved (or the original error was reported).
            }
        }
    }
}
