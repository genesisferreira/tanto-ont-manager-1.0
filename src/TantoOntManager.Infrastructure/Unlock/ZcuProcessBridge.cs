using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using TantoOntManager.Domain.Profiles;
using TantoOntManager.Domain.Unlock;

namespace TantoOntManager.Infrastructure.Unlock;

/// <summary>
/// Bridge para o zte-config-utility (ZCU) via processo Python.
/// Responsável por criptografar/descriptografar config.bin.
/// </summary>
public interface IZcuBridge
{
    /// <summary>
    /// Descriptografa um config.bin para XML.
    /// </summary>
    Task<string> DecryptAsync(
        byte[] configBin,
        OntIdentitySnapshot identity,
        ZteProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Criptografa um XML modificado de volta para config.bin.
    /// </summary>
    Task<byte[]> EncryptAsync(
        string xmlContent,
        OntIdentitySnapshot identity,
        ZteProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Valida se o config.bin pode ser processado pelo ZCU.
    /// </summary>
    Task<ZcuValidationResult> ValidateAsync(byte[] configBin);
}

public sealed class ZcuProcessBridge : IZcuBridge
{
    private readonly ILogger<ZcuProcessBridge> _logger;
    private readonly string _pythonPath;
    private readonly string _zcuPath;
    private readonly TimeSpan _timeout;

    public ZcuProcessBridge(
        ILogger<ZcuProcessBridge> logger,
        string pythonPath = "python",
        string zcuPath = @"D:\Tools\zte-config-utility",
        TimeSpan? timeout = null)
    {
        _logger = logger;
        _pythonPath = pythonPath;
        _zcuPath = zcuPath;
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
    }

    public async Task<string> DecryptAsync(
        byte[] configBin,
        OntIdentitySnapshot identity,
        ZteProfile profile,
        CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tanto_zcu_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var inputFile = Path.Combine(tempDir, "config.bin");
            var outputFile = Path.Combine(tempDir, "config.xml");
            await File.WriteAllBytesAsync(inputFile, configBin, cancellationToken);

            var args = BuildAutoPyArgs("decode", inputFile, outputFile, identity, profile);

            _logger.LogInformation(
                "ZCU Decrypt iniciado: {Model} | Serial: {Serial} | PayloadType: {Payload}",
                profile.Model, identity.SerialNumber, profile.PayloadType);

            var (exitCode, stdout, stderr) = await RunProcessAsync(args, cancellationToken);

            if (exitCode != 0)
            {
                _logger.LogError("ZCU Decrypt falhou: {Stderr}", stderr);
                throw new ZcuException($"Falha na descriptografia: {stderr}");
            }

            if (!File.Exists(outputFile))
            {
                _logger.LogError("ZCU não gerou arquivo XML de saída.");
                throw new ZcuException("Arquivo XML de saída não encontrado.");
            }

            var xml = await File.ReadAllTextAsync(outputFile, cancellationToken);

            _logger.LogInformation(
                "ZCU Decrypt concluído: {XmlLength} chars | Preview: {Preview}",
                xml.Length, xml.Length > 100 ? xml[..100] : xml);

            return xml;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public async Task<byte[]> EncryptAsync(
        string xmlContent,
        OntIdentitySnapshot identity,
        ZteProfile profile,
        CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tanto_zcu_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var inputFile = Path.Combine(tempDir, "config.xml");
            var outputFile = Path.Combine(tempDir, "config_new.bin");
            await File.WriteAllTextAsync(inputFile, xmlContent, cancellationToken);

            var args = BuildEncodePyArgs(inputFile, outputFile, identity, profile);

            _logger.LogInformation(
                "ZCU Encrypt iniciado: {Model} | Signature: {Signature} | PayloadType: {Payload}",
                profile.Model, profile.Signature, profile.PayloadType);

            var (exitCode, stdout, stderr) = await RunProcessAsync(args, cancellationToken);

            if (exitCode != 0)
            {
                _logger.LogError("ZCU Encrypt falhou: {Stderr}", stderr);
                throw new ZcuException($"Falha na criptografia: {stderr}");
            }

            if (!File.Exists(outputFile))
            {
                _logger.LogError("ZCU não gerou arquivo bin de saída.");
                throw new ZcuException("Arquivo bin de saída não encontrado.");
            }

            var data = await File.ReadAllBytesAsync(outputFile, cancellationToken);

            _logger.LogInformation(
                "ZCU Encrypt concluído: {Size} bytes",
                data.Length);

            return data;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public async Task<ZcuValidationResult> ValidateAsync(byte[] configBin)
    {
        // Tenta detectar se é um config.bin válido ZTE
        if (configBin.Length < 128)
            return ZcuValidationResult.Invalid("Arquivo muito pequeno");

        // Verificar assinatura mágica no header
        var headerHex = Convert.ToHexString(configBin[..64]);

        if (headerHex.Contains("9999999944444444"))
            return ZcuValidationResult.Valid("Família H198A/H199A/H3601 detectada");

        if (headerHex.Contains("04030201"))
            return ZcuValidationResult.Valid("Família F670L/F6600P detectada");

        // Tenta rodar ZCU em modo info
        var tempDir = Path.Combine(Path.GetTempPath(), $"tanto_zcu_val_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var inputFile = Path.Combine(tempDir, "config.bin");
            await File.WriteAllBytesAsync(inputFile, configBin);

            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = $"\"{_zcuPath}/examples/auto.py\" \"{inputFile}\" \"{tempDir}/dump.txt\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return ZcuValidationResult.Invalid("Não foi possível iniciar ZCU");

            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(waitCts.Token);

            if (stdout.Contains("Successfully decrypted") || stdout.Contains("payload type"))
                return ZcuValidationResult.Valid(stdout);

            return ZcuValidationResult.Invalid(stdout);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private string BuildAutoPyArgs(string mode, string input, string output, OntIdentitySnapshot identity, ZteProfile profile)
    {
        _ = mode;
        var sb = new StringBuilder();
        sb.Append($"\"{_zcuPath}/examples/auto.py\" ");

        if (!string.IsNullOrEmpty(identity.SerialNumber) && profile.RequiresSerialForDecrypt)
            sb.Append($"--serial {identity.SerialNumber} ");

        if (!string.IsNullOrEmpty(identity.MacAddress) && profile.RequiresMacForDecrypt)
            sb.Append($"--mac {identity.MacAddress} ");

        sb.Append($"\"{input}\" \"{output}\"");
        return sb.ToString();
    }

    private string BuildEncodePyArgs(string input, string output, OntIdentitySnapshot identity, ZteProfile profile)
    {
        var sb = new StringBuilder();
        sb.Append($"\"{_zcuPath}/examples/encode.py\" ");
        sb.Append($"--signature \"{profile.Signature}\" ");
        sb.Append($"--payload-type {profile.PayloadType} ");

        if (!string.IsNullOrEmpty(identity.SerialNumber))
            sb.Append($"--serial {identity.SerialNumber} ");

        if (!string.IsNullOrEmpty(identity.MacAddress))
            sb.Append($"--mac {identity.MacAddress} ");

        if (profile.NeedsClaroHeader)
            sb.Append("--include-header ");

        sb.Append($"\"{input}\" \"{output}\"");
        return sb.ToString();
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _zcuPath
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Falha ao iniciar processo Python.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { /* ignore */ }
            throw new ZcuException($"ZCU excedeu timeout de {_timeout.TotalSeconds}s");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, true); } catch { /* ignore cleanup errors */ }
    }
}

public sealed record ZcuValidationResult
{
    public bool IsValid { get; init; }
    public string Message { get; init; } = string.Empty;

    public static ZcuValidationResult Valid(string message) => new() { IsValid = true, Message = message };
    public static ZcuValidationResult Invalid(string message) => new() { IsValid = false, Message = message };
}

public class ZcuException : Exception
{
    public ZcuException(string message) : base(message) { }
    public ZcuException(string message, Exception inner) : base(message, inner) { }
}
