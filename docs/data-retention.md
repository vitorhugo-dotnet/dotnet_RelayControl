# Data retention and deletion

SonicRelay's Google Play Data Safety entry claims:

> No, but user data is automatically deleted within 90 days.

This document is the record of what makes that claim true. It inventories every
piece of data the backend persists, states a retention policy for each, and
describes the mechanism that enforces it (issue #44).

Two rules run through everything below:

- **Retention is measured from collection, not from activity.** A row's age is
  its `CreatedAt`/`JoinedAt`, never `LastSeenAt` or `UpdatedAt`. Using the
  latter would let a device that keeps checking in carry data collected two
  years ago and still look compliant.
- **Deletion is hard.** No tombstones, no archive tables, no soft-delete flags.
  A soft-deleted row still stores the identifier it was supposed to erase.

## Data inventory and retention policy

| Data | Table | Contains | Age measured from | Maximum retention | Action |
| --- | --- | --- | --- | ---: | --- |
| Device identity | `device_identities` | `deviceId`, device name, type, platform | `CreatedAt` | rotated at 60 days; deleted at 82 | rotate, then hard delete |
| Device credential | `device_identities.CredentialSecretHash` | HMAC of the device secret (never the secret) | `CreatedAt` | with the identity | hard delete |
| Pairing challenge | `pairing_challenges` | HMAC of a short-lived pairing code, publisher `deviceId` | `CreatedAt` | 24h after expiry/consumption; 82 days absolute | hard delete |
| Device pairing | `device_pairings` | publisher and viewer `deviceId` | `CreatedAt` | 82 days | hard delete |
| Streaming session | `stream_sessions` | session id, source `deviceId`, status, timestamps | `CreatedAt` | 82 days | hard delete |
| Session participant | `session_participants` | session id, `deviceId`, role, connection id, audio capability/mute flags | `JoinedAt` | 24h after disconnect; 82 days absolute | hard delete |
| Session join code | Redis (`ISessionCodeStore`) | HMAC of the join code → session id | write time | Redis TTL (minutes), dropped on session delete | expire / hard delete |
| Signaling metadata | `signaling_events` | session id, participant ids, event type | `CreatedAt` | 82 days | hard delete |
| Relay preferences | `relay_device_settings` | `deviceId`, relay mode, custom TURN URI/credentials | `CreatedAt` | 82 days | hard delete |
| WebRTC telemetry | — | not persisted | — | — | aggregated into Prometheus metrics only |
| Application logs | stdout / log collector | see [Logs](#logs) below | write time | 90 days (operator-configured) | delete |
| Database backups | operator-managed | everything above | backup time | 7 days | delete, see [Backups](#backups) |

`sonicrelay_*` Prometheus series carry no device id, session id, IP or SDP/ICE
data — only bounded enum labels — so they are not linkable to a person and are
not covered by the table above. `SonicRelayMetrics` documents that constraint.

### Why 82 days and not 90

The declared ceiling is 90 days, and it has to hold for *every copy* of a row,
not just the one in the live database. Two things are subtracted from it:

```
EffectiveRetention = MaxRetentionDays - SafetyMarginDays - BackupRetentionDays
                   =        90        -        1        -        7          = 82 days
```

- **`SafetyMarginDays` (1)** absorbs operational slack: a late scheduler tick, a
  short outage, a clock difference between hosts. Without it, a delay of hours
  is enough to push a row past the number stated publicly.
- **`BackupRetentionDays` (7)** absorbs backups. A row deleted from PostgreSQL
  on day 89 still sits inside the backup taken on day 88, and that backup
  outlives the ceiling. Pulling the cutoff forward by the backup window means
  the last backup that could still contain a row is itself destroyed on day 89 —
  before the ceiling, not after it.

This is the setting that most easily makes the declaration false: it must match
the backup schedule the deployment actually runs. Keeping 30-day backups while
leaving `BackupRetentionDays` at 7 means user data survives to day 112. Either
shorten the backups or raise the value — raising it to 30 pulls the database
cutoff to 59 days, which is still comfortably above the 60-day identity rotation
deadline only if that deadline is lowered too (it is clamped automatically, but
lower it deliberately rather than relying on the clamp).

## Device identity

A `deviceId` is the hardest case in the inventory. It is a stable, collected
identifier that persists across sessions, and SonicRelay has no human account
to attach data to instead — the device *is* the identity. Refreshing
`LastSeenAt`, bumping `CredentialVersion` or rotating the credential secret all
leave the same original identifier in the database, so none of them satisfy the
declaration on their own.

**The strategy: transparent rotation, then unconditional deletion.**

1. When a device exchanges its credential for a token
   (`POST /api/devices/token`) and its identity has reached
   `DataRetention:DeviceIdentityRotationDays` (default 60), the API creates a
   **brand-new identity row** — new `deviceId`, new credential secret,
   `CredentialVersion` back to 1 — and hard-deletes the old row in the same
   transaction.
2. Rows that referenced the old id (pairings, sessions, participants, relay
   settings) are re-pointed at the new one, so the rotation is invisible: an
   established pairing keeps working and a live session is not torn down.
   Outstanding pairing challenges are deleted rather than moved, since their
   code hash is bound to the retiring identity; the publisher just issues a new
   code.
3. **Nothing records which identity replaced which.** There is no
   `previousDeviceId` column, no audit row and no log line naming either id.
   Once the old row is gone, the previous identifier cannot be reconstructed
   from anything the backend still holds.
4. Re-pointed rows keep their own `CreatedAt`. Rotation moves a pairing to a new
   owner; it never restarts that pairing's retention clock.
5. A device that stops calling the API is never rotated. It is deleted outright
   by the sweep at 82 days, and its next request 401s and has to bootstrap and
   re-pair from scratch. That is what makes the ceiling unconditional rather
   than dependent on clients behaving.

Rotation is scheduled strictly inside the deletion window
(`DeviceIdentityRotationAfter` is clamped below `EffectiveRetention`), so a
misconfigured deadline can never let the sweep delete an identity that was
still waiting to rotate.

### Client contract

`POST /api/devices/token` now returns the authenticated device id alongside the
token:

```json
{
  "accessToken": "…",
  "expiresAt": "2026-08-18T02:05:00Z",
  "scopes": ["device:read", "…"],
  "deviceId": "8f1c…",
  "credentialVersion": 1,
  "rotatedCredentialSecret": "base64…"
}
```

`rotatedCredentialSecret` is present **only** on the response that performed a
rotation, and only once. A client that receives it must persist both
`deviceId` and `rotatedCredentialSecret`, replacing what it had stored — the
previous pair no longer exists server-side and its next use returns `401`.

A client that ignores the field is not broken, only degraded: its stored
credential stops working, it re-bootstraps, and the user re-pairs. Both first
party clients (Flutter viewer, Windows publisher) should handle the field so
rotation stays invisible.

## The cleanup job

`DataRetentionService` is an ASP.NET Core `BackgroundService` that runs every
`DataRetention:CleanupIntervalHours` (default 24), starting immediately at
boot so a deployment that was down over a scheduled tick catches up rather than
waiting a full interval.

Each pass sweeps entity groups in a fixed order — children before parents:

```
signaling_events → session_participants → stream_sessions → pairing_challenges
→ device_pairings → relay_device_settings → device_identities → orphan sweep
```

Properties the implementation guarantees, each covered by a test in
`tests/SonicRelay.Api.IntegrationTests/DataRetentionServiceTests.cs`:

- **Idempotent.** Every predicate is a pure "older than the cutoff" filter, so
  re-running a pass deletes nothing the second time and leaves live data alone.
- **Safe to run concurrently.** Two overlapping passes converge on the same
  state; a `DbUpdateConcurrencyException` means another pass already deleted the
  row, which is the outcome we wanted, and is not treated as a failure.
- **Partial failure is contained.** Each entity group has its own retries
  (`DataRetention:MaxAttemptsPerEntity`) and its own `SaveChanges`. A group that
  fails does not abort the pass, does not mark the pass successful, and is
  retried on the next tick.
- **Bounded.** `DataRetention:BatchSize` caps rows removed per entity per pass;
  a backlog is cleared over successive passes instead of in one huge
  transaction.
- **No copies survive.** Deleted rows are not written anywhere else — no audit
  table, no export, nothing in the logs.

### Referential integrity: ordered deletion, not database cascades

The schema stores relationships as plain id columns rather than declared
foreign keys with `ON DELETE CASCADE`. That is a deliberate choice, not an
oversight:

- Identity rotation re-points a device's pairings and sessions at a new
  identity row and deletes the old one in the same transaction. With a database
  cascade, deleting the retiring identity row would tear down the live session
  and the established pairing that rotation exists to preserve.
- Cleanup order is therefore the application's responsibility, which is exactly
  what the ordered sweep above implements and what the graph tests assert.

To make that choice safe rather than merely intentional, every pass ends with an
**orphan sweep** that deletes any row whose parent no longer exists —
participants and signaling events without a session, sessions/pairings/
challenges/relay settings without a device. It is the net that catches anything
an earlier partial failure, or any other code path, left dangling, so a stale
identifier cannot survive by hiding in a child table.

## Configuration

| Key | Default | Purpose |
| --- | --- | --- |
| `DataRetention:Enabled` | `true` | Master switch. Disabling it stops enforcing the public 90-day claim; the health check reports degraded and a warning is logged at startup. |
| `DataRetention:MaxRetentionDays` | `90` | The declared ceiling. Raising it above 90 makes the Data Safety declaration false. |
| `DataRetention:SafetyMarginDays` | `1` | Operational slack subtracted from the ceiling. |
| `DataRetention:BackupRetentionDays` | `7` | The deployment's PostgreSQL backup window, also subtracted from the ceiling so no backup copy outlives it. Must match reality. |
| `DataRetention:CleanupIntervalHours` | `24` | How often the sweep runs. |
| `DataRetention:DeviceIdentityRotationDays` | `60` | Age at which an active device identity is replaced. Always clamped below the deletion cutoff. |
| `DataRetention:PairingChallengeRetentionHours` | `24` | Short clock for spent/expired pairing challenges. |
| `DataRetention:BatchSize` | `500` | Rows removed per entity per pass. |
| `DataRetention:MaxAttemptsPerEntity` | `3` | Retries per entity group for transient database faults. |
| `DataRetention:StaleAfterHours` | `48` | How long the health check tolerates no successful pass. |

In Compose these map to `DataRetention__*` environment variables; see
`infra/compose.yml` and `infra/.env.prod.example`.

## Observability

| Metric | Meaning |
| --- | --- |
| `sonicrelay_data_retention_runs_total` | Cleanup passes that completed. |
| `sonicrelay_data_retention_deleted_records_total{entity="…"}` | Records permanently deleted, by entity. |
| `sonicrelay_data_retention_failures_total` | Passes that failed. |
| `sonicrelay_data_retention_last_success_timestamp` | Unix seconds of the last fully successful pass. |
| `sonicrelay_device_identity_rotations_total` | Identities replaced before the ceiling. |

The only label is `entity`, drawn from a fixed set of table names, so a scrape
can never be used to work out *which* device or session was erased.

`observability/prometheus/sonicrelay-alerts.yml` ships three rules:
`SonicRelayDataRetentionStalled` (no success in 48h),
`SonicRelayDataRetentionNeverRan` (the series is absent entirely) and
`SonicRelayDataRetentionFailures` (repeated failures). The first two are
`critical` — a silently stopped cleanup has no other symptom, and the
declaration stops being true without anything else going wrong.

Readiness (`/health/ready`) includes a `data-retention` check that reports
unhealthy once the last success is older than `StaleAfterHours`, degraded before
the first pass or when retention is disabled.

### Logs

The cleanup logs aggregate counts only:

```
Data retention pass removed 42 records across 5 entities (cutoff 82d); failed entities: 0
```

Never logged, at any level: credentials, pairing or session codes, code hashes,
SDP/ICE payloads, or the identifiers of deleted rows. Rotation logs how many
pairings and sessions moved and nothing else — printing the old and new device
ids on one line would re-create exactly the link rotation exists to destroy.
`DataRetentionServiceTests.Cleanup_logs_counts_and_never_the_data_it_erased`
asserts this.

Operators must configure their log pipeline to delete or anonymize collected
logs within 90 days as well. Application logs contain client IP addresses
(rate-limit warnings) and hashed session tags, which are linkable data; deleting
rows from PostgreSQL while keeping the logs that describe them indefinitely
would not satisfy the declaration.

## Backups

Deleting from the primary database is meaningless if a backup keeps the same
rows forever. This repository does not provision PostgreSQL or its backups (see
`docs/deployment-vps-ssh.md`), so this is a policy an operator must implement:

- **Maximum backup retention: 7 days**, matching `DataRetention:BackupRetentionDays`.
  A row is deleted from PostgreSQL at 82 days, so the newest backup that can
  still contain it was taken at day 82 and is destroyed at day 89 — one day
  inside the ceiling. Any other backup window is fine as long as
  `BackupRetentionDays` is set to the same number, which moves the database
  cutoff to match; the two values are a pair and must not drift apart.
- **Encrypt backups at rest** and keep them under the same access control as the
  live database.
- **Restoring a backup reintroduces expired data.** Any restore — full or
  partial — must be followed by a retention pass before the API serves traffic
  again. Restarting the API is enough: `DataRetentionService` runs a pass at
  boot. Verify with `sonicrelay_data_retention_last_success_timestamp`.
- **Restore drills** should be run against a non-production copy and the copy
  destroyed afterwards; a forgotten restore environment is an uncontrolled
  90-day-plus store of user data.

## Exceptions

There are none. No category in the inventory is retained beyond 90 days for
legal, anti-abuse or security reasons. Anti-abuse controls in this API are
rate limits keyed on IP within a 60-second window and per-challenge attempt
counters — neither needs long-lived storage. If an exception ever becomes
necessary, it must be minimized, added to the table above, and reflected in the
published privacy policy and Data Safety entry before it is implemented.

## Publishing checklist

Before ticking *"user data is automatically deleted within 90 days"* in the Play
Console (issue #43):

- [ ] This implementation is deployed to production, not just merged.
- [ ] `sonicrelay_data_retention_last_success_timestamp` is advancing in
      production and the retention alerts are loaded into Prometheus.
- [ ] `DataRetention:Enabled` is `true` and `MaxRetentionDays` is `90` or lower
      in the production environment.
- [ ] The host's PostgreSQL backup window and `DataRetention:BackupRetentionDays`
      are the same number.
- [ ] Log retention is configured to 90 days or less.
- [ ] Both clients handle `rotatedCredentialSecret`, or the re-bootstrap
      fallback is accepted knowingly.
- [ ] The published privacy policy states the same 90-day rule as this document.
