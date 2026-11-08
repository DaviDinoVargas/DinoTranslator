using System.IO;
using System.Windows;
using DinoTranslator.Installation;

namespace DinoTranslator.Setup;

public partial class App : Application
{
    public static string Version { get; } = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Pacote do app embutido pelo script de build; ausente num build comum.</summary>
    public static Stream? OpenPayload() => typeof(App).Assembly.GetManifestResourceStream("payload.zip");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Sem janela, para testes: /silencioso [/pasta "C:\..."] [/sem-atalho]
        if (e.Args.Length > 0 && e.Args[0] == "/silencioso")
        {
            Shutdown(InstallSilently(e.Args));
            return;
        }

        MainWindow = new SetupWindow();
        MainWindow.Show();
    }

    private static int InstallSilently(string[] args)
    {
        var folderIndex = Array.IndexOf(args, "/pasta");
        var folder = folderIndex >= 0 && folderIndex + 1 < args.Length ? args[folderIndex + 1] : Installer.DefaultFolder;
        using var payload = OpenPayload();
        if (payload is null)
        {
            return 2;
        }
        try
        {
            Installer.Install(payload, folder, Version, desktopShortcut: !args.Contains("/sem-atalho"), progress: null);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
