using System.Runtime.CompilerServices;
using System.Runtime.Loader;

/// <summary>SDK が持つ Roslyn の解決</summary>
internal static class RoslynLoader
{
    /// <summary>SDK の <c>Roslyn/bincore</c> を教える環境変数（targets の Exec が渡す）</summary>
    public const string DirectoryVariable = "EMPTYENGINE_ROSLYN_BINCORE";

    /// <summary>SDK の Roslyn アセンブリを解決できるようにする</summary>
    [ModuleInitializer]
    internal static void Install()
    {
        if (Environment.GetEnvironmentVariable(DirectoryVariable) is not { Length: > 0 } directory)
            return;

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name is not { } simple
                || !simple.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
            {
                return null;
            }

            string path = Path.Combine(directory, simple + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }
}
