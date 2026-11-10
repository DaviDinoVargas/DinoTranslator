using DinoTranslator.Server.Data;
using DinoTranslator.Shared.Social;

namespace DinoTranslator.Server.Social;

/// <summary>Resultado de uma operação da versão paga: o valor, ou o código HTTP e a mensagem para a pessoa.</summary>
public readonly record struct Outcome<T>(T? Value, int Status, string? Error)
{
    public static Outcome<T> Ok(T value) => new(value, StatusCodes.Status200OK, null);

    public static Outcome<T> Fail(int status, string error) => new(default, status, error);

    public static implicit operator Outcome<T>(Problem problem) => Fail(problem.Status, problem.Message);

    public IResult ToResult() => Error is null
        ? Value is null ? Results.NoContent() : Results.Ok(Value)
        : Results.Problem(statusCode: Status, title: Error);
}

/// <summary>Falhas comuns, com a mesma mensagem em todo lugar.</summary>
public readonly record struct Problem(int Status, string Message)
{
    /// <summary>Também para o que existe mas a pessoa não pode ver: não revela o que existe.</summary>
    public static Problem NotFound => new(StatusCodes.Status404NotFound, "Não encontrado.");

    public static Problem Forbidden(string message = "Você não tem permissão para isso.") => new(StatusCodes.Status403Forbidden, message);

    public static Problem Invalid(string message) => new(StatusCodes.Status400BadRequest, message);

    public static Problem Conflict(string message) => new(StatusCodes.Status409Conflict, message);
}

/// <summary>Nada a devolver além do "deu certo".</summary>
public sealed record Done
{
    public static readonly Done Value = new();
}

public static class Text
{
    /// <summary>Nome de grupo ou canal: sem espaços nas pontas, sem caracteres de controle, 1 a 64.</summary>
    public static string? CleanName(string? name)
    {
        var clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length is > 0 and <= SocialLimits.MaxNameLength ? clean : null;
    }

    /// <summary>Texto de mensagem: tira só o caractere nulo; precisa ter algo além de espaços.</summary>
    public static string? CleanMessage(string? content)
    {
        var clean = (content ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal).TrimEnd();
        return clean.Length is > 0 and <= SocialLimits.MaxMessageLength && !string.IsNullOrWhiteSpace(clean) ? clean : null;
    }

    public static UserSummary Summary(User user) => new(user.Id, user.UserName, user.DisplayName, user.Language);

    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
