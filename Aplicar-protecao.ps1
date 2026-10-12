$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'src\Firewall.ps1'
$reportPath = Join-Path $PSScriptRoot 'data\firewall-state.json'
$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $scriptPath + '"'), '-Mode', 'Apply', '-ReportPath', ('"' + $reportPath + '"'))
$adminProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
$adminProcess.WaitForExit()
if ($adminProcess.ExitCode -ne 0) { throw 'Nao foi possivel aplicar as regras. Confira data/firewall-state.json.' }
Write-Output 'Protecao aplicada e verificada para esta pasta do Brisa.'
