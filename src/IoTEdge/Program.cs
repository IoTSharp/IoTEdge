using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using IoTEdge;
using IoTEdge.Application;
using IoTEdge.BasicRuntime;
using IoTEdge.Domain;
using IoTEdge.Diagnostics;
using IoTEdge.Infrastructure;
using IoTEdge.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("bootstrap.json", optional: true, reloadOnChange: true);

builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});
builder.Services.Configure<EdgeReportingOptions>(builder.Configuration.GetSection("EdgeReporting"));
builder.Services.Configure<LocalCollectionConfigurationOptions>(builder.Configuration.GetSection("LocalCollection"));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
});
builder.Services.AddSingleton<BootstrapConfigurationService>();
builder.Services.AddSingleton<CollectionConfigurationSyncState>();
builder.Services.AddSingleton<LocalCollectionConfigurationService>();
builder.Services.AddSingleton<InMemoryLogStore>();
builder.Logging.Services.AddSingleton<ILoggerProvider, InMemoryLoggerProvider>();
#if EDGE_BASIC_RUNTIME_EXTENSIONS
builder.Services.AddSingleton<IBasicRuntimeExtension>(sp => new IoTEdge.RuntimeExtensions.GatewayRuntimeExtension(
    sp.GetRequiredService<IDeviceDriverRegistry>(),
    sp.GetRequiredService<IUploadTransportRegistry>()));
#endif
builder.Services.AddSingleton<BasicRuntime>(sp =>
{
    var runtime = new BasicRuntime();
    runtime.Use(sp.GetServices<IBasicRuntimeExtension>());
    return runtime;
});
builder.Services.AddSingleton<ValueTransformationService>();
builder.Services.AddScoped<DriverCatalogService>();
builder.Services.AddScoped<CollectionProtocolCatalogService>();
builder.Services.AddScoped<UploadProtocolCatalogService>();
builder.Services.AddScoped<GatewayRuntimeService>();
builder.Services.AddGatewayInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IEdgeTaskReceiptReporter, EdgeTaskReceiptReporter>();
builder.Services.AddHostedService<GatewayPollingWorker>();
builder.Services.AddHostedService<GatewayCollectionConfigurationWorker>();
builder.Services.AddHostedService<EdgeRuntimeReportingWorker>();

var app = builder.Build();
app.UseCors();

app.UseDefaultFiles();
app.MapStaticAssets();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        context.Context.Response.Headers.Pragma = "no-cache";
    }
});

using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<IGatewaySchemaInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);

    var localConfiguration = scope.ServiceProvider.GetRequiredService<LocalCollectionConfigurationService>();
    await localConfiguration.InitializeAsync(CancellationToken.None);
}

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    application = "IoTEdge",
    timestampUtc = DateTime.UtcNow
}));

app.MapGet("/api/bootstrap/config", async (BootstrapConfigurationService service, CancellationToken ct) =>
    Results.Ok(await service.GetAsync(ct)));

app.MapGet("/api/local/configuration", async (LocalCollectionConfigurationService service, CancellationToken ct) =>
    Results.Ok(await service.GetAsync(ct)));

app.MapPut("/api/local/configuration", async (
    EdgeCollectionConfigurationContract configuration,
    LocalCollectionConfigurationService service,
    HttpContext context,
    CancellationToken ct) =>
{
    try
    {
        var apply = !bool.TryParse(context.Request.Query["apply"], out var parsed) || parsed;
        return Results.Ok(await service.SaveAsync(configuration, apply, updatedBy: "本地控制台", ct));
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapPost("/api/local/configuration/apply", async (LocalCollectionConfigurationService service, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await service.ApplyCurrentAsync(ct));
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapPost("/api/local/configuration/reset", async (LocalCollectionConfigurationService service, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await service.ResetAsync(ct));
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapGet("/api/scripts/polling", () => Results.Ok(new
{
    name = "默认网关轮询脚本",
    language = "BASIC",
    script = GatewayRuntimeService.DefaultPollingScript
}));

app.MapGet("/api/collection/protocols", (CollectionProtocolCatalogService service) => Results.Ok(new
{
    generatedAtUtc = DateTime.UtcNow,
    protocols = service.GetProtocols()
}));

app.MapGet("/api/upload/protocols", (UploadProtocolCatalogService service) => Results.Ok(new
{
    generatedAtUtc = DateTime.UtcNow,
    protocols = service.GetProtocols()
}));

app.MapGet("/api/diagnostics/logs", (InMemoryLogStore store, int? count, string? level) => Results.Ok(new
{
    generatedAtUtc = DateTime.UtcNow,
    entries = store.GetRecent(count ?? 200, InMemoryLogStore.ParseLogLevel(level))
}));

app.MapPost("/api/bootstrap/config", async (BootstrapConfigUpdateRequest request, BootstrapConfigurationService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Json))
    {
        return Results.BadRequest(new { message = "Bootstrap 配置 JSON 为必填项。" });
    }

    try
    {
        var saved = await service.SaveAsync(request.Json, ct);
        return Results.Ok(saved);
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapGet("/api/diagnostics/summary", async (
    IHostEnvironment environment,
    BasicRuntime basicRuntime,
    BootstrapConfigurationService bootstrapConfiguration,
    LocalCollectionConfigurationService localConfigurationService,
    IOptionsMonitor<EdgeReportingOptions> edgeOptionsMonitor,
    CollectionConfigurationSyncState collectionSyncState,
    IGatewayRepository repository,
    CancellationToken ct) =>
{
    using var currentProcess = Process.GetCurrentProcess();
    currentProcess.Refresh();

    var edgeOptions = edgeOptionsMonitor.CurrentValue;
    var bootstrapInfo = await bootstrapConfiguration.GetAsync(ct);
    var localConfiguration = await localConfigurationService.GetAsync(ct);
    var channels = await repository.GetChannelsAsync(ct);
    var devices = await repository.GetDevicesAsync(ct);
    var points = await repository.GetPointsAsync(ct);
    var pollingTasks = await repository.GetPollingTasksAsync(ct);
    var uploadChannels = await repository.GetUploadChannelsAsync(ct);
    var uploadRoutes = await repository.GetUploadRoutesAsync(ct);
    var writeCommands = await repository.GetWriteCommandsAsync(ct);
    var collectionSync = collectionSyncState.GetSnapshot();

    return Results.Ok(new
    {
        generatedAtUtc = DateTime.UtcNow,
        applicationName = environment.ApplicationName,
        environment = environment.EnvironmentName,
        contentRootPath = environment.ContentRootPath,
        machineName = Environment.MachineName,
        process = new
        {
            id = currentProcess.Id,
            name = currentProcess.ProcessName,
            workingSetBytes = currentProcess.WorkingSet64,
            privateMemoryBytes = currentProcess.PrivateMemorySize64,
            threadCount = currentProcess.Threads.Count,
            startTimeUtc = currentProcess.StartTime.ToUniversalTime()
        },
        bootstrap = new
        {
            bootstrapInfo.Exists,
            bootstrapInfo.FilePath,
            bootstrapInfo.LastWriteTimeUtc
        },
        localConfiguration = new
        {
            localConfiguration.Exists,
            localConfiguration.FilePath,
            localConfiguration.LastWriteTimeUtc,
            localConfiguration.Applied,
            localConfiguration.Configuration.Version,
            localConfiguration.Configuration.UpdatedAt,
            localConfiguration.Configuration.UpdatedBy
        },
        edgeReporting = new
        {
            edgeOptions.Enabled,
            edgeOptions.RuntimeType,
            edgeOptions.RuntimeName,
            edgeOptions.InstanceId,
            edgeOptions.BaseUrl,
            hasAccessToken = !string.IsNullOrWhiteSpace(edgeOptions.AccessToken),
            edgeOptions.HeartbeatIntervalSeconds,
            edgeOptions.RetryDelaySeconds,
            metadata = edgeOptions.Metadata
        },
        collectionSync,
        basicRuntime = new
        {
            extensionCount = basicRuntime.RegisteredExtensions.Count,
            extensions = basicRuntime.RegisteredExtensions
        },
        counts = new
        {
            channelCount = channels.Count,
            enabledChannelCount = channels.Count(item => item.Enabled),
            deviceCount = devices.Count,
            enabledDeviceCount = devices.Count(item => item.Enabled),
            pointCount = points.Count,
            enabledPointCount = points.Count(item => item.Enabled),
            pollingTaskCount = pollingTasks.Count,
            enabledPollingTaskCount = pollingTasks.Count(item => item.Enabled),
            uploadChannelCount = uploadChannels.Count,
            enabledUploadChannelCount = uploadChannels.Count(item => item.Enabled),
            uploadRouteCount = uploadRoutes.Count,
            enabledUploadRouteCount = uploadRoutes.Count(item => item.Enabled),
            recentWriteCommandCount = writeCommands.Count
        }
    });
});

app.MapFallbackToFile("/index.html");

app.Run();

internal sealed record BootstrapConfigUpdateRequest(string Json);
