using System.ComponentModel.DataAnnotations;
using ConsoleAppFramework;

namespace EmptyEngine.Host;

/// <summary>Host CLI の入口</summary>
internal static class Program
{
    public static async Task Main(string[] args)
    {
        ConsoleApp.LogError = Console.Error.WriteLine;

        var app = ConsoleApp.Create();
        app.Add("", RunAsync);
        app.Add("build", Build);
        await app.RunAsync(args);
    }

    /// <summary>Start the runtime and the editor and supervise them.</summary>
    /// <param name="manifest">Project to open (*.emptyengine).</param>
    /// <param name="configuration">Build configuration of the editor.</param>
    /// <param name="uiPort">Port of the editor UI. 0 uses the default.</param>
    /// <param name="noBrowser">Do not open a browser on startup.</param>
    private static async Task<int> RunAsync(
        [Argument] string manifest,
        string configuration = "Debug",
        [Range(0, 65535)] int uiPort = 0,
        bool noBrowser = false)
    {
        await using var session = new HostSession(manifest, configuration, uiPort, noBrowser);
        if (!await session.StartAsync())
        {
            return 1;
        }

        await new ConsoleCommands(session).RunUntilQuitAsync();
        return 0;
    }

    /// <summary>Create a distribution build (headless).</summary>
    /// <param name="name">One of the declared builds.</param>
    /// <param name="project">Target project (*.emptyengine).</param>
    /// <param name="output">Output directory.</param>
    private static int Build(
        [Argument] string name,
        string project,
        string output) =>
        BuildLauncher.Run(name, project, output);
}
