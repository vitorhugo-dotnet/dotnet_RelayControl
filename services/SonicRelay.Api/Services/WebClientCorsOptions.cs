namespace SonicRelay.Api.Services;

/// <summary>
/// Browser allowlist for the Flutter web viewer. Bound once at startup from the
/// "Cors" section / CORS__* environment variables.
/// </summary>
/// <remarks>
/// The viewer ships as a web build served from its own origin, so every call it
/// makes here is cross-origin: the browser sends a preflight
/// <c>OPTIONS /api/devices/bootstrap</c> before the real POST, and without a
/// policy that preflight only ever hit routing's method fallback — 405, no
/// <c>Access-Control-Allow-Origin</c> — leaving the viewer stuck on device setup.
/// Native clients are unaffected either way; CORS is a browser rule.
/// </remarks>
public sealed class WebClientCorsOptions
{
    public const string SectionName = "Cors";

    /// <summary>
    /// The published viewer origin, used when nothing is configured so a stock
    /// deployment serves the real web client without extra .env work.
    /// </summary>
    public static readonly string[] DefaultAllowedOrigins = ["https://sonicrelay.hugodotnet.dev"];

    /// <summary>
    /// Exact origins to allow, replacing (not extending) <see cref="DefaultAllowedOrigins"/>.
    /// Configure a fork's own viewer here, e.g. <c>Cors__AllowedOrigins__0=https://viewer.example</c>.
    /// </summary>
    public string[]? AllowedOrigins { get; set; }

    /// <summary>
    /// Whether to also allow <c>http://localhost:*</c> / <c>http://127.0.0.1:*</c>.
    /// <c>flutter run -d chrome</c> picks a fresh port every launch, so a fixed
    /// allowlist cannot cover local development. Left unset it is on everywhere
    /// except Production, where a loopback origin belongs to the visitor's own
    /// machine and never to this deployment.
    /// </summary>
    public bool? AllowLoopbackOrigins { get; set; }

    /// <summary>
    /// How long a browser may cache a preflight result. One hour keeps the extra
    /// round trip off every request without outliving an allowlist change by much.
    /// </summary>
    public int PreflightMaxAgeSeconds { get; set; } = 3600;

    /// <summary>
    /// The configured origins normalized the way a browser sends <c>Origin</c>:
    /// trimmed, without the trailing slash a hand-written base URL usually carries.
    /// Falls back to <see cref="DefaultAllowedOrigins"/> when nothing usable is
    /// configured — a Compose file forwarding an unset variable hands this an empty
    /// string, and silently allowing no origin at all would take the web viewer down.
    /// </summary>
    public IReadOnlyList<string> EffectiveAllowedOrigins
    {
        get
        {
            var configured = (AllowedOrigins ?? [])
                .Select(origin => origin?.Trim().TrimEnd('/') ?? string.Empty)
                .Where(origin => origin.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return configured.Length > 0 ? configured : DefaultAllowedOrigins;
        }
    }

    public bool ResolveAllowLoopbackOrigins(bool isProduction) => AllowLoopbackOrigins ?? !isProduction;

    /// <summary>
    /// True for <c>http://localhost:1234</c> and <c>http://127.0.0.1:1234</c> (any port,
    /// including none), false for everything else — a lookalike host such as
    /// <c>https://localhost.attacker.example</c> included.
    /// </summary>
    public static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            // An IPv6 origin arrives bracketed ("http://[::1]:8000"); IPAddress.TryParse wants it bare.
            || (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
                && System.Net.IPAddress.IsLoopback(address)));
}
