using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TantoOntManager.Domain.Profiles;

namespace TantoOntManager.Infrastructure.Unlock;

/// <summary>
/// Bridge para o zteOnu — habilita Telnet em ONTs ZTE via factory mode.
/// </summary>
public interface IZteOnuBridge
{
    /// <summary>
    /// Executa zteOnu para habilitar Telnet persistente.
    /// </summary>
    Task<ZteOnuResult> EnableTelnetAsync(
        string ip,
        string username,
        string password,
        ZteProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifica se o Telnet está respondendo na ONT.
    /// </summary>
    Task<bool> ProbeTelnetAsync(string ip, int port = 23, CancellationToken cancellationToken = default);
}

public sealed class ZteOnuProcessBridge : IZteOnuBridge
{
    private readonly ILogger<ZteOnuProcessBridge> _logger;
    private readonly string _zteOnuPath;
    private readonly TimeSpan _timeout;

    public ZteOnuProcessBridge(
        ILogger<ZteOnuProcessBridge> logger,
        string zteOnuPath = @"D:\Tools\zteOnu\zteOnu.exe",
        TimeSpan? timeout = null)
    {
        _logger = logger;
        _zteOnuPath = zteOnuPath;
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
    }

    public async Task<ZteOnuResult> EnableTelnetAsync(
        string ip,
        string username,
        string password,
        ZteProfile profile,
        CancellationToken cancellationToken = default)
    {
        var args = $"-i {ip} -u {username} -p {password}";

        if (profile.ZteOnuExtraArgs != null)
            args += $" {profile.ZteOnuExtraArgs}";

        _logger.LogWarning(
            "⚠️ zteOnu será executado. Isso pode causar FACTORY RESET na ONT! " +
            "IP: {Ip} | Modelo: {Model}",
            ip, profile.Model);

        var (exitCode, stdout, stderr) = await RunZteOnuAsync(args, cancellationToken);

        _logger.LogInformation("zteOnu stdout: {Output}", stdout);
        if (!string.IsNullOrEmpty(stderr))
            _logger.LogWarning("zteOnu stderr: {Stderr}", stderr);

        var result = ParseOutput(stdout, stderr);
        result.ExitCode = exitCode;

        if (result.Success)
        {
            _logger.LogInformation(
                "✅ zteOnu concluído. Telnet habilitado. User: {User} | Pass: {Pass}",
                result.TelnetUser, result.TelnetPass);
        }
        else
        {
            _logger.LogError(
                "❌ zteOnu falhou. Código: {ExitCode} | Erro: {Error}",
                exitCode, result.ErrorMessage);
        }

        return result;
    }

    public async Task<bool> ProbeTelnetAsync(string ip, int port = 23, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(ip, port);
            client.Close();
            _logger.LogInformation("Telnet respondendo em {Ip}:{Port}", ip, port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunZteOnuAsync(
        string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _zteOnuPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Falha ao iniciar zteOnu.exe");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { }
            throw new ZteOnuException($"zteOnu excedeu timeout de {_timeout.TotalSeconds}s");
        }
    }

    private static ZteOnuResult ParseOutput(string stdout, string stderr)
    {
        var result = new ZteOnuResult();
        var combined = stdout + "\n" + stderr;

        // Detectar sucesso
        if (stdout.Contains("Permanent Telnet succeed") || 
            stdout.Contains("enter factory mode: ok") ||
            stdout.Contains("Telnet Credentials"))
        {
            result.Success = true;
        }

        // Extrair credenciais
        var userMatch = Regex.Match(stdout, @"User:\s*(\S+)", RegexOptions.IgnoreCase);
        var passMatch = Regex.Match(stdout, @"Pass:\s*(\S+)", RegexOptions.IgnoreCase);

        if (userMatch.Success) result.TelnetUser = userMatch.Groups[1].Value;
        if (passMatch.Success) result.TelnetPass = passMatch.Groups[1].Value;

        // Fallback para credenciais padrão
        if (string.IsNullOrEmpty(result.TelnetUser))
            result.TelnetUser = "root";
        if (string.IsNullOrEmpty(result.TelnetPass))
            result.TelnetPass = "Zte521";

        // Detectar erros conhecidos
        if (stdout.Contains("new algorithm") || stdout.Contains("re_rand"))
        {
            result.ErrorMessage = "Firmware nova detectada (pós-out/2024). Requer --new ou MAC spoofing.";
            result.RequiresNewAlgorithm = true;
        }
        else if (stdout.Contains("failed") || stdout.Contains("error") || !string.IsNullOrEmpty(stderr))
        {
            result.ErrorMessage = stderr;
            if (string.IsNullOrEmpty(result.ErrorMessage))
                result.ErrorMessage = "Falha não especificada no zteOnu";
        }

        return result;
    }
}

public sealed class ZteOnuResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string TelnetUser { get; set; } = "root";
    public string TelnetPass { get; set; } = "Zte521";
    public string ErrorMessage { get; set; } = string.Empty;
    public bool RequiresNewAlgorithm { get; set; }
}

public class ZteOnuException : Exception
{
    public ZteOnuException(string message) : base(message) { }
}
