# Security

This document separates controls present in the current code from work still required for production hardening.

## Implemented controls

### Device identity and tokens

There is no human user account, password, email or admin role anywhere in the
API (removed in issue #26 Phase 4; see [ADR 0006](adr/0006-remove-identity.md)).
The only authentication scheme is `DeviceBearer`:

- A device bootstraps a high-entropy credential secret once (`POST
  /api/devices/bootstrap`); only its HMAC-SHA-256 hash, keyed by
  `DeviceIdentity:CredentialHmacKey`, is persisted.
- That secret is exchanged for a short-lived `DeviceBearer` JWT (`POST
  /api/devices/token`), signed with `DeviceIdentity:TokenSigningKey` and
  valid for `DeviceIdentity:AccessTokenMinutes` (default 5).
- Every scoped request re-checks the device's live status and credential
  version against the database (`DeviceScopeAuthorizationHandler`), so
  rotation (`POST /api/devices/rotate-credential`) and revocation (`POST
  /api/devices/revoke`) take effect immediately despite the JWT being
  self-contained.
- Protected API groups and the WebSocket endpoint require a valid
  `DeviceBearer` token; there is no fallback authentication path.

### Authorization and isolation

- Session creation requires a `session:create`-scoped `DeviceBearer` token; the caller's own authenticated device is always the session's source device.
- Session reads (`GET /api/sessions/active`, `GET /api/sessions/{id}`) are limited to sessions where the caller's device is the source or a participant and return `404` otherwise.
- End and code rotation operations require a `session:end`-scoped token and require the caller's device to be the session's source device.
- Join requires a `session:join`-scoped token, an active `DevicePairing` to the session's source device, and enforces the session viewer limit; the joining device is always the caller's own, never a client-supplied one.
- WebSocket upgrade requires a `signaling:connect`-scoped token and a matching session participant record for the caller's device.
- Signaling routing always uses the authenticated participant as `from` and restricts recipients to the same session.
- Publishing audio requires the backend flag `audioSendAllowed` on the participant row. It is seeded from the session mode and role on join, and afterwards only the session's source device can change it, through `POST /api/sessions/{id}/participants/{participantId}/audio-permission` (a `session:end`-scoped, duplex-only route). A client declaring `canSendAudio: true` without it is refused with `audio_send_not_authorized`, and the permission is re-read from the database on every such message, so a revocation applies to a socket that is already open.

A new viewer participant needs both an active `DevicePairing` to the session's
source device and the current session join code. The API deliberately returns
the invalid/expired-code response when either condition is absent, so a
caller cannot tell a nonexistent session from one it just isn't paired with.
Existing participants may reconnect after pairing revocation until the
session ends; revocation only blocks new joins.

The named policies `session:create`, `session:join`, `session:end`, `signaling:connect`, `turn:credentials`, `device:read`, `device:manage`, `pairing:create`, `pairing:complete` and `pairing:revoke` each require a `DeviceBearer` token carrying the matching scope; `DeviceScopeAuthorizationHandler` also re-checks the device's live status and credential version against the database on every request, so revocation and credential rotation take effect immediately. `DeviceAuthenticated` is a scope-less variant of the same check, used by read-only routes that need no capability beyond an active device.

Because the API never parses SDP ([ADR 0001](adr/0001-control-plane-only.md)), "only authorized participants publish audio" is enforced at the signaling layer, not in the media: the server decides and broadcasts who may publish, and clients must reject inbound audio from a peer whose latest server-sent `audioSendAllowed` is false. Peers must not trust each other's self-reported capabilities.

### Session codes

- Codes are generated with `RandomNumberGenerator` from 36 uppercase alphanumeric symbols.
- Redis keys use HMAC-SHA-256 output keyed by `Sessions:CodeHmacKey`; plaintext codes are returned only when created/rotated.
- Redis entries have an absolute TTL and rotation removes the previous lookup.
- Expired/invalid code responses are deliberately indistinguishable.
- Background cleanup marks elapsed sessions expired, removes their code and prunes disconnected participants after the configured retention period.

Current limitation: successful join lookup does not consume a code. A code can be reused until rotation, session end or expiry.

### Device pairing

- Pairing codes follow the session-code convention: HMAC-hashed (keyed by
  `DeviceIdentity:PairingCodeHmacKey`), short TTL
  (`DeviceIdentity:PairingCodeTtlMinutes`), attempt-limited
  (`DeviceIdentity:PairingMaxAttempts`), and indistinguishable failure
  responses.
- `pairing-create` and `pairing-complete` are rate-limited by IP, not by
  device: per-device keying was evaluated but would require making
  `DeviceBearer` the app's default authentication scheme, so
  `DeviceIdentity:PairingMaxAttempts` remains the primary defense against
  pairing-code brute-forcing.
- `DeviceScopeAuthorizationHandler`'s live device-status and
  credential-version check — the same one backing the scoped
  `session:*`/`signaling:connect`/`turn:credentials` policies above — also
  protects three read-only routes that need no capability beyond an active
  device: `GET /api/sessions/active`, `GET /api/sessions/{id}` and
  `POST /api/webrtc/stats`, via a scope-less `DeviceAuthenticated` policy
  rather than a capability-scoped one.

### Abuse and data exposure

- Fixed-window limits return `429`: device-bootstrap, device-token, pairing-create, pairing-complete, create-session, join-session and rotate-code are all keyed by IP. Create/join/rotate cannot be keyed by device: `DeviceBearer` tokens carry no claim a per-caller limiter could key on.
- Defaults per 60-second window are create `10`, join `10`, rotate `5`, device-bootstrap `10`, device-token `10`, pairing-create `10`, pairing-complete `10`.
- Signaling frames are limited to 64 KiB text messages.
- Signaling logs record routing metadata only; SDP and ICE payloads are not logged by the endpoint.
- Readiness checks include PostgreSQL, Redis and the data-retention cleanup; liveness does not expose dependency state.
- Every persisted record is hard-deleted automatically at 82 days from collection — measured from `CreatedAt`/`JoinedAt`, never from last activity — keeping all data inside the 90 days declared in the Google Play Data Safety entry even after backup retention. Device identities are additionally replaced by a new `deviceId` at 60 days so no single identifier persists indefinitely. See [data retention](data-retention.md).

## Secrets and deployment

- Set high-entropy `Sessions__CodeHmacKey`, `DeviceIdentity__CredentialHmacKey`, `DeviceIdentity__PairingCodeHmacKey` and `DeviceIdentity__TokenSigningKey`; the Compose development fallbacks are not production-safe.
- Keep PostgreSQL, Redis, TURN and SSH credentials outside Git. The CI deploy script expects runtime secrets in `/opt/sonicrelay/.env` (or the configured app directory).
- The automated Compose file binds the API to `127.0.0.1:8080` by default; terminate TLS at a reverse proxy.
- TURN/STUN must use its native ports and should not be placed behind a normal HTTP reverse proxy.

## Known production gaps

- Device ownership and lifecycle are enforced by handlers; policy names alone do not express those resource checks.
- There is no CORS configuration. Browser-based clients need an explicit allowlist before use.
- There is no admin UI/API for device management beyond a device's own rotate/revoke endpoints; a human operator cannot remotely revoke another device's credential. Issue #26 explicitly scopes a human-user admin panel out of this project.
- `PUT /api/settings/relay` is the one exception to the "devices only manage themselves" rule above: it requires only `device:manage`, but the row it mutates is global relay/coturn configuration shared by every device, not the caller's own device. Any bootstrapped device (bootstrap is anonymous and only IP-rate-limited) can toggle `disableFallback` for every other device or point every other device's TURN traffic at attacker-controlled infrastructure. This is accepted for the current single-operator, self-hosted deployment model (there is no admin/account tier to scope it to) and should be revisited if this backend ever serves multiple independent operators/accounts.
- The live signaling registry is in memory, preventing safe multi-replica routing without sticky sessions or a backplane.
- TURN uses static configuration; temporary per-session TURN credentials are not issued by the API.
- The API-only CI deployment does not provision PostgreSQL, Redis, coturn, TLS or backups. Backup and log retention are therefore operator-owned, and both are load-bearing for the 90-day deletion guarantee: see [data retention](data-retention.md#backups).
- No explicit request-body limit is documented for HTTP endpoints beyond server defaults.

Before internet-facing production use, close these gaps, restrict network exposure, configure backups/restore testing, rotate secrets, and add operational alerting.
