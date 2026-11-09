# Teste de ponta a ponta da chamada por código, no próprio computador.
# Abre dois apps (build Debug) com perfis separados — Dino (pt-BR) e Anna (ru-RU) —,
# troca convite e resposta pela interface e confere conexão, código de segurança,
# mídia, legendas traduzidas (motor simulado só no computador do Dino), tela compartilhada
# em 2K e encerramento. A câmera é simulada pelo WebView2; o microfone toca uma fala gerada
# pela voz do Windows (o bipe do microfone simulado é ruído para o supressor, que o apaga, como
# deve). A captura de tela real escolhe sozinha a janela do próprio app.
#
# Uso (na raiz do repositório, depois de "dotnet build"):
#   powershell -File tests/e2e/chamada.ps1
#   powershell -File tests/e2e/chamada.ps1 -Shots C:\caminho\prefixo   (salva imagens da interface)
#   powershell -File tests/e2e/chamada.ps1 -Manual   (sem o encontro automático: resposta colada)

param([string]$Shots, [switch]$Manual)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$exe = Get-ChildItem "$root\src\DinoTranslator.Desktop\bin\Debug" -Recurse -Filter DinoTranslator.exe |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "Compile antes: dotnet build" }

$work = Join-Path ([IO.Path]::GetTempPath()) "dinotranslator-e2e-$([Guid]::NewGuid().ToString('N'))"
$people = @(
    @{ Folder = "$work\dino"; Port = 9301; Engine = "simulado"; Profile = '{"name":"Dino","language":"pt-BR","gender":"Male","formality":"Informal","timeZone":"America/Sao_Paulo"}' },
    @{ Folder = "$work\anna"; Port = 9302; Engine = $null; Profile = '{"name":"Anna","language":"ru-RU","gender":"Female","formality":"Informal","timeZone":"Europe/Moscow"}' }
)

$processes = @()
try {
    # Fala com pausas entre as frases (cada pausa fecha um trecho), repetida em laço pelo WebView2.
    New-Item -ItemType Directory -Force $work | Out-Null
    $speech = Join-Path $work "fala.wav"
    Add-Type -AssemblyName System.Speech
    $voice = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(48000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $voice.SetOutputToWaveFile($speech, $format)
    $prompt = New-Object System.Speech.Synthesis.PromptBuilder
    foreach ($sentence in @("Hello, how are you today?", "I am testing the call with live captions.", "The weather is nice and sunny.")) {
        $prompt.AppendText($sentence)
        $prompt.AppendBreak([TimeSpan]::FromMilliseconds(1500))
    }
    $voice.Speak($prompt)
    $voice.Dispose()

    foreach ($person in $people) {
        New-Item -ItemType Directory -Force $person.Folder | Out-Null
        [IO.File]::WriteAllText("$($person.Folder)\perfil.json", $person.Profile)
        # Variáveis lidas só pelo build Debug; a versão final as ignora.
        $env:DINOTRANSLATOR_DATA = $person.Folder
        if ($person.Engine) { $env:DINOTRANSLATOR_ENGINE = $person.Engine } else { Remove-Item Env:DINOTRANSLATOR_ENGINE -ErrorAction SilentlyContinue }
        if ($Manual) { $env:DINOTRANSLATOR_RENDEZVOUS = "desligado" } else { Remove-Item Env:DINOTRANSLATOR_RENDEZVOUS -ErrorAction SilentlyContinue }
        $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$($person.Port) --use-fake-device-for-media-stream --use-file-for-fake-audio-capture=`"$speech`" --auto-select-desktop-capture-source=DinoTranslator"
        $processes += Start-Process -FilePath $exe.FullName -PassThru
    }
    Remove-Item Env:DINOTRANSLATOR_DATA, Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
    Remove-Item Env:DINOTRANSLATOR_ENGINE, Env:DINOTRANSLATOR_RENDEZVOUS -ErrorAction SilentlyContinue

    if ($Shots) { $env:E2E_SHOTS = $Shots }
    if ($Manual) { $env:E2E_MANUAL = "1" } else { Remove-Item Env:E2E_MANUAL -ErrorAction SilentlyContinue }
    node "$PSScriptRoot\chamada.mjs" 9301 9302
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Remove-Item Env:E2E_SHOTS -ErrorAction SilentlyContinue
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -Confirm:$false }
    }
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
