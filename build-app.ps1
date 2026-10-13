param([string]$OutputName='CampusReconnect.exe')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$iconFile=Join-Path $PSScriptRoot 'campus.ico'
& (Join-Path $PSScriptRoot 'build-icon.ps1')
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CampusApp.cs') -Raw -Encoding UTF8
$output=Join-Path $PSScriptRoot $OutputName
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output }
$compiler=New-Object System.CodeDom.Compiler.CompilerParameters
$compiler.CompilerOptions='/target:winexe /optimize+ /win32manifest:"'+(Join-Path $PSScriptRoot 'app.manifest')+'" /win32icon:"'+$iconFile+'"'
$compiler.GenerateExecutable=$true
$compiler.OutputAssembly=$output
foreach ($assembly in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Security.dll')) { [void]$compiler.ReferencedAssemblies.Add($assembly) }
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider
$result=$provider.CompileAssemblyFromSource($compiler,$source)
if ($result.Errors.HasErrors) { $result.Errors | ForEach-Object { Write-Output $_.ToString() }; throw 'Compilation failed' }
$provider.Dispose()
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination ($output+'.config')
Write-Output 'Built native tray app.'
