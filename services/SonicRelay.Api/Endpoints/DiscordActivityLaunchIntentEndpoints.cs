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

/// <summary>Routes used by Discord Activities and desktop redemption for Activity launch links.</summary>
public static class DiscordActivityLaunchIntentEndpoints
{
    public sealed record ActivityRequest(string Code, string GuildId, string ChannelId,
        string RequestedByUserId, int TtlSeconds = 120);
    public sealed record TokenRequest(string Token);
    public sealed record BindRequest(Guid SessionId);

    public static IEndpointRouteBuilder MapDiscordActivityLaunchIntentEndpoints(this IEndpointRouteBuilder app)
    {
        var bot = app.MapGroup("/api/launch-intents").WithTags("Launch intents")
            .RequireAuthorization("launch-intents:bot");
        bot.MapPost("/activity", async (ActivityRequest request, AppDbContext db, ISessionCodeStore codes,
            IConfiguration config, TimeProvider time, CancellationToken ct) =>
        {
            if (!ValidContext(request.GuildId, request.ChannelId, request.RequestedByUserId))
                return Results.BadRequest();
            var session = await ResolveSessionAsync(request.Code, db, codes, config, time.GetUtcNow(), ct);
            if (session is null) return Results.NotFound();
            var token = RelayCapability.NewToken();
            var intent = RelayCapability.Create("activity", token, time.GetUtcNow(), request.TtlSeconds);
            intent.SessionId = session.Id;
            intent.GuildId = request.GuildId;
            intent.ChannelId = request.ChannelId;
            intent.UserId = request.RequestedByUserId;
            db.LaunchCapabilities.Add(intent);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { intent.Id, intent.ExpiresAt });
        });

        var device = app.MapGroup("/api/launch-intents").WithTags("Launch intents")
            .RequireAuthorization("DeviceAuthenticated");
        device.MapPost("/redeem", async (TokenRequest request, ClaimsPrincipal user, AppDbContext db,
            TimeProvider time, CancellationToken ct) =>
        {
            var deviceIdentity = await DeviceIdentityEndpoints.RequireDeviceAsync(user, db, ct);
            if (deviceIdentity is null) return Results.Unauthorized();
            if (request.Token is not { Length: 64 }) return Results.NotFound();
            var hash = RelayCapability.Hash(request.Token);
            var intent = await db.LaunchCapabilities.SingleOrDefaultAsync(x => x.TokenHash == hash
                && (x.Kind == "share" || x.Kind == "watch") && x.ExpiresAt > time.GetUtcNow()
                && x.ConsumedAt == null, ct);
            if (intent is null) return Results.NotFound();
            intent.ConsumedAt = time.GetUtcNow();
            intent.DeviceId = deviceIdentity.Id;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(); }
            return Results.Ok(new { intent.Id, intent.Kind, intent.Code, intent.SessionId });
        }).RequireAuthorization("session:create");

        device.MapPost("/{id:guid}/bind", async (Guid id, BindRequest request, ClaimsPrincipal user,
            AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var deviceIdentity = await DeviceIdentityEndpoints.RequireDeviceAsync(user, db, ct);
            if (deviceIdentity is null) return Results.Unauthorized();
            var intent = await db.LaunchCapabilities.SingleOrDefaultAsync(x => x.Id == id && x.Kind == "share"
                && x.DeviceId == deviceIdentity.Id && x.ConsumedAt != null && x.ExpiresAt > time.GetUtcNow()
                && x.SessionId == null, ct);
            if (intent is null || !await db.StreamSessions.AnyAsync(x => x.Id == request.SessionId
                    && x.SourceDeviceId == deviceIdentity.Id && x.Mode == SessionModes.ScreenShare
                    && x.Status != SessionStatuses.Ended && x.Status != SessionStatuses.Expired, ct))
                return Results.NotFound();
            intent.SessionId = request.SessionId;
            intent.ConsumedAt = time.GetUtcNow();
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(); }
            return Results.NoContent();
        }).RequireAuthorization("session:create");

        app.MapGet("/open/launch", (HttpContext context, IOptions<RelayLaunchOptions> options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var download = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(options.Value.DownloadUrl);
            const string start = "<!doctype html><meta name='referrer' content='no-referrer'><title>Open FrameRelay</title><p>Opening FrameRelay…</p><a id='open'>Open FrameRelay</a><p><a href='";
            const string end = "'>Download or get help</a></p><script>const t=location.hash.slice(1);history.replaceState(null,'',location.pathname);if(/^[a-f0-9]{64}$/.test(t)){const u='framerelay://launch?token='+t;document.getElementById('open').href=u;location.href=u;}</script>";
            return Results.Content(start + download + end, "text/html");
        }).AllowAnonymous().WithTags("Launch links");

        return app;
    }

    private static bool ValidContext(params string[] values) => values.All(value =>
        value is { Length: > 0 and <= 32 } && value.All(char.IsAsciiDigit));

    private static async Task<StreamSession?> ResolveSessionAsync(string code, AppDbContext db,
        ISessionCodeStore codes, IConfiguration config, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 6 || normalized.Any(c => !char.IsAsciiLetterOrDigit(c))) return null;
        var key = config["Sessions:CodeHmacKey"];
        if (string.IsNullOrWhiteSpace(key)) return null;
        var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes(normalized)));
        var id = await codes.RedeemAsync(hash, ct);
        return id is null ? null : await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == id
            && x.Mode == SessionModes.ScreenShare && x.CodeExpiresAt > now
            && x.Status != SessionStatuses.Ended && x.Status != SessionStatuses.Expired, ct);
    }
}
