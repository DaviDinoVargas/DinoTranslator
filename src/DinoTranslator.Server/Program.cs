using DinoTranslator.Server.Data;
using DinoTranslator.Server.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 64 * 1024;
});

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddReverseProxySupport();
builder.Services.AddDatabase();

var app = builder.Build();

await app.Services.MigrateDatabaseAsync();

app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseSecurityHeaders();

app.MapHealthChecks("/health");

await app.RunAsync();

public partial class Program;
