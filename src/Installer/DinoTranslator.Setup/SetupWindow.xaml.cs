using System.Diagnostics;
using System.IO;
using System.Windows;
using DinoTranslator.Installation;
using Microsoft.Win32;

namespace DinoTranslator.Setup;

public partial class SetupWindow : Window
{
    private const string WebView2Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>Folga além do app instalado: o disco não pode ficar cheio.</summary>
    private const long SpareSpace = 500_000_000;

    private string _folder = Installer.PreviousFolder() ?? Installer.DefaultFolder;
    private bool _installing;

    public SetupWindow()
    {
        InitializeComponent();
        Heading.Text = $"Instalar o DinoTranslator {App.Version}";
        FolderText.Text = _folder;
        WebView2Notice.Visibility = Installer.HasWebView2() ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Onde instalar o DinoTranslator" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        // A pasta escolhida recebe uma subpasta própria: nada do que já está lá é tocado.
        var folder = Path.Combine(dialog.FolderName, Installer.AppName);
        if (Installer.ValidateFolder(folder) is { } error)
        {
            ShowError(error);
            return;
        }
        _folder = folder;
        FolderText.Text = folder;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        var payload = App.OpenPayload();
        if (payload is null)
        {
            ShowError("Este instalador está sem o pacote do app. Gere-o com tools/criar-instalador.ps1.");
            return;
        }
        if (Installer.FreeSpace(_folder) < payload.Length * 3 + SpareSpace)
        {
            payload.Dispose();
            ShowError("Falta espaço no disco escolhido. Clique em Alterar… e escolha outro disco.");
            return;
        }

        _installing = true;
        CloseButton.IsEnabled = false;
        WelcomePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        var progress = new Progress<int>(percent =>
        {
            Progress.Value = percent;
            ProgressText.Text = $"{percent}% · {_folder}";
        });
        var desktop = DesktopShortcut.IsChecked == true;

        try
        {
            await using (payload)
            {
                await Task.Run(() => Installer.Install(payload, _folder, App.Version, desktop, progress));
            }
            ProgressPanel.Visibility = Visibility.Collapsed;
            DonePanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // Qualquer falha volta para a tela inicial com o motivo, em vez de fechar o instalador.
            ProgressPanel.Visibility = Visibility.Collapsed;
            WelcomePanel.Visibility = Visibility.Visible;
            ShowError(ex switch
            {
                InstallationException => ex.Message,
                IOException or UnauthorizedAccessException => "Não foi possível gravar na pasta escolhida.",
                _ => "A instalação não terminou. Tente de novo; se continuar, escolha outra pasta.",
            });
        }
        finally
        {
            _installing = false;
            CloseButton.IsEnabled = true;
        }
    }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        if (OpenNow.IsChecked == true)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(_folder, Installer.AppExecutable)) { UseShellExecute = true, WorkingDirectory = _folder });
        }
        Close();
    }

    private void OnGetWebView2(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(WebView2Download) { UseShellExecute = true });

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (!_installing)
        {
            Close();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
