using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32;

namespace DinoTranslator.Installation;

public sealed class InstallationException(string message) : Exception(message);

/// <summary>
/// Instalação por usuário (sem pedir administrador): copia o app para a pasta escolhida,
/// cria os atalhos e registra o desinstalador em "Aplicativos instalados" do Windows.
/// </summary>
public static class Installer
{
    public const string AppName = "DinoTranslator";
    public const string AppExecutable = "DinoTranslator.exe";
    public const string UninstallArgument = "/desinstalar";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DinoTranslator";
    private const string WebView2ClientKey = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    /// <summary>%LOCALAPPDATA%\Programs\DinoTranslator, o lugar comum de apps por usuário.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string StartMenuShortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    public static string DesktopShortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>Pasta de uma instalação anterior, se houver.</summary>
    public static string? InstalledFolder()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>O WebView2 Runtime já vem no Windows 11 e na maioria dos Windows 10.</summary>
    public static bool HasWebView2()
    {
        static bool Has(RegistryKey root, string path) =>
            root.OpenSubKey(path)?.GetValue("pv") is string version && version != "0.0.0.0";
        return Has(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + WebView2ClientKey)
            || Has(Registry.LocalMachine, @"SOFTWARE\" + WebView2ClientKey)
            || Has(Registry.CurrentUser, @"Software\" + WebView2ClientKey);
    }

    /// <summary>Pasta aceita: caminho completo num disco fixo deste computador.</summary>
    public static string? ValidateFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "Escolha uma pasta num disco deste computador.";
        }
        var root = Path.GetPathRoot(folder);
        if (root is null || new DriveInfo(root) is not { IsReady: true, DriveType: DriveType.Fixed })
        {
            return "Escolha uma pasta num disco fixo deste computador.";
        }
        return null;
    }

    public static long FreeSpace(string folder) => new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;

    public static bool IsRunning(string folder) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExecutable))
            .Any(p => p.Id != Environment.ProcessId && IsInside(TryGetPath(p), folder));

    /// <summary>Extrai o pacote do app; uma instalação anterior na mesma pasta é substituída.</summary>
    public static void Install(Stream payload, string folder, string version, bool desktopShortcut, IProgress<int>? progress)
    {
        if (ValidateFolder(folder) is { } error)
        {
            throw new InstallationException(error);
        }
        if (IsRunning(folder))
        {
            throw new InstallationException("Feche o DinoTranslator antes de instalar.");
        }

        var target = Path.GetFullPath(folder);
        if (Directory.Exists(target) && File.Exists(Path.Combine(target, AppExecutable)))
        {
            Directory.Delete(target, recursive: true);
        }
        Directory.CreateDirectory(target);

        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        var total = archive.Entries.Count;
        var done = 0;
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
            // Proteção contra pacote adulterado: nada pode ser escrito fora da pasta da instalação.
            if (!IsInside(destination, target))
            {
                throw new InstallationException("Pacote de instalação inválido.");
            }
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
            progress?.Report(++done * 100 / total);
        }

        var executable = Path.Combine(target, AppExecutable);
        if (!File.Exists(executable))
        {
            throw new InstallationException("Pacote de instalação incompleto.");
        }

        ShellLink.Create(StartMenuShortcut, executable, AppName);
        if (desktopShortcut)
        {
            ShellLink.Create(DesktopShortcut, executable, AppName);
        }
        Register(target, executable, version);
    }

    /// <summary>Tira atalhos e registro; a pasta é apagada depois que o app fechar.</summary>
    public static void Unregister()
    {
        File.Delete(StartMenuShortcut);
        File.Delete(DesktopShortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// O próprio app está rodando de dentro da pasta: um processo do sistema espera ele fechar
    /// e apaga a pasta da instalação (e só ela).
    /// </summary>
    public static void DeleteFolderAfterExit(string folder)
    {
        var target = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (!File.Exists(Path.Combine(target, AppExecutable)) || target.Contains('"'))
        {
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{target}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    private static void Register(string folder, string executable, string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", executable);
        key.SetValue("InstallLocation", folder);
        key.SetValue("UninstallString", $"\"{executable}\" {UninstallArgument}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(DirectorySize(folder) / 1024), RegistryValueKind.DWord);
    }

    private static long DirectorySize(string folder) =>
        new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private static bool IsInside(string? path, string folder)
    {
        if (path is null)
        {
            return false;
        }
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
