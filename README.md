# RelayControl

Shared control plane for SonicRelay and FrameRelay. RelayControl provides device identity, pairing, sessions, join codes, authenticated WebSocket signaling and TURN credential issuance using ASP.NET Core Minimal API, PostgreSQL and Redis. WebRTC media stays between clients, directly or through coturn; this API never carries, transcodes or stores the media itself.

## Project suite

| Project | Repository | Stack | Responsibility |
| --- | --- | --- | --- |
| RelayControl | [dotnet_SonicRelay](https://github.com/vitorhugo-dotnet/dotnet_SonicRelay) | .NET 10, ASP.NET Core, PostgreSQL, Redis | Shared device identity, pairing, sessions, join codes, authorization and signaling control plane. |
| SonicRelay Mobile Viewer | [flutter_mobile-web_SonicRelay](https://github.com/vitorhugo-dotnet/flutter_mobile-web_SonicRelay) | Flutter, `flutter_webrtc` | Join SonicRelay audio sessions and play WebRTC audio. |
| SonicRelay Desktop Publisher | [desktop_dotnet_SonicRelay](https://github.com/vitorhugo-dotnet/desktop_dotnet_SonicRelay) | C#/.NET, Avalonia, system-audio capture, WebRTC | Capture system audio and publish it to SonicRelay viewers. |
| FrameRelay | [dotnet_SonicDesktopRelay](https://github.com/vitorhugo-dotnet/dotnet_SonicDesktopRelay) | C#/.NET, Avalonia, Windows Graphics Capture, FFmpeg, WebRTC | Share a Windows screen and system audio with other Windows machines. |
| SonicRelay Landing Page | [react_landpage_SonicRelay](https://github.com/vitorhugo-dotnet/react_landpage_SonicRelay) | React, TypeScript, Vite, Tailwind CSS | Public marketing site for SonicRelay. |

This repository contains only the shared backend/control plane and its infrastructure.

## Current status

| Area | Status | Current implementation |
| --- | --- | --- |
| Device identity | Implemented | Devices bootstrap a persistent, HMAC-hashed credential and exchange it for short-lived `DeviceBearer` JWTs; no human account or password exists. See [device identity](docs/device-identity.md). |
| Device pairing | Implemented | Devices establish revocable pairings through short-lived challenges/codes, with mode-specific rules such as code-driven pairing for FrameRelay screen-share sessions. |
| Sessions | Implemented | Create, list, read, join, rotate code, end and background expiry/cleanup, all owned by device identity. |
| Audio session modes | Implemented | `broadcast` provides one-way audio and `duplex` lets authorized participants publish and receive audio. The API authorizes capabilities and routes renegotiation while media stays between clients. See [ADR 0007](docs/adr/0007-duplex-audio-sessions.md). |
| Screen-share sessions | Implemented | `screen_share` sessions authorize `windows_desktop` peers for video plus optional system audio while keeping media outside the API. See the [client integration protocol](docs/protocol.md#screen-share-sessions). |
| WebSocket signaling | Implemented | Authenticated participant validation and in-process, participant-targeted routing. |
| Device revocation | Implemented | `POST /api/devices/revoke` and credential rotation (`POST /api/devices/rotate-credential`); no separate account-deletion flow exists since devices are not owned by a human account. |
| Data retention | Implemented | Everything collected is hard-deleted automatically well inside 90 days, and device identities rotate to a new `deviceId` before the ceiling. See [data retention](docs/data-retention.md). |
| Observability | Implemented | Prometheus `/metrics`, client WebRTC stats ingestion (`POST /api/webrtc/stats`), structured signaling logs, Grafana dashboard and alerts. See [observability](docs/observability.md). |
| PostgreSQL | Implemented | Device-identity, pairing, session, participant and signaling-event schema plus migrations. |
| Redis | Implemented | Expiring HMAC-derived session-code lookup. |
| WebRTC media | Client responsibility | No media capture, transcoding or relay is implemented in this API. |
| CI/CD | Implemented with scope noted | GitHub Actions builds/tests/publishes and deploys the API-only Compose stack over SSH. |

ASP.NET Core Identity (email/password accounts, `/register`, `/login`, `/refresh`, admin/self-service account deletion) was removed in issue #26 Phase 4 once the clients migrated to device identity; there is no human user account model or admin user-management panel in this API (see [ADR 0006](docs/adr/0006-remove-identity.md)).

See the [client integration protocol](docs/protocol.md) for exact routes and WebRTC signaling flows, the [beginner guide](docs/beginner-guide.md) for a plain-language introduction, [Security](docs/security.md) for implemented controls and known gaps, and [data retention](docs/data-retention.md) for what is stored, for how long, and what deletes it.

### Audio direction

An audio session fixes its direction at creation. `broadcast` (the default, and what a client
that sends no `mode` gets) keeps the original one-way behavior. `duplex` lets authorized
participants send and receive on the same WebRTC connection, for intercom or voice-call
scenarios. Permission to publish is a backend decision — a client can announce that it intends
to send audio, never that it is allowed to — and the session's own device can revoke it per
participant at any time. RelayControl still never receives, mixes, transcodes or stores audio;
see the [client integration protocol](docs/protocol.md#bidirectional-audio-duplex-sessions).

### Pairing authorization

A new viewer participant normally needs both an active DevicePairing to the session's
source device and the current session join code. RelayControl deliberately returns
the invalid/expired-code response when either condition is absent. Existing
participants may reconnect after pairing revocation until the session ends. `screen_share`
sessions use their documented code-driven pairing flow for `windows_desktop` devices.

## Quick start

Requirements: .NET 10 SDK, PostgreSQL and Redis.

```bash
dotnet restore SonicRelay.sln
dotnet ef database update \
  --project src/SonicRelay.Infrastructure/SonicRelay.Infrastructure.csproj \
  --startup-project services/SonicRelay.Api/SonicRelay.Api.csproj
dotnet run --project services/SonicRelay.Api/SonicRelay.Api.csproj
```

Health endpoints:

```bash
curl http://localhost:8080/health/live
curl http://localhost:8080/health/ready
```

Docker development stack:

The root `Dockerfile` is the canonical image definition. Run `docker build .` from the repository root; it publishes `services/SonicRelay.Api/SonicRelay.Api.csproj` using a multi-stage, non-root runtime image. Compose and CI/CD use the same Dockerfile and project path.

FrameRelay Discord launch intents use `LAUNCHINTENTS__SERVICETOKEN` and `LAUNCHINTENTS__PUBLICBASEURL` in `infra/.env.example`. Configure the matching service token only in the separate bot deployment; use a strong random value in production. The API stores only hashes of one-time launch capabilities. Public `/open/...` routes contain those short-lived capabilities, so reverse-proxy access logs must redact that route's final path segment.

```bash
cp infra/.env.example infra/.env
docker compose \
  --env-file infra/.env \
  -f infra/compose.yml \
  -f infra/compose.dev.yml \
  --profile dev \
  up --build
```

Run the API integration/E2E tests:

```bash
dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj
```

Validate the real device-identity, session, and WebSocket signaling flow without media clients using the [fake signaling client](tools/SonicRelay.SignalingClient/README.md).

## Configuration

Set the following high-entropy secrets in production deployments (outside Git):

| Secret | Purpose |
| --- | --- |
| `Sessions:CodeHmacKey` | Server-side pepper for hashing session join codes. |
| `DeviceIdentity:CredentialHmacKey` | Server-side pepper for hashing device credential secrets. |
| `DeviceIdentity:PairingCodeHmacKey` | Server-side pepper for hashing pairing codes. |
| `DeviceIdentity:TokenSigningKey` | Symmetric signing key for `DeviceBearer` JWTs. |

See [device identity configuration](docs/device-identity.md#configuration) for details on the `DeviceIdentity:*` keys.

`DeviceIdentity:TokenSigningKey` (and the other `DeviceIdentity:*` keys above) are required in any real deployment: sessions, signaling, TURN credential issuance, device bootstrap and pairing all authenticate exclusively via `DeviceBearer` and have no fallback authentication path.

## Documentation

- [Architecture](docs/architecture.md)
- [HTTP, WebSocket and WebRTC client integration protocol](docs/protocol.md)
- [Guia para leigos: WebSocket, WebRTC, Signaling, Opus e arquitetura](docs/beginner-guide.md)
- [Security](docs/security.md)
- [VPS deployment over SSH](docs/deployment-vps-ssh.md)
- [Product naming](docs/naming.md)
- [Architecture decision records](docs/adr/)

## CI/CD summary

`.github/workflows/vps-ci-cd.yml` runs build and tests on pull requests and pushes. Non-PR runs publish immutable `sha-<commit>` images to GHCR; `main` also publishes `latest`. A push to `main`, or a manual run with deployment enabled, copies `deploy/docker-compose.prod.yml` and `deploy/deploy.sh` to the VPS and starts the API image over SSH.

The automated deployment Compose file contains only RelayControl's API process. PostgreSQL, Redis, coturn and reverse proxy must already be reachable/configured, or operators must deploy the separate full stack from `infra/`. Details and required secrets are in the [deployment guide](docs/deployment-vps-ssh.md).

## License

See [LICENSE](LICENSE).

### Feature switches

RelayControl uses Microsoft.FeatureManagement. `FeatureManagement__PublicRooms`, `FeatureManagement__DiscordActivity`, `FeatureManagement__DuplexAudio` and `FeatureManagement__ScreenShare` default to `true`; explicit `false` disables new work. Public-room and Activity routes return 404, disabled session modes return 409 with `feature_disabled`. Session end, credentials, cleanup, TURN and generic signaling remain available. Public rooms also require `PublicRoom__Enabled=true`. Docker environment changes require `docker compose up -d --force-recreate api`; restarting a container does not reload its environment. `FeatureManagement__DiscordWebSocketMedia` defaults to `false`.

### Discord WebSocket media rollout

See [media protocol](docs/protocol/discord-media-v1.md). Existing RTC publishers/viewers, TURN and generic signaling keep their transport. Media uses a separate `media-relay` process that forwards already encoded H.264/Opus with bounded queues, without transcoding. This service is single-instance; do not load-balance one session across instances.

Set a random `MediaRelay__ServiceToken` in the API deployment `.env`; Compose passes it only to the API and media relay. Set `MediaRelay__PublicBaseUrl=wss://media.example.com/ws/media` to the public upload endpoint. Proxy that host to `sonicrelay-media:8080`, preserving `/ws/media`, with WebSocket Upgrade headers and timeouts longer than 30 seconds. Do not expose `/api/internal/media-relay/*` through a separate public proxy rule; it requires a distinct service credential.

New transport is disabled by default. To roll out, set `COMPOSE_PROFILES=media`, `FeatureManagement__DiscordWebSocketMedia=true`, and keep `FeatureManagement__DiscordActivity=true` and `FeatureManagement__ScreenShare=true`. Use the API and media images from the same commit (`IMAGE` and `MEDIA_IMAGE`); CI publishes separate SHA tags and leaves existing API image selection intact. Recreate both containers with `docker compose -f docker-compose.prod.yml --profile media up -d --force-recreate api media-relay`.

On the publisher process, set `FRAMERELAY_WEBSOCKET_MEDIA_ENABLED=true` and restart FrameRelay. This is a process environment variable, not a desktop `.env` loader. When false or missing, there are no upload requests or extra encoders. H.264 output is reused; RTC AV1 uses an independent H.264 encoder for Activity without changing RTC codec selection.

Configure Discord Activity URL Mapping `/relay` to the existing API, and `/media` to the public media host. The Activity connects to `/media/ws/media` on its mapped origin; it does not use a direct desktop upload URL. It requires supported VideoDecoder/AudioDecoder and checks the actual H.264 profile and Opus configuration; unavailable codecs produce a desktop-viewer message. The play button resumes audio after a user gesture.

Disabling the media flag stops new grants and denies lease renewal. Leases expire after 30 seconds and renew every 10 seconds; disconnect/release and grant cleanup free Activity capacity without disconnecting RTC publishers. Live Discord verification must check pixels, audible synchronized audio, late join/keyframe recovery, reconnect, session end and a simultaneous desktop RTC viewer before enabling production rollout.

Local browser fixture: run `dotnet run --project tests/SonicRelay.MediaRelay.BrowserFixture` and the Activity Vite server on port 5173. Open `http://127.0.0.1:5173/tests/browser-media-fixture.html` and click Run. It encodes generated colors and a tone, forwards via real WebSockets, checks actual decoded canvas pixels and PCM energy, and closes resources. The fixture uses a fake grant API on localhost only; production never installs that project.
