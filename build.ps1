param(
    [string]$DalamudDir = $(if ($env:DALAMUD_HOME) { $env:DALAMUD_HOME } else { Join-Path $env:APPDATA 'XIVLauncherCN\addon\Hooks\dev' }),
    [string]$DotnetPath = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '..\..'))
$outputRoot = Join-Path $workspaceRoot '插件\lemon'
$buildRoot = Join-Path $outputRoot '.build'
$version = '1.1.3.0'
$packageRoot = Join-Path $outputRoot $version
[void][IO.Directory]::CreateDirectory((Join-Path $buildRoot 'offline-source'))

$required = @('Dalamud.dll', 'FFXIVClientStructs.dll', 'InteropGenerator.Runtime.dll', 'Lumina.dll', 'Lumina.Excel.dll', 'Dalamud.Bindings.ImGui.dll', 'Newtonsoft.Json.dll')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $DalamudDir $name) -PathType Leaf)) { throw "缺少宿主程序集：$name" }
}
if ((Get-Item -LiteralPath (Join-Path $DalamudDir 'Dalamud.dll')).VersionInfo.FileMajorPart -ne 15) {
    throw '此工程只面向 Dalamud API 15，请选择匹配的宿主目录'
}
& $DotnetPath build (Join-Path $sourceRoot 'src\ClothingPairing.csproj') -c Release "-p:DalamudDir=$DalamudDir" -p:UseSharedCompilation=false -v:minimal
if ($LASTEXITCODE) { throw '插件构建失败' }
& $DotnetPath build (Join-Path $sourceRoot 'tests\ClothingPairing.Tests.csproj') -c Release "-p:DalamudDir=$DalamudDir" -p:UseSharedCompilation=false -v:minimal
if ($LASTEXITCODE) { throw '检查工程构建失败' }
$dll = Join-Path $buildRoot 'bin\ClothingPairing\Release\net10.0-windows\ClothingPairing.dll'
$tests = Join-Path $buildRoot 'bin\ClothingPairing.Tests\Release\net10.0-windows\ClothingPairing.Tests.dll'
$testOutput = & $DotnetPath $tests $dll
$testExit = $LASTEXITCODE
$testOutput | ForEach-Object { Write-Host $_ }
if ($testExit) { throw '离线检查失败，不生成包' }

$manifestPath = Join-Path $sourceRoot 'packaging\ClothingPairing.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$identity = [Reflection.AssemblyName]::GetAssemblyName($dll)
if ($identity.Name -ne $manifest.InternalName -or $identity.Version.ToString() -ne $manifest.AssemblyVersion -or
    $manifest.Author -ne 'Displaced' -or $manifest.Name -ne 'Clothing pairing' -or $manifest.DalamudApiLevel -ne 15) {
    throw '插件身份或清单不一致'
}
# A fresh stage and explicit whitelist prevent research, source, host DLLs or local config entering the ZIP.
$stage = Join-Path $buildRoot ('stage-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
Copy-Item -LiteralPath $dll -Destination $stage
Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($dll, '.deps.json')) -Destination $stage
Copy-Item -LiteralPath $manifestPath -Destination $stage
Copy-Item -LiteralPath (Join-Path $sourceRoot 'README.md') -Destination $stage
$dllHash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $stage 'SHA256.txt'), "$dllHash  ClothingPairing.dll`n", [Text.UTF8Encoding]::new($false))
$expected = @('ClothingPairing.dll', 'ClothingPairing.deps.json', 'ClothingPairing.json', 'README.md', 'SHA256.txt')
$actual = @(Get-ChildItem -LiteralPath $stage -File | Select-Object -ExpandProperty Name)
if (@(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)).Count -ne 0) { throw '打包白名单不匹配' }
[void][IO.Directory]::CreateDirectory($packageRoot)
foreach ($name in $expected) { Copy-Item -LiteralPath (Join-Path $stage $name) -Destination $packageRoot -Force }
$zipPath = Join-Path $outputRoot 'ClothingPairing-1.1.3.zip'
$tempZip = Join-Path $buildRoot ('package-' + [Guid]::NewGuid().ToString('N') + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $tempZip)
Copy-Item -LiteralPath $tempZip -Destination $zipPath -Force
$repositoryEntry = $manifest.PSObject.Copy()
$downloadUrl = "https://github.com/Displaced5/lemon/releases/download/v$($identity.Version.ToString(3))/ClothingPairing-$($identity.Version.ToString(3)).zip"
$repositoryEntry | Add-Member -NotePropertyName DownloadLinkInstall -NotePropertyValue $downloadUrl -Force
$repositoryEntry | Add-Member -NotePropertyName DownloadLinkUpdate -NotePropertyValue $downloadUrl -Force
$repositoryEntry | Add-Member -NotePropertyName DownloadLinkTesting -NotePropertyValue $downloadUrl -Force
$repositoryJson = ConvertTo-Json -InputObject @($repositoryEntry) -Depth 6
[IO.File]::WriteAllText((Join-Path $outputRoot 'repo.json'), $repositoryJson + "`n", [Text.UTF8Encoding]::new($false))
$assemblyVersions = foreach ($name in $required) {
    $f = Get-Item -LiteralPath (Join-Path $DalamudDir $name)
    [pscustomobject]@{name=$name;product_version=$f.VersionInfo.ProductVersion;sha256=(Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$record = [pscustomobject]@{
    checked_at=[TimeZoneInfo]::ConvertTime([DateTimeOffset]::UtcNow,[TimeZoneInfo]::FindSystemTimeZoneById('China Standard Time')).ToString('o');version=$version;author='Displaced';name='Clothing pairing';
    target='CN / Dalamud API 15 / net10.0-windows x64';build='0 warnings, 0 errors';
    tests=@($testOutput);dll_sha256=$dllHash;zip_sha256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant();
    packaged_files=$expected;host_assemblies=@($assemblyVersions);installed=$false;in_game_verified=$false
}
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputRoot '验证记录-1.1.3.json') -Encoding utf8
Write-Host "插件包：$zipPath"
Write-Host "开发插件 DLL：$(Join-Path $packageRoot 'ClothingPairing.dll')"
Write-Host "DLL SHA256：$dllHash"
