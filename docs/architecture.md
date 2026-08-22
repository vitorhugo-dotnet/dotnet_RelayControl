# Architecture

## System boundary

SonicRelay is a control plane. It authenticates users, persists session state, issues temporary join codes and routes WebRTC signaling messages. It does not capture, encode, buffer, transcode or relay audio. Media flows directly between clients when possible and through coturn when NAT traversal requires it.

```mermaid
flowchart TD
    subgraph Windows["Windows PC - Publisher"]
        WAPP["C#/.NET Desktop App"]
        CAP["WASAPI Loopback Capture"]
        OPUS["Opus Encoder"]
        WRTC_P["WebRTC Publisher"]
        WAPP --> CAP --> OPUS --> WRTC_P
    end

    subgraph Mobile["Mobile - Viewer"]
        FAPP["Flutter App"]
        WRTC_V["WebRTC Receiver"]
        PLAYER["Audio Player"]
        FAPP --> WRTC_V --> PLAYER
    end

    subgraph Edge["Reverse proxy / DNS"]
        HTTPS["HTTPS/WSS API"]
        DNS["DNS-only TURN/STUN"]
    end

    subgraph VPS["VPS"]
        API["SonicRelay API"]
        DB[("PostgreSQL")]
        REDIS[("Redis")]
        TURN["coturn"]
        API --> DB
        API --> REDIS
    end

    WAPP <-->|"auth and signaling"| HTTPS
    FAPP <-->|"auth and signaling"| HTTPS
    HTTPS --> API
    WRTC_P <-->|"WebRTC media"| WRTC_V
    WRTC_P -.->|"relay fallback"| DNS
    WRTC_V -.->|"relay fallback"| DNS
    DNS --> TURN
```

## Components

- `services/SonicRelay.Api`: Minimal API composition, rate limits, health checks, endpoint handlers, WebSocket signaling and session cleanup.
- `src/SonicRelay.Domain`: device-identity credential/pairing, session, participant and signaling-event models — see `docs/device-identity.md`. `StreamSession.SourceDeviceId` and `SessionParticipant.DeviceId` reference `DeviceIdentity`. ASP.NET Core Identity and the old owner-scoped `Device` entity were removed in Phase 4 of issue #26 (see [ADR 0006](adr/0006-remove-identity.md)); `SonicRelay.Domain.Devices` now only holds the `DeviceTypes`/`DevicePlatforms` constants shared with `DeviceIdentity`.
- `src/SonicRelay.Application`: abstractions for session-code storage and live connection routing.
- `src/SonicRelay.Infrastructure`: EF Core/PostgreSQL persistence, Redis session-code storage and the in-memory connection registry.
- `infra`: development and full-stack production Compose definitions, nginx and coturn configuration.
- `deploy`: API-only production Compose file and SSH deployment script used by GitHub Actions.

The signaling registry is process-local. Multiple API replicas do not share live WebSocket registrations, so horizontal scaling requires sticky routing or a distributed signaling backplane.

## Primary flow

Each device bootstraps its own persistent credential and exchanges it for a short-lived `DeviceBearer` JWT; there is no human account and no ASP.NET Core Identity. Session creation and join validate the caller's own device identity from its `DeviceBearer` token; nothing about which device is calling is client-asserted.

Device pairing (Publisher issues a pairing challenge/QR, Viewer completes it via `POST /api/pairings/complete`) is how a Viewer discovers and trusts a Publisher's devices, and it **is** a prerequisite for joining a session: `POST /api/sessions/join` requires an active `DevicePairing` between the session's publisher device and the joining device, in addition to a valid join code. A device with a valid `DeviceBearer` but no active pairing with the publisher gets the same "invalid or expired code" response as a bad code, so join never leaks whether a session exists.

```mermaid
sequenceDiagram
    autonumber
    participant W as Windows Publisher
    participant API as SonicRelay API
    participant R as Redis
    participant DB as PostgreSQL
    participant F as Flutter Viewer
    participant TURN as coturn

    W->>API: POST /api/devices/bootstrap (windows_publisher)
    API-->>W: device ID + one-time credential secret
    W->>API: POST /api/devices/token
    API-->>W: short-lived DeviceBearer
    W->>API: POST /api/pairings/challenges
    API-->>W: pairing code / QR payload
    W->>API: POST /api/sessions
    API->>DB: create session + publisher participant
    API->>R: store HMAC-derived code lookup with TTL
    API-->>W: session + temporary code
    W->>API: GET /ws/signaling?sessionId=...

    F->>API: POST /api/devices/bootstrap (flutter_viewer)
    API-->>F: device ID + one-time credential secret
    F->>API: POST /api/devices/token
    API-->>F: short-lived DeviceBearer
    F->>API: POST /api/pairings/complete (pairing code)
    API->>DB: create active DevicePairing
    F->>API: POST /api/sessions/join
    API->>R: resolve code
    API->>DB: verify active pairing exists; reject with invalid/expired code if not; else create viewer participant
    API-->>F: session
    F->>API: GET /ws/signaling?sessionId=...

    W->>API: webrtc.offer targeted to viewer
    API-->>F: webrtc.offer
    F->>API: webrtc.answer targeted to publisher
    API-->>W: webrtc.answer
    W-.->F: direct WebRTC audio when possible
    W-.->TURN: TURN relay fallback
    F-.->TURN: TURN relay fallback
```

See [device identity](device-identity.md) for the pairing flow's own sequence.

## Persistence model

```mermaid
erDiagram
    DEVICE_IDENTITY ||--o{ STREAM_SESSION : publishes
    DEVICE_IDENTITY ||--o{ SESSION_PARTICIPANT : connects_as
    DEVICE_IDENTITY ||--o{ PAIRING_CHALLENGE : issues
    DEVICE_IDENTITY ||--o{ DEVICE_PAIRING : publisher_or_viewer
    STREAM_SESSION ||--o{ SESSION_PARTICIPANT : has
    STREAM_SESSION ||--o{ SIGNALING_EVENT : may_log

    DEVICE_IDENTITY {
        uuid id PK
        string name
        string deviceType
        string platform
        string credentialSecretHash
        int credentialVersion
        string status
        datetime lastSeenAt
        datetime revokedAt
    }
    PAIRING_CHALLENGE {
        uuid id PK
        uuid publisherDeviceId
        string codeHash
        datetime expiresAt
        int maxAttempts
        int attemptCount
        datetime consumedAt
    }
    DEVICE_PAIRING {
        uuid id PK
        uuid publisherDeviceId
        uuid viewerDeviceId
        string status
        datetime lastUsedAt
        datetime revokedAt
    }
    STREAM_SESSION {
        uuid id PK
        uuid sourceDeviceId
        string status
        string mode
        int maxViewers
        datetime codeExpiresAt
    }
    SESSION_PARTICIPANT {
        uuid id PK
        uuid sessionId
        uuid deviceId
        string role
        string status
        string connectionId
        bool audioSendAllowed
        bool canSendAudio
        bool canReceiveAudio
        bool audioMuted
    }
    DEVICE_PAIRING {
        uuid id PK
        uuid publisherDeviceId
        uuid viewerDeviceId
        string status
    }
    SIGNALING_EVENT {
        uuid id PK
        uuid sessionId
        string eventType
        uuid fromParticipantId
        uuid toParticipantId
    }
```

`StreamSession` and `SessionParticipant` are authorized through device identity: a session has a `sourceDeviceId`, and participants have a `deviceId`; neither stores an application-user owner. `DevicePairing` records the durable publisher/viewer relationship, and a viewer must have an active `DevicePairing` with the session's publisher device before `POST /api/sessions/join` will admit it — the legacy owner-scoped `Device` entity and its `ownerUserId` column were removed in Phase 4 (see [ADR 0006](adr/0006-remove-identity.md)), so there is no separate ownership model to reconcile with. EF Core maps these tables but does not declare relational foreign-key navigation constraints in `AppDbContext`; pairing and membership checks are enforced by handlers.

The absent foreign keys are also what makes device-identity rotation possible: a device's rows are re-pointed at a replacement identity and the old row is deleted in the same transaction, which a database-level `ON DELETE CASCADE` would turn into a teardown of the live session it was meant to preserve. Referential integrity during deletion is therefore enforced by the retention sweep's ordered graph delete plus an orphan sweep, not by the schema. Every table above carries a collection timestamp, and nothing in it survives past the retention ceiling — see [data retention](data-retention.md).

`StreamSession.mode` is `broadcast` or `duplex` and fixes the session's audio direction at creation; `SessionParticipant.audioSendAllowed` is the backend's decision about whether that participant may publish audio, and the three flags beside it are client-declared state the server clamps, stores and rebroadcasts. See [ADR 0007](adr/0007-duplex-audio-sessions.md).

## Session and peer topology

A device only sees sessions it publishes or participates in. A publisher is expected to create one peer connection per viewer.

In a `duplex` session the same peer connections carry audio in both directions (`sendrecv`) instead of only publisher-to-viewer; the topology below is unchanged, only the direction of the media on each connection is.

```mermaid
flowchart LR
    subgraph A["Devices A"]
        A_PC["Publisher device"] --> A_SESSION["Session"]
        A_PHONE["Viewer device"] --> A_SESSION
    end
    subgraph B["Devices B"]
        B_PC["Publisher device"] --> B_SESSION["Session"]
        B_PHONE["Viewer device"] --> B_SESSION
    end
    A_SESSION -. "isolated authorization scope" .- B_SESSION
```

```mermaid
flowchart TD
    PC["Windows Publisher"]
    V1["Viewer 1"]
    V2["Viewer 2"]
    V3["Viewer 3"]
    PC <-->|"PeerConnection 1"| V1
    PC <-->|"PeerConnection 2"| V2
    PC <-->|"PeerConnection 3"| V3
```

## Decision records

- [ADR 0001: Keep media outside the backend](adr/0001-control-plane-only.md)
- [ADR 0002: Use Identity opaque bearer tokens](adr/0002-identity-bearer-tokens.md) — superseded by ADR 0006
- [ADR 0003: Split durable and ephemeral storage](adr/0003-postgresql-and-redis-storage.md)
- [ADR 0004: Use authenticated WebSocket signaling](adr/0004-authenticated-websocket-signaling.md)
- [ADR 0005: Symmetric device credentials with a parallel DeviceBearer scheme](adr/0005-device-identity-credentials.md) — extended in Phase 2 to sessions, signaling and TURN credential issuance
- [ADR 0006: Remove ASP.NET Core Identity and owner-scoped Device CRUD](adr/0006-remove-identity.md)
- [ADR 0007: Model bidirectional audio as a session mode with backend-owned publish permission](adr/0007-duplex-audio-sessions.md)
