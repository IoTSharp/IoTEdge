using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace IoTEdge;

public sealed record EdgeTaskReceiptContext(
    string BaseUrl,
    string AccessToken,
    Guid TaskId,
    string TargetType,
    string TargetKey,
    string RuntimeType,
    string InstanceId);

public interface IEdgeTaskReceiptReporter
{
    Task ReportAcceptedAsync(EdgeTaskReceiptContext context, string message, CancellationToken cancellationToken = default);

    Task ReportRunningAsync(EdgeTaskReceiptContext context, string message, int progress, CancellationToken cancellationToken = default);

    Task ReportSucceededAsync(EdgeTaskReceiptContext context, string message, Dictionary<string, object>? result, CancellationToken cancellationToken = default);

    Task ReportFailedAsync(EdgeTaskReceiptContext context, string message, Dictionary<string, object>? result, CancellationToken cancellationToken = default);

    Task ReportTimedOutAsync(EdgeTaskReceiptContext context, string message, Dictionary<string, object>? result, CancellationToken cancellationToken = default);
}

public sealed class EdgeTaskReceiptReporter : IEdgeTaskReceiptReporter
{
    private const int ApiSuccessCode = 10000;
    private const string ContractVersion = "edge-task-v1";
    private const string Accepted = "Accepted";
    private const string Running = "Running";
    private const string Succeeded = "Succeeded";
    private const string Failed = "Failed";
    private const string TimedOut = "TimedOut";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EdgeTaskReceiptReporter> _logger;

    public EdgeTaskReceiptReporter(IHttpClientFactory httpClientFactory, ILogger<EdgeTaskReceiptReporter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task ReportAcceptedAsync(EdgeTaskReceiptContext context, string message, CancellationToken cancellationToken = default)
    {
        var payload = CreatePayload(context, Accepted, message, 0, null);
        var path = $"api/EdgeTask/Dispatch/{Uri.EscapeDataString(context.AccessToken)}/Accept";
        await PostReceiptAsync(context.BaseUrl, path, payload, cancellationToken);

        _logger.LogInformation("已接受边缘任务 {TaskId}，目标 {TargetKey}。", context.TaskId, context.TargetKey);
    }

    public Task ReportRunningAsync(EdgeTaskReceiptContext context, string message, int progress, CancellationToken cancellationToken = default)
        => ReportAsync(context, Running, message, Math.Clamp(progress, 1, 99), null, cancellationToken);

    public Task ReportSucceededAsync(EdgeTaskReceiptContext context, string message, Dictionary<string, object>? result, CancellationToken cancellationToken = default)
        => ReportAsync(context, Succeeded, message, 100, result, cancellationToken);

    public Task ReportFailedAsync(EdgeTaskReceiptContext context, string message, Dictionary<string, object>? result, CancellationToken cancellationToken = default)
        => ReportAsync(context, Failed, message, 100, result, cancellationToken);

    public Task ReportTimedOutAsync(EdgeTaskReceiptContext context, string message, Dictionary<string, object>? result, CancellationToken cancellationToken = default)
        => ReportAsync(context, TimedOut, message, 100, result, cancellationToken);

    private async Task ReportAsync(
        EdgeTaskReceiptContext context,
        string status,
        string message,
        int? progress,
        Dictionary<string, object>? result,
        CancellationToken cancellationToken)
    {
        var payload = CreatePayload(context, status, message, progress, result);
        await PostReceiptAsync(context.BaseUrl, "api/EdgeTask/Receipt", payload, cancellationToken);

        _logger.LogInformation("已上报边缘任务回执 {TaskId}，目标 {TargetKey}，状态 {Status}。", context.TaskId, context.TargetKey, status);
    }

    private static EdgeTaskReceiptPayload CreatePayload(
        EdgeTaskReceiptContext context,
        string status,
        string message,
        int? progress,
        Dictionary<string, object>? result)
    {
        if (context.TaskId == Guid.Empty)
        {
            throw new InvalidOperationException("边缘任务回执缺少 taskId。");
        }

        if (string.IsNullOrWhiteSpace(context.TargetKey))
        {
            throw new InvalidOperationException("边缘任务回执缺少 targetKey。");
        }

        return new EdgeTaskReceiptPayload
        {
            ContractVersion = ContractVersion,
            TaskId = context.TaskId,
            TargetType = string.IsNullOrWhiteSpace(context.TargetType) ? "GatewayRuntime" : context.TargetType,
            TargetKey = context.TargetKey,
            RuntimeType = context.RuntimeType,
            InstanceId = context.InstanceId,
            Status = status,
            Message = message,
            ReportedAt = DateTime.UtcNow,
            Progress = progress,
            Result = result ?? new Dictionary<string, object>(),
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "edge-task-dispatch-worker"
            }
        };
    }

    private async Task PostReceiptAsync(string baseUrl, string path, EdgeTaskReceiptPayload payload, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(EdgeTaskReceiptReporter));
        using var response = await client.PostAsJsonAsync(BuildUri(baseUrl, path), payload, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var apiResult = await response.Content.ReadFromJsonAsync<EdgeTaskApiResult>(JsonOptions, cancellationToken);
        if (apiResult is null)
        {
            throw new InvalidOperationException("IoTSharp 返回了空的任务回执响应。");
        }

        if (apiResult.Code != ApiSuccessCode)
        {
            throw new InvalidOperationException($"IoTSharp 拒绝任务回执，代码 {apiResult.Code}：{apiResult.Msg}");
        }
    }

    private static string BuildUri(string baseUrl, string path)
        => $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private sealed class EdgeTaskReceiptPayload
    {
        public string ContractVersion { get; init; } = string.Empty;

        public Guid TaskId { get; init; }

        public string TargetType { get; init; } = string.Empty;

        public string TargetKey { get; init; } = string.Empty;

        public string RuntimeType { get; init; } = string.Empty;

        public string InstanceId { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public DateTime ReportedAt { get; init; }

        public int? Progress { get; init; }

        public Dictionary<string, object> Result { get; init; } = [];

        public Dictionary<string, string> Metadata { get; init; } = [];
    }

    private sealed class EdgeTaskApiResult
    {
        public int Code { get; init; }

        public string Msg { get; init; } = string.Empty;
    }
}
