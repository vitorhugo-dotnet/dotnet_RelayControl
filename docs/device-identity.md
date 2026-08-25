# Device Identity Auth

This document describes the device-credential flow that is the API's only
authentication scheme (see `docs/adr/0005-device-identity-credentials.md`
and `docs/adr/0006-remove-identity.md`). It covers backend behavior; the
Windows and Flutter clients both do real device-identity bootstrap and
pairing (issue #26 Phase 3).

## Flow

1. `POST /api/devices/bootstrap` — a device registers with a `name`,
   `deviceType` (`windows_publisher`, `windows_desktop` or `flutter_viewer`),
   and `platform`. The two must agree: `windows_publisher` and
   `windows_desktop` are `windows`-only, and `flutter_viewer` accepts
   `android`, `ios` or `web` — `web` being the same viewer build served as a
   browser app. Any other combination is rejected with `400`.
   The response includes the device ID and a credential secret returned
   exactly once; only its HMAC is ever stored.
2. `POST /api/devices/token` — the device exchanges its ID and secret for a
   short-lived JWT (`DeviceIdentity:AccessTokenMinutes`, default 5 minutes)
   carrying scopes appropriate to its device type.
3. `POST /api/devices/rotate-credential` — requires the current secret and a
   valid `device:manage`-scoped token; issues a new secret and invalidates
   every token issued under the previous credential version immediately.
4. `POST /api/devices/revoke` — marks the device revoked; blocks future token
   requests and rejects already-issued tokens on their next authorized
   request, without waiting for expiry.

Step 2 is also where a device identity is retired. See
[Identity rotation](#identity-rotation) below.

## Pairing

1. A publisher device with a `pairing:create`-scoped token calls
   `POST /api/pairings/challenges` and receives a short-TTL code
   (`DeviceIdentity:PairingCodeTtlMinutes`, default 5 minutes) plus a QR
   payload containing the challenge ID and code — no persistent secret is
   ever embedded in the QR payload.
2. A viewer device with a `pairing:complete`-scoped token calls
   `POST /api/pairings/complete` with the code. A wrong code, expired
   challenge, and already-consumed challenge all return the same generic
   error and increment the attempt counter; the challenge is rejected outright
   after `DeviceIdentity:PairingMaxAttempts` (default 5) failed attempts.
3. `GET /api/devices/{deviceId}/pairings` and `DELETE /api/pairings/{pairingId}`
   are restricted to a caller whose authenticated device ID participates in
   the pairing.

`pairing-create` and `pairing-complete` are rate-limited by caller IP address,
not by device ID. Per-device keying was evaluated but is not currently
achievable without making `DeviceBearer` the app's default authentication
scheme (`UseRateLimiter()` runs before the `DeviceBearer` principal is
populated by `UseAuthorization()`'s explicit `AddAuthenticationSchemes` call).
`DeviceIdentity:PairingMaxAttempts` is the primary defense against pairing-code
brute-forcing regardless of rate-limit keying granularity.

## Configuration

| Key | Purpose |
| --- | --- |
| `DeviceIdentity:CredentialHmacKey` | Server-side pepper for hashing device credential secrets. |
| `DeviceIdentity:PairingCodeHmacKey` | Server-side pepper for hashing pairing codes. |
| `DeviceIdentity:TokenSigningKey` | Symmetric signing key for `DeviceBearer` JWTs. |
| `DeviceIdentity:AccessTokenMinutes` | Access token lifetime (default 5). |
| `DeviceIdentity:PairingCodeTtlMinutes` | Pairing challenge TTL (default 5). |
| `DeviceIdentity:PairingMaxAttempts` | Max failed pairing attempts before a challenge is rejected (default 5). |
| `DataRetention:DeviceIdentityRotationDays` | Age at which a device identity is replaced by a new one (default 60). |

Set high-entropy values for `CredentialHmacKey`, `PairingCodeHmacKey`, and
`TokenSigningKey` outside Git, the same way `Sessions:CodeHmacKey` is handled.

## Sessions, signaling, and TURN

Session lifecycle, WebRTC signaling and TURN credential issuance
authenticate exclusively through `DeviceBearer`. `DeviceCredentialService.ScopesFor`
issues five additional scopes alongside `device:read`/`device:manage`/the
pairing scopes, split by device type:

| Scope | Windows Publisher | Windows Desktop | Flutter Viewer | Used by |
| --- | --- | --- | --- | --- |
| `session:create` | yes | yes | — | `POST /api/sessions` |
| `session:end` | yes | yes | — | `POST /api/sessions/{sessionId}/end`, `POST /api/sessions/{sessionId}/rotate-code` |
| `session:join` | — | yes | yes | `POST /api/sessions/join` |
| `signaling:connect` | yes | yes | yes | `GET /ws/signaling` |
| `turn:credentials` | yes | yes | yes | `GET /api/webrtc/ice-servers` |

`windows_desktop` (the Windows screen-sharing app) publishes and views with one identity, so
it carries the union of the other two types' scopes — ten in total: `device:read`,
`device:manage`, `pairing:create`, `pairing:complete`, `pairing:revoke`, `session:create`,
`session:join`, `session:end`, `signaling:connect`, `turn:credentials`. It is the only type
holding both `pairing:create` and `pairing:complete`, and the only one allowed to join a
`screen_share` session (see [protocol.md](protocol.md#screen-share-sessions)).

Two read-only session routes (`GET /api/sessions/active`, `GET /api/sessions/{sessionId}`)
and the WebRTC stats endpoint (`POST /api/webrtc/stats`) require only the
scope-less `DeviceAuthenticated` policy — any active device presenting a
current credential version — since they need no capability beyond being a
live, authenticated device.

Neither session creation nor joining takes a client-supplied device id any
more: the caller's own authenticated device (from the token's `sub` claim) is
always the publisher of a session it creates and always the viewer that joins
one, so there is nothing left for a client to assert about which device it
is. The WebSocket handshake follows the same rule: `GET /ws/signaling` takes
only `sessionId` as a query parameter — the previous `deviceId` parameter is
gone, and the connecting participant's device comes from the bearer token.

`create-session`, `join-session` and `rotate-code` are rate-limited by caller
IP address, the same keying (and for the same reason) as
`pairing-create`/`pairing-complete` above: `DeviceBearer` tokens carry no
claim a per-device limiter could key on without making `DeviceBearer` the
app's default authentication scheme.

## Pairing authorization at session join

`POST /api/sessions/join` requires both an active `DevicePairing` between the
caller's device and the session's source (publisher) device, and the current
session join code. If either condition is absent — bad code, expired code, or
no active pairing — the API returns the same invalid/expired-code response,
so a caller cannot distinguish "session doesn't exist" from "you're not
paired with its publisher." Existing participants may reconnect after
pairing revocation until the session ends; revocation only blocks new joins.

## Identity rotation

A `deviceId` is a collected identifier, and SonicRelay tells Google Play users
that collected data is deleted automatically within 90 days. Refreshing
`LastSeenAt` or rotating the credential secret does not help: the same original
identifier stays in the database. So the identity itself expires.

At `DataRetention:DeviceIdentityRotationDays` (default 60 days from
`CreatedAt`), the next `POST /api/devices/token` call transparently replaces the
identity: a new row with a new `deviceId` and a new credential secret is
created, everything that referenced the old id is re-pointed at it, and the old
row is hard-deleted in the same transaction. The response carries the new
`deviceId` plus a `rotatedCredentialSecret`, which the client must store in
place of what it had. Pairings and live sessions survive the rotation; the user
sees nothing.

Nothing links the old identity to the new one — no `previousDeviceId` column, no
audit row, no log line naming either id — so the retired identifier cannot be
reconstructed from what remains.

A device that stops calling the API is not rotated. It is deleted outright by
the retention sweep, and its next request returns `401`, requiring a fresh
bootstrap and re-pairing. [Data retention](data-retention.md) covers the full
policy, the sweep and its configuration.

## Revocation and lifecycle

There is no separate account-deletion or admin user-management surface: a
device is the whole unit of identity. `POST /api/devices/rotate-credential`
and `POST /api/devices/revoke` (both `device:manage`-scoped, self-service —
a device can only rotate/revoke itself, not another device) are the complete
lifecycle-management surface. Issue #26 explicitly puts a human-user admin
panel out of scope for this project.
