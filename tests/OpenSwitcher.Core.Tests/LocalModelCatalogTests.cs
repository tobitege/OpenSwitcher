using System.Net;
using System.Text;

namespace OpenSwitcher.Core.Tests;

[TestClass]
public sealed class LocalModelCatalogTests
{
    private const string OllamaTags = """
        { "models": [ { "name": "model-a:latest", "capabilities": ["tools"] }, { "model": "model-b:latest", "capabilities": ["vision"] } ] }
        """;

    [TestMethod]
    [DataRow("not json")]
    [DataRow("[1, 2]")]
    [DataRow("{ \"data\": [] }")]
    public void ParseLmStudioModels_InvalidPayload_ThrowsInvalidDataException(string json)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => LocalModelCatalog.ParseLmStudioModels(json));
    }

    [TestMethod]
    public void ParseOllamaModels_InvalidJson_ThrowsInvalidDataException()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => LocalModelCatalog.ParseOllamaModels("{"));
    }

    [TestMethod]
    public async Task GetLmStudioModels_UsesTheServerRootOfTheConfiguredBaseUrl()
    {
        using var handler = new FakeHandler(_ => Json("""
            { "models": [ { "type": "llm", "key": "a", "display_name": "A", "loaded_instances": [ {} ] }, { "type": "embedding", "key": "e" } ] }
            """));
        using var client = new HttpClient(handler);

        var models = await new LocalModelCatalog(client).GetLmStudioModelsAsync(new Uri("http://lmstudio.example.test:4321/v1"));

        Assert.AreEqual(new Uri("http://lmstudio.example.test:4321/api/v1/models"), handler.Requests.Single());
        Assert.AreEqual("a", models.Single().Id);
        Assert.IsTrue(models.Single().IsLoaded);
    }

    [TestMethod]
    public async Task GetOllamaModels_MarksRunningModels()
    {
        using var handler = new FakeHandler(uri => uri.AbsolutePath == "/api/ps"
            ? Json("""{ "models": [ { "name": "model-a:latest" } ] }""")
            : Json(OllamaTags));
        using var client = new HttpClient(handler);

        var models = await new LocalModelCatalog(client).GetOllamaModelsAsync(LocalProviderDescriptor.Ollama.DefaultBaseUrl);

        CollectionAssert.AreEqual(
            new[] { new Uri("http://127.0.0.1:11434/api/tags"), new Uri("http://127.0.0.1:11434/api/ps") },
            handler.Requests.ToArray());
        Assert.IsTrue(models.Single(model => model.Id == "model-a:latest").IsLoaded);
        Assert.IsTrue(models.Single(model => model.Id == "model-b:latest").SupportsVision);
    }

    [TestMethod]
    [DataRow("invalid-json")]
    [DataRow("timeout")]
    [DataRow("connection")]
    [DataRow("http-error")]
    public async Task GetOllamaModels_RunningModelsFailure_StillReturnsInstalledModels(string failure)
    {
        using var handler = new FakeHandler(uri =>
        {
            if (uri.AbsolutePath != "/api/ps")
            {
                return Json(OllamaTags);
            }

            return failure switch
            {
                "invalid-json" => Json("<html>"),
                "timeout" => throw new TaskCanceledException("timeout"),
                "connection" => throw new HttpRequestException("refused"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var client = new HttpClient(handler);

        var models = await new LocalModelCatalog(client).GetOllamaModelsAsync(LocalProviderDescriptor.Ollama.DefaultBaseUrl);

        Assert.AreEqual(2, models.Count);
        Assert.IsTrue(models.All(model => !model.IsLoaded));
    }

    [TestMethod]
    public async Task GetOllamaModels_UserCancellation_IsNotSwallowed()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new FakeHandler(uri =>
        {
            if (uri.AbsolutePath != "/api/ps")
            {
                return Json(OllamaTags);
            }

            cancellation.Cancel();
            throw new TaskCanceledException("cancelled");
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new LocalModelCatalog(client).GetOllamaModelsAsync(LocalProviderDescriptor.Ollama.DefaultBaseUrl, cancellation.Token));
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
