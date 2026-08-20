using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TantoOntManager.Domain.Unlock;

namespace TantoOntManager.Domain.Backup;

/// <summary>
/// Serviço de backup e restauração de configurações de ONT.
/// Garante que qualquer operação de desbloqueio possa ser revertida.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Cria backup do config.bin original antes de qualquer modificação.
    /// </summary>
    Task<BackupTicket> CreateBackupAsync(
        byte[] originalConfigBin,
        OntIdentitySnapshot identity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restaura a ONT para o config.bin original.
    /// </summary>
    Task<bool> RestoreAsync(
        BackupTicket ticket,
        IConfigBinUploader uploader,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista backups disponíveis para um modelo/serial.
    /// </summary>
    Task<IReadOnlyList<BackupTicket>> ListBackupsAsync(string serialNumber);

    /// <summary>
    /// Remove backups antigos além do limite configurado.
    /// </summary>
    Task CleanupOldBackupsAsync(int keepCount = 10);
}

/// <summary>
/// Ticket de backup imutável.
/// </summary>
public sealed record BackupTicket
{
    public string TicketId { get; init; } = Guid.NewGuid().ToString("N");
    public string SerialNumber { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string FirmwareVersion { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool IsRestorable { get; init; } = true;
}

/// <summary>
/// Implementação do serviço de backup com armazenamento local seguro.
/// </summary>
public sealed class LocalBackupService : IBackupService
{
    private readonly ILogger<LocalBackupService> _logger;
    private readonly string _backupBasePath;

    public LocalBackupService(ILogger<LocalBackupService> logger, string? backupBasePath = null)
    {
        _logger = logger;
        _backupBasePath = backupBasePath 
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TantoTelecom", "TantoOntManager", "backups");

        Directory.CreateDirectory(_backupBasePath);
    }

    public async Task<BackupTicket> CreateBackupAsync(
        byte[] originalConfigBin,
        OntIdentitySnapshot identity,
        CancellationToken cancellationToken = default)
    {
        var ticket = new BackupTicket
        {
            SerialNumber = identity.SerialNumber,
            Model = identity.Model,
            FirmwareVersion = identity.FirmwareVersion,
            FileSize = originalConfigBin.Length,
            Sha256 = ComputeSha256(originalConfigBin)
        };

        var safeSerial = SanitizeFileName(identity.SerialNumber);
        var folder = Path.Combine(_backupBasePath, safeSerial);
        Directory.CreateDirectory(folder);

        var fileName = $"{identity.Model}_{identity.FirmwareVersion}_{ticket.TicketId}_{ticket.CreatedAt:yyyyMMdd_HHmmss}.bin";
        fileName = SanitizeFileName(fileName);
        var filePath = Path.Combine(folder, fileName);

        await File.WriteAllBytesAsync(filePath, originalConfigBin, cancellationToken);

        // Salvar metadados JSON junto
        var metaPath = filePath + ".json";
        var meta = new
        {
            ticket.TicketId,
            ticket.SerialNumber,
            ticket.Model,
            ticket.FirmwareVersion,
            ticket.Sha256,
            ticket.CreatedAt,
            ticket.FileSize,
            identity.MacAddress,
            identity.HardwareVersion
        };
        await File.WriteAllTextAsync(metaPath, System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        _logger.LogInformation(
            "Backup criado: {TicketId} | {Model} | {Serial} | {Size} bytes | {Path}",
            ticket.TicketId, identity.Model, identity.SerialNumber, originalConfigBin.Length, filePath);

        return ticket with { FilePath = filePath };
    }

    public async Task<bool> RestoreAsync(
        BackupTicket ticket,
        IConfigBinUploader uploader,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ticket.FilePath))
        {
            _logger.LogError("Arquivo de backup não encontrado: {Path}", ticket.FilePath);
            return false;
        }

        var data = await File.ReadAllBytesAsync(ticket.FilePath, cancellationToken);
        var currentHash = ComputeSha256(data);

        if (!currentHash.Equals(ticket.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Hash do backup não corresponde! Backup corrompido.");
            return false;
        }

        _logger.LogWarning(
            "Iniciando RESTAURAÇÃO do backup {TicketId} para {Serial}",
            ticket.TicketId, ticket.SerialNumber);

        var result = await uploader.UploadAsync(data, cancellationToken);

        if (result)
        {
            _logger.LogInformation("Restauração concluída com sucesso.");
        }
        else
        {
            _logger.LogError("Falha na restauração do backup.");
        }

        return result;
    }

    public Task<IReadOnlyList<BackupTicket>> ListBackupsAsync(string serialNumber)
    {
        var safeSerial = SanitizeFileName(serialNumber);
        var folder = Path.Combine(_backupBasePath, safeSerial);

        if (!Directory.Exists(folder))
            return Task.FromResult<IReadOnlyList<BackupTicket>>(Array.Empty<BackupTicket>());

        var tickets = new List<BackupTicket>();
        foreach (var metaFile in Directory.GetFiles(folder, "*.bin.json"))
        {
            try
            {
                var json = File.ReadAllText(metaFile);
                var meta = System.Text.Json.JsonSerializer.Deserialize<BackupMetadata>(json);
                if (meta != null)
                {
                    var binPath = metaFile.Replace(".json", "");
                    tickets.Add(new BackupTicket
                    {
                        TicketId = meta.TicketId,
                        SerialNumber = meta.SerialNumber,
                        Model = meta.Model,
                        FirmwareVersion = meta.FirmwareVersion,
                        FilePath = binPath,
                        FileSize = meta.FileSize,
                        Sha256 = meta.Sha256,
                        CreatedAt = meta.CreatedAt,
                        IsRestorable = File.Exists(binPath)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler metadata: {File}", metaFile);
            }
        }

        return Task.FromResult<IReadOnlyList<BackupTicket>>(
            tickets.OrderByDescending(t => t.CreatedAt).ToList());
    }

    public Task CleanupOldBackupsAsync(int keepCount = 10)
    {
        foreach (var serialFolder in Directory.GetDirectories(_backupBasePath))
        {
            var files = Directory.GetFiles(serialFolder, "*.bin")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .Skip(keepCount)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                    var meta = file.FullName + ".json";
                    if (File.Exists(meta)) File.Delete(meta);
                    _logger.LogInformation("Backup antigo removido: {File}", file.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao remover backup antigo");
                }
            }
        }

        return Task.CompletedTask;
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private class BackupMetadata
    {
        public string TicketId { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string FirmwareVersion { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public long FileSize { get; set; }
        public string MacAddress { get; set; } = string.Empty;
        public string HardwareVersion { get; set; } = string.Empty;
    }
}

/// <summary>
/// Interface abstrata para upload de config.bin (injetada no BackupService).
/// </summary>
public interface IConfigBinUploader
{
    Task<bool> UploadAsync(byte[] configBinData, CancellationToken cancellationToken = default);
}
