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
