using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace TantoOntManager.Domain.Profiles;

/// <summary>
/// Repositório de perfis ZTE. Contém todas as configurações homologadas
/// e oferece fallback para perfis genéricos quando necessário.
/// </summary>
public sealed class ZteProfileRepository
{
    private readonly ILogger<ZteProfileRepository> _logger;
    private readonly Dictionary<string, ZteProfile> _profiles;

    public ZteProfileRepository(ILogger<ZteProfileRepository> logger)
    {
        _logger = logger;
        _profiles = new Dictionary<string, ZteProfile>(StringComparer.OrdinalIgnoreCase);
        LoadHomologatedProfiles();
    }

    private void LoadHomologatedProfiles()
    {
        // ============================================================
        // F6201B V9.3.10P8N1 — Perfil de referência (seu código original)
        // ============================================================
        Add(new ZteProfile
        {
            ProfileId = "f6201b-v9310p8n1",
            Model = "F6201B",
            FirmwarePattern = @"V9\.3\.10P8N1",
            HardwarePattern = @"V9\.3\.12",
            AuthType = AuthType.ChallengeSha256,
            LoginEndpoint = "/?_type=loginData&_tag=login_entry",
            ChallengeEndpoint = "/?_type=loginData&_tag=login_token",
            SessionCookiePattern = "SID_HTTPS_.*",
            HashAlgorithm = "sha256",
            MenuViewType = "menuView",
            MenuDataType = "menuData",
            ObjectIdMapping = new Dictionary<string, string>
            {
                ["DeviceInfo"] = "OBJ_DEVINFO_ID",
                ["GponStatus"] = "OBJ_GPONREGSTATUS_ID",
                ["WanStatus"] = "OBJ_WANSTATUS_ID",
                ["WanConfig"] = "OBJ_WANCONFIG_ID"
            },
            DownloadConfigEndpoint = "/cgi-bin/backup.cgi",
            UploadConfigEndpoint = "/cgi-bin/upload.cgi",
            AesKey = "L04&Product@5A238dc79b15726d5c06",
            AesIv = "ZTE%FN$GponNJ025678b02a85c63c706",
            PayloadType = 5,
            Signature = "ZXHN F6201B",
            MagicHeader = "04 03 02 01",
            RequiresSerialForDecrypt = true,
            UnlockStrategy = UnlockStrategy.ConfigBinOnly,
            SupportsTelnetEnable = true,
            IsHomologated = true,
            HomologationNotes = "Perfil de referência. Testado extensivamente."
        });

        // ============================================================
        // F670L V1.1.20P3N8B (firmware V1)
        // ============================================================
        Add(new ZteProfile
        {
            ProfileId = "f670l-v1",
            Model = "F670L",
            FirmwarePattern = @"V1\..*",
            AuthType = AuthType.ChallengeSha256,
            LoginEndpoint = "/?_type=loginData&_tag=login_entry",
            ChallengeEndpoint = "/?_type=loginData&_tag=login_token",
            SessionCookiePattern = "SID_HTTPS_.*",
            HashAlgorithm = "sha256",
            ObjectIdMapping = new Dictionary<string, string>
            {
                ["DeviceInfo"] = "OBJ_DEVINFO_ID",
                ["GponStatus"] = "OBJ_GPONREGSTATUS_ID",
                ["WanStatus"] = "OBJ_WANSTATUS_ID",
                ["WanConfig"] = "OBJ_WANCONFIG_ID"
            },
            DownloadConfigEndpoint = "/cgi-bin/backup.cgi",
            UploadConfigEndpoint = "/cgi-bin/upload.cgi",
            AesKey = "L04&Product@5A238dc79b15726d5c06",
            AesIv = "ZTE%FN$GponNJ025678b02a85c63c706",
            PayloadType = 5,
            Signature = "ZXHN F670L",
            MagicHeader = "04 03 02 01",
            RequiresSerialForDecrypt = true,
            UnlockStrategy = UnlockStrategy.ConfigBinOnly,
            SupportsTelnetEnable = true,
            IsHomologated = true,
            HomologationNotes = "Chaves AES confirmadas pela comunidade ZCU."
        });

        // ============================================================
        // F670L V9.0.11P1N40 (firmware V9)
        // ============================================================
        Add(new ZteProfile
        {
            ProfileId = "f670l-v9",
            Model = "F670L",
            FirmwarePattern = @"V9\..*",
            AuthType = AuthType.ChallengeSha256,
            LoginEndpoint = "/?_type=loginData&_tag=login_entry",
            ChallengeEndpoint = "/?_type=loginData&_tag=login_token",
            SessionCookiePattern = "SID_HTTPS_.*",
            HashAlgorithm = "sha256",
            ObjectIdMapping = new Dictionary<string, string>
            {
                ["DeviceInfo"] = "OBJ_DEVINFO_ID",
                ["GponStatus"] = "OBJ_GPONREGSTATUS_ID",
                ["WanStatus"] = "OBJ_WANSTATUS_ID",
                ["WanConfig"] = "OBJ_WANCONFIG_ID"
            },
            DownloadConfigEndpoint = "/cgi-bin/backup.cgi",
            UploadConfigEndpoint = "/cgi-bin/upload.cgi",
            AesKey = "L04&Product@5A238dc79b15726d5c06",
            AesIv = "ZTE%FN$GponNJ025678b02a85c63c706",
            PayloadType = 5,
            Signature = "ZXHN F670L",
            MagicHeader = "04 03 02 01",
            RequiresSerialForDecrypt = true,
            UnlockStrategy = UnlockStrategy.ConfigBinOnly,
            SupportsTelnetEnable = true,
            IsHomologated = true,
            HomologationNotes = "Mesmas chaves da V1. Testado em múltiplas unidades."
        });

        // ============================================================
        // F6600P (Claro e outras operadoras)
        // ============================================================
        Add(new ZteProfile
        {
            ProfileId = "f6600p-generic",
            Model = "F6600P",
            FirmwarePattern = @"V.*",
            AuthType = AuthType.FormPost,
            LoginEndpoint = "/cgi-bin/login.cgi",
            SessionCookiePattern = "SID_.*",
            HashAlgorithm = "none",
            ObjectIdMapping = new Dictionary<string, string>
            {
                ["DeviceInfo"] = "OBJ_DEVINFO_ID",
                ["GponStatus"] = "OBJ_GPONREGSTATUS_ID"
            },
            DownloadConfigEndpoint = "/config.bin",
            UploadConfigEndpoint = "/upload.cgi",
            AesKey = "########487157d99648", // Chave parcial — requer serial
            AesIv = "ZTE%FN$GponNJ025",
            PayloadType = 5,
            Signature = "ZXHN F6600P",
            MagicHeader = "04 03 02 01",
            RequiresSerialForDecrypt = true,
            NeedsClaroHeader = false,
            UnlockStrategy = UnlockStrategy.ZteOnuPreferred, // Config.bin pode falhar
            SupportsTelnetEnable = true,
            IsHomologated = false,
            HomologationNotes = "Chave AES parcial. Recomendado testar com zteOnu primeiro."
        });

        // ============================================================
        // F6645P
        // ============================================================
        Add(new ZteProfile
        {
            ProfileId = "f6645p-generic",
            Model = "F6645P",
            FirmwarePattern = @"V.*",
            AuthType = AuthType.FormPost,
            LoginEndpoint = "/cgi-bin/login.cgi",
            SessionCookiePattern = "SID_.*",
            HashAlgorithm = "none",
            DownloadConfigEndpoint = "/config.bin",
            UploadConfigEndpoint = "/upload.cgi",
            AesKey = "########487157d99648",
            AesIv = "ZTE%FN$GponNJ025",
            PayloadType = 5,
            Signature = "ZXHN F6645P",
            RequiresSerialForDecrypt = true,
            UnlockStrategy = UnlockStrategy.ZteOnuPreferred,
            SupportsTelnetEnable = true,
            IsHomologated = false,
            HomologationNotes = "Similar ao F6600P. Testar com cautela."
        });

        // ============================================================
        // H198A (família 99 99 99 99 44 44 44 44 55 55 55 55 AA AA AA AA)
        // ============================================================
        Add(new ZteProfile
        {
            ProfileId = "h198a-generic",
            Model = "H198A",
            FirmwarePattern = @"V.*",
            AuthType = AuthType.FormPost,
            LoginEndpoint = "/cgi-bin/login.cgi",
            SessionCookiePattern = "SID_.*",
            HashAlgorithm = "none",
            ObjectIdMapping = new Dictionary<string, string>
            {
                ["DeviceInfo"] = "OBJ_DEVINFO_ID"
            },
            DownloadConfigEndpoint = "/config.bin",
            UploadConfigEndpoint = "/upload.cgi",
            AesKey = string.Empty, // Não confirmada — usa zteOnu
            AesIv = string.Empty,
            PayloadType = 5,
            Signature = "ZXHN H198A",
            MagicHeader = "99 99 99 99 44 44 44 44 55 55 55 55 AA AA AA AA",
            UnlockStrategy = UnlockStrategy.ZteOnuOnly,
            SupportsTelnetEnable = true,
            TelnetDefaultUser = "root",
            TelnetDefaultPass = "Zte521",
            IsHomologated = true,
            HomologationNotes = "Desbloqueio via zteOnu + Telnet confirmado. Não tentar config.bin sem chave."
        });

        // ============================================================
        // H199A (mesma família H198A)
        // ============================================================
        Add(new ZteProfile
        {
            ProfileId = "h199a-generic",
            Model = "H199A",
            FirmwarePattern = @"V.*",
            AuthType = AuthType.FormPost,
            LoginEndpoint = "/cgi-bin/login.cgi",
            SessionCookiePattern = "SID_.*",
            HashAlgorithm = "none",
            DownloadConfigEndpoint = "/config.bin",
            UploadConfigEndpoint = "/upload.cgi",
            AesKey = string.Empty,
            AesIv = string.Empty,
            PayloadType = 5,
            Signature = "ZXHN H199A",
            MagicHeader = "99 99 99 99 44 44 44 44 55 55 55 55 AA AA AA AA",
            UnlockStrategy = UnlockStrategy.ZteOnuOnly,
            SupportsTelnetEnable = true,
            TelnetDefaultUser = "root",
            TelnetDefaultPass = "Zte521",
            IsHomologated = false,
            HomologationNotes = "Mesma família do H198A. Usar mesmo procedimento."
        });

        // ============================================================
        // H3601 (mesma família, firmware V9.1)
        // ============================================================
        Add(new ZteProfile
        {
            ProfileId = "h3601-v9",
            Model = "H3601",
            FirmwarePattern = @"V9\..*",
            AuthType = AuthType.FormPost,
            LoginEndpoint = "/cgi-bin/login.cgi",
            SessionCookiePattern = "SID_.*",
            HashAlgorithm = "none",
            DownloadConfigEndpoint = "/config.bin",
            UploadConfigEndpoint = "/upload.cgi",
            AesKey = "L04&Product@5A238dc79b15726d5c06", // Mesma da F670L!
            AesIv = "ZTE%FN$GponNJ025678b02a85c63c706",
            PayloadType = 5,
            Signature = "H3601 V9.1",
            MagicHeader = "99 99 99 99 44 44 44 44 55 55 55 55 AA AA AA AA",
            RequiresSerialForDecrypt = true,
            UnlockStrategy = UnlockStrategy.ZteOnuPreferred, // zteOnu mais confiável, mas config.bin funciona
            SupportsTelnetEnable = true,
            TelnetDefaultUser = "root",
            TelnetDefaultPass = "Zte521",
            IsHomologated = false,
            HomologationNotes = "Chave AES igual à F670L. zteOnu é método mais confiável."
        });

        // ============================================================
        // F6601P (Payload Type 6 — suporte limitado)
        // ============================================================
        Add(new ZteProfile
        {
            ProfileId = "f6601p-generic",
            Model = "F6601P",
            FirmwarePattern = @"V.*",
            AuthType = AuthType.ChallengeSha256,
            LoginEndpoint = "/?_type=loginData&_tag=login_entry",
            ChallengeEndpoint = "/?_type=loginData&_tag=login_token",
            SessionCookiePattern = "SID_HTTPS_.*",
            HashAlgorithm = "sha256",
            DownloadConfigEndpoint = "/cgi-bin/backup.cgi",
            UploadConfigEndpoint = "/cgi-bin/upload.cgi",
            AesKey = "D7203B59ecc02b299e68",
            AesIv = "ZTE%FN$GponNJ025",
            PayloadType = 6, // ⚠️ Tipo 6 requer serial + mac
            Signature = "ZXHN F6601P",
            RequiresSerialForDecrypt = true,
            RequiresMacForDecrypt = true,
            UnlockStrategy = UnlockStrategy.ConfigBinOnly,
            SupportsTelnetEnable = false,
            IsHomologated = false,
            HomologationNotes = "Payload type 6. ZCU tem suporte limitado. Requer serial + MAC."
        });

        _logger.LogInformation(
            "Repositório de perfis carregado: {Count} perfis homologados.",
            _profiles.Count);
    }

    private void Add(ZteProfile profile)
    {
        _profiles[profile.ProfileId] = profile;
    }

    /// <summary>
    /// Busca perfil exato por modelo + firmware.
    /// </summary>
    public ZteProfile? FindExact(string model, string firmware)
    {
        var match = _profiles.Values
            .Where(p => p.MatchesModel(model))
            .Where(p => p.MatchesFirmware(firmware))
            .OrderByDescending(p => p.IsHomologated)
            .ThenBy(p => p.ProfileId)
            .FirstOrDefault();

        if (match != null)
        {
            _logger.LogInformation(
                "Perfil exato encontrado: {ProfileId} para {Model} {Firmware}",
                match.ProfileId, model, firmware);
        }

        return match;
    }

    /// <summary>
    /// Busca perfil por modelo (ignora firmware, pega o mais homologado).
    /// </summary>
    public ZteProfile? FindByModel(string model)
    {
        return _profiles.Values
            .Where(p => p.MatchesModel(model))
            .OrderByDescending(p => p.IsHomologated)
            .ThenBy(p => p.ProfileId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Retorna perfil genérico como último recurso.
    /// </summary>
    public ZteProfile GetGenericFallback(string model)
    {
        _logger.LogWarning(
            "Usando perfil genérico para {Model}. Operações de desbloqueio desativadas.",
            model);

        return new ZteProfile
        {
            ProfileId = $"{model.ToLowerInvariant()}-generic-fallback",
            Model = model,
            FirmwarePattern = ".*",
            AuthType = AuthType.FormPost,
            LoginEndpoint = "/cgi-bin/login.cgi",
            SessionCookiePattern = "SID_.*",
            HashAlgorithm = "none",
            DownloadConfigEndpoint = "/config.bin",
            UploadConfigEndpoint = "/upload.cgi",
            PayloadType = 5,
            Signature = model,
            UnlockStrategy = UnlockStrategy.Unsupported,
            IsHomologated = false,
            HomologationNotes = "Perfil genérico. Desbloqueio não disponível."
        };
    }

    public IReadOnlyCollection<ZteProfile> GetAllProfiles()
        => _profiles.Values.ToList();
}
