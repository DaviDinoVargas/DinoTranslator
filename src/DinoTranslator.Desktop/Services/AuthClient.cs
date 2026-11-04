using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using DinoTranslator.Shared.Auth;

namespace DinoTranslator.Desktop.Services;

public enum LoginResult
{
    Success,
    InvalidCredentials,
    TooManyAttempts,
    ServerUnavailable,
}

/// <summary>
/// Sessão do usuário: login, renovação automática do token de acesso e saída.
/// O token de acesso fica só em memória.
/// </summary>
public sealed class AuthClient(HttpClient http, ISessionStore store, TimeProvider time)
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    public event Action? SessionChanged;

    public UserProfile? CurrentUser => _session?.User;

    public async Task<LoginResult> LoginAsync(string userName, string password, bool remember, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(AuthRoutes.Login, new LoginRequest(userName, password), cancellationToken);
        }
        catch (HttpRequestException)
        {
            return LoginResult.ServerUnavailable;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LoginResult.ServerUnavailable;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return LoginResult.TooManyAttempts;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return LoginResult.InvalidCredentials;
            }
            if (!response.IsSuccessStatusCode)
            {
                return LoginResult.ServerUnavailable;
            }

            var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Resposta de login vazia.");
            await _gate.WaitAsync(cancellationToken);
            try
            {
                Apply(tokens, remember);
            }
            finally
            {
                _gate.Release();
            }
            return LoginResult.Success;
        }
    }

    /// <summary>Retoma a sessão salva ("manter conectado"), se houver.</summary>
    public async Task<bool> TryResumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null)
            {
                return true;
            }
            var refreshToken = store.LoadRefreshToken();
            return refreshToken is not null && await RenewAsync(refreshToken, remember: true, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Token de acesso válido, renovado antes de expirar; <c>null</c> se a sessão acabou.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_session is not { } session)
            {
                return null;
            }
            if (session.AccessTokenExpiresAt - time.GetUtcNow() > RenewBefore)
            {
                return session.AccessToken;
            }
            return await RenewAsync(session.RefreshToken, session.Remember, cancellationToken) ? _session?.AccessToken : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        Session? session;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            session = _session;
            End();
        }
        finally
        {
            _gate.Release();
        }

        if (session is null)
        {
            return;
        }
        try
        {
            using var _ = await http.PostAsJsonAsync(AuthRoutes.Logout, new RefreshRequest(session.RefreshToken), cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Sem conexão: a sessão local já foi apagada e o token expira sozinho no servidor.
        }
    }

    private async Task<bool> RenewAsync(string refreshToken, bool remember, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(AuthRoutes.Refresh, new RefreshRequest(refreshToken), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Servidor fora do ar: mantém o token salvo para tentar de novo depois.
            return false;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                End();
                return false;
            }
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (tokens is null)
            {
                return false;
            }
            Apply(tokens, remember);
            return true;
        }
    }

    private void Apply(TokenResponse tokens, bool remember)
    {
        _session = new Session(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.RefreshToken, tokens.User, remember);
        if (remember)
        {
            store.SaveRefreshToken(tokens.RefreshToken);
        }
        else
        {
            store.Clear();
        }
        SessionChanged?.Invoke();
    }

    private void End()
    {
        var hadSession = _session is not null;
        _session = null;
        store.Clear();
        if (hadSession)
        {
            SessionChanged?.Invoke();
        }
    }

    private sealed record Session(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        UserProfile User,
        bool Remember);
}
