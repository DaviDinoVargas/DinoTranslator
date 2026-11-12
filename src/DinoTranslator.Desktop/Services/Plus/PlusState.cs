using System.Net.Http;
using DinoTranslator.Shared.Auth;
using Microsoft.AspNetCore.SignalR;
using DinoTranslator.Shared.Social;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace DinoTranslator.Desktop.Services.Plus;

/// <summary>Mensagens já carregadas de um canal, da mais antiga para a mais nova.</summary>
public sealed class ChannelMessages
{
    public List<MessageInfo> Items { get; } = [];

    public bool HasMore { get; set; } = true;

    public bool Loaded { get; set; }

    public bool Loading { get; set; }
}

/// <summary>
/// Tudo o que a versão paga mostra: grupos, canais, amigos, conversas, mensagens, presença,
/// "digitando" e quem está em cada canal de voz. A conexão em tempo real (/hubs/app) mantém
/// tudo atualizado; as telas só leem daqui e redesenham quando <see cref="Changed"/> avisa.
/// </summary>
public sealed class PlusState : IAsyncDisposable, ICallSignaling
{
    private static readonly TimeSpan TypingShows = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TypingEvery = TimeSpan.FromSeconds(3);

    private readonly PlusApi _api;
    private readonly AuthClient _auth;
    private readonly PlusServerStore _server;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, GroupDetails> _details = [];
    private readonly Dictionary<Guid, ChannelMessages> _messages = [];
    private readonly Dictionary<Guid, long> _read = [];
    private readonly Dictionary<Guid, bool> _online = [];
    private readonly Dictionary<Guid, Dictionary<Guid, DateTimeOffset>> _typing = [];
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> _voice = [];
    private HubConnection? _hub;
    private DateTimeOffset _lastTyping;

    public PlusState(PlusApi api, AuthClient auth, PlusServerStore server, MessageTranslator translator, TimeProvider time)
    {
        _api = api;
        _auth = auth;
        _server = server;
        _time = time;
        Translator = translator;
        translator.Translated += Notify;
    }

    public event Action? Changed;

    /// <summary>Sinal da chamada pela conta (ver <see cref="DirectCalls"/>).</summary>
    public event Action<CallSignalInfo>? CallSignalReceived;

    public MessageTranslator Translator { get; }

    public UserProfile? Me => _auth.CurrentUser;

    public bool Plus => Me is { Plan: AccountPlan.Plus or AccountPlan.Pro } me && (me.PlanExpiresAt is not { } until || until > _time.GetUtcNow());

    public bool Pro => Plus && Me is { Plan: AccountPlan.Pro };

    public bool Connected => _hub?.State == HubConnectionState.Connected;

    public string? Problem { get; private set; }

    public IReadOnlyList<GroupSummary> Groups { get; private set; } = [];

    public IReadOnlyList<FriendInfo> Friends { get; private set; } = [];

    public IReadOnlyList<DirectInfo> Directs { get; private set; } = [];

    /// <summary>null: a tela inicial (amigos e conversas).</summary>
    public Guid? SelectedGroup { get; private set; }

    public Guid? SelectedChannel { get; private set; }

    public GroupDetails? Details(Guid groupId)
    {
        lock (_lock)
        {
            return _details.GetValueOrDefault(groupId);
        }
    }

    public ChannelMessages Messages(Guid channelId)
    {
        lock (_lock)
        {
            return _messages.TryGetValue(channelId, out var messages) ? messages : _messages[channelId] = new ChannelMessages();
        }
    }

    public bool IsOnline(Guid userId)
    {
        lock (_lock)
        {
            return _online.GetValueOrDefault(userId);
        }
    }

    public IReadOnlyList<Guid> VoiceMembers(Guid channelId)
    {
        lock (_lock)
        {
            return _voice.GetValueOrDefault(channelId) ?? [];
        }
    }

    /// <summary>Quem está digitando no canal (fora a própria pessoa).</summary>
    public IReadOnlyList<Guid> Typing(Guid channelId)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            return _typing.TryGetValue(channelId, out var users)
                ? [.. users.Where(u => now - u.Value < TypingShows).Select(u => u.Key)]
                : [];
        }
    }

    /// <summary>Canal com mensagem nova desde a última vez que a pessoa o abriu.</summary>
    public bool Unread(ChannelInfo channel)
    {
        lock (_lock)
        {
            return channel.Id != SelectedChannel && channel.LastMessageId > _read.GetValueOrDefault(channel.Id, long.MaxValue);
        }
    }

    public DirectInfo? DirectFor(Guid channelId) => Directs.FirstOrDefault(d => d.Channel.Id == channelId);

    public UserSummary? FindUser(Guid userId) =>
        Directs.Select(d => d.Other).Concat(Friends.Select(f => f.User))
            .Concat(_details.Values.SelectMany(g => g.Members.Select(m => m.User)))
            .FirstOrDefault(u => u.Id == userId);

    /// <summary>Carrega o que a pessoa tem e liga o tempo real. Conta grátis só vê o aviso do Plus.</summary>
    /// <summary>
    /// Ao abrir o app: com a sessão salva ("manter conectado") de uma conta paga, liga o tempo real
    /// para as chamadas pela conta chegarem em qualquer tela, sem precisar abrir o Plus antes.
    /// </summary>
    public async Task ConnectIfSignedInAsync()
    {
        if (_server.Current is null || _hub is not null)
        {
            return;
        }
        try
        {
            if (_auth.CurrentUser is null && !await _auth.TryResumeAsync())
            {
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return;
        }
        await StartAsync();
    }

    public async Task<SignalResult> SendCallSignalAsync(Guid channelId, string callId, CallSignal signal, string? code)
    {
        if (_hub?.State != HubConnectionState.Connected)
        {
            return new SignalResult(false, false, "Sem conexão em tempo real com o servidor agora.");
        }
        try
        {
            return new SignalResult(true, await _hub.InvokeAsync<bool>(AppHubMethods.DirectCall, channelId, callId, signal, code));
        }
        catch (HubException ex)
        {
            return new SignalResult(false, false, HubProblem(ex));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or TaskCanceledException)
        {
            return new SignalResult(false, false, "Sem conexão em tempo real com o servidor agora.");
        }
    }

    public string? PeerName(Guid channelId) => DirectFor(channelId)?.Other.DisplayName;

    /// <summary>O motivo que o servidor mandou (a mensagem chega como "... HubException: motivo").</summary>
    private static string HubProblem(HubException ex)
    {
        const string Marker = "HubException: ";
        var at = ex.Message.LastIndexOf(Marker, StringComparison.Ordinal);
        return at >= 0 ? ex.Message[(at + Marker.Length)..] : "O servidor recusou o pedido.";
    }

    public async Task StartAsync()
    {
        if (!Plus || _server.Current is not { } server)
        {
            Notify();
            return;
        }
        if (_hub is null)
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(new Uri(server, SocialRoutes.Hub), options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.AccessTokenProvider = () => _auth.GetAccessTokenAsync();
                })
                .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
                .Build();
            Subscribe(_hub);
            _hub.Reconnected += async _ =>
            {
                await ReadyAsync();
                await RefreshListsAsync();
            };
            _hub.Reconnecting += _ =>
            {
                Notify();
                return Task.CompletedTask;
            };
            _hub.Closed += _ =>
            {
                Notify();
                return Task.CompletedTask;
            };
        }
        if (_hub.State == HubConnectionState.Disconnected)
        {
            try
            {
                await _hub.StartAsync();
                await ReadyAsync();
                Problem = null;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Net.WebSockets.WebSocketException or HubException)
            {
                Problem = "Sem conexão em tempo real com o servidor. Tentando de novo…";
            }
        }
        // Depois do tempo real ligado: o que chegar enquanto as listas carregam não se perde.
        await RefreshListsAsync();
        Notify();
    }

    private async Task ReadyAsync()
    {
        if (_hub?.State == HubConnectionState.Connected)
        {
            await _hub.InvokeAsync<bool>(AppHubMethods.Ready);
        }
    }

    private async Task RefreshListsAsync()
    {
        var groups = await _api.MyGroupsAsync();
        var friends = await _api.FriendsAsync();
        var directs = await _api.DirectsAsync();
        lock (_lock)
        {
            Groups = groups.Value ?? Groups;
            Friends = friends.Value ?? Friends;
            Directs = directs.Value ?? Directs;
            foreach (var friend in Friends)
            {
                _online[friend.User.Id] = friend.Online;
            }
            foreach (var direct in Directs)
            {
                _online[direct.Other.Id] = direct.Online;
                _read.TryAdd(direct.Channel.Id, direct.Channel.LastMessageId);
            }
        }
        Problem = groups.Error ?? friends.Error ?? directs.Error;
        if (SelectedGroup is { } group)
        {
            await LoadGroupAsync(group);
        }
        Notify();
    }

    public async Task<GroupDetails?> LoadGroupAsync(Guid groupId)
    {
        var details = await _api.GroupAsync(groupId);
        if (details.Value is not { } value)
        {
            Problem = details.Error;
            Notify();
            return null;
        }
        lock (_lock)
        {
            _details[groupId] = value;
            foreach (var member in value.Members)
            {
                _online[member.User.Id] = member.Online;
            }
            foreach (var channel in value.Channels)
            {
                _read.TryAdd(channel.Id, channel.LastMessageId);
            }
        }
        foreach (var voice in value.Channels.Where(c => c.Kind == ChannelKind.Voice))
        {
            if ((await _api.VoiceMembersAsync(voice.Id)).Value is { } members)
            {
                lock (_lock)
                {
                    _voice[voice.Id] = members;
                }
            }
        }
        Notify();
        return value;
    }

    public void SelectHome()
    {
        SelectedGroup = null;
        SelectedChannel = null;
        Notify();
    }

    public async Task SelectGroupAsync(Guid groupId)
    {
        SelectedGroup = groupId;
        SelectedChannel = null;
        Notify();
        if (await LoadGroupAsync(groupId) is { } details)
        {
            await SelectChannelAsync(details.Channels.FirstOrDefault(c => c.Kind == ChannelKind.Text)?.Id);
        }
    }

    public async Task SelectChannelAsync(Guid? channelId)
    {
        SelectedChannel = channelId;
        if (channelId is { } id)
        {
            MarkRead(id);
            var messages = Messages(id);
            if (!messages.Loaded)
            {
                await LoadOlderAsync(id);
            }
        }
        Notify();
    }

    public async Task OpenDirectAsync(Guid userId)
    {
        var direct = await _api.OpenDirectAsync(userId);
        if (direct.Value is not { } value)
        {
            Problem = direct.Error;
            Notify();
            return;
        }
        AddDirect(value);
        SelectedGroup = null;
        await SelectChannelAsync(value.Channel.Id);
    }

    public async Task LoadOlderAsync(Guid channelId)
    {
        var messages = Messages(channelId);
        long? before;
        lock (_lock)
        {
            if (messages.Loading || (messages.Loaded && !messages.HasMore))
            {
                return;
            }
            messages.Loading = true;
            before = messages.Items.Count > 0 ? messages.Items[0].Id : null;
        }
        var page = await _api.MessagesAsync(channelId, before);
        lock (_lock)
        {
            messages.Loading = false;
            if (page.Value is { } value)
            {
                messages.Items.InsertRange(0, value.Messages.Where(m => messages.Items.All(x => x.Id != m.Id)).OrderBy(m => m.Id));
                messages.HasMore = value.HasMore;
                messages.Loaded = true;
            }
        }
        Problem = page.Error;
        Notify();
    }

    public async Task<string?> SendAsync(Guid channelId, string text, long? replyTo = null)
    {
        var sent = await _api.SendMessageAsync(channelId, text, replyTo);
        if (sent.Value is { } message)
        {
            Upsert(message);
            MarkRead(channelId);
        }
        return sent.Error;
    }

    public async Task<string?> EditAsync(long messageId, string text)
    {
        var edited = await _api.EditMessageAsync(messageId, text);
        if (edited.Value is { } message)
        {
            Upsert(message);
        }
        return edited.Error;
    }

    /// <summary>Põe ou tira a própria reação.</summary>
    public async Task<string?> ToggleReactionAsync(MessageInfo message, string emoji)
    {
        var mine = Me is { } me && message.Reactions?.FirstOrDefault(r => r.Emoji == emoji)?.Users.Contains(me.Id) == true;
        var result = await _api.ReactAsync(message.Id, emoji, add: !mine);
        if (result.Value is { } reactions)
        {
            SetReactions(message.ChannelId, message.Id, reactions);
        }
        return result.Error;
    }

    public async Task<string?> PinAsync(MessageInfo message, bool pin)
    {
        var result = await _api.PinAsync(message.Id, pin);
        if (result.Value is { } updated)
        {
            Upsert(updated);
        }
        return result.Error;
    }

    public Task<ApiResult<List<MessageInfo>>> PinsAsync(Guid channelId) => _api.PinsAsync(channelId);

    public Task<ApiResult<List<SearchResult>>> SearchAsync(string query, Guid? channelId, Guid? groupId) => _api.SearchAsync(query, channelId, groupId);

    /// <summary>Pode fixar: numa conversa direta, qualquer das duas pessoas; num grupo, administradores.</summary>
    public bool CanPin(Guid channelId) =>
        DirectFor(channelId) is not null
        || (SelectedGroup is { } group && Details(group) is { } details && details.Group.MyRole >= GroupRole.Admin);

    /// <summary>A mensagem menciona a própria pessoa.</summary>
    public bool MentionsMe(MessageInfo message) => Me is { } me && message.Mentions?.Contains(me.Id) == true;

    public async Task<string?> DeleteAsync(MessageInfo message)
    {
        var deleted = await _api.DeleteMessageAsync(message.Id);
        if (deleted.Ok)
        {
            MarkDeleted(message.ChannelId, message.Id);
        }
        return deleted.Error;
    }

    /// <summary>Avisa "digitando" no máximo a cada 3 s.</summary>
    public async Task TypingAsync(Guid channelId)
    {
        var now = _time.GetUtcNow();
        if (_hub?.State != HubConnectionState.Connected || now - _lastTyping < TypingEvery)
        {
            return;
        }
        _lastTyping = now;
        try
        {
            await _hub.InvokeAsync(AppHubMethods.Typing, channelId);
        }
        catch (Exception ex) when (ex is HubException or InvalidOperationException)
        {
            // "Digitando" é só um aviso.
        }
    }

    public async Task<string?> CreateGroupAsync(string name)
    {
        var created = await _api.CreateGroupAsync(name);
        if (created.Value is not { } details)
        {
            return created.Error;
        }
        lock (_lock)
        {
            _details[details.Group.Id] = details;
        }
        await RefreshListsAsync();
        await SelectGroupAsync(details.Group.Id);
        return null;
    }

    public async Task<string?> JoinAsync(string code)
    {
        var joined = await _api.JoinAsync(InviteCode(code));
        if (joined.Value is not { } details)
        {
            return joined.Error is "Não encontrado." ? "Convite inválido, vencido ou já usado." : joined.Error;
        }
        await RefreshListsAsync();
        await SelectGroupAsync(details.Group.Id);
        return null;
    }

    /// <summary>Aceita o código puro ou colado de uma mensagem ("…convite: abcd2345ef").</summary>
    public static string InviteCode(string text)
    {
        var words = (text ?? string.Empty).Split([' ', '\n', '\r', '\t', '/', ':'], StringSplitOptions.RemoveEmptyEntries);
        return words.LastOrDefault(w => w.Length == 10 && w.All(char.IsAsciiLetterOrDigit))?.ToLowerInvariant() ?? (text ?? string.Empty).Trim();
    }

    public async Task<string?> AddFriendAsync(string userName)
    {
        var added = await _api.AddFriendAsync(userName);
        if (added.Ok)
        {
            await RefreshListsAsync();
        }
        return added.Error is "Não encontrado." ? "Ninguém com esse nome de usuário." : added.Error;
    }

    public async Task<string?> RemoveFriendAsync(Guid userId)
    {
        var removed = await _api.RemoveFriendAsync(userId);
        await RefreshListsAsync();
        return removed.Error;
    }

    public async Task<string?> LeaveGroupAsync(Guid groupId)
    {
        if (Me is not { } me)
        {
            return null;
        }
        var left = await _api.RemoveMemberAsync(groupId, me.Id);
        if (left.Ok)
        {
            ForgetGroup(groupId);
        }
        return left.Error;
    }

    private void Subscribe(HubConnection hub)
    {
        hub.On<MessageInfo>(nameof(IAppClient.MessageCreated), message =>
        {
            Upsert(message);
            if (message.ChannelId == SelectedChannel)
            {
                MarkRead(message.ChannelId);
            }
        });
        hub.On<MessageInfo>(nameof(IAppClient.MessageEdited), Upsert);
        hub.On<Guid, long>(nameof(IAppClient.MessageDeleted), MarkDeleted);
        hub.On<Guid, long, IReadOnlyList<ReactionInfo>>(nameof(IAppClient.ReactionsChanged), SetReactions);
        hub.On<Guid, Guid>(nameof(IAppClient.Typing), (channelId, userId) =>
        {
            lock (_lock)
            {
                if (!_typing.TryGetValue(channelId, out var users))
                {
                    _typing[channelId] = users = [];
                }
                users[userId] = _time.GetUtcNow();
            }
            Notify();
        });
        hub.On<Guid, bool>(nameof(IAppClient.PresenceChanged), (userId, online) =>
        {
            lock (_lock)
            {
                _online[userId] = online;
            }
            Notify();
        });
        hub.On<Guid>(nameof(IAppClient.GroupChanged), async groupId =>
        {
            await LoadGroupAsync(groupId);
            if ((await _api.MyGroupsAsync()).Value is { } groups)
            {
                Groups = groups;
            }
            Notify();
        });
        hub.On<Guid>(nameof(IAppClient.GroupRemoved), ForgetGroup);
        hub.On(nameof(IAppClient.FriendsChanged), async () =>
        {
            if ((await _api.FriendsAsync()).Value is { } friends)
            {
                Friends = friends;
            }
            Notify();
        });
        hub.On<DirectInfo>(nameof(IAppClient.DirectOpened), AddDirect);
        hub.On<Guid, IReadOnlyList<Guid>>(nameof(IAppClient.VoiceChanged), (channelId, users) =>
        {
            lock (_lock)
            {
                _voice[channelId] = users;
            }
            Notify();
        });
        hub.On<CallSignalInfo>(nameof(IAppClient.DirectCall), signal => CallSignalReceived?.Invoke(signal));
    }

    private void Upsert(MessageInfo message)
    {
        lock (_lock)
        {
            var messages = Messages(message.ChannelId);
            var index = messages.Items.FindIndex(m => m.Id == message.Id);
            if (index >= 0)
            {
                messages.Items[index] = message;
            }
            else if (messages.Loaded)
            {
                messages.Items.Add(message);
                messages.Items.Sort((a, b) => a.Id.CompareTo(b.Id));
            }
            BumpChannel(message.ChannelId, message.Id);
        }
        Notify();
    }

    private void SetReactions(Guid channelId, long messageId, IReadOnlyList<ReactionInfo> reactions)
    {
        lock (_lock)
        {
            var messages = Messages(channelId);
            var index = messages.Items.FindIndex(m => m.Id == messageId);
            if (index >= 0)
            {
                messages.Items[index] = messages.Items[index] with { Reactions = reactions };
            }
        }
        Notify();
    }

    private void MarkDeleted(Guid channelId, long messageId)
    {
        lock (_lock)
        {
            var messages = Messages(channelId);
            var index = messages.Items.FindIndex(m => m.Id == messageId);
            if (index >= 0)
            {
                messages.Items[index] = messages.Items[index] with { Content = string.Empty, Deleted = true, Reactions = [], Pinned = false };
            }
        }
        Notify();
    }

    private void BumpChannel(Guid channelId, long messageId)
    {
        foreach (var (groupId, details) in _details.ToList())
        {
            var channels = details.Channels.Select(c => c.Id == channelId && c.LastMessageId < messageId ? c with { LastMessageId = messageId } : c).ToList();
            _details[groupId] = details with { Channels = channels };
        }
        Directs = [.. Directs
            .Select(d => d.Channel.Id == channelId && d.Channel.LastMessageId < messageId ? d with { Channel = d.Channel with { LastMessageId = messageId } } : d)
            .OrderByDescending(d => d.Channel.LastMessageId)];
    }

    private void MarkRead(Guid channelId)
    {
        lock (_lock)
        {
            var last = _details.Values.SelectMany(g => g.Channels).Concat(Directs.Select(d => d.Channel))
                .Where(c => c.Id == channelId).Select(c => c.LastMessageId).DefaultIfEmpty(0).Max();
            _read[channelId] = Math.Max(last, Messages(channelId).Items.LastOrDefault()?.Id ?? 0);
        }
    }

    private void AddDirect(DirectInfo direct)
    {
        lock (_lock)
        {
            if (Directs.All(d => d.Channel.Id != direct.Channel.Id))
            {
                Directs = [direct, .. Directs];
            }
            _online[direct.Other.Id] = direct.Online;
            _read.TryAdd(direct.Channel.Id, direct.Channel.LastMessageId);
        }
        Notify();
    }

    private void ForgetGroup(Guid groupId)
    {
        lock (_lock)
        {
            _details.Remove(groupId);
            Groups = [.. Groups.Where(g => g.Id != groupId)];
        }
        if (SelectedGroup == groupId)
        {
            SelectHome();
        }
        Notify();
    }

    /// <summary>Saiu da conta: esquece tudo e desliga o tempo real.</summary>
    public async Task ResetAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }
        lock (_lock)
        {
            _details.Clear();
            _messages.Clear();
            _read.Clear();
            _online.Clear();
            _typing.Clear();
            _voice.Clear();
            Groups = [];
            Friends = [];
            Directs = [];
        }
        SelectedGroup = null;
        SelectedChannel = null;
        Problem = null;
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        Translator.Translated -= Notify;
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }
    }
}
