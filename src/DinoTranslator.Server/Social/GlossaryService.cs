using DinoTranslator.Server.Data;
using DinoTranslator.Server.Realtime;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Social;
using Microsoft.EntityFrameworkCore;

namespace DinoTranslator.Server.Social;

/// <summary>
/// Glossário pessoal (de cada conta, igual em todos os aparelhos) e do grupo (todos os membros
/// leem; só administradores mudam). A lista é trocada inteira a cada gravação.
/// </summary>
public sealed class GlossaryService(AppDbContext db, SocialAccess access, Notifier notifier)
{
    public async Task<Outcome<List<GlossaryEntry>>> PersonalAsync(Guid userId, CancellationToken cancellationToken) =>
        Outcome<List<GlossaryEntry>>.Ok(await ListAsync(db.GlossaryTerms.Where(t => t.OwnerId == userId), cancellationToken));

    public async Task<Outcome<List<GlossaryEntry>>> SetPersonalAsync(Guid userId, GlossaryRequest? request, CancellationToken cancellationToken)
    {
        if (Validate(request?.Entries, GlossaryLimits.MaxPersonal) is not { } entries)
        {
            return Invalid(request?.Entries, GlossaryLimits.MaxPersonal);
        }
        await ReplaceAsync(db.GlossaryTerms.Where(t => t.OwnerId == userId), entries, term => term.OwnerId = userId, cancellationToken);
        await notifier.GlossaryChangedAsync(userId);
        return Outcome<List<GlossaryEntry>>.Ok(entries);
    }

    public async Task<Outcome<List<GlossaryEntry>>> GroupAsync(Guid userId, Guid groupId, CancellationToken cancellationToken)
    {
        if (await access.RoleAsync(groupId, userId, cancellationToken) is null)
        {
            return Problem.NotFound;
        }
        return Outcome<List<GlossaryEntry>>.Ok(await ListAsync(db.GlossaryTerms.Where(t => t.GroupId == groupId), cancellationToken));
    }

    public async Task<Outcome<List<GlossaryEntry>>> SetGroupAsync(Guid userId, Guid groupId, GlossaryRequest? request, CancellationToken cancellationToken)
    {
        switch (await access.RoleAsync(groupId, userId, cancellationToken))
        {
            case null:
                return Problem.NotFound;
            case GroupRole.Member:
                return Problem.Forbidden("Só administradores mudam o glossário do grupo.");
        }
        if (Validate(request?.Entries, GlossaryLimits.MaxGroup) is not { } entries)
        {
            return Invalid(request?.Entries, GlossaryLimits.MaxGroup);
        }
        await ReplaceAsync(db.GlossaryTerms.Where(t => t.GroupId == groupId), entries, term => term.GroupId = groupId, cancellationToken);
        await notifier.GroupGlossaryChangedAsync(groupId);
        return Outcome<List<GlossaryEntry>>.Ok(entries);
    }

    /// <summary>
    /// Termos limpos (sem controle nem espaços nas pontas, 1 a 60 caracteres), tradução opcional
    /// com o idioma da lista, sem repetir termo e idioma. Null se algo não vale.
    /// </summary>
    public static List<GlossaryEntry>? Validate(IReadOnlyList<GlossaryEntry>? entries, int max)
    {
        if (entries is null || entries.Count > max)
        {
            return null;
        }
        var clean = new List<GlossaryEntry>(entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry is null || Clean(entry.Term) is not { } term)
            {
                return null;
            }
            var translation = string.IsNullOrWhiteSpace(entry.Translation) ? null : Clean(entry.Translation);
            if ((translation is null) != string.IsNullOrWhiteSpace(entry.Translation))
            {
                return null;
            }
            // Sem tradução, o termo fica como está em todos os idiomas; com tradução, vale para um idioma.
            var language = translation is null ? null : entry.Language;
            if (translation is not null && !Languages.IsSupported(language))
            {
                return null;
            }
            if (!seen.Add($"{term}\u0001{language}"))
            {
                return null;
            }
            clean.Add(new GlossaryEntry(term, translation, language));
        }
        return clean;
    }

    private static Outcome<List<GlossaryEntry>> Invalid(IReadOnlyList<GlossaryEntry>? entries, int max) =>
        Problem.Invalid(entries is { Count: var count } && count > max
            ? $"O glossário aceita até {max} termos."
            : "Confira os termos: cada um com até 60 caracteres, sem repetir, e a tradução com o idioma.");

    private static string? Clean(string? text)
    {
        var clean = new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length is > 0 and <= GlossaryLimits.MaxTermLength ? clean : null;
    }

    private static Task<List<GlossaryEntry>> ListAsync(IQueryable<GlossaryTerm> terms, CancellationToken cancellationToken) =>
        terms.AsNoTracking().OrderBy(t => t.Position).Select(t => new GlossaryEntry(t.Term, t.Translation, t.Language)).ToListAsync(cancellationToken);

    private async Task ReplaceAsync(IQueryable<GlossaryTerm> current, List<GlossaryEntry> entries, Action<GlossaryTerm> owner, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await current.ExecuteDeleteAsync(cancellationToken);
        for (var i = 0; i < entries.Count; i++)
        {
            var term = new GlossaryTerm { Term = entries[i].Term, Translation = entries[i].Translation, Language = entries[i].Language, Position = i };
            owner(term);
            db.GlossaryTerms.Add(term);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
