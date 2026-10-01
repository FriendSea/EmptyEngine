using EmptyEngine.Editor.Hosting;
using Xunit;

namespace EmptyEngine.Tests;

// CLI はプロセス全体の終了コードとログ出力先を設定する。
[CollectionDefinition("Editor CLI", DisableParallelization = true)]
public sealed class EditorCliCollection;

[Collection("Editor CLI")]
public class EditorCommandLineTests
{
    [Fact]
    public void HelpListsCommandsWithoutInitializingTheEditor()
    {
        var (exitCode, output) = Run("--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("--ui-port", output);
        Assert.Contains("5170", output);
        Assert.Contains("import", output);
        Assert.Contains("deploy-assets", output);
        Assert.Contains("build", output);
        Assert.DoesNotContain("is not declared", output);
    }

    [Fact]
    public void BuildHelpDescribesTheOptionalTargetAndOutputAlias()
    {
        var (exitCode, output) = Run("build", "--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("Name of a declared build. Defaults to the first one.", output);
        Assert.Contains("-o, --output", output);
        Assert.DoesNotContain("is not declared", output);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("deploy-assets")]
    public void HeadlessCommandHelpDoesNotInitializeTheEditor(string command)
    {
        var (exitCode, output) = Run(command, "--help");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("is not declared", output);
    }

    [Fact]
    public void DeployWithoutADestinationShowsHelp()
    {
        var (exitCode, output) = Run("deploy-assets");

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", output);
        Assert.Contains("Distribution directory.", output);
        Assert.DoesNotContain("is not declared", output);
    }

    [Theory]
    [InlineData("--ui-port 0")]
    [InlineData("--ui-port 65536")]
    [InlineData("--ui-port invalid")]
    [InlineData("--ui-port")]
    [InlineData("build -o")]
    [InlineData("unknown-command")]
    public void InvalidArgumentsFailBeforeInitializingTheEditor(string commandLine)
    {
        var (exitCode, output) = Run(commandLine.Split(' '));

        Assert.Equal(1, exitCode);
        Assert.NotEmpty(output);
        Assert.DoesNotContain("is not declared", output);
    }

    private static (int ExitCode, string Output) Run(params string[] args)
    {
        int previousExitCode = Environment.ExitCode;
        var lines = new List<string>();
        try
        {
            Environment.ExitCode = 0;
            EditorBootstrap.Run(args, lines.Add);
            return (Environment.ExitCode, string.Join(Environment.NewLine, lines));
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }
}
