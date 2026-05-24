using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IoTEdge.Application;
using IoTEdge.Domain;

namespace IoTEdge;

internal static class GatewayCollectionConfigurationMapper
{
    public static GatewayConfigurationSnapshot Map(EdgeCollectionConfigurationContract configuration, EdgeReportingOptions options)
    {
        if (configuration == null)
        {
            throw new InvalidOperationException("采集配置载荷是必填项。");
        }

        GatewayCollectionConfigurationValidator.ValidateStructuralKeys(configuration);

        var edgeNodeId = configuration.EdgeNodeId;
        var normalizedBaseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? string.Empty : NormalizeBaseUrl(options.BaseUrl);
        var accessToken = options.AccessToken ?? string.Empty;
        var uploadTargets = ResolveUploadTargets(configuration, normalizedBaseUrl, accessToken);
        var uploadTargetsByKey = uploadTargets.ToDictionary(target => target.TargetKey, StringComparer.OrdinalIgnoreCase);
        var uploadRoutesByPoint = BuildUploadRoutesByPoint(configuration.UploadRoutes);

        var channels = new List<GatewayChannel>();
        var devices = new List<Device>();
        var points = new List<Point>();
        var pollingTasks = new List<PollingTask>();
        var transformRules = new List<TransformRule>();
        var uploadRoutes = new List<UploadRoute>();
        var requiredTargetTypes = new HashSet<GatewayCollectionTargetType>();

        foreach (var task in configuration.Tasks ?? [])
        {
            if (string.IsNullOrWhiteSpace(task.TaskKey))
            {
                throw new InvalidOperationException("task.taskKey 为必填项。");
            }

            var channelId = CreateDeterministicGuid(edgeNodeId, "channel", task.TaskKey);
            channels.Add(new GatewayChannel
            {
                Id = channelId,
                DriverCode = ResolveDriverCode(task.Protocol),
                Name = string.IsNullOrWhiteSpace(task.Connection?.ConnectionName) ? task.TaskKey : task.Connection.ConnectionName,
                Description = $"边缘采集任务 {task.TaskKey}",
                ConnectionJson = GatewayJson.Serialize(BuildConnectionSettings(task)),
                Enabled = true
            });

            foreach (var deviceContract in task.Devices ?? [])
            {
                if (string.IsNullOrWhiteSpace(deviceContract.DeviceKey))
                {
                    throw new InvalidOperationException($"任务“{task.TaskKey}”包含未设置 deviceKey 的设备。");
                }

                var deviceId = CreateDeterministicGuid(edgeNodeId, "device", task.TaskKey, deviceContract.DeviceKey);
                var devicePointStates = new List<DevicePointState>();

                foreach (var pointContract in deviceContract.Points ?? [])
                {
                    if (string.IsNullOrWhiteSpace(pointContract.PointKey) ||
                        string.IsNullOrWhiteSpace(pointContract.PointName) ||
                        string.IsNullOrWhiteSpace(pointContract.Address))
                    {
                        throw new InvalidOperationException($"任务“{task.TaskKey}”、设备“{deviceContract.DeviceKey}”包含无效的点位定义。");
                    }

                    if (pointContract.Mapping == null || string.IsNullOrWhiteSpace(pointContract.Mapping.TargetName))
                    {
                        throw new InvalidOperationException($"任务“{task.TaskKey}”、点位“{pointContract.PointKey}”需要 mapping.targetName。");
                    }

                    var pointId = CreateDeterministicGuid(edgeNodeId, "point", task.TaskKey, deviceContract.DeviceKey, pointContract.PointKey);
                    var pointEnabled = deviceContract.Enabled;
                    var point = new Point
                    {
                        Id = pointId,
                        DeviceId = deviceId,
                        Name = pointContract.PointName,
                        Address = pointContract.Address,
                        DataType = ResolveDataType(pointContract.RawValueType),
                        AccessMode = ResolveAccessMode(pointContract.Mapping.TargetType),
                        Length = ResolvePointLength(pointContract),
                        SettingsJson = GatewayJson.Serialize(BuildPointSettings(task, deviceContract, pointContract)),
                        Enabled = pointEnabled
                    };

                    points.Add(point);
                    devicePointStates.Add(new DevicePointState(point.Id, pointEnabled, pointContract));

                    foreach (var transform in BuildTransformRules(edgeNodeId, task, deviceContract, pointContract, pointId))
                    {
                        transformRules.Add(transform);
                    }

                    var targetType = NormalizeTargetType(pointContract.Mapping.TargetType, pointContract.PointKey);
                    requiredTargetTypes.Add(targetType);

                    var pointRouteKey = BuildPointRouteKey(task.TaskKey, deviceContract.DeviceKey, pointContract.PointKey);
                    if (uploadRoutesByPoint.TryGetValue(pointRouteKey, out var explicitRoutes))
                    {
                        foreach (var routeContract in explicitRoutes.Where(route => route.Enabled))
                        {
                            if (!uploadTargetsByKey.TryGetValue(routeContract.UploadTargetKey, out var uploadTarget))
                            {
                                throw new InvalidOperationException($"任务“{task.TaskKey}”、设备“{deviceContract.DeviceKey}”、点位“{pointContract.PointKey}”的上传路由引用了不存在的上传目标“{routeContract.UploadTargetKey}”。");
                            }

                            if (!uploadTarget.Enabled)
                            {
                                continue;
                            }

                            uploadRoutes.Add(new UploadRoute
                            {
                                Id = CreateDeterministicGuid(edgeNodeId, "upload-route", task.TaskKey, deviceContract.DeviceKey, pointContract.PointKey, uploadTarget.TargetKey, ResolveRouteTargetName(routeContract, pointContract.Mapping.TargetName), targetType.ToString()),
                                PointId = pointId,
                                UploadChannelId = CreateUploadChannelId(edgeNodeId, uploadTarget.TargetKey, targetType),
                                PayloadTemplate = routeContract.PayloadTemplate,
                                Target = ResolveRouteTargetName(routeContract, pointContract.Mapping.TargetName),
                                Enabled = pointEnabled && routeContract.Enabled
                            });
                        }
                    }
                    else
                    {
                        foreach (var uploadTarget in uploadTargets.Where(target => target.Enabled))
                        {
                            uploadRoutes.Add(new UploadRoute
                            {
                                Id = CreateDeterministicGuid(edgeNodeId, "upload-route", task.TaskKey, deviceContract.DeviceKey, pointContract.PointKey, uploadTarget.TargetKey, targetType.ToString()),
                                PointId = pointId,
                                UploadChannelId = CreateUploadChannelId(edgeNodeId, uploadTarget.TargetKey, targetType),
                                PayloadTemplate = string.Empty,
                                Target = pointContract.Mapping.TargetName,
                                Enabled = pointEnabled
                            });
                        }
                    }
                }

                devices.Add(new Device
                {
                    Id = deviceId,
                    ChannelId = channelId,
                    Name = string.IsNullOrWhiteSpace(deviceContract.DeviceName) ? deviceContract.DeviceKey : deviceContract.DeviceName,
                    ExternalId = string.IsNullOrWhiteSpace(deviceContract.ExternalKey) ? deviceContract.DeviceKey : deviceContract.ExternalKey,
                    PollingIntervalSeconds = ResolveDevicePollingIntervalSeconds(devicePointStates.Select(item => item.Contract)),
                    SettingsJson = GatewayJson.Serialize(BuildDeviceSettings(deviceContract)),
                    Enabled = deviceContract.Enabled
                });

                foreach (var pollingTask in BuildPollingTasks(edgeNodeId, task, deviceContract, deviceId, devicePointStates))
                {
                    pollingTasks.Add(pollingTask);
                }
            }
        }

        var uploadChannels = BuildUploadChannels(
            requiredTargetTypes,
            uploadTargets,
            normalizedBaseUrl,
            accessToken,
            configuration.EdgeNodeId);

        return new GatewayConfigurationSnapshot(
            channels.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            devices.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            points.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            pollingTasks.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            transformRules.OrderBy(item => item.SortOrder).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            uploadChannels.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            uploadRoutes.OrderBy(item => item.Target, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IReadOnlyCollection<UploadChannel> BuildUploadChannels(
        IReadOnlyCollection<GatewayCollectionTargetType> requiredTargetTypes,
        IReadOnlyCollection<ResolvedUploadTarget> uploadTargets,
        string normalizedBaseUrl,
        string accessToken,
        Guid edgeNodeId)
    {
        var uploadChannels = new List<UploadChannel>();
        foreach (var uploadTarget in uploadTargets.Where(target => target.Enabled))
        {
            foreach (var targetType in requiredTargetTypes)
            {
                var channelSettings = BuildUploadSettings(uploadTarget, normalizedBaseUrl, accessToken, edgeNodeId, targetType);
                uploadChannels.Add(new UploadChannel
                {
                    Id = CreateUploadChannelId(edgeNodeId, uploadTarget.TargetKey, targetType),
                    Name = BuildUploadChannelName(uploadTarget.DisplayName, targetType),
                    Protocol = uploadTarget.Protocol,
                    Endpoint = ResolveUploadChannelEndpoint(uploadTarget, targetType),
                    SettingsJson = GatewayJson.Serialize(channelSettings),
                    BatchSize = Math.Max(uploadTarget.BatchSize, 1),
                    BufferingEnabled = uploadTarget.BufferingEnabled,
                    Enabled = uploadTarget.Enabled
                });
            }
        }

        return uploadChannels;
    }

    private static Dictionary<string, string?> BuildUploadSettings(
        ResolvedUploadTarget uploadTarget,
        string normalizedBaseUrl,
        string accessToken,
        Guid edgeNodeId,
        GatewayCollectionTargetType targetType)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uploadTarget.Settings)
        {
            settings[pair.Key] = pair.Value;
        }

        settings["edgeNodeId"] = edgeNodeId == Guid.Empty ? null : edgeNodeId.ToString("D");
        settings["edgeBaseUrl"] = string.IsNullOrWhiteSpace(normalizedBaseUrl) ? null : normalizedBaseUrl;
        settings["accessToken"] = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken;
        settings["targetKey"] = uploadTarget.TargetKey;
        settings["targetName"] = uploadTarget.DisplayName;
        settings["targetProtocol"] = uploadTarget.Protocol.ToString();
        settings["targetKind"] = targetType switch
        {
            GatewayCollectionTargetType.Telemetry => "telemetry",
            GatewayCollectionTargetType.Attribute => "attributes",
            _ => targetType.ToString().ToLowerInvariant()
        };

        return settings;
    }

    private static IReadOnlyCollection<ResolvedUploadTarget> ResolveUploadTargets(
        EdgeCollectionConfigurationContract configuration,
        string normalizedBaseUrl,
        string accessToken)
    {
        var configuredUploads = configuration.Uploads is { Count: > 0 }
            ? configuration.Uploads
            : configuration.Upload is not null
                ? [configuration.Upload]
                : [];

        if (configuredUploads.Count == 0 && !string.IsNullOrWhiteSpace(normalizedBaseUrl) && !string.IsNullOrWhiteSpace(accessToken))
        {
            configuredUploads =
            [
                new CollectionUploadContract
                {
                    TargetKey = "iotsharp-default",
                    DisplayName = "IoTSharp 默认目标",
                    Protocol = "IoTSharp",
                    Endpoint = normalizedBaseUrl,
                    Settings = JsonSerializer.SerializeToElement(
                        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["token"] = accessToken
                        },
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))
                }
            ];
        }

        return configuredUploads
            .Select((upload, index) => ResolveUploadTarget(upload, index, normalizedBaseUrl, accessToken))
            .ToArray();
    }

    private static ResolvedUploadTarget ResolveUploadTarget(
        CollectionUploadContract upload,
        int index,
        string normalizedBaseUrl,
        string accessToken)
    {
        var protocol = ResolveUploadProtocol(upload.Protocol);
        var displayName = string.IsNullOrWhiteSpace(upload.DisplayName)
            ? GetUploadProtocolDisplayName(protocol)
            : upload.DisplayName.Trim();
        var targetKey = string.IsNullOrWhiteSpace(upload.TargetKey)
            ? CreateUploadTargetKey(displayName, protocol, index)
            : upload.TargetKey.Trim();
        var settings = BuildUploadTargetSettings(upload, normalizedBaseUrl, accessToken, targetKey, displayName, protocol);

        return new ResolvedUploadTarget(
            targetKey,
            displayName,
            protocol,
            ResolveUploadEndpoint(upload, normalizedBaseUrl, accessToken, protocol),
            settings,
            Math.Max(upload.BatchSize, 1),
            upload.BufferingEnabled,
            upload.Enabled);
    }

    private static IReadOnlyDictionary<string, CollectionRouteContract[]> BuildUploadRoutesByPoint(IReadOnlyList<CollectionRouteContract>? routes)
    {
        if (routes is not { Count: > 0 })
        {
            return new Dictionary<string, CollectionRouteContract[]>(StringComparer.OrdinalIgnoreCase);
        }

        return routes
            .GroupBy(route => BuildPointRouteKey(route.TaskKey, route.DeviceKey, route.PointKey), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string?> BuildUploadTargetSettings(
        CollectionUploadContract upload,
        string normalizedBaseUrl,
        string accessToken,
        string targetKey,
        string displayName,
        UploadProtocol protocol)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        MergeJsonObject(settings, upload.Settings);

        settings["targetKey"] = targetKey;
        settings["targetName"] = displayName;
        settings["targetProtocol"] = protocol.ToString();
        settings["edgeBaseUrl"] = string.IsNullOrWhiteSpace(normalizedBaseUrl) ? null : normalizedBaseUrl;
        settings["accessToken"] = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken;

        return settings;
    }

    private static UploadProtocol ResolveUploadProtocol(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return UploadProtocol.IoTSharp;
        }

        return protocol.Trim().ToLowerInvariant() switch
        {
            "http" => UploadProtocol.Http,
            "mqtt" or "iotsharpmqtt" => UploadProtocol.IotSharpMqtt,
            "devicehttp" or "iotsharpdevicehttp" => UploadProtocol.IotSharpDeviceHttp,
            "iotsharp" => UploadProtocol.IoTSharp,
            "thingboard" or "thingsboard" => UploadProtocol.ThingsBoard,
            "sonnet" or "sonnetdb" => UploadProtocol.SonnetDb,
            "influx" or "influxdb" => UploadProtocol.InfluxDb,
            _ => throw new NotSupportedException($"未配置的上传协议“{protocol}”不受支持。")
        };
    }

    private static string ResolveUploadEndpoint(
        CollectionUploadContract upload,
        string normalizedBaseUrl,
        string accessToken,
        UploadProtocol uploadProtocol)
    {
        if (!string.IsNullOrWhiteSpace(upload.Endpoint))
        {
            return upload.Endpoint.Trim();
        }

        return uploadProtocol switch
        {
            UploadProtocol.IoTSharp when !string.IsNullOrWhiteSpace(normalizedBaseUrl) && !string.IsNullOrWhiteSpace(accessToken) => normalizedBaseUrl,
            UploadProtocol.IotSharpDeviceHttp when !string.IsNullOrWhiteSpace(normalizedBaseUrl) && !string.IsNullOrWhiteSpace(accessToken) => normalizedBaseUrl,
            UploadProtocol.SonnetDb or UploadProtocol.InfluxDb => string.Empty,
            _ => string.Empty
        };
    }

    private static string ResolveUploadChannelEndpoint(ResolvedUploadTarget uploadTarget, GatewayCollectionTargetType targetType)
    {
        if (uploadTarget.Protocol != UploadProtocol.IotSharpDeviceHttp)
        {
            return uploadTarget.Endpoint;
        }

        if (string.IsNullOrWhiteSpace(uploadTarget.Endpoint) ||
            uploadTarget.Endpoint.Contains("/api/Devices/", StringComparison.OrdinalIgnoreCase))
        {
            return uploadTarget.Endpoint;
        }

        var token = FirstNonEmpty(uploadTarget.Settings, "token", "accessToken", "deviceToken");
        if (string.IsNullOrWhiteSpace(token))
        {
            return uploadTarget.Endpoint;
        }

        var targetKind = targetType == GatewayCollectionTargetType.Attribute ? "Attributes" : "Telemetry";
        return new Uri(new Uri(uploadTarget.Endpoint.Trim().TrimEnd('/') + "/", UriKind.Absolute), $"api/Devices/{Uri.EscapeDataString(token)}/{targetKind}").ToString();
    }

    private static string BuildUploadChannelName(string displayName, GatewayCollectionTargetType targetType)
        => targetType switch
        {
            GatewayCollectionTargetType.Telemetry => $"{displayName} 遥测",
            GatewayCollectionTargetType.Attribute => $"{displayName} 属性",
            _ => $"{displayName} {targetType}"
        };

    private static Guid CreateUploadChannelId(Guid edgeNodeId, string targetKey, GatewayCollectionTargetType targetType)
        => CreateDeterministicGuid(edgeNodeId, "upload-channel", targetKey, targetType.ToString());

    private static string ResolveRouteTargetName(CollectionRouteContract route, string fallback)
        => string.IsNullOrWhiteSpace(route.TargetName) ? fallback : route.TargetName.Trim();

    private static string BuildPointRouteKey(string taskKey, string deviceKey, string pointKey)
        => string.Join("::", taskKey.Trim(), deviceKey.Trim(), pointKey.Trim());

    private static string CreateUploadTargetKey(string displayName, UploadProtocol protocol, int index)
    {
        var key = NormalizeKey(displayName);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = NormalizeKey(GetUploadProtocolDisplayName(protocol));
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            key = NormalizeKey(protocol.ToString());
        }

        return string.IsNullOrWhiteSpace(key)
            ? $"upload-target-{index + 1}"
            : $"{key}-{index + 1}";
    }

    private static string GetUploadProtocolDisplayName(UploadProtocol protocol)
        => protocol switch
        {
            UploadProtocol.IoTSharp or UploadProtocol.IotSharpDeviceHttp or UploadProtocol.IotSharpMqtt => "IoTSharp",
            UploadProtocol.ThingsBoard => "ThingsBoard",
            UploadProtocol.SonnetDb => "SonnetDB",
            UploadProtocol.InfluxDb => "InfluxDB",
            UploadProtocol.Http => "HTTP",
            _ => protocol.ToString()
        };

    private sealed record ResolvedUploadTarget(
        string TargetKey,
        string DisplayName,
        UploadProtocol Protocol,
        string Endpoint,
        IReadOnlyDictionary<string, string?> Settings,
        int BatchSize,
        bool BufferingEnabled,
        bool Enabled);

    private static IEnumerable<PollingTask> BuildPollingTasks(
        Guid edgeNodeId,
        CollectionTaskContract task,
        CollectionDeviceContract device,
        Guid deviceId,
        IReadOnlyCollection<DevicePointState> devicePointStates)
    {
        var groups = devicePointStates
            .GroupBy(item => new PollingKey(
                Math.Max(item.Contract.Polling?.ReadPeriodMs ?? 5000, 1000),
                string.IsNullOrWhiteSpace(item.Contract.Polling?.Group) ? "default" : item.Contract.Polling.Group!.Trim()),
                item => item)
            .OrderBy(group => group.Key.ReadPeriodMs)
            .ThenBy(group => group.Key.Group, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var pointIds = group
                .Select(item => item.PointId)
                .Where(item => item != Guid.Empty)
                .Distinct()
                .OrderBy(item => item)
                .ToArray();

            yield return new PollingTask
            {
                Id = CreateDeterministicGuid(edgeNodeId, "polling-task", task.TaskKey, device.DeviceKey, group.Key.Group, group.Key.ReadPeriodMs.ToString(CultureInfo.InvariantCulture)),
                DeviceId = deviceId,
                Name = $"{task.TaskKey}/{device.DeviceKey}/{group.Key.Group}",
                IntervalSeconds = Math.Max(1, (int)Math.Ceiling(group.Key.ReadPeriodMs / 1000d)),
                PointIdsJson = JsonSerializer.Serialize(pointIds),
                TriggerOnChange = task.ReportPolicy.DefaultTrigger == GatewayReportTriggerType.OnChange,
                BatchRead = true,
                Enabled = device.Enabled && group.Any(item => item.Enabled)
            };
        }
    }

    private static IEnumerable<TransformRule> BuildTransformRules(
        Guid edgeNodeId,
        CollectionTaskContract task,
        CollectionDeviceContract device,
        CollectionPointContract point,
        Guid pointId)
    {
        if (task.Protocol == GatewayCollectionProtocolType.Modbus)
        {
            var nextOrder = 0;
            var scale = FirstString(point.ProtocolOptions, "scale");
            if (!string.IsNullOrWhiteSpace(scale))
            {
                yield return new TransformRule
                {
                    Id = CreateDeterministicGuid(edgeNodeId, "transform", task.TaskKey, device.DeviceKey, point.PointKey, "modbus-scale"),
                    PointId = pointId,
                    Name = $"{point.PointName}-ModbusScale",
                    Kind = TransformationKind.Scale,
                    SortOrder = nextOrder,
                    ArgumentsJson = GatewayJson.Serialize(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["factor"] = scale
                    }),
                    Enabled = true
                };

                nextOrder++;
            }

            var offset = FirstString(point.ProtocolOptions, "offset");
            if (!string.IsNullOrWhiteSpace(offset))
            {
                yield return new TransformRule
                {
                    Id = CreateDeterministicGuid(edgeNodeId, "transform", task.TaskKey, device.DeviceKey, point.PointKey, "modbus-offset"),
                    PointId = pointId,
                    Name = $"{point.PointName}-ModbusOffset",
                    Kind = TransformationKind.Offset,
                    SortOrder = nextOrder,
                    ArgumentsJson = GatewayJson.Serialize(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["offset"] = offset
                    }),
                    Enabled = true
                };
            }
        }

        foreach (var transform in (point.Transforms ?? Array.Empty<ValueTransformContract>()).OrderBy(item => item.Order))
        {
            yield return new TransformRule
            {
                Id = CreateDeterministicGuid(edgeNodeId, "transform", task.TaskKey, device.DeviceKey, point.PointKey, transform.Order.ToString(CultureInfo.InvariantCulture), transform.TransformType.ToString()),
                PointId = pointId,
                Name = $"{point.PointName}-{transform.TransformType}",
                Kind = ResolveTransformKind(transform.TransformType),
                SortOrder = transform.Order,
                ArgumentsJson = GatewayJson.Serialize(BuildTransformArguments(transform)),
                Enabled = true
            };
        }
    }

    private static Dictionary<string, string?> BuildConnectionSettings(CollectionTaskContract task)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (task.Connection == null)
        {
            return settings;
        }

        switch (task.Protocol)
        {
            case GatewayCollectionProtocolType.Modbus:
                settings["timeout"] = Math.Max(task.Connection.TimeoutMs, 1).ToString(CultureInfo.InvariantCulture);
                settings["transport"] = NormalizeModbusTransport(
                    task.Connection.Transport,
                    FirstString(task.Connection.ProtocolOptions, "transport"));

                if (IsSerialModbusTransport(settings["transport"]))
                {
                    var serialPort = task.Connection.SerialPort
                        ?? FirstString(task.Connection.ProtocolOptions, "serialPort", "portName", "comPort");
                    settings["serialPort"] = Require(serialPort, $"任务“{task.TaskKey}”的 Modbus 串口传输需要 connection.serialPort。");
                }
                else
                {
                    settings["host"] = Require(task.Connection.Host, $"任务“{task.TaskKey}”的 Modbus 需要 connection.host。");
                    settings["port"] = (task.Connection.Port ?? 502).ToString(CultureInfo.InvariantCulture);
                }

                break;
            case GatewayCollectionProtocolType.SiemensS7:
                settings["host"] = Require(task.Connection.Host, $"任务“{task.TaskKey}”的西门子 S7 需要 connection.host。");
                settings["port"] = (task.Connection.Port ?? 102).ToString(CultureInfo.InvariantCulture);
                settings["timeout"] = Math.Max(task.Connection.TimeoutMs, 1).ToString(CultureInfo.InvariantCulture);
                settings["model"] = FirstString(task.Connection.ProtocolOptions, "model", "plcModel")
                    ?? throw new InvalidOperationException($"任务“{task.TaskKey}”的西门子 S7 需要 protocolOptions.model。");
                settings["rack"] = FirstString(task.Connection.ProtocolOptions, "rack") ?? "0";
                settings["slot"] = FirstString(task.Connection.ProtocolOptions, "slot") ?? "0";
                break;
            case GatewayCollectionProtocolType.Mitsubishi:
                settings["host"] = Require(task.Connection.Host, $"任务“{task.TaskKey}”的三菱协议需要 connection.host。");
                settings["port"] = (task.Connection.Port ?? 6000).ToString(CultureInfo.InvariantCulture);
                settings["timeout"] = Math.Max(task.Connection.TimeoutMs, 1).ToString(CultureInfo.InvariantCulture);
                settings["model"] = FirstString(task.Connection.ProtocolOptions, "model", "plcModel")
                    ?? throw new InvalidOperationException($"任务“{task.TaskKey}”的三菱协议需要 protocolOptions.model。");
                break;
            case GatewayCollectionProtocolType.OmronFins:
                settings["host"] = Require(task.Connection.Host, $"任务“{task.TaskKey}”的欧姆龙 FINS 需要 connection.host。");
                settings["port"] = (task.Connection.Port ?? 9600).ToString(CultureInfo.InvariantCulture);
                settings["timeout"] = Math.Max(task.Connection.TimeoutMs, 1).ToString(CultureInfo.InvariantCulture);
                settings["endianFormat"] = FirstString(task.Connection.ProtocolOptions, "endianFormat", "endian") ?? "ABCD";
                break;
            case GatewayCollectionProtocolType.AllenBradley:
                settings["host"] = Require(task.Connection.Host, $"任务“{task.TaskKey}”的艾伦-布拉德利需要 connection.host。");
                settings["port"] = (task.Connection.Port ?? 44818).ToString(CultureInfo.InvariantCulture);
                settings["timeout"] = Math.Max(task.Connection.TimeoutMs, 1).ToString(CultureInfo.InvariantCulture);
                settings["slot"] = FirstString(task.Connection.ProtocolOptions, "slot") ?? "0";
                break;
            case GatewayCollectionProtocolType.OpcUa:
                settings["endpoint"] = ResolveOpcUaEndpoint(task.Connection);
                if (!string.IsNullOrWhiteSpace(task.Connection.Host))
                {
                    settings["host"] = task.Connection.Host;
                }
                if (task.Connection.Port.HasValue)
                {
                    settings["port"] = task.Connection.Port.Value.ToString(CultureInfo.InvariantCulture);
                }
                break;
            case GatewayCollectionProtocolType.OpcDa:
                settings["progId"] = FirstString(task.Connection.ProtocolOptions, "progId", "programId")
                    ?? throw new InvalidOperationException($"任务“{task.TaskKey}”的 OPC DA 需要 protocolOptions.progId。");
                if (!string.IsNullOrWhiteSpace(task.Connection.Host))
                {
                    settings["host"] = task.Connection.Host;
                }
                break;
            case GatewayCollectionProtocolType.MtCnc:
                settings["baseUrl"] = ResolveMtConnectBaseUrl(task.Connection);
                if (!string.IsNullOrWhiteSpace(task.Connection.Host))
                {
                    settings["host"] = task.Connection.Host;
                }
                if (task.Connection.Port.HasValue)
                {
                    settings["port"] = task.Connection.Port.Value.ToString(CultureInfo.InvariantCulture);
                }
                settings["timeout"] = Math.Max(task.Connection.TimeoutMs, 1).ToString(CultureInfo.InvariantCulture);
                break;
            case GatewayCollectionProtocolType.FanucCnc:
                settings["host"] = Require(task.Connection.Host, $"任务“{task.TaskKey}”的 Fanuc CNC 需要 connection.host。");
                settings["port"] = (task.Connection.Port ?? 8193).ToString(CultureInfo.InvariantCulture);
                settings["timeout"] = Math.Max((int)Math.Ceiling(task.Connection.TimeoutMs / 1000d), 1).ToString(CultureInfo.InvariantCulture);
                break;
            default:
                throw new NotSupportedException($"网关同步不支持采集协议“{task.Protocol}”。");
        }

        MergeJsonObject(settings, task.Connection.ProtocolOptions);
        return settings;
    }

    private static Dictionary<string, string?> BuildDeviceSettings(CollectionDeviceContract device)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        MergeJsonObject(settings, device.ProtocolOptions);
        return settings;
    }

    private static Dictionary<string, string?> BuildPointSettings(
        CollectionTaskContract task,
        CollectionDeviceContract device,
        CollectionPointContract point)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        MergeJsonObject(settings, device.ProtocolOptions);
        MergeJsonObject(settings, point.ProtocolOptions);

        if (task.Protocol == GatewayCollectionProtocolType.Modbus)
        {
            var stationNumber = FirstString(point.ProtocolOptions, "slaveId")
                ?? FirstString(point.ProtocolOptions, "stationNumber")
                ?? FirstString(device.ProtocolOptions, "slaveId")
                ?? FirstString(device.ProtocolOptions, "stationNumber");
            if (!string.IsNullOrWhiteSpace(stationNumber))
            {
                settings["stationNumber"] = stationNumber;
            }

            settings["registerType"] = NormalizeModbusRegisterType(point.SourceType);
            settings["functionCode"] = FirstString(point.ProtocolOptions, "functionCode")
                ?? ResolveModbusFunctionCode(point.SourceType).ToString(CultureInfo.InvariantCulture);
            settings["registerCount"] = ResolvePointLength(point).ToString(CultureInfo.InvariantCulture);

            CopyIfPresent(settings, point.ProtocolOptions, "byteOrder");
            CopyIfPresent(settings, point.ProtocolOptions, "wordOrder");

            var stringEncoding = FirstString(point.ProtocolOptions, "stringEncoding");
            if (!string.IsNullOrWhiteSpace(stringEncoding))
            {
                settings["encoding"] = stringEncoding switch
                {
                    "GBK" => "GBK",
                    "ASCII" => "ASCII",
                    "UTF8" => "UTF-8",
                    _ => stringEncoding
                };
            }
        }

        return settings;
    }

    private static Dictionary<string, string?> BuildTransformArguments(ValueTransformContract transform)
    {
        return transform.TransformType switch
        {
            GatewayCollectionTransformType.Scale => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["factor"] = RequireNumber(transform.Parameters, "factor", "value", "scale")
            },
            GatewayCollectionTransformType.Offset => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["offset"] = RequireNumber(transform.Parameters, "offset", "value")
            },
            GatewayCollectionTransformType.EnumMap => BuildEnumMapArguments(transform.Parameters),
            GatewayCollectionTransformType.BitExtract => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["index"] = RequireInteger(transform.Parameters, "index", "bitOffset", "offset")
            },
            GatewayCollectionTransformType.Expression => BuildExpressionArguments(transform.Parameters),
            GatewayCollectionTransformType.WordSwap => throw new NotSupportedException("网关同步暂不支持字交换变换。"),
            GatewayCollectionTransformType.ByteSwap => throw new NotSupportedException("网关同步暂不支持字节交换变换。"),
            GatewayCollectionTransformType.Clamp => throw new NotSupportedException("网关同步暂不支持限幅变换。"),
            GatewayCollectionTransformType.DefaultOnError => throw new NotSupportedException("网关同步暂不支持出错默认值变换。"),
            _ => throw new NotSupportedException($"网关同步不支持变换“{transform.TransformType}”。")
        };
    }

    private static Dictionary<string, string?> BuildEnumMapArguments(JsonElement? parameters)
    {
        if (!parameters.HasValue || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("枚举映射变换需要对象参数。");
        }

        var source = parameters.Value;
        if (TryGetProperty(source, "mapping", out var mappingElement) ||
            TryGetProperty(source, "enumMapping", out mappingElement) ||
            TryGetProperty(source, "map", out mappingElement))
        {
            source = mappingElement;
        }

        if (source.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("枚举映射变换需要对象映射。");
        }

        var arguments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in source.EnumerateObject())
        {
            arguments[property.Name] = ToStringValue(property.Value);
        }

        if (arguments.Count == 0)
        {
            throw new InvalidOperationException("枚举映射变换至少需要一个映射项。");
        }

        return arguments;
    }

    private static Dictionary<string, string?> BuildExpressionArguments(JsonElement? parameters)
    {
        if (!parameters.HasValue)
        {
            throw new InvalidOperationException("表达式变换需要参数。");
        }

        if (parameters.Value.ValueKind == JsonValueKind.String)
        {
            var expression = parameters.Value.GetString();
            if (string.IsNullOrWhiteSpace(expression))
            {
                throw new InvalidOperationException("表达式变换需要非空表达式。");
            }

            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["expression"] = expression
            };
        }

        if (parameters.Value.ValueKind != JsonValueKind.Object)
        {
            var expression = ToStringValue(parameters.Value);
            if (string.IsNullOrWhiteSpace(expression))
            {
                throw new InvalidOperationException("表达式变换需要非空表达式。");
            }

            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["expression"] = expression
            };
        }

        var arguments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.Value.EnumerateObject())
        {
            arguments[property.Name] = ToStringValue(property.Value);
        }

        var code = FirstNonEmpty(arguments, "expression", "script", "code", "value");
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("表达式变换需要配置表达式、脚本或代码。");
        }

        arguments["expression"] = code;
        return arguments;
    }

    private static string RequireNumber(JsonElement? parameters, params string[] keys)
    {
        var value = FirstString(parameters, keys);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"变换参数 {string.Join(" 或 ", keys)} 为必填项。");
        }

        return value;
    }

    private static string RequireInteger(JsonElement? parameters, params string[] keys)
    {
        var value = RequireNumber(parameters, keys);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"变换参数 {string.Join(" 或 ", keys)} 必须是整数。");
    }

    private static ushort ResolvePointLength(CollectionPointContract point)
    {
        var length = point.Length > 0 ? point.Length : 1;
        return length > ushort.MaxValue ? ushort.MaxValue : (ushort)length;
    }

    private static int ResolveDevicePollingIntervalSeconds(IEnumerable<CollectionPointContract> points)
    {
        var minPeriodMs = points
            .Select(point => point.Polling?.ReadPeriodMs ?? 5000)
            .Where(value => value > 0)
            .DefaultIfEmpty(5000)
            .Min();

        return Math.Max(1, (int)Math.Ceiling(minPeriodMs / 1000d));
    }

    private static PointAccessMode ResolveAccessMode(GatewayCollectionTargetType targetType)
    {
        return targetType switch
        {
            GatewayCollectionTargetType.Telemetry => PointAccessMode.Read,
            GatewayCollectionTargetType.Attribute => PointAccessMode.Read,
            GatewayCollectionTargetType.AlarmInput => throw new NotSupportedException("网关同步暂不支持告警输入目标映射。"),
            GatewayCollectionTargetType.CommandFeedback => throw new NotSupportedException("网关同步暂不支持命令反馈目标映射。"),
            _ => throw new NotSupportedException($"网关同步不支持目标类型“{targetType}”。")
        };
    }

    private static GatewayCollectionTargetType NormalizeTargetType(GatewayCollectionTargetType targetType, string pointKey)
    {
        return targetType switch
        {
            GatewayCollectionTargetType.Telemetry => GatewayCollectionTargetType.Telemetry,
            GatewayCollectionTargetType.Attribute => GatewayCollectionTargetType.Attribute,
            GatewayCollectionTargetType.AlarmInput => throw new NotSupportedException($"点位“{pointKey}”使用了不受支持的告警输入目标类型。"),
            GatewayCollectionTargetType.CommandFeedback => throw new NotSupportedException($"点位“{pointKey}”使用了不受支持的命令反馈目标类型。"),
            _ => throw new NotSupportedException($"点位“{pointKey}”使用了不受支持的目标类型“{targetType}”。")
        };
    }

    private static TransformationKind ResolveTransformKind(GatewayCollectionTransformType transformType)
    {
        return transformType switch
        {
            GatewayCollectionTransformType.Scale => TransformationKind.Scale,
            GatewayCollectionTransformType.Offset => TransformationKind.Offset,
            GatewayCollectionTransformType.EnumMap => TransformationKind.EnumMap,
            GatewayCollectionTransformType.BitExtract => TransformationKind.BitExtract,
            GatewayCollectionTransformType.Expression => TransformationKind.Expression,
            _ => throw new NotSupportedException($"网关同步不支持变换“{transformType}”。")
        };
    }

    private static GatewayDataType ResolveDataType(string rawValueType)
    {
        return rawValueType.Trim().ToLowerInvariant() switch
        {
            "bool" or "boolean" => GatewayDataType.Boolean,
            "byte" => GatewayDataType.Byte,
            "int16" or "short" => GatewayDataType.Int16,
            "uint16" or "ushort" => GatewayDataType.UInt16,
            "int32" or "int" => GatewayDataType.Int32,
            "uint32" => GatewayDataType.UInt32,
            "int64" or "long" => GatewayDataType.Int64,
            "uint64" or "ulong" => GatewayDataType.UInt64,
            "float" or "float32" or "single" => GatewayDataType.Float,
            "double" or "float64" => GatewayDataType.Double,
            "decimal" => GatewayDataType.Double,
            "string" or "text" => GatewayDataType.String,
            _ => throw new NotSupportedException($"网关同步不支持原始数据类型“{rawValueType}”。")
        };
    }

    private static string ResolveDriverCode(GatewayCollectionProtocolType protocol)
    {
        return protocol switch
        {
            GatewayCollectionProtocolType.Modbus => "modbus",
            GatewayCollectionProtocolType.OpcUa => "opc-ua",
            GatewayCollectionProtocolType.OpcDa => "opc-da",
            GatewayCollectionProtocolType.MtCnc => "mt-cnc",
            GatewayCollectionProtocolType.FanucCnc => "fanuc-cnc",
            GatewayCollectionProtocolType.SiemensS7 => "siemens-s7",
            GatewayCollectionProtocolType.Mitsubishi => "mitsubishi",
            GatewayCollectionProtocolType.OmronFins => "omron-fins",
            GatewayCollectionProtocolType.AllenBradley => "allen-bradley",
            _ => throw new NotSupportedException($"网关同步不支持采集协议“{protocol}”。")
        };
    }

    private static string ResolveOpcUaEndpoint(CollectionConnectionContract connection)
    {
        var endpoint = FirstString(connection.ProtocolOptions, "endpointUrl")
            ?? FirstString(connection.ProtocolOptions, "endpoint");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint;
        }

        if (string.IsNullOrWhiteSpace(connection.Host))
        {
            throw new InvalidOperationException("OPC UA 连接需要 protocolOptions.endpointUrl 或 connection.host。");
        }

        var port = connection.Port ?? 4840;
        return $"opc.tcp://{connection.Host}:{port}";
    }

    private static string ResolveMtConnectBaseUrl(CollectionConnectionContract connection)
    {
        var baseUrl = FirstString(connection.ProtocolOptions, "baseUrl", "agentUrl", "endpoint");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        if (string.IsNullOrWhiteSpace(connection.Host))
        {
            throw new InvalidOperationException("MTConnect 连接需要 protocolOptions.baseUrl 或 connection.host。");
        }

        var scheme = FirstString(connection.ProtocolOptions, "scheme", "protocol") ?? "http";
        var port = connection.Port.HasValue ? $":{connection.Port.Value}" : string.Empty;
        return $"{scheme}://{connection.Host}{port}";
    }

    private static string NormalizeModbusTransport(string? transport, string? fallback = null)
    {
        return NormalizeKey(transport ?? fallback) switch
        {
            "tcp" => "tcp",
            "rtuovertcp" => "rtuOverTcp",
            "serialrtu" or "rtu" or "modbusrtu" or "rs485" or "rs232" or "serial" or "serialdtu" or "dtu" => "serialRtu",
            "serialascii" or "ascii" or "modbusascii" => "serialAscii",
            "" => "tcp",
            _ => throw new NotSupportedException($"网关同步不支持 Modbus 传输方式“{transport ?? fallback}”。")
        };
    }

    private static bool IsSerialModbusTransport(string? transport)
        => NormalizeKey(transport) is "serialrtu" or "serialascii";

    private static byte ResolveModbusFunctionCode(string? sourceType)
    {
        return NormalizeModbusRegisterType(sourceType) switch
        {
            "coil" => 1,
            "discrete-input" => 2,
            "holding-register" => 3,
            "input-register" => 4,
            _ => 3
        };
    }

    private static string NormalizeModbusRegisterType(string? sourceType)
    {
        return NormalizeKey(sourceType) switch
        {
            "coil" or "coils" => "coil",
            "discreteinput" or "discreteinputs" or "discrete" => "discrete-input",
            "inputregister" or "inputregisters" => "input-register",
            "holdingregister" or "holdingregisters" or "register" or "registers" or "" => "holding-register",
            _ => throw new NotSupportedException($"网关同步不支持 Modbus 点位源类型“{sourceType}”。")
        };
    }

    private static void CopyIfPresent(IDictionary<string, string?> target, JsonElement? source, string key)
    {
        var value = FirstString(source, key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }

    private static void MergeJsonObject(IDictionary<string, string?> target, JsonElement? element)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.Value.EnumerateObject())
        {
            target[property.Name] = ToStringValue(property.Value);
        }
    }

    private static string? FirstString(JsonElement? element, params string[] keys)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (TryGetProperty(element.Value, key, out var value))
            {
                return ToStringValue(value);
            }
        }

        return null;
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ToStringValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => value.ToString()
        };
    }

    private static string NormalizeBaseUrl(string baseUrl)
        => baseUrl.Trim().TrimEnd('/') + "/";

    private static string Require(string? value, string message)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value.Trim();

    private static string NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static Guid CreateDeterministicGuid(Guid edgeNodeId, params string[] parts)
    {
        var normalized = string.Join("::", new[] { edgeNodeId.ToString("N") }.Concat(parts).Select(value => value?.Trim() ?? string.Empty));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private sealed record PollingKey(int ReadPeriodMs, string Group);

    private sealed record DevicePointState(Guid PointId, bool Enabled, CollectionPointContract Contract);
}
