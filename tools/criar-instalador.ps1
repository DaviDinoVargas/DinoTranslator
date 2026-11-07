# Gera o instalador do DinoTranslator: um único DinoTranslatorSetup-<versão>.exe que não
# exige .NET instalado (app e instalador autocontidos para Windows x64).
#
# Uso (na raiz do repositório):
#   powershell -File tools/criar-instalador.ps1
#   powershell -File tools/criar-instalador.ps1 -Saida E:\builds
#
# Saída padrão: artifacts\ (fora do Git), com o SHA-256 do instalador ao lado.

param([string]$Saida = (Join-Path $PSScriptRoot "..\artifacts"))

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$work = Join-Path $root "artifacts\instalador-trabalho"
$app = Join-Path $work "app"
$payload = Join-Path $work "payload.zip"

if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work, $Saida | Out-Null

Write-Host "1/4 Publicando o app..."
dotnet publish (Join-Path $root "src\DinoTranslator.Desktop") -c Release -r win-x64 --self-contained -o $app -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar o app." }

Write-Host "2/4 Removendo o que não roda em Windows x64..."
foreach ($extra in @("runtimes\win-arm64", "runtimes\win-x86", "runtimes\vulkan\linux-x64", "runtimes\linux-x64", "runtimes\osx-x64", "runtimes\osx-arm64")) {
    $path = Join-Path $app $extra
    if (Test-Path $path) { Remove-Item -Recurse -Force $path }
}
Get-ChildItem $app -Recurse -Filter *.pdb | Remove-Item -Force

Write-Host "3/4 Compactando o pacote..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($app, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "4/4 Gerando o instalador..."
$setup = Join-Path $work "setup"
dotnet publish (Join-Path $root "src\Installer\DinoTranslator.Setup") -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:PayloadZip="$payload" -o $setup -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Falha ao gerar o instalador." }

$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$installer = Join-Path (Resolve-Path $Saida) "DinoTranslatorSetup-$version.exe"
Copy-Item (Join-Path $setup "DinoTranslatorSetup.exe") $installer -Force
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$installer.sha256" -Value "$hash  DinoTranslatorSetup-$version.exe" -Encoding ascii

Remove-Item -Recurse -Force $work
Write-Host ""
Write-Host "Instalador: $installer"
Write-Host ("Tamanho: {0:N0} MB" -f ((Get-Item $installer).Length / 1MB))
Write-Host "SHA-256: $hash"
