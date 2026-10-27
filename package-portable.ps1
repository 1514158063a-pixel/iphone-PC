[CmdletBinding()]
param(
    [string]$ReceiverSource
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReceiverSource)) {
    $workspaceEngine = Join-Path $root 'outputs\iPhone-PC\engine'
    $ReceiverSource = if (Test-Path -LiteralPath (Join-Path $workspaceEngine 'uxplay-windows.exe')) {
        $workspaceEngine
    } else {
        Join-Path $env:LOCALAPPDATA 'Programs\uxplay-windows'
    }
}
$receiverSource = [IO.Path]::GetFullPath($ReceiverSource)
$stageParent = Join-Path $root 'work\portable-package'
$stage = Join-Path $stageParent 'iPhone-PC 便携版'
$compiled = Join-Path $root 'work\portable-build'
$release = Join-Path $root 'release'
$zip = Join-Path $release 'iPhone-PC-便携版-x64.zip'

function Assert-WorkspaceOutput([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside this workspace: $absolute"
    }
}
foreach ($outputPath in @($compiled, $stageParent, $stage, $release, $zip)) { Assert-WorkspaceOutput $outputPath }

if (-not (Test-Path -LiteralPath (Join-Path $receiverSource 'uxplay-windows.exe'))) {
    throw "找不到完整接收核心：$receiverSource"
}

if (Test-Path -LiteralPath $compiled) {
    Remove-Item -LiteralPath $compiled -Recurse -Force
}
& (Join-Path $root 'build.ps1') -OutputDirectory $compiled
if ($LASTEXITCODE -ne 0) { throw '应用编译失败。' }

if (Test-Path -LiteralPath $stageParent) {
    Remove-Item -LiteralPath $stageParent -Recurse -Force
}
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -LiteralPath (Join-Path $compiled 'iPhone-PC.exe') -Destination (Join-Path $stage 'iPhone-PC.exe')
Copy-Item -LiteralPath (Join-Path $root 'packaging\启动 iPhone 投屏.cmd') -Destination (Join-Path $stage '启动 iPhone 投屏.cmd')
Copy-Item -LiteralPath (Join-Path $root 'packaging\使用说明.txt') -Destination (Join-Path $stage '使用说明.txt')
Copy-Item -LiteralPath (Join-Path $root 'packaging\第三方组件与源代码说明.txt') -Destination (Join-Path $stage '第三方组件与源代码说明.txt')
Copy-Item -LiteralPath $receiverSource -Destination (Join-Path $stage 'engine') -Recurse -Force

# Do not carry this PC's receiver logs or mutable local state into the release.
Get-ChildItem -LiteralPath (Join-Path $stage 'engine') -Recurse -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @('logs', 'cache') } |
    ForEach-Object { Assert-WorkspaceOutput $_.FullName; Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
# These upstream convenience launchers are not part of the runtime manifest;
# the workbench starts the receiver itself and the release intentionally ships
# no PowerShell source/scripts.
foreach ($optionalScript in @('Start-iPhoneMirror.ps1', 'Restart-iPhoneMirror.ps1')) {
    $optionalPath = Join-Path (Join-Path $stage 'engine') $optionalScript
    if (Test-Path -LiteralPath $optionalPath) { Remove-Item -LiteralPath $optionalPath -Force }
}

$forbidden = Get-ChildItem -LiteralPath $stage -Recurse -File |
    Where-Object { $_.Extension -in @('.cs', '.xaml', '.ps1', '.pdb', '.sln', '.csproj') }
if ($forbidden) {
    throw ('发行包中发现不应包含的源码或构建文件：' + (($forbidden | ForEach-Object FullName) -join '; '))
}

$manifestPath = Join-Path $stage 'engine\resources\bundle-files.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($file in $manifest) {
    $runtimeFile = Join-Path (Join-Path $stage 'engine') $file.path
    if (-not (Test-Path -LiteralPath $runtimeFile)) { throw "接收核心缺失文件：$($file.path)" }
    $hash = (Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $file.sha256.ToLowerInvariant()) { throw "接收核心校验不匹配：$($file.path)" }
}

New-Item -ItemType Directory -Path $release -Force | Out-Null
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stageParent, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = @($archive.Entries | ForEach-Object FullName)
    foreach ($required in @('iPhone-PC 便携版/iPhone-PC.exe', 'iPhone-PC 便携版/启动 iPhone 投屏.cmd', 'iPhone-PC 便携版/engine/uxplay-windows.exe', 'iPhone-PC 便携版/engine/LICENSE.rtf')) {
        if ($entries -notcontains $required) { throw "压缩包缺少：$required" }
    }
    if ($entries | Where-Object { $_ -match '\.(cs|xaml|ps1|pdb|sln|csproj)$' }) { throw '压缩包中发现源码或构建文件。' }
} finally {
    $archive.Dispose()
}

$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
[PSCustomObject]@{
    Archive = $zip
    Bytes = (Get-Item -LiteralPath $zip).Length
    SHA256 = $zipHash
    RuntimeFilesVerified = @($manifest).Count
} | Format-List
