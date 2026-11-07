using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using DinoTranslator.Desktop.Services;
using DinoTranslator.Shared.Call;

namespace DinoTranslator.Desktop.Tests;

public sealed class RendezvousTests
{
    private static readonly string CallId = CallCodes.NewCallId();

    [Fact]
    public void Resposta_cifrada_volta_igual_so_para_quem_tem_o_convite()
    {
        var host = new CallRendezvous(CallId);
        var guest = new CallRendezvous(CallId);
        var sealedText = guest.Seal(new RendezvousMessage(RendezvousMessage.Answer, "DT1.resposta"));

        Assert.Equal(host.Topic, guest.Topic);
        Assert.Matches("^dt[0-9a-f]{32}$", host.Topic);
        Assert.DoesNotContain("resposta", sealedText);
        Assert.Equal(new RendezvousMessage(RendezvousMessage.Answer, "DT1.resposta"), host.Open(sealedText));
        Assert.Null(new CallRendezvous(CallCodes.NewCallId()).Open(sealedText));
    }

    [Fact]
    public void Topico_nao_revela_o_id_da_chamada()
    {
        Assert.DoesNotContain(CallId, new CallRendezvous(CallId).Topic);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é base64!")]
    [InlineData("AAAA")]
    public void Mensagem_estranha_e_ignorada(string? text)
    {
        Assert.Null(new CallRendezvous(CallId).Open(text));
    }

    [Fact]
    public void Mensagem_adulterada_e_ignorada()
    {
        var rendezvous = new CallRendezvous(CallId);
        var sealedText = rendezvous.Seal(new RendezvousMessage(RendezvousMessage.Answer, "DT1.resposta"));
        var tampered = (sealedText[20] == 'A' ? "B" : "A") is var c ? sealedText[..20] + c + sealedText[21..] : sealedText;

        Assert.Null(rendezvous.Open(tampered));
        Assert.Null(rendezvous.Open(new string('A', CallRendezvous.MaxMessageLength + 1)));
    }

    [Fact]
    public void Tipo_desconhecido_e_ignorado()
    {
        var rendezvous = new CallRendezvous(CallId);
        Assert.Null(rendezvous.Open(rendezvous.Seal(new RendezvousMessage("outra-coisa", "DT1.x"))));
    }

    [Fact]
    public void Linha_do_servico_so_vale_se_for_mensagem_do_topico()
    {
        Assert.Equal(("a1", "texto"), RendezvousRelay.Parse("""{"id":"a1","event":"message","topic":"dtabc","message":"texto"}""", "dtabc"));
        Assert.Null(RendezvousRelay.Parse("""{"id":"a1","event":"keepalive","topic":"dtabc"}""", "dtabc"));
        Assert.Null(RendezvousRelay.Parse("""{"id":"a1","event":"message","topic":"outro","message":"texto"}""", "dtabc"));
        Assert.Null(RendezvousRelay.Parse("""{"id":1,"event":"message","topic":"dtabc","message":"texto"}""", "dtabc"));
        Assert.Null(RendezvousRelay.Parse("não é json", "dtabc"));
        Assert.Null(RendezvousRelay.Parse("[1,2]", "dtabc"));
    }

    [Fact]
    public async Task Linha_gigante_do_servico_e_descartada_sem_estourar_a_memoria()
    {
        var text = new string('x', 100_000) + "\n{\"ok\":1}\n";
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));

        Assert.Equal("{\"ok\":1}", await RendezvousRelay.ReadLineAsync(reader, CancellationToken.None));
        Assert.Null(await RendezvousRelay.ReadLineAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task Publica_no_topico_sem_repassar_para_celulares()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var relay = new RendezvousRelay(new HttpClient(handler), new Uri("https://encontro.exemplo/"));

        Assert.True(await relay.PublishAsync("dtabc", "cifrado", CancellationToken.None));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://encontro.exemplo/dtabc", request.RequestUri!.AbsoluteUri);
        Assert.Equal("no", Assert.Single(request.Headers.GetValues("Firebase")));
    }

    [Fact]
    public async Task Servico_fora_do_ar_nao_e_erro_so_devolve_falso()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("sem rede"));
        var relay = new RendezvousRelay(new HttpClient(handler), new Uri("https://encontro.exemplo/"));

        Assert.False(await relay.PublishAsync("dtabc", "cifrado", CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Acompanha_o_topico_e_entrega_cada_mensagem_uma_vez()
    {
        var since = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        const string Stream = """
            {"id":"m1","event":"open","topic":"dtabc"}
            {"id":"m2","event":"message","topic":"dtabc","message":"primeira"}
            {"id":"m2","event":"message","topic":"dtabc","message":"primeira"}
            {"id":"m3","event":"message","topic":"dtabc","message":"segunda"}
            """;
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Stream) });
        var relay = new RendezvousRelay(new HttpClient(handler), new Uri("https://encontro.exemplo/"));
        using var stop = new CancellationTokenSource();
        var received = new List<string>();

        await relay.ListenAsync("dtabc", since, message =>
        {
            received.Add(message);
            if (received.Count == 2)
            {
                stop.Cancel();
            }
            return Task.CompletedTask;
        }, stop.Token);

        Assert.Equal(["primeira", "segunda"], received);
        Assert.Equal("https://encontro.exemplo/dtabc/json?since=1800000000", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Desligado_nao_acessa_a_rede()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("não deveria chamar"));
        var relay = new RendezvousRelay(new HttpClient(handler), null);

        Assert.False(relay.Enabled);
        Assert.False(await relay.PublishAsync("dtabc", "x", CancellationToken.None));
        await relay.ListenAsync("dtabc", DateTimeOffset.UtcNow, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null, "https://ntfy.sh/")]
    [InlineData("", null)]
    [InlineData("desligado", null)]
    [InlineData("https://ntfy.minha-casa.com.br", "https://ntfy.minha-casa.com.br/")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/")]
    public void Servico_do_encontro_vem_da_configuracao(string? value, string? expected)
    {
        Assert.Equal(expected, DesktopSettings.ParseRendezvousUri(value)?.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://ntfy.sh")]
    [InlineData("ftp://ntfy.sh")]
    [InlineData("https://usuario:senha@ntfy.sh")]
    [InlineData("https://ntfy.sh/?x=1")]
    [InlineData("ntfy.sh")]
    public void Servico_do_encontro_sem_https_e_recusado(string value)
    {
        Assert.Throws<InvalidOperationException>(() => DesktopSettings.ParseRendezvousUri(value));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
