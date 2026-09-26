using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Services;
using SonicRelay.Application.Abstractions;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class LaunchIntentEndpoints
{
    public sealed record ShareRequest(string Provider, string GuildId, string ChannelId, string RequestedByUserId, int TtlSeconds = 300);
    public sealed record WatchRequest(string Code, int TtlSeconds = 300);
    public sealed record ActivityRequest(string Code, string GuildId, string ChannelId, string RequestedByUserId, int TtlSeconds = 120);
    public sealed record TokenRequest(string Token);
    public sealed record BindRequest(Guid SessionId);
    public sealed record PublishedRequest(string? MessageId);

    public static IEndpointRouteBuilder MapLaunchIntentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/launch-intents");
        group.AddEndpointFilter(async (context, next) =>
        {
            // Device redemption/binding use the normal device policies. Every other route is bot-only.
            if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<DeviceLaunchOperation>() is not null) return await next(context);
            var expected = context.HttpContext.RequestServices.GetRequiredService<IOptions<RelayLaunchOptions>>().Value.ServiceToken;
            var actual = context.HttpContext.Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(expected) || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + expected))))
                return Results.Unauthorized();
            return await next(context);
        });
        group.MapPost("/share", async (ShareRequest request, AppDbContext db, IOptions<RelayLaunchOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            if (request.Provider != "discord" || !ValidContext(request.GuildId, request.ChannelId, request.RequestedByUserId)) return Results.BadRequest();
            var token = RelayCapability.NewToken();
            var intent = RelayCapability.Create("share", token, time.GetUtcNow(), request.TtlSeconds);
            intent.GuildId = request.GuildId; intent.ChannelId = request.ChannelId; intent.UserId = request.RequestedByUserId;
            db.LaunchCapabilities.Add(intent); await db.SaveChangesAsync(ct);
            return Results.Ok(new { intent.Id, launchUrl = Url(options.Value, token), intent.ExpiresAt });
        });
        group.MapPost("/watch", async (WatchRequest request, AppDbContext db, ISessionCodeStore codes, IConfiguration config,
            IOptions<RelayLaunchOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var session = await ResolveSessionAsync(request.Code, db, codes, config, time.GetUtcNow(), ct);
            if (session is null) return Results.NotFound();
            var token = RelayCapability.NewToken();
            var intent = RelayCapability.Create("watch", token, time.GetUtcNow(), request.TtlSeconds);
            intent.SessionId = session.Id; intent.Code = request.Code.Trim().ToUpperInvariant();
            db.LaunchCapabilities.Add(intent); await db.SaveChangesAsync(ct);
            return Results.Ok(new { launchUrl = Url(options.Value, token), intent.ExpiresAt });
        });
        group.MapPost("/activity", async (ActivityRequest request, AppDbContext db, ISessionCodeStore codes, IConfiguration config, TimeProvider time, CancellationToken ct) =>
        {
            if (!ValidContext(request.GuildId, request.ChannelId, request.RequestedByUserId)) return Results.BadRequest();
            var session = await ResolveSessionAsync(request.Code, db, codes, config, time.GetUtcNow(), ct);
            if (session is null) return Results.NotFound();
            var intent = RelayCapability.Create("activity", RelayCapability.NewToken(), time.GetUtcNow(), request.TtlSeconds);
            intent.SessionId = session.Id; intent.GuildId = request.GuildId; intent.ChannelId = request.ChannelId; intent.UserId = request.RequestedByUserId;
            db.LaunchCapabilities.Add(intent); await db.SaveChangesAsync(ct);
            return Results.Ok(new { intent.Id, intent.ExpiresAt });
        });
        group.MapGet("/pending", async (AppDbContext db, TimeProvider time, CancellationToken ct) => Results.Ok(
            await db.LaunchCapabilities.Where(x => x.Kind == "share" && x.ExpiresAt > time.GetUtcNow() && x.MessageId == null)
                .Select(x => new { x.Id, x.GuildId, x.ChannelId, requestedByUserId = x.UserId, status = x.SessionId == null ? "pending" : "ready", x.ExpiresAt }).ToListAsync(ct)
        ));
        group.MapGet("/{id:guid}", async (Guid id, int? watchTtlSeconds, AppDbContext db, IOptions<RelayLaunchOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var intent = await db.LaunchCapabilities.SingleOrDefaultAsync(x => x.Id == id && x.Kind == "share" && x.ExpiresAt > time.GetUtcNow(), ct);
            if (intent is null) return Results.NotFound();
            string? watchLaunchUrl = null;
            if (intent.SessionId is { } sessionId)
            {
                var session = await db.StreamSessions.FindAsync([sessionId], ct);
                if (session is null || session.Status is SessionStatuses.Ended or SessionStatuses.Expired) return Results.NotFound();
                var token = RelayCapability.NewToken();
                var watch = RelayCapability.Create("watch", token, time.GetUtcNow(), watchTtlSeconds ?? 300);
                watch.SessionId = sessionId;
                db.LaunchCapabilities.Add(watch); await db.SaveChangesAsync(ct);
                watchLaunchUrl = Url(options.Value, token);
            }
            return Results.Ok(new { intent.Id, status = intent.SessionId == null ? "pending" : "ready", intent.SessionId, watchLaunchUrl, intent.ExpiresAt });
        });
        group.MapPost("/{id:guid}/published", async (Guid id, PublishedRequest request, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var intent = await db.LaunchCapabilities.SingleOrDefaultAsync(x => x.Id == id && x.Kind == "share" && x.ExpiresAt > time.GetUtcNow(), ct);
            if (intent?.SessionId is null) return Results.NotFound();
            intent.MessageId = request.MessageId ?? "published"; await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        group.MapPost("/redeem", RedeemAsync).WithMetadata(new DeviceLaunchOperation()).RequireAuthorization("DeviceAuthenticated");
        group.MapPost("/{id:guid}/bind", async (Guid id, BindRequest request, ClaimsPrincipal user, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var device = await DeviceIdentityEndpoints.RequireDeviceAsync(user, db, ct);
            if (device is null) return Results.Unauthorized();
            var intent = await db.LaunchCapabilities.SingleOrDefaultAsync(x => x.Id == id && x.Kind == "share" && x.DeviceId == device.Id && x.ConsumedAt != null && x.ExpiresAt > time.GetUtcNow() && x.SessionId == null, ct);
            if (intent is null || !await db.StreamSessions.AnyAsync(x => x.Id == request.SessionId && x.SourceDeviceId == device.Id && x.Mode == SessionModes.ScreenShare && x.Status != SessionStatuses.Ended && x.Status != SessionStatuses.Expired, ct)) return Results.NotFound();
            intent.SessionId = request.SessionId;
            intent.ConsumedAt = time.GetUtcNow();
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Results.Conflict(); }
            return Results.NoContent();
        }).WithMetadata(new DeviceLaunchOperation()).RequireAuthorization("session:create");
        app.MapGet("/open/launch", (HttpContext context, IOptions<RelayLaunchOptions> options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var download = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(options.Value.DownloadUrl);
            return Results.Content("<!doctype html><meta name='referrer' content='no-referrer'><title>Open FrameRelay</title><p>Opening FrameRelay…</p><a id='open'>Open FrameRelay</a><p><a href='" + download + "'>Download or get help</a></p><script>const t=location.hash.slice(1);history.replaceState(null,'',location.pathname);if(/^[a-f0-9]{64}$/.test(t)){const u='framerelay://launch?token='+t;document.getElementById('open').href=u;location.href=u;}</script>", "text/html");
        });
        return app;
    }

    private sealed class DeviceLaunchOperation;
    private static bool ValidContext(params string[] values) => values.All(x => x is { Length: > 0 and <= 32 } && x.All(char.IsAsciiDigit));
    private static string Url(RelayLaunchOptions settings, string token) => settings.PublicBaseUrl.TrimEnd('/') + "/open/launch#" + token;
    private static async Task<IResult> RedeemAsync(TokenRequest request, ClaimsPrincipal user, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(user, db, ct);
        if (device is null) return Results.Unauthorized();
        if (request.Token is not { Length: 64 }) return Results.NotFound();
        var hash = RelayCapability.Hash(request.Token);
        var intent = await db.LaunchCapabilities.SingleOrDefaultAsync(x => x.TokenHash == hash && (x.Kind == "share" || x.Kind == "watch") && x.ExpiresAt > time.GetUtcNow() && x.ConsumedAt == null, ct);
        if (intent is null) return Results.NotFound();
        intent.ConsumedAt = time.GetUtcNow(); intent.DeviceId = device.Id;
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Results.Conflict(); }
        return Results.Ok(new { intent.Id, intent.Kind, intent.Code, intent.SessionId });
    }
    internal static async Task<StreamSession?> ResolveSessionAsync(string code, AppDbContext db, ISessionCodeStore codes, IConfiguration config, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = code?.Trim().ToUpperInvariant() ?? "";
        if (normalized.Length != 6 || normalized.Any(x => !char.IsAsciiLetterOrDigit(x))) return null;
        var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(config["Sessions:CodeHmacKey"]!), Encoding.UTF8.GetBytes(normalized)));
        var id = await codes.RedeemAsync(hash, ct);
        return id is null ? null : await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == id && x.Mode == SessionModes.ScreenShare && x.CodeExpiresAt > now && x.Status != SessionStatuses.Ended && x.Status != SessionStatuses.Expired, ct);
    }
}
