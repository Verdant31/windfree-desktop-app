[CmdletBinding()]
param([string]$Python = 'python')

$ErrorActionPreference = 'Stop'
$brisaRoot = Split-Path -Parent $PSScriptRoot
$runtimePath = Join-Path $brisaRoot 'runtime'
$cachePath = Join-Path $brisaRoot '.cache'
$archivePath = Join-Path $cachePath 'python-3.14.6.zip'
$archiveUrl = 'https://www.python.org/ftp/python/3.14.6/python-3.14.6-embeddable-amd64.zip'
$archiveHash = 'c3a89aa7ead530bcac4729f3db7d000fbadb6ca7aa0461c2841867172aa0752a'

& $Python -c 'import sys; sys.exit(0 if sys.version_info[:2] == (3, 14) and sys.maxsize > 2**32 else 1)'
if ($LASTEXITCODE -ne 0) { throw 'Use uma instalacao de Python 3.14 x64 com pip.' }
& $Python -m pip --version
if ($LASTEXITCODE -ne 0) { throw 'O Python selecionado precisa ter pip instalado.' }

New-Item -ItemType Directory -Force -Path $cachePath | Out-Null
if (!(Test-Path -LiteralPath $archivePath) -or (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archiveHash) {
    Invoke-WebRequest -Uri $archiveUrl -OutFile $archivePath -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archiveHash) {
    throw 'O arquivo Python baixado nao corresponde ao SHA-256 esperado.'
}
Expand-Archive -LiteralPath $archivePath -DestinationPath $runtimePath -Force

& $Python -m pip --isolated install --disable-pip-version-check --index-url https://pypi.org/simple --only-binary=:all: --require-hashes --no-deps --upgrade --target (Join-Path $brisaRoot 'libs') -r (Join-Path $brisaRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Nao foi possivel instalar as dependencias verificadas.' }
& (Join-Path $runtimePath 'python.exe') -c 'import sys; sys.path.insert(0, sys.argv[1]); import cbor2, cryptography, OpenSSL, smartthings_local' (Join-Path $brisaRoot 'libs')
if ($LASTEXITCODE -ne 0) { throw 'As dependencias nao puderam ser carregadas pelo runtime.' }
& (Join-Path $PSScriptRoot 'build.ps1')
Write-Output 'Brisa compilado. Cadastre os aparelhos antes de abrir Brisa.exe.'
