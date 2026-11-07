using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace DinoTranslator.Server.Infrastructure;

/// <summary>
/// O servidor roda atrás do Caddy, que termina o TLS. Só confiamos nos cabeçalhos
/// X-Forwarded-* vindos das redes configuradas em <c>ReverseProxy:KnownNetworks</c>;
/// sem isso, qualquer cliente poderia forjar o IP usado no limite de tentativas.
/// </summary>
public static class ReverseProxy
{
    public static IServiceCollection AddReverseProxySupport(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var cidr in configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            }
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        });

        return services;
    }
}
