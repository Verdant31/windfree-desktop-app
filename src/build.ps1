$ErrorActionPreference = 'Stop'
$brisaRoot = Split-Path -Parent $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$frameworkPath = Split-Path -Parent $compilerPath
$wpfPath = Join-Path $frameworkPath 'WPF'
$references = @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $wpfPath $_) }
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ (('/out:' + (Join-Path $brisaRoot 'Brisa.exe'))) (('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest'))) /reference:System.dll /reference:System.Core.dll /reference:System.Xaml.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $references (Join-Path $PSScriptRoot 'Brisa.cs') (Join-Path $PSScriptRoot 'NativeTray.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o Brisa.' }
