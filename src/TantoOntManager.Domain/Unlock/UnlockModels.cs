using TantoOntManager.Domain.Sessions;

namespace TantoOntManager.Domain.Unlock;

/// <summary>
/// Opções de desbloqueio selecionadas pelo usuário.
/// </summary>
public sealed record UnlockOptions
{
    public bool EnableSuperAdmin { get; init; } = true;
    public bool DisableTr069 { get; init; } = true;
    public bool EnableBridgeMode { get; init; } = false;
    public bool EnableTelnet { get; init; } = true;
    public bool EnableSsh { get; init; } = false;
    public bool ChangeAdminPassword { get; init; } = false;
    public string? NewAdminPassword { get; init; }
    public bool PreserveWanConfig { get; init; } = true;
    public bool PreserveWifiConfig { get; init; } = true;
    public bool AutoRollbackOnFailure { get; init; } = true;
    public TimeSpan UploadTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Resultado da operação de desbloqueio.
/// </summary>
public sealed record UnlockResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public UnlockPhase Phase { get; init; }
    public string? BackupPath { get; init; }
    public string? ModifiedConfigPath { get; init; }
    public IReadOnlyList<string> AppliedPatches { get; init; } = Array.Empty<string>();
    public string? NewAdminUsername { get; init; }
    public string? NewAdminPassword { get; init; }
    public bool RollbackAvailable { get; init; }
    public string? RollbackPath { get; init; }
    public TimeSpan Duration { get; init; }

    public static UnlockResult Succeeded(
        UnlockPhase finalPhase,
        string backupPath,
        IReadOnlyList<string> patches,
        string? newAdminUser,
        string? newAdminPass,
        TimeSpan duration)
        => new()
        {
            Success = true,
            Message = "Desbloqueio concluído com sucesso.",
            Phase = finalPhase,
            BackupPath = backupPath,
            AppliedPatches = patches,
            NewAdminUsername = newAdminUser,
            NewAdminPassword = newAdminPass,
            RollbackAvailable = true,
            RollbackPath = backupPath,
            Duration = duration
        };

    public static UnlockResult Failed(
        UnlockPhase failedPhase,
        string reason,
        string? backupPath = null,
        bool rollbackAvailable = false,
        string? rollbackPath = null)
        => new()
        {
            Success = false,
            Message = reason,
            Phase = failedPhase,
            BackupPath = backupPath,
            RollbackAvailable = rollbackAvailable,
            RollbackPath = rollbackPath
        };

    public static UnlockResult UnsupportedModel(string model)
        => new()
        {
            Success = false,
            Message = $"Modelo {model} não possui perfil de desbloqueio homologado.",
            Phase = UnlockPhase.Detection
        };
}

public enum UnlockPhase
{
    Detection,
    Authentication,
    IdentityRead,
    Backup,
    Decrypt,
    Patch,
    Encrypt,
    Upload,
    Reboot,
    Verification,
    ZteOnuFactoryMode,
    TelnetCommands,
    Rollback
}

/// <summary>
/// Snapshot de identidade da ONT necessário para operações de unlock.
/// </summary>
public sealed record OntIdentitySnapshot
{
    public string Model { get; init; } = string.Empty;
    public string FirmwareVersion { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string MacAddress { get; init; } = string.Empty;
    public string HardwareVersion { get; init; } = string.Empty;
    public string? GponPassword { get; init; }
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
}

public interface IZteUnlockOrchestrator
{
    Task<UnlockResult> ExecuteAsync(
        AuthorizedDeviceSession session,
        UnlockOptions options,
        CancellationToken cancellationToken = default);

    Task<bool> RollbackAsync(
        AuthorizedDeviceSession session,
        string ticketId,
        CancellationToken cancellationToken = default);
}

public interface IUnlockableDeviceAdapter
{
    Task<UnlockResult> UnlockAsync(
        AuthorizedDeviceSession session,
        UnlockOptions options,
        CancellationToken cancellationToken = default);

    Task<bool> RollbackAsync(
        AuthorizedDeviceSession session,
        string ticketId,
        CancellationToken cancellationToken = default);
}
