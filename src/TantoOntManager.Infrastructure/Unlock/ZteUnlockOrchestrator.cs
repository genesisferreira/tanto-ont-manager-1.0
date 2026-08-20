using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TantoOntManager.DeviceAdapters.Abstractions;
using TantoOntManager.Domain.Backup;
using TantoOntManager.Domain.Profiles;
using TantoOntManager.Domain.Sessions;
using TantoOntManager.Domain.Unlock;

namespace TantoOntManager.Infrastructure.Unlock;

/// <summary>
/// Orquestrador principal de desbloqueio de ONTs ZTE.
/// Responsável por coordenar todo o fluxo: detecção, backup, desbloqueio, verificação e rollback.
/// </summary>
public sealed class ZteUnlockOrchestrator : IZteUnlockOrchestrator
{
    private readonly ILogger<ZteUnlockOrchestrator> _logger;
    private readonly ZteProfileRepository _profileRepository;
    private readonly IBackupService _backupService;
    private readonly IConfigBinService _configBinService;
    private readonly IZcuBridge _zcuBridge;
    private readonly IConfigBinXmlPatcher _xmlPatcher;
    private readonly IZteOnuBridge _zteOnuBridge;
    private readonly ITelnetCommandClient _telnetClient;
    private readonly IBoundOntTransportFactory _transportFactory;
    private readonly IOntAuthSessionStore _sessionStore;

    public ZteUnlockOrchestrator(
        ILogger<ZteUnlockOrchestrator> logger,
        ZteProfileRepository profileRepository,
        IBackupService backupService,
        IConfigBinService configBinService,
        IZcuBridge zcuBridge,
        IConfigBinXmlPatcher xmlPatcher,
        IZteOnuBridge zteOnuBridge,
        ITelnetCommandClient telnetClient,
        IBoundOntTransportFactory transportFactory,
        IOntAuthSessionStore sessionStore)
    {
        _logger = logger;
        _profileRepository = profileRepository;
        _backupService = backupService;
        _configBinService = configBinService;
        _zcuBridge = zcuBridge;
        _xmlPatcher = xmlPatcher;
        _zteOnuBridge = zteOnuBridge;
        _telnetClient = telnetClient;
        _transportFactory = transportFactory;
        _sessionStore = sessionStore;
    }

    public async Task<UnlockResult> ExecuteAsync(
        AuthorizedDeviceSession session,
        UnlockOptions options,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var (transport, ownsTransport) = ResolveTransport(session);

        try
        {
            var identity = ResolveIdentity(session);
            _logger.LogWarning(
                "INICIANDO DESBLOQUEIO | Modelo: {Model} | Serial: {Serial} | Estratégia será determinada pelo perfil",
                identity.Model, identity.SerialNumber);

            // ============================================================
            // FASE 1: DETECÇÃO E PERFIL
            // ============================================================
            var profile = _profileRepository.FindExact(identity.Model, identity.FirmwareVersion)
                ?? _profileRepository.FindByModel(identity.Model)
                ?? _profileRepository.GetGenericFallback(identity.Model);

            _logger.LogInformation(
                "Perfil selecionado: {ProfileId} | Homologado: {Homologated} | Estratégia: {Strategy}",
                profile.ProfileId, profile.IsHomologated, profile.UnlockStrategy);

            if (profile.UnlockStrategy == UnlockStrategy.Unsupported)
            {
                return UnlockResult.UnsupportedModel(identity.Model);
            }

            // ============================================================
            // FASE 2: BACKUP OBRIGATÓRIO
            // ============================================================
            byte[] originalConfigBin;
            BackupTicket? backupTicket = null;

            try
            {
                originalConfigBin = await _configBinService.DownloadAsync(transport, profile, cancellationToken);
                backupTicket = await _backupService.CreateBackupAsync(originalConfigBin, identity, cancellationToken);
                _logger.LogInformation(
                    "💾 Backup criado: {TicketId} | {Size} bytes",
                    backupTicket.TicketId, backupTicket.FileSize);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no backup. Abortando desbloqueio por segurança.");
                return UnlockResult.Failed(
                    UnlockPhase.Backup,
                    "Falha no download do config.bin original. Desbloqueio abortado para segurança.",
                    rollbackAvailable: false);
            }

            // ============================================================
            // FASE 3: DESBLOQUEIO (estratégia depende do perfil)
            // ============================================================
            UnlockResult result;

            switch (profile.UnlockStrategy)
            {
                case UnlockStrategy.ConfigBinOnly:
                    result = await UnlockViaConfigBinAsync(
                        transport, profile, identity, originalConfigBin, options, backupTicket, sw, cancellationToken);
                    break;

                case UnlockStrategy.ZteOnuPreferred:
                    // Tenta config.bin primeiro (não-destrutivo), se falhar usa zteOnu
                    result = await UnlockHybridAsync(
                        transport, profile, identity, originalConfigBin, options, backupTicket, sw, cancellationToken);
                    break;

                case UnlockStrategy.ZteOnuOnly:
                    result = await UnlockViaZteOnuAsync(
                        transport, profile, identity, options, backupTicket, sw, cancellationToken);
                    break;

                default:
                    return UnlockResult.Failed(UnlockPhase.Detection, "Estratégia de desbloqueio desconhecida.", backupTicket?.FilePath);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Desbloqueio cancelado pelo usuário.");
            return UnlockResult.Failed(UnlockPhase.Detection, "Operação cancelada.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro inesperado no desbloqueio");
            return UnlockResult.Failed(UnlockPhase.Detection, $"Erro inesperado: {ex.Message}");
        }
        finally
        {
            if (ownsTransport)
            {
                transport.Dispose();
            }

            _telnetClient.Disconnect();
        }
    }

    // ============================================================
    // ESTRATÉGIA A: ConfigBin Only (F670L, F6201B)
    // ============================================================
    private async Task<UnlockResult> UnlockViaConfigBinAsync(
        IBoundOntTransport transport,
        ZteProfile profile,
        OntIdentitySnapshot identity,
        byte[] originalConfigBin,
        UnlockOptions options,
        BackupTicket backupTicket,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        try
        {
            // Decrypt
            _logger.LogInformation("🔓 FASE: Decrypt config.bin via ZCU...");
            var xml = await _zcuBridge.DecryptAsync(originalConfigBin, identity, profile, cancellationToken);

            // Patch
            _logger.LogInformation("🔧 FASE: Aplicando patches no XML...");
            var (patchedXml, patches) = _xmlPatcher.ApplyPatches(xml, options, identity);

            // Encrypt
            _logger.LogInformation("🔒 FASE: Re-encrypt config.bin via ZCU...");
            var newConfigBin = await _zcuBridge.EncryptAsync(patchedXml, identity, profile, cancellationToken);

            // Upload
            _logger.LogInformation("📤 FASE: Upload do config.bin modificado...");
            var uploadSuccess = await _configBinService.UploadAsync(transport, profile, newConfigBin, cancellationToken);

            if (!uploadSuccess)
            {
                return UnlockResult.Failed(
                    UnlockPhase.Upload,
                    "Falha no upload do config.bin modificado. A ONT pode ter rejeitado o arquivo.",
                    backupTicket.FilePath,
                    rollbackAvailable: true,
                    rollbackPath: backupTicket.FilePath);
            }

            // Reboot
            _logger.LogInformation("🔄 FASE: Reboot da ONT...");
            await RebootAsync(transport, cancellationToken);

            sw.Stop();
            return UnlockResult.Succeeded(
                UnlockPhase.Reboot,
                backupTicket.FilePath,
                patches,
                options.EnableSuperAdmin ? "superadmin" : null,
                options.EnableSuperAdmin ? (options.NewAdminPassword ?? "superadmin") : null,
                sw.Elapsed);
        }
        catch (ZcuException ex)
        {
            _logger.LogError(ex, "Falha no ZCU");
            return UnlockResult.Failed(
                UnlockPhase.Decrypt,
                $"Falha no ZCU: {ex.Message}",
                backupTicket.FilePath,
                rollbackAvailable: true,
                rollbackPath: backupTicket.FilePath);
        }
    }

    // ============================================================
    // ESTRATÉGIA B: Hybrid (F6600P — tenta config.bin, fallback zteOnu)
    // ============================================================
    private async Task<UnlockResult> UnlockHybridAsync(
        IBoundOntTransport transport,
        ZteProfile profile,
        OntIdentitySnapshot identity,
        byte[] originalConfigBin,
        UnlockOptions options,
        BackupTicket backupTicket,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        // Primeiro tenta o caminho não-destrutivo (config.bin)
        try
        {
            _logger.LogInformation("Tentando desbloqueio via config.bin (não-destrutivo)...");
            var zcuValidation = await _zcuBridge.ValidateAsync(originalConfigBin);

            if (zcuValidation.IsValid && !string.IsNullOrEmpty(profile.AesKey))
            {
                return await UnlockViaConfigBinAsync(
                    transport, profile, identity, originalConfigBin, options, backupTicket, sw, cancellationToken);
            }

            _logger.LogWarning("Config.bin inválido ou chave AES ausente. Caindo para zteOnu...");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha no config.bin. Caindo para zteOnu...");
        }

        // Fallback para zteOnu
        return await UnlockViaZteOnuAsync(transport, profile, identity, options, backupTicket, sw, cancellationToken);
    }

    // ============================================================
    // ESTRATÉGIA C: zteOnu Only (H198A, H199A, H3601)
    // ============================================================
    private async Task<UnlockResult> UnlockViaZteOnuAsync(
        IBoundOntTransport transport,
        ZteProfile profile,
        OntIdentitySnapshot identity,
        UnlockOptions options,
        BackupTicket backupTicket,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "⚠️ ESTRATÉGIA ZTEONU: Isso causará FACTORY RESET na ONT! " +
            "Todas as configurações serão perdidas exceto as que reconfigurarmos.");

        // 1. Executar zteOnu
        var zteOnuResult = await _zteOnuBridge.EnableTelnetAsync(
            transport.BoundAddress.ToString(),
            "admin", // username padrão para zteOnu
            "admin", // password padrão para zteOnu
            profile,
            cancellationToken);

        if (!zteOnuResult.Success)
        {
            return UnlockResult.Failed(
                UnlockPhase.ZteOnuFactoryMode,
                $"zteOnu falhou: {zteOnuResult.ErrorMessage}",
                backupTicket.FilePath,
                rollbackAvailable: true,
                rollbackPath: backupTicket.FilePath);
        }

        // 2. Aguardar reboot e Telnet
        _logger.LogInformation("Aguardando 30s para reboot e Telnet...");
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

        // 3. Conectar Telnet
        var telnetIp = transport.BoundAddress.ToString();
        var telnetConnected = await _telnetClient.ConnectAsync(
            telnetIp,
            zteOnuResult.TelnetUser,
            zteOnuResult.TelnetPass,
            23,
            cancellationToken);

        if (!telnetConnected)
        {
            // Tentar credenciais padrão
            _logger.LogWarning("Tentando credenciais padrão root/Zte521...");
            telnetConnected = await _telnetClient.ConnectAsync(
                telnetIp, "root", "Zte521", 23, cancellationToken);
        }

        if (!telnetConnected)
        {
            return UnlockResult.Failed(
                UnlockPhase.TelnetCommands,
                "Não foi possível conectar ao Telnet após zteOnu.",
                backupTicket.FilePath,
                rollbackAvailable: true,
                rollbackPath: backupTicket.FilePath);
        }

        // 4. Executar script de desbloqueio
        var telnetResult = await _telnetClient.ExecuteUnlockScriptAsync(cancellationToken);
        _telnetClient.Disconnect();

        if (!telnetResult.Success)
        {
            return UnlockResult.Failed(
                UnlockPhase.TelnetCommands,
                $"Script Telnet falhou: {telnetResult.Summary}",
                backupTicket.FilePath,
                rollbackAvailable: false); // zteOnu já fez factory reset, backup original não serve mais
        }

        // 5. Reboot final
        _logger.LogInformation("Reboot final para aplicar configurações...");
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

        // Tentar reboot via Telnet novamente
        await _telnetClient.ConnectAsync(telnetIp, "root", "Zte521", 23, cancellationToken);
        await _telnetClient.ExecuteCommandsAsync(new[] { "reboot" }, cancellationToken);
        _telnetClient.Disconnect();

        sw.Stop();
        return UnlockResult.Succeeded(
            UnlockPhase.TelnetCommands,
            backupTicket.FilePath,
            new List<string> { "zteOnu factory mode", "Telnet enable", "Superadmin create", "TR-069 disable" },
            "superadmin",
            "superadmin",
            sw.Elapsed);
    }

    // ============================================================
    // ROLLBACK
    // ============================================================
    public async Task<bool> RollbackAsync(
        AuthorizedDeviceSession session,
        string ticketId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("🔄 INICIANDO ROLLBACK para ticket: {TicketId}", ticketId);

        var identity = ResolveIdentity(session);
        var backups = await _backupService.ListBackupsAsync(identity.SerialNumber);
        var ticket = backups.FirstOrDefault(b => b.TicketId == ticketId);

        if (ticket == null || !ticket.IsRestorable)
        {
            _logger.LogError("Ticket de backup não encontrado ou não restaurável.");
            return false;
        }

        var (transport, ownsTransport) = ResolveTransport(session);
        try
        {
            var uploader = new BackupUploaderAdapter(_configBinService, transport, 
                _profileRepository.FindByModel(identity.Model) 
                ?? _profileRepository.GetGenericFallback(identity.Model));

            return await _backupService.RestoreAsync(ticket, uploader, cancellationToken);
        }
        finally
        {
            if (ownsTransport)
            {
                transport.Dispose();
            }
        }
    }

    // ============================================================
    // HELPERS
    // ============================================================
    private (IBoundOntTransport Transport, bool Owns) ResolveTransport(AuthorizedDeviceSession session)
    {
        var existing = _sessionStore.Transport;
        if (existing is not null
            && existing.BoundAddress.Equals(session.Endpoint.Address)
            && existing.HasSessionCookie)
        {
            return (existing, false);
        }

        return (_transportFactory.Create(session.Endpoint, session.PinnedCertificateSha256), true);
    }

    private OntIdentitySnapshot ResolveIdentity(AuthorizedDeviceSession session)
    {
        var identity = session.Identity ?? _sessionStore.Snapshot?.Identity;
        return new OntIdentitySnapshot
        {
            Model = identity?.Model ?? "Unknown",
            FirmwareVersion = identity?.Firmware?.SoftwareVersion ?? "Unknown",
            SerialNumber = identity?.SerialNumber ?? "Unknown",
            MacAddress = identity?.MacAddress ?? "Unknown",
            HardwareVersion = identity?.Firmware?.HardwareVersion ?? "Unknown"
        };
    }

    private async Task RebootAsync(IBoundOntTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            // Tentar reboot via endpoint comum
            await transport.GetAsync("/?_type=menuData&_tag=reboot_entry", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao solicitar reboot via HTTP. Reboot manual pode ser necessário.");
        }
    }

    /// <summary>
    /// Adapter que permite o BackupService usar o ConfigBinService para upload.
    /// </summary>
    private class BackupUploaderAdapter : IConfigBinUploader
    {
        private readonly IConfigBinService _service;
        private readonly IBoundOntTransport _transport;
        private readonly ZteProfile _profile;

        public BackupUploaderAdapter(IConfigBinService service, IBoundOntTransport transport, ZteProfile profile)
        {
            _service = service;
            _transport = transport;
            _profile = profile;
        }

        public Task<bool> UploadAsync(byte[] configBinData, CancellationToken cancellationToken = default)
            => _service.UploadAsync(_transport, _profile, configBinData, cancellationToken);
    }
}
