# Screen-share sessions (backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a new Windows app that both publishes and views create and join `screen_share` sessions, without altering a single behavior of the existing audio publisher or the Flutter viewer.

**Architecture:** Purely additive changes to the control plane. A new device type carries the union of the publisher and viewer scopes; a new session mode marks screen sessions; the single admission path (`AdmitViewerCoreAsync`) gains two mode-scoped rules — reject non-`windows_desktop` devices, and create the pairing instead of rejecting when it is missing. The API still never parses SDP and never touches media.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core (InMemory in tests), xunit, prometheus-net.

**Spec:** `docs/superpowers/specs/2026-08-23-screen-share-sessions-design.md`

## Global Constraints

- Every change is **additive**. `windows_publisher` and `flutter_viewer` keep byte-identical scope lists; `broadcast` and `duplex` keep identical behavior.
- **No EF migration.** `Mode` is `HasMaxLength(16)` (`src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs:31`) and `screen_share` is 12 chars; `DeviceType` is `HasMaxLength(40)` (`:74`) and `windows_desktop` is 15 chars.
- New device type string: exactly `windows_desktop`. New session mode string: exactly `screen_share`. New error code: exactly `device_type_not_allowed`.
- Metric names are prefixed `sonicrelay_` like every existing metric, and carry **no** high-cardinality label — no session id, device id, IP, SDP or ICE candidate.
- The API must not parse SDP (ADR 0001) and must not log SDP, ICE candidates, or media content.
- Run the whole suite with: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj`
- Run one test with: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~<TestName>"`

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/SonicRelay.Domain/Devices/DeviceTypes.cs` | Device type constants | Add `WindowsDesktop` |
| `src/SonicRelay.Domain/Sessions/StreamSession.cs` | Session modes and audio policy | Add `ScreenShare`, extend `IsSupported`, extend `DefaultsFor` |
| `services/SonicRelay.Api/Endpoints/DeviceIdentityEndpoints.cs` | Bootstrap validation | Extend `ValidTypePlatform` |
| `services/SonicRelay.Api/Services/DeviceCredentialService.cs` | Scopes per device type | Add `WindowsDesktop` branch |
| `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs` | Session create and admission | Error message, device-type gate, auto-pairing |
| `services/SonicRelay.Api/Endpoints/PairingEndpoints.cs` | Pairing | Correct two stale comments |
| `services/SonicRelay.Api/Observability/SonicRelayMetrics.cs` | Prometheus metrics | Three new instruments |
| `tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs` | All new behavior + regression guards | Create |
| `docs/protocol.md`, `docs/device-identity.md`, `docs/adr/0008-screen-share-sessions.md` | Contract documentation | Update / create |

---

### Task 1: Device type `windows_desktop`

**Files:**
- Modify: `src/SonicRelay.Domain/Devices/DeviceTypes.cs`
- Modify: `services/SonicRelay.Api/Endpoints/DeviceIdentityEndpoints.cs:118-120`
- Modify: `services/SonicRelay.Api/Services/DeviceCredentialService.cs:59-71`
- Test: `tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `DeviceTypes.WindowsDesktop` (const string `"windows_desktop"`), valid with `DevicePlatforms.Windows`; its token carries exactly ten scopes. Every later task bootstraps devices with it.

- [ ] **Step 1: Write the failing tests**

Create `tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// Screen-share sessions (issue #30, phase 0): the windows_desktop device type, the
/// screen_share session mode, device-type-gated admission and auto-pairing on join — plus
/// the guarantee that the audio device types and session modes behave exactly as before.
/// </summary>
public sealed class ScreenShareSessionTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public ScreenShareSessionTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Windows_desktop_bootstraps_on_the_windows_platform()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Desk PC", DeviceTypes.WindowsDesktop, DevicePlatforms.Windows));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("android")]
    [InlineData("ios")]
    public async Task Windows_desktop_is_rejected_on_non_windows_platforms(string platform)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Desk PC", DeviceTypes.WindowsDesktop, platform));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Windows_desktop_token_carries_exactly_the_union_of_publisher_and_viewer_scopes()
    {
        var client = _factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var scopes = ScopesInToken(session.AccessToken);

        Assert.Equal(
            new[]
            {
                "device:manage", "device:read",
                "pairing:complete", "pairing:create", "pairing:revoke",
                "session:create", "session:end", "session:join",
                "signaling:connect", "turn:credentials"
            },
            scopes.Order().ToArray());
    }

    // The token is the contract under test, so the scopes are read out of the JWT itself
    // rather than trusted from the response body.
    private static string[] ScopesInToken(string accessToken)
    {
        var payload = accessToken.Split('.')[1];
        var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
            .Replace('-', '+').Replace('_', '/');
        using var document = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(padded));
        return document.RootElement.GetProperty("scope").GetString()!.Split(' ');
    }
}
```

Later tasks append their tests and helpers **inside** this class, before its closing brace.

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ScreenShareSessionTests"`
Expected: compile error — `DeviceTypes` has no member `WindowsDesktop`.

- [ ] **Step 3: Add the constant**

In `src/SonicRelay.Domain/Devices/DeviceTypes.cs`, inside `DeviceTypes`:

```csharp
    /// <summary>
    /// The Windows desktop app (SonicDesktopRelay), which both publishes and views screen
    /// sessions with one identity — hence the union of the publisher and viewer scopes.
    /// </summary>
    public const string WindowsDesktop = "windows_desktop";
```

- [ ] **Step 4: Accept the new type/platform pair**

In `services/SonicRelay.Api/Endpoints/DeviceIdentityEndpoints.cs`, replace `ValidTypePlatform`:

```csharp
    private static bool ValidTypePlatform(string? type, string? platform) =>
        (type == DeviceTypes.WindowsPublisher && platform == DevicePlatforms.Windows)
        || (type == DeviceTypes.WindowsDesktop && platform == DevicePlatforms.Windows)
        || (type == DeviceTypes.FlutterViewer && platform is DevicePlatforms.Android or DevicePlatforms.Ios);
```

- [ ] **Step 5: Grant the union of scopes**

In `services/SonicRelay.Api/Services/DeviceCredentialService.cs`, add a branch to `ScopesFor` between the two existing ones. Do not edit the existing branches:

```csharp
        // The desktop app is publisher and viewer at once: it shares its own screen and views
        // another machine's. Rather than widening windows_publisher — which would hand new
        // privileges to an app that never asked for them — this type carries the union.
        DeviceTypes.WindowsDesktop =>
        [
            "device:read", "device:manage",
            "pairing:create", "pairing:complete", "pairing:revoke",
            "session:create", "session:join", "session:end",
            "signaling:connect", "turn:credentials"
        ],
```

- [ ] **Step 6: Run the tests and verify they pass**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ScreenShareSessionTests"`
Expected: PASS, 4 tests.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj`
Expected: PASS, no test edited.

- [ ] **Step 8: Commit**

```bash
git add src/SonicRelay.Domain/Devices/DeviceTypes.cs services/SonicRelay.Api/Endpoints/DeviceIdentityEndpoints.cs services/SonicRelay.Api/Services/DeviceCredentialService.cs tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs
git commit -m "feat(devices): add windows_desktop device type with publisher+viewer scopes"
```

---

### Task 2: Session mode `screen_share`

**Files:**
- Modify: `src/SonicRelay.Domain/Sessions/StreamSession.cs:73-95` (`SessionModes`) and `:110-116` (`SessionAudioPolicy.DefaultsFor`)
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs:47`
- Test: `tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs`

**Interfaces:**
- Consumes: `DeviceTypes.WindowsDesktop` from Task 1.
- Produces: `SessionModes.ScreenShare` (const string `"screen_share"`), accepted by `SessionModes.Normalize` and `IsSupported`. `SessionAudioPolicy.DefaultsFor(SessionModes.ScreenShare, ParticipantRoles.Publisher)` returns `(SendAllowed: true, CanSendAudio: true, CanReceiveAudio: false)`; for `Viewer`, `(false, false, true)`.

- [ ] **Step 1: Write the failing tests**

Append to `ScreenShareSessionTests.cs`:

```csharp
    [Fact]
    public async Task Create_in_screen_share_mode_returns_the_mode_and_a_send_only_publisher()
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var response = await client.PostAsJsonAsync("/api/sessions",
            new { maxViewers = 3, mode = " Screen_Share " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("screen_share", body.GetProperty("mode").GetString());

        var publisher = await GetParticipantAsync(body.GetProperty("id").GetGuid(), ParticipantRoles.Publisher);
        Assert.True(publisher.AudioSendAllowed);
        Assert.True(publisher.CanSendAudio);
        Assert.False(publisher.CanReceiveAudio);
    }

    [Fact]
    public async Task An_unknown_mode_is_still_rejected_as_invalid_session_mode()
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers = 1, mode = "remote_desktop" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("invalid_session_mode", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_omitted_mode_still_means_broadcast()
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers = 1 });

        var body = await ReadJsonAsync(response);
        Assert.Equal(SessionModes.Broadcast, body.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Audio_permission_on_a_screen_share_session_is_rejected_as_not_duplex()
    {
        var (owner, sessionId, _) = await CreateScreenShareSessionAsync();
        var publisher = await GetParticipantAsync(sessionId, ParticipantRoles.Publisher);

        var response = await owner.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/participants/{publisher.Id}/audio-permission",
            new { canSendAudio = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("session_not_duplex", body.GetProperty("code").GetString());
    }
```

Add these private helpers to the class (copied from the idiom in `DuplexAudioTests.cs:447-520`, adapted):

```csharp
    private async Task<(HttpClient Client, Guid DeviceId)> BootstrapAsync(string deviceType, string platform)
    {
        var client = _factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(client, deviceType, platform);
        return (client, session.DeviceId);
    }

    private async Task<(HttpClient Owner, Guid SessionId, string Code)> CreateScreenShareSessionAsync(
        int maxViewers = 3)
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        var response = await client.PostAsJsonAsync("/api/sessions",
            new { maxViewers, mode = SessionModes.ScreenShare });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        return (client, body.GetProperty("id").GetGuid(), body.GetProperty("code").GetString()!);
    }

    private async Task<SessionParticipant> GetParticipantAsync(Guid sessionId, string role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SessionParticipants.AsNoTracking()
            .SingleAsync(x => x.SessionId == sessionId && x.Role == role);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
```

Add the matching usings at the top of the file:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ScreenShareSessionTests"`
Expected: compile error — `SessionModes` has no member `ScreenShare`.

- [ ] **Step 3: Add the mode**

In `src/SonicRelay.Domain/Sessions/StreamSession.cs`, in `SessionModes`:

```csharp
    /// <summary>
    /// The source shares a screen (video) plus its system audio; the others only receive.
    /// Audio permissions match <see cref="Broadcast"/>; what the mode adds is a way for the
    /// backend and the clients to recognise a screen session without parsing SDP, which
    /// ADR 0001 forbids.
    /// </summary>
    public const string ScreenShare = "screen_share";

    public static bool IsSupported(string mode) => mode is Broadcast or Duplex or ScreenShare;
```

- [ ] **Step 4: Give the mode explicit audio defaults**

Replace `SessionAudioPolicy.DefaultsFor`:

```csharp
    /// <summary>
    /// In broadcast the publisher transmits and viewers listen, exactly as before duplex
    /// existed. Screen sharing is one-way too, and says so explicitly rather than falling
    /// through to the broadcast branch — so a future change to one mode cannot silently
    /// change the other. In duplex every participant may do both; the session owner can
    /// still revoke an individual participant's send permission afterwards.
    /// </summary>
    public static AudioDefaults DefaultsFor(string sessionMode, string role) =>
        sessionMode == SessionModes.Duplex
            ? new AudioDefaults(SendAllowed: true, CanSendAudio: true, CanReceiveAudio: true)
            : role == ParticipantRoles.Publisher
                ? new AudioDefaults(SendAllowed: true, CanSendAudio: true, CanReceiveAudio: false)
                : new AudioDefaults(SendAllowed: false, CanSendAudio: false, CanReceiveAudio: true);
```

The body is unchanged — only the comment names screen sharing. That is deliberate: `screen_share` and `broadcast` genuinely want the same defaults, and duplicating the branch would be a lie about them being independent.

- [ ] **Step 5: Update the error message**

In `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs`, in `CreateAsync`:

```csharp
        if (mode is null)
            return Results.BadRequest(new
            {
                error = $"Mode must be '{SessionModes.Broadcast}', '{SessionModes.Duplex}' or '{SessionModes.ScreenShare}'.",
                code = "invalid_session_mode"
            });
```

- [ ] **Step 6: Run the tests and verify they pass**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ScreenShareSessionTests"`
Expected: PASS, 8 tests.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/SonicRelay.Domain/Sessions/StreamSession.cs services/SonicRelay.Api/Endpoints/SessionEndpoints.cs tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs
git commit -m "feat(sessions): add screen_share session mode"
```

---

### Task 3: Admission gate — only `windows_desktop` joins a screen session

**Files:**
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs` (`AdmitViewerCoreAsync`, around `:288-308`)
- Test: `tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs`

**Interfaces:**
- Consumes: `DeviceTypes.WindowsDesktop` (Task 1), `SessionModes.ScreenShare` (Task 2).
- Produces: `403` with body `{ "error": "...", "code": "device_type_not_allowed" }` for a non-`windows_desktop` device joining a `screen_share` session, on both join paths.

- [ ] **Step 1: Write the failing tests**

Append to `ScreenShareSessionTests.cs`:

```csharp
    [Fact]
    public async Task A_flutter_viewer_cannot_join_a_screen_share_session_even_when_paired()
    {
        var (_, sessionId, code) = await CreateScreenShareSessionAsync();
        var (viewer, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        await PairWithSessionSourceAsync(sessionId, viewerDeviceId);

        var response = await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("device_type_not_allowed", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_windows_publisher_cannot_join_a_screen_share_session()
    {
        var (_, sessionId, code) = await CreateScreenShareSessionAsync();
        var (publisher, publisherDeviceId) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        await PairWithSessionSourceAsync(sessionId, publisherDeviceId);

        var response = await publisher.PostAsJsonAsync("/api/sessions/join", new { code });

        // windows_publisher has no session:join scope, so it is stopped by authorization
        // before admission ever runs. Asserting 403 either way is the point: this device
        // type must never reach a screen session.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_windows_desktop_device_joins_a_screen_share_session()
    {
        var (_, sessionId, code) = await CreateScreenShareSessionAsync();
        var (viewer, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var response = await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
```

Add the pairing helper:

```csharp
    private async Task PairWithSessionSourceAsync(Guid sessionId, Guid viewerDeviceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sourceDeviceId = await db.StreamSessions.Where(x => x.Id == sessionId)
            .Select(x => x.SourceDeviceId).SingleAsync();
        db.DevicePairings.Add(new DevicePairing
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = sourceDeviceId,
            ViewerDeviceId = viewerDeviceId,
            Status = DevicePairingStatuses.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~A_flutter_viewer_cannot_join"`
Expected: FAIL — the Flutter viewer gets `200 OK` because nothing rejects it yet.

- [ ] **Step 3: Add the gate**

In `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs`, inside `AdmitViewerCoreAsync`, immediately after the `existing is not null` early return and **before** the public-room/pairing block:

```csharp
        // A screen session carries a video track. A device type that cannot render video would
        // be handed an offer with a video m-line it does not understand, so the gate is on the
        // server rather than a hope that older clients withdraw politely. It also runs before
        // the pairing check: being paired must never be a way around it.
        if (session.Mode == SessionModes.ScreenShare && device.DeviceType != DeviceTypes.WindowsDesktop)
            return DeviceTypeNotAllowed();
```

Add the `using SonicRelay.Domain.Devices;` import at the top of the file if it is not already there, and the response helper next to `NotPaired()` around `:472`:

```csharp
    private static IResult DeviceTypeNotAllowed() =>
        Results.Json(new
        {
            error = "This session type is not available for this device.",
            code = "device_type_not_allowed"
        }, statusCode: StatusCodes.Status403Forbidden);
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ScreenShareSessionTests"`
Expected: PASS, 11 tests.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add services/SonicRelay.Api/Endpoints/SessionEndpoints.cs tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs
git commit -m "feat(sessions): restrict screen_share admission to windows_desktop devices"
```

---

### Task 4: Auto-pairing on join, scoped to `screen_share`

**Files:**
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs` (`AdmitViewerCoreAsync`, the pairing check around `:307-308`)
- Modify: `services/SonicRelay.Api/Endpoints/PairingEndpoints.cs:39` and `:64` (stale comments)
- Test: `tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: joining a `screen_share` session with a valid code creates an active `DevicePairing` (`PublisherDeviceId` = session source, `ViewerDeviceId` = joining device) when none exists. `broadcast` and `duplex` still return `not_paired`.

- [ ] **Step 1: Write the failing tests**

Append to `ScreenShareSessionTests.cs`:

```csharp
    [Fact]
    public async Task Joining_a_screen_share_session_creates_the_pairing()
    {
        var (_, sessionId, code) = await CreateScreenShareSessionAsync();
        var (viewer, viewerDeviceId) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var response = await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await HasActivePairingAsync(sessionId, viewerDeviceId));
    }

    [Fact]
    public async Task Rejoining_a_screen_share_session_does_not_duplicate_the_pairing()
    {
        var (_, sessionId, code) = await CreateScreenShareSessionAsync();
        var (viewer, viewerDeviceId) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        await viewer.PostAsJsonAsync("/api/sessions/join", new { code });
        await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(1, await CountActivePairingsAsync(sessionId, viewerDeviceId));
    }

    [Fact]
    public async Task Joining_a_broadcast_session_without_a_pairing_is_still_refused()
    {
        var (owner, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var created = await owner.PostAsJsonAsync("/api/sessions", new { maxViewers = 1, mode = SessionModes.Broadcast });
        var createdBody = await ReadJsonAsync(created);
        var sessionId = createdBody.GetProperty("id").GetGuid();
        var code = createdBody.GetProperty("code").GetString()!;
        var (viewer, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("not_paired", body.GetProperty("code").GetString());
        Assert.False(await HasActivePairingAsync(sessionId, viewerDeviceId));
    }

    [Fact]
    public async Task Joining_a_duplex_session_without_a_pairing_is_still_refused()
    {
        var (owner, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var created = await owner.PostAsJsonAsync("/api/sessions", new { maxViewers = 1, mode = SessionModes.Duplex });
        var createdBody = await ReadJsonAsync(created);
        var sessionId = createdBody.GetProperty("id").GetGuid();
        var code = createdBody.GetProperty("code").GetString()!;
        var (viewer, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await HasActivePairingAsync(sessionId, viewerDeviceId));
    }

    [Fact]
    public async Task The_viewer_limit_still_applies_to_screen_share_sessions()
    {
        var (_, _, code) = await CreateScreenShareSessionAsync(maxViewers: 1);
        var (first, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        var (second, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        Assert.Equal(HttpStatusCode.OK, (await first.PostAsJsonAsync("/api/sessions/join", new { code })).StatusCode);
        var response = await second.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
```

Add the two query helpers:

```csharp
    private async Task<bool> HasActivePairingAsync(Guid sessionId, Guid viewerDeviceId) =>
        await CountActivePairingsAsync(sessionId, viewerDeviceId) > 0;

    private async Task<int> CountActivePairingsAsync(Guid sessionId, Guid viewerDeviceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sourceDeviceId = await db.StreamSessions.Where(x => x.Id == sessionId)
            .Select(x => x.SourceDeviceId).SingleAsync();
        return await db.DevicePairings.CountAsync(x =>
            x.PublisherDeviceId == sourceDeviceId
            && x.ViewerDeviceId == viewerDeviceId
            && x.Status == DevicePairingStatuses.Active);
    }
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~Joining_a_screen_share_session_creates_the_pairing"`
Expected: FAIL — `403 not_paired`, and no pairing row.

- [ ] **Step 3: Create the pairing instead of refusing**

In `AdmitViewerCoreAsync`, replace the pairing check:

```csharp
        var isPublicRoomSession = session.SourceDeviceId == PublicRoomSeeder.VirtualPublisherDeviceId;
        if (!isPublicRoomSession && !await HasActivePairingAsync(db, session.SourceDeviceId, device.Id, ct))
        {
            // A screen session is entered with one secret: its join code. Holding a valid code
            // is what establishes the pairing, rather than the pairing being a prerequisite the
            // user would have to satisfy with a second code. The row is still created — it is
            // what carries revocation, the pairings listing and code-free rejoin — so the only
            // thing dropped is the extra step, not the record. Audio sessions are untouched:
            // there, a missing pairing is still a refusal.
            if (session.Mode != SessionModes.ScreenShare) return NotPaired();

            db.DevicePairings.Add(new DevicePairing
            {
                Id = Guid.NewGuid(),
                PublisherDeviceId = session.SourceDeviceId,
                ViewerDeviceId = device.Id,
                Status = DevicePairingStatuses.Active,
                CreatedAt = now,
                LastUsedAt = now
            });
        }
```

`now` is already in scope at the top of the method. Add `using SonicRelay.Domain.DeviceIdentities;` if not present. The `SaveChangesAsync` further down persists the pairing together with the participant, so a failure leaves neither behind.

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ScreenShareSessionTests"`
Expected: PASS, 16 tests.

- [ ] **Step 5: Correct the two comments the change invalidates**

In `services/SonicRelay.Api/Endpoints/PairingEndpoints.cs`, replace the comment above `CreateChallengeAsync`:

```csharp
    // No device-type check here: the "pairing:create" policy is the gate, and both device
    // types that hold that scope (windows_publisher and windows_desktop) may legitimately
    // issue a pairing code.
```

And the one above `CompleteAsync`:

```csharp
    // No device-type check here either, mirroring CreateChallengeAsync: "pairing:complete" is
    // held by flutter_viewer and windows_desktop, and both may legitimately redeem a code.
```

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add services/SonicRelay.Api/Endpoints/SessionEndpoints.cs services/SonicRelay.Api/Endpoints/PairingEndpoints.cs tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs
git commit -m "feat(sessions): create the pairing on screen_share join"
```

---

### Task 5: Metrics

**Files:**
- Modify: `services/SonicRelay.Api/Observability/SonicRelayMetrics.cs`
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs` (`CreateAsync`, `AdmitViewerCoreAsync`)
- Test: `tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-4.
- Produces: on `SonicRelayMetrics` — `ScreenShareSessionCreated()`, `ScreenShareJoinRejected(string reason)`, `ScreenShareAutoPairingCreated()`. Exposed at `/metrics` as `sonicrelay_screen_share_sessions_created_total`, `sonicrelay_screen_share_join_rejected_total{reason}`, `sonicrelay_screen_share_auto_pairings_created_total`.

- [ ] **Step 1: Write the failing test**

Append to `ScreenShareSessionTests.cs`:

```csharp
    [Fact]
    public async Task Screen_share_activity_is_counted_without_high_cardinality_labels()
    {
        var (_, _, code) = await CreateScreenShareSessionAsync();
        var (viewer, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        await viewer.PostAsJsonAsync("/api/sessions/join", new { code });
        var (rejected, rejectedDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        await rejected.PostAsJsonAsync("/api/sessions/join", new { code });

        var metrics = await _factory.CreateClient().GetStringAsync("/metrics");

        Assert.Contains("sonicrelay_screen_share_sessions_created_total", metrics);
        Assert.Contains("sonicrelay_screen_share_auto_pairings_created_total", metrics);
        Assert.Contains("sonicrelay_screen_share_join_rejected_total{reason=\"device_type\"}", metrics);
        Assert.DoesNotContain(rejectedDeviceId.ToString(), metrics);
    }
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~Screen_share_activity_is_counted"`
Expected: FAIL — none of the series exist.

- [ ] **Step 3: Add the instruments**

In `services/SonicRelay.Api/Observability/SonicRelayMetrics.cs`, alongside the existing fields:

```csharp
    private readonly Counter _screenShareSessionsCreated = Metrics.CreateCounter(
        "sonicrelay_screen_share_sessions_created_total",
        "Screen-share sessions created.");

    private readonly Counter _screenShareJoinRejected = Metrics.CreateCounter(
        "sonicrelay_screen_share_join_rejected_total",
        "Join attempts refused on a screen-share session, by reason.",
        new CounterConfiguration { LabelNames = ["reason"] });

    private readonly Counter _screenShareAutoPairings = Metrics.CreateCounter(
        "sonicrelay_screen_share_auto_pairings_created_total",
        "Device pairings created implicitly by a screen-share join.");
```

And the methods:

```csharp
    public void ScreenShareSessionCreated() => _screenShareSessionsCreated.Inc();

    /// <summary>Reason must be a bounded enum value — today only "device_type".</summary>
    public void ScreenShareJoinRejected(string reason) => _screenShareJoinRejected.WithLabels(reason).Inc();

    public void ScreenShareAutoPairingCreated() => _screenShareAutoPairings.Inc();
```

- [ ] **Step 4: Record the events**

`SonicRelayMetrics` is already registered in DI; add it as a parameter where needed.

In `CreateAsync`, add `SonicRelayMetrics metrics` to the parameter list and, right after `await db.SaveChangesAsync(ct);`:

```csharp
        if (session.Mode == SessionModes.ScreenShare) metrics.ScreenShareSessionCreated();
```

In `AdmitViewerCoreAsync`, add `SonicRelayMetrics metrics` to the parameter list (and pass it through from `AdmitViewerAsync`, `JoinAsync` and `JoinByIdAsync`, which also take it as an injected parameter). Then:

```csharp
        if (session.Mode == SessionModes.ScreenShare && device.DeviceType != DeviceTypes.WindowsDesktop)
        {
            metrics.ScreenShareJoinRejected("device_type");
            return DeviceTypeNotAllowed();
        }
```

and inside the auto-pairing branch, after `db.DevicePairings.Add(...)`:

```csharp
            metrics.ScreenShareAutoPairingCreated();
```

- [ ] **Step 5: Run the test and verify it passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~Screen_share_activity_is_counted"`
Expected: PASS.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj`
Expected: PASS. `WebRtcObservabilityTests` and `DataRetentionObservabilityTests` must be unaffected.

- [ ] **Step 7: Commit**

```bash
git add services/SonicRelay.Api/Observability/SonicRelayMetrics.cs services/SonicRelay.Api/Endpoints/SessionEndpoints.cs tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs
git commit -m "feat(observability): count screen-share sessions, refusals and auto-pairings"
```

---

### Task 6: Regression guard on the existing device types

**Files:**
- Test: `tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs`

**Interfaces:**
- Consumes: `ScopesInToken` from Task 1.
- Produces: nothing consumed by later tasks. This task exists to make the plan's central promise checkable by a machine rather than by inspection.

- [ ] **Step 1: Write the tests**

These must pass immediately — that is the point. If any fails, an earlier task broke something.

```csharp
    [Fact]
    public async Task Windows_publisher_scopes_are_unchanged()
    {
        var client = _factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        Assert.Equal(
            new[]
            {
                "device:manage", "device:read", "pairing:create", "pairing:revoke",
                "session:create", "session:end", "signaling:connect", "turn:credentials"
            },
            ScopesInToken(session.AccessToken).Order().ToArray());
    }

    [Fact]
    public async Task Flutter_viewer_scopes_are_unchanged()
    {
        var client = _factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        Assert.Equal(
            new[]
            {
                "device:manage", "device:read", "pairing:complete", "pairing:revoke",
                "session:join", "signaling:connect", "turn:credentials"
            },
            ScopesInToken(session.AccessToken).Order().ToArray());
    }

    [Fact]
    public async Task Broadcast_audio_defaults_are_unchanged()
    {
        var (owner, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var created = await owner.PostAsJsonAsync("/api/sessions", new { maxViewers = 1, mode = SessionModes.Broadcast });
        var sessionId = (await ReadJsonAsync(created)).GetProperty("id").GetGuid();

        var publisher = await GetParticipantAsync(sessionId, ParticipantRoles.Publisher);

        Assert.True(publisher.AudioSendAllowed);
        Assert.True(publisher.CanSendAudio);
        Assert.False(publisher.CanReceiveAudio);
    }

    [Fact]
    public async Task Duplex_audio_defaults_are_unchanged()
    {
        var (owner, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var created = await owner.PostAsJsonAsync("/api/sessions", new { maxViewers = 1, mode = SessionModes.Duplex });
        var sessionId = (await ReadJsonAsync(created)).GetProperty("id").GetGuid();

        var publisher = await GetParticipantAsync(sessionId, ParticipantRoles.Publisher);

        Assert.True(publisher.AudioSendAllowed);
        Assert.True(publisher.CanSendAudio);
        Assert.True(publisher.CanReceiveAudio);
    }
```

- [ ] **Step 2: Run them and verify they pass**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ScreenShareSessionTests"`
Expected: PASS, 21 tests. A failure here means an earlier task changed existing behavior — fix the production code, never the assertion.

- [ ] **Step 3: Commit**

```bash
git add tests/SonicRelay.Api.IntegrationTests/ScreenShareSessionTests.cs
git commit -m "test: guard the existing device types and session modes against regression"
```

---

### Task 7: Documentation and ADR

**Files:**
- Modify: `docs/protocol.md`
- Modify: `docs/device-identity.md`
- Create: `docs/adr/0008-screen-share-sessions.md`

**Interfaces:**
- Consumes: the finished contract from Tasks 1-5.
- Produces: nothing in code. This is the artifact the client repository reads to implement against.

- [ ] **Step 1: Update the device-type table in `docs/protocol.md`**

The sentence reading "Valid `deviceType`/`platform` pairs are `windows_publisher`/`windows` and `flutter_viewer`/`android|ios`." becomes:

```markdown
Valid `deviceType`/`platform` pairs are `windows_publisher`/`windows`,
`windows_desktop`/`windows` and `flutter_viewer`/`android|ios`. `windows_desktop` is the
Windows screen-sharing app, which both publishes and views with one identity and therefore
carries the union of the publisher and viewer scopes. Revoked devices cannot bootstrap new
tokens or create, join or connect to sessions.
```

- [ ] **Step 2: Add `screen_share` to the mode table in `docs/protocol.md`**

Add a row to the table under "Create request":

```markdown
| `screen_share` | The publisher shares a screen and its system audio; the other participants only receive. Audio permissions match `broadcast`. |
```

- [ ] **Step 3: Document the screen-share join in `docs/protocol.md`**

Add a section after "Bidirectional audio (duplex sessions)":

```markdown
## Screen-share sessions

A `screen_share` session carries a video track (the publisher's screen) plus, optionally, the
publisher's system audio. As with audio, the API neither sees nor forwards the media: it
authenticates, authorizes, tracks presence and routes signaling.

Two rules apply only to this mode:

- **Only `windows_desktop` devices may join.** Any other device type joining a `screen_share`
  session gets `403 { "code": "device_type_not_allowed" }`, whether or not it is paired. A
  client that cannot render video must not be handed an offer containing a video m-line.
- **The join code establishes the pairing.** A `windows_desktop` device presenting a valid
  code is admitted even with no prior `DevicePairing`, and the pairing is created as part of
  the join. The record still exists, so revocation, `GET /api/devices/{deviceId}/pairings`
  and code-free rejoin through `/discoverable` all keep working.

`broadcast` and `duplex` are unaffected by both rules: they still require a pairing
established beforehand through `POST /api/pairings/challenges` and `POST /api/pairings/complete`.

The consequence, stated plainly: in a screen session the six-character code is the only
credential. It has a short TTL, it can be rotated at any time with
`POST /api/sessions/{sessionId}/rotate-code`, and `GET /api/sessions/{sessionId}/participants`
lets the publishing app show who is watching for as long as the session lasts.
```

- [ ] **Step 4: Add the scope row in `docs/device-identity.md`**

Add `windows_desktop` wherever the file lists device types and their scopes, with the ten
scopes: `device:read`, `device:manage`, `pairing:create`, `pairing:complete`,
`pairing:revoke`, `session:create`, `session:join`, `session:end`, `signaling:connect`,
`turn:credentials`.

- [ ] **Step 5: Write the ADR**

Create `docs/adr/0008-screen-share-sessions.md`:

```markdown
# 8. Screen-share sessions

Date: 2026-08-23

## Status

Accepted.

## Context

A new Windows app (SonicDesktopRelay) shares a screen with other Windows machines, using the
same control plane as the audio publisher. It both publishes and views with one identity.
This is the first slice of issue #30 — video and audio, without remote control.

Three facts constrained the design. `windows_publisher` holds no `session:join` or
`pairing:complete` scope, so no existing type can do both jobs. The Flutter viewer cannot
render video, so it must never receive an offer with a video m-line. And the product owner
asked for a single code: sharing the join code should be all it takes.

## Decision

Add a device type `windows_desktop` carrying the union of the publisher and viewer scopes,
rather than widening `windows_publisher`.

Add a session mode `screen_share`, so the backend and the clients can recognise a screen
session without parsing SDP — which ADR 0001 forbids.

Refuse admission to a `screen_share` session for any device type other than
`windows_desktop`, in the server, before the pairing check.

For `screen_share` only, create the `DevicePairing` during join instead of requiring one
beforehand. `broadcast` and `duplex` keep the prior-pairing requirement unchanged.

## Consequences

The audio publisher and the Flutter viewer are untouched: same scopes, same modes, same
responses, guarded by explicit regression tests.

In a screen session the six-character join code becomes the only credential. Mitigations:
short TTL, rotation on demand, and a participants list the publishing app displays for the
whole session. Explicit host approval was designed and deliberately not implemented; adding
it later means a pending participant state and two signaling frames.

The pairing entity survives as the record of who was granted access, so revocation, the
pairings listing and code-free rejoin keep working for screen sessions too.

Video will consume far more coturn bandwidth than audio. Direct-versus-relay metrics already
exist; per-device quotas are deliberately left to a later phase.
```

- [ ] **Step 6: Verify the docs match the code**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj`
Expected: PASS. Then re-read the three documented error codes (`device_type_not_allowed`,
`not_paired`, `invalid_session_mode`) against the strings in `SessionEndpoints.cs` and confirm
they match character for character.

- [ ] **Step 7: Commit**

```bash
git add docs/protocol.md docs/device-identity.md docs/adr/0008-screen-share-sessions.md
git commit -m "docs: document screen_share sessions, windows_desktop and ADR 0008"
```

---

## Done when

- `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj` passes, with no pre-existing test edited.
- A `windows_desktop` device can create a `screen_share` session and a second one can join with only the code.
- A `flutter_viewer` cannot join a `screen_share` session even when paired.
- `broadcast` and `duplex` refuse an unpaired join exactly as before, creating no pairing.
- `/metrics` exposes the three new series with no device id, session id or IP in any label.
