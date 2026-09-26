# FrameRelay launch and Discord Activity contract

Configure `RelayLaunch:ServiceToken` (a separate high entropy bot-only bearer), `PublicBaseUrl`
(public HTTPS API origin), `DownloadUrl`, `DiscordClientId`, `DiscordClientSecret`,
`DiscordBotToken` and optional `DiscordRedirectUri`. Keep both Discord secrets API-side.
Apply `AddLaunchCapabilities` and `EnforceActivityInstanceBinding` before enabling bot routes. Existing device session routes remain
DeviceBearer authenticated. Bot credentials cannot create/join sessions.

All bot operations use `Authorization: Bearer <RelayLaunch:ServiceToken>`:

| Request | Body / query | Response |
|---|---|---|
| POST /api/launch-intents/share | `{provider:"discord",guildId,channelId,requestedByUserId,ttlSeconds}` | `{id,launchUrl,expiresAt}` |
| GET /api/launch-intents/pending | none | `[{id,guildId,channelId,requestedByUserId,status,expiresAt}]` |
| GET /api/launch-intents/{id} | `watchTtlSeconds` optional | `{id,status,sessionId,watchLaunchUrl,expiresAt}` |
| POST /api/launch-intents/{id}/published | `{messageId?}` | 204 |
| POST /api/launch-intents/watch | `{code,ttlSeconds}` | `{launchUrl,expiresAt}` |
| POST /api/launch-intents/activity | `{code,guildId,channelId,requestedByUserId,ttlSeconds}` | `{id,expiresAt}` |

TTL is clamped to 30–900 seconds. Share status is `pending` or `ready`. Capability tokens
are 64 lowercase hex characters and only hashes are stored; Activity bootstrap tokens are
retained exclusively server-side. Watch only admits live screen-share codes.

Public URLs are `/open/launch#<token>`: the fragment is absent from HTTP access logs. The landing
page clears it from history and opens `framerelay://launch?token=<token>`, and shows the configured
download/help link. The operating system cannot reliably report whether a protocol handler is
installed, so the download/help link stays visible rather than claiming automatic detection.

Desktop uses its own DeviceBearer for `POST /api/launch-intents/redeem {token}` →
`{id,kind,code,sessionId}`. Redemption is single-use. `kind=share` starts the normal screen
session, then `POST /api/launch-intents/{id}/bind {sessionId}` → 204; API checks both the
redeeming device and session source ownership. `kind=watch` uses `code` where present or the
sessionId for an owner-announcement watch link. No bot-supplied session ownership is trusted.

The Activity SDK authorizes with `identify`. It submits its authorization code and SDK instance:

1. `POST /api/discord/activity/authorize {code,instanceId}` →
   `{accessToken,userId,instanceId,expiresAt}`. This accessToken is an opaque FrameRelay identity
   credential, never the Discord OAuth token. API exchanges the code and calls Discord `/users/@me`,
   then bot-authenticated `GET /applications/{application.id}/activity-instances/{instance_id}`.
   Application, instance, current user membership, guild and channel must match. The invoking user
   binds the newest pending context intent once; later users may authenticate in that bound instance.
   An instance can bind one active session. A new watch for a different session returns 409
   `activity_instance_busy` while that session remains live, so it cannot silently show old media.
   When the previous session ends or the binding expires, a new pending intent can replace it;
   replacement expires all old instance/session credentials. A filtered unique database index,
   instance lock and serializable transaction protect concurrent initial binding.
2. `POST /api/discord/activity/viewer-grants` with identity bearer and no body → `{grant,expiresAt}`.
   Revalidates current Discord membership. Grant lasts 60 seconds and works once.
3. `POST /api/discord/activity/viewer-grants/redeem {grant}` →
   `{sessionId,participantId,signalingToken,expiresAt,iceServers}`. Counts the participant against
   the existing session viewer limit. No host/device credential or participant selector is accepted.
4. Connect `/ws/signaling?sessionId=<sessionId>` with WebSocket subprotocols
   `["framerelay","token."+signalingToken]`. The token works for this session only and is single-use;
   the server negotiates `framerelay`. Existing signaling envelope/SDP/ICE shapes are unchanged.

Bound instances and viewer signaling grants last 15 minutes; sockets close at credential expiry,
session end or Activity disconnect, and expired unused viewer reservations are cleaned every 30s.
Presence is checked during signaling admission and every five seconds thereafter using one
coalesced Discord REST snapshot per instance every five seconds. A snapshot that omits users
revokes their identity/grant/signaling credentials together; their sockets close and viewer slots
are released. Discord lookup failure fails closed. Disconnected Activity viewers do not use the desktop reconnect grace period. Obtain a new grant
to reconnect while the bound Activity authorization remains valid.

Secrets must not be included in request body or header logging. Activity requires reachable HTTPS
hosting, Discord Activities enabled, Activity URL Mapping to the client/API/WebSocket host, and
the same Discord application ID for bot, SDK and API. No RTP passes through this API.

The official validation contract is documented at
https://docs.discord.com/developers/resources/application#get-application-activity-instance.
Deployment and real Discord/TURN/H.264/Opus playback require manual validation.
