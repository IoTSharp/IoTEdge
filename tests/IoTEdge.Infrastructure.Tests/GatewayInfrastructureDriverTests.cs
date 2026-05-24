using System.Text.Json;
using IoTEdge.Application;
using IoTEdge.Domain;
using IoTEdge.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTEdge.Infrastructure.Tests;

public sealed class GatewayInfrastructureDriverTests
{
    [Fact]
    public void Infrastructure_registers_expanded_driver_catalog()
    {
        var services = new ServiceCollection();
        services.AddGatewayInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IDeviceDriverRegistry>();
        var metadata = registry.GetMetadata().ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);

        Assert.Contains("opc-ua", metadata.Keys);
        Assert.Contains("opc-da", metadata.Keys);
        Assert.Contains("mt-cnc", metadata.Keys);
        Assert.Contains("fanuc-cnc", metadata.Keys);
        Assert.Contains("siemens-s7", metadata.Keys);
        Assert.Contains("mitsubishi", metadata.Keys);
        Assert.Contains("omron-fins", metadata.Keys);
        Assert.Contains("allen-bradley", metadata.Keys);
        Assert.Equal(DriverType.OpcUa, metadata["opc-ua"].DriverType);
        Assert.Equal(DriverType.MtCnc, metadata["mt-cnc"].DriverType);
        Assert.Equal("high", metadata["opc-da"].RiskLevel);
        Assert.Equal("high", metadata["fanuc-cnc"].RiskLevel);
    }

    [Fact]
    public void Protocol_catalog_exposes_collection_families()
    {
        var services = new ServiceCollection();
        services.AddGatewayInfrastructure(new ConfigurationBuilder().Build());
        services.AddScoped<DriverCatalogService>();
        services.AddScoped<CollectionProtocolCatalogService>();
        using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<CollectionProtocolCatalogService>();
        var protocols = catalog.GetProtocols().ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("Modbus", protocols["modbus"].ContractProtocol);
        Assert.Equal("PLC", protocols["modbus"].Category);
        Assert.Equal("ready", protocols["modbus"].Lifecycle);
        Assert.Equal("PLC", protocols["siemens-s7"].Category);
        Assert.Equal("CNC", protocols["mt-cnc"].Category);
        Assert.Equal("guarded", protocols["opc-da"].Lifecycle);
        Assert.Equal("guarded", protocols["fanuc-cnc"].Lifecycle);
    }

    [Fact]
    public void Upload_protocol_catalog_exposes_prioritized_targets()
    {
        var catalog = new UploadProtocolCatalogService();
        var protocols = catalog.GetProtocols().ToArray();

        Assert.Equal(["IoTSharp", "ThingsBoard", "SonnetDb", "InfluxDb"], protocols.Select(item => item.Code).ToArray());
        Assert.Equal("平台", protocols[0].Category);
        Assert.Equal("时序数据库", protocols[2].Category);
    }

    [Fact]
    public void Infrastructure_registers_upload_transports()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IUploadTransportRegistry>();

        Assert.Equal(UploadProtocol.IoTSharp, registry.GetRequiredTransport(UploadProtocol.IoTSharp).Protocol);
        Assert.Equal(UploadProtocol.ThingsBoard, registry.GetRequiredTransport(UploadProtocol.ThingsBoard).Protocol);
        Assert.Equal(UploadProtocol.SonnetDb, registry.GetRequiredTransport(UploadProtocol.SonnetDb).Protocol);
        Assert.Equal(UploadProtocol.InfluxDb, registry.GetRequiredTransport(UploadProtocol.InfluxDb).Protocol);
    }

    [Fact]
    public async Task OpcUa_driver_validates_node_id_format()
    {
        var services = new ServiceCollection();
        services.AddGatewayInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var driver = provider.GetRequiredService<IDeviceDriverRegistry>().GetRequiredDriver("opc-ua");
        var valid = await driver.ValidateAddressAsync(new DriverReadRequest("ns=2;s=Device.Temperature", GatewayDataType.Double), CancellationToken.None);
        var invalid = await driver.ValidateAddressAsync(new DriverReadRequest("not a node id", GatewayDataType.Double), CancellationToken.None);

        Assert.True(valid.IsValid);
        Assert.False(invalid.IsValid);
    }

    [Fact]
    public async Task Modbus_driver_validates_address_and_function_code()
    {
        var services = new ServiceCollection();
        services.AddGatewayInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var driver = provider.GetRequiredService<IDeviceDriverRegistry>().GetRequiredDriver("modbus");
        var valid = await driver.ValidateAddressAsync(
            new DriverReadRequest(
                "holding-register:40001",
                GatewayDataType.Float,
                2,
                new Dictionary<string, string?>
                {
                    ["registerType"] = "holding-register",
                    ["functionCode"] = "3",
                    ["registerCount"] = "2",
                    ["byteOrder"] = "bigEndian",
                    ["wordOrder"] = "littleEndian"
                }),
            CancellationToken.None);
        var invalidFunctionCode = await driver.ValidateAddressAsync(
            new DriverReadRequest(
                "holding-register:40001",
                GatewayDataType.Float,
                2,
                new Dictionary<string, string?>
                {
                    ["registerType"] = "holding-register",
                    ["functionCode"] = "2"
                }),
            CancellationToken.None);
        var invalidAddress = await driver.ValidateAddressAsync(
            new DriverReadRequest(
                "holding-register:70000",
                GatewayDataType.Float,
                2,
                new Dictionary<string, string?>
                {
                    ["registerType"] = "holding-register",
                    ["functionCode"] = "3"
                }),
            CancellationToken.None);

        Assert.True(valid.IsValid);
        Assert.False(invalidFunctionCode.IsValid);
        Assert.False(invalidAddress.IsValid);
    }

    [Fact]
    public async Task Modbus_driver_rejects_invalid_byte_order()
    {
        var services = new ServiceCollection();
        services.AddGatewayInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var driver = provider.GetRequiredService<IDeviceDriverRegistry>().GetRequiredDriver("modbus");
        var result = await driver.ValidateAddressAsync(
            new DriverReadRequest(
                "input-register:30001",
                GatewayDataType.UInt16,
                1,
                new Dictionary<string, string?>
                {
                    ["registerType"] = "input-register",
                    ["functionCode"] = "4",
                    ["byteOrder"] = "middleEndian"
                }),
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("byteOrder", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Collection_mapper_preserves_modbus_binding_fields_and_scale_offset()
    {
        var configuration = new EdgeCollectionConfigurationContract
        {
            EdgeNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Tasks =
            [
                new CollectionTaskContract
                {
                    TaskKey = "modbus-task",
                    Protocol = GatewayCollectionProtocolType.Modbus,
                    Connection = new CollectionConnectionContract
                    {
                        ConnectionName = "modbus-task",
                        Protocol = GatewayCollectionProtocolType.Modbus,
                        Transport = "tcp",
                        Host = "127.0.0.1",
                        Port = 1502
                    },
                    Devices =
                    [
                        new CollectionDeviceContract
                        {
                            DeviceKey = "slave-1",
                            DeviceName = "Slave 1",
                            ProtocolOptions = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                            {
                                ["slaveId"] = 1
                            }),
                            Points =
                            [
                                new CollectionPointContract
                                {
                                    PointKey = "supply-temp",
                                    PointName = "Supply temperature",
                                    SourceType = "HoldingRegister",
                                    Address = "holding-register:40001",
                                    RawValueType = "Float32",
                                    Length = 2,
                                    ProtocolOptions = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                                    {
                                        ["byteOrder"] = "bigEndian",
                                        ["wordOrder"] = "littleEndian",
                                        ["scale"] = 0.1m,
                                        ["offset"] = 2m
                                    }),
                                    Mapping = new PlatformMappingContract
                                    {
                                        TargetType = GatewayCollectionTargetType.Telemetry,
                                        TargetName = "supplyTemperature",
                                        ValueType = GatewayCollectionValueType.Double
                                    }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var snapshot = GatewayCollectionConfigurationMapper.Map(configuration, new EdgeReportingOptions());
        var point = Assert.Single(snapshot.Points);
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(point.SettingsJson)
            ?? throw new InvalidOperationException("Could not deserialize point settings.");

        Assert.Equal("holding-register", settings["registerType"]);
        Assert.Equal("3", settings["functionCode"]);
        Assert.Equal("2", settings["registerCount"]);
        Assert.Equal("bigEndian", settings["byteOrder"]);
        Assert.Equal("littleEndian", settings["wordOrder"]);
        Assert.Collection(
            snapshot.TransformRules.OrderBy(rule => rule.SortOrder),
            scale =>
            {
                Assert.Equal(TransformationKind.Scale, scale.Kind);
                Assert.Contains("\"factor\":\"0.1\"", scale.ArgumentsJson, StringComparison.Ordinal);
            },
            offset =>
            {
                Assert.Equal(TransformationKind.Offset, offset.Kind);
                Assert.Contains("\"offset\":\"2\"", offset.ArgumentsJson, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task MtConnect_driver_reports_read_only_writes()
    {
        var services = new ServiceCollection();
        services.AddGatewayInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var driver = provider.GetRequiredService<IDeviceDriverRegistry>().GetRequiredDriver("mt-cnc");
        var result = await driver.WriteAsync(
            new DriverConnectionContext("mt-cnc", new Dictionary<string, string?> { ["baseUrl"] = "http://127.0.0.1:5000" }),
            new DriverWriteRequest("avail", GatewayDataType.String, "AVAILABLE"),
            CancellationToken.None);

        Assert.Equal(QualityStatus.Bad, result.Quality);
        Assert.Contains("只读", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
