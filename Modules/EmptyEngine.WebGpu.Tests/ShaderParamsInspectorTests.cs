using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Inspection;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.Graphics;
using EmptyEngine.WebGpu.Editor;
using Xunit;

namespace EmptyEngine.WebGpu.Tests;

public sealed class ShaderParamsInspectorTests : IDisposable
{
    private const string ShaderHostType = "EmptyEngine.WebGpu.IShaderParamsHost";
    private const string LineRendererType = "EmptyEngine.WebGpu.LineRendererComponent";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ee-shader-inspector-" + Guid.NewGuid().ToString("N"));

    public ShaderParamsInspectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Initial_import_and_cached_reload_both_feed_authoring_shader_to_inspector()
    {
        string assets = Path.Combine(_root, "Assets");
        string artifacts = Path.Combine(_root, "Artifacts");
        string stamps = Path.Combine(_root, "Stamps");
        Directory.CreateDirectory(assets);

        string source = Path.Combine(assets, "GridLine.wgsl");
        await File.WriteAllTextAsync(source, """
            @group(0) @binding(1) var tex : texture_2d<f32>;
            @group(0) @binding(2) var samp : sampler;

            struct Params {
                scale : f32, // default 1
                tint : vec4<f32>, // default #ff8040ff
            };
            @group(0) @binding(3) var<uniform> params : Params;
            """);

        (AssetCatalog firstCatalog, AssetImportService first) = Service(assets, artifacts, stamps);
        await first.ImportAllAsync();
        ImportedSource initial = Assert.Single(Imported(firstCatalog));
        AuthoringObject initialShader = Assert.IsType<ImportedAsset>(initial).Value;
        AssertShaderInspector(initialShader, initial.Key);

        (AssetCatalog secondCatalog, AssetImportService second) = Service(assets, artifacts, stamps);
        await second.ImportIfChangedAsync();
        ImportedSource cached = Assert.Single(Imported(secondCatalog));
        AuthoringObject cachedShader = Assert.IsType<ImportedAsset>(cached).Value;
        AssertShaderInspector(cachedShader, cached.Key);
    }

    private (AssetCatalog Catalog, AssetImportService Service) Service(string assets, string artifacts, string stamps)
    {
        var catalog = new AssetCatalog();
        var service = new AssetImportService(catalog, new ProjectAssetLayout(assets).Sources, [new ShaderImporter(CatalogStub.Schemas)], stamps);
        service.SetArtifacts(TestArtifacts.At(artifacts));
        return (catalog, service);
    }

    private static ImportedSource[] Imported(AssetCatalog catalog) =>
        catalog.EnumerateAssets()
            .Select(entry =>
            {
                Assert.True(catalog.TryGetImportedAsset(entry.Key.Value, out ImportedSource? asset));
                return asset!;
            })
            .ToArray();

    private static void AssertShaderInspector(AuthoringObject shader, string shaderKey)
    {
        Assert.Equal(typeof(ShaderAsset).FullName, shader.TypeName);

        AuthoringObject component = LineRenderer(shaderKey);
        AuthoringObjectViewModel viewModel = Component(component);

        // 完全一致では当たらず、カタログのスキーマ上の派生関係で拾われる側の型。
        var registry = new InspectorRegistry([
            (typeof(ShaderParamsInspector), [new InspectorForAttribute(ShaderHostType)]),
        ]);
        Assert.Equal(typeof(ShaderParamsInspector), registry.Resolve(component.Schema));

        (FieldViewModel[] parameters, FieldViewModel[] textures) = ShaderParamsInspector.BuildFields(
            viewModel,
            key => key.Value == shaderKey ? shader : null);

        Assert.NotEmpty(parameters);
        Assert.NotEmpty(textures);
        Assert.DoesNotContain(ShaderParamsInspector.PlainFields(viewModel), field => field.Name is "Params" or "Textures");

        FieldViewModel scale = parameters.Single(field => field.Name == "scale");
        FieldViewModel tint = parameters.Single(field => field.Name == "tint");
        FieldViewModel texture = textures.Single(field => field.Name == "tex");
        Assert.Equal(1d, scale.NumericValue, precision: 5);
        Assert.True(tint.IsColor);
        Assert.True(texture.IsAssetReference);
        Assert.Equal(typeof(TextureAsset).FullName, texture.AssetTypeConstraint);

        scale.NumericValue = 2.5;
        tint.ColorValue = EditorColor.FromArgb(128, 64, 128, 255);
        texture.AssignAssetReference(new AssetKey("texture-key"));

        var data = Assert.IsType<FieldValue>(viewModel.Capture().Data);
        var paramValues = Assert.IsType<FieldValue>(data.Get("Params"));
        Assert.Equal(2.5, Assert.IsType<FieldValue>(paramValues.Items[0]).Real, precision: 5);
        Assert.Equal(8, paramValues.Items.Count);
        Assert.Equal(64d / 255d, Assert.IsType<FieldValue>(paramValues.Items[4]).Real, precision: 5);
        Assert.Equal(128d / 255d, Assert.IsType<FieldValue>(paramValues.Items[5]).Real, precision: 5);
        Assert.Equal(1d, Assert.IsType<FieldValue>(paramValues.Items[6]).Real, precision: 5);
        Assert.Equal(128d / 255d, Assert.IsType<FieldValue>(paramValues.Items[7]).Real, precision: 5);

        var textureValues = Assert.IsType<FieldValue>(data.Get("Textures"));
        Assert.Equal("texture-key", textureValues.Items[0].ReferenceKey);
    }

    /// <summary>未設定（nil）で届いたテクスチャ枠にも代入できる</summary>
    [Fact]
    public void Assigning_to_a_texture_slot_that_arrived_unset_lands()
    {
        AuthoringObject component = MeshWithUnsetTexture();
        AuthoringObjectViewModel viewModel = Component(component);
        (_, FieldViewModel[] slots) = ShaderParamsInspector.BuildFields(viewModel, _ => null);

        FieldViewModel texture = Assert.Single(slots);
        texture.AssignAssetReference(new AssetKey("texture-key"));

        FieldValue textures = Assert.IsType<FieldValue>(viewModel.Capture().Data.Get("Textures"));
        Assert.Equal("texture-key", textures.Items[0].ReferenceKey);
    }

    /// <summary>組み込みスロットを 1 つ持ち、その鍵が nil のメッシュ</summary>
    private static AuthoringObject MeshWithUnsetTexture()
    {
        var textures = new FieldValue();
        textures.Items.Add(FieldValue.Nil());

        var data = new FieldValue();
        data.Add("Shader", new FieldValue { Text = string.Empty });
        data.Add("Params", new FieldValue());
        data.Add("Textures", textures);

        var texture = new FieldTypeInfo(typeof(TextureAsset).FullName!, FieldKind.AssetReference);
        var schema = new ObjectSchema
        {
            TypeName = "EmptyEngine.WebGpu.MeshComponent",
            DisplayName = "Mesh",
            AssignableTypeNames = [ShaderHostType],
            Fields = new Dictionary<string, FieldTypeInfo>
            {
                ["Shader"] = new(typeof(ShaderAsset).FullName!, FieldKind.AssetReference),
                ["Params"] = new(typeof(float[]).FullName!, FieldKind.Array)
                {
                    Element = new FieldTypeInfo(typeof(float).FullName!, FieldKind.Float32),
                },
                ["Textures"] = new(typeof(AssetReference<TextureAsset>[]).FullName!, FieldKind.Array) { Element = texture },
            },
        };
        return new AuthoringObject(schema, data);
    }

    private static AuthoringObject LineRenderer(string shaderKey)
    {
        var data = new FieldValue();
        data.Add("Shader", new FieldValue { Text = shaderKey });
        data.Add("Params", new FieldValue());
        data.Add("Textures", new FieldValue());

        var scalar = new FieldTypeInfo(typeof(float).FullName!, FieldKind.Float32);
        var texture = new FieldTypeInfo(typeof(TextureAsset).FullName!, FieldKind.AssetReference);
        var schema = new ObjectSchema
        {
            TypeName = LineRendererType,
            DisplayName = "Line Renderer",
            AssignableTypeNames = [ShaderHostType],
            Fields = new Dictionary<string, FieldTypeInfo>
            {
                ["Shader"] = new(typeof(ShaderAsset).FullName!, FieldKind.AssetReference),
                ["Params"] = new(typeof(float[]).FullName!, FieldKind.Array) { Element = scalar },
                ["Textures"] = new(typeof(AssetReference<TextureAsset>[]).FullName!, FieldKind.Array) { Element = texture },
            },
        };
        return new AuthoringObject(schema, data);
    }

    private static AuthoringObjectViewModel Component(AuthoringObject component) =>
        new(component.TypeName, "Line Renderer", component, _ => { });
}
