# Teste de ponta a ponta da chamada por código, no próprio computador.
# Abre dois apps (build Debug) com perfis separados — Dino (pt-BR) e Anna (ru-RU) —,
# troca convite e resposta pela interface e confere conexão, código de segurança,
# mídia e encerramento. Câmera e microfone são simulados pelo WebView2.
#
# Uso (na raiz do repositório, depois de "dotnet build"):
#   powershell -File tests/e2e/chamada.ps1

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$exe = Get-ChildItem "$root\src\DinoTranslator.Desktop\bin\Debug" -Recurse -Filter DinoTranslator.exe |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "Compile antes: dotnet build" }

$work = Join-Path ([IO.Path]::GetTempPath()) "dinotranslator-e2e-$([Guid]::NewGuid().ToString('N'))"
$people = @(
    @{ Folder = "$work\dino"; Port = 9301; Profile = '{"name":"Dino","language":"pt-BR","gender":"Male","formality":"Informal","timeZone":"America/Sao_Paulo"}' },
    @{ Folder = "$work\anna"; Port = 9302; Profile = '{"name":"Anna","language":"ru-RU","gender":"Female","formality":"Informal","timeZone":"Europe/Moscow"}' }
)

$processes = @()
try {
    foreach ($person in $people) {
        New-Item -ItemType Directory -Force $person.Folder | Out-Null
        [IO.File]::WriteAllText("$($person.Folder)\perfil.json", $person.Profile)
        # Variáveis lidas só pelo build Debug; a versão final as ignora.
        $env:DINOTRANSLATOR_DATA = $person.Folder
        $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$($person.Port) --use-fake-ui-for-media-stream --use-fake-device-for-media-stream"
        $processes += Start-Process -FilePath $exe.FullName -PassThru
    }
    Remove-Item Env:DINOTRANSLATOR_DATA, Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS

    node "$PSScriptRoot\chamada.mjs" 9301 9302
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -Confirm:$false }
    }
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
