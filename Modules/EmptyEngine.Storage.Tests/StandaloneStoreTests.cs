using EmptyEngine.Modules.Testing;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using EmptyEngine.Core;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.Storage.Tests;

public sealed class StandaloneStoreTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "ee-standalone-store-" + Guid.NewGuid().ToString("N"));
    private string Imports => Path.Combine(_project, ".artifacts", "ImportedAssets");
    private string Archive => Path.Combine(_project, "ImportedAssets.pak");

    public StandaloneStoreTests()
    {
        Directory.CreateDirectory(Imports);
        WriteScene("first", "First");
        WriteScene("second", "Second");
        File.WriteAllText(Path.Combine(_project, "BuildScenes.json"), """{"Scenes":["second","first"]}""");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_pak_mounts_directory_and_loads_project_scenes_in_order(bool trailingSeparator)
    {
        string directory = trailingSeparator ? Imports + Path.DirectorySeparatorChar : Imports;
        using AssetStorage store = AssetStorage.FromArchive(Archive, directory);
        Assert.NotNull(store.DirectoryRoot);
        var world = new SceneWorld();
        Assert.Equal(new[] { "second", "first" }, world.LoadAllIntoNow(store).Select(key => key.Value));
        Assert.Equal(new[] { "Second", "First" }, world.LoadedSceneNames);
        Assert.False(File.Exists(Archive));
        Assert.False(File.Exists(Path.Combine(Imports, ImportedAssetsLayout.ArtifactPath(StartupScene.ManifestKey))));

        // コピーもバンドルもないので、同じストアでファイルの更新・削除とシーン一覧の変更が見える。
        WriteScene("first", "Updated");
        File.Delete(Path.Combine(Imports, ImportedAssetsLayout.ArtifactPath("second")));
        File.WriteAllText(Path.Combine(_project, "BuildScenes.json"), """{"Scenes":["first"]}""");
        var nextWorld = new SceneWorld();
        nextWorld.LoadAllIntoNow(store);
        Assert.False(store.CanOpenNow("second"));
        Assert.Equal(new[] { "Updated" }, nextWorld.LoadedSceneNames);
    }

    [Fact]
    public void Existing_pak_uses_its_own_assets_and_manifest_only()
    {
        using (ZipArchive archive = ZipFile.Open(Archive, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(Path.Combine(Imports, ImportedAssetsLayout.ArtifactPath("first")), ImportedAssetsLayout.ArtifactPath("first"));
            using var writer = new StreamWriter(archive.CreateEntry(ImportedAssetsLayout.ArtifactPath(StartupScene.ManifestKey)).Open());
            writer.Write("""["first"]""");
        }

        WriteScene("first", "Changed outside pak");
        using AssetStorage store = AssetStorage.FromArchive(Archive, Imports);
        Assert.Null(store.DirectoryRoot);
        Assert.False(store.CanOpenNow("second"));
        var world = new SceneWorld();
        world.LoadAllIntoNow(store);
        Assert.Equal(new[] { "First" }, world.LoadedSceneNames);
    }

    [Fact]
    public void Folder_manifest_takes_precedence_over_project_scene_list()
    {
        File.WriteAllText(Path.Combine(Imports, ImportedAssetsLayout.ArtifactPath(StartupScene.ManifestKey)), """["first"]""");
        using AssetStorage store = AssetStorage.FromArchive(Archive, Imports);
        Assert.Equal(new[] { "first" }, store.ReadStartupScenesNow().Select(key => key.Value));
    }

    [Fact]
    public void Pak_without_manifest_does_not_load_project_scene_list()
    {
        using (ZipArchive archive = ZipFile.Open(Archive, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(Path.Combine(Imports, ImportedAssetsLayout.ArtifactPath("first")), ImportedAssetsLayout.ArtifactPath("first"));
        using AssetStorage store = AssetStorage.FromArchive(Archive, Imports);
        Assert.Empty(store.ReadStartupScenesNow());
    }

    [Theory]
    [InlineData("Release", true)]
    [InlineData("Debug", false)]
    public async Task Publish_disables_folder_and_scene_list_fallback(string configuration, bool noBuild)
    {
        string targets = StorageTargetsPath();
        Assert.True(File.Exists(targets), targets);

        var assemblies = new[] { typeof(RuntimeStore).Assembly, typeof(StartupScene).Assembly, typeof(AssetKey).Assembly };
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("OutputType", "Exe"),
                new XElement("TargetFramework", "net10.0"),
                new XElement("NuGetAudit", "false")),
            new XElement("ItemGroup", assemblies.Select(assembly =>
                new XElement("Reference", new XAttribute("Include", assembly.GetName().Name!),
                    new XElement("HintPath", assembly.Location)))),
            new XElement("Import", new XAttribute("Project", targets))))
            .Save(Path.Combine(_project, "Probe.csproj"));
        File.WriteAllText(Path.Combine(_project, "Program.cs"), """
            using EmptyEngine.Storage;
            using EmptyEngine.World;
            using var store = RuntimeStore.Open();
            using var folder = AssetStorage.FromDirectory(args[0]);
            bool exists = store.TryOpen("first", out var first);
            first?.Dispose();
            System.Console.WriteLine($"Development={RuntimeStore.DevelopmentAssetsEnabled};Store={exists};Scenes={(await StartupScene.ReadStartupScenesAsync(store)).Count};ProjectScenes={(await StartupScene.ReadStartupScenesAsync(folder)).Count}");
            """);

        string build = Path.Combine(_project, ".artifacts", "build");
        string development = await Dotnet("run", "-c", configuration, "--artifacts-path", build,
            "--no-launch-profile", "--", Imports);
        Assert.Contains("Development=True;Store=True;Scenes=2;ProjectScenes=2", development);

        string published = Path.Combine(_project, "published");
        var arguments = new List<string> { "publish", "-c", configuration, "--artifacts-path", build, "-o", published };
        if (noBuild) arguments.Add("--no-build");
        await Dotnet(arguments.ToArray());
        string executable = Path.Combine(published, "Probe.dll");
        string withoutPak = await Dotnet(executable, Imports);
        Assert.Contains("Development=False;Store=False;Scenes=0;ProjectScenes=0", withoutPak);

        using (ZipArchive archive = ZipFile.Open(Path.Combine(published, "ImportedAssets.pak"), ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(Path.Combine(Imports, ImportedAssetsLayout.ArtifactPath("first")), ImportedAssetsLayout.ArtifactPath("first"));
            using var writer = new StreamWriter(archive.CreateEntry(ImportedAssetsLayout.ArtifactPath(StartupScene.ManifestKey)).Open());
            writer.Write("""["first"]""");
        }
        string withPak = await Dotnet(executable, Imports);
        Assert.Contains("Development=False;Store=True;Scenes=1;ProjectScenes=0", withPak);
    }

    private async Task<string> Dotnet(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _project,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("EmptyEngineEditorWebSocketUrl");
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        string output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        return output;
    }

    private void WriteScene(string key, string name) =>
        File.WriteAllBytes(Path.Combine(Imports, ImportedAssetsLayout.ArtifactPath(key)), SceneBlob.Encode(
            [new RootInstanceData(string.Empty, new ObjectData("root", name, [], []))]));

    private static string StorageTargetsPath([CallerFilePath] string? thisFile = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile!)!, "..", "EmptyEngine.Storage", "buildTransitive", "EmptyEngine.Storage.targets"));

    public void Dispose() => Directory.Delete(_project, recursive: true);
}
