using System.Net;
using TantoOntManager.Domain.Common;
using TantoOntManager.Domain.Network;

namespace TantoOntManager.DeviceAdapters.Abstractions;

public sealed record BoundHttpResult(
    bool Succeeded,
    int StatusCode,
    string Body,
    string? ContentType,
    string FinalUri,
    int RedirectCount,
    string SanitizedBodySha256,
    TimeSpan Duration,
    Error? Error)
{
    public byte[]? RawBody { get; init; }

    public static BoundHttpResult Fail(Error error, int statusCode = 0, TimeSpan duration = default, int redirectCount = 0)
        => new(false, statusCode, string.Empty, null, string.Empty, redirectCount, string.Empty, duration, error);
}

public interface IBoundOntTransport : IDisposable
{
    IPAddress BoundAddress { get; }

    string? ObservedCertificateSha256 { get; }

    bool HasSessionCookie { get; }

    int PostCount { get; }

    int LoginPostCount { get; }

    int LogoutPostCount { get; }

    int ConfigPostCount { get; }

    string? SessionToken { get; }

    string SessionId { get; }

    int HttpClientInstanceId { get; }

    int CookieCount { get; }

    IReadOnlyList<string> HttpMethodsUsed { get; }

    IReadOnlyList<string> MaskedGetPages { get; }

    IReadOnlyList<string> MaskedPosts { get; }

    Task<BoundHttpResult> GetAsync(string pathAndQuery, CancellationToken cancellationToken);

    Task<BoundHttpResult> PostLoginFormAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken);

    Task<BoundHttpResult> PostLogoutFormAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken);

    Task<BoundHttpResult> PostAsync(
        string pathAndQuery,
        HttpContent content,
        CancellationToken cancellationToken)
        => Task.FromResult(BoundHttpResult.Fail(Error.Create(
            ErrorCodes.PostNotAllowed,
            "POST genérico não suportado neste transporte.")));

    Task<BoundHttpResult> GetBinaryAsync(
        string pathAndQuery,
        CancellationToken cancellationToken)
        => Task.FromResult(BoundHttpResult.Fail(Error.Create(
            ErrorCodes.GetNotAllowlisted,
            "Download binário não suportado neste transporte.")));

    void RememberSafeRead(string type, string tag);

    void RememberProvenQueryParameters(string type, string tag, IReadOnlyDictionary<string, string> extras)
    {
        RememberSafeRead(type, tag);
    }

    void RememberReferencedAsset(string relativePath);

    void ClearCookiesAndState(string reason);

    IReadOnlyList<IsolatedObserverCookie> CopyCookiesForIsolatedObserver()
        => [];
}

public sealed class IsolatedObserverCookie
{
    private readonly string _value;

    public IsolatedObserverCookie(string name, string value, string domain, string path, bool secure, bool httpOnly)
    {
        Name = name;
        _value = value ?? string.Empty;
        Domain = domain;
        Path = string.IsNullOrWhiteSpace(path) ? "/" : path;
        Secure = secure;
        HttpOnly = httpOnly;
    }

    public string Name { get; }

    public string Domain { get; }

    public string Path { get; }

    public bool Secure { get; }

    public bool HttpOnly { get; }

    public string RevealValueForIsolatedWebView() => _value;

    public override string ToString() => $"{Name}=[redacted]; Domain={Domain}; Path={Path}";
}

public interface IBoundOntTransportFactory
{
    IBoundOntTransport Create(OntEndpoint endpoint, string? pinnedCertificateSha256);
}
