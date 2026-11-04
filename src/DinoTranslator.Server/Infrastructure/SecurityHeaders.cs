namespace DinoTranslator.Server.Infrastructure;

/// <summary>
/// Cabeçalhos de segurança aplicados a todas as respostas. O servidor só expõe API,
/// então nada pode ser embutido em página, carregado como script ou usar câmera.
/// </summary>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), display-capture=()";
            await next(context);
        });
}
