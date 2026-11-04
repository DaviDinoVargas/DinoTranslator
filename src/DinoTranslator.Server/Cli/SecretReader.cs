using System.Text;

namespace DinoTranslator.Server.Cli;

public interface ISecretReader
{
    /// <summary>Falso quando a entrada vem de um script (pipe); aí não há confirmação.</summary>
    bool IsInteractive { get; }

    string? ReadSecret(string prompt);
}

/// <summary>Lê senhas do terminal sem mostrar o que é digitado.</summary>
public sealed class ConsoleSecretReader : ISecretReader
{
    public bool IsInteractive => !Console.IsInputRedirected;

    public string? ReadSecret(string prompt)
    {
        if (!IsInteractive)
        {
            return Console.In.ReadLine();
        }

        Console.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();
                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                    }
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                    }
                    break;
            }
        }
    }
}
