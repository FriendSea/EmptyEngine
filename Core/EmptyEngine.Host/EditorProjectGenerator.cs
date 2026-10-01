using System.Text;

namespace EmptyEngine.Host;

/// <summary>生成エディタプロジェクトの生成と場所の解決</summary>
internal static class EditorProjectGenerator
{
    public const string EditorFramework = "net10.0";

    /// <summary>ゲーム側の専用インスペクタが暗黙に使える名前空間を置く場所</summary>
    private const string ImportsFileName = "_Imports.razor";

    /// <summary>ゲーム側の <c>.razor</c> が要る名前空間</summary>
    private static readonly string GeneratedEditorImports = string.Join(
        Environment.NewLine,
        [
            "@* 生成物。ゲーム側の Editor/*.razor に効く @using をここへ集める（EditorProjectGenerator が書く） *@",
            "@using System.Numerics",
            "@using Microsoft.AspNetCore.Components",
            "@using Microsoft.AspNetCore.Components.Web",
            "@using Microsoft.JSInterop",
            "@using EmptyEngine.Editor",
            "@using EmptyEngine.Editor.Authoring",
            "@using EmptyEngine.Editor.Components",
            "@using EmptyEngine.Editor.Components.Inspector",
            "@using EmptyEngine.Editor.Inspection",
            "@using EmptyEngine.Editor.ViewModels",
            "",
        ]);

    public static string ResolveEditorProjectPath(EmptyEngineProject project)
    {
        string editorName = project.Name + ".Editor";
        return Path.GetFullPath(Path.Combine(project.ProjectDirectory, ".artifacts", editorName, editorName + ".csproj"));
    }

    /// <summary>指定したエディタビルド操作専用の出力ルートを返す</summary>
    internal static string ResolveEditorArtifactsPath(EmptyEngineProject project, string operation) =>
        Path.GetFullPath(Path.Combine(project.ProjectDirectory, ".artifacts", "build", operation));

    /// <summary>エディタプロジェクトの内容が変わったときだけの書き出し</summary>
    /// <returns>生成したエディタ csproj のパスと、内容が変わったか。</returns>
    public static async Task<(string Project, bool Changed)> GenerateAsync(EmptyEngineProject project, Action<string> log)
    {
        string editorProject = ResolveEditorProjectPath(project);
        string editorDir = Path.GetDirectoryName(editorProject) ?? throw new InvalidOperationException("Invalid editor project path.");
        Directory.CreateDirectory(editorDir);

        string editorTargets = project.EditorPropsPath;
        if (!File.Exists(editorTargets))
        {
            log($"[Host] WARNING: editor targets not found: {editorTargets}. Generated editor project will be missing references/sources.");
        }

        string relEditorTargets = NormalizeProjectPath(Path.GetRelativePath(editorDir, editorTargets));

        string content =
$"<Project Sdk=\"Microsoft.NET.Sdk.Web\">{Environment.NewLine}{Environment.NewLine}" +
$"  <Import Project=\"{relEditorTargets}\" />{Environment.NewLine}{Environment.NewLine}" +
$"  <ItemGroup>{Environment.NewLine}" +
    $"    <Compile Include=\"Program.cs\" />{Environment.NewLine}" +
    $"    <RazorComponent Include=\"{ImportsFileName}\" />{Environment.NewLine}" +
$"  </ItemGroup>{Environment.NewLine}{Environment.NewLine}" +
$"  <PropertyGroup>{Environment.NewLine}" +
$"    <OutputType>Exe</OutputType>{Environment.NewLine}" +
$"    <TargetFramework Condition=\"'$(TargetFramework)' == ''\">{EditorFramework}</TargetFramework>{Environment.NewLine}" +
    $"    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>{Environment.NewLine}" +
    $"    <EnableDefaultContentItems>false</EnableDefaultContentItems>{Environment.NewLine}" +
    $"    <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>{Environment.NewLine}" +
$"    <ImplicitUsings>enable</ImplicitUsings>{Environment.NewLine}" +
$"    <Nullable>enable</Nullable>{Environment.NewLine}" +
$"  </PropertyGroup>{Environment.NewLine}{Environment.NewLine}" +
$"</Project>{Environment.NewLine}";

        string programPath = Path.Combine(editorDir, "Program.cs");
        string programContent = BuildGeneratedEditorProgram();

        string importsPath = Path.Combine(editorDir, ImportsFileName);

        bool changed = false;
        changed |= await WriteIfDifferentAsync(editorProject, content, Encoding.UTF8);
        changed |= await WriteIfDifferentAsync(programPath, programContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        changed |= await WriteIfDifferentAsync(importsPath, GeneratedEditorImports, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (changed)
        {
            log("[Host] Editor project regenerated (template/paths changed).");
        }

        return (editorProject, changed);
    }

    private static async Task<bool> WriteIfDifferentAsync(string path, string desired, Encoding encoding)
    {
        if (File.Exists(path))
        {
            string existing = await File.ReadAllTextAsync(path);
            if (string.Equals(existing, desired, StringComparison.Ordinal)) return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, desired, encoding);
        return true;
    }

    private static string BuildGeneratedEditorProgram()
    {
        return
            "using EmptyEngine.Editor.Hosting;" + Environment.NewLine + Environment.NewLine +
            "namespace GeneratedEditor;" + Environment.NewLine + Environment.NewLine +
            "public static class Program" + Environment.NewLine +
            "{" + Environment.NewLine +
            "    public static void Main(string[] args) =>" + Environment.NewLine +
            "        EditorBootstrap.Run(args);" + Environment.NewLine +
            "}" + Environment.NewLine;
    }

    private static string NormalizeProjectPath(string path)
    {
        return path.Replace('\\', '/');
    }
}
