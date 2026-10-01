using System.Text.Json;
using EmptyEngine.Host;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>生成されるエディタプロジェクトの中身</summary>
public sealed class EditorProjectGeneratorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ee-editorproj-" + Guid.NewGuid().ToString("N"));

    /// <summary>生成エディタが、ゲーム側の Razor コンポーネント用の名前空間を提供することを検証する</summary>
    [Fact]
    public async Task Generated_editor_carries_imports_for_game_side_razor()
    {
        (string project, _) = await EditorProjectGenerator.GenerateAsync(CreateProject(), _ => { });

        string imports = Path.Combine(Path.GetDirectoryName(project)!, "_Imports.razor");
        Assert.True(File.Exists(imports));

        string content = await File.ReadAllTextAsync(imports);
        Assert.Contains("@using EmptyEngine.Editor.Components.Inspector", content);
        Assert.Contains("@using EmptyEngine.Editor.ViewModels", content);
        Assert.Contains("@using EmptyEngine.Editor", content);
        string csproj = await File.ReadAllTextAsync(project);
        Assert.Contains("<RazorComponent Include=\"_Imports.razor\" />", csproj);
    }

    /// <summary>生成エディタのビルド対象に Razor の名前空間宣言ファイルが含まれることを検証する</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private EmptyEngineProject CreateProject()
    {
        Directory.CreateDirectory(_root);

        string manifest = Path.Combine(_root, "ProbeGame.emptyengine");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            runtimeCommand = "dotnet run",
            editorProps = "ProbeGame.Editor.targets",
        }));

        return EmptyEngineProject.Load(manifest);
    }
}
