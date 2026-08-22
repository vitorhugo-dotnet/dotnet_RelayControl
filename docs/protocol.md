# HTTP and WebSocket protocol

This document describes routes mapped by the current API. Unless marked public, HTTP requests require `Authorization: Bearer <DeviceBearer-token>`, a `DeviceBearer` JWT issued by `POST /api/devices/token` (see [device identity](device-identity.md)). There is no human user account, login or password anywhere in the API, and no ASP.NET Core Identity.

## Health

| Method | Route | Auth | Behavior |
| --- | --- | --- | --- |
| `GET` | `/health/live` | Public | Process liveness only; excludes registered dependency checks. |
| `GET` | `/health/ready` | Public | Checks PostgreSQL, Redis and the data-retention cleanup. |

Swagger is enabled by default only in Development, or when `Swagger:Enabled=true`.

## Device identity

Bootstrap a publisher or viewer with `POST /api/devices/bootstrap`, then
exchange its device ID and one-time credential secret at `POST
/api/devices/token` for a short-lived `DeviceBearer` JWT. Device bootstrap and
token exchange are public (no `Authorization` header); everything else
requires a `DeviceBearer` access token with the matching scope. Use that
token for sessions, WebSocket signaling and TURN credentials. Before a viewer
can join a publisher's session, the two devices must establish a durable
pairing through `POST /api/pairings/challenges` and `POST
/api/pairings/complete` — `POST /api/sessions/join` enforces that an active
pairing exists. See [device identity](device-identity.md) for the full flow,
scopes and configuration.

| Method | Route | Auth | Behavior |
| --- | --- | --- | --- |
| `POST` | `/api/devices/bootstrap` | Public, rate-limited | Validates type/platform, persists a `DeviceIdentity` and returns the device ID plus a credential secret shown exactly once. |
| `POST` | `/api/devices/token` | Public, rate-limited | Exchanges a device ID and credential secret for a short-lived `DeviceBearer` JWT with scopes for that device type. |
| `POST` | `/api/devices/rotate-credential` | `device:manage` | Requires the current secret; issues a new one and bumps the credential version, invalidating tokens issued under the previous one. |
| `POST` | `/api/devices/revoke` | `device:manage` | Idempotently revokes the caller's own device; a device cannot revoke another device. |

Bootstrap response:

```json
{ "deviceId": "<uuid>", "credentialSecret": "<shown once>", "credentialVersion": 1 }
```

Token response:

```json
{
  "accessToken": "<jwt>",
  "expiresAt": "2026-08-01T14:05:00Z",
  "scopes": ["session:create", "..."],
  "deviceId": "<uuid>",
  "credentialVersion": 1,
  "rotatedCredentialSecret": null
}
```

`deviceId` is the device the returned token authenticates. It is normally the id
the caller sent, but once an identity reaches the retention rotation deadline
this call replaces it: `deviceId` is then a **new** id and
`rotatedCredentialSecret` carries the matching new secret, shown exactly once.
A client that receives a non-null `rotatedCredentialSecret` must persist both
values in place of what it had stored — the previous device id and secret no
longer exist and their next use returns `401`. A client that ignores the field
simply re-bootstraps and re-pairs later. See
[data retention](data-retention.md#device-identity).

Valid `deviceType`/`platform` pairs are `windows_publisher`/`windows` and `flutter_viewer`/`android|ios`. Revoked devices cannot bootstrap new tokens or create, join or connect to sessions.

## Device pairing

| Method | Route | Auth | Behavior |
| --- | --- | --- | --- |
| `POST` | `/api/pairings/challenges` | `pairing:create` | A publisher device issues a short-TTL pairing code plus QR payload. |
| `POST` | `/api/pairings/complete` | `pairing:complete` | A viewer device redeems the code and creates a `DevicePairing`. |
| `GET` | `/api/devices/{deviceId}/pairings` | `device:read` | Lists a device's active pairings; only the device itself may query its own. |
| `DELETE` | `/api/pairings/{pairingId}` | `pairing:revoke` | Idempotently revokes a pairing the caller's device participates in. |

## Sessions

All session routes require a `DeviceBearer` token; the caller's own device (from the token, never client-supplied) is always the publisher of a session it creates and always the viewer that joins one.

| Method | Route | Scope | Behavior |
| --- | --- | --- | --- |
| `POST` | `/api/sessions/` | `session:create` | Creates a waiting session and publisher participant for the caller's device; returns `201` and a six-character code. |
| `GET` | `/api/sessions/active` | `DeviceAuthenticated` | Lists waiting/active sessions published or joined by the caller's device, including connected viewer count. |
| `GET` | `/api/sessions/{sessionId}` | `DeviceAuthenticated` | Returns a session to its publisher or a participant; inaccessible sessions return `404`. |
| `POST` | `/api/sessions/{sessionId}/end` | `session:end` | Publisher-only; marks the session ended, disconnects participants and removes the Redis code. Idempotently returns the ended session. |
| `POST` | `/api/sessions/{sessionId}/rotate-code` | `session:end` | Publisher-only; rejects ended/expired sessions with `409`, invalidates the previous code and returns a new code. |
| `POST` | `/api/sessions/join` | `session:join` | Resolves a valid code, enforces the viewer limit, creates/reconnects a participant for the caller's device and activates a waiting session. Also requires an active `DevicePairing` between the caller's device and the session's source device. |
| `GET` | `/api/sessions/{sessionId}/participants` | `DeviceAuthenticated` | Returns the session mode and every participant's presence and audio capabilities, to the publisher or any participant; inaccessible sessions return `404`. |
| `POST` | `/api/sessions/{sessionId}/participants/{participantId}/audio-permission` | `session:end` | Publisher-only; grants or revokes one participant's permission to publish audio. Duplex sessions only (`409 session_not_duplex` otherwise). |

Create request:

```json
{ "maxViewers": 3, "mode": "broadcast" }
```

`maxViewers` defaults to `Sessions:MaxViewersPerSession` and must be at least one. There is currently no upper bound.

`mode` selects the audio direction of the session and cannot be changed afterwards:

| Mode | Meaning |
| --- | --- |
| `broadcast` (default) | The publisher transmits and the other participants only receive. |
| `duplex` | Every authorized participant may send and receive audio on the same peer connection. |

The value is trimmed and lowercased; omitting it (or sending `null`) means `broadcast`, so a
client written before duplex existed keeps creating one-way sessions. Anything else returns
`400` with `{ "code": "invalid_session_mode" }`. See [bidirectional audio](#bidirectional-audio-duplex-sessions).

Join request:

```json
{ "code": "ABC123" }
```

Codes are trimmed, uppercased and must contain exactly six ASCII letters/digits. A new viewer participant needs both an active DevicePairing to the session's source device and the current session join code. Wrong, malformed, expired and terminal-session codes, as well as a missing/revoked pairing for a new participant, all return the same `404` invalid/expired-code response. Existing participants may reconnect after pairing revocation until the session ends. Despite the store method name `RedeemAsync`, a successful lookup does not consume a code; it remains reusable until rotation, session end or expiry.

Session responses contain `id`, `sourceDeviceId`, `status`, `mode`, `maxViewers`, `codeExpiresAt`, `startedAt`, `endedAt`, `createdAt`, and `code` when a new code is issued. `GET /api/sessions/active` and `GET /api/sessions/discoverable` project `mode` as well.

## Bidirectional audio (duplex sessions)

A duplex session lets authorized participants send and receive audio over the same WebRTC
connection. The API's role does not change: it authenticates, authorizes, tracks presence and
forwards signaling, and never receives, mixes, transcodes or stores audio. Media still flows
directly between peers, or through coturn when a direct path is impossible.

### Who is allowed to publish

Publishing is a backend decision, not a client claim. Every participant carries two separate
flags:

| Field | Owner | Meaning |
| --- | --- | --- |
| `audioSendAllowed` | Backend | Whether the participant is *authorized* to publish audio. A client can never raise it. |
| `canSendAudio` | Client, clamped | Whether the participant currently *intends* to publish. Rejected outright while `audioSendAllowed` is false. |
| `canReceiveAudio` | Client | Whether the participant wants to receive audio. Carries no authorization weight. |
| `audioMuted` | Client | The last mute state the participant announced. |

Defaults are derived from the session mode and the role when the participant joins:

| Session mode | Role | `audioSendAllowed` | `canSendAudio` | `canReceiveAudio` |
| --- | --- | --- | --- | --- |
| `broadcast` | `publisher` | `true` | `true` | `false` |
| `broadcast` | `viewer` | `false` | `false` | `true` |
| `duplex` | any | `true` | `true` | `true` |

`publisher` and `viewer` keep their existing meaning as routing and capacity concepts. In a
duplex session, "viewer" only means "a participant that is not the session owner" — it says
nothing about audio direction.

The session's own device can revoke or restore one participant's permission at any time:

```text
POST /api/sessions/{sessionId}/participants/{participantId}/audio-permission
{ "canSendAudio": false }
```

Only the session's publisher device may call it, only while the session is live (`409
session_terminal` otherwise), and only on a duplex session (`409 session_not_duplex`). A
session the caller does not own returns `404`. Revoking also clears the participant's declared
`canSendAudio` and immediately broadcasts a `participant.capabilities` frame to everyone in the
session, the revoked participant included, so it stops publishing without waiting to be told by
a peer.

`GET /api/sessions/{sessionId}/participants` returns the same state for the whole session:

```json
{
  "sessionId": "<uuid>",
  "mode": "duplex",
  "participants": [
    {
      "participantId": "<uuid>",
      "role": "publisher",
      "status": "connected",
      "audioSendAllowed": true,
      "canSendAudio": true,
      "canReceiveAudio": true,
      "audioMuted": false,
      "joinedAt": "2026-08-22T14:00:00Z",
      "leftAt": null,
      "isSelf": true
    }
  ]
}
```

Device ids are deliberately not projected: participant ids are what signaling addresses, and a
device id is a durable identifier peers have no reason to learn from each other.

### What the backend cannot enforce

The API does not parse SDP (see [ADR 0001](adr/0001-control-plane-only.md)), so it cannot see
that a peer attached an audio track it was not authorized to send. The server is the authority
on *who may publish*, and it publishes that authority to every participant; the clients are
responsible for the last step: **reject or ignore inbound audio from a peer whose latest
server-sent `audioSendAllowed` is false**. Treat the server's `participant.capabilities` frames
as the only source of truth for that — never a peer's own claim.

## WebSocket signaling

### Mapa mental: signaling não é mídia

- **WebSocket** é o canal persistente de signaling entre cada client e o backend.
- **WebRTC** cria a conexão de mídia entre Publisher e Viewer, direta ou via relay.
- **SDP offer/answer** negocia capacidades e parâmetros da conexão.
- **ICE candidate** descreve um caminho de rede que um peer pode tentar.
- **STUN** ajuda um peer a descobrir seu endereço público.
- **TURN/coturn** retransmite os pacotes WebRTC quando a conexão direta falha.
- **Opus** é o codec de áudio usado pelos clients; não roda no backend.

O backend é o **control-plane**: autentica, autoriza, mantém sessões e encaminha signaling. O áudio pertence ao **media-plane** e flui entre os clients ou através do coturn. A API não captura, codifica, decodifica, armazena nem retransmite áudio.

### Fluxo do Publisher

1. Bootstrap (`POST /api/devices/bootstrap`) and get a token (`POST /api/devices/token`) for a `windows_publisher`/`windows` device.
2. Crie uma sessão com `POST /api/sessions/` e exiba o código temporário ao usuário.
3. Abra o WebSocket autenticado usando apenas `sessionId`; a identidade do Publisher vem do token `DeviceBearer`.
4. Guarde seu `participantId` recebido em `session.joined`. Quando outro `session.joined` anunciar um Viewer, use o `participantId` do payload como destino de `publisher.ready`.
5. Para cada Viewer, crie uma `RTCPeerConnection`, adicione a faixa de áudio Opus e envie uma `webrtc.offer` direcionada ao `participantId` dele.
6. Ao receber `webrtc.answer`, aplique o SDP como remote description na conexão daquele Viewer.
7. Troque `webrtc.ice_candidate` nos dois sentidos enquanto o ICE gathering estiver ativo. Candidate vazio/nulo para fim de gathering deve ser representado no payload conforme a biblioteca do client, pois o backend não interpreta o campo.
8. Mantenha uma peer connection por Viewer. Capture áudio e gerencie reconnect/cleanup no app Windows, não nesta API.

### Fluxo do Viewer

1. Bootstrap (`POST /api/devices/bootstrap`) e obtenha um token (`POST /api/devices/token`) para um device `flutter_viewer` (`android` ou `ios`), complete o pairing com o Publisher (`POST /api/pairings/complete`), depois entre com `POST /api/sessions/join`.
2. Abra o WebSocket autenticado usando o `sessionId` retornado; a identidade do Viewer vem do token `DeviceBearer`.
3. Guarde seu `participantId` recebido em `session.joined`. Ao receber `publisher.ready`, aprenda o ID do Publisher pelo campo autenticado `from` e responda com `viewer.ready` para esse destino.
4. Ao receber `webrtc.offer`, crie/configure a `RTCPeerConnection` e aplique o SDP como remote description.
5. Gere a answer, aplique-a localmente e envie `webrtc.answer` ao Publisher.
6. Troque `webrtc.ice_candidate` nos dois sentidos e conecte a faixa de áudio remota ao playback Flutter.
7. Encerre a peer connection ao receber `session.ended`, ao sair da sessão ou ao perder a autorização do device.

### Admissão e envelope

Connect with an authenticated WebSocket upgrade:

```text
GET /ws/signaling?sessionId={uuid}
Authorization: Bearer <DeviceBearer-token>
```

Before upgrade, the API verifies:

- the `sessionId` query parameter is a UUID;
- the caller's `DeviceBearer` token is valid, has the required signaling scope, and its device is active with a current credential version;
- the session exists and is not ended, expired or past `codeExpiresAt`;
- a participant matches the session and the caller's device.

Validation failures return HTTP `400`, `401`, `403`, `404` or `410` before the upgrade. Every server frame uses this envelope:

```json
{
  "type": "session.joined",
  "messageId": "<uuid>",
  "sessionId": "<uuid>",
  "from": null,
  "to": "<participant-uuid>",
  "timestamp": "2026-07-04T14:00:00Z",
  "payload": {
    "participantId": "<participant-uuid>",
    "role": "publisher"
  }
}
```

Ao admitir um socket, o servidor envia ao novo socket um `session.joined` sobre ele próprio (`from: null`) e anuncia o novo participante aos peers já conectados (`from: <new-participant-uuid>`). O payload sempre contém `participantId` e `role`. Assim, o Publisher descobre cada Viewer sem compartilhar IDs fora do protocolo; o Viewer descobre o Publisher quando recebe `publisher.ready`.

Além de `participantId` e `role`, o payload traz o estado de áudio do participante — `sessionMode`, `audioSendAllowed`, `canSendAudio`, `canReceiveAudio` e `audioMuted` (ver [áudio bidirecional](#bidirectional-audio-duplex-sessions)). Clients anteriores ao modo duplex simplesmente ignoram os campos extras:

```json
{
  "participantId": "<participant-uuid>",
  "role": "publisher",
  "sessionMode": "duplex",
  "audioSendAllowed": true,
  "canSendAudio": true,
  "canReceiveAudio": true,
  "audioMuted": false
}
```

Logo após o próprio `session.joined`, o novo socket recebe um `participant.capabilities` para cada peer **já conectado** (`from` é o peer descrito). Peers que entram depois se anunciam sozinhos via `session.joined`; sem esse roster inicial, os que já estavam conectados nunca seriam descritos ao recém-chegado — exatamente o que uma sessão duplex precisa saber antes de decidir se espera áudio de cada um.

### Client messages

Clients send the same envelope shape. `type` is required. `messageId` may be supplied as a UUID and is preserved; otherwise the server generates it. Client `sessionId`, `from`, and `timestamp` values are never trusted. The server derives the session from the connection, overwrites `from` with the authenticated participant, and assigns its own timestamp.

Um client precisa enviar apenas `type`, `to`, `payload` e, opcionalmente, `messageId`:

```json
{
  "type": "viewer.ready",
  "messageId": "0f057269-0f91-4a30-a7be-f5755b01f82a",
  "to": "<publisher-participant-uuid>",
  "payload": {}
}
```

`ping` requires no recipient and produces an enveloped `pong`. These routed types require a UUID `to` participant in the same live session and may include any JSON `payload`:

- `publisher.ready`
- `viewer.ready`
- `webrtc.offer`
- `webrtc.answer`
- `webrtc.ice_candidate`
- `webrtc.renegotiate`
- `pong`

These types describe the sender's own state instead of addressing one peer, so they take **no** `to`. The server validates them, persists the result and broadcasts the authoritative version to the whole session, sender included:

- `participant.capabilities`
- `participant.audio_state_changed`

The server emits a normalized routed frame:

```json
{
  "type": "webrtc.offer",
  "messageId": "<uuid>",
  "sessionId": "<uuid>",
  "from": "<sender-participant-uuid>",
  "to": "<recipient-participant-uuid>",
  "timestamp": "2026-07-04T14:00:00Z",
  "payload": {}
}
```

`session.joined`, `session.left`, `session.ended`, `participant.disconnected`, `participant.reconnected`, and `error` are server-generated types and are rejected when sent by a client. Routing is constrained to the current session. Errors use the canonical envelope with `type: "error"` and a payload such as `{ "code": "participant_not_found" }`. Other error codes are `invalid_message`, `unsupported_message_type`, `invalid_recipient` and `audio_send_not_authorized`.

`participant.capabilities` and `participant.audio_state_changed` are also emitted by the server, but unlike the types above they may be sent by a client about itself — the server answers with its own authoritative version rather than forwarding the client's frame.

### Reconnect grace period

A dropped signaling socket does not immediately tear down the participant. When the underlying
session is still live, the server holds the participant in a `reconnecting` state for
`Sessions:ParticipantDisconnectGraceSeconds` (default `15`, configurable via
`Sessions__ParticipantDisconnectGraceSeconds`) before finalizing it as left:

1. On disconnect, other participants receive `participant.disconnected` with `{ "participantId": "<uuid>" }`. Treat this as "peer is transiently unreachable" — keep the peer connection and wait, do not tear it down yet.
2. If the same participant (same session and device) reconnects its WebSocket within the grace period, the reused participant row is reported to peers as `participant.reconnected` (same payload shape as `session.joined`: `{ "participantId": "<uuid>", "role": "publisher" | "viewer" }`) instead of a fresh `session.joined`. The reconnecting client itself always gets `session.joined` about itself, as on a first connect, so it can confirm its `participantId`. Clients should resume the existing peer connection on `participant.reconnected`, restarting ICE or renegotiating rather than starting over from scratch.
3. If the grace period elapses without a reconnect, the participant is finalized as disconnected and peers receive the usual `session.left`.

A participant that rejoins via `POST /api/sessions/join` before reopening its WebSocket (a full manual reconnect) also cancels any pending grace period once its new WebSocket connects, so both a lightweight socket-only retry and a full re-authenticate-and-rejoin flow converge on the same `participant.reconnected` signal. Ending a session (`POST /api/sessions/{sessionId}/end`) always wins immediately over a pending grace period.

A viewer mid-grace-period still holds its viewer slot: `POST /api/sessions/join`'s capacity check counts `reconnecting` viewers alongside `connected` ones, so a dropped viewer cannot be displaced by a new one joining during the grace window.

The server never automatically reconnects to a session that has already ended or expired; clients must treat `session.ended`, socket closure without any of the above server messages, and HTTP `410`/`404` as terminal and stop retrying that session.

SDP and ICE payloads are opaque JSON to the API. SDP describes the peer media/session parameters, and ICE candidates describe network paths discovered by the peers. The server forwards those payloads unchanged and never writes their content to logs; routing logs contain only message type, session ID, sender ID, recipient ID, and message ID.

### Offer/answer flow

O Publisher inicia a negociação para cada Viewer. Use o SDP produzido pela biblioteca WebRTC sem analisá-lo ou remontá-lo manualmente:

```json
{
  "type": "webrtc.offer",
  "to": "<viewer-participant-uuid>",
  "payload": { "type": "offer", "sdp": "<sdp-gerado-pelo-webrtc>" }
}
```

O Viewer responde ao participante Publisher indicado em `from`:

```json
{
  "type": "webrtc.answer",
  "to": "<publisher-participant-uuid>",
  "payload": { "type": "answer", "sdp": "<sdp-gerado-pelo-webrtc>" }
}
```

O backend preserva `payload`, mas normaliza os metadados do envelope. O recebimento de signaling não significa que a mídia conectou: cada client deve observar os estados ICE/peer connection e tratar timeout ou reconexão.

### ICE candidate flow

Publisher e Viewer enviam candidates conforme a biblioteca WebRTC os descobre (trickle ICE), sempre direcionados ao outro participante:

```json
{
  "type": "webrtc.ice_candidate",
  "to": "<other-participant-uuid>",
  "payload": {
    "candidate": "candidate:<dados-omitidos>",
    "sdpMid": "0",
    "sdpMLineIndex": 0
  }
}
```

Configure STUN e TURN/coturn nos clients ao criar a peer connection. Essas credenciais não passam pelo payload de signaling e nunca devem ser incorporadas a exemplos, logs ou repositórios públicos.

### Capabilities, mute e renegociação (duplex)

Numa sessão `duplex` os dois lados podem publicar áudio na **mesma** `RTCPeerConnection`
(`sendrecv`), em vez de o Publisher ser o único a adicionar faixa. O fluxo de offer/answer e
ICE acima não muda; o que muda é que ambos anunciam o que fazem e renegociam quando a faixa
local aparece ou some.

Anuncie a própria capacidade logo após `session.joined` (sem `to`):

```json
{
  "type": "participant.capabilities",
  "payload": { "canSendAudio": true, "canReceiveAudio": true }
}
```

O servidor recusa `canSendAudio: true` de um participante sem autorização, respondendo
`error` com `{ "code": "audio_send_not_authorized" }` e **não** aplicando nada da mensagem.
Aceita, ele persiste o estado e envia a versão autoritativa a toda a sessão — inclusive de
volta ao remetente, que assim nunca precisa supor que o pedido foi aplicado literalmente. Os
dois campos são opcionais individualmente, mas a mensagem precisa conter ao menos um deles
(caso contrário: `invalid_message`).

Mute e unmute usam o mesmo padrão (também sem `to`):

```json
{
  "type": "participant.audio_state_changed",
  "payload": { "muted": true }
}
```

O broadcast resultante de ambas as mensagens usa o mesmo payload do `session.joined`
(`participantId`, `role`, `sessionMode`, `audioSendAllowed`, `canSendAudio`, `canReceiveAudio`,
`audioMuted`), com `from` igual ao participante descrito. Trate esse frame como a única fonte
de verdade sobre o que cada peer pode fazer: um peer que anuncia áudio sem
`audioSendAllowed: true` deve ter a faixa recusada ou ignorada localmente.

Para adicionar ou remover uma faixa numa sessão que já está de pé, peça renegociação ao peer em
vez de refazer a sessão:

```json
{
  "type": "webrtc.renegotiate",
  "to": "<other-participant-uuid>",
  "payload": { "reason": "adding-microphone-track" }
}
```

`webrtc.renegotiate` é encaminhado como qualquer outra mensagem direcionada — o `payload` é
opaco para a API. Quem recebe gera uma nova `webrtc.offer` na conexão existente e o ciclo
offer/answer/ICE se repete sem que a sessão, os participantes ou o join code sejam recriados.

Numa chamada 1:1, uma única peer connection bidirecional basta. Para grupos pequenos o modelo
mesh continua funcionando (uma peer connection por par), mas o custo de upload e CPU cresce
rápido; salas grandes exigiriam uma SFU, que está fora do escopo.

### O que o backend valida

- token `DeviceBearer` e upgrade WebSocket;
- formato UUID de `sessionId` (o único parâmetro de consulta do signaling);
- status ativo e versão de credencial do device autenticado (revogação/rotação têm efeito imediato);
- existência e estado/validade temporal da sessão;
- participação do device autenticado na sessão;
- JSON válido, `type` permitido, limite de 64 KiB e frame textual;
- presença/formato de `to` e pertencimento do destinatário à mesma sessão;
- identidade do remetente, derivada do socket autenticado;
- autorização para publicar áudio antes de aceitar `canSendAudio: true`, relida do banco a cada
  mensagem (uma revogação feita por HTTP vale imediatamente, sem esperar o socket reabrir).

### O que o backend não inspeciona

- conteúdo ou validade semântica do SDP;
- conteúdo, alcançabilidade ou prioridade de ICE candidates;
- codec, bitrate, samples ou qualquer pacote de áudio;
- estado interno da peer connection;
- credenciais/configuração STUN/TURN dos clients.

Esse limite é deliberado: o backend coordena peers e trata `payload` como JSON opaco. Validação WebRTC pertence às bibliotecas dos clients.

### Notas de segurança para clients

- Use apenas HTTPS/WSS em produção e valide o certificado do servidor.
- Armazene o segredo de credencial persistente do device e os tokens
  `DeviceBearer` de curta duração no armazenamento seguro da plataforma e
  nunca em logs.
- Não registre SDP, ICE candidates, tokens, códigos de sessão nem credenciais TURN; SDP/ICE podem revelar dados de rede e mídia.
- Aceite mensagens somente pelo socket autenticado e para a sessão/participante esperado, mesmo com a normalização do servidor.
- Trate `error`, `session.left`, `session.ended`, fechamento do socket e expiração como estados normais e limpe recursos.
- Não confunda coturn com a API: coturn pode retransmitir pacotes WebRTC cifrados; a API só retransmite signaling JSON.

### Confusões comuns de iniciantes

- WebSocket conectado não significa áudio conectado; ele apenas permite negociar WebRTC.
- SDP não contém o áudio e ICE candidate não é um pacote de áudio.
- STUN não retransmite mídia; TURN/coturn é o fallback que pode retransmiti-la.
- Opus roda nos clients através do stack WebRTC, não no ASP.NET Core.
- `to` recebe um **participant ID**, não user ID, device ID ou session ID.
- Uma sessão com vários Viewers exige uma peer connection Publisher↔Viewer para cada Viewer no MVP; não existe SFU.
- `duplex` não faz o backend "abrir o microfone": ele só autoriza e propaga estado. Quem captura, envia e recusa faixa não autorizada é o client.

Text messages may be fragmented but may not exceed 64 KiB. Binary frames are rejected. Disconnects broadcast `session.left` to other live participants. When the session becomes terminal, the server sends `session.ended` and closes routing for that connection. There is no persisted signaling history; `SignalingEvent` is mapped in EF Core but the endpoint does not write it.

## Próximos passos dos clients

Este repositório termina no contrato de signaling. O Windows Publisher deve implementar captura WASAPI, criação das peer connections e publicação Opus em seu próprio repositório. O Flutter Viewer deve implementar recepção WebRTC e playback no repositório mobile. Nenhuma dessas responsabilidades deve ser movida para o backend, e o MVP não requer SFU ou outro media server.

Para uma introdução aos conceitos, leia o [guia para leigos](beginner-guide.md). Para os limites arquiteturais e controles existentes, consulte [Architecture](architecture.md) e [Security](security.md).
