using System.Text.Json;

namespace EmptyEngine.Host;

/// <summary>ゲームプロジェクトのマニフェスト（<c>*.emptyengine</c>）</summary>
internal sealed class EmptyEngineProject
{
    /// <summary>マニフェスト（<c>*.emptyengine</c>）の絶対パス</summary>
    public string ManifestPath { get; private set; } = string.Empty;

    /// <summary>マニフェストの置かれたディレクトリ＝ゲームプロジェクトのルート</summary>
    public string ProjectDirectory => Path.GetDirectoryName(ManifestPath)
        ?? throw new InvalidOperationException($"Cannot resolve directory of manifest: {ManifestPath}");

    /// <summary>プロジェクト名（マニフェストのファイル名・拡張子なし）</summary>
    public string Name => Path.GetFileNameWithoutExtension(ManifestPath);

    /// <summary>ランタイムを起動するコマンド</summary>
    public required string RuntimeCommand { get; init; }

    /// <summary>ゲーム側エディタ拡張 targets（マニフェスト基準のまま）</summary>
    public required string EditorProps { get; init; }

    /// <summary>生成エディタ csproj が <c>Import</c> するエディタ拡張 targets の絶対パス</summary>
    public string EditorPropsPath => Resolve(EditorProps);

    /// <summary><see cref="RuntimeCommand"/> の実行ファイルと引数列への分割</summary>
    public (string FileName, string Arguments) SplitRuntimeCommand()
    {
        string command = RuntimeCommand.Trim();
        int space = command.IndexOf(' ');
        return space < 0 ? (command, string.Empty) : (command[..space], command[(space + 1)..].TrimStart());
    }

    /// <summary>マニフェストの読み込み</summary>
    public static EmptyEngineProject Load(string manifestPath)
    {
        string full = Path.GetFullPath(manifestPath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Project manifest not found: {full}");
        }

        EmptyEngineProject project;
        try
        {
            project = JsonSerializer.Deserialize<EmptyEngineProject>(File.ReadAllText(full), new JsonSerializerOptions()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            })
                ?? throw new InvalidOperationException($"Project manifest is empty: {full}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid project manifest {full}: {ex.Message}", ex);
        }

        project.ManifestPath = full;
        return project;
    }

    private string Resolve(string relativeToManifest) =>
        Path.GetFullPath(Path.Combine(ProjectDirectory, relativeToManifest));
}
