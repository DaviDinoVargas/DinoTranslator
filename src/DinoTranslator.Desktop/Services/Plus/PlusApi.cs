using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DinoTranslator.Shared.Social;

namespace DinoTranslator.Desktop.Services.Plus;

/// <summary>Resposta da API: o valor, ou a mensagem para mostrar à pessoa.</summary>
public readonly record struct ApiResult<T>(T? Value, string? Error, HttpStatusCode Status)
{
    public bool Ok => Error is null;
}

/// <summary>Chamadas REST da versão paga, sempre com o token de acesso da sessão (renovado sozinho).</summary>
public sealed class PlusApi(PlusHttp plus, AuthClient auth)
{
    public Task<ApiResult<List<GroupSummary>>> MyGroupsAsync() => SendAsync<List<GroupSummary>>(HttpMethod.Get, SocialRoutes.MyGroups);

    public Task<ApiResult<GroupDetails>> CreateGroupAsync(string name) => SendAsync<GroupDetails>(HttpMethod.Post, SocialRoutes.Groups, new NameRequest(name));

    public Task<ApiResult<GroupDetails>> GroupAsync(Guid id) => SendAsync<GroupDetails>(HttpMethod.Get, SocialRoutes.Group(id));

    public Task<ApiResult<object>> RenameGroupAsync(Guid id, string name) => SendAsync<object>(HttpMethod.Patch, SocialRoutes.Group(id), new NameRequest(name));

    public Task<ApiResult<object>> DeleteGroupAsync(Guid id) => SendAsync<object>(HttpMethod.Delete, SocialRoutes.Group(id));

    public Task<ApiResult<ChannelInfo>> CreateChannelAsync(Guid groupId, string name, ChannelKind kind) =>
        SendAsync<ChannelInfo>(HttpMethod.Post, SocialRoutes.GroupChannels(groupId), new CreateChannelRequest(name, kind));

    public Task<ApiResult<object>> DeleteChannelAsync(Guid channelId) => SendAsync<object>(HttpMethod.Delete, SocialRoutes.Channel(channelId));

    public Task<ApiResult<InviteInfo>> CreateInviteAsync(Guid groupId, int? maxUses = null, int? hours = null) =>
        SendAsync<InviteInfo>(HttpMethod.Post, SocialRoutes.GroupInvites(groupId), new CreateInviteRequest(maxUses, hours));

    public Task<ApiResult<InviteInfo>> InviteAsync(string code) => SendAsync<InviteInfo>(HttpMethod.Get, SocialRoutes.Invite(code));

    public Task<ApiResult<GroupDetails>> JoinAsync(string code) => SendAsync<GroupDetails>(HttpMethod.Post, SocialRoutes.InviteJoin(code));

    public Task<ApiResult<object>> RemoveMemberAsync(Guid groupId, Guid userId) => SendAsync<object>(HttpMethod.Delete, SocialRoutes.GroupMember(groupId, userId));

    public Task<ApiResult<object>> SetRoleAsync(Guid groupId, Guid userId, GroupRole role) =>
        SendAsync<object>(HttpMethod.Put, SocialRoutes.GroupMemberRole(groupId, userId), new RoleRequest(role));

    public Task<ApiResult<MessagePage>> MessagesAsync(Guid channelId, long? before = null, int limit = SocialLimits.DefaultPageSize) =>
        SendAsync<MessagePage>(HttpMethod.Get, SocialRoutes.ChannelMessages(channelId) + $"?limite={limit}" + (before is { } b ? $"&antes={b}" : string.Empty));

    public Task<ApiResult<MessageInfo>> SendMessageAsync(Guid channelId, string content, long? replyTo = null) =>
        SendAsync<MessageInfo>(HttpMethod.Post, SocialRoutes.ChannelMessages(channelId), new SendMessageRequest(content, replyTo));

    public Task<ApiResult<MessageInfo>> EditMessageAsync(long messageId, string content) =>
        SendAsync<MessageInfo>(HttpMethod.Patch, SocialRoutes.Message(messageId), new EditMessageRequest(content));

    public Task<ApiResult<object>> DeleteMessageAsync(long messageId) => SendAsync<object>(HttpMethod.Delete, SocialRoutes.Message(messageId));

    public Task<ApiResult<List<ReactionInfo>>> ReactAsync(long messageId, string emoji, bool add) =>
        SendAsync<List<ReactionInfo>>(add ? HttpMethod.Put : HttpMethod.Delete, SocialRoutes.MessageReaction(messageId, emoji));

    public Task<ApiResult<MessageInfo>> PinAsync(long messageId, bool pin) =>
        SendAsync<MessageInfo>(pin ? HttpMethod.Put : HttpMethod.Delete, SocialRoutes.MessagePin(messageId));

    public Task<ApiResult<List<MessageInfo>>> PinsAsync(Guid channelId) => SendAsync<List<MessageInfo>>(HttpMethod.Get, SocialRoutes.ChannelPins(channelId));

    public Task<ApiResult<List<SearchResult>>> SearchAsync(string query, Guid? channelId = null, Guid? groupId = null) =>
        SendAsync<List<SearchResult>>(HttpMethod.Get, SocialRoutes.Search + "?q=" + Uri.EscapeDataString(query)
            + (channelId is { } c ? $"&canal={c}" : string.Empty) + (groupId is { } g ? $"&grupo={g}" : string.Empty));

    public Task<ApiResult<List<GlossaryEntry>>> GlossaryAsync() => SendAsync<List<GlossaryEntry>>(HttpMethod.Get, SocialRoutes.Glossary);

    public Task<ApiResult<List<GlossaryEntry>>> SetGlossaryAsync(IReadOnlyList<GlossaryEntry> entries) =>
        SendAsync<List<GlossaryEntry>>(HttpMethod.Put, SocialRoutes.Glossary, new GlossaryRequest(entries));

    public Task<ApiResult<List<GlossaryEntry>>> GroupGlossaryAsync(Guid groupId) =>
        SendAsync<List<GlossaryEntry>>(HttpMethod.Get, SocialRoutes.GroupGlossary(groupId));

    public Task<ApiResult<List<GlossaryEntry>>> SetGroupGlossaryAsync(Guid groupId, IReadOnlyList<GlossaryEntry> entries) =>
        SendAsync<List<GlossaryEntry>>(HttpMethod.Put, SocialRoutes.GroupGlossary(groupId), new GlossaryRequest(entries));

    public Task<ApiResult<List<FriendInfo>>> FriendsAsync() => SendAsync<List<FriendInfo>>(HttpMethod.Get, SocialRoutes.Friends);

    public Task<ApiResult<FriendInfo>> AddFriendAsync(string userName) => SendAsync<FriendInfo>(HttpMethod.Post, SocialRoutes.Friends, new FriendRequest(userName));

    public Task<ApiResult<object>> RemoveFriendAsync(Guid userId) => SendAsync<object>(HttpMethod.Delete, SocialRoutes.Friend(userId));

    public Task<ApiResult<object>> BlockAsync(Guid userId) => SendAsync<object>(HttpMethod.Post, SocialRoutes.Block(userId));

    public Task<ApiResult<List<DirectInfo>>> DirectsAsync() => SendAsync<List<DirectInfo>>(HttpMethod.Get, SocialRoutes.Directs);

    public Task<ApiResult<DirectInfo>> OpenDirectAsync(Guid userId) => SendAsync<DirectInfo>(HttpMethod.Post, SocialRoutes.Direct(userId));

    public Task<ApiResult<VoiceJoin>> JoinVoiceAsync(Guid channelId) => SendAsync<VoiceJoin>(HttpMethod.Post, SocialRoutes.ChannelVoice(channelId));

    public Task<ApiResult<List<Guid>>> VoiceMembersAsync(Guid channelId) => SendAsync<List<Guid>>(HttpMethod.Get, SocialRoutes.ChannelVoice(channelId));

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string route, object? body = null)
    {
        try
        {
            if (await auth.GetAccessTokenAsync() is not { } token)
            {
                return new(default, "Sua sessão terminou. Entre de novo.", HttpStatusCode.Unauthorized);
            }
            using var request = new HttpRequestMessage(method, route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType());
            }
            using var response = await plus.Client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var value = response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)
                    ? default
                    : await response.Content.ReadFromJsonAsync<T>();
                return new(value, null, response.StatusCode);
            }
            var message = await AuthClient.ProblemTitleAsync(response, CancellationToken.None);
            return new(default, response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "Devagar: espere alguns segundos e tente de novo.",
                HttpStatusCode.Forbidden when message is null => "Esta parte é da versão Plus.",
                HttpStatusCode.Unauthorized => "Sua sessão terminou. Entre de novo.",
                _ => message ?? "O servidor recusou o pedido.",
            }, response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new(default, "Sem conexão com o servidor.", HttpStatusCode.ServiceUnavailable);
        }
    }
}
