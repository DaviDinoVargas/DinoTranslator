using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32;

namespace DinoTranslator.Installation;

public sealed class InstallationException(string message) : Exception(message);

/// <summary>Onde a instalação deixa marcas no Windows. Os testes usam lugares só deles.</summary>
public sealed record InstallLocations(string RegistryKey, string StartMenuShortcut, string DesktopShortcut)
{
    public static InstallLocations Windows { get; } = new(
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DinoTranslator",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Installer.AppName + ".lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Installer.AppName + ".lnk"));
}

/// <summary>
/// Instalação por usuário (sem pedir administrador): copia o app para a pasta escolhida, deixa
/// o Desinstalar.exe junto, registra em "Aplicativos instalados" do Windows e cria os atalhos.
/// </summary>
public static class Installer
{
    public const string AppName = "DinoTranslator";
    public const string AppExecutable = "DinoTranslator.exe";
    public const string UninstallArgument = "/desinstalar";

    /// <summary>
    /// Cópia do inicializador do app (o .exe pequeno que abre o DinoTranslator.dll) com outro nome:
    /// aberto assim, o app vai direto para a desinstalação.
    /// </summary>
    public const string UninstallerExecutable = "Desinstalar.exe";

    private const string WebView2ClientKey = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    /// <summary>%LOCALAPPDATA%\Programs\DinoTranslator, o lugar comum de apps por usuário.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    /// <summary>Pasta de uma instalação anterior, se houver.</summary>
    public static string? InstalledFolder(InstallLocations? at = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey((at ?? InstallLocations.Windows).RegistryKey);
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>
    /// Pasta da instalação anterior: a registrada ou, se o registro sumiu, a do atalho do menu
    /// Iniciar. Assim a atualização cai na mesma pasta em vez de criar uma segunda cópia.
    /// </summary>
    public static string? PreviousFolder(InstallLocations? at = null)
    {
        at ??= InstallLocations.Windows;
        if (InstalledFolder(at) is { } registered)
        {
            return registered;
        }
        var target = ShellLink.TargetOf(at.StartMenuShortcut);
        return target is not null
            && string.Equals(Path.GetFileName(target), AppExecutable, StringComparison.OrdinalIgnoreCase)
            && File.Exists(target)
            ? Path.GetDirectoryName(target)
            : null;
    }

    /// <summary>O app foi aberto pelo Desinstalar.exe.</summary>
    public static bool IsUninstaller(string? processPath) =>
        string.Equals(Path.GetFileName(processPath), UninstallerExecutable, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Esta pasta é uma instalação feita pelo instalador: está registrada, ou o atalho do menu
    /// Iniciar aponta para ela (registro apagado por engano).
    /// </summary>
    public static bool IsInstalledCopy(string folder, InstallLocations? at = null)
    {
        at ??= InstallLocations.Windows;
        if (!File.Exists(Path.Combine(folder, AppExecutable)))
        {
            return false;
        }
        return SameFolder(InstalledFolder(at), folder) || IsInside(ShellLink.TargetOf(at.StartMenuShortcut), folder);
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
    public static void Install(Stream payload, string folder, string version, bool desktopShortcut, IProgress<int>? progress, InstallLocations? at = null)
    {
        at ??= InstallLocations.Windows;
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
            ClearPreviousVersion(target);
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

        // Primeiro o que permite desinstalar; os atalhos são conveniência e não podem impedir isso.
        PlaceUninstaller(target);
        Register(target, version, at);
        TryCreateShortcut(at.StartMenuShortcut, executable);
        if (desktopShortcut)
        {
            TryCreateShortcut(at.DesktopShortcut, executable);
        }
    }

    /// <summary>
    /// Conserta uma instalação sem o Desinstalar.exe ou sem o registro (de versões antigas, ou
    /// apagado por engano). Só age numa pasta instalada pelo instalador.
    /// </summary>
    public static bool Repair(string folder, string version, InstallLocations? at = null)
    {
        at ??= InstallLocations.Windows;
        var target = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (!IsInstalledCopy(target, at))
        {
            return false;
        }
        var repaired = PlaceUninstaller(target);
        using (var key = Registry.CurrentUser.OpenSubKey(at.RegistryKey))
        {
            if (key is null
                || !SameFolder(key.GetValue("InstallLocation") as string, target)
                || key.GetValue("UninstallString") as string != UninstallCommand(target)
                || key.GetValue("DisplayVersion") as string != version)
            {
                repaired = true;
            }
        }
        if (repaired)
        {
            Register(target, version, at);
        }
        return repaired;
    }

    /// <summary>Tira os atalhos e o registro desta instalação; a pasta é apagada depois que o app fechar.</summary>
    public static void Unregister(string folder, InstallLocations? at = null)
    {
        at ??= InstallLocations.Windows;
        // Só o que é desta pasta: outra instalação (ou outra cópia) continua intacta.
        foreach (var shortcut in new[] { at.StartMenuShortcut, at.DesktopShortcut })
        {
            if (IsInside(ShellLink.TargetOf(shortcut), folder))
            {
                File.Delete(shortcut);
            }
        }
        var registered = InstalledFolder(at);
        if (registered is null || SameFolder(registered, folder))
        {
            Registry.CurrentUser.DeleteSubKeyTree(at.RegistryKey, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// Espera o processo fechar e então apaga a pasta da instalação (e só ela). Os testes passam o
    /// processo a esperar e esperam a limpeza terminar.
    /// </summary>
    /// <remarks>
    /// O desinstalador roda de dentro da pasta: apagar antes de ele fechar leva arquivos que o
    /// .NET ainda usa ao encerrar, e ele quebra ao fechar a janela. Por isso a limpeza espera o
    /// processo terminar de verdade (PowerShell, Wait-Process) e tenta de novo enquanto algum
    /// arquivo ainda estiver preso. O rmdir do cmd apaga junções sem entrar nelas. O caminho e o
    /// processo vão por variáveis de ambiente, nunca montados no comando.
    /// </remarks>
    public static Process? DeleteFolderAfterExit(string folder, int? processId = null)
    {
        var target = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (!File.Exists(Path.Combine(target, AppExecutable)) || target.Contains('"') || Path.GetPathRoot(target) == target)
        {
            return null;
        }
        const string Script =
            "Wait-Process -Id $env:DINO_PROCESSO -ErrorAction SilentlyContinue; " +
            "for ($i = 0; $i -lt 30 -and (Test-Path -LiteralPath $env:DINO_PASTA); $i++) { " +
            "& \"$env:SystemRoot\\System32\\cmd.exe\" /d /c 'rmdir /s /q \"%DINO_PASTA%\"' 2>$null; " +
            "if (Test-Path -LiteralPath $env:DINO_PASTA) { Start-Sleep -Seconds 1 } }";
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", Script })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["DINO_PROCESSO"] = (processId ?? Environment.ProcessId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["DINO_PASTA"] = target;
        return Process.Start(start);
    }

    /// <summary>
    /// Apaga a versão anterior do app, menos os motores extras (vários GB baixados uma vez, que
    /// valem para as próximas versões).
    /// </summary>
    private static void ClearPreviousVersion(string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(target))
        {
            if (!string.Equals(Path.GetFileName(directory), ExtrasInstaller.FolderName, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        foreach (var file in Directory.EnumerateFiles(target))
        {
            File.Delete(file);
        }
    }

    private static void Register(string folder, string version, InstallLocations at)
    {
        using var key = Registry.CurrentUser.CreateSubKey(at.RegistryKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", Path.Combine(folder, AppExecutable));
        key.SetValue("InstallLocation", folder);
        key.SetValue("UninstallString", UninstallCommand(folder));
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(DirectorySize(folder) / 1024), RegistryValueKind.DWord);
    }

    private static string UninstallCommand(string folder) => $"\"{Path.Combine(folder, UninstallerExecutable)}\"";

    /// <summary>Copia o inicializador como Desinstalar.exe (de novo, se estiver faltando ou for de outra versão).</summary>
    private static bool PlaceUninstaller(string folder)
    {
        var launcher = new FileInfo(Path.Combine(folder, AppExecutable));
        var uninstaller = new FileInfo(Path.Combine(folder, UninstallerExecutable));
        if (uninstaller.Exists && uninstaller.Length == launcher.Length && uninstaller.LastWriteTimeUtc == launcher.LastWriteTimeUtc)
        {
            return false;
        }
        launcher.CopyTo(uninstaller.FullName, overwrite: true);
        return true;
    }

    private static void TryCreateShortcut(string shortcut, string executable)
    {
        try
        {
            ShellLink.Create(shortcut, executable, AppName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Sem o atalho o app continua instalado, registrado e com o Desinstalar.exe.
        }
    }

    private static bool SameFolder(string? a, string b) =>
        a is not null && string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

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
