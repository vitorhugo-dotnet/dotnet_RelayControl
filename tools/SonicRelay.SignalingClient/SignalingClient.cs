using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SonicRelay.SignalingClient;

public sealed class SignalingClient(Uri baseUrl, TextWriter output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri _baseUrl = EnsureTrailingSlash(baseUrl);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = _baseUrl };
        var suffix = Guid.NewGuid().ToString("N");
        const string password = "FakeTest1!Password";

        var publisher = await CreateUserAsync(http, $"fake-publisher-{suffix}@example.test", password,
            cancellationToken);
        var viewer = await CreateUserAsync(http, $"fake-viewer-{suffix}@example.test", password,
            cancellationToken);
        await output.WriteLineAsync("[ok] authenticated fake publisher and viewer");

        var publisherDeviceId = await CreateDeviceAsync(http, publisher,
            "Fake test publisher", "windows_publisher", "windows", cancellationToken);
        var viewerDeviceId = await CreateDeviceAsync(http, viewer,
            "Fake test viewer", "flutter_viewer", "android", cancellationToken);
        await output.WriteLineAsync("[ok] registered fake publisher and viewer devices");

        var (sessionId, code) = await CreateSessionAsync(http, publisher, publisherDeviceId, cancellationToken);
        await JoinSessionAsync(http, viewer, viewerDeviceId, code, cancellationToken);
        await output.WriteLineAsync($"[ok] created and joined session {sessionId} with test code {code}");

        using var publisherSocket = await ConnectAsync(publisher, sessionId, publisherDeviceId, cancellationToken);
        var publisherParticipantId = await ReadJoinedParticipantAsync(publisherSocket, sessionId, cancellationToken);
        using var viewerSocket = await ConnectAsync(viewer, sessionId, viewerDeviceId, cancellationToken);
        var viewerParticipantId = await ReadJoinedParticipantAsync(viewerSocket, sessionId, cancellationToken);
        await output.WriteLineAsync("[ok] opened two authenticated signaling WebSockets");

        try
        {
            await RouteAndValidateAsync(publisherSocket, viewerSocket, "publisher.ready",
                sessionId, publisherParticipantId, viewerParticipantId,
                new { marker = "fake-test-publisher-ready" }, cancellationToken);
            await output.WriteLineAsync("[ok] routed publisher.ready");

            await RouteAndValidateAsync(viewerSocket, publisherSocket, "viewer.ready",
                sessionId, viewerParticipantId, publisherParticipantId,
                new { marker = "fake-test-viewer-ready" }, cancellationToken);
            await output.WriteLineAsync("[ok] routed viewer.ready");

            await RouteAndValidateAsync(publisherSocket, viewerSocket, "webrtc.offer",
                sessionId, publisherParticipantId, viewerParticipantId,
                new { sdp = "fake-test-offer-sdp" }, cancellationToken);
            await output.WriteLineAsync("[ok] routed webrtc.offer with fake test SDP");

            await RouteAndValidateAsync(viewerSocket, publisherSocket, "webrtc.answer",
                sessionId, viewerParticipantId, publisherParticipantId,
                new { sdp = "fake-test-answer-sdp" }, cancellationToken);
            await output.WriteLineAsync("[ok] routed webrtc.answer with fake test SDP");

            await RouteAndValidateAsync(publisherSocket, viewerSocket, "webrtc.ice_candidate",
                sessionId, publisherParticipantId, viewerParticipantId,
                new { candidate = "fake-test-ice-candidate", sdpMid = "fake-test-audio", sdpMLineIndex = 0 },
                cancellationToken);
            await output.WriteLineAsync("[ok] routed webrtc.ice_candidate with fake test ICE data");
        }
        finally
        {
            await CloseNormallyAsync(viewerSocket);
            await CloseNormallyAsync(publisherSocket);
        }

        await output.WriteLineAsync("[ok] fake signaling flow completed; no audio or real WebRTC was used");
    }

    private async Task<string> CreateUserAsync(HttpClient http, string email, string password,
        CancellationToken cancellationToken)
    {
        await PostAsync(http, "auth/register", new { email, password }, null, cancellationToken);
        using var login = await PostAsync(http, "auth/login?useCookies=false",
            new { email, password }, null, cancellationToken);
        return RequiredString(login.RootElement, "accessToken");
    }

    private static async Task<Guid> CreateDeviceAsync(HttpClient http, string accessToken,
        string name, string type, string platform, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(http, "api/devices/",
            new { name, type, platform, publicKey = (string?)null }, accessToken, cancellationToken);
        return response.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<(Guid SessionId, string Code)> CreateSessionAsync(HttpClient http,
        string accessToken, Guid sourceDeviceId, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(http, "api/sessions/",
            new { sourceDeviceId, maxViewers = 1 }, accessToken, cancellationToken);
        return (response.RootElement.GetProperty("id").GetGuid(), RequiredString(response.RootElement, "code"));
    }

    private static async Task JoinSessionAsync(HttpClient http, string accessToken, Guid deviceId,
        string code, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(http, "api/sessions/join",
            new { code, deviceId }, accessToken, cancellationToken);
    }

    private async Task<ClientWebSocket> ConnectAsync(string accessToken, Guid sessionId, Guid deviceId,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        var builder = new UriBuilder(new Uri(_baseUrl, "ws/signaling"))
        {
            Scheme = _baseUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Query = $"sessionId={sessionId}&deviceId={deviceId}"
        };
        try
        {
            await socket.ConnectAsync(builder.Uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<Guid> ReadJoinedParticipantAsync(ClientWebSocket socket, Guid sessionId,
        CancellationToken cancellationToken)
    {
        var envelope = await ReceiveAsync(socket, cancellationToken);
        if (envelope.GetProperty("type").GetString() != "session.joined"
            || envelope.GetProperty("sessionId").GetGuid() != sessionId)
            throw new InvalidOperationException("Expected the server session.joined envelope.");
        return envelope.GetProperty("payload").GetProperty("participantId").GetGuid();
    }

    private static async Task RouteAndValidateAsync(ClientWebSocket senderSocket,
        ClientWebSocket receiverSocket, string type, Guid sessionId, Guid senderParticipantId,
        Guid receiverParticipantId, object payload, CancellationToken cancellationToken)
    {
        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var message = SignalingMessage.Create(type, receiverParticipantId, payloadElement);
        await SendAsync(senderSocket, message, cancellationToken);
        // The server also pushes announcements about other participants (session.joined,
        // participant.capabilities for the peers already connected, and so on) onto the same
        // socket. They are not what this step is validating and their exact set grows as the
        // protocol does, so skip anything that is not the routed message we just sent.
        JsonElement routed;
        do
        {
            routed = await ReceiveAsync(receiverSocket, cancellationToken);
        } while (routed.GetProperty("type").GetString() != type);
        SignalingMessage.ValidateRouted(routed, type, message.MessageId, sessionId,
            senderParticipantId, receiverParticipantId);
        if (!JsonElement.DeepEquals(message.Payload, routed.GetProperty("payload")))
            throw new InvalidOperationException($"Routed {type} payload did not preserve fake test data.");
    }

    private static async Task SendAsync(ClientWebSocket socket, SignalingMessage message,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Signaling socket closed before the expected message arrived.");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("Signaling server returned a non-text frame.");
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), timeout.Token);
        } while (!result.EndOfMessage);

        stream.Position = 0;
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        return document.RootElement.Clone();
    }

    private static async Task<JsonDocument> PostAsync(HttpClient http, string path, object body,
        string? accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        if (accessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"POST {path} failed with {(int)response.StatusCode}: {content}");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content);
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidOperationException($"Response property '{propertyName}' was null.");

    private static async Task CloseNormallyAsync(ClientWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fake test complete", timeout.Token);
        }
        catch (WebSocketException)
        {
            // The peer may finish its close first; disposal below still releases local resources.
        }
        catch (OperationCanceledException)
        {
            // Do not hide a completed validation flow when the peer does not finish the close handshake.
        }
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
