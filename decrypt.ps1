param([Parameter(Mandatory=$true)][string]$ConfigFile)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
# Node can inherit PowerShell 7's module path. Load the Windows PowerShell
# security module explicitly rather than relying on automatic module discovery.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
$stage='read'
try {
    $settings = Get-Content -LiteralPath $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $stage='decrypt'
    $secure = ConvertTo-SecureString $settings.passwordCipher
    $stage='serialize'
    $credential = New-Object System.Management.Automation.PSCredential('campus', $secure)
    @{password=$credential.GetNetworkCredential().Password} | ConvertTo-Json -Compress
} catch {
    [Console]::Error.WriteLine('Cannot decrypt local configuration ('+$stage+': '+$_.Exception.GetType().Name+').')
    exit 1
}
