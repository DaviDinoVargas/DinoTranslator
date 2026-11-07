# Teste de ponta a ponta da chamada por código, no próprio computador.
# Abre dois apps (build Debug) com perfis separados — Dino (pt-BR) e Anna (ru-RU) —,
# troca convite e resposta pela interface e confere conexão, código de segurança,
# mídia, legendas traduzidas (motor simulado só no computador do Dino), tela compartilhada
# em 2K e encerramento. Câmera e microfone são simulados pelo
# WebView2; a captura de tela real escolhe sozinha a janela do próprio app.
#
# Uso (na raiz do repositório, depois de "dotnet build"):
#   powershell -File tests/e2e/chamada.ps1
#   powershell -File tests/e2e/chamada.ps1 -Shots C:\caminho\prefixo   (salva imagens da interface)

param([string]$Shots)

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
    foreach ($person in $people) {
        New-Item -ItemType Directory -Force $person.Folder | Out-Null
        [IO.File]::WriteAllText("$($person.Folder)\perfil.json", $person.Profile)
        # Variáveis lidas só pelo build Debug; a versão final as ignora.
        $env:DINOTRANSLATOR_DATA = $person.Folder
        if ($person.Engine) { $env:DINOTRANSLATOR_ENGINE = $person.Engine } else { Remove-Item Env:DINOTRANSLATOR_ENGINE -ErrorAction SilentlyContinue }
        $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$($person.Port) --use-fake-device-for-media-stream --auto-select-desktop-capture-source=DinoTranslator"
        $processes += Start-Process -FilePath $exe.FullName -PassThru
    }
    Remove-Item Env:DINOTRANSLATOR_DATA, Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
    Remove-Item Env:DINOTRANSLATOR_ENGINE -ErrorAction SilentlyContinue

    if ($Shots) { $env:E2E_SHOTS = $Shots }
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
