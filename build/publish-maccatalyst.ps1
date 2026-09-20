<#
.SYNOPSIS
    Собирает .dmg Чудика под macOS на Apple Silicon.

.DESCRIPTION
    Результат — один файл artifacts/Chudik-<версия>-arm64.dmg: образ с бандлом
    «Чудик.app» и ссылкой на /Applications, то самое окно «перетащи сюда».

    Подпись ad-hoc. Это не украшение: на Apple Silicon macOS вообще не запускает
    неподписанный код, поэтому минимальная подпись обязательна. Но ad-hoc не
    означает «доверенная»: приложение не нотаризовано, и при первом открытии
    macOS скажет «не удаётся проверить разработчика». Разрешается в Системных
    настройках → Конфиденциальность и безопасность → «Всё равно открыть».
    Обход по Control-клику Apple убрала в macOS 15.

    Entitlements.plist из Platforms/MacCatalyst сюда НЕ подключается, и это
    осознанно. Автоподхват ищет Entitlements.plist в корне проекта, а он лежит
    в папке платформы, поэтому сейчас файл не применяется ни к одной сборке.
    Подключать его к этой раздаче нельзя: там com.apple.security.app-sandbox,
    а в песочнице SpecialFolder.UserProfile указывает внутрь контейнера
    приложения — принятые файлы уехали бы в невидимую папку вместо «Загрузок».
    Тот файл нужен для Mac App Store, и там же понадобится ветка для
    DownloadRoot в PlatformEnvironment.

    Собирается только на самом Mac: Xcode на других платформах нет.

.EXAMPLE
    ./build/publish-maccatalyst.ps1
    ./build/publish-maccatalyst.ps1 -Version 2.0.42 -VersionCode 42
#>
[CmdletBinding()]
param(
    [string]$Version = '2.0.0',
    [int]$VersionCode = 1
)

$ErrorActionPreference = 'Stop'

if (-not $IsMacOS) {
    throw 'Mac Catalyst собирается только на macOS: нужен Xcode'
}

$repo      = Split-Path -Parent $PSScriptRoot
$tfm       = 'net10.0-maccatalyst'
$rid       = 'maccatalyst-arm64'
$project   = [IO.Path]::Combine($repo, 'src', 'ChudikChat.App', 'ChudikChat.App.csproj')
$outputDir = [IO.Path]::Combine($repo, 'src', 'ChudikChat.App', 'bin', 'Release', $tfm)
$artifacts = [IO.Path]::Combine($repo, 'artifacts')
$dmg       = [IO.Path]::Combine($artifacts, "Chudik-$Version-arm64.dmg")

# При одной архитектуре бандл кладётся в подпапку с RID, при универсальной —
# прямо в папку цели. Архитектура у нас одна, но проверяем оба места:
# это умолчание SDK, а не обещание.
$appHomes = @([IO.Path]::Combine($outputDir, $rid), $outputDir)

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

function Find-AppBundle {
    foreach ($dir in $appHomes) {
        if (-not (Test-Path $dir)) { continue }

        $hit = @(Get-ChildItem $dir -Directory -Filter '*.app' -ErrorAction SilentlyContinue)
        if ($hit.Count -gt 1) {
            $names = ($hit | ForEach-Object { $_.FullName }) -join "`n  "
            throw "бандлов несколько, неясно какой брать:`n  $names"
        }
        if ($hit.Count -eq 1) { return $hit[0] }
    }
    return $null
}

Step 'Публикация приложения'

# Бандл от прошлой сборки убирается до публикации, иначе поиск ниже мог бы
# подобрать устаревший: инкрементальная сборка каталог не пересоздаёт.
$stale = Find-AppBundle
if ($stale) { Remove-Item $stale.FullName -Recurse -Force }

# Ни -r, ни -p:RuntimeIdentifier, ни -p:TargetFrameworks: всё это глобальные
# свойства, они протекают во все цели и во все проекты — подробности
# в build/publish-windows.ps1. Архитектура и CreatePackage заданы в csproj
# под условием maccatalyst, где протекать некуда.
dotnet publish $project -f $tfm -c Release `
    -p:ApplicationDisplayVersion=$Version `
    -p:ApplicationVersion=$VersionCode `
    -v minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "публикация не удалась (код $LASTEXITCODE)" }

$app = Find-AppBundle
if (-not $app) {
    throw "публикация прошла, но бандла .app нет ни в одном из: $($appHomes -join ', ')"
}

Write-Host "  бандл: $($app.FullName)"

Step 'Проверка архитектуры'

$exe = @(Get-ChildItem ([IO.Path]::Combine($app.FullName, 'Contents', 'MacOS')) -File -ErrorAction SilentlyContinue)
if ($exe.Count -eq 0) { throw "в бандле нет исполняемого файла в Contents/MacOS" }

$archs = @((lipo -archs $exe[0].FullName) -split '\s+' | Where-Object { $_ })
if ($LASTEXITCODE -ne 0) { throw "lipo не смог прочитать $($exe[0].Name) (код $LASTEXITCODE)" }

if ($archs.Count -ne 1 -or $archs[0] -ne 'arm64') {
    throw "в бандле архитектуры [$($archs -join ', ')], а ожидалась одна arm64"
}
Write-Host "  $($exe[0].Name): $($archs -join ', ')"

Step 'Проверка подписи'

# Своей подписи здесь нет и быть не должно. Бандл подписывает сам
# dotnet publish — ad-hoc и вместе с правами доступа, которые выдаёт
# сборка MAUI: allow-jit, клиент и сервер сети, выбор файлов.
#
# Проход `codesign --force --sign -` поверх этого права СТИРАЕТ: режима
# «сохранить то, что было» у codesign нет, права надо передавать заново
# ключом --entitlements. Раньше этот проход здесь стоял, и приезжало вот
# что: без allow-jit среда исполнения .NET не может выделить исполняемую
# память, и приложение умирает раньше, чем dyld загрузит хоть одну
# библиотеку. Значок прыгает в доке, окна нет, отчёта о падении нет,
# в журнале пусто. Ровно так вёл себя первый образ, который CI довёл
# до конца, — 2.0.7.
#
# --deep для ПРОВЕРКИ не устарела, в отличие от подписи: она убеждается,
# что подписано всё содержимое, а не только внешняя оболочка.
codesign --verify --deep --strict $app.FullName
if ($LASTEXITCODE -ne 0) { throw "подпись не прошла проверку (код $LASTEXITCODE)" }

# Права проверяются отдельно и поимённо. Без этой проверки поломка беззвучна:
# подпись остаётся целой, --verify выше проходит, сборка зеленеет — и всё
# это ровно до того момента, когда образ скачает живой человек.
$entFile = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'chudik-entitlements.plist')
Remove-Item $entFile -ErrorAction SilentlyContinue
codesign -d --entitlements $entFile --xml $app.FullName 2>$null

if (-not (Test-Path $entFile)) { throw 'в подписи нет прав доступа вовсе — приложение не запустится' }
$entitlements = Get-Content $entFile -Raw

foreach ($required in @(
    'com.apple.security.cs.allow-jit',
    'com.apple.security.network.client',
    'com.apple.security.network.server'
)) {
    if ($entitlements -notlike "*$required*") {
        throw "в подписи нет права $required — приложение не запустится"
    }
}

Write-Host '  подпись ad-hoc на месте, права тоже'

Step 'Сборка образа'

if (-not (Test-Path $artifacts)) { New-Item -ItemType Directory -Path $artifacts | Out-Null }

$staging = [IO.Path]::Combine($artifacts, 'dmg-staging')
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

try {
    # ditto, а не Copy-Item: обычное копирование теряет симлинки и права внутри
    # бандла, и подпись после него разваливается.
    ditto $app.FullName ([IO.Path]::Combine($staging, $app.Name))
    if ($LASTEXITCODE -ne 0) { throw "ditto не справился (код $LASTEXITCODE)" }

    # Ссылка на /Applications даёт привычное окно «перетащи сюда».
    ln -s /Applications ([IO.Path]::Combine($staging, 'Applications'))
    if ($LASTEXITCODE -ne 0) { throw "не удалось создать ссылку на /Applications (код $LASTEXITCODE)" }

    if (Test-Path $dmg) { Remove-Item $dmg -Force }

    # Размер образа задаётся явно, и это не перестраховка. Без -size hdiutil
    # считает его сам по содержимому и промахивается: на дереве из множества
    # мелких файлов запаса не хватает на служебные структуры файловой системы,
    # и создание падает с «No space left on device» — ровно это и случилось
    # на первом же прогоне в CI. Ошибка при этом указывает путь внутри
    # смонтированного тома, а не на диске, и выглядит как нехватка места
    # на машине, каковой не является.
    #
    # Полуторный запас плюс 64 МБ. На вес готового файла это не влияет:
    # UDZO сжимает только занятые блоки, пустые не попадают в образ вовсе.
    $contentBytes = (Get-ChildItem $app.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum

    $sizeMb = [Math]::Max(128, [int][Math]::Ceiling($contentBytes / 1MB * 1.5) + 64)
    Write-Host ("  содержимое {0:N0} МБ, образ создаётся на {1:N0} МБ" -f ($contentBytes / 1MB), $sizeMb)

    # Свободное место на самой машине — на случай, если однажды кончится и оно:
    # по одной этой строке в журнале две причины различаются сразу.
    Write-Host "  свободно на диске: $((df -h $artifacts | Select-Object -Last 1) -replace '\s+', ' ')"

    hdiutil create -size "${sizeMb}m" -volname 'Чудик' -srcfolder $staging -ov -format UDZO $dmg
    if ($LASTEXITCODE -ne 0) { throw "hdiutil не справился (код $LASTEXITCODE)" }
} finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}

$size = (Get-Item $dmg).Length / 1MB
Write-Host ("`nГотово: {0} ({1:N0} МБ, {2})" -f $dmg, $size, ($archs -join ', ')) -ForegroundColor Green
Write-Host "CFBundleShortVersionString $Version, CFBundleVersion $VersionCode, подпись ad-hoc."
Write-Host 'При первом открытии macOS предупредит о непроверенном разработчике:'
Write-Host 'Системные настройки -> Конфиденциальность и безопасность -> «Всё равно открыть».'
