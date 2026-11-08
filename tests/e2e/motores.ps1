# Comparativo dos motores numa chamada entre dois apps no mesmo computador (build Debug).
# Precisa dos motores extras e dos modelos instalados numa pasta no formato da instalação
# (python, cuda12, silero, faster-whisper-turbo, ct2 e modelos) e de um arquivo WAV de fala em
# português, que o app do Dino usa no lugar do microfone.
#
# Uso: powershell -File tests/e2e/motores.ps1 -Extras E:\DinoTranslator\extras -Fala E:\...\conversa.wav [-Shots prefixo]

param(
    [Parameter(Mandatory = $true)][string]$Extras,
    [Parameter(Mandatory = $true)][string]$Fala,
    [string]$Shots
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$exe = Get-ChildItem "$root\src\DinoTranslator.Desktop\bin\Debug" -Recurse -Filter DinoTranslator.exe |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "Compile antes: dotnet build" }

$work = Join-Path ([IO.Path]::GetTempPath()) "dinotranslator-motores-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work | Out-Null
# Silêncio para a Anna: o microfone dela não pode "falar" a mesma frase em português.
$silencio = Join-Path $work "silencio.wav"
$bytes = New-Object byte[] (48000 * 2 * 5)
$cabecalho = [byte[]](0x52,0x49,0x46,0x46) + [BitConverter]::GetBytes([int](36 + $bytes.Length)) + [byte[]](0x57,0x41,0x56,0x45,0x66,0x6D,0x74,0x20) +
    [BitConverter]::GetBytes([int]16) + [BitConverter]::GetBytes([int16]1) + [BitConverter]::GetBytes([int16]1) + [BitConverter]::GetBytes([int]48000) +
    [BitConverter]::GetBytes([int](48000 * 2)) + [BitConverter]::GetBytes([int16]2) + [BitConverter]::GetBytes([int16]16) +
    [byte[]](0x64,0x61,0x74,0x61) + [BitConverter]::GetBytes([int]$bytes.Length)
[IO.File]::WriteAllBytes($silencio, $cabecalho + $bytes)

$people = @(
    @{ Folder = "$work\dino"; Port = 9311; Audio = $Fala; Profile = '{"name":"Dino","language":"pt-BR","gender":"Male","formality":"Informal","timeZone":"America/Sao_Paulo"}' },
    @{ Folder = "$work\anna"; Port = 9312; Audio = $silencio; Profile = '{"name":"Anna","language":"ru-RU","gender":"Female","formality":"Informal","timeZone":"Europe/Moscow"}' }
)

$processes = @()
try {
    foreach ($person in $people) {
        New-Item -ItemType Directory -Force $person.Folder | Out-Null
        [IO.File]::WriteAllText("$($person.Folder)\perfil.json", $person.Profile)
        $env:DINOTRANSLATOR_DATA = $person.Folder
        $env:DINOTRANSLATOR_EXTRAS = $Extras
        Remove-Item Env:DINOTRANSLATOR_ENGINE -ErrorAction SilentlyContinue
        $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$($person.Port) --use-fake-device-for-media-stream --use-file-for-fake-audio-capture=`"$($person.Audio)`""
        $processes += Start-Process -FilePath $exe.FullName -PassThru
    }
    Remove-Item Env:DINOTRANSLATOR_DATA, Env:DINOTRANSLATOR_EXTRAS, Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS

    if ($Shots) { $env:E2E_SHOTS = $Shots }
    node "$PSScriptRoot\motores.mjs" 9311 9312
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Remove-Item Env:E2E_SHOTS -ErrorAction SilentlyContinue
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -Confirm:$false }
    }
    # O motor auxiliar (Python) fecha sozinho quando o app fecha; confere.
    Get-Process python -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Extras*" } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
