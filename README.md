# SonicRelay

Backend/control plane for low-latency audio streaming between a Windows publisher and mobile viewers. The API uses ASP.NET Core Minimal API, PostgreSQL, Redis and authenticated WebSocket signaling; WebRTC/Opus media stays between clients, directly or through coturn.

## Project suite

| Project | Repository | Stack | Responsibility |
| --- | --- | --- | --- |
| Backend API | [dotnet_SonicRelay](https://github.com/vitorhugo-dotnet/dotnet_SonicRelay) | .NET 10, ASP.NET Core, PostgreSQL, Redis | Device identity, pairing, sessions, join codes and signaling. |
| Mobile Viewer | [flutter_SonicRelay](https://github.com/vitorhugo-dotnet/flutter_SonicRelay) | Flutter, `flutter_webrtc` | Join a session and play WebRTC audio. |
| Windows Publisher | [windows_SonicRelay](https://github.com/vitorhugo-dotnet/windows_SonicRelay) | C#/.NET Desktop, WASAPI, WebRTC | Capture system audio and publish it to viewers. |
| Landing Page | [react_landpage_SonicRelay](https://github.com/vitorhugo-dotnet/react_landpage_SonicRelay) | React, TypeScript, Vite, Tailwind CSS | Public marketing site for the suite. |

This repository contains only the backend and its infrastructure.

## Current status

| Area | Status | Current implementation |
| --- | --- | --- |
| Device identity | Implemented | Devices bootstrap a persistent, HMAC-hashed credential and exchange it for short-lived `DeviceBearer` JWTs; no human account or password exists. See [device identity](docs/device-identity.md). |
| Device pairing | Implemented | A Windows publisher issues a short-lived pairing challenge/QR code; a Flutter viewer completes it to establish a revocable device pairing. |
| Sessions | Implemented | Create, list, read, join, rotate code, end and background expiry/cleanup, all owned by device identity. |
| Bidirectional audio | Implemented | Sessions are created as `broadcast` (default, one-way) or `duplex`; the API authorizes which participants may publish audio, propagates capability and mute changes, and routes renegotiation. Media stays between clients. See [ADR 0007](docs/adr/0007-duplex-audio-sessions.md). |
| WebSocket signaling | Implemented | Authenticated participant validation and in-process, participant-targeted routing. |
| Device revocation | Implemented | `POST /api/devices/revoke` and credential rotation (`POST /api/devices/rotate-credential`); no separate account-deletion flow exists since devices are not owned by a human account. |
| Data retention | Implemented | Everything collected is hard-deleted automatically well inside 90 days, and device identities rotate to a new `deviceId` before the ceiling. See [data retention](docs/data-retention.md). |
| Observability | Implemented | Prometheus `/metrics`, client WebRTC stats ingestion (`POST /api/webrtc/stats`), structured signaling logs, Grafana dashboard and alerts. See [observability](docs/observability.md). |
| PostgreSQL | Implemented | Device-identity, pairing, session, participant and signaling-event schema plus migrations. |
| Redis | Implemented | Expiring HMAC-derived session-code lookup. |
| WebRTC media | Client responsibility | No media capture, transcoding or relay is implemented in this API. |
| CI/CD | Implemented with scope noted | GitHub Actions builds/tests/publishes and deploys the API-only Compose stack over SSH. |

ASP.NET Core Identity (email/password accounts, `/register`, `/login`, `/refresh`, admin/self-service account deletion) was removed in issue #26 Phase 4 once both clients migrated to device identity; there is no human user account model or admin user-management panel in this API (see [ADR 0006](docs/adr/0006-remove-identity.md)).

See the [client integration protocol](docs/protocol.md) for exact routes and WebRTC signaling flows, the [beginner guide](docs/beginner-guide.md) for a plain-language introduction, [Security](docs/security.md) for implemented controls and known gaps, and [data retention](docs/data-retention.md) for what is stored, for how long, and what deletes it.

### Audio direction

A session fixes its audio direction at creation. `broadcast` (the default, and what a client
that sends no `mode` gets) keeps the original one-way behavior. `duplex` lets authorized
participants send and receive on the same WebRTC connection, for intercom or voice-call
scenarios. Permission to publish is a backend decision — a client can announce that it intends
to send audio, never that it is allowed to — and the session's own device can revoke it per
participant at any time. The API still never receives, mixes, transcodes or stores audio; see
the [client integration protocol](docs/protocol.md#bidirectional-audio-duplex-sessions).

### Pairing authorization

A new viewer participant needs both an active DevicePairing to the session's
source device and the current session join code. The API deliberately returns
the invalid/expired-code response when either condition is absent. Existing
participants may reconnect after pairing revocation until the session ends.

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

Validate the real device-identity, session, and WebSocket signaling flow without audio or WebRTC clients using the [fake signaling client](tools/SonicRelay.SignalingClient/README.md).

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

The automated deployment Compose file contains only the API. PostgreSQL, Redis, coturn and reverse proxy must already be reachable/configured, or operators must deploy the separate full stack from `infra/`. Details and required secrets are in the [deployment guide](docs/deployment-vps-ssh.md).

## License

See [LICENSE](LICENSE).
