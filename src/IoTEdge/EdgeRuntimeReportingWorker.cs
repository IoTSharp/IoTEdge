using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using IoTEdge.Application;
using IoTEdge.Domain;
using Microsoft.Extensions.Options;

namespace IoTEdge;

public sealed class EdgeReportingOptions
{
    public bool Enabled { get; set; } = true;
    public string RuntimeType { get; set; } = "gateway";
    public string? RuntimeName { get; set; }
    public string? InstanceId { get; set; }
    public string? BaseUrl { get; set; }
    public string? AccessToken { get; set; }
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int RetryDelaySeconds { get; set; } = 5;
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class EdgeRuntimeReportingWorker : BackgroundService
{
    // IoTSharp ApiResult uses 10000 as the logical success code even when HTTP status is 200.
    private const int ApiSuccessCode = 10000;
    private const string TaskContractVersion = "edge-task-v1";
    private const string GatewayRuntimeTargetType = "GatewayRuntime";
    private const string DefaultPollingTaskName = "gateway-polling";
    private const int IPv4Priority = 0;
    private const int IPv6Priority = 1;
    private static readonly TimeSpan MaxCancelAfterDelay = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEdgeTaskReceiptReporter _receiptReporter;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<EdgeRuntimeReportingWorker> _logger;
    private readonly IOptionsMonitor<EdgeReportingOptions> _optionsMonitor;
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly string _version;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private bool _registrationPending = true;
    private bool _capabilitiesPending = true;
    private string? _lastRegistrationSignature;
    private string? _lastCapabilitiesSignature;
    private DateTimeOffset? _lastHeartbeatAt;
    private readonly Channel<EdgeTaskRequestPayload> _dispatchQueue = Channel.CreateUnbounded<EdgeTaskRequestPayload>();
    private readonly HashSet<Guid> _queuedTaskIds = [];
    private bool _missingConfigurationLogged;
    private bool _reportingDisabledLogged;

    public EdgeRuntimeReportingWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IEdgeTaskReceiptReporter receiptReporter,
        IHostEnvironment hostEnvironment,
        IOptionsMonitor<EdgeReportingOptions> optionsMonitor,
        ILogger<EdgeRuntimeReportingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _receiptReporter = receiptReporter;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
        _optionsMonitor = optionsMonitor;
        _version = ResolveVersion();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = RetryDelay(_optionsMonitor.CurrentValue);

            try
            {
                delay = await ExecuteIterationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _registrationPending = true;
                _capabilitiesPending = true;
                delay = RetryDelay(_optionsMonitor.CurrentValue);
                _logger.LogError(exception, "IoTEdge 上报迭代失败。");
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task<TimeSpan> ExecuteIterationAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGatewayRepository>();
        var driverCatalog = scope.ServiceProvider.GetRequiredService<DriverCatalogService>();
        var runtimeService = scope.ServiceProvider.GetRequiredService<GatewayRuntimeService>();

        var channels = await repository.GetChannelsAsync(cancellationToken);
        var devices = await repository.GetDevicesAsync(cancellationToken);
        var points = await repository.GetPointsAsync(cancellationToken);
        var pollingTasks = await repository.GetPollingTasksAsync(cancellationToken);
        var transformRules = await repository.GetTransformRulesAsync(cancellationToken);
        var uploadChannels = await repository.GetUploadChannelsAsync(cancellationToken);
        var uploadRoutes = await repository.GetUploadRoutesAsync(cancellationToken);
        var options = _optionsMonitor.CurrentValue;

        if (!options.Enabled)
        {
            if (!_reportingDisabledLogged)
            {
                _logger.LogInformation("IoTEdge 上报已禁用。");
                _reportingDisabledLogged = true;
            }

            _registrationPending = true;
            _capabilitiesPending = true;
            _lastRegistrationSignature = null;
            _lastCapabilitiesSignature = null;
            _lastHeartbeatAt = null;
            return RetryDelay(options);
        }

        _reportingDisabledLogged = false;

        var edgeTarget = ResolveEdgeTarget(options, uploadChannels);
        if (edgeTarget is null)
        {
            if (!_missingConfigurationLogged)
            {
                _missingConfigurationLogged = true;
                _logger.LogWarning("IoTEdge 上报已跳过，因为基础地址或访问令牌未配置。");
            }
            _registrationPending = true;
            _capabilitiesPending = true;
            _lastRegistrationSignature = null;
            _lastHeartbeatAt = null;
            return RetryDelay(options);
        }

        _missingConfigurationLogged = false;

        var snapshot = BuildRuntimeSnapshot(options, channels, devices, points, pollingTasks, uploadChannels, uploadRoutes);
        var capabilities = BuildCapabilities(driverCatalog, channels, points, pollingTasks, transformRules, uploadChannels, uploadRoutes);
        var registrationSignature = ComputeRegistrationSignature(edgeTarget, snapshot.Registration);
        var capabilitiesSignature = ComputeCapabilitiesSignature(capabilities);

        if (!string.Equals(_lastRegistrationSignature, registrationSignature, StringComparison.Ordinal))
        {
            _registrationPending = true;
            _capabilitiesPending = true;
            _lastHeartbeatAt = null;
        }

        if (!string.Equals(_lastCapabilitiesSignature, capabilitiesSignature, StringComparison.Ordinal))
        {
            _capabilitiesPending = true;
        }

        if (_registrationPending)
        {
            await PostAsync(edgeTarget, "Register", snapshot.Registration, cancellationToken);
            _registrationPending = false;
            _capabilitiesPending = true;
            _lastRegistrationSignature = registrationSignature;
            _logger.LogInformation("已向 IoTEdge 注册网关运行时，实例 {InstanceId}。", snapshot.Registration.InstanceId);
        }

        if (_capabilitiesPending)
        {
            await PostAsync(edgeTarget, "Capabilities", capabilities, cancellationToken);
            _capabilitiesPending = false;
            _lastCapabilitiesSignature = capabilitiesSignature;
            _logger.LogInformation("已向 IoTEdge 上报网关能力。");
        }

        if (!_lastHeartbeatAt.HasValue || DateTimeOffset.UtcNow - _lastHeartbeatAt.Value >= HeartbeatInterval(options))
        {
            await PostAsync(edgeTarget, "Heartbeat", snapshot.Heartbeat, cancellationToken);
            _lastHeartbeatAt = DateTimeOffset.UtcNow;
            _logger.LogDebug("已向 IoTEdge 发送网关心跳。");
        }

        await EnqueuePendingDispatchesAsync(edgeTarget, cancellationToken);
        await DrainDispatchQueueAsync(runtimeService, edgeTarget, options.RuntimeType, snapshot, points, cancellationToken);

        return NextDelay(options);
    }

    private async Task EnqueuePendingDispatchesAsync(
        EdgeTarget edgeTarget,
        CancellationToken cancellationToken)
    {
        var requests = await PullPendingDispatchAsync(edgeTarget, cancellationToken);
        if (requests.Count == 0)
        {
            return;
        }

        foreach (var request in requests)
        {
            if (request.TaskId == Guid.Empty || !_queuedTaskIds.Add(request.TaskId))
            {
                continue;
            }

            await _dispatchQueue.Writer.WriteAsync(request, cancellationToken);
        }
    }

    private async Task DrainDispatchQueueAsync(
        GatewayRuntimeService runtimeService,
        EdgeTarget edgeTarget,
        string runtimeType,
        EdgeRuntimeSnapshot snapshot,
        IReadOnlyCollection<Point> points,
        CancellationToken cancellationToken)
    {
        while (_dispatchQueue.Reader.TryRead(out var request))
        {
            var context = CreateReceiptContext(edgeTarget, runtimeType, snapshot.Registration.InstanceId, request);

            try
            {
                if (IsExpired(request, out var expiredAt))
                {
                    await _receiptReporter.ReportTimedOutAsync(
                        context,
                        $"边缘任务已超过过期时间 {expiredAt:O}，执行端不再受理。",
                        CreateTimeoutResult(request, expiredAt),
                        cancellationToken);
                    continue;
                }

                await _receiptReporter.ReportAcceptedAsync(context, "边缘任务已进入执行队列。", cancellationToken);
                await _receiptReporter.ReportRunningAsync(context, "边缘任务开始执行。", 10, cancellationToken);

                using var executionToken = CreateExecutionCancellationToken(request, cancellationToken);
                var deviceId = ResolveDeviceId(request.Address);
                var result = await ExecuteDispatchAsync(runtimeService, request, deviceId, points, executionToken.Token);

                if (IsExpired(request, out expiredAt))
                {
                    await _receiptReporter.ReportTimedOutAsync(
                        context,
                        $"边缘任务执行完成时已超过过期时间 {expiredAt:O}。",
                        CreateTimeoutResult(request, expiredAt),
                        cancellationToken);
                    continue;
                }

                await _receiptReporter.ReportSucceededAsync(context, result.Message, result.Payload, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && IsExpired(request, out var expiredAt))
            {
                await _receiptReporter.ReportTimedOutAsync(
                    context,
                    $"边缘任务执行超过过期时间 {expiredAt:O}。",
                    CreateTimeoutResult(request, expiredAt),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                await _receiptReporter.ReportFailedAsync(
                    context,
                    exception.Message,
                    new Dictionary<string, object>
                    {
                        ["exception"] = exception.GetType().Name,
                        ["taskType"] = request.TaskType ?? string.Empty
                    },
                    cancellationToken);
            }
            finally
            {
                _queuedTaskIds.Remove(request.TaskId);
            }
        }
    }

    private async Task<List<EdgeTaskRequestPayload>> PullPendingDispatchAsync(EdgeTarget edgeTarget, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(EdgeRuntimeReportingWorker));
        client.BaseAddress = new Uri(edgeTarget.BaseUrl, UriKind.Absolute);
        using var response = await client.GetAsync($"api/EdgeTask/Dispatch/{Uri.EscapeDataString(edgeTarget.AccessToken)}", cancellationToken);
        response.EnsureSuccessStatusCode();

        var apiResult = await response.Content.ReadFromJsonAsync<EdgeApiResult<List<EdgeTaskRequestPayload>>>(JsonOptions, cancellationToken);
        return apiResult is { Code: ApiSuccessCode, Data: not null } ? apiResult.Data : [];
    }

    private static Guid ResolveDeviceId(EdgeTaskAddressPayload address)
    {
        if (address.DeviceId is { } explicitDeviceId && explicitDeviceId != Guid.Empty)
        {
            return explicitDeviceId;
        }

        var deviceIdSegment = address.TargetKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return Guid.TryParse(deviceIdSegment, out var parsedDeviceId) ? parsedDeviceId : Guid.Empty;
    }

    private static async Task<DispatchExecutionResult> ExecuteDispatchAsync(
        GatewayRuntimeService runtimeService,
        EdgeTaskRequestPayload request,
        Guid deviceId,
        IReadOnlyCollection<Point> points,
        CancellationToken cancellationToken)
    {
        return request.TaskType switch
        {
            "HealthProbe" => new DispatchExecutionResult("网关运行时健康探测已完成。", new Dictionary<string, object>
            {
                ["checkedAtUtc"] = DateTime.UtcNow,
                ["taskType"] = request.TaskType,
                ["contractVersion"] = string.IsNullOrWhiteSpace(request.ContractVersion) ? TaskContractVersion : request.ContractVersion
            }),
            "ConfigPush" => await ExecuteConfigPushAsync(runtimeService, request, deviceId, points, cancellationToken),
            _ => throw new InvalidOperationException($"不支持的任务类型：{request.TaskType}")
        };
    }

    private static async Task<DispatchExecutionResult> ExecuteConfigPushAsync(
        GatewayRuntimeService runtimeService,
        EdgeTaskRequestPayload request,
        Guid deviceId,
        IReadOnlyCollection<Point> points,
        CancellationToken cancellationToken)
    {
        if (deviceId == Guid.Empty)
        {
            throw new InvalidOperationException("配置下发任务需要可解析的 Gateway 设备 ID。");
        }

        var parameters = GetTaskParameters(request);
        if (!TryGetGuid(parameters, "pointId", out var pointId))
        {
            throw new InvalidOperationException("配置下发任务需要 parameters.pointId。");
        }

        var point = points.FirstOrDefault(item => item.Id == pointId && item.DeviceId == deviceId)
            ?? throw new InvalidOperationException($"设备“{deviceId}”上未找到点位“{pointId}”。");

        parameters.TryGetValue("value", out var value);
        var writeResult = await runtimeService.ExecutePointWriteAsync(deviceId, point.Id, value, cancellationToken);
        return new DispatchExecutionResult(
            writeResult.Quality == QualityStatus.Good ? "配置下发已完成。" : "配置下发已完成，但质量降级。",
            new Dictionary<string, object>
            {
                ["pointId"] = point.Id,
                ["address"] = point.Address,
                ["quality"] = writeResult.Quality.ToString(),
                ["errorMessage"] = writeResult.ErrorMessage ?? string.Empty
            });
    }

    private static EdgeTaskReceiptContext CreateReceiptContext(
        EdgeTarget edgeTarget,
        string defaultRuntimeType,
        string defaultInstanceId,
        EdgeTaskRequestPayload request)
    {
        var address = request.Address ?? new EdgeTaskAddressPayload();
        var deviceId = ResolveDeviceId(address);
        var runtimeType = FirstNonEmpty(address.RuntimeType, [defaultRuntimeType]) ?? string.Empty;
        var instanceId = FirstNonEmpty(address.InstanceId, [defaultInstanceId]) ?? string.Empty;
        var targetType = FirstNonEmpty(address.TargetType, [GatewayRuntimeTargetType]) ?? GatewayRuntimeTargetType;
        var fallbackTargetKey = deviceId == Guid.Empty ? null : $"{deviceId}:{runtimeType}:{instanceId}";
        var targetKey = FirstNonEmpty(address.TargetKey, [fallbackTargetKey]) ?? string.Empty;

        return new EdgeTaskReceiptContext(
            edgeTarget.BaseUrl,
            edgeTarget.AccessToken,
            request.TaskId,
            targetType,
            targetKey,
            runtimeType,
            instanceId);
    }

    private static CancellationTokenSource CreateExecutionCancellationToken(
        EdgeTaskRequestPayload request,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var expireAt = GetExpireAtUtc(request);
        if (expireAt is null)
        {
            return source;
        }

        var delay = expireAt.Value - DateTime.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            source.Cancel();
            return source;
        }

        if (delay <= MaxCancelAfterDelay)
        {
            source.CancelAfter(delay);
        }

        return source;
    }

    private static bool IsExpired(EdgeTaskRequestPayload request, out DateTime expiredAt)
    {
        var expireAt = GetExpireAtUtc(request);
        if (expireAt is not null && expireAt.Value <= DateTime.UtcNow)
        {
            expiredAt = expireAt.Value;
            return true;
        }

        expiredAt = default;
        return false;
    }

    private static DateTime? GetExpireAtUtc(EdgeTaskRequestPayload request)
    {
        var expireAt = request.ExpireAt ?? request.ExpiredAt;
        return expireAt?.ToUniversalTime();
    }

    private static Dictionary<string, object> CreateTimeoutResult(EdgeTaskRequestPayload request, DateTime expiredAt)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["taskType"] = request.TaskType ?? string.Empty,
            ["expiredAtUtc"] = expiredAt,
            ["contractVersion"] = string.IsNullOrWhiteSpace(request.ContractVersion) ? TaskContractVersion : request.ContractVersion
        };

    private static Dictionary<string, object> GetTaskParameters(EdgeTaskRequestPayload request)
    {
        var source = request.Parameters is { Count: > 0 }
            ? request.Parameters
            : request.Payload ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        return source.ToDictionary(
            pair => pair.Key,
            pair => NormalizeJsonValue(pair.Value) ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetGuid(IReadOnlyDictionary<string, object> values, string key, out Guid value)
    {
        value = Guid.Empty;
        if (!values.TryGetValue(key, out var raw))
        {
            return false;
        }

        raw = NormalizeJsonValue(raw);
        if (raw is Guid guid)
        {
            value = guid;
            return value != Guid.Empty;
        }

        return Guid.TryParse(Convert.ToString(raw), out value) && value != Guid.Empty;
    }

    private static object? NormalizeJsonValue(object? value)
    {
        if (value is not JsonElement element)
        {
            return value;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => element.Deserialize<Dictionary<string, object>>(JsonOptions) ?? new Dictionary<string, object>(),
            JsonValueKind.Array => element.Deserialize<object[]>(JsonOptions) ?? Array.Empty<object>(),
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }

    private async Task PostAsync<TPayload>(EdgeTarget edgeTarget, string action, TPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(EdgeRuntimeReportingWorker));
            client.BaseAddress = new Uri(edgeTarget.BaseUrl, UriKind.Absolute);
            using var response = await client.PostAsJsonAsync(
                $"api/Edge/{Uri.EscapeDataString(edgeTarget.AccessToken)}/{action}",
                payload,
                JsonOptions,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var apiResult = await response.Content.ReadFromJsonAsync<EdgeApiResult>(JsonOptions, cancellationToken);
            if (apiResult is null)
            {
                throw new InvalidOperationException($"IoTEdge {action} 返回了空响应。");
            }

            if (apiResult.Code != ApiSuccessCode)
            {
                throw new InvalidOperationException($"IoTEdge {action} 失败，代码 {apiResult.Code}：{apiResult.Msg}");
            }
        }
        catch (Exception)
        {
            _registrationPending = true;
            _capabilitiesPending = true;
            throw;
        }
    }

    private EdgeRuntimeSnapshot BuildRuntimeSnapshot(
        EdgeReportingOptions options,
        IReadOnlyCollection<GatewayChannel> channels,
        IReadOnlyCollection<Device> devices,
        IReadOnlyCollection<Point> points,
        IReadOnlyCollection<PollingTask> pollingTasks,
        IReadOnlyCollection<UploadChannel> uploadChannels,
        IReadOnlyCollection<UploadRoute> uploadRoutes)
    {
        var hostName = ResolveHostName();
        var runtimeName = string.IsNullOrWhiteSpace(options.RuntimeName) ? hostName : options.RuntimeName.Trim();
        var instanceId = string.IsNullOrWhiteSpace(options.InstanceId)
            ? CreateStableInstanceId(hostName, _hostEnvironment.ApplicationName, _hostEnvironment.ContentRootPath)
            : options.InstanceId.Trim();
        var metadata = BuildMetadata(options);
        var metrics = BuildMetrics(channels, devices, points, pollingTasks, uploadChannels, uploadRoutes);
        var ipAddress = ResolveIpAddress();
        var uptimeSeconds = Math.Max(0L, (long)_uptime.Elapsed.TotalSeconds);
        var heartbeatAt = DateTimeOffset.UtcNow;

        return new EdgeRuntimeSnapshot(
            new EdgeRegistrationRequest(
                options.RuntimeType,
                runtimeName,
                _version,
                instanceId,
                ResolvePlatform(),
                hostName,
                ipAddress,
                metadata),
            new EdgeHeartbeatRequest(
                heartbeatAt.UtcDateTime,
                "Running",
                true,
                uptimeSeconds,
                ipAddress,
                metrics));
    }

    private EdgeCapabilityReportRequest BuildCapabilities(
        DriverCatalogService driverCatalog,
        IReadOnlyCollection<GatewayChannel> channels,
        IReadOnlyCollection<Point> points,
        IReadOnlyCollection<PollingTask> pollingTasks,
        IReadOnlyCollection<TransformRule> transformRules,
        IReadOnlyCollection<UploadChannel> uploadChannels,
        IReadOnlyCollection<UploadRoute> uploadRoutes)
    {
        var protocols = driverCatalog.GetDrivers()
            .Select(driver => driver.Code)
            .Concat(channels.Where(channel => channel.Enabled).Select(channel => channel.DriverCode))
            .Concat(uploadChannels.Where(channel => channel.Enabled).Select(channel => channel.Protocol switch
            {
                UploadProtocol.Http => "http",
                UploadProtocol.IoTSharp => "iotsharp",
                UploadProtocol.ThingsBoard => "thingsboard",
                UploadProtocol.SonnetDb => "sonnetdb",
                UploadProtocol.InfluxDb => "influxdb",
                UploadProtocol.IotSharpMqtt => "mqtt",
                UploadProtocol.IotSharpDeviceHttp => "iotsharpdevicehttp",
                _ => channel.Protocol.ToString()
            }))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var features = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "polling",
            "runtime-reporting",
            "edge-task-v1",
            "edge-task-dispatch",
            "edge-task-receipts"
        };

        if (points.Any(point => point.Enabled && point.AccessMode is PointAccessMode.Write or PointAccessMode.ReadWrite))
        {
            features.Add("point-write");
        }

        if (transformRules.Any(rule => rule.Enabled))
        {
            features.Add("transform-rules");
        }

        if (uploadRoutes.Any(route => route.Enabled))
        {
            features.Add("upload-routing");
        }

        if (uploadChannels.Any(channel => channel.Enabled && channel.Protocol == UploadProtocol.Http))
        {
            features.Add("http-upload");
        }

        if (uploadChannels.Any(channel => channel.Enabled && channel.Protocol == UploadProtocol.IoTSharp))
        {
            features.Add("iotsharp-upload");
        }

        if (uploadChannels.Any(channel => channel.Enabled && channel.Protocol == UploadProtocol.ThingsBoard))
        {
            features.Add("thingsboard-upload");
        }

        if (uploadChannels.Any(channel => channel.Enabled && channel.Protocol == UploadProtocol.IotSharpMqtt))
        {
            features.Add("iotsharp-mqtt-upload");
        }

        if (uploadChannels.Any(channel => channel.Enabled && channel.Protocol == UploadProtocol.IotSharpDeviceHttp))
        {
            features.Add("iotsharp-device-http-upload");
        }

        if (uploadChannels.Any(channel => channel.Enabled && channel.Protocol == UploadProtocol.SonnetDb))
        {
            features.Add("sonnetdb-upload");
        }

        if (uploadChannels.Any(channel => channel.Enabled && channel.Protocol == UploadProtocol.InfluxDb))
        {
            features.Add("influxdb-upload");
        }

        var pollingTaskNames = pollingTasks.Where(task => task.Enabled)
            .Select(task => task.Name.Trim())
            .Where(task => !string.IsNullOrWhiteSpace(task))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(task => task, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (pollingTaskNames.Length == 0)
        {
            pollingTaskNames = [DefaultPollingTaskName];
        }

        var supportedTaskTypes = new[]
        {
            "ConfigPush",
            "HealthProbe"
        };

        var metadata = new Dictionary<string, object>
        {
            ["pollingTasks"] = pollingTaskNames,
            ["taskReceiptContractVersion"] = TaskContractVersion,
            ["taskReceiptStatuses"] = new[] { "Accepted", "Running", "Succeeded", "Failed", "TimedOut" }
        };

        return new EdgeCapabilityReportRequest(
            protocols,
            features.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            supportedTaskTypes,
            metadata);
    }

    private EdgeTarget? ResolveEdgeTarget(EdgeReportingOptions options, IReadOnlyCollection<UploadChannel> uploadChannels)
    {
        var reportingChannels = uploadChannels.Where(channel =>
            channel.Enabled &&
            channel.Protocol is UploadProtocol.Http or
                UploadProtocol.IoTSharp or
                UploadProtocol.IotSharpMqtt or
                UploadProtocol.IotSharpDeviceHttp);

        var accessToken = FirstNonEmpty(
            options.AccessToken,
            reportingChannels.SelectMany(channel =>
            {
                var settings = GatewayJson.Parse(channel.SettingsJson);
                return new[]
                {
                    GatewayJson.Get(settings, "token"),
                    GatewayJson.Get(settings, "accessToken"),
                    GatewayJson.Get(settings, "gatewayAccessToken"),
                    channel.Protocol == UploadProtocol.IotSharpMqtt ? GatewayJson.Get(settings, "username") : null
                };
            }));

        var baseUrl = FirstNonEmpty(
            options.BaseUrl,
            reportingChannels.SelectMany(channel =>
            {
                var settings = GatewayJson.Parse(channel.SettingsJson);
                var configuredBaseUrl = GatewayJson.Get(settings, "edgeBaseUrl");
                var endpointBaseUrl = TryGetHttpBaseUrl(channel.Endpoint);
                return new[] { configuredBaseUrl, endpointBaseUrl };
            }));

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return new EdgeTarget(accessToken.Trim(), NormalizeBaseUrl(baseUrl));
    }

    private Dictionary<string, string> BuildMetadata(EdgeReportingOptions options)
    {
        var metadata = new Dictionary<string, string>(options.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["applicationName"] = _hostEnvironment.ApplicationName,
            ["environment"] = _hostEnvironment.EnvironmentName,
            ["startedAtUtc"] = _startedAt.ToString("O")
        };

        return metadata;
    }

    private Dictionary<string, object> BuildMetrics(
        IReadOnlyCollection<GatewayChannel> channels,
        IReadOnlyCollection<Device> devices,
        IReadOnlyCollection<Point> points,
        IReadOnlyCollection<PollingTask> pollingTasks,
        IReadOnlyCollection<UploadChannel> uploadChannels,
        IReadOnlyCollection<UploadRoute> uploadRoutes)
    {
        _currentProcess.Refresh();

        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["workingSetBytes"] = _currentProcess.WorkingSet64,
            ["privateMemoryBytes"] = _currentProcess.PrivateMemorySize64,
            ["threadCount"] = _currentProcess.Threads.Count,
            ["gcHeapBytes"] = GC.GetTotalMemory(false),
            ["enabledChannelCount"] = channels.Count(channel => channel.Enabled),
            ["enabledDeviceCount"] = devices.Count(device => device.Enabled),
            ["enabledPointCount"] = points.Count(point => point.Enabled),
            ["enabledPollingTaskCount"] = pollingTasks.Count(task => task.Enabled),
            ["enabledUploadChannelCount"] = uploadChannels.Count(channel => channel.Enabled),
            ["enabledUploadRouteCount"] = uploadRoutes.Count(route => route.Enabled)
        };
    }

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(EdgeRuntimeReportingWorker).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static string ResolvePlatform()
        => $"{RuntimeInformation.OSDescription.Trim()}-{RuntimeInformation.OSArchitecture}".Trim();

    private static string ResolveHostName()
        => Dns.GetHostName();

    private static string ResolveIpAddress()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface =>
                networkInterface.OperationalStatus == OperationalStatus.Up &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address =>
                !IPAddress.IsLoopback(address) &&
                address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Select(address => address.ToString())
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetAddressPriority)
            .ToArray();

        return addresses.FirstOrDefault() ?? string.Empty;
    }

    private static string CreateStableInstanceId(string hostName, string applicationName, string contentRootPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{hostName}:{applicationName}:{contentRootPath}"));
        return Convert.ToHexString(bytes[..16]).ToLowerInvariant();
    }

    private static string ComputeCapabilitiesSignature(EdgeCapabilityReportRequest capabilities)
        => JsonSerializer.Serialize(capabilities, JsonOptions);

    private static string ComputeRegistrationSignature(EdgeTarget edgeTarget, EdgeRegistrationRequest registration)
        => JsonSerializer.Serialize(
            new
            {
                edgeTarget.BaseUrl,
                edgeTarget.AccessToken,
                registration.RuntimeType,
                registration.RuntimeName,
                registration.InstanceId,
                registration.Version,
                registration.Platform,
                registration.HostName,
                registration.IpAddress,
                registration.Metadata
            },
            JsonOptions);

    private static string? TryGetHttpBaseUrl(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }

    private static string NormalizeBaseUrl(string baseUrl)
        => baseUrl.Trim().TrimEnd('/') + "/";

    private static int GetAddressPriority(string address)
        => address.Contains(':') ? IPv6Priority : IPv4Priority;

    private static string? FirstNonEmpty(string? first, IEnumerable<string?> others)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }

        return others.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static TimeSpan HeartbeatInterval(EdgeReportingOptions options)
        => TimeSpan.FromSeconds(Math.Max(options.HeartbeatIntervalSeconds, 1));

    private static TimeSpan RetryDelay(EdgeReportingOptions options)
        => TimeSpan.FromSeconds(Math.Max(options.RetryDelaySeconds, 1));

    private TimeSpan NextDelay(EdgeReportingOptions options)
    {
        if (_registrationPending || _capabilitiesPending || !_lastHeartbeatAt.HasValue)
        {
            return RetryDelay(options);
        }

        var nextHeartbeatAt = _lastHeartbeatAt.Value + HeartbeatInterval(options);
        var delay = nextHeartbeatAt - DateTimeOffset.UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private sealed record EdgeTarget(string AccessToken, string BaseUrl);

    private sealed record DispatchExecutionResult(string Message, Dictionary<string, object> Payload);

    private sealed record EdgeRuntimeSnapshot(EdgeRegistrationRequest Registration, EdgeHeartbeatRequest Heartbeat);

    private sealed record EdgeRegistrationRequest(
        string RuntimeType,
        string RuntimeName,
        string Version,
        string InstanceId,
        string Platform,
        string HostName,
        string IpAddress,
        Dictionary<string, string> Metadata);

    private sealed record EdgeHeartbeatRequest(
        DateTime Timestamp,
        string Status,
        bool Healthy,
        long UptimeSeconds,
        string IpAddress,
        Dictionary<string, object> Metrics);

    private sealed record EdgeCapabilityReportRequest(
        string[] Protocols,
        string[] Features,
        string[] Tasks,
        Dictionary<string, object>? Metadata);

    private sealed class EdgeApiResult
    {
        public int Code { get; set; }
        public string Msg { get; set; } = string.Empty;
    }

    private sealed class EdgeApiResult<T>
    {
        public int Code { get; set; }
        public string Msg { get; set; } = string.Empty;
        public T? Data { get; set; }
    }

    private sealed class EdgeTaskRequestPayload
    {
        public string ContractVersion { get; init; } = TaskContractVersion;

        public Guid TaskId { get; init; }

        public string TaskType { get; init; } = string.Empty;

        public EdgeTaskAddressPayload Address { get; init; } = new();

        public Dictionary<string, object>? Parameters { get; init; }

        public Dictionary<string, object>? Payload { get; init; }

        public Dictionary<string, string>? Metadata { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime? ExpireAt { get; init; }

        public DateTime? ExpiredAt { get; init; }
    }

    private sealed class EdgeTaskAddressPayload
    {
        public string TargetType { get; init; } = GatewayRuntimeTargetType;

        public string TargetKey { get; init; } = string.Empty;

        public Guid? DeviceId { get; init; }

        public string AccessToken { get; init; } = string.Empty;

        public string RuntimeType { get; init; } = string.Empty;

        public string InstanceId { get; init; } = string.Empty;

        public string RuntimeName { get; init; } = string.Empty;
    }
}
