using System.Text;
using Microsoft.Extensions.Logging;

namespace TantoOntManager.Infrastructure.Unlock;

/// <summary>
/// Cliente Telnet para enviar comandos de desbloqueio diretamente na ONT
/// após o zteOnu ter habilitado o acesso.
/// </summary>
public interface ITelnetCommandClient
{
    /// <summary>
    /// Conecta e autentica no Telnet da ONT.
    /// </summary>
    Task<bool> ConnectAsync(string ip, string username, string password, int port = 23, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executa uma sequência de comandos e retorna as respostas.
    /// </summary>
    Task<IReadOnlyList<TelnetCommandResult>> ExecuteCommandsAsync(
        IEnumerable<string> commands,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executa o script padrão de desbloqueio (superadmin + disable TR-069).
    /// </summary>
    Task<TelnetUnlockResult> ExecuteUnlockScriptAsync(CancellationToken cancellationToken = default);

    void Disconnect();
}

public sealed class ZteTelnetCommandClient : ITelnetCommandClient, IDisposable
{
    private readonly ILogger<ZteTelnetCommandClient> _logger;
    private System.Net.Sockets.TcpClient? _client;
    private System.IO.StreamReader? _reader;
    private System.IO.StreamWriter? _writer;
    private readonly TimeSpan _commandTimeout;
    private readonly StringBuilder _sessionLog;

    public ZteTelnetCommandClient(
        ILogger<ZteTelnetCommandClient> logger,
        TimeSpan? commandTimeout = null)
    {
        _logger = logger;
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(10);
        _sessionLog = new StringBuilder();
    }

    public async Task<bool> ConnectAsync(
        string ip, string username, string password, 
        int port = 23, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Conectando Telnet {Ip}:{Port}...", ip, port);

            _client = new System.Net.Sockets.TcpClient();
            await _client.ConnectAsync(ip, port);

            var stream = _client.GetStream();
            _reader = new System.IO.StreamReader(stream, Encoding.ASCII);
            _writer = new System.IO.StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

            // Aguardar prompt de login
            var banner = await ReadUntilAsync(new[] { "login:", "Login:", "Username:" }, cancellationToken);
            _sessionLog.AppendLine(banner);

            if (string.IsNullOrEmpty(banner))
            {
                _logger.LogError("Não recebeu prompt de login do Telnet");
                return false;
            }

            // Enviar username
            await SendLineAsync(username);
            var userPrompt = await ReadUntilAsync(new[] { "Password:", "password:" }, cancellationToken);
            _sessionLog.AppendLine(userPrompt);

            // Enviar password
            await SendLineAsync(password);
            var authResult = await ReadUntilAsync(new[] { ">", "#", "$", "incorrect", "failed" }, cancellationToken);
            _sessionLog.AppendLine(authResult);

            var success = authResult.Contains(">") || authResult.Contains("#") || authResult.Contains("$");

            if (success)
            {
                _logger.LogInformation("✅ Telnet autenticado como {User}", username);
            }
            else
            {
                _logger.LogError("❌ Falha na autenticação Telnet");
                Disconnect();
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exceção ao conectar Telnet");
            Disconnect();
            return false;
        }
    }

    public async Task<IReadOnlyList<TelnetCommandResult>> ExecuteCommandsAsync(
        IEnumerable<string> commands,
        CancellationToken cancellationToken = default)
    {
        var results = new List<TelnetCommandResult>();

        foreach (var cmd in commands)
        {
            if (_client?.Connected != true)
            {
                results.Add(new TelnetCommandResult(cmd, "", false, "Não conectado"));
                continue;
            }

            _logger.LogDebug("Telnet >> {Command}", cmd);
            await SendLineAsync(cmd);

            var response = await ReadUntilAsync(new[] { ">", "#", "$", "OK", "error" }, cancellationToken);
            _sessionLog.AppendLine($">> {cmd}");
            _sessionLog.AppendLine(response);

            var success = !response.Contains("error", StringComparison.OrdinalIgnoreCase)
                       && !response.Contains("failed", StringComparison.OrdinalIgnoreCase);

            results.Add(new TelnetCommandResult(cmd, response, success, success ? null : "Erro na resposta"));

            _logger.LogInformation(
                "Telnet {Status}: {Command}",
                success ? "OK" : "FALHA", cmd);

            // Pequena pausa entre comandos
            await Task.Delay(500, cancellationToken);
        }

        return results;
    }

    public async Task<TelnetUnlockResult> ExecuteUnlockScriptAsync(CancellationToken cancellationToken = default)
    {
        var script = new List<string>
        {
            // 1. Criar superusuário persistente
            "sendcmd 1 DB set DevAuthInfo 5 Enable 1",
            "sendcmd 1 DB set DevAuthInfo 5 User superadmin",
            "sendcmd 1 DB set DevAuthInfo 5 Pass superadmin",
            "sendcmd 1 DB set DevAuthInfo 5 Level 1",
            "sendcmd 1 DB set DevAuthInfo 5 AppID 1",
            "sendcmd 1 DB saveasy",

            // 2. Desabilitar TR-069
            "sendcmd 1 DB set MgtServer 0 Enable 0",
            "sendcmd 1 DB set MgtServer 0 URL http://0.0.0.0",
            "sendcmd 1 DB set MgtServer 0 PeriodicInformEnable 0",
            "sendcmd 1 DB saveasy",

            // 3. Ativar Telnet permanente (se ainda não estiver)
            "sendcmd 1 DB set TelnetCfg 0 Enable 1",
            "sendcmd 1 DB set TelnetCfg 0 Lan_Enable 1",
            "sendcmd 1 DB set TelnetCfg 0 Wan_Enable 0", // Segurança: só LAN
            "sendcmd 1 DB saveasy"
        };

        _logger.LogWarning("Executando script de desbloqueio via Telnet...");
        var results = await ExecuteCommandsAsync(script, cancellationToken);

        var allSuccess = results.All(r => r.Success);
        var log = string.Join("\n", results.Select(r => $"{r.Command} => {(r.Success ? "OK" : "FALHO")}"));

        if (allSuccess)
        {
            _logger.LogInformation("✅ Script de desbloqueio Telnet concluído com sucesso");
        }
        else
        {
            _logger.LogError("❌ Alguns comandos do script falharam: {Log}", log);
        }

        return new TelnetUnlockResult
        {
            Success = allSuccess,
            CommandResults = results,
            Summary = log
        };
    }

    public void Disconnect()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
        _writer = null;
        _reader = null;
        _client = null;
        _logger.LogInformation("Telnet desconectado");
    }

    public void Dispose()
    {
        Disconnect();
    }

    private async Task<string> ReadUntilAsync(string[] terminators, CancellationToken cancellationToken)
    {
        if (_reader == null) return string.Empty;

        var sb = new StringBuilder();
        var buffer = new char[1024];
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < _commandTimeout)
        {
            if (_client?.Available > 0)
            {
                var read = await _reader.ReadAsync(buffer, 0, Math.Min(buffer.Length, _client.Available));
                sb.Append(buffer, 0, read);

                var text = sb.ToString();
                if (terminators.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    return text;
            }

            await Task.Delay(100, cancellationToken);
        }

        return sb.ToString();
    }

    private async Task SendLineAsync(string text)
    {
        if (_writer != null)
        {
            await _writer.WriteLineAsync(text);
            await _writer.FlushAsync();
        }
    }
}

public sealed record TelnetCommandResult(
    string Command,
    string Response,
    bool Success,
    string? Error);

public sealed class TelnetUnlockResult
{
    public bool Success { get; set; }
    public IReadOnlyList<TelnetCommandResult> CommandResults { get; set; } = Array.Empty<TelnetCommandResult>();
    public string Summary { get; set; } = string.Empty;
}
