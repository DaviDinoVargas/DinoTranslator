# Teste de ponta a ponta da versão Plus: servidor(es) de verdade e dois apps (Dino e Anna).
# Os dois entram na conta, o Dino cria um grupo e manda o convite, a Anna entra, os dois
# conversam no canal (cada um lê no próprio idioma, com o motor simulado), viram amigos e
# conversam na conversa direta. Com -Instancias 2 (exige Redis), cada app fala com uma
# instância diferente da API: o tempo real precisa atravessar o Redis.
#
# Uso (na raiz do repositório, depois de "dotnet build"):
#   powershell -File tests/e2e/plus.ps1                                   (SQLite, uma instância)
#   powershell -File tests/e2e/plus.ps1 -Banco "Host=…;Database=…;Username=…;Password=…" -Redis 127.0.0.1:6379 -Instancias 2
#   … -Shots C:\caminho\prefixo   (salva imagens da interface)

param([string]$Banco, [string]$Redis, [int]$Instancias = 1, [string]$Shots)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$exe = Get-ChildItem "$root\src\DinoTranslator.Desktop\bin\Debug" -Recurse -Filter DinoTranslator.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$server = Get-ChildItem "$root\src\DinoTranslator.Server\bin\Debug" -Recurse -Filter DinoTranslator.Server.dll | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe -or -not $server) { throw "Compile antes: dotnet build" }
if ($Instancias -gt 1 -and -not $Redis) { throw "Mais de uma instância exige -Redis." }

$work = Join-Path ([IO.Path]::GetTempPath()) "dinotranslator-plus-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work | Out-Null
if (-not $Banco) { $Banco = "Data Source=$work\servidor.db" }
$key = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($key)

function Set-ServerEnvironment {
    $env:ConnectionStrings__Database = $Banco
    $env:Jwt__SigningKey = [Convert]::ToBase64String($key)
    $env:ASPNETCORE_ENVIRONMENT = "Production"
    $env:Auth__LoginPermitsPerMinute = "1000"
    $env:Auth__RegisterPermitsPerHour = "1000"
    if ($Redis) { $env:Redis__Configuration = $Redis } else { Remove-Item Env:Redis__Configuration -ErrorAction SilentlyContinue }
}

$processes = @()
try {
    Set-ServerEnvironment
    $ports = @(5180..(5180 + $Instancias - 1))
    foreach ($port in $ports) {
        $env:ASPNETCORE_URLS = "http://127.0.0.1:$port"
        $processes += Start-Process dotnet -ArgumentList "`"$($server.FullName)`"" -WorkingDirectory $server.DirectoryName -PassThru -WindowStyle Hidden
    }
    foreach ($port in $ports) {
        $ready = $false
        for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
            try { $ready = (Invoke-WebRequest "http://127.0.0.1:$port/health/pronto" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $ready) { throw "Servidor na porta $port não respondeu." }
    }

    $password = "senha-de-teste-bem-forte"
    $people = @(
        @{ User = "dino"; Name = "Dino"; Language = "pt-BR"; Port = 9341; Server = $ports[0] },
        @{ User = "anna"; Name = "Anna"; Language = "ru-RU"; Port = 9342; Server = $ports[-1] }
    )
    foreach ($person in $people) {
        $body = @{ userName = $person.User; password = $password; displayName = $person.Name; language = $person.Language; gender = "Unspecified" } | ConvertTo-Json
        Invoke-RestMethod "http://127.0.0.1:$($ports[0])/api/auth/register" -Method Post -Body $body -ContentType "application/json" | Out-Null
        & dotnet $server.FullName users plan $person.User plus | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Falha ao ativar o Plus de $($person.User)." }
    }

    foreach ($person in $people) {
        $folder = "$work\$($person.User)"
        New-Item -ItemType Directory -Force $folder | Out-Null
        [IO.File]::WriteAllText("$folder\perfil.json", "{`"name`":`"$($person.Name)`",`"language`":`"$($person.Language)`",`"gender`":`"Unspecified`",`"formality`":`"Informal`",`"timeZone`":`"UTC`"}")
        [IO.File]::WriteAllText("$folder\plus-servidor.json", "{`"servidor`":`"http://127.0.0.1:$($person.Server)/`"}")
        $env:DINOTRANSLATOR_DATA = $folder
        $env:DINOTRANSLATOR_ENGINE = "simulado"
        $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$($person.Port) --use-fake-device-for-media-stream"
        $processes += Start-Process -FilePath $exe.FullName -PassThru
    }
    Remove-Item Env:DINOTRANSLATOR_DATA, Env:DINOTRANSLATOR_ENGINE, Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS

    if ($Shots) { $env:E2E_SHOTS = $Shots }
    $env:E2E_SENHA = $password
    node "$PSScriptRoot\plus.mjs" 9341 9342
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Remove-Item Env:E2E_SHOTS, Env:E2E_SENHA -ErrorAction SilentlyContinue
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -Confirm:$false }
    }
    Start-Sleep -Milliseconds 800
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
