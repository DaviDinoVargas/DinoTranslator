using System.IO;
using System.Text;
using DinoTranslator.Desktop.Services.Translation;

namespace DinoTranslator.Desktop.Tests;

public sealed class TranslationSettingsTests : IDisposable
{
    private const string Key = "0123456789abcdef0123456789ABCDEF";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dinotranslator-motores-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_folder, "motores.dat");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Sem_arquivo_nao_ha_motor()
    {
        Assert.False(new TranslationSettingsStore(SettingsPath).Current.IsConfigured);
    }

    [Fact]
    public void Configuracao_salva_e_lida_de_volta()
    {
        var settings = new TranslationSettings(TranslationProvider.AzureSpeech, "brazilsouth", Key);
        var changed = false;
        var store = new TranslationSettingsStore(SettingsPath);
        store.Changed += () => changed = true;

        store.Save(settings);

        Assert.True(changed);
        Assert.Equal(settings, new TranslationSettingsStore(SettingsPath).Current);
    }

    [Fact]
    public void Chave_nao_fica_legivel_no_disco()
    {
        new TranslationSettingsStore(SettingsPath).Save(new TranslationSettings(TranslationProvider.AzureSpeech, "brazilsouth", Key));

        var bytes = File.ReadAllBytes(SettingsPath);

        Assert.DoesNotContain(Key, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("brazilsouth", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Arquivo_adulterado_e_ignorado()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, """{"provider":"AzureSpeech","region":"brazilsouth","key":"x"}""");

        Assert.False(new TranslationSettingsStore(SettingsPath).Current.IsConfigured);
    }

    [Fact]
    public void Remover_apaga_a_chave()
    {
        var store = new TranslationSettingsStore(SettingsPath);
        store.Save(new TranslationSettings(TranslationProvider.AzureSpeech, "brazilsouth", Key));

        store.Clear();

        Assert.False(File.Exists(SettingsPath));
        Assert.False(store.Current.IsConfigured);
    }

    [Theory]
    [InlineData("Brazil South", Key)]
    [InlineData("brazilsouth;x", Key)]
    [InlineData("brazilsouth", "curta")]
    [InlineData("brazilsouth", "chave com espaço 0123456789abcdef")]
    [InlineData(null, Key)]
    public void Regiao_ou_chave_invalida_e_recusada(string? region, string key)
    {
        var settings = new TranslationSettings(TranslationProvider.AzureSpeech, region, key);

        Assert.NotNull(TranslationSettings.Validate(settings));
        Assert.Throws<ArgumentException>(() => new TranslationSettingsStore(SettingsPath).Save(settings));
    }
}
