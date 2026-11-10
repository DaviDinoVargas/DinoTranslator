using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Cli;
using DinoTranslator.Server.Data;
using DinoTranslator.Server.Infrastructure;
using DinoTranslator.Server.Room;
using DinoTranslator.Server.Social;

// "users ..." administra as contas e sai; sem argumentos, sobe o servidor.
var isUsersCommand = args is ["users", ..];

var builder = WebApplication.CreateBuilder(isUsersCommand ? [] : args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 64 * 1024;
});

builder.Services.AddProblemDetails();
builder.Services.AddScaling(builder.Configuration);
builder.Services.AddReverseProxySupport();
builder.Services.AddDatabase();
builder.Services.AddAccounts();
builder.Services.AddTokenAuthentication();
builder.Services.AddAuthRateLimits();
builder.Services.AddRoom(builder.Configuration);
builder.Services.AddSocial(builder.Configuration);
builder.Services.AddIceServers();

var app = builder.Build();

await app.Services.MigrateDatabaseAsync();

if (isUsersCommand)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    await using var scope = app.Services.CreateAsyncScope();
    var commands = new UserCommands(
        scope.ServiceProvider.GetRequiredService<UserAdministration>(),
        Console.Out,
        new ConsoleSecretReader());
    return await commands.RunAsync(args[1..]);
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseSecurityHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealth();
app.MapAuthEndpoints();
app.MapSocial();
app.MapIceServers();

await app.RunAsync();
return 0;

public partial class Program;
