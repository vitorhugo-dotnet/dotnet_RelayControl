# Screen-share sessions — design

Evolução do control-plane para suportar sessões de compartilhamento de tela entre
computadores Windows. Fase 0 do SonicDesktopRelay, e a primeira fatia entregue da
[issue #30](https://github.com/vitorhugo-dotnet/dotnet_SonicRelay/issues/30) — a parte de
vídeo e áudio, **sem** controle remoto, `RTCDataChannel` de input ou papel `controller`.

Design do cliente: `desktop_SonicRelay/docs/superpowers/specs/2026-08-23-sonicdesktoprelay-design.md`.
Decisões: `H:\Script\SonicRelay\DECISOES-SonicDesktopRelay.md`.

## Objetivo

Permitir que um app Windows novo — que **publica e assiste** com a mesma identidade — crie e
entre em sessões cujo conteúdo é vídeo de tela mais áudio do sistema, mantendo intactos o
publisher de áudio (`windows_publisher`) e o viewer Flutter (`flutter_viewer`).

A API não muda de natureza. Ela continua sem receber, inspecionar, armazenar ou retransmitir
mídia, e continua sem analisar SDP ([ADR 0001](../../adr/0001-control-plane-only.md)). Vídeo
é, para o servidor, exatamente o que áudio sempre foi: um payload opaco que ele encaminha.

## Princípio que organiza a mudança

Toda alteração é **aditiva**. Nenhum tipo de device existente ganha ou perde escopo, nenhum
modo de sessão existente muda de comportamento, nenhuma resposta existente muda de forma. A
verificação disso não é uma promessa no fim do trabalho: a suíte atual precisa passar sem
edição, e os testes novos precisam afirmar explicitamente o comportamento antigo.

## Mudanças

### 1. Tipo de device `windows_desktop`

`src/SonicRelay.Domain/Devices/DeviceTypes.cs`:

```csharp
public const string WindowsDesktop = "windows_desktop";
```

`DeviceIdentityEndpoints.cs:119-120` passa a aceitar o par `windows_desktop`/`windows`,
somando-se aos dois pares já válidos.

`DeviceCredentialService.ScopesFor` ganha um ramo com a união dos escopos:

```csharp
DeviceTypes.WindowsDesktop =>
[
    "device:read", "device:manage",
    "pairing:create", "pairing:complete", "pairing:revoke",
    "session:create", "session:join", "session:end",
    "signaling:connect", "turn:credentials"
],
```

Os ramos `WindowsPublisher` e `FlutterViewer` não são tocados.

**Consequência a tratar.** Dois comentários no código afirmam hoje que a política
`pairing:create` restringe o chamador a publishers e que `pairing:complete` o restringe a
viewers (`PairingEndpoints.cs:39` e `:64`). A partir daqui isso deixa de valer:
`windows_desktop` tem os dois. Os comentários precisam ser corrigidos, não apagados — eles
documentam por que não existe checagem de tipo ali, e a nova razão é que ambos os papéis são
legítimos para este tipo.

### 2. Modo de sessão `screen_share`

`SessionModes` ganha a constante, entra em `IsSupported` e é aceita por `Normalize`. O valor
tem 12 caracteres e a coluna aceita 16 (`AppDbContext.cs:31`) — **sem migration**.

`SessionAudioPolicy.DefaultsFor` trata `screen_share` como `broadcast`: a origem recebe
`SendAllowed`/`CanSendAudio` e não recebe áudio; os demais recebem e não enviam. Escrever o
ramo explicitamente, em vez de deixar cair no `else` de `duplex`, para que a intenção fique
legível e uma mudança futura em `duplex` não altere sessões de tela por acidente.

A mensagem de erro de modo inválido em `SessionEndpoints.cs:47` passa a listar os três modos.

`POST /api/sessions/{id}/participants/{pid}/audio-permission` continua respondendo
`409 session_not_duplex` para `screen_share` — a permissão de áudio numa sessão de tela é
derivada do papel e não é negociável, como em `broadcast`.

### 3. Join: auto-pareamento escopado, e recusa por tipo de device

Ambas as regras vivem em `AdmitViewerCoreAsync` (`SessionEndpoints.cs:288`), o ponto único
por onde os dois caminhos de join passam. Ordem das verificações, antes do limite de
espectadores:

1. **Tipo de device.** Se `session.Mode == SessionModes.ScreenShare` e o device chamador não
   é `windows_desktop`, responder `403` com `{ "code": "device_type_not_allowed" }`. Vem
   primeiro porque é a regra que protege o viewer Flutter de receber uma offer de vídeo, e
   porque não deve depender do estado de pareamento.
2. **Pareamento.** A verificação atual continua igual para `broadcast` e `duplex`. Para
   `screen_share`, quando não existe pareamento ativo entre o device chamador e
   `session.SourceDeviceId`, criar um `DevicePairing` ativo — `PublisherDeviceId` é a origem
   da sessão, `ViewerDeviceId` é quem entrou — em vez de responder `NotPaired()`.

A sala de rádio pública (`PublicRoomSeeder.VirtualPublisherDeviceId`) mantém sua exceção
atual, intocada.

**Por que o pareamento é criado e não dispensado.** A entidade continua sendo o que sustenta
revogação, listagem em `GET /api/devices/{id}/pairings`, reconexão após rotação de código e
o join sem código via `/discoverable`. Dispensá-la faria a sessão de tela ser o único fluxo
sem trilha de quem teve acesso a quê.

**O que isso enfraquece, dito claramente.** Em sessões de tela o código de 6 caracteres passa
a ser a única credencial de acesso. É uma escolha do dono do produto, tomada com o
trade-off na mesa. O que o backend faz a respeito: mantém o TTL curto já existente, mantém a
rotação de código disponível, e continua entregando a lista de participantes para que o app
mostre permanentemente quem está assistindo.

### 4. Observabilidade

Em `SonicRelayMetrics`, seguindo os nomes da issue #30 e sem rótulo de alta cardinalidade
(nada de `sessionId`, `deviceId` ou IP):

```text
screen_share_sessions_created_total
screen_share_sessions_active
screen_share_join_rejected_total{reason="device_type"|"viewer_limit"|"invalid_code"}
screen_share_auto_pairings_created_total
```

As métricas existentes não mudam de nome nem de rótulo.

### 5. Documentação

`docs/protocol.md`: `screen_share` na tabela de modos, o novo par tipo/plataforma, os escopos
de `windows_desktop`, o código `device_type_not_allowed`, e uma seção curta descrevendo o
join de sessão de tela — incluindo, explicitamente, que ele cria o pareamento.

`docs/device-identity.md`: o novo tipo e seus escopos.

Uma ADR nova, `0008-screen-share-sessions.md`, registrando: por que um tipo de device novo em
vez de ampliar `windows_publisher`; por que a restrição de join é por tipo no servidor; por
que o auto-pareamento é escopado a um modo; e o que isso custa em segurança.

## O que não muda

Vale enumerar, porque é o critério de aceite mais importante:

- `windows_publisher` e `flutter_viewer`: mesmos escopos, mesmos pares tipo/plataforma,
  mesmas respostas.
- `broadcast` e `duplex`: mesmos padrões de áudio, mesma exigência de pareamento prévio no
  join, mesmos códigos de erro.
- Envelope de signaling, tipos de mensagem, roteamento e período de graça de reconexão.
- Emissão de credenciais TURN.
- Schema do banco.

## Testes

Em `tests/SonicRelay.Api.IntegrationTests`:

**Comportamento novo**

- `windows_desktop`/`windows` faz bootstrap; qualquer outra plataforma para esse tipo é
  recusada.
- O token de `windows_desktop` carrega exatamente os dez escopos, nem mais nem menos.
- Criar sessão com `mode: "screen_share"` devolve `201` e projeta o modo.
- Um segundo `windows_desktop`, **sem pareamento prévio**, entra com o código e é admitido; um
  `DevicePairing` ativo passa a existir entre os dois devices.
- Um `flutter_viewer` pareado, com código válido, recebe `403 device_type_not_allowed`.
- Um `windows_publisher` com código válido recebe `403 device_type_not_allowed`.
- Reentrada do mesmo device não cria pareamento duplicado.
- Limite de espectadores continua valendo em `screen_share`.
- `audio-permission` em `screen_share` responde `409 session_not_duplex`.

**Não-regressão — a parte que justifica a fase**

- `windows_publisher` e `flutter_viewer` recebem exatamente as listas de escopo atuais.
- Join em `broadcast` sem pareamento continua respondendo `not_paired`; nenhum
  `DevicePairing` é criado.
- Join em `duplex` sem pareamento idem.
- Os padrões de áudio de `broadcast` e `duplex` permanecem os mesmos.
- Modo desconhecido continua respondendo `400 invalid_session_mode`.
- Modo omitido continua virando `broadcast`.

## Riscos

| Risco | Impacto | Mitigação |
|---|---|---|
| Auto-pareamento vazar para modos de áudio | Alto | Condicionado ao modo dentro do único ponto de admissão, com teste de não-regressão para cada modo |
| Vídeo saturar o coturn | Alto | Métricas direto vs. relay; teto de bitrate no cliente; cota é fase posterior |
| Código como única credencial | Médio | TTL curto, rotação, participantes visíveis; aceite explícito no host fica desenhado e não implementado |
| Comentários desatualizados sobre escopo de pareamento | Baixo | Corrigidos na mesma mudança que os invalida |
