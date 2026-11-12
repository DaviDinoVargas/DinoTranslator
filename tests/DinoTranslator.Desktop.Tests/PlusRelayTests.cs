using System.Text.Json;
using DinoTranslator.Desktop.Services.Plus;
using DinoTranslator.Shared.Room;

namespace DinoTranslator.Desktop.Tests;

/// <summary>Conexão garantida: o que vem do servidor antes de chegar ao WebRTC.</summary>
public sealed class PlusRelayTests
{
    [Fact]
    public void So_turn_com_credencial_entra_na_chamada()
    {
        var response = new IceServersResponse(
        [
            new IceServer(["stun:turn.exemplo:3478"], null, null),
            new IceServer(["turn:turn.exemplo:3478?transport=udp", "https://outro.exemplo", "turns:turn.exemplo:5349"], "1700000000:abc", "c2VncmVkbw=="),
            new IceServer(["turn:sem-credencial.exemplo:3478"], null, null),
            new IceServer(["turn:longa.exemplo:3478"], "1700000000:abc", new string('x', 300)),
        ], DateTimeOffset.UtcNow.AddHours(12));

        var relay = PlusRelay.ToRtc(response);

        var server = Assert.Single(relay);
        Assert.Equal(["turn:turn.exemplo:3478?transport=udp", "turns:turn.exemplo:5349"], server.Urls);
        Assert.Equal("1700000000:abc", server.Username);
        Assert.Equal("c2VncmVkbw==", server.Credential);
    }

    [Fact]
    public void Chega_ao_javascript_no_formato_do_RTCIceServer()
    {
        var server = new RtcIceServer(["turn:turn.exemplo:3478"], "1700000000:abc", "c2VncmVkbw==");

        // O Blazor serializa com as opções da Web (camelCase), como aqui.
        var json = JsonSerializer.Serialize(server, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("""{"urls":["turn:turn.exemplo:3478"],"username":"1700000000:abc","credential":"c2VncmVkbw=="}""", json);
    }
}
