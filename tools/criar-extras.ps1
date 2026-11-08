# Empacota os motores extras para a release "componentes-N" do GitHub: um zip por componente
# (cada um abaixo do limite de 2 GB por arquivo do GitHub), com tamanho e SHA-256 para o catálogo
# do app (src\Installer\DinoTranslator.Installation\ExtrasCatalog.cs).
#
# A pasta de origem tem o mesmo formato da instalação ("extras" ao lado do app):
#   python\                  Python 3.11 incorporável + faster-whisper, CTranslate2 e sentencepiece
#   cuda12\                  DLLs da NVIDIA (cuBLAS, cuDNN, NVRTC) usadas pelo CTranslate2
#   silero\                  Silero VAD (ONNX)
#   faster-whisper-turbo\    Whisper large-v3-turbo no formato do CTranslate2
#   ct2\<modelo>\            NLLB e OPUS-MT convertidos (tools: ver docs/PLANEJAMENTO.md)
#
# Uso: powershell -File tools/criar-extras.ps1 -Origem E:\DinoTranslator\extras -Destino E:\DinoTranslator\pacotes

param(
    [Parameter(Mandatory = $true)][string]$Origem,
    [Parameter(Mandatory = $true)][string]$Destino,
    # Refaz só estes componentes (o manifesto guarda os outros como estavam).
    [string[]]$Somente
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force $Destino | Out-Null

$componentes = @(
    @{ Nome = "motor-python"; Pasta = "python" },
    @{ Nome = "motor-cuda12"; Pasta = "cuda12" },
    @{ Nome = "silero"; Pasta = "silero" },
    @{ Nome = "faster-whisper-turbo"; Pasta = "faster-whisper-turbo" }
)
foreach ($modelo in Get-ChildItem (Join-Path $Origem "ct2") -Directory | Sort-Object Name) {
    $componentes += @{ Nome = "ct2-$($modelo.Name)"; Pasta = "ct2\$($modelo.Name)" }
}

$arquivoManifesto = Join-Path $Destino "manifesto.json"
$anterior = @{}
if ($Somente -and (Test-Path $arquivoManifesto)) {
    foreach ($item in (Get-Content $arquivoManifesto -Raw | ConvertFrom-Json)) { $anterior[$item.nome] = $item }
}
$manifesto = @()
foreach ($c in $componentes) {
    if ($Somente -and $c.Nome -notin $Somente -and $anterior.ContainsKey($c.Nome)) {
        $manifesto += $anterior[$c.Nome]
        continue
    }
    $pasta = Join-Path $Origem $c.Pasta
    $zip = Join-Path $Destino "$($c.Nome).zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    $arquivo = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $base = (Resolve-Path $pasta).Path.TrimEnd('\') + '\'
        foreach ($f in Get-ChildItem $pasta -Recurse -File) {
            $relativo = $f.FullName.Substring($base.Length).Replace('\', '/')
            # Sobras de download e caches do Python não vão.
            if ($relativo -like ".cache/*" -or $relativo -like "*/__pycache__/*" -or $relativo -like "__pycache__/*" -or $f.Name -in @(".gitattributes", "README.md")) { continue }
            # Pesos dos modelos (int8, fp16) quase não comprimem: só armazena. DLLs comprimem bem.
            $nivel = if ($f.Name -eq "model.bin") { [System.IO.Compression.CompressionLevel]::NoCompression } else { [System.IO.Compression.CompressionLevel]::Optimal }
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($arquivo, $f.FullName, $relativo, $nivel) | Out-Null
        }
    }
    finally {
        $arquivo.Dispose()
    }
    $info = Get-Item $zip
    if ($info.Length -ge 2GB) { throw "$($c.Nome) passou de 2 GB ($($info.Length) bytes)." }
    $manifesto += [pscustomobject]@{
        nome = $c.Nome
        pasta = $c.Pasta.Replace('\', '/')
        tamanho = $info.Length
        sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    Write-Host ("{0,-28} {1,8:N0} MB" -f $c.Nome, ($info.Length / 1MB))
}
$manifesto | ConvertTo-Json | Set-Content $arquivoManifesto -Encoding utf8
Write-Host "Manifesto em $arquivoManifesto"
