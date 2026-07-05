using System.Net;
using System.Text.Json;
using IoTEdge;
using Microsoft.Extensions.Logging.Abstractions;

namespace IoTEdge.Infrastructure.Tests;

public sealed class EdgeTaskReceiptReporterTests
{
    [Fact]
    public async Task ReportAccepted_posts_acceptance_to_dispatch_accept_endpoint()
    {
        var handler = new RecordingHandler();
        var reporter = CreateReporter(handler);
        var context = CreateContext(accessToken: "edge token");

        await reporter.ReportAcceptedAsync(context, "accepted", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://iotsharp.example/api/EdgeTask/Dispatch/edge%20token/Accept", request.Uri);

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal("edge-task-v1", root.GetProperty("contractVersion").GetString());
        Assert.Equal("Accepted", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("progress").GetInt32());
        Assert.Equal(context.TargetKey, root.GetProperty("targetKey").GetString());
    }

    [Theory]
    [InlineData("running", "Running", 35)]
    [InlineData("succeeded", "Succeeded", 100)]
    [InlineData("failed", "Failed", 100)]
    [InlineData("timedout", "TimedOut", 100)]
    public async Task Reporter_posts_edge_task_v1_receipt_payloads(string action, string expectedStatus, int expectedProgress)
    {
        var handler = new RecordingHandler();
        var reporter = CreateReporter(handler);
        var context = CreateContext();
        var result = new Dictionary<string, object> { ["phase"] = action };

        switch (action)
        {
            case "running":
                await reporter.ReportRunningAsync(context, "running", expectedProgress, CancellationToken.None);
                break;
            case "succeeded":
                await reporter.ReportSucceededAsync(context, "succeeded", result, CancellationToken.None);
                break;
            case "failed":
                await reporter.ReportFailedAsync(context, "failed", result, CancellationToken.None);
                break;
            case "timedout":
                await reporter.ReportTimedOutAsync(context, "timed out", result, CancellationToken.None);
                break;
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://iotsharp.example/api/EdgeTask/Receipt", request.Uri);

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal("edge-task-v1", root.GetProperty("contractVersion").GetString());
        Assert.Equal(context.TaskId, root.GetProperty("taskId").GetGuid());
        Assert.Equal("GatewayRuntime", root.GetProperty("targetType").GetString());
        Assert.Equal(context.TargetKey, root.GetProperty("targetKey").GetString());
        Assert.Equal(context.RuntimeType, root.GetProperty("runtimeType").GetString());
        Assert.Equal(context.InstanceId, root.GetProperty("instanceId").GetString());
        Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
        Assert.Equal(expectedProgress, root.GetProperty("progress").GetInt32());
        Assert.Equal("edge-task-dispatch-worker", root.GetProperty("metadata").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Reporter_throws_when_platform_rejects_receipt_logically()
    {
        var handler = new RecordingHandler("""{"code":40000,"msg":"invalid transition"}""");
        var reporter = CreateReporter(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reporter.ReportRunningAsync(CreateContext(), "running", 10, CancellationToken.None));

        Assert.Contains("invalid transition", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static EdgeTaskReceiptReporter CreateReporter(RecordingHandler handler)
        => new(new StaticHttpClientFactory(handler), NullLogger<EdgeTaskReceiptReporter>.Instance);

    private static EdgeTaskReceiptContext CreateContext(string accessToken = "edge-token")
    {
        var gatewayId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        return new EdgeTaskReceiptContext(
            "https://iotsharp.example/",
            accessToken,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "GatewayRuntime",
            $"{gatewayId}:gateway:instance-1",
            "gateway",
            "instance-1");
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StaticHttpClientFactory(HttpMessageHandler handler)
            => _handler = handler;

        public HttpClient CreateClient(string name)
            => new(_handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public RecordingHandler(string responseJson = """{"code":10000,"msg":"OK"}""")
            => _responseJson = responseJson;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri?.AbsoluteUri ?? string.Empty, body));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson)
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string Body);
}
