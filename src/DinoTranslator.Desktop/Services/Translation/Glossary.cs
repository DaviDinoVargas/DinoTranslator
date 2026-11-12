using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DinoTranslator.Shared.Social;

namespace DinoTranslator.Desktop.Services.Translation;

/// <summary>De onde vem o glossário pessoal da conta aberta (para as chamadas).</summary>
public interface IGlossarySource
{
    /// <summary>O glossário pessoal; vazio sem conta paga aberta.</summary>
    Glossary Personal { get; }
}

/// <summary>
/// Glossário pronto para usar (nomes, marcas, jargão). Na tradução, cada termo vira um marcador
/// que os modelos copiam sem mexer (X1Q, X2Q... — medido no OPUS-MT e no NLLB: colchetes fazem o
/// modelo inventar frases e números ganham sufixos no russo); depois o marcador volta como o termo
/// (ou a tradução dada para o idioma). Se algum marcador não voltar intacto, traduz o texto
/// original, sem o glossário: nunca aparece um marcador para quem lê.
/// </summary>
public sealed class Glossary
{
    /// <summary>Marcadores por texto: acima disso o resto vai sem proteção.</summary>
    public const int MaxMarkers = 20;

    /// <summary>Dica para o Whisper: o prompt dele é curto.</summary>
    public const int MaxPhrasesLength = 600;

    private static readonly string[] MarkerShapes = ["X{0}Q", "Z{0}J", "K{0}W"];

    private readonly ConcurrentDictionary<string, Rules?> _rules = new();

    public Glossary(IReadOnlyList<GlossaryEntry> entries)
    {
        Entries = entries;
        Key = entries.Count == 0
            ? string.Empty
            : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entries)), 0, 8);
    }

    public static Glossary Empty { get; } = new([]);

    public IReadOnlyList<GlossaryEntry> Entries { get; }

    /// <summary>Muda quando os termos mudam (entra na chave das traduções guardadas).</summary>
    public string Key { get; }

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>
    /// O do grupo com o pessoal por cima: o mesmo termo (e idioma) no pessoal vale mais.
    /// </summary>
    public static Glossary Merge(Glossary personal, Glossary group)
    {
        if (group.IsEmpty)
        {
            return personal;
        }
        if (personal.IsEmpty)
        {
            return group;
        }
        var mine = personal.Entries.Select(e => (e.Term.ToUpperInvariant(), e.Language)).ToHashSet();
        return new Glossary([.. personal.Entries, .. group.Entries.Where(e => !mine.Contains((e.Term.ToUpperInvariant(), e.Language)))]);
    }

    /// <summary>
    /// Dica para o reconhecimento de fala no idioma: os termos e as traduções dadas para ele.
    /// </summary>
    public IReadOnlyList<string> Phrases(string language)
    {
        var phrases = new List<string>();
        var length = 0;
        foreach (var phrase in Entries.Select(e => e.Term)
                     .Concat(Entries.Where(e => e.Language == language && e.Translation is not null).Select(e => e.Translation!))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (length + phrase.Length + 2 > MaxPhrasesLength)
            {
                break;
            }
            phrases.Add(phrase);
            length += phrase.Length + 2;
        }
        return phrases;
    }

    /// <summary>Traduz com o glossário (ou sem ele, se os marcadores não sobreviverem).</summary>
    public async Task<string> TranslateAsync(string text, string target, Func<string, Task<string>> translate)
    {
        var (protectedText, slots) = Protect(text, target);
        if (slots.Count == 0)
        {
            return await translate(text);
        }
        var translated = await translate(protectedText);
        return Restore(translated, slots) ?? await translate(text);
    }

    /// <summary>
    /// Troca os termos que valem para o idioma de destino por marcadores. Termo com maiúscula
    /// (nome) só casa escrito igual — "Rosa" protege o nome e deixa a cor "rosa" em paz; termo todo
    /// em minúsculas casa de qualquer jeito. Sempre a palavra inteira.
    /// </summary>
    public (string Text, IReadOnlyList<KeyValuePair<string, string>> Slots) Protect(string text, string target)
    {
        if (IsEmpty || string.IsNullOrEmpty(text) || _rules.GetOrAdd(target, Build) is not { } rules)
        {
            return (text, []);
        }
        // Um formato de marcador que o texto não tenha (na prática, sempre o primeiro).
        if (MarkerShapes.FirstOrDefault(shape => !Collides(text, shape)) is not { } shape)
        {
            return (text, []);
        }
        var slots = new List<KeyValuePair<string, string>>();
        try
        {
            var result = rules.Pattern.Replace(text, match =>
            {
                if (slots.Count >= MaxMarkers || rules.Find(match.Value) is not { } entry)
                {
                    return match.Value;
                }
                var marker = string.Format(shape, slots.Count + 1);
                slots.Add(new KeyValuePair<string, string>(marker, entry.Translation ?? match.Value));
                return marker;
            });
            return (result, slots);
        }
        catch (RegexMatchTimeoutException)
        {
            return (text, []);
        }
    }

    private static bool Collides(string text, string shape)
    {
        for (var i = 1; i <= MaxMarkers; i++)
        {
            if (text.Contains(string.Format(shape, i), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Põe os termos no lugar dos marcadores; null se algum sumiu ou repetiu.</summary>
    public static string? Restore(string translated, IReadOnlyList<KeyValuePair<string, string>> slots)
    {
        foreach (var (marker, value) in slots)
        {
            var at = translated.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0 || translated.IndexOf(marker, at + marker.Length, StringComparison.Ordinal) >= 0)
            {
                return null;
            }
            translated = string.Concat(translated.AsSpan(0, at), value, translated.AsSpan(at + marker.Length));
        }
        return translated;
    }

    /// <summary>As regras para um idioma de destino: os termos "manter" e os traduzidos para ele.</summary>
    private Rules? Build(string target)
    {
        var applicable = Entries
            .Where(e => e.Translation is null || e.Language == target)
            // A tradução específica do idioma vale mais que o "manter".
            .OrderBy(e => e.Translation is null ? 1 : 0)
            .ToList();
        if (applicable.Count == 0)
        {
            return null;
        }
        var exact = new Dictionary<string, GlossaryEntry>(StringComparer.Ordinal);
        var loose = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in applicable)
        {
            if (entry.Term.Any(char.IsUpper))
            {
                exact.TryAdd(entry.Term, entry);
            }
            else
            {
                loose.TryAdd(entry.Term, entry);
            }
        }
        var alternatives = applicable
            .Select(e => e.Term)
            .Distinct()
            .OrderByDescending(term => term.Length)
            .Select(term => term.Any(char.IsUpper) ? $"(?-i:{Regex.Escape(term)})" : $"(?i:{Regex.Escape(term)})");
        var pattern = new Regex(
            $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', alternatives)})(?![\p{{L}}\p{{N}}])",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200));
        return new Rules(pattern, exact, loose);
    }

    private sealed record Rules(Regex Pattern, Dictionary<string, GlossaryEntry> Exact, Dictionary<string, GlossaryEntry> Loose)
    {
        public GlossaryEntry? Find(string matched) =>
            Exact.TryGetValue(matched, out var exact) ? exact : Loose.TryGetValue(matched, out var loose) ? loose : null;
    }
}
