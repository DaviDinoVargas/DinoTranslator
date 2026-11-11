# Teste de carga da versão Plus contra servidores de verdade (PostgreSQL + Redis), no próprio
# computador: sobe N instâncias da API, cria as contas, ativa o Plus delas e roda a ferramenta
# tools/DinoTranslator.Carga (contas conectadas em tempo real, em grupos, todas conversando).
#
# Uso (na raiz do repositório, depois de "dotnet build"):
#   powershell -File tests/carga/carga.ps1 -Banco "Host=…;Database=…;Username=…;Password=…" -Redis 127.0.0.1:6379
#       [-Instancias 2] [-Usuarios 300] [-PorGrupo 10] [-Mensagens 20] [-IntervaloMs 2000]
# Use um banco só para o teste: as contas carga-* ficam nele.

param(
    [Parameter(Mandatory = $true)][string]$Banco,
    [Parameter(Mandatory = $true)][string]$Redis,
    [int]$Instancias = 2,
    [int]$Usuarios = 300,
    [int]$PorGrupo = 10,
    [int]$Mensagens = 20,
    [int]$IntervaloMs = 2000)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$server = Get-ChildItem "$root\src\DinoTranslator.Server\bin\Debug" -Recurse -Filter DinoTranslator.Server.dll | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$tool = Get-ChildItem "$root\tools\DinoTranslator.Carga\bin\Debug" -Recurse -Filter DinoTranslator.Carga.dll | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $server -or -not $tool) { throw "Compile antes: dotnet build" }

$key = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($key)
$password = "carga-" + [Guid]::NewGuid().ToString("N")
$env:ConnectionStrings__Database = $Banco
$env:Redis__Configuration = $Redis
$env:Jwt__SigningKey = [Convert]::ToBase64String($key)
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:Auth__LoginPermitsPerMinute = "1000000"
$env:Auth__RegisterPermitsPerHour = "1000000"
$env:Logging__LogLevel__Default = "Warning"

$processes = @()
try {
    $ports = @(5180..(5180 + $Instancias - 1))
    foreach ($port in $ports) {
        $env:ASPNETCORE_URLS = "http://127.0.0.1:$port"
        $processes += Start-Process dotnet -ArgumentList "`"$($server.FullName)`"" -WorkingDirectory $server.DirectoryName -PassThru -WindowStyle Hidden
    }
    foreach ($port in $ports) {
        $ready = $false
        for ($i = 0; $i -lt 90 -and -not $ready; $i++) {
            try { $ready = (Invoke-WebRequest "http://127.0.0.1:$port/health/pronto" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $ready) { throw "Servidor na porta $port não respondeu." }
    }
    $urls = ($ports | ForEach-Object { "http://127.0.0.1:$_" }) -join ","
    $prefix = "carga" + (Get-Random -Maximum 99999) + "-"

    & dotnet $tool.FullName --criar --servidores $urls --prefixo $prefix --usuarios $Usuarios --senha $password
    if ($LASTEXITCODE -ne 0) { throw "Falha ao criar as contas." }
    & dotnet $server.FullName users plan "$prefix*" plus
    if ($LASTEXITCODE -ne 0) { throw "Falha ao ativar o Plus." }

    & dotnet $tool.FullName --servidores $urls --prefixo $prefix --usuarios $Usuarios --por-grupo $PorGrupo --mensagens $Mensagens --intervalo-ms $IntervaloMs --senha $password
    exit $LASTEXITCODE
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -Confirm:$false }
    }
}
