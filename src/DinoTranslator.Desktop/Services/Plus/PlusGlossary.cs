using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Shared.Social;

namespace DinoTranslator.Desktop.Services.Plus;

/// <summary>
/// Glossário da conta aberta: o pessoal (o mesmo em todos os aparelhos) e o de cada grupo, vindos do
/// servidor e atualizados em tempo real quando mudam. As mensagens usam o do grupo com o pessoal por
/// cima; as chamadas usam o pessoal.
/// </summary>
public sealed class PlusGlossary : IGlossarySource
{
    private readonly PlusApi _api;
    private readonly PlusState _state;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Glossary> _groups = [];
    private readonly Dictionary<Guid, Glossary> _merged = [];
    private readonly HashSet<Guid> _loading = [];
    private bool _personalLoaded;
    private bool _personalLoading;

    public PlusGlossary(PlusApi api, PlusState state)
    {
        _api = api;
        _state = state;
        _state.SessionStarted += () => _ = ReloadAsync();
        _state.SessionEnded += Forget;
        _state.GlossaryChanged += groupId => _ = groupId is { } group ? LoadGroupAsync(group) : LoadPersonalAsync();
    }

    public event Action? Changed;

    public Glossary Personal { get; private set; } = Glossary.Empty;

    /// <summary>
    /// O glossário de uma conversa: o do grupo com o pessoal por cima (conversa direta: só o pessoal).
    /// Null enquanto ainda carrega, para nada ser traduzido duas vezes.
    /// </summary>
    public Glossary? For(Guid? groupId)
    {
        lock (_lock)
        {
            if (!_personalLoaded)
            {
                if (!_personalLoading && _state.Plus)
                {
                    _ = LoadPersonalAsync();
                }
                return null;
            }
            if (groupId is not { } group)
            {
                return Personal;
            }
            if (_merged.TryGetValue(group, out var merged))
            {
                return merged;
            }
        }
        _ = LoadGroupAsync(groupId!.Value);
        return null;
    }

    /// <summary>Os termos do grupo (para mostrar e editar); null se ainda não carregou.</summary>
    public Glossary? Group(Guid groupId)
    {
        lock (_lock)
        {
            return _groups.GetValueOrDefault(groupId);
        }
    }

    public async Task<string?> SavePersonalAsync(IReadOnlyList<GlossaryEntry> entries)
    {
        var saved = await _api.SetGlossaryAsync(entries);
        if (saved.Value is { } list)
        {
            SetPersonal(list);
        }
        return saved.Error;
    }

    public async Task<string?> SaveGroupAsync(Guid groupId, IReadOnlyList<GlossaryEntry> entries)
    {
        var saved = await _api.SetGroupGlossaryAsync(groupId, entries);
        if (saved.Value is { } list)
        {
            SetGroup(groupId, list);
        }
        return saved.Error;
    }

    public async Task LoadGroupAsync(Guid groupId)
    {
        lock (_lock)
        {
            if (!_loading.Add(groupId))
            {
                return;
            }
        }
        try
        {
            var loaded = await _api.GroupGlossaryAsync(groupId);
            if (loaded.Value is { } list)
            {
                SetGroup(groupId, list);
            }
            else if (Group(groupId) is null)
            {
                // Sem resposta agora: as mensagens do grupo seguem sem o glossário dele.
                SetGroup(groupId, []);
            }
        }
        finally
        {
            lock (_lock)
            {
                _loading.Remove(groupId);
            }
        }
    }

    private async Task ReloadAsync()
    {
        await LoadPersonalAsync();
        Guid[] groups;
        lock (_lock)
        {
            groups = [.. _groups.Keys];
        }
        foreach (var group in groups)
        {
            await LoadGroupAsync(group);
        }
    }

    private async Task LoadPersonalAsync()
    {
        lock (_lock)
        {
            _personalLoading = true;
        }
        var loaded = await _api.GlossaryAsync();
        lock (_lock)
        {
            _personalLoading = false;
        }
        if (loaded.Value is { } list)
        {
            SetPersonal(list);
        }
        else
        {
            // Servidor sem resposta agora: as mensagens seguem sem o glossário em vez de esperar.
            lock (_lock)
            {
                _personalLoaded = true;
            }
            Changed?.Invoke();
        }
    }

    private void SetPersonal(IReadOnlyList<GlossaryEntry> entries)
    {
        lock (_lock)
        {
            Personal = new Glossary(entries);
            _personalLoaded = true;
            _merged.Clear();
            foreach (var (group, glossary) in _groups)
            {
                _merged[group] = Glossary.Merge(Personal, glossary);
            }
        }
        Changed?.Invoke();
    }

    private void SetGroup(Guid groupId, IReadOnlyList<GlossaryEntry> entries)
    {
        lock (_lock)
        {
            var glossary = new Glossary(entries);
            _groups[groupId] = glossary;
            _merged[groupId] = Glossary.Merge(Personal, glossary);
        }
        Changed?.Invoke();
    }

    private void Forget()
    {
        lock (_lock)
        {
            Personal = Glossary.Empty;
            _personalLoaded = false;
            _groups.Clear();
            _merged.Clear();
        }
        Changed?.Invoke();
    }
}
