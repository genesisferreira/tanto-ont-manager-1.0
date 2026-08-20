using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using TantoOntManager.Application.Batch;
using TantoOntManager.Application.Contracts;
using TantoOntManager.Application.UseCases;
using TantoOntManager.DeviceAdapters.Abstractions;
using TantoOntManager.DeviceAdapters.Zte;
using TantoOntManager.Domain.Backup;
using TantoOntManager.Domain.Profiles;
using TantoOntManager.Domain.Unlock;
using TantoOntManager.Infrastructure.Export;
using TantoOntManager.Infrastructure.Logging;
using TantoOntManager.Infrastructure.Security;
using TantoOntManager.Infrastructure.Unlock;
using TantoOntManager.Networking.Discovery;
using TantoOntManager.Networking.Probing;
using TantoOntManager.Security.Logging;
using TantoOntManager.Security.Secrets;
using TantoOntManager.Security.Tls;

namespace TantoOntManager.Infrastructure.DependencyInjection;

[SupportedOSPlatform("windows")]
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTantoOntManager(this IServiceCollection services, string rootDirectory)
    {
        var paths = new LoggingPaths(rootDirectory);
        Directory.CreateDirectory(paths.LogDirectory);
        Directory.CreateDirectory(paths.DiagnosticsDirectory);

        var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.With(new SensitiveDataEnricher())
            .Destructure.With(new SensitiveDataDestructuringPolicy())
            .WriteTo.File(
                path: Path.Combine(paths.LogDirectory, "tanto-ont-manager-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(serilog, dispose: true);
        });

        services.AddHttpClient();
        services.AddSingleton(new ProbeSessionSettings());
        services.AddSingleton<IPublicProbeCache, PublicProbeCache>();
        services.AddSingleton<LogSanitizer>();
        services.AddSingleton<DpapiSecretProtector>();
        services.AddSingleton<ISecureCredentialStore, NonPersistentCredentialStore>();
        services.AddSingleton<IAuditLogService, AuditLogService>();
        services.AddSingleton<IEthernetDiscoveryService, EthernetDiscoveryService>();
        services.AddSingleton<IConnectivityProbeService, ConnectivityProbeService>();
        services.AddSingleton<IPublicWebReader, HttpPublicWebReader>();
        services.AddSingleton<ZteProfileRepository>();
        services.AddSingleton<IBackupService>(sp =>
            new LocalBackupService(sp.GetRequiredService<ILogger<LocalBackupService>>(), null));
        services.AddSingleton<IConfigBinService, ZteConfigBinService>();
        services.AddSingleton<IZcuBridge>(sp =>
            new ZcuProcessBridge(
                sp.GetRequiredService<ILogger<ZcuProcessBridge>>(),
                @"D:\Tools\Python311\python.exe",
                @"D:\Tools\zte-config-utility"));
        services.AddSingleton<IConfigBinXmlPatcher, ZteConfigBinXmlPatcher>();
        services.AddSingleton<IZteOnuBridge>(sp =>
            new ZteOnuProcessBridge(
                sp.GetRequiredService<ILogger<ZteOnuProcessBridge>>(),
                @"D:\Tools\zteOnu\zteOnu.exe"));
        services.AddSingleton<ITelnetCommandClient, ZteTelnetCommandClient>();
        services.AddSingleton<IZteUnlockOrchestrator, ZteUnlockOrchestrator>();
        services.AddSingleton<ZteDeviceAdapter>();
        services.AddSingleton<IOntDeviceAdapter>(sp => sp.GetRequiredService<ZteDeviceAdapter>());
        services.AddSingleton<IUnlockableDeviceAdapter>(sp => sp.GetRequiredService<ZteDeviceAdapter>());
        services.AddSingleton<IBoundOntTransportFactory, BoundOntTransportFactory>();
        services.AddSingleton<IOntAuthSessionStore, OntAuthSessionStore>();
        services.AddSingleton<IOntAuthenticationAdapter, ZteF6201BV9310P8N1AuthenticationAdapter>();
        services.AddSingleton<IDetectOntUseCase, DetectOntUseCase>();
        services.AddSingleton<ITestConnectionUseCase, TestConnectionUseCase>();
        services.AddSingleton<IListEthernetAdaptersUseCase, ListEthernetAdaptersUseCase>();
        services.AddSingleton<IAuthenticateDeviceUseCase, AuthenticateDeviceUseCase>();
        services.AddSingleton<IEndAuthenticatedSessionUseCase, EndAuthenticatedSessionUseCase>();
        services.AddSingleton<IExportPublicDiagnosticUseCase, ExportPublicDiagnosticUseCase>();
        services.AddSingleton<IExportAuthenticatedDiagnosticUseCase, ExportAuthenticatedDiagnosticUseCase>();
        services.AddSingleton<IMapAuthenticatedReadsUseCase, MapAuthenticatedReadsUseCase>();
        services.AddSingleton<IExportAuthenticatedReadMapUseCase, ExportAuthenticatedReadMapUseCase>();
        services.AddSingleton<IObservationSessionStore, ObservationSessionStore>();
        services.AddSingleton<IExportObservationUseCase, ExportObservationUseCase>();
        services.AddSingleton<IPromoteReadContractUseCase, PromoteReadContractUseCase>();
        services.AddSingleton<IExportWriteContractUseCase, ExportWriteContractUseCase>();
        services.AddSingleton<IPromoteWriteContractUseCase, PromoteWriteContractUseCase>();
        services.AddSingleton<IExportWriteCapabilityUseCase, ExportWriteCapabilityUseCase>();
        services.AddSingleton<IBatchProcessingOrchestrator, DisabledBatchProcessingOrchestrator>();
        services.AddSingleton<IBatchWorkOrderReader, UnsupportedBatchWorkOrderReader>();
        services.AddSingleton(paths);

        return services;
    }
}

public sealed record LoggingPaths(string RootDirectory)
{
    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    public string DiagnosticsDirectory => Path.Combine(RootDirectory, "diagnostics");

    public string WriteContractProposalsDirectory
        => Path.Combine(DiagnosticsDirectory, "proposals", "write-contract");

    public string WriteCapabilityReportsDirectory
        => Path.Combine(DiagnosticsDirectory, "proposals", "write-capability");

    public string CurrentHint => Path.Combine(LogDirectory, "tanto-ont-manager-.log");
}
