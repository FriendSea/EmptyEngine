using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EmptyEngine.Serialization.Generator.Tests;

/// <summary>入力の変更に応じた生成コードと診断の更新を検証する</summary>
public sealed class IncrementalCachingTests
{
    private const string Component = """
        using EmptyEngine.ObjectModel;

        namespace Sample;

        public sealed class Health : IAttachable
        {
            public int Current { get; set; }
        }
        """;

    /// <summary>実行中の TFM の解決済み参照一式（<c>EmptyEngine.*</c> を含む）</summary>
    private static readonly ImmutableArray<MetadataReference> References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => path.Length != 0)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToImmutableArray();

    private static CSharpCompilation Compile(string source)
    {
        var compilation = CSharpCompilation.Create(
            "GeneratorCachingProbe",
            [CSharpSyntaxTree.ParseText(source, path: "Probe.cs")],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }

    /// <summary>2 つのコンパイルを続けて流し、それぞれの結果の取得</summary>
    private static (GeneratorRunResult First, GeneratorRunResult Second) RunTwice(string before, string after)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SerializerGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(Compile(before));
        GeneratorRunResult first = driver.GetRunResult().Results[0];

        driver = driver.RunGenerators(Compile(after));
        return (first, driver.GetRunResult().Results[0]);
    }

    private static IncrementalStepRunReason ReasonOf(GeneratorRunResult result, string step) =>
        Assert.Single(Assert.Single(result.TrackedSteps[step]).Outputs).Reason;

    private static string GeneratedText(GeneratorRunResult result) =>
        Assert.Single(result.GeneratedSources).SourceText.ToString();

    [Fact]
    public void An_edit_that_does_not_reach_the_wire_reuses_the_generated_source()
    {
        (_, GeneratorRunResult second) = RunTwice(Component, Component + "\n// 生成には効かない編集\n");

        Assert.Equal(IncrementalStepRunReason.Unchanged, ReasonOf(second, SerializerGenerator.SourceStep));
        Assert.All(
            second.TrackedOutputSteps.SelectMany(entry => entry.Value).SelectMany(step => step.Outputs),
            output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
    }

    [Fact]
    public void A_new_member_regenerates_the_source()
    {
        (GeneratorRunResult first, GeneratorRunResult second) = RunTwice(
            Component,
            Component.Replace(
                "public int Current { get; set; }",
                "public int Current { get; set; } public int Max { get; set; }"));

        Assert.Empty(first.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
        Assert.Contains("Sample.Health", GeneratedText(first));
        Assert.NotEqual(GeneratedText(first), GeneratedText(second));
        Assert.Contains("Max", GeneratedText(second));
    }

    /// <summary>診断の位置だけが動いたとき、生成テキストの段を巻き込まないこと</summary>
    [Fact]
    public void Moving_a_reported_type_does_not_regenerate_the_source()
    {
        // private なネスト型は復元コードを作れない＝場所つきの診断が 1 件出る
        const string WithSkippedType = """
            using EmptyEngine.ObjectModel;

            namespace Sample;

            public sealed class Holder
            {
                private sealed class Hidden : IAttachable
                {
                    public int Value { get; set; }
                }
            }

            public sealed class Health : IAttachable
            {
                public int Current { get; set; }
                internal sealed class Nested : IAttachable { public int Value; }
            }
            internal sealed class Internal : IAttachable { public int Value; }
            public abstract class Abstract : IAttachable { }
            public sealed class Open<T> : IAttachable { public T Value = default!; }
            """;

        (GeneratorRunResult first, GeneratorRunResult second) =
            RunTwice(WithSkippedType, "\n" + WithSkippedType);

        Diagnostic before = Assert.Single(first.Diagnostics.Where(d => d.GetMessage().Contains("Hidden")));
        Diagnostic after = Assert.Single(second.Diagnostics.Where(d => d.GetMessage().Contains("Hidden")));
        Assert.Equal("EEMPS001", before.Id);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Location.GetLineSpan().StartLinePosition.Line + 1,
            after.Location.GetLineSpan().StartLinePosition.Line);
        string generated = GeneratedText(first);
        Assert.Equal(generated, GeneratedText(second));
        Assert.Contains("Sample.Health", generated);
        Assert.Contains("Sample.Health.Nested", generated);
        Assert.Contains("Sample.Internal", generated);
        Assert.DoesNotContain("Sample.Holder.Hidden", generated);
        Assert.DoesNotContain("Sample.Abstract", generated);
        Assert.DoesNotContain("Sample.Open", generated);
    }
}
