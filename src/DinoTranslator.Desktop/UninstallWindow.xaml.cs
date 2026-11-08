using System.IO;
using System.Windows;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Desktop.Services.Translation.Local;
using DinoTranslator.Installation;

namespace DinoTranslator.Desktop;

/// <summary>
/// Desinstalador próprio: aberto pelo Desinstalar.exe da pasta do app ou por "Aplicativos
/// instalados" do Windows. Só age sobre uma cópia instalada pelo instalador.
/// </summary>
public partial class UninstallWindow : Window
{
    private readonly string _folder = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
    private readonly bool _installed;

    public UninstallWindow()
    {
        InitializeComponent();
        _installed = Installer.IsInstalledCopy(_folder);
        if (!_installed)
        {
            Message.Text = "Esta cópia do DinoTranslator não foi instalada pelo instalador; não há o que remover.";
            DeleteData.Visibility = Visibility.Collapsed;
            RemoveButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (!_installed)
        {
            return;
        }
        if (Installer.IsRunning(_folder))
        {
            ShowError("Feche o DinoTranslator antes de desinstalar.");
            return;
        }

        try
        {
            if (DeleteData.IsChecked == true)
            {
                DeleteUserData();
            }
            Installer.Unregister(_folder);
            Installer.DeleteFolderAfterExit(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Parte dos arquivos estava em uso e não foi removida. Feche outros programas e tente de novo.");
            return;
        }

        ConfirmPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
    }

    /// <summary>Perfil, chave cifrada, sessão, dados do WebView2 e modelos (numa pasta escolhida, só os do catálogo).</summary>
    private static void DeleteUserData()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DinoTranslator");
        var settings = new TranslationSettingsStore(Path.Combine(data, "motores.dat")).Current;
        if (settings.ModelsFolder is { } models)
        {
            new ModelFolder(models).Remove();
        }
        if (Directory.Exists(data))
        {
            Directory.Delete(data, recursive: true);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
