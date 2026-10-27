param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot 'outputs\iPhone-PC' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$sourceFiles = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object FullName
$extra = @()
if (Test-Path -LiteralPath (Join-Path $taskRoot 'src\SelfTests.cs')) { $extra += '/define:SELFTEST' }
if (Test-Path -LiteralPath (Join-Path $taskRoot 'src\app.ico')) { $extra += '/win32icon:' + (Join-Path $taskRoot 'src\app.ico') }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /langversion:5 ('/out:' + (Join-Path $output 'iPhone-PC.exe')) ('/win32manifest:' + (Join-Path $taskRoot 'src\app.manifest')) ('/resource:' + (Join-Path $taskRoot 'src\MainWindow.xaml') + ',MainWindow.xaml') @extra @references @sourceFiles
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
Write-Output (Join-Path $output 'iPhone-PC.exe')
