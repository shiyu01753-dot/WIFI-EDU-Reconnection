param(
    [string]$ProjectRoot=$PSScriptRoot,
    [Parameter(Mandatory=$true)][string]$NodePath,
    [Parameter(Mandatory=$true)][string]$NodeLicensePath,
    [string]$OutputDirectory,
    [string]$WorkDirectory
)
$ErrorActionPreference='Stop'
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $ProjectRoot 'dist' }
if (-not $WorkDirectory) { $WorkDirectory=Join-Path $OutputDirectory ('single-file-work-'+[Guid]::NewGuid().ToString('N')) }
[void](New-Item -ItemType Directory -Path $OutputDirectory -Force)
[void](New-Item -ItemType Directory -Path $WorkDirectory -Force)
$project=(Resolve-Path -LiteralPath $ProjectRoot).Path
$work=(Resolve-Path -LiteralPath $WorkDirectory).Path
if ($work -eq $project) { throw 'WorkDirectory must be separate from the source checkout' }
$snapshot=Join-Path $work 'source'
$payload=Join-Path $work 'payload'
[void](New-Item -ItemType Directory -Path $snapshot,$payload -Force)
foreach ($name in @('CampusApp.cs','build-app.ps1','build-icon.ps1','app.config','app.manifest','campus.ico')) {
    Copy-Item -LiteralPath (Join-Path $project $name) -Destination (Join-Path $snapshot $name)
}
Copy-Item -LiteralPath (Join-Path $project 'assets') -Destination $snapshot -Recurse
& (Join-Path $snapshot 'build-app.ps1')
foreach ($name in @('CampusReconnect.exe','CampusReconnect.exe.config','campus.ico')) {
    Copy-Item -LiteralPath (Join-Path $snapshot $name) -Destination (Join-Path $payload $name)
}
foreach ($name in @('reconnect.cjs','discovery.cjs','channel.cjs','network-channel.ps1','decrypt.ps1','LICENSE','THIRD_PARTY_NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $project $name) -Destination (Join-Path $payload $name)
}
[void](New-Item -ItemType Directory -Path (Join-Path $payload 'runtime'),(Join-Path $payload 'licenses') -Force)
Copy-Item -LiteralPath $NodePath -Destination (Join-Path $payload 'runtime\node.exe')
Copy-Item -LiteralPath $NodeLicensePath -Destination (Join-Path $payload 'licenses\Node.js-LICENSE.txt')
$files=Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName
$manifest=@($files | ForEach-Object {
    $relative=$_.FullName.Substring($payload.Length+1).Replace('\','/')
    ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())+'|'+$relative
})
$utf8=New-Object Text.UTF8Encoding($false)
$manifestPath=Join-Path $work 'files.sha256'
[IO.File]::WriteAllLines($manifestPath,[string[]]$manifest,$utf8)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath=Join-Path $work ('payload-'+[Guid]::NewGuid().ToString('N')+'.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($payload,$zipPath,[IO.Compression.CompressionLevel]::Optimal,$false)
$packageHash=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$version=([IO.File]::ReadAllText((Join-Path $project 'VERSION'))).Trim()
$launcherFile=Join-Path $project 'SingleFileLauncher.cs'
if (-not (Test-Path -LiteralPath $launcherFile)) { $launcherFile=Join-Path $PSScriptRoot 'SingleFileLauncher.cs' }
$source=([IO.File]::ReadAllText($launcherFile)).Replace('__PACKAGE_HASH__',$packageHash).Replace('__APP_VERSION__',$version)
$output=Join-Path $OutputDirectory ('CampusReconnect-'+$version+'-Single.exe')
if (Test-Path -LiteralPath $output) { throw 'Output already exists; choose a new OutputDirectory to preserve the existing release' }
$compiler=New-Object System.CodeDom.Compiler.CompilerParameters
$compiler.CompilerOptions='/target:winexe /platform:x64 /optimize+ /win32manifest:"'+(Join-Path $snapshot 'app.manifest')+'" /win32icon:"'+(Join-Path $snapshot 'campus.ico')+'"'
$compiler.GenerateExecutable=$true
$compiler.OutputAssembly=$output
foreach ($assembly in @('System.dll','System.Core.dll','System.Windows.Forms.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')) {
    [void]$compiler.ReferencedAssemblies.Add($assembly)
}
# CodeDom resource names are file names; keep a stable embedded payload.zip name.
$resourceZip=Join-Path $work 'payload.zip'
Copy-Item -LiteralPath $zipPath -Destination $resourceZip
[void]$compiler.EmbeddedResources.Add($resourceZip)
[void]$compiler.EmbeddedResources.Add($manifestPath)
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $result=$provider.CompileAssemblyFromSource($compiler,$source)
    if ($result.Errors.HasErrors) { $result.Errors | ForEach-Object { Write-Output $_.ToString() }; throw 'Single-file launcher compilation failed' }
} finally { $provider.Dispose() }
[PSCustomObject]@{Exe=$output;PayloadFiles=$files.Count;Size=(Get-Item -LiteralPath $output).Length;SHA256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()}
