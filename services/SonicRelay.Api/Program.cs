using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Prometheus;
using SonicRelay.Api.Authorization;
using SonicRelay.Api.Endpoints;
using SonicRelay.Api.Services;
using SonicRelay.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// The signaling receive loop polls session state every second per socket, which
// floods the console with EF `SELECT Status, CodeExpiresAt` command logs. Keep
// SQL at Warning+ so app logs (SonicRelay.*, request diagnostics) stay readable.
// Overridable via Logging:LogLevel configuration if full SQL is ever needed.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

builder.Services.AddEndpointsApiExplorer();
// Name the document explicitly: unconfigured, Swashbuckle titles the Swagger UI
// after the assembly ("SonicRelay.Api"), showing an internal identifier on the
// API's most public page. `SonicRelay` is the canonical product spelling.
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "SonicRelay API",
    Version = "v1",
    Description =
        "Control plane for SonicRelay: device identity, pairing, sessions, join codes and " +
        "WebSocket signaling. WebRTC/Opus audio flows directly between clients, or through " +
        "coturn, and never through this API.",
}));
builder.Services.AddSonicRelayInfrastructure(builder.Configuration);
builder.Services.AddSingleton<SonicRelay.Api.Observability.SonicRelayMetrics>();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddScoped<TurnCredentialService>();
builder.Services.Configure<TurnOptions>(builder.Configuration.GetSection("Turn"));
// The deploy .env feeds coturn with flat variable names; accept those as a
// fallback so one .env configures both containers without duplication.
builder.Services.PostConfigure<TurnOptions>(options =>
{
    var configuration = builder.Configuration;
    options.StaticAuthSecret ??= configuration["TURN_STATIC_AUTH_SECRET"];
    if (options.TurnUris.Length == 0 && configuration["TURN_URIS"] is { Length: > 0 } turnUris)
    {
        options.TurnUris = turnUris.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    // The deploy .env template only names the relay's public host; derive the
    // standard turn: URIs (and a matching stun:) from it so TURN works without
    // hand-writing TURN_URIS. A turns:5349 entry is intentionally not derived —
    // it requires a TLS certificate mounted into coturn, so it stays opt-in via
    // an explicit TURN_URIS.
    var turnPublicHost = configuration["TURN_PUBLIC_HOST"];
    if (options.TurnUris.Length == 0 && !string.IsNullOrWhiteSpace(turnPublicHost))
    {
        options.TurnUris =
        [
            $"turn:{turnPublicHost}:3478?transport=udp",
            $"turn:{turnPublicHost}:3478?transport=tcp"
        ];
    }
    if (configuration["STUN_URIS"] is { Length: > 0 } stunUris)
    {
        options.StunUris = stunUris.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    else if (!string.IsNullOrWhiteSpace(turnPublicHost))
    {
        // Prefer our own coturn for STUN over the Google default when a relay
        // host is configured.
        options.StunUris = [$"stun:{turnPublicHost}:3478"];
    }
    if (configuration.GetValue<int?>("TURN_CREDENTIAL_TTL_SECONDS") is { } ttl && ttl > 0)
    {
        options.CredentialTtlSeconds = ttl;
    }
});
// CORS exists for exactly one client: the Flutter viewer's web build, whose calls are
// all cross-origin. Native clients never send an Origin header and are untouched by
// this. Credentials are deliberately not allowed — the viewer authenticates with a
// bearer token in a header, never with a cookie, so nothing here needs to be
// credentialed, and `AllowAnyOrigin` stays off so the allowlist keeps meaning something.
var corsOptions = builder.Configuration.GetSection(WebClientCorsOptions.SectionName).Get<WebClientCorsOptions>()
    ?? new WebClientCorsOptions();
builder.Services.Configure<WebClientCorsOptions>(builder.Configuration.GetSection(WebClientCorsOptions.SectionName));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var allowedOrigins = corsOptions.EffectiveAllowedOrigins;
    if (corsOptions.ResolveAllowLoopbackOrigins(builder.Environment.IsProduction()))
    {
        // `flutter run -d chrome` binds a new random port every launch, so local
        // development cannot be covered by a fixed list of origins.
        policy.SetIsOriginAllowed(origin =>
            allowedOrigins.Contains(origin, StringComparer.Ordinal)
            || WebClientCorsOptions.IsLoopbackOrigin(origin));
    }
    else
    {
        policy.WithOrigins([.. allowedOrigins]);
    }

    policy
        .AllowAnyMethod()
        .AllowAnyHeader()
        // Without this the browser hides Retry-After from the viewer, and a rate-limited
        // device setup cannot tell the user when to try again.
        .WithExposedHeaders("Retry-After")
        .SetPreflightMaxAge(TimeSpan.FromSeconds(corsOptions.PreflightMaxAgeSeconds));
}));
builder.Services.Configure<DeviceIdentityOptions>(builder.Configuration.GetSection("DeviceIdentity"));
builder.Services.Configure<SignalingOriginOptions>(
    builder.Configuration.GetSection(SignalingOriginOptions.SectionName));
var allowedWebOrigins = builder.Configuration
    .GetSection(SignalingOriginOptions.SectionName)
    .Get<SignalingOriginOptions>()?.AllowedWebOrigins ?? [];
builder.Services.AddCors(options => options.AddPolicy(SignalingGrantEndpoints.CorsPolicyName, policy =>
    policy.WithOrigins(allowedWebOrigins)
        .WithHeaders("Authorization", "Content-Type")
        .WithMethods(HttpMethods.Post)
        .AllowCredentials()));
builder.Services.Configure<PublicRoomOptions>(builder.Configuration.GetSection(PublicRoomOptions.SectionName));
builder.Services.AddSingleton<PublicRoomSeeder>();
builder.Services.AddSingleton<PublicRoomPublisherService>();
builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<PublicRoomPublisherService>());
builder.Services.AddSingleton<DeviceCredentialService>();
builder.Services.AddSingleton<SignalingGrantService>();
builder.Services.AddSingleton<PairingChallengeService>();
builder.Services.AddScoped<IAuthorizationHandler, DeviceScopeAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, SignalingGrantAuthorizationHandler>();

builder.Services.AddAuthentication(options => options.DefaultForbidScheme = "DeviceBearer")
    .AddJwtBearer("DeviceBearer", jwtOptions =>
    {
        // Keep claim types as issued (e.g. "sub", not ClaimTypes.NameIdentifier) so
        // downstream code reading JwtRegisteredClaimNames.Sub/"cv"/"scope" matches
        // what DeviceCredentialService.IssueAccessToken actually put in the token.
        jwtOptions.MapInboundClaims = false;
        var deviceOptions = builder.Configuration.GetSection("DeviceIdentity").Get<DeviceIdentityOptions>()
            ?? new DeviceIdentityOptions();
        jwtOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = deviceOptions.Issuer,
            ValidAudience = deviceOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(deviceOptions.TokenSigningKey ?? string.Empty)),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    })
    .AddScheme<AuthenticationSchemeOptions, SignalingGrantAuthenticationHandler>(
        SignalingGrantAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddSingleton<SessionCleanupService>();
builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<SessionCleanupService>());
// Data retention (issue #44): nothing SonicRelay collects may stay identifiable beyond the
// 90 days declared in the Google Play Data Safety entry. See docs/data-retention.md.
builder.Services.Configure<DataRetentionOptions>(
    builder.Configuration.GetSection(DataRetentionOptions.SectionName));
builder.Services.AddSingleton<SonicRelay.Api.Observability.DataRetentionMetrics>();
builder.Services.AddSingleton<DataRetentionState>();
builder.Services.AddScoped<DeviceIdentityRotationService>();
builder.Services.AddSingleton<DataRetentionService>();
builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<DataRetentionService>());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
        }

        var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SonicRelay.RateLimiting");
        logger.LogWarning("Rate limit rejected request for policy {PolicyName} on path {RequestPath}",
            policy, context.HttpContext.Request.Path);
        return ValueTask.CompletedTask;
    };

    options.AddPolicy("create-session", context => IpLimit(context, "RateLimits:CreateSession", 10));
    options.AddPolicy("join-session", context => IpLimit(context, "RateLimits:JoinSession", 10));
    options.AddPolicy("rotate-code", context => IpLimit(context, "RateLimits:RotateCode", 5));
    options.AddPolicy("device-bootstrap", context => IpLimit(context, "RateLimits:DeviceBootstrap", 10));
    options.AddPolicy("device-token", context => IpLimit(context, "RateLimits:DeviceToken", 10));
    options.AddPolicy("pairing-create", context => IpLimit(context, "RateLimits:PairingCreate", 10));
    options.AddPolicy("pairing-complete", context => IpLimit(context, "RateLimits:PairingComplete", 10));
});
// A signaling socket carries no traffic between negotiations — this server only answers
// `pong` to a client `ping` and never initiates one — so it sits idle, and intermediaries
// reap idle WebSockets. Viewer diagnostics showed a near-constant ~90s close; nginx's own
// default read timeout is 60s. The framework default of 2 minutes is longer than either, so
// it never fired in time. Clients set their own keepalive too, but an already installed app
// version keeps whatever it shipped with, so the server has to hold the connection open on
// its own. Configured here rather than passed to UseWebSockets() so the value is observable
// through DI and can be asserted in tests.
// 20s matches both clients, leaving room for a lost ping inside a 60s window.
builder.Services.Configure<WebSocketOptions>(options =>
    options.KeepAliveInterval = TimeSpan.FromSeconds(20));
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Postgres") ?? string.Empty, name: "postgres")
    .AddRedis(builder.Configuration["Redis:ConnectionString"] ?? string.Empty, name: "redis")
    // Surfaces a cleanup that stopped running, long before anything could reach 90 days.
    .AddCheck<DataRetentionHealthCheck>("data-retention", tags: ["retention"]);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("DeviceAuthenticated", policy =>
    {
        policy.AddAuthenticationSchemes("DeviceBearer");
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new DeviceScopeRequirement());
    });

    options.AddPolicy("signaling:connect", policy =>
    {
        policy.AddAuthenticationSchemes("DeviceBearer", SignalingGrantAuthenticationHandler.SchemeName);
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new DeviceScopeRequirement("signaling:connect"));
    });

    foreach (var scope in new[]
    {
        "session:create", "session:join", "session:end", "turn:credentials",
        "device:read", "device:manage", "pairing:create", "pairing:complete", "pairing:revoke"
    })
    {
        options.AddPolicy(scope, policy =>
        {
            policy.AddAuthenticationSchemes("DeviceBearer");
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new DeviceScopeRequirement(scope));
        });
    }
});

var app = builder.Build();

// Missing DeviceIdentity secrets otherwise surface only on the first request, as an
// opaque IDX10703 SymmetricSecurityKey exception deep in JwtBearer's lazy options
// binding — DeviceBearer is the app's only auth scheme, so every route (including
// anonymous ones like /api/devices/bootstrap) hits it via UseAuthentication(). Fail
// loudly at startup instead, naming exactly which configuration key is missing.
var deviceIdentityOptions = app.Services.GetRequiredService<IOptions<DeviceIdentityOptions>>().Value;
var missingDeviceIdentityKeys = new[]
{
    (nameof(DeviceIdentityOptions.TokenSigningKey), deviceIdentityOptions.TokenSigningKey),
    (nameof(DeviceIdentityOptions.CredentialHmacKey), deviceIdentityOptions.CredentialHmacKey),
    (nameof(DeviceIdentityOptions.PairingCodeHmacKey), deviceIdentityOptions.PairingCodeHmacKey),
}.Where(pair => string.IsNullOrWhiteSpace(pair.Item2)).Select(pair => pair.Item1).ToArray();
if (missingDeviceIdentityKeys.Length > 0)
{
    throw new InvalidOperationException(
        $"Missing required DeviceIdentity configuration: {string.Join(", ", missingDeviceIdentityKeys)}. " +
        $"Set the corresponding DeviceIdentity__* environment variable(s) (see docs/deployment-vps-ssh.md).");
}

if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseWebSockets();
// Ahead of authentication and the rate limiter on purpose: a preflight carries neither
// credentials nor a body, so letting it reach either would 401 the browser's own probe
// or spend the caller's device-bootstrap budget before the real request arrives.
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
// Prometheus scrape endpoint (issue #21). Anonymous so Prometheus can scrape it;
// exposes only aggregate SonicRelay metrics, no session ids or PII.
app.MapMetrics();
app.MapDeviceIdentityEndpoints();
app.MapPairingEndpoints();
app.MapSessionEndpoints();
app.MapWebRtcEndpoints();
app.MapSettingsEndpoints();
app.MapSignalingGrantEndpoints();
app.MapSignalingWebSocketEndpoint();
app.MapPublicRoomEndpoints();

app.Run();

RateLimitPartition<string> IpLimit(HttpContext context, string section, int defaultPermitLimit) =>
    CreateLimit(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", section, defaultPermitLimit);

RateLimitPartition<string> CreateLimit(string key, string section, int defaultPermitLimit) =>
    RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = builder.Configuration.GetValue($"{section}:PermitLimit", defaultPermitLimit),
        Window = TimeSpan.FromSeconds(builder.Configuration.GetValue($"{section}:WindowSeconds", 60)),
        QueueLimit = 0,
        AutoReplenishment = true
    });

public partial class Program;
