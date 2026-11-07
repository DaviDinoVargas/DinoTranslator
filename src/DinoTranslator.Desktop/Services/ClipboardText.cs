using System.Runtime.InteropServices;
using System.Windows;

namespace DinoTranslator.Desktop.Services;

/// <summary>
/// Área de transferência pelo WPF: o menu de contexto do WebView2 fica desligado na versão
/// final, então copiar e colar os códigos passa por botões.
/// </summary>
public static class ClipboardText
{
    private const int Attempts = 5;

    public static void Copy(string text) => Retry(() => Clipboard.SetText(text));

    public static string Paste()
    {
        var text = string.Empty;
        Retry(() => text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty);
        return text;
    }

    /// <summary>Outro programa pode estar com a área de transferência aberta por um instante.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(action);
                return;
            }
            catch (COMException) when (attempt < Attempts)
            {
                Thread.Sleep(50);
            }
        }
    }
}
