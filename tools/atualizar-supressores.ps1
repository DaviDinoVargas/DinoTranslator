# Baixa de novo os supressores de ruído do app (src/DinoTranslator.Desktop/wwwroot/js/vendor) dos
# pacotes npm listados em ORIGEM.json e confere tudo: o SHA-256 do pacote e o de cada arquivo
# copiado. Serve para provar de onde os arquivos vieram e para atualizar uma versão (mudando a
# origem e os SHA-256 no ORIGEM.json, depois de revisar o pacote novo).
#
# Uso (na raiz do repositório):
#   powershell -File tools/atualizar-supressores.ps1            (só confere)
#   powershell -File tools/atualizar-supressores.ps1 -Copiar    (confere e copia para o app)

param([switch]$Copiar, [string]$Trabalho = (Join-Path ([IO.Path]::GetTempPath()) "dino-supressores"))

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$vendor = Join-Path $root "src\DinoTranslator.Desktop\wwwroot\js\vendor"
$manifest = Get-Content (Join-Path $vendor "ORIGEM.json") -Raw -Encoding UTF8 | ConvertFrom-Json

function Sha256($path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }

if (Test-Path $Trabalho) { Remove-Item -Recurse -Force $Trabalho }
New-Item -ItemType Directory -Force $Trabalho | Out-Null
$problemas = 0
foreach ($pacote in $manifest.pacotes) {
    $uri = [Uri]$pacote.origem
    if ($uri.Scheme -ne "https" -or $uri.Host -ne "registry.npmjs.org") { throw "Origem não permitida: $($pacote.origem)" }
    $pasta = Join-Path $Trabalho ($pacote.pasta)
    New-Item -ItemType Directory -Force $pasta | Out-Null
    $tgz = Join-Path $pasta "pacote.tgz"
    Invoke-WebRequest -Uri $uri -OutFile $tgz -UseBasicParsing
    if ((Sha256 $tgz) -ne $pacote.sha256) { throw "SHA-256 diferente em $($pacote.nome) $($pacote.versao)" }
    tar -xzf $tgz -C $pasta
    foreach ($entrada in $pacote.copiados.PSObject.Properties) {
        $de = Join-Path $pasta ("package/" + $entrada.Name)
        $relativo = "$($pacote.pasta)/$($entrada.Value)"
        $esperado = $pacote.arquivos.$relativo
        $obtido = Sha256 $de
        if ($obtido -ne $esperado) { Write-Host "DIFERENTE  $relativo"; $problemas++; continue }
        $para = Join-Path $vendor $relativo
        if ($Copiar) {
            New-Item -ItemType Directory -Force (Split-Path $para) | Out-Null
            Copy-Item $de $para -Force
        }
        $noApp = if (Test-Path $para) { Sha256 $para } else { "" }
        Write-Host ("{0,-10} {1}" -f $(if ($noApp -eq $esperado) { "ok" } else { "NO APP ≠" }), $relativo)
        if ($noApp -ne $esperado) { $problemas++ }
    }
}
Remove-Item -Recurse -Force $Trabalho
if ($problemas -gt 0) { throw "$problemas arquivo(s) não conferem." }
Write-Host "Tudo confere com os pacotes publicados."
