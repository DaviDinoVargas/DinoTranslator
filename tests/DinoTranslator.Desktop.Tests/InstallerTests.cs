using System.IO;
using System.IO.Compression;
using DinoTranslator.Installation;
using Microsoft.Win32;

namespace DinoTranslator.Desktop.Tests;

/// <summary>
/// Instalação de verdade numa pasta temporária, com registro e atalhos em lugares só do teste:
/// a instalação real do computador nunca é tocada.
/// </summary>
public sealed class InstallerTests : IDisposable
{
    private const string Version = "9.9.9";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "DinoTranslator.Testes", Guid.NewGuid().ToString("N"));
    private readonly string _registryRoot = @"Software\DinoTranslator.Testes\" + Guid.NewGuid().ToString("N");
    private readonly InstallLocations _at;

    public InstallerTests()
    {
        Directory.CreateDirectory(_root);
        _at = new InstallLocations(
            _registryRoot + @"\Uninstall",
            Path.Combine(_root, "Menu Iniciar", "DinoTranslator.lnk"),
            Path.Combine(_root, "Area de trabalho", "DinoTranslator.lnk"));
    }

    private string Folder => Path.Combine(_root, "Programas", "DinoTranslator");

    [Fact]
    public void Instalacao_deixa_o_desinstalador_o_registro_e_os_atalhos()
    {
        Install(Folder);

        Assert.True(File.Exists(Path.Combine(Folder, Installer.UninstallerExecutable)));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Folder, Installer.AppExecutable)), File.ReadAllBytes(Path.Combine(Folder, Installer.UninstallerExecutable)));
        using var key = Registry.CurrentUser.OpenSubKey(_at.RegistryKey);
        Assert.NotNull(key);
        Assert.Equal(Folder, key.GetValue("InstallLocation"));
        Assert.Equal($"\"{Path.Combine(Folder, Installer.UninstallerExecutable)}\"", key.GetValue("UninstallString"));
        Assert.Equal(Version, key.GetValue("DisplayVersion"));
        Assert.Equal(Path.Combine(Folder, Installer.AppExecutable), ShellLink.TargetOf(_at.StartMenuShortcut), ignoreCase: true);
        Assert.Equal(Path.Combine(Folder, Installer.AppExecutable), ShellLink.TargetOf(_at.DesktopShortcut), ignoreCase: true);
        Assert.True(Installer.IsInstalledCopy(Folder, _at));
    }

    [Fact]
    public void Atualizacao_mantem_os_motores_extras()
    {
        Install(Folder);
        var model = Path.Combine(Folder, ExtrasInstaller.FolderName, "ct2", "opus-pt-zle", "model.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllText(model, "pesos");
        File.WriteAllText(Path.Combine(Folder, "sobra-da-versao-anterior.dll"), "x");

        Install(Folder);

        Assert.True(File.Exists(model));
        Assert.False(File.Exists(Path.Combine(Folder, "sobra-da-versao-anterior.dll")));
        Assert.True(File.Exists(Path.Combine(Folder, Installer.AppExecutable)));
    }

    [Fact]
    public void Atalho_que_falha_nao_impede_o_registro()
    {
        // A "pasta" da área de trabalho é um arquivo: o atalho não tem onde ficar.
        File.WriteAllText(Path.Combine(_root, "Area de trabalho"), "");

        Install(Folder);

        Assert.Equal(Folder, Installer.InstalledFolder(_at));
        Assert.True(File.Exists(Path.Combine(Folder, Installer.UninstallerExecutable)));
    }

    [Fact]
    public void Registro_apagado_volta_com_o_conserto()
    {
        Install(Folder);
        Registry.CurrentUser.DeleteSubKeyTree(_at.RegistryKey);
        File.Delete(Path.Combine(Folder, Installer.UninstallerExecutable));

        // O atalho do menu Iniciar mostra que a pasta é uma instalação.
        Assert.True(Installer.Repair(Folder, Version, _at));

        Assert.Equal(Folder, Installer.InstalledFolder(_at));
        Assert.True(File.Exists(Path.Combine(Folder, Installer.UninstallerExecutable)));
        Assert.False(Installer.Repair(Folder, Version, _at));
        Assert.True(Installer.Repair(Folder, "10.0.0", _at));
    }

    [Fact]
    public void Atualizacao_acha_a_pasta_anterior_mesmo_sem_registro()
    {
        Assert.Null(Installer.PreviousFolder(_at));
        Install(Folder);
        Registry.CurrentUser.DeleteSubKeyTree(_at.RegistryKey);

        Assert.Equal(Folder, Installer.PreviousFolder(_at), ignoreCase: true);
    }

    [Fact]
    public void Conserto_nao_age_numa_copia_que_nao_foi_instalada()
    {
        var copy = Path.Combine(_root, "bin", "Debug");
        Directory.CreateDirectory(copy);
        File.WriteAllText(Path.Combine(copy, Installer.AppExecutable), "app");

        Assert.False(Installer.IsInstalledCopy(copy, _at));
        Assert.False(Installer.Repair(copy, Version, _at));
        Assert.Null(Installer.InstalledFolder(_at));
        Assert.False(File.Exists(Path.Combine(copy, Installer.UninstallerExecutable)));
    }

    [Fact]
    public void Desinstalar_outra_copia_nao_mexe_na_instalacao()
    {
        Install(Folder);
        var other = Path.Combine(_root, "Outra");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, Installer.AppExecutable), "app");

        Installer.Unregister(other, _at);
        Assert.Equal(Folder, Installer.InstalledFolder(_at));
        Assert.True(File.Exists(_at.StartMenuShortcut));

        Installer.Unregister(Folder, _at);
        Assert.Null(Installer.InstalledFolder(_at));
        Assert.False(File.Exists(_at.StartMenuShortcut));
        Assert.False(File.Exists(_at.DesktopShortcut));
    }

    [Fact]
    public void Pasta_so_e_apagada_depois_que_o_desinstalador_fecha()
    {
        Install(Folder);
        var extras = Path.Combine(Folder, ExtrasInstaller.FolderName, "ct2", "model.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(extras)!);
        File.WriteAllText(extras, "pesos");
        // Faz o papel do Desinstalar.exe: continua aberto até o teste fechar.
        using var uninstaller = StartWaiting();

        using var cleanup = Installer.DeleteFolderAfterExit(Folder, uninstaller.Id);
        Assert.NotNull(cleanup);
        Assert.False(cleanup.WaitForExit(4000));
        Assert.True(File.Exists(Path.Combine(Folder, Installer.AppExecutable)));
        Assert.True(File.Exists(extras));

        uninstaller.Kill();
        Assert.True(cleanup.WaitForExit(30000));
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void Limpeza_nao_entra_em_juncoes_para_fora_da_pasta()
    {
        Install(Folder);
        var outside = Path.Combine(_root, "Fora");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "importante.txt"), "não apagar");
        var junction = Path.Combine(Folder, ExtrasInstaller.FolderName, "modelos");
        Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{junction}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            mklink.WaitForExit();
        }
        Assert.True(Directory.Exists(junction));
        using var finished = StartWaiting();
        finished.Kill();
        finished.WaitForExit();

        using var cleanup = Installer.DeleteFolderAfterExit(Folder, finished.Id);
        Assert.True(cleanup!.WaitForExit(30000));

        Assert.False(Directory.Exists(Folder));
        Assert.True(File.Exists(Path.Combine(outside, "importante.txt")));
    }

    [Fact]
    public void Limpeza_recusa_pasta_que_nao_e_do_app()
    {
        var other = Path.Combine(_root, "Documentos");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "texto.txt"), "x");

        Assert.Null(Installer.DeleteFolderAfterExit(other, Environment.ProcessId));
        Assert.True(File.Exists(Path.Combine(other, "texto.txt")));
    }

    private static System.Diagnostics.Process StartWaiting() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c ping -n 60 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        })!;

    [Theory]
    [InlineData(@"D:\DinoTranslator\Desinstalar.exe", true)]
    [InlineData(@"D:\DinoTranslator\desinstalar.EXE", true)]
    [InlineData(@"D:\DinoTranslator\DinoTranslator.exe", false)]
    [InlineData(null, false)]
    public void Desinstalador_e_reconhecido_pelo_nome(string? path, bool expected)
    {
        Assert.Equal(expected, Installer.IsUninstaller(path));
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registryRoot, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\DinoTranslator.Testes", writable: true))
        {
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\DinoTranslator.Testes", throwOnMissingSubKey: false);
            }
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Install(string folder)
    {
        using var payload = new MemoryStream();
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(Installer.AppExecutable).Open()))
            {
                writer.Write("inicializador do app");
            }
            using (var writer = new StreamWriter(zip.CreateEntry("wwwroot/index.html").Open()))
            {
                writer.Write("<!doctype html>");
            }
        }
        payload.Position = 0;
        Installer.Install(payload, folder, Version, desktopShortcut: true, progress: null, _at);
    }
}
