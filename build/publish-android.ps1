<#
.SYNOPSIS
    Собирает APK Чудика под Android arm64.

.DESCRIPTION
    Результат — один файл artifacts/Chudik-<версия>-arm64.apk.

    Подпись отладочная. Постоянного ключа у проекта нет, а .NET Android при
    AndroidKeyStore != True подписывает пакет файлом debug.keystore и создаёт
    его, если файла нет. Отсюда следствие, о котором надо помнить: подпись
    у каждой машины своя, а в CI — своя у каждого запуска, поэтому новая версия
    НЕ встанет поверх старой. Телефон потребует сначала удалить приложение,
    а вместе с ним пропадёт и всё принятое: оно лежит в папке приложения
    во внешнем хранилище.

    Нужны воркло́д android, JDK от 17 до 21 и Android SDK с платформой android-36.
    Всё это приносит Visual Studio с нагрузкой .NET MAUI.

.EXAMPLE
    ./build/publish-android.ps1
    ./build/publish-android.ps1 -Version 2.0.42 -VersionCode 42
#>
[CmdletBinding()]
param(
    [string]$Version = '2.0.0',
    [int]$VersionCode = 1,
    [string]$JavaHome
)

$ErrorActionPreference = 'Stop'

# [IO.Path]::Combine, а не Join-Path с обратными слэшами: скрипт гоняется и на
# Windows локально, и на Linux в CI, а Join-Path с тремя сегментами до
# PowerShell 6 не умеет вовсе.
$repo      = Split-Path -Parent $PSScriptRoot
$tfm       = 'net10.0-android'
$project   = [IO.Path]::Combine($repo, 'src', 'ChudikChat.App', 'ChudikChat.App.csproj')
$outputDir = [IO.Path]::Combine($repo, 'src', 'ChudikChat.App', 'bin', 'Release', $tfm)
$artifacts = [IO.Path]::Combine($repo, 'artifacts')
$apk       = [IO.Path]::Combine($artifacts, "Chudik-$Version-arm64.apk")

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

Step 'Публикация приложения'

# Пакеты от прошлой сборки убираются до публикации, а не после. Иначе поиск
# готового APK ниже наткнётся на два файла и не сможет выбрать — а угадывать
# по времени изменения ненадёжно: инкрементальная сборка файл не трогает.
if (Test-Path $outputDir) {
    Get-ChildItem $outputDir -Recurse -File -Include '*.apk', '*.aab' -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

# Ни -r, ни -p:TargetFrameworks, ни -p:RuntimeIdentifiers здесь быть не должно:
# всё это ГЛОБАЛЬНЫЕ свойства, они протекают во все цели и во все проекты —
# подробности в build/publish-windows.ps1. Архитектура и формат пакета заданы
# в csproj под условием android, где протекать некуда.
#
# -o тоже нет: у android-публикации свой путь вывода, и подменять его незачем.
$publishArgs = @(
    'publish', $project,
    '-f', $tfm,
    '-c', 'Release',
    "-p:ApplicationDisplayVersion=$Version",
    "-p:ApplicationVersion=$VersionCode",
    '-v', 'minimal', '-nologo'
)

# JavaSdkDirectory — свойство только для android, в других проектах бездействует.
# В CI сюда уезжает JAVA_HOME_21_X64: на ubuntu по умолчанию стоит JDK 17, а это
# нижняя граница поддержки воркло́да.
if ($JavaHome) { $publishArgs += "-p:JavaSdkDirectory=$JavaHome" }

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "публикация не удалась (код $LASTEXITCODE)" }

Step 'Сборка пакета'

$bundles = @(Get-ChildItem $outputDir -Recurse -File -Filter '*.aab' -ErrorAction SilentlyContinue)
if ($bundles.Count -gt 0) {
    Write-Warning "собрался .aab — значит AndroidPackageFormats=apk из csproj не применилось"
}

# Подписанных APK после публикации ДВА, и они одинаковые: промежуточный лежит
# в корне цели сборки, окончательный — в publish. Берём второй, иначе выбор
# между ними был бы случайным. Фильтр по имени папки, а не жёсткий путь:
# при нескольких RID каталог publish уезжает на уровень глубже.
$found = @(Get-ChildItem $outputDir -Recurse -File -Filter '*-Signed.apk' -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq 'publish' })
if ($found.Count -eq 0) {
    throw "публикация прошла, но подписанного APK нет в $outputDir"
}
if ($found.Count -gt 1) {
    $names = ($found | ForEach-Object { $_.FullName }) -join "`n  "
    throw "подписанных APK несколько, неясно какой брать:`n  $names"
}

if (-not (Test-Path $artifacts)) { New-Item -ItemType Directory -Path $artifacts | Out-Null }
if (Test-Path $apk) { Remove-Item $apk -Force }
Copy-Item $found[0].FullName $apk -Force

# Проверка, что arm64 действительно единственная архитектура. Потеря
# RuntimeIdentifiers из csproj прошла бы иначе незаметно: пакет собрался бы,
# просто стал бы вдвое тяжелее за счёт кода под эмулятор.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($apk)
try {
    $abis = @($zip.Entries |
        Where-Object { $_.FullName -like 'lib/*/*' } |
        ForEach-Object { ($_.FullName -split '/')[1] } |
        Sort-Object -Unique)
} finally {
    $zip.Dispose()
}

if ($abis.Count -ne 1 -or $abis[0] -ne 'arm64-v8a') {
    throw "внутри APK архитектуры [$($abis -join ', ')], а ожидалась одна arm64-v8a"
}

$size = (Get-Item $apk).Length / 1MB
Write-Host ("`nГотово: {0} ({1:N0} МБ, {2})" -f $apk, $size, ($abis -join ', ')) -ForegroundColor Green
Write-Host "versionName $Version, versionCode $VersionCode, подпись отладочная."
Write-Host 'Обновление поверх предыдущей версии не встанет — её нужно удалить, и принятые файлы пропадут.'
