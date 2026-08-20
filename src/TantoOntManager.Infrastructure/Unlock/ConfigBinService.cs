using Microsoft.Extensions.Logging;
using TantoOntManager.DeviceAdapters.Abstractions;
using TantoOntManager.Domain.Profiles;

namespace TantoOntManager.Infrastructure.Unlock;

/// <summary>
/// Serviço responsável por baixar e enviar o config.bin da ONT
/// usando o transporte autenticado existente.
/// </summary>
public interface IConfigBinService
{
    /// <summary>
    /// Baixa o config.bin da ONT autenticada.
    /// </summary>
    Task<byte[]> DownloadAsync(
        IBoundOntTransport transport,
        ZteProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Envia o config.bin modificado para a ONT.
    /// </summary>
    Task<bool> UploadAsync(
        IBoundOntTransport transport,
        ZteProfile profile,
        byte[] configBinData,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementação usando o transporte HTTP já autenticado.
/// </summary>
public sealed class ZteConfigBinService : IConfigBinService
{
    private readonly ILogger<ZteConfigBinService> _logger;

    public ZteConfigBinService(ILogger<ZteConfigBinService> logger)
    {
        _logger = logger;
    }

    public async Task<byte[]> DownloadAsync(
        IBoundOntTransport transport,
        ZteProfile profile,
        CancellationToken cancellationToken = default)
    {
        var endpoint = profile.DownloadConfigEndpoint;
        if (!string.IsNullOrEmpty(profile.ConfigBinQueryParameter))
        {
            endpoint += (endpoint.Contains("?") ? "&" : "?") + profile.ConfigBinQueryParameter;
        }

        _logger.LogInformation(
            "Iniciando download do config.bin de {Endpoint} para {Model}",
            endpoint, profile.Model);

        var result = await transport.GetBinaryAsync(endpoint, cancellationToken);

        if (!result.Succeeded)
        {
            _logger.LogError(
                "Falha no download do config.bin: {StatusCode} | {Error}",
                result.StatusCode, result.Error?.Message);
            throw new InvalidOperationException($"Download falhou: {result.StatusCode}");
        }

        var data = result.RawBody ?? Array.Empty<byte>();

        if (data.Length < 1024)
        {
            _logger.LogError(
                "Download retornou arquivo muito pequeno ({Size} bytes). Possivelmente página de erro HTML.",
                data.Length);
            throw new InvalidOperationException("Arquivo config.bin parece inválido (muito pequeno).");
        }

        _logger.LogInformation(
            "Config.bin baixado com sucesso: {Size} bytes | Hash: {Hash}",
            data.Length, ComputeShortHash(data));

        return data;
    }

    public async Task<bool> UploadAsync(
        IBoundOntTransport transport,
        ZteProfile profile,
        byte[] configBinData,
        CancellationToken cancellationToken = default)
    {
        var endpoint = profile.UploadConfigEndpoint;

        _logger.LogInformation(
            "Iniciando upload do config.bin para {Endpoint} | {Size} bytes",
            endpoint, configBinData.Length);

        // Construir multipart/form-data
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(configBinData);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "config.bin");

        var result = await transport.PostAsync(endpoint, content, cancellationToken);

        if (!result.Succeeded)
        {
            _logger.LogError(
                "Falha no upload do config.bin: {StatusCode} | Corpo: {BodyPreview}",
                result.StatusCode, 
                result.Body?.Length > 200 ? result.Body[..200] : result.Body);
            return false;
        }

        // Verificar resposta de sucesso
        var isSuccess = result.Body?.Contains("success", StringComparison.OrdinalIgnoreCase) == true
                     || result.Body?.Contains("ok", StringComparison.OrdinalIgnoreCase) == true
                     || result.StatusCode == 200;

        if (isSuccess)
        {
            _logger.LogInformation("Upload do config.bin concluído com sucesso.");
        }
        else
        {
            _logger.LogWarning(
                "Upload retornou status 200 mas corpo não indica sucesso: {BodyPreview}",
                result.Body?.Length > 200 ? result.Body[..200] : result.Body);
        }

        return isSuccess;
    }

    private static string ComputeShortHash(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(data);
        return Convert.ToHexString(hash)[..16];
    }
}
