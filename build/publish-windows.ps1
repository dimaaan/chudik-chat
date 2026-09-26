<#
.SYNOPSIS
    Собирает установщик Чудика под Windows x64: публикация + MSI.

.DESCRIPTION
    Результат — один файл artifacts/Chudik-<версия>-x64.msi, самодостаточный:
    на целевой машине не нужны ни .NET, ни воркло́ды MAUI, ни рантайм
    Windows App SDK.

    Требуется один раз подготовить WiX:
        dotnet tool install --global wix
        wix eula accept wix7
        wix extension add -g WixToolset.Firewall.wixext
        wix extension add -g WixToolset.Util.wixext

.EXAMPLE
    ./build/publish-windows.ps1
    ./build/publish-windows.ps1 -Version 2.0.0
    ./build/publish-windows.ps1 -SkipPublish     # пересобрать только MSI
#>
[CmdletBinding()]
param(
    [string]$Version = '2.0.0',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$repo       = Split-Path -Parent $PSScriptRoot
$tfm        = 'net10.0-windows10.0.19041.0'
$project    = Join-Path $repo 'src\ChudikChat.App\ChudikChat.App.csproj'
$publishDir = Join-Path $repo 'artifacts\publish\win-x64'
$artifacts  = Join-Path $repo 'artifacts'
$msi        = Join-Path $artifacts "Chudik-$Version-x64.msi"
$icon       = Join-Path $repo "src\ChudikChat.App\obj\Release\$tfm\win-x64\resizetizer\r\appicon.ico"

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

if (-not $SkipPublish) {
    Step 'Публикация приложения'

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    # Ни -r, ни -p:TargetFrameworks здесь быть не должно, и это не стилистика.
    #
    # -r win-x64 задаёт ГЛОБАЛЬНОЕ свойство: оно применяется ко всем целевым
    # платформам проекта, включая net10.0-android. Android собирается на Mono,
    # и восстановление уходит искать несуществующий пакет
    # Microsoft.NETCore.App.Runtime.Mono.win-x64.
    #
    # -p:TargetFrameworks=<одна цель> кажется обходом, но глобальное свойство
    # протекает и в ChudikChat.Core, у которого цель net10.0: его assets-файл
    # восстанавливается не под ту платформу, и сборка падает с NETSDK1005.
    #
    # Правильного флага здесь не нужно вовсе: MAUI сама ставит win-x64 для
    # Windows-цели, а WindowsAppSDKSelfContained=true — умолчание при
    # WindowsPackageType=None.
    # ApplicationDisplayVersion уезжает в метаданные exe, чтобы в свойствах файла
    # стояло то же число, что в имени MSI и в теге релиза.
    dotnet publish $project -f $tfm -c Release --self-contained true `
        -p:ApplicationDisplayVersion=$Version `
        -o $publishDir -v minimal -nologo
    if ($LASTEXITCODE -ne 0) { throw "публикация не удалась (код $LASTEXITCODE)" }
}

if (-not (Test-Path $publishDir)) { throw "нет публикации: $publishDir" }
if (-not (Test-Path $icon))       { throw "нет иконки: $icon" }

$exe = Join-Path $publishDir 'ChudikChat.App.exe'
if (-not (Test-Path $exe)) { throw "в публикации нет ChudikChat.App.exe" }

$files = Get-ChildItem $publishDir -Recurse -File
Write-Host ("  файлов: {0}, размер: {1:N0} МБ" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

Step 'Сборка MSI'

$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    $candidate = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
    if (Test-Path $candidate) { $wix = $candidate } else { throw 'wix не найден: dotnet tool install --global wix' }
} else {
    $wix = $wix.Source
}

if (-not (Test-Path $artifacts)) { New-Item -ItemType Directory -Path $artifacts | Out-Null }
if (Test-Path $msi) { Remove-Item $msi -Force }

& $wix build (Join-Path $PSScriptRoot 'Chudik.wxs') `
    -arch x64 `
    -ext WixToolset.Firewall.wixext `
    -ext WixToolset.Util.wixext `
    -d PublishDir="$publishDir" `
    -d IconPath="$icon" `
    -d ProductVersion="$Version" `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build не удался (код $LASTEXITCODE)" }

$size = (Get-Item $msi).Length / 1MB
Write-Host ("`nГотово: {0} ({1:N0} МБ)" -f $msi, $size) -ForegroundColor Green
Write-Host 'Установщик не подписан — Windows покажет SmartScreen: «Подробнее» → «Выполнить в любом случае».'
