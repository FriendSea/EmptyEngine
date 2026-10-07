using System.Runtime.CompilerServices;
using System.Xml.Linq;
using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Inspection;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.Graphics;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.WebGpu.Editor;
using Xunit;

namespace EmptyEngine.WebGpu.Tests;

public sealed class ShaderParamsInspectorTests : IDisposable
{
    private const string ShaderHostType = "EmptyEngine.WebGpu.IShaderParamsHost";
    private const string LineRendererType = "EmptyEngine.WebGpu.LineRendererComponent";
    private const string SpriteType = "EmptyEngine.WebGpu.SpriteComponent";

    /// <summary>頂点もフラグメントも持ち、コンポーネント側とマテリアル側の両方に値の枠があるシェーダ</summary>
    private const string FullShader = """
        struct VertexParams {
            scale : f32, // default 1
            tint : vec4<f32>, // default #ff8040ff
        };
        @group(0) @binding(3) var<uniform> vertexParams : VertexParams;

        struct Params {
            cutoff : f32, // default 0.5
        };
        @group(2) @binding(0) var<uniform> params : Params;
        @group(2) @binding(2) var mask : texture_2d<f32>;

        @vertex
        fn vs_main(@location(0) position : vec3<f32>) -> @builtin(position) vec4<f32> {
            return vec4<f32>(position, 1.0);
        }

        @fragment
        fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(1.0); }
        """;

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

        await File.WriteAllTextAsync(Path.Combine(assets, "GridLine.wgsl"), FullShader);

        (AssetCatalog firstCatalog, AssetImportService first) = Service(assets, artifacts, stamps);
        await first.ImportAllAsync();
        ImportedSource initial = Assert.Single(Imported(firstCatalog));
        AuthoringObject initialShader = Assert.IsType<ImportedAsset>(initial).Value;
        AssertComponentInspector(initialShader, initial.Key);

        (AssetCatalog secondCatalog, AssetImportService second) = Service(assets, artifacts, stamps);
        await second.ImportIfChangedAsync();
        ImportedSource cached = Assert.Single(Imported(secondCatalog));
        AuthoringObject cachedShader = Assert.IsType<ImportedAsset>(cached).Value;
        AssertComponentInspector(cachedShader, cached.Key);
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

    private static void AssertComponentInspector(AuthoringObject shader, string shaderKey)
    {
        Assert.Equal(typeof(ShaderAsset).FullName, shader.TypeName);

        AuthoringObject component = Component(LineRendererType, shaderKey: shaderKey);
        AuthoringObjectViewModel viewModel = ViewModel(component);

        // 完全一致では当たらず、カタログのスキーマ上の派生関係で拾われる側の型。
        var registry = new InspectorRegistry([
            (typeof(ShaderParamsInspector), [new InspectorForAttribute(ShaderHostType)]),
        ]);
        Assert.Equal(typeof(ShaderParamsInspector), registry.Resolve(component.Schema));

        (FieldViewModel[] parameters, string[] warnings) = ShaderParamsInspector.BuildParams(
            viewModel,
            key => key.Value == shaderKey ? shader : null);
        Assert.Empty(warnings);

        // コンポーネントに出るのは group(0) の分だけ。マテリアル側の cutoff は出ない。
        Assert.Equal(["scale", "tint"], parameters.Select(field => field.Name));
        Assert.DoesNotContain(ShaderParamsInspector.PlainFields(viewModel), field => field.Name is "Params");

        FieldViewModel scale = parameters[0];
        FieldViewModel tint = parameters[1];
        Assert.Equal(1d, scale.NumericValue, precision: 5);
        Assert.True(tint.IsColor);

        scale.NumericValue = 2.5;
        tint.ColorValue = EditorColor.FromArgb(128, 64, 128, 255);

        var data = Assert.IsType<FieldValue>(viewModel.Capture().Data);
        var paramValues = Assert.IsType<FieldValue>(data.Get("Params"));
        Assert.Equal(2.5, Assert.IsType<FieldValue>(paramValues.Items[0]).Real, precision: 5);
        Assert.Equal(8, paramValues.Items.Count);
        Assert.Equal(64d / 255d, Assert.IsType<FieldValue>(paramValues.Items[4]).Real, precision: 5);
        Assert.Equal(128d / 255d, Assert.IsType<FieldValue>(paramValues.Items[5]).Real, precision: 5);
        Assert.Equal(1d, Assert.IsType<FieldValue>(paramValues.Items[6]).Real, precision: 5);
        Assert.Equal(128d / 255d, Assert.IsType<FieldValue>(paramValues.Items[7]).Real, precision: 5);
    }

    /// <summary>コンポーネントの Shader が空なら、マテリアルのシェーダの頂点側の枠が出る</summary>
    [Fact]
    public async Task Without_its_own_shader_a_component_shows_the_material_shaders_vertex_params()
    {
        (AssetCatalog catalog, string shaderKey, string materialKey) = await ProjectWithMaterialAsync(FullShader);
        AuthoringObjectViewModel component = ViewModel(Component(LineRendererType, materialKey: materialKey));

        (FieldViewModel[] parameters, _) = ShaderParamsInspector.BuildParams(component, key => catalog.GetAsset(key.Value));

        Assert.NotEqual(shaderKey, materialKey);
        Assert.Equal(["scale", "tint"], parameters.Select(field => field.Name));
    }

    /// <summary>マテリアルのシェーダが頂点を持たなければ、同梱の頂点シェーダの枠（＝無し）になる</summary>
    [Fact]
    public async Task A_fragment_only_material_leaves_the_component_with_the_built_in_vertex_params()
    {
        (AssetCatalog catalog, _, string materialKey) = await ProjectWithMaterialAsync("""
            struct VertexParams {
                unused : f32,
            };
            @group(0) @binding(3) var<uniform> vertexParams : VertexParams;
            @fragment fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(vertexParams.unused); }
            """);
        AuthoringObjectViewModel component = ViewModel(Component(SpriteType, materialKey: materialKey));

        (FieldViewModel[] parameters, string[] warnings) =
            ShaderParamsInspector.BuildParams(component, key => catalog.GetAsset(key.Value));

        Assert.Empty(parameters);
        Assert.Empty(warnings);
    }

    /// <summary>頂点入力の合わないシェーダは描画と同じく飛ばされ、その枠は出ず、警告が出る</summary>
    [Fact]
    public async Task A_shader_that_does_not_fit_the_component_is_skipped_with_a_warning()
    {
        // FullShader は位置 vec3 を受け取るので、Line には合うが Sprite（quad は vec2）には合わない。
        (AssetCatalog catalog, string shaderKey, string materialKey) = await ProjectWithMaterialAsync(FullShader);
        Func<AssetKey, AuthoringObject?> assets = key => catalog.GetAsset(key.Value);

        (FieldViewModel[] own, string[] ownWarnings) =
            ShaderParamsInspector.BuildParams(ViewModel(Component(SpriteType, shaderKey: shaderKey)), assets);
        Assert.Empty(own);
        Assert.Contains("Quad", Assert.Single(ownWarnings));

        (FieldViewModel[] viaMaterial, string[] materialWarnings) =
            ShaderParamsInspector.BuildParams(ViewModel(Component(SpriteType, materialKey: materialKey)), assets);
        Assert.Empty(viaMaterial);
        Assert.Contains("Quad", Assert.Single(materialWarnings));
    }

    /// <summary>マテリアルのインスペクタは、シェーダの group(2) の枠を出し、値をマテリアルへ書く</summary>
    [Fact]
    public async Task Material_inspector_edits_the_fragment_params_and_extra_textures()
    {
        (AssetCatalog catalog, _, string materialKey) = await ProjectWithMaterialAsync(FullShader);
        AuthoringObject material = Assert.IsType<AuthoringObject>(catalog.GetAsset(materialKey));
        Assert.Equal(typeof(MaterialAsset).FullName, material.TypeName);

        var registry = new InspectorRegistry([
            (typeof(MaterialInspector), [new InspectorForAttribute(MaterialInspector.MaterialAssetTypeId)]),
        ]);
        Assert.Equal(typeof(MaterialInspector), registry.Resolve(material.Schema));

        AuthoringObjectViewModel viewModel = ViewModel(material);
        (FieldViewModel[] parameters, FieldViewModel[] textures) = MaterialInspector.BuildFields(
            viewModel, key => catalog.GetAsset(key.Value));

        FieldViewModel cutoff = Assert.Single(parameters);
        Assert.Equal("cutoff", cutoff.Name);
        Assert.Equal(0.5d, cutoff.NumericValue, precision: 5);
        FieldViewModel mask = Assert.Single(textures);
        Assert.Equal("mask", mask.Name);
        Assert.True(mask.IsAssetReference);
        Assert.Equal(typeof(TextureAsset).FullName, mask.AssetTypeConstraint);
        Assert.Contains(MaterialInspector.PlainFields(viewModel), field => field.Name == "Blend");
        Assert.DoesNotContain(MaterialInspector.PlainFields(viewModel), field => field.Name is "Params" or "Textures");

        // 枠がまだ無い（空配列で届いた）ところへも代入できる。
        cutoff.NumericValue = 0.25;
        mask.AssignAssetReference(new AssetKey("texture-key"));

        FieldValue data = viewModel.Capture().Data;
        Assert.Equal(0.25, Assert.IsType<FieldValue>(data.Get("Params")).Items[0].Real, precision: 5);
        Assert.Equal("texture-key", Assert.IsType<FieldValue>(data.Get("Textures")).Items[0].ReferenceKey);
    }

    /// <summary>同梱の既定の頂点シェーダと既定マテリアルは、コードが持つキーで取り込まれ、同じキーが配布の起点として宣言されている</summary>
    [Fact]
    public async Task Shipped_assets_import_under_the_keys_the_components_resolve()
    {
        AssetCatalog catalog = await ShippedAssetsAsync();

        string[] shaders = [BuiltinShaders.Sprite, BuiltinShaders.Mesh, BuiltinShaders.Line, BuiltinShaders.Effect];
        Assert.All(shaders, key => Assert.Equal(typeof(ShaderAsset).FullName, catalog.GetAsset(key)?.TypeName));
        string[] materials = [BuiltinMaterials.Opaque, BuiltinMaterials.Transparent];
        Assert.All(materials, key => Assert.Equal(typeof(MaterialAsset).FullName, catalog.GetAsset(key)?.TypeName));
        Assert.Equal(
            [.. shaders, .. materials],
            XDocument.Load(Path.Combine(ShippedPath(), "..", "buildTransitive", "EmptyEngine.WebGpu.Editor.props"))
                .Descendants("DistributionRoot").Select(root => root.Attribute("Include")!.Value));
        Assert.Equal(
            [
                "Packages/EmptyEngine.WebGpu/Default.wgsl", "Packages/EmptyEngine.WebGpu/Effect.wgsl",
                "Packages/EmptyEngine.WebGpu/Line.wgsl", "Packages/EmptyEngine.WebGpu/Mesh.wgsl",
                "Packages/EmptyEngine.WebGpu/Opaque.asset", "Packages/EmptyEngine.WebGpu/Sprite.wgsl",
                "Packages/EmptyEngine.WebGpu/Transparent.asset",
            ],
            catalog.EnumerateAssets().Select(entry => entry.DisplayPath).Order(StringComparer.Ordinal));
    }

    /// <summary>同梱の頂点シェーダは頂点だけ、2 つの既定マテリアルが共用するシェーダはフラグメントだけを持つ</summary>
    [Fact]
    public async Task Shipped_vertex_shaders_and_the_default_materials_shader_each_hold_one_stage()
    {
        AssetCatalog catalog = await ShippedAssetsAsync();

        Assert.All(
            [BuiltinShaders.Sprite, BuiltinShaders.Mesh, BuiltinShaders.Line, BuiltinShaders.Effect],
            key =>
            {
                FieldValue vertex = catalog.GetAsset(key)!.Data;
                Assert.True(vertex.Get("HasVertex")!.Bool);
                Assert.False(vertex.Get("HasFragment")!.Bool);
            });

        string fragmentKey = catalog.GetAsset(BuiltinMaterials.Opaque)!.Data.Get("Shader")!.ReferenceKey!;
        Assert.Equal(fragmentKey, catalog.GetAsset(BuiltinMaterials.Transparent)!.Data.Get("Shader")!.ReferenceKey);
        FieldValue fragment = catalog.GetAsset(fragmentKey)!.Data;
        Assert.False(fragment.Get("HasVertex")!.Bool);
        Assert.True(fragment.Get("HasFragment")!.Bool);
    }

    /// <summary>モジュールが同梱する既定アセットを、パッケージのアセットとして取り込んだカタログ</summary>
    private async Task<AssetCatalog> ShippedAssetsAsync()
    {
        string assets = Path.Combine(_root, "Assets");
        Directory.CreateDirectory(assets);

        var catalog = new AssetCatalog();
        var service = new AssetImportService(
            catalog,
            new ProjectAssetLayout(assets, [ProjectAssetLayout.Package("EmptyEngine.WebGpu", ShippedPath())]).Sources,
            [new ShaderImporter(CatalogStub.Schemas), new TypedAssetImporter(CatalogStub.Schemas)],
            Path.Combine(_root, "Stamps"));
        service.SetArtifacts(TestArtifacts.At(Path.Combine(_root, "Artifacts")));
        await service.ImportAllAsync();
        return catalog;
    }

    /// <summary>シェーダ 1 本と、それを指す値の入っていないマテリアル 1 つを取り込んだカタログ</summary>
    private async Task<(AssetCatalog Catalog, string ShaderKey, string MaterialKey)> ProjectWithMaterialAsync(string wgsl)
    {
        const string ShaderKey = "11111111111111111111111111111111";
        const string MaterialKey = "22222222222222222222222222222222";

        string assets = Path.Combine(_root, "Assets");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Combine(assets, "Probe.wgsl"), wgsl);
        await File.WriteAllTextAsync(Path.Combine(assets, "Probe.wgsl.meta"), $$"""{"guid":"{{ShaderKey}}"}""");
        await File.WriteAllTextAsync(Path.Combine(assets, "Probe.asset"), $$"""
            {
              "TypeName": "EmptyEngine.WebGpu.MaterialAsset",
              "Data": { "Shader": "{{ShaderKey}}" }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(assets, "Probe.asset.meta"), $$"""{"guid":"{{MaterialKey}}"}""");

        var catalog = new AssetCatalog();
        var service = new AssetImportService(
            catalog,
            new ProjectAssetLayout(assets).Sources,
            [new ShaderImporter(CatalogStub.Schemas), new TypedAssetImporter(CatalogStub.Schemas)],
            Path.Combine(_root, "Stamps"));
        service.SetArtifacts(TestArtifacts.At(Path.Combine(_root, "Artifacts")));
        await service.ImportAllAsync();
        return (catalog, ShaderKey, MaterialKey);
    }

    private static string ShippedPath([CallerFilePath] string? thisFile = null) =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "EmptyEngine.WebGpu.Editor", "assets");

    private static AuthoringObject Component(string typeName, string shaderKey = "", string materialKey = "")
    {
        var data = new FieldValue();
        data.Add("Shader", new FieldValue { Text = shaderKey });
        data.Add("Material", new FieldValue { Text = materialKey });
        data.Add("Params", new FieldValue());

        var scalar = new FieldTypeInfo(typeof(float).FullName!, FieldKind.Float32);
        var schema = new ObjectSchema
        {
            TypeName = typeName,
            DisplayName = typeName,
            AssignableTypeNames = [ShaderHostType],
            Fields = new Dictionary<string, FieldTypeInfo>
            {
                ["Shader"] = new(typeof(ShaderAsset).FullName!, FieldKind.AssetReference),
                ["Material"] = new(typeof(MaterialAsset).FullName!, FieldKind.AssetReference),
                ["Params"] = new(typeof(float[]).FullName!, FieldKind.Array) { Element = scalar },
            },
        };
        return new AuthoringObject(schema, data);
    }

    private static AuthoringObjectViewModel ViewModel(AuthoringObject target) =>
        new(target.TypeName, target.Schema.DisplayName, target, _ => { });
}
