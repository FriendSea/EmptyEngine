using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmptyEngine.Editor.State;

/// <summary>建て直しを跨がせる undo 履歴の置き場</summary>
public sealed class EditHistoryStore
{
    private const byte Version = 1;

    private const int MaxSteps = 4096;

    private const string StepsDirectoryName = "steps";
    private const string StepExtension = ".step";
    private const string IndexFileName = "index";

    private readonly string _stepsDirectory;
    private readonly string _indexPath;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private EditHistory? _pending;

    /// <param name="directory">履歴を置くディレクトリ</param>
    public EditHistoryStore(string directory, ILogger<EditHistoryStore>? logger = null)
    {
        _stepsDirectory = Path.Combine(directory, StepsDirectoryName);
        _indexPath = Path.Combine(directory, IndexFileName);
        _logger = logger ?? NullLogger<EditHistoryStore>.Instance;
    }

    /// <summary>現在の undo 履歴の書き出し</summary>
    public async Task SaveAsync(EditHistory history, CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _pending, history);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EditHistory? latest = Interlocked.Exchange(ref _pending, null);
            if (latest is null) return;

            Directory.CreateDirectory(_stepsDirectory);

            foreach (EditHistoryStep step in latest.Steps)
            {
                string path = StepPath(step.Key);
                if (File.Exists(path)) continue;
                await WriteAtomicAsync(path, step.Snapshot.Encode(), cancellationToken);
            }

            await WriteAtomicAsync(_indexPath, EncodeIndex(latest), cancellationToken);

            var live = new HashSet<ulong>(latest.Steps.Select(s => s.Key));
            foreach (string file in Directory.EnumerateFiles(_stepsDirectory, "*" + StepExtension))
            {
                if (TryParseKey(Path.GetFileNameWithoutExtension(file), out ulong key) && live.Contains(key)) continue;
                try { File.Delete(file); } catch (IOException) { }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>前回のセッションの読み出し</summary>
    /// <remarks>段が 1 つでも欠けていたら部分的に組み立てず丸ごと捨てる</remarks>
    public EditHistory? TryLoad()
    {
        try
        {
            if (!File.Exists(_indexPath)) return null;
            if (DecodeIndex(File.ReadAllBytes(_indexPath)) is not { } index) return null;
            (ulong[] keys, int cursor) = index;

            var steps = new List<EditHistoryStep>(keys.Length);
            foreach (ulong key in keys)
            {
                string path = StepPath(key);
                if (!File.Exists(path)) return null;
                if (SceneSnapshot.TryDecode(File.ReadAllBytes(path)) is not { } snapshot) return null;
                steps.Add(new EditHistoryStep(key, snapshot));
            }

            return new EditHistory(steps, cursor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            _logger.LogWarning("Editor session could not be read (falling back to normal startup): {Error}", ex.Message);
            return null;
        }
    }

    private static byte[] EncodeIndex(EditHistory history)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(history.Cursor);
            writer.Write(history.Steps.Count);
            foreach (EditHistoryStep step in history.Steps) writer.Write(step.Key);
        }

        return buffer.ToArray();
    }

    private static (ulong[] Keys, int Cursor)? DecodeIndex(byte[] bytes)
    {
        try
        {
            using var buffer = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(buffer, Encoding.UTF8);

            if (reader.ReadByte() != Version) return null;

            int cursor = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (count <= 0 || count > MaxSteps) return null;
            if (cursor < 0 || cursor >= count) return null;

            var keys = new ulong[count];
            for (int i = 0; i < count; i++) keys[i] = reader.ReadUInt64();
            return (keys, cursor);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        string temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private string StepPath(ulong key) =>
        Path.Combine(_stepsDirectory, key.ToString("x16", CultureInfo.InvariantCulture) + StepExtension);

    private static bool TryParseKey(string name, out ulong key) =>
        ulong.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key);
}
