# ADR 0007: Model bidirectional audio as a session mode with backend-owned publish permission

- Status: Accepted
- Date: 2026-08-22

## Context

SonicRelay was designed around one-way streaming: a Windows publisher transmits and mobile
viewers listen. Issue #22 asks for two or more participants to send and receive audio on the
same WebRTC connection (intercom / voice-call scenarios) without the API ever carrying,
mixing, transcoding or storing media.

Two shapes were available. Either drop the `publisher`/`viewer` roles and treat every
participant as a symmetric peer, or keep the roles and add an explicit session mode. Dropping
the roles would have touched the participant unique index `(SessionId, DeviceId, Role)`, the
viewer-capacity accounting, the discovery projections and both clients' handshake — a wide
migration to express something that is really a property of the session, not of the row.

## Decision

A session carries a `mode`, chosen once at creation and never changed afterwards:

- `broadcast` (default): the publisher transmits, other participants receive. This is the
  pre-existing behavior, and anything created without a mode gets it.
- `duplex`: every authorized participant may both send and receive audio.

`publisher` and `viewer` keep their existing meaning as routing and capacity concepts; in a
duplex session "viewer" simply means "a participant that is not the session owner".

Whether a participant may publish audio is a backend decision, recorded per participant as
`AudioSendAllowed`. It is seeded from the session mode and the role on join, and afterwards
only the session's own device can change it, through
`POST /api/sessions/{id}/participants/{participantId}/audio-permission` (duplex sessions only).
A client declares its intent through the `participant.capabilities` signaling message; the
server refuses `canSendAudio: true` from a participant it has not authorized, and republishes
the authoritative state to the whole session — sender included — so no peer learns another
peer's capabilities from the peer itself.

Mute state travels the same way, through `participant.audio_state_changed`, and
`webrtc.renegotiate` is routed peer-to-peer so a running session can add or drop an audio
track without being torn down and recreated.

## Consequences

Duplex is opt-in and one-way sessions are unaffected: their participants get exactly the
permissions they had before, including a data migration that backfills existing publisher rows.

The API's boundary is unchanged — it still never inspects SDP, ICE or audio. That bounds what
"only authorized participants may publish" can mean here: the server authorizes and publishes
the authoritative capability state, and clients must reject audio tracks from a peer the server
has not marked `audioSendAllowed`. Enforcing it any deeper would require parsing or terminating
media, which [ADR 0001](0001-control-plane-only.md) rules out.

Mesh topology is inherited from the existing model: for 1:1 a single bidirectional
`RTCPeerConnection` is enough, and small groups still work, but upload and CPU cost grow with
each participant. A larger room needs an SFU, which stays out of scope and belongs in its own
decision.
