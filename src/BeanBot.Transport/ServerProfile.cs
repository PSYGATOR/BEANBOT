using System.Net;
using Microsoft.Extensions.Logging;

namespace BeanBot.Transport;

public enum ServerProfileMode
{
    Local,
    Private,
    Live,
}

/// <summary>
/// Environment configuration separating Local Impostor CI, Private servers, and Live Innersloth servers
/// without forking protocol or behavior code paths.
/// </summary>
public sealed record ServerProfile(
    ServerProfileMode Mode,
    string Region,
    string MatchmakerBaseUrl,
    bool UseDtls,
    bool UseDeviceAuth,
    int DefaultGamePort = 22023,
    string CertsDirectory = "certs",
    bool AcceptNewPin = false)
{
    public static ServerProfile Local(string host = "127.0.0.1", int port = 22023) =>
        new(
            Mode: ServerProfileMode.Local,
            Region: "local",
            MatchmakerBaseUrl: $"http://{host}:{port}",
            UseDtls: false,
            UseDeviceAuth: false,
            DefaultGamePort: port);

    public static ServerProfile Private(
        string matchmakerBaseUrl,
        bool useDtls = false,
        bool useDeviceAuth = false,
        string region = "private",
        int defaultGamePort = 22023,
        string certsDirectory = "certs",
        bool acceptNewPin = false) =>
        new(
            Mode: ServerProfileMode.Private,
            Region: region,
            MatchmakerBaseUrl: matchmakerBaseUrl.TrimEnd('/'),
            UseDtls: useDtls,
            UseDeviceAuth: useDeviceAuth,
            DefaultGamePort: defaultGamePort,
            CertsDirectory: certsDirectory,
            AcceptNewPin: acceptNewPin);

    public static readonly string[] LiveRegions = ["na", "eu", "as"];

    public static ServerProfile Live(
        string region = "na",
        string certsDirectory = "certs",
        bool acceptNewPin = false)
    {
        var normalizedRegion = region.Trim().ToLowerInvariant();
        var host = normalizedRegion switch
        {
            "na" => "https://matchmaker.among.us",
            "eu" => "https://matchmaker-eu.among.us",
            "as" => "https://matchmaker-as.among.us",
            _ => throw new ArgumentException($"Unknown live region '{region}'. Expected na, eu, or as.", nameof(region)),
        };

        return new ServerProfile(
            Mode: ServerProfileMode.Live,
            Region: normalizedRegion,
            MatchmakerBaseUrl: host,
            UseDtls: true,
            UseDeviceAuth: true,
            DefaultGamePort: 22023,
            CertsDirectory: certsDirectory,
            AcceptNewPin: acceptNewPin);
    }

    /// <summary>
    /// True when connecting to a two-stage Innersloth relay (custom Hazel DTLS Auth on port+2 for uint32 nonce,
    /// followed by plain Hazel UDP on relayPort).
    /// </summary>
    public bool RequiresRelayDtlsAuth => Mode == ServerProfileMode.Live || (UseDtls && UseDeviceAuth);

    public ITransport CreateTransport(ILoggerFactory? loggerFactory = null)
    {
        if (!UseDtls)
        {
            return new UdpTransport();
        }

        var pinStore = new TofuCertificatePinStore(
            CertsDirectory,
            loggerFactory?.CreateLogger<TofuCertificatePinStore>());
        return new DtlsSession(
            Region,
            pinStore,
            AcceptNewPin,
            loggerFactory?.CreateLogger<DtlsSession>());
    }

    public ITransport CreateRelayGameTransport(ILoggerFactory? loggerFactory = null) =>
        RequiresRelayDtlsAuth ? new UdpTransport() : CreateTransport(loggerFactory);

    public HazelDtlsAuthClient CreateHazelDtlsAuthClient(ILoggerFactory? loggerFactory = null)
    {
        var pinStore = new TofuCertificatePinStore(
            CertsDirectory,
            loggerFactory?.CreateLogger<TofuCertificatePinStore>());
        return new HazelDtlsAuthClient(
            Region,
            pinStore,
            AcceptNewPin,
            loggerFactory?.CreateLogger<HazelDtlsAuthClient>());
    }
}
