namespace IoTEdge.Infrastructure.Drivers;

internal sealed class ModbusDriver : DeviceDriverBase
{
    public override DriverMetadata Metadata { get; } = new(
        "modbus",
        DriverType.Modbus,
        "Modbus 协议",
        "通过统一驱动契约支持 Modbus TCP、RTU over TCP、串口 RTU 和串口 ASCII。",
        true,
        true,
        true,
        true,
        new[]
        {
            new ConnectionSettingDefinition("transport", "传输方式", "select", true, "可选 tcp、rtuOverTcp、serialRtu 或 serialAscii。", new[] { "tcp", "rtuOverTcp", "serialRtu", "serialAscii" }),
            new ConnectionSettingDefinition("host", "主机", "text", false, "TCP 连接使用的 PLC 主机名或 IP 地址。"),
            new ConnectionSettingDefinition("port", "端口", "number", false, "PLC TCP 端口，通常为 502。"),
            new ConnectionSettingDefinition("serialPort", "串口", "text", false, "Modbus RTU/ASCII 使用的串口，例如 COM3 或 /dev/ttyUSB0。"),
            new ConnectionSettingDefinition("baudRate", "波特率", "number", false, "串口波特率，通常为 9600。"),
            new ConnectionSettingDefinition("dataBits", "数据位", "number", false, "串口数据位，通常为 8。"),
            new ConnectionSettingDefinition("parity", "校验位", "select", false, "串口校验方式。", new[] { "None", "Odd", "Even", "Mark", "Space" }),
            new ConnectionSettingDefinition("stopBits", "停止位", "select", false, "串口停止位。", new[] { "One", "OnePointFive", "Two" }),
            new ConnectionSettingDefinition("timeout", "超时", "number", false, "超时时间，单位毫秒。"),
            new ConnectionSettingDefinition("endianFormat", "字节序", "select", false, "字和字节的顺序。", Enum.GetNames<EndianFormat>()),
            new ConnectionSettingDefinition("plcAddresses", "按 PLC 地址", "boolean", false, "将地址按 PLC 风格地址处理。")
        });

    public override Task<ConnectionTestResult> TestConnectionAsync(DriverConnectionContext context, CancellationToken cancellationToken)
    {
        try
        {
            _ = CreateClient(context.Settings);
            return Task.FromResult(new ConnectionTestResult(true));
        }
        catch (Exception exception)
        {
            return Task.FromResult(new ConnectionTestResult(false, exception.Message));
        }
    }

    public override Task<AddressValidationResult> ValidateAddressAsync(DriverReadRequest request, CancellationToken cancellationToken)
    {
        var settings = request.Settings ?? new Dictionary<string, string?>();
        if (!TryParseRegisterType(settings, request.Address, out var registerType, out var error))
        {
            return Task.FromResult(new AddressValidationResult(false, error));
        }

        if (!TryParseAddress(request.Address, registerType, out _, out error))
        {
            return Task.FromResult(new AddressValidationResult(false, error));
        }

        var functionCode = Byte(settings, "functionCode", DefaultReadFunctionCode(registerType));
        if (!IsReadFunctionCode(functionCode) || !IsFunctionCodeAllowed(registerType, functionCode))
        {
            return Task.FromResult(new AddressValidationResult(false, $"Modbus 功能码 {functionCode} 与寄存器类型“{registerType}”不匹配。"));
        }

        var registerCount = Int(settings, "registerCount", request.Length <= 0 ? 1 : request.Length);
        var maxRegisterCount = MaxRegisterCount(registerType);
        if (registerCount < 1 || registerCount > maxRegisterCount)
        {
            return Task.FromResult(new AddressValidationResult(false, $"Modbus registerCount 必须在 1 到 {maxRegisterCount} 之间。"));
        }

        if ((registerType is ModbusRegisterType.Coil or ModbusRegisterType.DiscreteInput) && request.DataType != GatewayDataType.Boolean)
        {
            return Task.FromResult(new AddressValidationResult(false, "Modbus coil 和 discrete-input 点位必须使用 Boolean 数据类型。"));
        }

        if (!IsValidOrder(settings, "byteOrder"))
        {
            return Task.FromResult(new AddressValidationResult(false, "Modbus byteOrder 必须是 bigEndian 或 littleEndian。"));
        }

        if (!IsValidOrder(settings, "wordOrder"))
        {
            return Task.FromResult(new AddressValidationResult(false, "Modbus wordOrder 必须是 bigEndian 或 littleEndian。"));
        }

        return Task.FromResult(new AddressValidationResult(true));
    }

    public override Task<DriverReadResult> ReadAsync(DriverConnectionContext context, DriverReadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var client = CreateClient(context.Settings);
            var station = Byte(request.Settings ?? new Dictionary<string, string?>(), "stationNumber", 1);
            var settings = request.Settings ?? new Dictionary<string, string?>();
            var functionCode = Byte(settings, "functionCode", request.DataType == GatewayDataType.Boolean ? (byte)1 : (byte)3);
            if (!IsReadFunctionCode(functionCode))
            {
                return Task.FromResult(new DriverReadResult(request.Address, null, null, DateTimeOffset.UtcNow, QualityStatus.Bad, $"Modbus 读取不支持功能码 {functionCode}。"));
            }

            if (request.DataType == GatewayDataType.String)
            {
                var rawResult = client.Read(request.Address, station, functionCode, request.Length, true);
                if (!rawResult.IsSucceed)
                {
                    return Task.FromResult(new DriverReadResult(request.Address, null, null, DateTimeOffset.UtcNow, QualityStatus.Bad, rawResult.Err));
                }

                var stringValue = ResolveEncoding(request.Settings ?? new Dictionary<string, string?>()).GetString(rawResult.Value).TrimEnd('\0');
                return Task.FromResult(new DriverReadResult(request.Address, stringValue, stringValue, DateTimeOffset.UtcNow, QualityStatus.Good));
            }

            var result = request.DataType switch
            {
                GatewayDataType.Boolean when functionCode == 2 => ToReadResult(request.Address, client.ReadDiscrete(request.Address, station, functionCode)),
                GatewayDataType.Boolean => ToReadResult(request.Address, client.ReadCoil(request.Address, station, functionCode)),
                GatewayDataType.Int16 => ToReadResult(request.Address, client.ReadInt16(request.Address, station, functionCode)),
                GatewayDataType.UInt16 => ToReadResult(request.Address, client.ReadUInt16(request.Address, station, functionCode)),
                GatewayDataType.Int32 => ToReadResult(request.Address, client.ReadInt32(request.Address, station, functionCode)),
                GatewayDataType.UInt32 => ToReadResult(request.Address, client.ReadUInt32(request.Address, station, functionCode)),
                GatewayDataType.Int64 => ToReadResult(request.Address, client.ReadInt64(request.Address, station, functionCode)),
                GatewayDataType.UInt64 => ToReadResult(request.Address, client.ReadUInt64(request.Address, station, functionCode)),
                GatewayDataType.Float => ToReadResult(request.Address, client.ReadFloat(request.Address, station, functionCode)),
                GatewayDataType.Double => ToReadResult(request.Address, client.ReadDouble(request.Address, station, functionCode)),
                _ => new DriverReadResult(request.Address, null, null, DateTimeOffset.UtcNow, QualityStatus.Bad, $"不支持的 Modbus 数据类型“{request.DataType}”。")
            };

            return Task.FromResult(result);
        }
        catch (Exception exception)
        {
            return Task.FromResult(FailedRead(request.Address, exception));
        }
    }

    public override Task<DriverWriteResult> WriteAsync(DriverConnectionContext context, DriverWriteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var client = CreateClient(context.Settings);
            var settings = request.Settings ?? new Dictionary<string, string?>();
            var station = Byte(settings, "stationNumber", 1);
            var functionCode = Byte(settings, "functionCode", request.DataType == GatewayDataType.Boolean ? (byte)5 : (byte)16);
            if (!IsWriteFunctionCode(functionCode))
            {
                return Task.FromResult(new DriverWriteResult(request.Address, request.Value, DateTimeOffset.UtcNow, QualityStatus.Bad, $"Modbus 写入不支持功能码 {functionCode}。"));
            }

            var result = request.DataType switch
            {
                GatewayDataType.Boolean => ToWriteResult(request.Address, request.Value, client.Write(request.Address, Convert.ToBoolean(request.Value), station, functionCode)),
                GatewayDataType.Int16 => ToWriteResult(request.Address, request.Value, client.Write(request.Address, Convert.ToInt16(request.Value), station, functionCode)),
                GatewayDataType.UInt16 => ToWriteResult(request.Address, request.Value, client.Write(request.Address, Convert.ToUInt16(request.Value), station, functionCode)),
                GatewayDataType.Int32 => ToWriteResult(request.Address, request.Value, client.Write(request.Address, Convert.ToInt32(request.Value), station, functionCode)),
                GatewayDataType.UInt32 => ToWriteResult(request.Address, request.Value, client.Write(request.Address, Convert.ToUInt32(request.Value), station, functionCode)),
                GatewayDataType.Int64 => ToWriteResult(request.Address, request.Value, client.Write(request.Address, Convert.ToInt64(request.Value), station, functionCode)),
                GatewayDataType.UInt64 => ToWriteResult(request.Address, request.Value, client.Write(request.Address, Convert.ToUInt64(request.Value), station, functionCode)),
                GatewayDataType.Float => ToWriteResult(request.Address, request.Value, client.Write(request.Address, Convert.ToSingle(request.Value), station, functionCode)),
                GatewayDataType.Double => ToWriteResult(request.Address, request.Value, client.Write(request.Address, Convert.ToDouble(request.Value), station, functionCode)),
                GatewayDataType.String => ToWriteResult(request.Address, request.Value, client.Write(request.Address, ResolveEncoding(settings).GetBytes(Convert.ToString(request.Value) ?? string.Empty), station, functionCode, true)),
                _ => new DriverWriteResult(request.Address, request.Value, DateTimeOffset.UtcNow, QualityStatus.Bad, $"不支持的 Modbus 数据类型“{request.DataType}”。")
            };

            return Task.FromResult(result);
        }
        catch (Exception exception)
        {
            return Task.FromResult(FailedWrite(request.Address, request.Value, exception));
        }
    }

    private static IModbusClient CreateClient(IReadOnlyDictionary<string, string?> settings)
    {
        var transport = NormalizeTransport(Required(settings, "transport"));
        var timeout = Int(settings, "timeout", 1500);
        var endian = ResolveEndian(settings);
        var plcAddresses = Boolean(settings, "plcAddresses", false);

        return transport switch
        {
            "tcp" => new ModbusTcpClient(Required(settings, "host"), Int(settings, "port", 502), timeout, endian, plcAddresses),
            "rtuOverTcp" => new ModbusRtuOverTcpClient(Required(settings, "host"), Int(settings, "port", 502), timeout, endian, plcAddresses),
            "serialRtu" => new ModbusRtuClient(
                RequiredAny(settings, "serialPort", "portName", "comPort"),
                IntAny(settings, 9600, "baudRate", "baud"),
                timeout,
                ParseStopBits(GetAny(settings, "stopBits")),
                ParseParity(GetAny(settings, "parity")),
                IntAny(settings, 8, "dataBits"),
                endian,
                plcAddresses),
            "serialAscii" => new ModbusAsciiClient(
                RequiredAny(settings, "serialPort", "portName", "comPort"),
                IntAny(settings, 9600, "baudRate", "baud"),
                timeout,
                ParseStopBits(GetAny(settings, "stopBits")),
                ParseParity(GetAny(settings, "parity")),
                IntAny(settings, 8, "dataBits"),
                endian,
                plcAddresses),
            _ => throw new NotSupportedException($"暂不支持的 Modbus 传输方式“{transport}”。")
        };
    }

    private static string NormalizeTransport(string transport)
    {
        return NormalizeKey(transport) switch
        {
            "tcp" or "modbustcp" => "tcp",
            "rtuovertcp" or "rtutcp" or "modbusrtuovertcp" => "rtuOverTcp",
            "serialrtu" or "rtu" or "modbusrtu" or "rs485" or "rs232" or "serial" or "serialdtu" or "dtu" => "serialRtu",
            "serialascii" or "ascii" or "modbusascii" => "serialAscii",
            var value => throw new NotSupportedException($"暂不支持的 Modbus 传输方式“{transport}”。")
        };
    }

    private static string RequiredAny(IReadOnlyDictionary<string, string?> values, params string[] keys)
        => GetAny(values, keys) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"必须提供以下连接参数之一：{string.Join("、", keys)}。");

    private static string? GetAny(IReadOnlyDictionary<string, string?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static int IntAny(IReadOnlyDictionary<string, string?> values, int defaultValue, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    private static Parity ParseParity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Parity.None;
        }

        return NormalizeKey(value) switch
        {
            "0" or "n" or "none" => Parity.None,
            "1" or "o" or "odd" => Parity.Odd,
            "2" or "e" or "even" => Parity.Even,
            "3" or "m" or "mark" => Parity.Mark,
            "4" or "s" or "space" => Parity.Space,
            _ when Enum.TryParse<Parity>(value, true, out var parity) => parity,
            _ => throw new InvalidOperationException("Modbus 串口校验位必须是无校验、奇校验、偶校验、标记校验或空格校验。")
        };
    }

    private static StopBits ParseStopBits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return StopBits.One;
        }

        return NormalizeKey(value) switch
        {
            "1" or "one" => StopBits.One,
            "15" or "onepointfive" or "onepoint5" => StopBits.OnePointFive,
            "2" or "two" => StopBits.Two,
            _ when double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) && Math.Abs(parsed - 1.0d) < 0.0000000001d => StopBits.One,
            _ when double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) && Math.Abs(parsed - 1.5d) < 0.0000000001d => StopBits.OnePointFive,
            _ when double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) && Math.Abs(parsed - 2.0d) < 0.0000000001d => StopBits.Two,
            _ => throw new InvalidOperationException("Modbus 串口停止位必须是 1 位、1.5 位、2 位、1、1.5 或 2。")
        };
    }

    private static string NormalizeKey(string value)
        => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool TryParseRegisterType(
        IReadOnlyDictionary<string, string?> settings,
        string address,
        out ModbusRegisterType registerType,
        out string? error)
    {
        var configured = GetAny(settings, "registerType", "sourceType", "area");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return TryParseRegisterType(configured, out registerType, out error);
        }

        var separatorIndex = address.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            return TryParseRegisterType(address[..separatorIndex], out registerType, out error);
        }

        registerType = ModbusRegisterType.HoldingRegister;
        error = null;
        return true;
    }

    private static bool TryParseRegisterType(string value, out ModbusRegisterType registerType, out string? error)
    {
        switch (NormalizeKey(value))
        {
            case "coil":
            case "coils":
                registerType = ModbusRegisterType.Coil;
                error = null;
                return true;
            case "discreteinput":
            case "discreteinputs":
            case "discrete":
                registerType = ModbusRegisterType.DiscreteInput;
                error = null;
                return true;
            case "inputregister":
            case "inputregisters":
                registerType = ModbusRegisterType.InputRegister;
                error = null;
                return true;
            case "holdingregister":
            case "holdingregisters":
            case "register":
            case "registers":
                registerType = ModbusRegisterType.HoldingRegister;
                error = null;
                return true;
            default:
                registerType = default;
                error = "Modbus registerType 必须是 coil、discrete-input、input-register 或 holding-register。";
                return false;
        }
    }

    private static bool TryParseAddress(
        string address,
        ModbusRegisterType registerType,
        out int zeroBasedAddress,
        out string? error)
    {
        zeroBasedAddress = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(address))
        {
            error = "Modbus 地址为必填项。";
            return false;
        }

        var text = address.Trim();
        var separatorIndex = text.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex >= 0)
        {
            if (!TryParseRegisterType(text[..separatorIndex], out var addressType, out error))
            {
                return false;
            }

            if (addressType != registerType)
            {
                error = $"Modbus 地址区域“{addressType}”与 registerType“{registerType}”不一致。";
                return false;
            }

            text = text[(separatorIndex + 1)..].Trim();
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            error = "Modbus 地址必须是非负整数，或 area:address 格式。";
            return false;
        }

        zeroBasedAddress = ToZeroBasedAddress(registerType, parsed);
        if (zeroBasedAddress is < 0 or > 65535)
        {
            error = "Modbus 地址必须映射到 0 到 65535 范围内。";
            return false;
        }

        return true;
    }

    private static int ToZeroBasedAddress(ModbusRegisterType registerType, int address)
    {
        return registerType switch
        {
            ModbusRegisterType.Coil when address is >= 1 and <= 99999 => address - 1,
            ModbusRegisterType.DiscreteInput when address is >= 10001 and <= 19999 => address - 10001,
            ModbusRegisterType.DiscreteInput when address is >= 1 and <= 99999 => address - 1,
            ModbusRegisterType.InputRegister when address is >= 30001 and <= 39999 => address - 30001,
            ModbusRegisterType.InputRegister when address is >= 1 and <= 99999 => address - 1,
            ModbusRegisterType.HoldingRegister when address is >= 40001 and <= 49999 => address - 40001,
            ModbusRegisterType.HoldingRegister when address is >= 1 and <= 99999 => address - 1,
            _ => address
        };
    }

    private static byte DefaultReadFunctionCode(ModbusRegisterType registerType)
    {
        return registerType switch
        {
            ModbusRegisterType.Coil => 1,
            ModbusRegisterType.DiscreteInput => 2,
            ModbusRegisterType.InputRegister => 4,
            ModbusRegisterType.HoldingRegister => 3,
            _ => 3
        };
    }

    private static bool IsReadFunctionCode(byte functionCode)
        => functionCode is 1 or 2 or 3 or 4;

    private static bool IsWriteFunctionCode(byte functionCode)
        => functionCode is 5 or 6 or 15 or 16;

    private static bool IsFunctionCodeAllowed(ModbusRegisterType registerType, byte functionCode)
    {
        return registerType switch
        {
            ModbusRegisterType.Coil => functionCode is 1 or 5 or 15,
            ModbusRegisterType.DiscreteInput => functionCode == 2,
            ModbusRegisterType.InputRegister => functionCode == 4,
            ModbusRegisterType.HoldingRegister => functionCode is 3 or 6 or 16,
            _ => false
        };
    }

    private static int MaxRegisterCount(ModbusRegisterType registerType)
        => registerType is ModbusRegisterType.Coil or ModbusRegisterType.DiscreteInput ? 2000 : 125;

    private static bool IsValidOrder(IReadOnlyDictionary<string, string?> settings, string key)
    {
        var value = GetAny(settings, key);
        return string.IsNullOrWhiteSpace(value)
            || NormalizeKey(value) is "bigendian" or "littleendian";
    }

    private enum ModbusRegisterType
    {
        Coil,
        DiscreteInput,
        InputRegister,
        HoldingRegister
    }
}
