// Teste de carga da versão Plus: muitas contas conectadas ao mesmo tempo (tempo real ligado),
// divididas em grupos, todas conversando. Mede quanto a API leva para aceitar cada mensagem e
// quanto cada mensagem leva para chegar aos outros membros do grupo — por qualquer instância.
//
// Uso:
//   dotnet run --project tools/DinoTranslator.Carga -- --servidores http://127.0.0.1:5180,http://127.0.0.1:5181
//       --prefixo carga- --usuarios 300 --por-grupo 10 --mensagens 20 --intervalo-ms 2000 --senha <senha>
// As contas <prefixo>0001… precisam existir e ter o Plus (veja tests/carga/carga.ps1).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DinoTranslator.Shared.Auth;
using DinoTranslator.Shared.Social;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

var options = Options.Parse(args);
var servers = options.Servers;
var stopwatch = Stopwatch.StartNew();
var sendLatencies = new ConcurrentBag<double>();
var deliveryLatencies = new ConcurrentBag<double>();
// Horário de envio pelo texto (único), anotado antes do POST: a entrega pode chegar antes da resposta.
var sentAt = new ConcurrentDictionary<string, long>();
var errors = new ConcurrentDictionary<string, int>();
var limited = 0;
long delivered = 0;

void Error(string kind) => errors.AddOrUpdate(kind, 1, (_, n) => n + 1);

if (options.Create)
{
    Console.WriteLine($"Criando {options.Users} contas...");
    using var http = new HttpClient { BaseAddress = servers[0], Timeout = TimeSpan.FromSeconds(30) };
    using var gate = new SemaphoreSlim(16);
    await Task.WhenAll(Enumerable.Range(0, options.Users).Select(async i =>
    {
        await gate.WaitAsync();
        try
        {
            var name = $"{options.Prefix}{i + 1:D4}";
            var language = new[] { "pt-BR", "ru-RU", "en-US", "es-ES", "uk-UA" }[i % 5];
            using var response = await http.PostAsJsonAsync(AuthRoutes.Register, new RegisterRequest(name, options.Password, name, language, DinoTranslator.Shared.Gender.Unspecified));
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 400)
            {
                Error("cadastro: " + (int)response.StatusCode);
            }
        }
        finally
        {
            gate.Release();
        }
    }));
    Console.WriteLine("  contas criadas; ative o Plus (users plan <prefixo>* plus) e rode de novo sem --criar.");
    return 0;
}

// 1. Entrar com todas as contas (as instâncias se revezam).
Console.WriteLine($"Entrando com {options.Users} contas em {servers.Count} instância(s)...");
var people = new Person[options.Users];
using (var gate = new SemaphoreSlim(32))
{
    await Task.WhenAll(Enumerable.Range(0, options.Users).Select(async i =>
    {
        await gate.WaitAsync();
        try
        {
            var server = servers[i % servers.Count];
            var http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
            var name = $"{options.Prefix}{i + 1:D4}";
            var login = await http.PostAsJsonAsync(AuthRoutes.Login, new LoginRequest(name, options.Password));
            login.EnsureSuccessStatusCode();
            var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            people[i] = new Person(i, name, server, http, tokens.AccessToken);
        }
        finally
        {
            gate.Release();
        }
    }));
}
Console.WriteLine($"  pronto em {stopwatch.Elapsed.TotalSeconds:0.0} s");

// 2. Tempo real: uma conexão por conta, cada uma contando as mensagens que chegam.
Console.WriteLine("Abrindo as conexões em tempo real...");
var connectWatch = Stopwatch.StartNew();
using (var gate = new SemaphoreSlim(32))
{
    await Task.WhenAll(people.Select(async person =>
    {
        await gate.WaitAsync();
        try
        {
            var hub = new HubConnectionBuilder()
                .WithUrl(new Uri(person.Server, SocialRoutes.Hub), o =>
                {
                    o.Transports = HttpTransportType.WebSockets;
                    o.AccessTokenProvider = () => Task.FromResult<string?>(person.Token);
                })
                .Build();
            hub.On<MessageInfo>(nameof(IAppClient.MessageCreated), message =>
            {
                if (message.Author.UserName != person.Name && sentAt.TryGetValue(message.Content, out var sent))
                {
                    deliveryLatencies.Add((Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency);
                    Interlocked.Increment(ref delivered);
                }
            });
            await hub.StartAsync();
            person.Hub = hub;
        }
        catch (Exception ex)
        {
            Error("conexão: " + ex.GetType().Name);
        }
        finally
        {
            gate.Release();
        }
    }));
}
Console.WriteLine($"  {people.Count(p => p.Hub is not null)} conexões em {connectWatch.Elapsed.TotalSeconds:0.0} s");

// 3. Grupos: o primeiro de cada bloco cria e convida; os outros entram.
Console.WriteLine($"Montando {options.Users / options.PerGroup} grupos de {options.PerGroup}...");
var groups = people.Chunk(options.PerGroup).ToList();
var channels = new Guid[groups.Count];
await Task.WhenAll(groups.Select(async (members, index) =>
{
    var owner = members[0];
    var created = await owner.Http.PostAsJsonAsync(SocialRoutes.Groups, new NameRequest($"Carga {index + 1}"));
    created.EnsureSuccessStatusCode();
    var group = (await created.Content.ReadFromJsonAsync<GroupDetails>())!;
    channels[index] = group.Channels.First(c => c.Kind == ChannelKind.Text).Id;
    var invite = await owner.Http.PostAsJsonAsync(SocialRoutes.GroupInvites(group.Group.Id), new CreateInviteRequest());
    var code = (await invite.Content.ReadFromJsonAsync<InviteInfo>())!.Code;
    foreach (var member in members.Skip(1))
    {
        var joined = await member.Http.PostAsync(SocialRoutes.InviteJoin(code), null);
        if (!joined.IsSuccessStatusCode)
        {
            Error("entrar no grupo: " + (int)joined.StatusCode);
        }
    }
}));
await Task.Delay(1000);

// 4. Conversa: cada conta manda N mensagens, com um intervalo e um início espalhado.
var total = options.Users * options.Messages;
Console.WriteLine($"Mandando {total} mensagens (uma a cada {options.IntervalMs} ms por conta)...");
var sendWatch = Stopwatch.StartNew();
await Task.WhenAll(groups.SelectMany((members, index) => members.Select(async person =>
{
    await Task.Delay(Random.Shared.Next(options.IntervalMs));
    for (var n = 0; n < options.Messages; n++)
    {
        var content = $"Mensagem {n} de {person.Name}";
        var started = Stopwatch.GetTimestamp();
        sentAt[content] = started;
        try
        {
            var response = await person.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(channels[index]), new SendMessageRequest(content));
            var elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (response.IsSuccessStatusCode)
            {
                sendLatencies.Add(elapsed);
            }
            else if ((int)response.StatusCode == 429)
            {
                Interlocked.Increment(ref limited);
            }
            else
            {
                Error("mensagem: " + (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Error("mensagem: " + ex.GetType().Name);
        }
        await Task.Delay(options.IntervalMs);
    }
})));
await Task.Delay(3000);
var seconds = sendWatch.Elapsed.TotalSeconds;

static double Percentile(IEnumerable<double> values, double p)
{
    var sorted = values.OrderBy(v => v).ToArray();
    return sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];
}

var expected = (long)sendLatencies.Count * (options.PerGroup - 1);
var report = new
{
    instancias = servers.Count,
    contas = options.Users,
    conexoes = people.Count(p => p.Hub is not null),
    grupos = groups.Count,
    mensagens_aceitas = sendLatencies.Count,
    mensagens_por_segundo = Math.Round(sendLatencies.Count / seconds, 1),
    limitadas_429 = limited,
    entregas = delivered,
    entregas_esperadas = expected,
    entregas_por_segundo = Math.Round(delivered / seconds, 1),
    envio_ms = new { p50 = Math.Round(Percentile(sendLatencies, .5), 1), p95 = Math.Round(Percentile(sendLatencies, .95), 1), p99 = Math.Round(Percentile(sendLatencies, .99), 1) },
    entrega_ms = new { p50 = Math.Round(Percentile(deliveryLatencies, .5), 1), p95 = Math.Round(Percentile(deliveryLatencies, .95), 1), p99 = Math.Round(Percentile(deliveryLatencies, .99), 1) },
    erros = errors,
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

foreach (var person in people)
{
    if (person.Hub is not null)
    {
        await person.Hub.DisposeAsync();
    }
    person.Http.Dispose();
}
return errors.IsEmpty && delivered >= expected * 0.99 ? 0 : 1;

internal sealed class Person(int index, string name, Uri server, HttpClient http, string token)
{
    public int Index { get; } = index;

    public string Name { get; } = name;

    public Uri Server { get; } = server;

    public HttpClient Http { get; } = http;

    public string Token { get; } = token;

    public HubConnection? Hub { get; set; }
}

internal sealed record Options(IReadOnlyList<Uri> Servers, string Prefix, int Users, int PerGroup, int Messages, int IntervalMs, string Password, bool Create)
{
    public static Options Parse(string[] args)
    {
        var create = args.Contains("--criar");
        args = [.. args.Where(a => a != "--criar")];
        var values = new Dictionary<string, string>();
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            values[args[i]] = args[i + 1];
        }
        string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
        return new Options(
            [.. Get("--servidores", "http://127.0.0.1:5180").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => new Uri(s.TrimEnd('/') + "/"))],
            Get("--prefixo", "carga-"),
            int.Parse(Get("--usuarios", "100")),
            int.Parse(Get("--por-grupo", "10")),
            int.Parse(Get("--mensagens", "10")),
            int.Parse(Get("--intervalo-ms", "2000")),
            values.GetValueOrDefault("--senha") ?? Environment.GetEnvironmentVariable("CARGA_SENHA") ?? throw new ArgumentException("Informe --senha ou CARGA_SENHA."),
            create);
    }
}
