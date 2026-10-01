using EmptyEngine.Modules.Testing;
using System.Text.Json;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.Storage;
using EmptyEngine.Tests;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary><c>.asset</c> からの <see cref="AuthoringObject"/> 生成の検証</summary>
public sealed class TypedAssetImportTests
{
    [Fact]
    public async Task Imports_arbitrary_reference_type_by_type_name()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "Greeting.asset");
            await File.WriteAllTextAsync(path, """
                {
                  "TypeName": "EmptyEngine.Modules.Testing.TestAsset",
                  "Data": { "Text": "hello world" }
                }
                """);

            AssetImportResult result = await new TypedAssetImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(path, "Greeting.asset", dir));

            Assert.True(result.Success, result.Message);
            ImportedSource element = Assert.Single(result.Assets);
            Assert.Equal(string.Empty, element.LocalId);
            Assert.True(AuthoringTestHelpers.IsType<TestAsset>(AuthoringTestHelpers.AssetOf(result)));
            Assert.Equal("hello world", AuthoringTestHelpers.GetString(AuthoringTestHelpers.AssetOf(result), "Text"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Imports_arbitrary_value_type()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "Health.asset");
            await File.WriteAllTextAsync(path, """
                {
                  "TypeName": "EmptyEngine.Modules.Testing.TestHealth",
                  "Data": { "Max": 100, "Current": 75 }
                }
                """);

            AssetImportResult result = await new TypedAssetImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(path, "Health.asset", dir));

            Assert.True(result.Success, result.Message);
            AuthoringObject health = AuthoringTestHelpers.AssetOf(result);
            Assert.Equal(100, AuthoringTestHelpers.GetInt(health, "Max"));
            Assert.Equal(75, AuthoringTestHelpers.GetInt(health, "Current"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Omitted_Data_creates_default_instance()
    {
        CatalogStub.Register<TestHealth>();

        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "Bare.asset");
            await File.WriteAllTextAsync(path, """{ "TypeName": "EmptyEngine.Modules.Testing.TestHealth" }""");

            AssetImportResult result = await new TypedAssetImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(path, "Bare.asset", dir));

            Assert.True(result.Success, result.Message);
            AuthoringObject health = AuthoringTestHelpers.AssetOf(result);
            Assert.Equal(0, AuthoringTestHelpers.GetInt(health, "Max"));
            Assert.Equal(0, AuthoringTestHelpers.GetInt(health, "Current"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Source_values_overlay_catalog_defaults()
    {
        CatalogStub.Register<TestHealth>();

        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "Partial.asset");
            await File.WriteAllTextAsync(path, """
                { "TypeName": "EmptyEngine.Modules.Testing.TestHealth", "Data": { "Current": 42 } }
                """);

            AssetImportResult result = await new TypedAssetImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(path, "Partial.asset", dir));

            Assert.True(result.Success, result.Message);
            AuthoringObject health = AuthoringTestHelpers.AssetOf(result);
            Assert.Equal(42, AuthoringTestHelpers.GetInt(health, "Current"));
            Assert.Equal(0, AuthoringTestHelpers.GetInt(health, "Max"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_schema_is_reported_as_an_import_failure()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "Bad.asset");
            await File.WriteAllTextAsync(path, """{ "TypeName": "Nope.DoesNotExist", "Data": { "Keep": 1 } }""");

            AssetImportResult result = await new TypedAssetImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(path, "Bad.asset", dir));

            Assert.False(result.Success);
            Assert.Contains("Nope.DoesNotExist", result.Message);
            Assert.Empty(result.Assets);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Unset_reference_field_is_editable_with_its_declared_constraint()
    {
        CatalogStub.Register<TestSpriteHolder>();

        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "Holder.asset");
            await File.WriteAllTextAsync(path, """{ "TypeName": "EmptyEngine.Modules.Testing.TestSpriteHolder" }""");

            AssetImportResult result = await new TypedAssetImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(path, "Holder.asset", dir));
            AuthoringObject authoring = AuthoringTestHelpers.AssetOf(result);

            var idleNode = Assert.IsType<FieldValue>(Assert.IsType<FieldValue>(authoring.Data).Get("idle"));
            Assert.Equal(FieldKind.AssetReference, authoring.Schema.Fields["idle"].Kind);
            Assert.Equal(string.Empty, idleNode.ReferenceKey);

            var field = new FieldViewModel("idle", authoring.Schema.Fields["idle"], idleNode);
            Assert.Null(field.AssetKey);
            Assert.True(field.IsAssetReference);
            Assert.Equal(typeof(TestAsset).FullName, field.AssetTypeConstraint);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_TypeName_fails()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "NoType.asset");
            await File.WriteAllTextAsync(path, """{ "Data": { "Text": "x" } }""");

            AssetImportResult result = await new TypedAssetImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(path, "NoType.asset", dir));

            Assert.False(result.Success);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Imported_asset_round_trips_through_artifact_as_its_own_type()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "Greeting.asset");
            await File.WriteAllTextAsync(path, """
                { "TypeName": "EmptyEngine.Modules.Testing.TestAsset", "Data": { "Text": "persisted" } }
                """);

            AssetImportResult result = await new TypedAssetImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(path, "Greeting.asset", dir));
            Assert.True(result.Success, result.Message);

            var reference = new AssetKey("greeting-key");
            await TestArtifacts.At(dir).SaveAssetAsync(reference, AuthoringTestHelpers.AssetOf(result));

            var resolver = new WorldAssetResolver(AssetStorage.FromDirectory(dir));
            Assert.True(resolver.TryLoad(new AssetReference<TestAsset>(reference), out TestAsset? loaded));
            Assert.Equal("persisted", loaded!.Text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Artifact_round_trips_back_into_an_authoring_tree()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "Greeting.asset");
            await File.WriteAllTextAsync(path, """
                { "TypeName": "EmptyEngine.Modules.Testing.TestAsset", "Data": { "Text": "reloaded" } }
                """);

            AssetImportResult result = await new TypedAssetImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(path, "Greeting.asset", dir));
            Assert.True(result.Success, result.Message);

            var serializer = TestArtifacts.At(dir);
            var reference = new AssetKey("greeting-key");
            await serializer.SaveAssetAsync(reference, AuthoringTestHelpers.AssetOf(result));

            AuthoringObject? loaded = serializer.LoadAsset(reference);
            Assert.NotNull(loaded);
            Assert.Equal(typeof(TestAsset).FullName, loaded!.TypeName);
            Assert.Equal("reloaded", AuthoringTestHelpers.GetString(loaded, "Text"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Edited_asset_saves_back_to_source_and_re_imports()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "Health.asset");
            await File.WriteAllTextAsync(path, """
                { "TypeName": "EmptyEngine.Modules.Testing.TestHealth", "Data": { "Max": 100, "Current": 75 } }
                """);

            var importer = new TypedAssetImporter(CatalogStub.Schemas);
            AuthoringObject imported = AuthoringTestHelpers.AssetOf(
                await importer.ImportAsync(new AssetImportRequest(path, "Health.asset", dir)));

            AuthoringTestHelpers.SetNumber(imported, "Current", 30);
            AuthoringTestHelpers.SetNumber(imported, "Max", 120);
            await importer.SaveAsync(imported, path);

            string written = await File.ReadAllTextAsync(path);
            Assert.Contains("\"TypeName\"", written);
            Assert.Contains("EmptyEngine.Modules.Testing.TestHealth", written);

            AuthoringObject reimported = AuthoringTestHelpers.AssetOf(
                await importer.ImportAsync(new AssetImportRequest(path, "Health.asset", dir)));
            Assert.Equal(120, AuthoringTestHelpers.GetInt(reimported, "Max"));
            Assert.Equal(30, AuthoringTestHelpers.GetInt(reimported, "Current"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Edited_asset_reference_fields_save_back_and_re_import()
    {
        CatalogStub.Register<TestSpriteHolder>();

        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "Holder.asset");
            await File.WriteAllTextAsync(path, """
                { "TypeName": "EmptyEngine.Modules.Testing.TestSpriteHolder" }
                """);

            var importer = new TypedAssetImporter(CatalogStub.Schemas);
            AuthoringObject imported = AuthoringTestHelpers.AssetOf(
                await importer.ImportAsync(new AssetImportRequest(path, "Holder.asset", dir)));

            Assert.Equal(string.Empty, AuthoringTestHelpers.GetAssetKey(imported, "idle"));
            Assert.Equal(string.Empty, AuthoringTestHelpers.GetAssetKey(imported, "run"));

            AuthoringTestHelpers.SetAssetKey(imported, "idle", "idle-key");
            AuthoringTestHelpers.SetAssetKey(imported, "run", "run-key");
            await importer.SaveAsync(imported, path);

            string written = await File.ReadAllTextAsync(path);
            Assert.Contains("\"Data\"", written);
            Assert.Contains("idle-key", written);
            Assert.Contains("run-key", written);

            AuthoringObject reimported = AuthoringTestHelpers.AssetOf(
                await importer.ImportAsync(new AssetImportRequest(path, "Holder.asset", dir)));
            Assert.Equal("idle-key", AuthoringTestHelpers.GetAssetKey(reimported, "idle"));
            Assert.Equal("run-key", AuthoringTestHelpers.GetAssetKey(reimported, "run"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary><c>.asset</c> が <c>$schema</c> でカタログのスキーマを指せること</summary>
    [Fact]
    public async Task Saved_source_points_at_the_asset_schema()
    {
        string root = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".artifacts"));
            await File.WriteAllTextAsync(Path.Combine(root, ".artifacts", "asset.schema.json"), "{}");
            string dir = Path.Combine(root, "Assets", "UI");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, "Health.asset");
            await File.WriteAllTextAsync(path, """
                { "TypeName": "EmptyEngine.Modules.Testing.TestHealth", "Data": { "Max": 100, "Current": 75 } }
                """);

            var importer = new TypedAssetImporter(CatalogStub.Schemas);
            AuthoringObject imported = AuthoringTestHelpers.AssetOf(
                await importer.ImportAsync(new AssetImportRequest(path, "Health.asset", dir)));
            await importer.SaveAsync(imported, path);

            using JsonDocument written = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("../../.artifacts/asset.schema.json", written.RootElement.GetProperty("$schema").GetString());

            AuthoringObject reimported = AuthoringTestHelpers.AssetOf(
                await importer.ImportAsync(new AssetImportRequest(path, "Health.asset", dir)));
            Assert.Equal(100, AuthoringTestHelpers.GetInt(reimported, "Max"));
            Assert.Equal(75, AuthoringTestHelpers.GetInt(reimported, "Current"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-asset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
