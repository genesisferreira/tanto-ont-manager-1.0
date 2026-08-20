using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace TantoOntManager.Domain.Profiles;

/// <summary>
/// Tipo de autenticação suportado pela ONT.
/// </summary>
public enum AuthType
{
    FormPost,           // POST simples com username/password (F6600P, H198A)
    ChallengeSha256,    // Challenge-response SHA-256 (F6201B, F670L V9)
    ChallengeMd5,       // Challenge-response MD5 (algumas F600)
    AjaxXml,            // Login via XML/AJAX (firmwares antigas)
    Unknown
}

/// <summary>
/// Estratégia de desbloqueio recomendada para o modelo.
/// </summary>
public enum UnlockStrategy
{
    ConfigBinOnly,      // Apenas via download/upload config.bin (mais seguro)
    ZteOnuPreferred,    // zteOnu primeiro, config.bin como fallback
    ZteOnuOnly,         // Apenas via zteOnu + Telnet (H198A/H3601)
    Unsupported
}

/// <summary>
/// Perfil imutável de uma ONT ZTE. Contém todos os parâmetros necessários
/// para detecção, autenticação, leitura e desbloqueio.
/// </summary>
public sealed record ZteProfile
{
    public string ProfileId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string FirmwarePattern { get; init; } = ".*";
    public string HardwarePattern { get; init; } = ".*";

    // Autenticação
    public AuthType AuthType { get; init; } = AuthType.Unknown;
    public string LoginEndpoint { get; init; } = "/cgi-bin/login.cgi";
    public string? ChallengeEndpoint { get; init; }
    public string SessionCookiePattern { get; init; } = "SID_.*";
    public string HashAlgorithm { get; init; } = "none"; // "sha256", "md5", "none"

    // Leitura
    public string MenuViewType { get; init; } = "menuView";
    public string MenuDataType { get; init; } = "menuData";
    public IReadOnlyDictionary<string, string> ObjectIdMapping { get; init; } 
        = ImmutableDictionary<string, string>.Empty;

    // Config.bin
    public string DownloadConfigEndpoint { get; init; } = "/cgi-bin/backup.cgi";
    public string UploadConfigEndpoint { get; init; } = "/cgi-bin/upload.cgi";
    public string? ConfigBinQueryParameter { get; init; } // ex: "action=backup" para alguns modelos
    public string AesKey { get; init; } = string.Empty;
    public string AesIv { get; init; } = string.Empty;
    public int PayloadType { get; init; } = 5;
    public string Signature { get; init; } = string.Empty; // ex: "ZXHN F670L"
    public string MagicHeader { get; init; } = string.Empty; // hex signature para detectar payload
    public bool RequiresSerialForDecrypt { get; init; } = false;
    public bool RequiresMacForDecrypt { get; init; } = false;
    public bool NeedsClaroHeader { get; init; } = false;

    // Desbloqueio
    public UnlockStrategy UnlockStrategy { get; init; } = UnlockStrategy.Unsupported;
    public string? ZteOnuExtraArgs { get; init; }
    public bool SupportsTelnetEnable { get; init; } = false;
    public string? TelnetDefaultUser { get; init; }
    public string? TelnetDefaultPass { get; init; }

    // Segurança / Homologação
    public bool IsHomologated { get; init; } = false;
    public string? HomologationNotes { get; init; }
    public DateTime ProfileDate { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Verifica se o firmware informado corresponde ao padrão deste perfil.
    /// </summary>
    public bool MatchesFirmware(string firmware)
        => !string.IsNullOrEmpty(firmware) && Regex.IsMatch(firmware, FirmwarePattern, RegexOptions.IgnoreCase);

    /// <summary>
    /// Verifica se o hardware informado corresponde ao padrão deste perfil.
    /// </summary>
    public bool MatchesHardware(string hardware)
        => !string.IsNullOrEmpty(hardware) && Regex.IsMatch(hardware, HardwarePattern, RegexOptions.IgnoreCase);

    /// <summary>
    /// Compara modelo observado (ex.: "ZXHN F6201B") com o modelo do perfil (ex.: "F6201B").
    /// </summary>
    public bool MatchesModel(string observedModel)
        => ModelKeysMatch(Model, observedModel);

    public static bool ModelKeysMatch(string profileModel, string observedModel)
    {
        var left = NormalizeModelKey(profileModel);
        var right = NormalizeModelKey(observedModel);
        return left.Length > 0
               && right.Length > 0
               && left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeModelKey(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        var trimmed = model.Trim();
        if (trimmed.StartsWith("ZXHN ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["ZXHN ".Length..];
        }

        return trimmed.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifica se este perfil suporta desbloqueio.
    /// </summary>
    public bool CanUnlock 
        => UnlockStrategy != UnlockStrategy.Unsupported 
           && !string.IsNullOrEmpty(AesKey) 
           && !string.IsNullOrEmpty(Signature);
}
