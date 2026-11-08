using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
        ExtrasText.Text = $"Instalar também os motores e modelos de tradução (download de {Gigabytes(ExtrasCatalog.TotalSize)}). "
            + "Sem eles, dá para baixar depois em Ajustes › Tradução.";
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
        var extras = ExtrasCheck.IsChecked == true;
        var extrasNeeded = extras ? ExtrasCatalog.TotalSize * 2 : 0;
        if (Installer.FreeSpace(_folder) < payload.Length * 3 + SpareSpace + extrasNeeded)
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
            if (extras)
            {
                await InstallExtrasAsync();
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

    /// <summary>
    /// Motores e modelos de tradução: o app já está instalado, então uma falha aqui só vira um aviso
    /// (dá para baixar de novo pelos Ajustes do app).
    /// </summary>
    private async Task InstallExtrasAsync()
    {
        var root = ExtrasInstaller.RootFor(_folder);
        var missing = ExtrasInstaller.Missing(root);
        if (missing.Count == 0)
        {
            return;
        }
        ProgressHeading.Text = "Baixando os motores de tradução…";
        var progress = new Progress<ExtrasProgress>(p =>
        {
            Progress.Value = p.TotalBytes == 0 ? 100 : p.DoneBytes * 100.0 / p.TotalBytes;
            ProgressText.Text = $"{Gigabytes(p.DoneBytes)} de {Gigabytes(p.TotalBytes)} · {p.Title}";
        });
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DinoTranslator-Instalador");
            await Task.Run(() => ExtrasInstaller.InstallAsync(root, missing, http, progress, CancellationToken.None));
        }
        catch (Exception ex) when (ex is InstallationException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            ExtrasNote.Text = ex is InstallationException
                ? ex.Message + " O app está instalado; baixe os motores depois em Ajustes › Tradução."
                : "Os motores de tradução não terminaram de baixar. O app está instalado; baixe-os depois em Ajustes › Tradução.";
            ExtrasNote.Visibility = Visibility.Visible;
        }
    }

    private static string Gigabytes(long bytes) => $"{bytes / 1_000_000_000.0:0.0} GB";

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
