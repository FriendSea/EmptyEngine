using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Api;
using EmptyEngine.Editor.Hosting;
using EmptyEngine.Editor.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmptyEngine.Tests;

public sealed class EditorApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>操作 API の経路をリファレンスに掲載し、UI と文書自身の経路は除外する。</summary>
    [Fact]
    public async Task Api_reference_comes_from_the_registered_routes()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        EditorWebRunner.AddEditorApi(builder.Services);
        await using WebApplication app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        using var dispatcher = new EditorDispatcher(NullLogger<EditorDispatcher>.Instance);
        EditorViewModel editor = EditorFixture.NewEditor();
        app.MapGet("/healthz", () => Results.Ok("ok"));
        EditorWebRunner.MapEditorApi(
            app, editor, dispatcher, new AssetCatalog(), new HierarchyApi(editor));

        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        JsonObject document = Assert.IsType<JsonObject>(
            await http.GetFromJsonAsync<JsonNode>("/openapi/v1.json"));
        JsonObject paths = Assert.IsType<JsonObject>(document["paths"]);

        Assert.Equal(
            [
                "/api/editor/hierarchy",
                "/api/editor/play-mode",
                "/api/editor/scenes",
                "/api/editor/scenes/{sceneId}",
                "/api/editor/select-asset",
                "/healthz",
            ],
            paths.Select(path => path.Key).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["get", "post"],
            Assert.IsType<JsonObject>(paths["/api/editor/play-mode"]).Select(operation => operation.Key));
        Assert.Equal(
            ["delete"],
            Assert.IsType<JsonObject>(paths["/api/editor/scenes/{sceneId}"]).Select(operation => operation.Key));

        HttpResponseMessage reference = await http.GetAsync("/api/editor");
        reference.EnsureSuccessStatusCode();
        Assert.Contains("EmptyEngine Editor API", await reference.Content.ReadAsStringAsync());

        // 画面は根の下を丸ごと拾う経路を持つので、API 本体が食われていないことを見る。
        HttpResponseMessage hierarchy = await http.GetAsync("/api/editor/hierarchy");
        Assert.Equal("application/json", hierarchy.Content.Headers.ContentType?.MediaType);

        await app.StopAsync();
    }

    [Fact]
    public void Hierarchy_response_is_the_json_contract()
    {
        EditorViewModel editor = EditorWithSelection(out _);
        var api = new HierarchyApi(editor);

        HierarchyResponse response = api.GetHierarchy();
        JsonObject json = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(response, JsonOptions));

        HierarchyItemResponse root = Assert.Single(response.Roots);
        HierarchyItemResponse child = Assert.Single(root.Children);
        Assert.Equal("Object", child.Name);
        Assert.Equal("object-1", response.Selected?.ObjectId);

        JsonObject jsonRoot = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(json["roots"])[0]);
        JsonObject jsonChild = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(jsonRoot["children"])[0]);
        Assert.Equal("Object", jsonChild["name"]?.GetValue<string>());

        JsonObject jsonScene = Assert.IsType<JsonObject>(jsonRoot["scene"]);
        Assert.Equal("scene-1", jsonScene["sceneId"]?.GetValue<string>());
        Assert.Equal("Root", jsonScene["name"]?.GetValue<string>());
    }

    /// <summary>応答が ViewModel を抱えないこと</summary>
    [Fact]
    public void A_response_is_a_fixed_snapshot()
    {
        EditorViewModel editor = EditorWithSelection(out _);
        var api = new HierarchyApi(editor);

        HierarchyResponse before = api.GetHierarchy();

        editor.SelectedNodeName = "Renamed";

        Assert.Equal("Object", Assert.Single(Assert.Single(before.Roots).Children).Name);
        Assert.Equal("Renamed", Assert.Single(Assert.Single(api.GetHierarchy().Roots).Children).Name);
    }

    private static EditorViewModel EditorWithSelection(out AuthoringObject component)
    {
        var fields = new FieldValue();
        fields.Add("Speed", new FieldValue() { Real = 1.5 });
        component = new AuthoringObject(TestSchemas.Object("Probe.Component", ("Speed", TestSchemas.Scalar("System.Single"))), fields);

        var scene = new HierarchyNode(
            "root", "Root",
            [new HierarchyNode("object-1", "Object", components: [component], sceneId: "scene-1")],
            sceneId: "scene-1");

        var editor = EditorFixture.NewEditor();
        editor.UpdateHierarchy([scene]);
        editor.SelectedNode = editor.RootNodes[0].Children[0];
        return editor;
    }
}
