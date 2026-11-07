using System.Buffers.Text;
using System.IO;
using System.IO.Compression;
using System.Text;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Call;

namespace DinoTranslator.Desktop.Tests;

public class CallCodesTests
{
    public const string Fingerprint = "D2:B9:31:8F:DF:24:D8:0E:ED:D2:EF:25:9E:AF:6F:B8:34:AE:53:9C:E6:F3:8F:F2:64:15:FA:E8:7F:53:2D:38";

    /// <summary>SDP como a que o WebView2 gera para um canal de dados.</summary>
    public const string Sdp =
        "v=0\r\n" +
        "o=- 4611731400430051336 2 IN IP4 127.0.0.1\r\n" +
        "s=-\r\n" +
        "t=0 0\r\n" +
        "a=group:BUNDLE 0\r\n" +
        "a=extmap-allow-mixed\r\n" +
        "a=msid-semantic: WMS\r\n" +
        "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n" +
        "c=IN IP4 0.0.0.0\r\n" +
        "a=candidate:1467250027 1 udp 2122260223 0a1b2c3d-1111-2222-3333-444455556666.local 54321 typ host generation 0 network-id 1\r\n" +
        "a=candidate:842163049 1 udp 1686052607 203.0.113.7 54321 typ srflx raddr 0.0.0.0 rport 0 generation 0 network-id 1\r\n" +
        "a=ice-ufrag:EsAw\r\n" +
        "a=ice-pwd:bP+XJMM09aR8AiX1jdukzR6Y\r\n" +
        "a=ice-options:trickle\r\n" +
        "a=fingerprint:sha-256 " + Fingerprint + "\r\n" +
        "a=setup:actpass\r\n" +
        "a=mid:0\r\n" +
        "a=sctp-port:5000\r\n" +
        "a=max-message-size:262144\r\n";

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 18, 0, 0, TimeSpan.Zero);

    private static readonly Profile Anna = new("Anna", Languages.Russian, Gender.Female, Formality.Informal, "Europe/Moscow");

    public static CallCode Invite(string? sdp = null) =>
        new(CallCodes.CurrentVersion, CallCodeKind.Invite, CallCodes.NewCallId(), Now, Anna, sdp ?? Sdp);

    [Fact]
    public void Convite_vai_e_volta_igual()
    {
        var invite = Invite();

        var text = CallCodes.Encode(invite);

        Assert.StartsWith("DT1.", text);
        Assert.Equal(invite, CallCodes.Decode(text, CallCodeKind.Invite, Now));
    }

    [Fact]
    public void Codigo_cabe_numa_mensagem_de_celular()
    {
        Assert.InRange(CallCodes.Encode(Invite()).Length, 100, 1_500);
    }

    [Fact]
    public void Espacos_e_quebras_de_linha_do_mensageiro_sao_ignorados()
    {
        var text = CallCodes.Encode(Invite());
        var pasted = "  " + text[..40] + "\r\n" + text[40..80] + " \n" + text[80..] + "\n";

        Assert.Equal(Invite().CallId.Length, CallCodes.Decode(pasted, CallCodeKind.Invite, Now).CallId.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("oi, tudo bem?")]
    [InlineData("DT2.abc")]
    [InlineData("DT1.!!!não-é-base64!!!")]
    [InlineData("DT1.AAAA")]
    public void Texto_que_nao_e_codigo_e_recusado(string text)
    {
        Assert.Throws<CallCodeException>(() => CallCodes.Decode(text, CallCodeKind.Invite, Now));
    }

    [Fact]
    public void Codigo_cortado_ao_meio_e_recusado()
    {
        var text = CallCodes.Encode(Invite());

        Assert.Throws<CallCodeException>(() => CallCodes.Decode(text[..(text.Length / 2)], CallCodeKind.Invite, Now));
    }

    [Fact]
    public void Codigo_com_um_caractere_trocado_e_recusado_ou_muda_o_conteudo()
    {
        var invite = Invite();
        var text = CallCodes.Encode(invite);
        var middle = text.Length / 2;
        var tampered = text[..middle] + (text[middle] == 'A' ? 'B' : 'A') + text[(middle + 1)..];

        try
        {
            Assert.NotEqual(invite, CallCodes.Decode(tampered, CallCodeKind.Invite, Now));
        }
        catch (CallCodeException)
        {
            // Recusado: também é o comportamento esperado.
        }
    }

    [Fact]
    public void Compressao_explosiva_e_recusada_sem_descomprimir_tudo()
    {
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            // 4 MB de zeros viram ~4 KB: cabe no limite do texto e testa o limite de descompressão.
            deflate.Write(new byte[4 * 1024 * 1024]);
        }
        var bomb = CallCodes.Prefix + Base64Url.EncodeToString(compressed.ToArray());

        Assert.True(bomb.Length < CallCodes.MaxEncodedLength);
        Assert.Throws<CallCodeException>(() => CallCodes.Decode(bomb, CallCodeKind.Invite, Now));
    }

    [Fact]
    public void Codigo_gigante_e_recusado()
    {
        Assert.Throws<CallCodeException>(() =>
            CallCodes.Decode(CallCodes.Prefix + new string('A', CallCodes.MaxEncodedLength * 3), CallCodeKind.Invite, Now));
    }

    [Fact]
    public void Campo_desconhecido_no_json_e_recusado()
    {
        var json = $$"""{"version":1,"kind":"Invite","callId":"{{CallCodes.NewCallId()}}","createdAt":"2026-11-07T18:00:00+00:00","profile":{"name":"Anna","language":"ru-RU","gender":"Female","formality":"Informal","timeZone":"Europe/Moscow"},"sdp":"x","admin":true}""";

        Assert.Throws<CallCodeException>(() => CallCodes.Decode(Pack(json), CallCodeKind.Invite, Now));
    }

    [Fact]
    public void Resposta_no_lugar_do_convite_explica_o_engano()
    {
        var answer = Invite() with { Kind = CallCodeKind.Answer };

        var error = Assert.Throws<CallCodeException>(() =>
            CallCodes.Decode(CallCodes.Encode(answer), CallCodeKind.Invite, Now));

        Assert.Contains("código de resposta", error.Message);
    }

    [Theory]
    [InlineData(-36)]
    [InlineData(10)]
    public void Codigo_vencido_ou_do_futuro_e_recusado(int minutesFromNow)
    {
        var invite = Invite() with { CreatedAt = Now.AddMinutes(minutesFromNow) };

        var error = Assert.Throws<CallCodeException>(() =>
            CallCodes.Decode(CallCodes.Encode(invite), CallCodeKind.Invite, Now));

        Assert.Contains("expirou", error.Message);
    }

    [Theory]
    [InlineData(-34)]
    [InlineData(4)]
    public void Relogios_um_pouco_fora_de_sincronia_sao_tolerados(int minutesFromNow)
    {
        var invite = Invite() with { CreatedAt = Now.AddMinutes(minutesFromNow) };

        CallCodes.Decode(CallCodes.Encode(invite), CallCodeKind.Invite, Now);
    }

    [Fact]
    public void Versao_desconhecida_pede_atualizacao()
    {
        var error = Assert.Throws<CallCodeException>(() => CallCodes.Encode(Invite() with { Version = 2 }));

        Assert.Contains("versão", error.Message);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public void Id_da_chamada_invalido_e_recusado(string callId)
    {
        Assert.Throws<CallCodeException>(() => CallCodes.Encode(Invite() with { CallId = callId }));
    }

    [Fact]
    public void Perfil_invalido_no_codigo_e_recusado()
    {
        var invite = Invite() with { Profile = Anna with { Name = "Anna‮" } };

        Assert.Throws<CallCodeException>(() => CallCodes.Encode(invite));
    }

    [Theory]
    [InlineData("m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n", "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n")]
    [InlineData("a=fingerprint:sha-256 " + Fingerprint + "\r\n", "")]
    [InlineData("a=fingerprint:sha-256 " + Fingerprint + "\r\n", "a=fingerprint:sha-1 D2:B9:31:8F:DF:24:D8:0E:ED:D2:EF:25:9E:AF:6F:B8:34:AE:53:9C\r\n")]
    [InlineData("a=ice-pwd:bP+XJMM09aR8AiX1jdukzR6Y\r\n", "")]
    [InlineData("v=0\r\n", "v=1\r\n")]
    [InlineData("s=-\r\n", "s=\u0007\r\n")]
    [InlineData("s=-\r\n", "s=é\r\n")]
    public void Sdp_fora_do_esperado_e_recusada(string original, string replacement)
    {
        Assert.Throws<CallCodeException>(() => CallCodes.Encode(Invite(Sdp.Replace(original, replacement))));
    }

    [Fact]
    public void Muitos_candidatos_sao_recusados()
    {
        var candidates = string.Concat(Enumerable.Range(0, 17).Select(i =>
            $"a=candidate:{i} 1 udp 1 203.0.113.{i} 5000 typ srflx raddr 0.0.0.0 rport 0\r\n"));

        Assert.Throws<CallCodeException>(() => CallCodes.Encode(Invite(Sdp.Replace("a=ice-ufrag:", candidates + "a=ice-ufrag:"))));
    }

    [Fact]
    public void Impressao_digital_e_lida_em_maiusculas()
    {
        Assert.Equal(Fingerprint, SessionDescriptions.Fingerprint(Sdp.Replace(Fingerprint, Fingerprint.ToLowerInvariant())));
    }

    [Fact]
    public void Codigo_de_seguranca_e_o_mesmo_dos_dois_lados()
    {
        const string other = "11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00";

        var host = CallCodes.SecurityCode(Fingerprint, other);
        var guest = CallCodes.SecurityCode(other, Fingerprint);

        Assert.Equal(host, guest);
        Assert.Matches("^[0-9]{4} [0-9]{4} [0-9]{4}$", host);
    }

    [Fact]
    public void Chave_trocada_no_caminho_muda_o_codigo_de_seguranca()
    {
        const string guest = "11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00";
        const string attacker = "AA:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00";

        Assert.NotEqual(CallCodes.SecurityCode(Fingerprint, guest), CallCodes.SecurityCode(Fingerprint, attacker));
    }

    [Fact]
    public void Ids_de_chamada_sao_aleatorios()
    {
        Assert.Equal(100, Enumerable.Range(0, 100).Select(_ => CallCodes.NewCallId()).Distinct().Count());
    }

    private static string Pack(string json)
    {
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(Encoding.UTF8.GetBytes(json));
        }
        return CallCodes.Prefix + Base64Url.EncodeToString(compressed.ToArray());
    }
}
