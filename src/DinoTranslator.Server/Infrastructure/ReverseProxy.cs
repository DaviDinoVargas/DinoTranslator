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
    public static IServiceCollection AddReverseProxySupport(this IServiceCollection services, IConfiguration configuration)
    {
        var networks = configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [];

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var cidr in networks)
            {
                var network = System.Net.IPNetwork.Parse(cidr);
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(network.BaseAddress, network.PrefixLength));
            }
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        });

        return services;
    }
}
