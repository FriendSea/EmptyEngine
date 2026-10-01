using System.Text;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>エディタが動いたままのビルド時カタログ読み直しの検証</summary>
public sealed class CatalogReloadTests
{
    private static byte[] BuildCatalog(params string[] typeNames) =>
        BuildCatalog(typeNames.Select(t => Type(t)).ToArray());

    /// <summary>型 1 つ分の記述（欄は名前だけ与えれば float になる）</summary>
    private static (string TypeName, string[] Members) Type(string typeName, params string[] members) =>
        (typeName, members);

    private static byte[] BuildCatalog(params (string TypeName, string[] Members)[] types)
    {
        string defs = string.Join(",", types.Select(type =>
            $$"""
            "{{type.TypeName}}": {"type":"object","title":"{{type.TypeName.Split('.').Last()}}",
            "x-attachable":true,"properties": {{Properties(type.Members)}} }
            """));
        return Encoding.UTF8.GetBytes("""{"x-formatVersion":4,"$defs":{""" + defs + "}}");

        static string Properties(string[] members) =>
            "{" + string.Join(",", members.Select(member =>
                $$"""
                "{{member}}": {"type":"number","x-kind":"float"}
                """)) + "}";
    }

    [Fact]
    public void Catalog_written_after_startup_is_picked_up_without_restarting()
    {
        string path = TemporaryCatalogPath();

        try
        {
            var source = new TypeCatalog(path, NullLogger<TypeCatalog>.Instance);

            Assert.Empty(source.GetComponentTypes());

            File.WriteAllBytes(path, BuildCatalog("Game.Alpha"));
            WaitPastRecheckWindow();

            Assert.Equal(new[] { "Alpha" }, Names(source));

            File.WriteAllBytes(path, BuildCatalog("Game.Alpha", "Game.Beta"));
            WaitPastRecheckWindow();

            Assert.Equal(new[] { "Alpha", "Beta" }, Names(source));
            Assert.NotNull(source.CreateDefault("Game.Beta"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Catalog_disappearing_does_not_wipe_what_was_already_loaded()
    {
        string path = TemporaryCatalogPath();

        try
        {
            File.WriteAllBytes(path, BuildCatalog("Game.Alpha"));
            var source = new TypeCatalog(path, NullLogger<TypeCatalog>.Instance);
            Assert.Equal(new[] { "Alpha" }, Names(source));

            File.Delete(path);
            WaitPastRecheckWindow();

            Assert.Equal(new[] { "Alpha" }, Names(source));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Schemas_already_handed_out_pick_up_a_new_member()
    {
        string path = TemporaryCatalogPath();

        try
        {
            File.WriteAllBytes(path, BuildCatalog(Type("Game.Alpha", "Value")));
            var source = new TypeCatalog(path, NullLogger<TypeCatalog>.Instance);
            ObjectSchema schema = source.Find("Game.Alpha")!;
            Assert.Equal(["Value"], schema.Fields.Keys);

            File.WriteAllBytes(path, BuildCatalog(Type("Game.Alpha", "Value", "Added")));
            WaitPastRecheckWindow();

            // 引き直した実体が同じ＝掴んだままの authoring 値と VM も新しい欄を見る。
            Assert.Same(schema, source.Find("Game.Alpha"));
            Assert.Equal(["Value", "Added"], schema.Fields.Keys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rewriting_the_same_catalog_does_not_disturb_what_was_handed_out()
    {
        string path = TemporaryCatalogPath();

        try
        {
            File.WriteAllBytes(path, BuildCatalog(Type("Game.Alpha", "Value")));
            var source = new TypeCatalog(path, NullLogger<TypeCatalog>.Instance);
            FieldTypeInfo declared = source.Find("Game.Alpha")!.Root;

            // ゲームを建て直すたびにカタログは焼き直されるが、中身が同じなら欄は起こし直させない。
            File.WriteAllBytes(path, BuildCatalog(Type("Game.Alpha", "Value")));
            WaitPastRecheckWindow();

            Assert.Equal(declared, source.Find("Game.Alpha")!.Root);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TemporaryCatalogPath() =>
        Path.Combine(Path.GetTempPath(), $"TypeCatalog.{Guid.NewGuid():N}.json");

    private static string[] Names(TypeCatalog source) =>
        source.GetComponentTypes().Select(o => o.Name).ToArray();

    private static void WaitPastRecheckWindow() => Thread.Sleep(1100);
}
