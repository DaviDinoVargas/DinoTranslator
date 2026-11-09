# Mede a supressão de ruído no app de verdade: o microfone simulado toca uma fala (voz do Windows)
# misturada com ventilador e teclado, e o teste de dispositivos passa por cada modo. Para cada um,
# anota o volume que sai da cadeia nos trechos só de ruído e nos trechos com fala: a diferença é
# o quanto a voz fica acima do ruído para quem ouve. Também confere os controles e o "ouvir".
#
# Uso (na raiz do repositório, depois de "dotnet build"):
#   powershell -File tests/e2e/ruido.ps1 [-Shots C:\caminho\prefixo]

param([string]$Shots)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$exe = Get-ChildItem "$root\src\DinoTranslator.Desktop\bin\Debug" -Recurse -Filter DinoTranslator.exe |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "Compile antes: dotnet build" }

$work = Join-Path ([IO.Path]::GetTempPath()) "dinotranslator-ruido-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force "$work\perfil" | Out-Null
$process = $null
try {
    $clean = Join-Path $work "fala.wav"
    Add-Type -AssemblyName System.Speech
    $voice = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(48000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $voice.SetOutputToWaveFile($clean, $format)
    $voice.Speak("Hello, I am testing the noise suppression. Can you hear me clearly over the keyboard and the fan?")
    $voice.Dispose()
    $noisy = Join-Path $work "fala-com-ruido.wav"
    node "$PSScriptRoot\ruido-mistura.mjs" $clean $noisy
    if ($LASTEXITCODE -ne 0) { throw "Falha ao misturar o ruído." }

    [IO.File]::WriteAllText("$work\perfil\perfil.json", '{"name":"Dino","language":"pt-BR","gender":"Male","formality":"Informal","timeZone":"America/Sao_Paulo"}')
    $env:DINOTRANSLATOR_DATA = "$work\perfil"
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9321 --use-fake-device-for-media-stream --use-file-for-fake-audio-capture=`"$noisy`""
    $process = Start-Process -FilePath $exe.FullName -PassThru
    Remove-Item Env:DINOTRANSLATOR_DATA, Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS

    if ($Shots) { $env:E2E_SHOTS = $Shots }
    node "$PSScriptRoot\ruido.mjs" 9321
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Remove-Item Env:E2E_SHOTS -ErrorAction SilentlyContinue
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -Confirm:$false }
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
