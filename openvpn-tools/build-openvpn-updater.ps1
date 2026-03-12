$ErrorActionPreference = "Stop"

$sourcePath = Join-Path $PSScriptRoot "OpenVpnBypassUpdater.cs"
$outputPath = Join-Path $PSScriptRoot "OpenVpnBypassUpdater.exe"
$tempOutputPath = Join-Path $PSScriptRoot ("OpenVpnBypassUpdater." + [Guid]::NewGuid().ToString("N") + ".exe")
$source = Get-Content -LiteralPath $sourcePath -Raw

Add-Type `
    -TypeDefinition $source `
    -OutputAssembly $tempOutputPath `
    -OutputType ConsoleApplication `
    -Language CSharp `
    -ReferencedAssemblies @("System.dll", "System.Core.dll", "System.Management.dll")

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Force
}

Move-Item -LiteralPath $tempOutputPath -Destination $outputPath -Force

Write-Host "Built $outputPath"
