using DinoTranslator.Desktop.Services;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Call;

namespace DinoTranslator.Desktop.Tests;

public class LiveTranslationTests
{
    private static readonly Profile Dino = new("Dino", Languages.PortugueseBrazil, Gender.Male, Formality.Informal, "America/Sao_Paulo");
    private static readonly Profile Anna = new("Anna", Languages.Russian, Gender.Female, Formality.Informal, "Europe/Moscow");

    [Theory]
    [InlineData(CallRole.Host, true, true, TranslatorSide.Here)]
    [InlineData(CallRole.Host, true, false, TranslatorSide.Here)]
    [InlineData(CallRole.Host, false, true, TranslatorSide.There)]
    [InlineData(CallRole.Host, false, false, TranslatorSide.None)]
    [InlineData(CallRole.Guest, true, true, TranslatorSide.There)]
    [InlineData(CallRole.Guest, true, false, TranslatorSide.Here)]
    [InlineData(CallRole.Guest, false, true, TranslatorSide.There)]
    [InlineData(CallRole.Guest, false, false, TranslatorSide.None)]
    public void Anfitriao_traduz_se_puder_senao_o_convidado(CallRole role, bool here, bool there, TranslatorSide expected)
    {
        Assert.Equal(expected, TranslatorChoice.Decide(role, here, there));
    }

    [Fact]
    public void Os_dois_lados_chegam_a_mesma_escolha()
    {
        foreach (var hostCan in new[] { true, false })
        {
            foreach (var guestCan in new[] { true, false })
            {
                var host = TranslatorChoice.Decide(CallRole.Host, hostCan, guestCan);
                var guest = TranslatorChoice.Decide(CallRole.Guest, guestCan, hostCan);
                Assert.Equal(host switch { TranslatorSide.Here => TranslatorSide.There, TranslatorSide.There => TranslatorSide.Here, _ => TranslatorSide.None }, guest);
            }
        }
    }

    [Fact]
    public async Task Abre_um_fluxo_por_pessoa_com_os_idiomas_cruzados()
    {
        var engine = new FakeEngine();
        await using var translation = new LiveTranslation(engine, Dino, Anna, TimeProvider.System);

        await translation.StartAsync(["Anna"], CancellationToken.None);

        Assert.Equal(
            [new SpeechStreamOptions(Languages.PortugueseBrazil, Languages.Russian, ["Anna"]),
             new SpeechStreamOptions(Languages.Russian, Languages.PortugueseBrazil, ["Anna"])],
            engine.Streams.Select(s => s.Options),
            new OptionsComparer());
    }

    [Fact]
    public async Task Microfone_vai_para_a_sua_fala_e_audio_da_chamada_para_a_dela()
    {
        var engine = new FakeEngine();
        await using var translation = new LiveTranslation(engine, Dino, Anna, TimeProvider.System);
        await translation.StartAsync([], CancellationToken.None);

        translation.WriteAudio("local", new byte[3200]);
        translation.WriteAudio("remote", new byte[100]);
        translation.WriteAudio("outro", new byte[7]);

        Assert.Equal([3200], engine.Streams[0].Stream.Written);
        Assert.Equal([100], engine.Streams[1].Stream.Written);
    }

    [Fact]
    public async Task Cada_fala_vira_legenda_de_quem_falou()
    {
        var engine = new FakeEngine();
        await using var translation = new LiveTranslation(engine, Dino, Anna, TimeProvider.System);
        var captions = new List<Caption>();
        translation.CaptionReady += captions.Add;
        await translation.StartAsync([], CancellationToken.None);

        engine.Streams[0].Stream.Emit(new SpeechUpdate(0, 0, "Oi, Anna", "Привет, Анна", Final: false));
        engine.Streams[1].Stream.Emit(new SpeechUpdate(3, 1, "Привет!", "Oi!", Final: true));

        Assert.Collection(
            captions,
            mine =>
            {
                Assert.Equal(CaptionOwner.Me, mine.Owner);
                Assert.Equal("s0", mine.SegmentId);
                Assert.Equal(Languages.PortugueseBrazil, mine.SourceLanguage);
                Assert.Equal("Привет, Анна", mine.TranslatedText);
                Assert.False(mine.Final);
            },
            theirs =>
            {
                Assert.Equal(CaptionOwner.Other, theirs.Owner);
                Assert.Equal("s3", theirs.SegmentId);
                Assert.Equal(Languages.Russian, theirs.SourceLanguage);
                Assert.Equal("Oi!", theirs.TranslatedText);
                Assert.True(theirs.Final);
            });
    }

    [Fact]
    public async Task Mesmo_idioma_mostra_so_a_transcricao()
    {
        var engine = new FakeEngine();
        var spanish = Dino with { Language = Languages.Spanish };
        await using var translation = new LiveTranslation(engine, spanish, Anna with { Language = Languages.Spanish }, TimeProvider.System);
        Caption? caption = null;
        translation.CaptionReady += c => caption = c;
        await translation.StartAsync([], CancellationToken.None);

        engine.Streams[0].Stream.Emit(new SpeechUpdate(0, 0, "Hola", "Hola", Final: true));

        Assert.Null(caption!.TranslatedText);
    }

    [Fact]
    public async Task Falha_do_motor_vira_aviso()
    {
        var engine = new FakeEngine();
        await using var translation = new LiveTranslation(engine, Dino, Anna, TimeProvider.System);
        string? failure = null;
        translation.Failed += m => failure = m;
        await translation.StartAsync([], CancellationToken.None);

        engine.Streams[1].Stream.Fail("Sem conexão com o serviço.");

        Assert.Equal("Sem conexão com o serviço.", failure);
    }

    [Fact]
    public async Task Encerrar_fecha_os_dois_fluxos()
    {
        var engine = new FakeEngine();
        var translation = new LiveTranslation(engine, Dino, Anna, TimeProvider.System);
        await translation.StartAsync([], CancellationToken.None);

        await translation.DisposeAsync();

        Assert.All(engine.Streams, s => Assert.True(s.Stream.Disposed));
    }

    [Fact]
    public void Texto_do_motor_sai_limpo_e_no_tamanho_maximo()
    {
        Assert.Equal("linha um  linha dois", LiveTranslation.Clean(" linha um\r\nlinha dois‮ "));

        var longText = new string('a', CaptionMessage.MaxTextLength) + "fim";
        var cleaned = LiveTranslation.Clean(longText);

        Assert.Equal(CaptionMessage.MaxTextLength, cleaned.Length);
        Assert.EndsWith("fim", cleaned);
        Assert.True(AppMessages.IsDisplayableText(cleaned));
    }

    private sealed class FakeEngine : ISpeechTranslationEngine
    {
        public List<(SpeechStreamOptions Options, FakeStream Stream)> Streams { get; } = [];

        public Task<ISpeechTranslationStream> StartAsync(SpeechStreamOptions options, CancellationToken cancellationToken)
        {
            var stream = new FakeStream();
            Streams.Add((options, stream));
            return Task.FromResult<ISpeechTranslationStream>(stream);
        }
    }

    private sealed class FakeStream : ISpeechTranslationStream
    {
        public event Action<SpeechUpdate>? Updated;

        public event Action<string>? Failed;

        public List<int> Written { get; } = [];

        public bool Disposed { get; private set; }

        public void Write(byte[] pcm) => Written.Add(pcm.Length);

        public void Emit(SpeechUpdate update) => Updated?.Invoke(update);

        public void Fail(string message) => Failed?.Invoke(message);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OptionsComparer : IEqualityComparer<SpeechStreamOptions>
    {
        public bool Equals(SpeechStreamOptions? x, SpeechStreamOptions? y) =>
            x!.SourceLanguage == y!.SourceLanguage && x.TargetLanguage == y.TargetLanguage && x.Phrases.SequenceEqual(y.Phrases);

        public int GetHashCode(SpeechStreamOptions obj) => HashCode.Combine(obj.SourceLanguage, obj.TargetLanguage);
    }
}
