using System.Diagnostics;

namespace EmptyEngine.Host;

/// <summary>ヘッドレス配布ビルドの入口（<c>Host build</c>）</summary>
internal static class BuildLauncher
{
    /// <param name="name">宣言されたビルドのどれか（空なら先頭）。</param>
    public static int Run(string name, string manifestPath, string output)
    {
        EmptyEngineProject project = EmptyEngineProject.Load(manifestPath);
        Console.WriteLine($"[Host] Project: {project.ManifestPath}");

        (string editorProject, _) = EditorProjectGenerator.GenerateAsync(project, Console.WriteLine).GetAwaiter().GetResult();
        Console.WriteLine($"[Host] Editor project: {editorProject}");

        string artifactsPath = EditorProjectGenerator.ResolveEditorArtifactsPath(project, "editor-build");
        string forwarded = $"run --project \"{editorProject}\" --artifacts-path \"{artifactsPath}\" -- build";
        if (name is not null) forwarded += $" \"{name}\"";
        if (!string.IsNullOrWhiteSpace(output)) forwarded += $" -o \"{output}\"";

        Console.WriteLine($"[Host] dotnet {forwarded}");
        return RunDotnet(forwarded);
    }

    private static int RunDotnet(string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", arguments) { UseShellExecute = false };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start 'dotnet'.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
