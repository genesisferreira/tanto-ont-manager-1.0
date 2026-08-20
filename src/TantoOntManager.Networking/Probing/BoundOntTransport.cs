using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using TantoOntManager.DeviceAdapters.Abstractions;
using TantoOntManager.DeviceAdapters.Zte.Auth;
using TantoOntManager.Domain.Discovery;
using TantoOntManager.Domain.Common;
using TantoOntManager.Domain.Network;
using TantoOntManager.Security.Export;
using TantoOntManager.Security.Tls;

namespace TantoOntManager.Networking.Probing;

public sealed class BoundOntTransportFactory : IBoundOntTransportFactory
{
    private readonly ProbeSessionSettings _settings;
    private readonly ILogger<BoundOntTransport> _logger;
    private readonly HttpMessageHandler? _testHandler;

    public BoundOntTransportFactory(
        ProbeSessionSettings settings,
        ILogger<BoundOntTransport> logger,
        HttpMessageHandler? testHandler = null)
    {
        _settings = settings;
        _logger = logger;
        _testHandler = testHandler;
    }

    public IBoundOntTransport Create(OntEndpoint endpoint, string? pinnedCertificateSha256)
        => new BoundOntTransport(endpoint, pinnedCertificateSha256, _settings, _logger, _testHandler);
}

public sealed class BoundOntTransport : IBoundOntTransport
{
    private const int MaxRedirects = 5;
    private const int MaxConfigBinBytes = 8_388_608;
    private readonly OntEndpoint _endpoint;
    private readonly ProbeSessionSettings _settings;
    private readonly ILogger _logger;
    private readonly HttpMessageHandler? _testHandler;
    private readonly CookieContainer _cookies = new();
    private readonly List<string> _methods = [];
    private readonly List<string> _gets = [];
    private readonly List<string> _posts = [];
    private readonly HashSet<string> _discoveredTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _discoveredAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _provenExtras = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private HttpClient? _client;
    private int _httpClientInstanceId;
    private bool _disposed;

    public BoundOntTransport(
        OntEndpoint endpoint,
        string? pinnedCertificateSha256,
        ProbeSessionSettings settings,
        ILogger logger,
        HttpMessageHandler? testHandler = null)
    {
        _endpoint = endpoint;
        ObservedCertificateSha256 = pinnedCertificateSha256;
        _settings = settings;
        _logger = logger;
        _testHandler = testHandler;
        BoundAddress = endpoint.Address;
    }

    public IPAddress BoundAddress { get; }

    public string? ObservedCertificateSha256 { get; private set; }

    public bool HasSessionCookie
    {
        get
        {
            lock (_gate)
            {
                return _cookies.GetCookies(_endpoint.BaseUri)
                    .Cast<Cookie>()
                    .Any(item => item.Name.StartsWith(F6201BV9310P8N1AuthContract.SessionCookieNamePrefix, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public int PostCount => LoginPostCount + LogoutPostCount;

    public int LoginPostCount { get; private set; }

    public int LogoutPostCount { get; private set; }

    public int ConfigPostCount { get; private set; }

    public string? SessionToken { get; private set; }

    public string? LastCleanupReason { get; private set; }

    public string SessionId => _sessionId;

    public int HttpClientInstanceId => _httpClientInstanceId;

    public int CookieCount
    {
        get
        {
            lock (_gate)
            {
                return _cookies.GetCookies(_endpoint.BaseUri).Count;
            }
        }
    }

    public IReadOnlyList<string> HttpMethodsUsed
    {
        get
        {
            lock (_gate)
            {
                return _methods.ToList();
            }
        }
    }

    public IReadOnlyList<string> MaskedGetPages
    {
        get
        {
            lock (_gate)
            {
                return _gets.ToList();
            }
        }
    }

    public IReadOnlyList<string> MaskedPosts
    {
        get
        {
            lock (_gate)
            {
                return _posts.ToList();
            }
        }
    }

    public async Task<BoundHttpResult> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        if (!TryCreateUri(pathAndQuery, out var uri, out var error))
        {
            return BoundHttpResult.Fail(error!);
        }

        if (F6201BV9310P8N1AuthContract.IsAllowedJsAsset(uri, BoundAddress, _discoveredAssets))
        {
            return await SendAsync(HttpMethod.Get, uri, null, cancellationToken);
        }

        if (!F6201BV9310P8N1AuthContract.IsAllowedGet(
                uri,
                BoundAddress,
                _discoveredTags,
                ExtraIsProven))
        {
            _logger.LogWarning("GET recusado fora da allowlist: {Path}", F6201BV9310P8N1AuthContract.MaskUri(uri));
            return BoundHttpResult.Fail(Error.Create(
                ErrorCodes.GetNotAllowlisted,
                "GET autenticado recusado: o caminho não está na allowlist desta firmware."));
        }

        return await SendAsync(HttpMethod.Get, uri, null, cancellationToken);
    }

    public async Task<BoundHttpResult> PostLoginFormAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        if (!TryCreateUri(F6201BV9310P8N1AuthContract.LoginPathAndQuery, out var uri, out var error))
        {
            return BoundHttpResult.Fail(error!);
        }

        if (!F6201BV9310P8N1AuthContract.IsLoginPost(uri, BoundAddress))
        {
            return BoundHttpResult.Fail(Error.Create(
                ErrorCodes.PostNotAllowed,
                "POST recusado: somente o endpoint de login homologado pode receber POST."));
        }

        if (LoginPostCount >= 1)
        {
            return BoundHttpResult.Fail(Error.Create(
                ErrorCodes.PostNotAllowed,
                "POST recusado: o login já foi enviado nesta tentativa e não é repetido automaticamente."));
        }

        var content = new FormUrlEncodedContent(form);
        return await SendAsync(HttpMethod.Post, uri, content, cancellationToken);
    }

    public async Task<BoundHttpResult> PostLogoutFormAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        if (!TryCreateUri(F6201BV9310P8N1AuthContract.LogoutPathAndQuery, out var uri, out var error))
        {
            return BoundHttpResult.Fail(error!);
        }

        if (!F6201BV9310P8N1AuthContract.IsLogoutPost(uri, BoundAddress))
        {
            return BoundHttpResult.Fail(Error.Create(
                ErrorCodes.LogoutNotAllowlisted,
                "POST recusado: somente o endpoint de logout observado na interface pode ser usado."));
        }

        if (LogoutPostCount >= 1)
        {
            return BoundHttpResult.Fail(Error.Create(
                ErrorCodes.PostNotAllowed,
                "POST recusado: o logout já foi enviado nesta sessão."));
        }

        var content = new FormUrlEncodedContent(form);
        return await SendAsync(HttpMethod.Post, uri, content, cancellationToken);
    }

    public async Task<BoundHttpResult> PostAsync(
        string pathAndQuery,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (!TryCreateUri(pathAndQuery, out var uri, out var error))
        {
            return BoundHttpResult.Fail(error!);
        }

        return await SendAsync(HttpMethod.Post, uri, content, cancellationToken);
    }

    public async Task<BoundHttpResult> GetBinaryAsync(
        string pathAndQuery,
        CancellationToken cancellationToken)
    {
        if (!TryCreateUri(pathAndQuery, out var uri, out var error))
        {
            return BoundHttpResult.Fail(error!);
        }

        if (!IsConfigBinDownloadPath(uri))
        {
            _logger.LogWarning("GET binário recusado: {Path}", F6201BV9310P8N1AuthContract.MaskUri(uri));
            return BoundHttpResult.Fail(Error.Create(
                ErrorCodes.GetNotAllowlisted,
                "GET binário recusado: somente endpoints de backup/config.bin homologados."));
        }

        return await SendAsync(HttpMethod.Get, uri, null, cancellationToken, maxBodyBytes: MaxConfigBinBytes);
    }

    public void RememberSafeRead(string type, string tag)
    {
        if (!F6201BV9310P8N1AuthContract.IsAllowedGetType(type)
            || !F6201BV9310P8N1AuthContract.IsValidTag(tag)
            || F6201BV9310P8N1AuthContract.IsDestructiveTag(tag)
            || F6201BV9310P8N1AuthContract.IsAuthControlTag(tag))
        {
            return;
        }

        _discoveredTags.Add(F6201BV9310P8N1AuthContract.MakeKey(type, tag));
    }

    public void RememberProvenQueryParameters(string type, string tag, IReadOnlyDictionary<string, string> extras)
    {
        RememberSafeRead(type, tag);
        if (extras is null || extras.Count == 0)
        {
            return;
        }

        var key = F6201BV9310P8N1AuthContract.MakeKey(type, tag);
        if (!_provenExtras.TryGetValue(key, out var map))
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _provenExtras[key] = map;
        }

        foreach (var pair in extras)
        {
            if (F6201BProvenQueryParameter.TryCreate(pair.Key, pair.Value, out var name, out var value))
            {
                map[name] = value;
            }
        }
    }

    private bool ExtraIsProven(string typeAndTag, string name, string value)
        => _provenExtras.TryGetValue(typeAndTag, out var map)
           && map.TryGetValue(name, out var expected)
           && expected.Equals(value, StringComparison.Ordinal);

    public void RememberReferencedAsset(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || _discoveredAssets.Count >= F6201BV9310P8N1AuthContract.MaxJsAssets)
        {
            return;
        }

        if (!F6201BScriptReference.TryNormalize(relativePath, BoundAddress, _endpoint.BaseUri, out var path))
        {
            return;
        }

        _discoveredAssets.Add(path);
    }

    public void RememberDiscoveredTags(string html)
    {
        foreach (var item in F6201BSafeReadDiscovery.Discover(html))
        {
            if (item.Classification != SafeReadClassification.SafeRead)
            {
                continue;
            }

            var parts = item.TypeAndTag.Split(':');
            if (parts.Length != 2)
            {
                continue;
            }

            RememberProvenQueryParameters(parts[0], parts[1], item.ExtraParameters);
        }
    }

    public void ClearCookiesAndState(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "unspecified";
        }

        lock (_gate)
        {
            foreach (Cookie cookie in _cookies.GetCookies(_endpoint.BaseUri))
            {
                cookie.Expired = true;
            }

            SessionToken = null;
        }

        LastCleanupReason = reason;
        _logger.LogInformation(
            "Sessão limpa sessionId={SessionId} motivo={Reason} cookiesRestantes={Cookies}",
            _sessionId,
            reason,
            CookieCount);
    }

    public IReadOnlyList<IsolatedObserverCookie> CopyCookiesForIsolatedObserver()
    {
        lock (_gate)
        {
            return _cookies.GetCookies(_endpoint.BaseUri)
                .Cast<Cookie>()
                .Where(item => !item.Expired)
                .Select(item => new IsolatedObserverCookie(
                    item.Name,
                    item.Value,
                    item.Domain,
                    item.Path,
                    item.Secure,
                    item.HttpOnly))
                .ToList();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client?.Dispose();
        _client = null;
        if (LastCleanupReason is null)
        {
            ClearCookiesAndState("dispose");
        }
    }

    public void DiscoverFrom(string html) => RememberDiscoveredTags(html);

    private async Task<BoundHttpResult> SendAsync(
        HttpMethod method,
        Uri start,
        HttpContent? content,
        CancellationToken cancellationToken,
        int? maxBodyBytes = null)
    {
        var watch = Stopwatch.StartNew();
        var current = start;
        var redirects = 0;
        var client = GetOrCreateClient();
        var cookiesBefore = DescribeCookiesDetailed();
        var bodyLimit = maxBodyBytes ?? PublicHttpFetcher.MaxBodyBytes;

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(method, current);
                if (content is not null && hop == 0)
                {
                    request.Content = content;
                }

                ApplyHomologatedAjaxHeaders(request, current);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var rawBody = await ReadBodyBytesAsync(response, cancellationToken, bodyLimit);
                var body = Encoding.UTF8.GetString(rawBody);
                var status = (int)response.StatusCode;

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!next.Host.Equals(BoundAddress.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        Record(method, current, isPost: method == HttpMethod.Post);
                        return BoundHttpResult.Fail(
                            Error.Create(
                                ErrorCodes.RedirectForeignHost,
                                "Redirect para outro host foi recusado."),
                            status,
                            watch.Elapsed,
                            redirects + 1);
                    }

                    if (method == HttpMethod.Post)
                    {
                        var isAuthPost = F6201BV9310P8N1AuthContract.IsLoginPost(current, BoundAddress)
                                         || F6201BV9310P8N1AuthContract.IsLogoutPost(current, BoundAddress);
                        if (isAuthPost)
                        {
                            Record(method, current, isPost: true);
                            return BoundHttpResult.Fail(
                                Error.Create(
                                    ErrorCodes.UnexpectedRedirect,
                                    "Redirect inesperado após o POST de login."),
                                status,
                                watch.Elapsed,
                                redirects + 1);
                        }

                        Record(method, current, isPost: true);
                        ConfigPostCount++;
                        return new BoundHttpResult(
                            true,
                            status,
                            body,
                            response.Content.Headers.ContentType?.MediaType,
                            current.ToString(),
                            redirects + 1,
                            AuthenticatedPayloadSanitizer.Sha256Short(body),
                            watch.Elapsed,
                            null)
                        {
                            RawBody = rawBody
                        };
                    }

                    if (!F6201BV9310P8N1AuthContract.IsAllowedGet(next, BoundAddress, _discoveredTags, ExtraIsProven)
                        && !IsConfigBinDownloadPath(next))
                    {
                        Record(method, current, isPost: false);
                        return BoundHttpResult.Fail(
                            Error.Create(
                                ErrorCodes.UnexpectedRedirect,
                                "Redirect para caminho não homologado foi recusado."),
                            status,
                            watch.Elapsed,
                            redirects + 1);
                    }

                    current = next;
                    redirects++;
                    method = HttpMethod.Get;
                    content = null;
                    continue;
                }

                Record(method, current, isPost: method == HttpMethod.Post);
                if (method == HttpMethod.Post)
                {
                    if (F6201BV9310P8N1AuthContract.IsLoginPost(current, BoundAddress))
                    {
                        LoginPostCount++;
                    }
                    else if (F6201BV9310P8N1AuthContract.IsLogoutPost(current, BoundAddress))
                    {
                        LogoutPostCount++;
                    }
                    else
                    {
                        ConfigPostCount++;
                    }
                }

                RememberDiscoveredTags(body);
                CaptureSessionToken(body);
                var hash = AuthenticatedPayloadSanitizer.Sha256Short(body);
                var kind = F6201BHtmlText.Classify(body);
                var cookiesAfter = DescribeCookiesDetailed();
                _logger.LogInformation(
                    "HTTP autenticado sessionId={SessionId} client={Client} method={Method} path={Path} status={Status} redirects={Redirects} hash={Hash} posts={Posts} cookiesAntes={CookiesBefore} cookiesDepois={CookiesAfter} sidPresente={Sid} sidSecure={Secure} sidHttpOnly={HttpOnly} sidPath={PathCookie} tokenPresente={Token} tokenLen={TokenLen} tokenNoQuery={TokenQuery} ajaxHeaders={Ajax} classificacao={Kind} marcador={Marker}",
                    _sessionId,
                    _httpClientInstanceId,
                    method.Method,
                    F6201BV9310P8N1AuthContract.MaskUri(current),
                    status,
                    redirects,
                    hash,
                    PostCount,
                    cookiesBefore.Summary,
                    cookiesAfter.Summary,
                    cookiesAfter.HasSid,
                    cookiesAfter.Secure,
                    cookiesAfter.HttpOnly,
                    cookiesAfter.Path,
                    !string.IsNullOrEmpty(SessionToken),
                    SessionToken?.Length ?? 0,
                    F6201BV9310P8N1AuthContract.ParseQuery(current.Query).ContainsKey("_sessionTOKEN"),
                    DescribeAjaxHeaders(request),
                    kind,
                    F6201BHtmlText.SanitizedMarker(body));

                return new BoundHttpResult(
                    true,
                    status,
                    body,
                    response.Content.Headers.ContentType?.MediaType,
                    current.ToString(),
                    redirects,
                    hash,
                    watch.Elapsed,
                    null)
                {
                    RawBody = rawBody
                };
            }

            return BoundHttpResult.Fail(
                Error.Create(ErrorCodes.UnexpectedRedirect, "Limite de redirects excedido."),
                0,
                watch.Elapsed,
                redirects);
        }
        catch (OperationCanceledException)
        {
            return BoundHttpResult.Fail(
                Error.Create(ErrorCodes.ProbeTimeout, "A requisição autenticada excedeu o tempo limite."),
                0,
                watch.Elapsed);
        }
        catch (AuthenticationException)
        {
            return BoundHttpResult.Fail(
                Error.Create(ErrorCodes.CertificateChanged, "O certificado TLS mudou ou foi rejeitado durante a sessão."),
                0,
                watch.Elapsed);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("certificado", StringComparison.OrdinalIgnoreCase)
                                              || ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                                              || ex.Message.Contains("fingerprint", StringComparison.OrdinalIgnoreCase))
        {
            return BoundHttpResult.Fail(
                Error.Create(ErrorCodes.CertificateChanged, "O certificado TLS observado não coincide com a sessão."),
                0,
                watch.Elapsed);
        }
        catch (HttpRequestException)
        {
            return BoundHttpResult.Fail(
                Error.Create(ErrorCodes.HttpProbeFailed, "Falha HTTP controlada na sessão autenticada."),
                0,
                watch.Elapsed);
        }
    }

    private void ApplyHomologatedAjaxHeaders(HttpRequestMessage request, Uri uri)
    {
        var query = F6201BV9310P8N1AuthContract.ParseQuery(uri.Query);
        if (!query.TryGetValue("_type", out var type)
            || !F6201BV9310P8N1AuthContract.IsAllowedGetType(type))
        {
            return;
        }

        if (!request.Headers.Contains(F6201BV9310P8N1HomologatedReadContract.JqueryAjaxHeaderName))
        {
            request.Headers.TryAddWithoutValidation(
                F6201BV9310P8N1HomologatedReadContract.JqueryAjaxHeaderName,
                F6201BV9310P8N1HomologatedReadContract.JqueryAjaxHeaderValue);
        }

        request.Headers.Referrer ??= _endpoint.BaseUri;
    }

    private static string DescribeAjaxHeaders(HttpRequestMessage request)
    {
        var names = new List<string>();
        if (request.Headers.Contains(F6201BV9310P8N1HomologatedReadContract.JqueryAjaxHeaderName))
        {
            names.Add(F6201BV9310P8N1HomologatedReadContract.JqueryAjaxHeaderName);
        }

        if (request.Headers.Referrer is not null)
        {
            names.Add("Referer");
        }

        return names.Count == 0 ? "none" : string.Join(";", names);
    }

    private HttpClient GetOrCreateClient()
    {
        if (_client is not null)
        {
            return _client;
        }

        lock (_gate)
        {
            if (_client is not null)
            {
                return _client;
            }

            HttpMessageHandler pipeline = _testHandler is not null
                ? new CookieAwareHandler(_cookies, _testHandler)
                : CreateHandler();
            _client = new HttpClient(pipeline, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("TantoOntManager/0.1.6 (lab-readonly)");
            _client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
            _httpClientInstanceId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_client);
            return _client;
        }
    }

    private CookieLog DescribeCookiesDetailed()
    {
        lock (_gate)
        {
            var cookies = _cookies.GetCookies(_endpoint.BaseUri).Cast<Cookie>().ToList();
            var sid = cookies.FirstOrDefault(item =>
                item.Name.StartsWith(F6201BV9310P8N1AuthContract.SessionCookieNamePrefix, StringComparison.OrdinalIgnoreCase));
            return new CookieLog(
                cookies.Count + ":" + string.Join(",", cookies.Select(item => item.Name)),
                sid is not null,
                sid?.Secure,
                sid?.HttpOnly,
                sid?.Path);
        }
    }

    private sealed record CookieLog(string Summary, bool HasSid, bool? Secure, bool? HttpOnly, string? Path);

    private HttpMessageHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = _cookies,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    var accepted = LocalEndpointCertificatePolicy.Validate(
                        _settings.Trust,
                        BoundAddress,
                        errors,
                        cert);
                    if (!accepted || cert is null)
                    {
                        return false;
                    }

                    var fingerprint = Convert.ToHexString(SHA256.HashData(new X509Certificate2(cert).RawData)).ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(ObservedCertificateSha256))
                    {
                        ObservedCertificateSha256 = fingerprint;
                        return true;
                    }

                    if (!ObservedCertificateSha256.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AuthenticationException("fingerprint-mismatch");
                    }

                    return true;
                }
            },
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!IPAddress.TryParse(context.DnsEndPoint.Host, out var host)
                    || !host.Equals(BoundAddress))
                {
                    throw new InvalidOperationException("A sessão autenticada não pode ser reutilizada em outro IP.");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    private void Record(HttpMethod method, Uri uri, bool isPost)
    {
        var masked = F6201BV9310P8N1AuthContract.MaskUri(uri);
        lock (_gate)
        {
            _methods.Add(method.Method);
            if (isPost)
            {
                _posts.Add(masked);
            }
            else
            {
                _gets.Add(masked);
            }
        }
    }

    private bool TryCreateUri(string pathAndQuery, out Uri uri, out Error? error)
    {
        uri = null!;
        error = null;
        if (!Uri.TryCreate(_endpoint.BaseUri, pathAndQuery, out uri!))
        {
            error = Error.Create(ErrorCodes.GetNotAllowlisted, "Caminho HTTP inválido.");
            return false;
        }

        if (!uri.Host.Equals(BoundAddress.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            error = Error.Create(ErrorCodes.RedirectForeignHost, "A sessão autenticada não pode ser reutilizada em outro IP.");
            return false;
        }

        return true;
    }

    private void CaptureSessionToken(string body)
    {
        var token = F6201BHtmlText.ReadSessionToken(body);
        if (!string.IsNullOrWhiteSpace(token))
        {
            SessionToken = token;
        }
    }

    private static async Task<byte[]> ReadBodyBytesAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        int maxBodyBytes)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > maxBodyBytes)
        {
            bytes = bytes[..maxBodyBytes];
        }

        return bytes;
    }

    private static bool IsConfigBinDownloadPath(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        return path.Equals("/cgi-bin/backup.cgi", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/config.bin", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/cgi-bin/download.cgi", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class CookieAwareHandler : DelegatingHandler
{
    private readonly CookieContainer _cookies;

    public CookieAwareHandler(CookieContainer cookies, HttpMessageHandler inner)
        : base(inner)
    {
        _cookies = cookies;
    }

    protected override void Dispose(bool disposing)
    {
        _ = disposing;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri)
        {
            var header = _cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.Remove("Cookie");
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (request.RequestUri is { } responseUri
            && response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                try
                {
                    _cookies.SetCookies(responseUri, cookie);
                }
                catch (CookieException)
                {
                }
            }
        }

        return response;
    }
}
