using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.Editor.ViewModels.Utils;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>保存に対応しないアセットの読み取り専用表示</summary>
public sealed class ReadOnlyAssetInspectorTests : IDisposable
{
    private readonly string _assetsRoot =
        Path.Combine(Path.GetTempPath(), "ee-readonly-" + Guid.NewGuid().ToString("N"));

    public ReadOnlyAssetInspectorTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    /// <summary>本体データはバイト数で表示する。</summary>
    [Fact]
    public void Binary_members_show_their_size()
    {
        FieldViewModel body = ReadOnlyFields(Probe(new ProbeBinary(4096))).Single(f => f.Name == "Body");
        Assert.True(body.IsBinary);
        Assert.Equal("4,096 bytes", body.DisplayText);

        Assert.Equal("(none)", ReadOnlyFields(Probe()).Single(f => f.Name == "Body").DisplayText);
    }

    /// <summary>本体は書き換えられるアセットでも編集させない</summary>
    [Fact]
    public void Binary_members_are_never_editable()
    {
        FieldViewModel body = EditableFields(Probe(new ProbeBinary(16))).Single(f => f.Name == "Body");

        Assert.False(body.IsReadOnly);
        Assert.Equal(EditorFieldKinds.Binary, EditorFieldKinds.Of(body));
    }

    /// <summary>欄が選択の時点で揃っていること</summary>
    [Fact]
    public async Task Selecting_a_read_only_asset_builds_its_fields_once()
    {
        var catalog = new AssetCatalog();
        var service = new AssetImportService(catalog, _assetsRoot, [new ProbeImporter()]);
        await File.WriteAllTextAsync(Path.Combine(_assetsRoot, "Probe.probe"), "probe");
        await service.ImportAllAsync();

        var vm = EditorFixture.NewEditor(assets: catalog, imports: service);

        Assert.Null(vm.Asset.Inspected);

        vm.SelectAsset(new AssetKey(catalog.EnumerateAssets().Single().Key.Value));

        Assert.False(vm.Asset.IsEditable);
        AuthoringObjectViewModel inspected = Assert.IsType<AuthoringObjectViewModel>(vm.Asset.Inspected);
        Assert.Equal("Probe", inspected.DisplayName);
        Assert.Equal("probe", inspected.Fields.Single(f => f.Name == "Name").DisplayText);
        Assert.All(inspected.Fields, field => Assert.True(field.IsReadOnly));
        Assert.Empty(inspected.Fields.Single(f => f.Name == "Name").Children);
        FieldViewModel size = inspected.Fields.Single(f => f.Name == "Size");
        Assert.True(size.IsComposite);
        Assert.Equal(["Width", "Height"], size.Children.Select(c => c.Name));
        Assert.Equal("4", size.Children.Single(c => c.Name == "Width").DisplayText);
        Assert.All(size.Children, child => Assert.True(child.IsReadOnly));

        vm.SelectAsset(null);
        Assert.Null(vm.Asset.Inspected);
    }

    private static IReadOnlyList<FieldViewModel> ReadOnlyFields(AuthoringObject asset)
    {
        IReadOnlyList<FieldViewModel> fields = EditableFields(asset);
        foreach (FieldViewModel field in fields) field.MarkReadOnly();
        return fields;
    }

    private static IReadOnlyList<FieldViewModel> EditableFields(AuthoringObject asset) =>
        new AuthoringObjectViewModel(asset.TypeName, asset.Schema.DisplayName, asset, _ => { }).Fields;

    private static AuthoringObject Probe(IAssetBinary? body = null)
    {
        var size = new FieldTypeInfo("ProbeSize", FieldKind.Map)
        {
            Members = new Dictionary<string, FieldTypeInfo>
            {
                ["Width"] = TestSchemas.Scalar("System.Int32"),
                ["Height"] = TestSchemas.Scalar("System.Int32"),
            },
        };
        ObjectSchema schema = TestSchemas.Object(
            "Probe",
            ("Name", TestSchemas.Scalar("System.String")),
            ("Size", size),
            ("Body", new FieldTypeInfo("EmptyEngine.Core.IAssetBinary", FieldKind.Binary)));

        var sizeValue = new FieldValue();
        sizeValue.Add("Width", new FieldValue { Integer = 4 });
        sizeValue.Add("Height", new FieldValue { Integer = 8 });

        var data = new FieldValue();
        data.Add("Name", new FieldValue { Text = "probe" });
        data.Add("Size", sizeValue);
        data.Add("Body", body is null ? FieldValue.Nil() : new FieldValue { Binary = body });

        return new AuthoringObject(schema, data);
    }

    /// <summary>書き戻しに対応しない取り込み（<c>IsSaveSupported</c> は既定の false）</summary>
    private sealed class ProbeImporter : IAssetImporter
    {
        public IReadOnlyCollection<string> SupportedExtensions => [".probe"];

        public Task<AssetImportResult> ImportAsync(
            AssetImportRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(AssetImportResult.Succeeded(
                "probe",
                new ImportedAsset(request.RelativePath, Probe(), request.SourcePath)));
    }

    /// <summary>大きさだけを名乗る本体ハンドル</summary>
    private sealed class ProbeBinary(long length) : IAssetBinary
    {
        public long Length => length;

        public Stream OpenRead() => new MemoryStream();
    }
}
