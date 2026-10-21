param([ValidateSet('inventory','route')][string]$Mode='inventory', [string]$Guid='', [string]$LocalAddress='', [string]$RemoteAddress='')
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
if($Mode -eq 'route') {
    $parsedGuid=[guid]::Parse($Guid)
    $local=[Net.IPAddress]::Parse($LocalAddress)
    $remote=[Net.IPAddress]::Parse($RemoteAddress)
    $adapter=@(Get-NetAdapter -Physical | Where-Object {[guid]$_.InterfaceGuid -eq $parsedGuid -and $_.Status -eq 'Up'})
    if($adapter.Count -ne 1) { throw 'Selected adapter unavailable' }
    $index=$adapter[0].ifIndex
    $interface=Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4
    if($interface.WeakHostSend -eq 'Enabled') { throw 'Weak host routing is not supported' }
    $address=@(Get-NetIPAddress -InterfaceIndex $index -AddressFamily IPv4 | Where-Object {$_.IPAddress -eq $local.ToString() -and $_.AddressState -eq 'Preferred'})
    if($address.Count -ne 1) { throw 'Selected address unavailable' }
    $routes=@(Find-NetRoute -RemoteIPAddress $remote.ToString() -LocalIPAddress $local.ToString() -InterfaceIndex $index)
    $route=@($routes | Where-Object {$_.DestinationPrefix -and $_.InterfaceIndex -eq $index})
    $gateway=@(Get-NetRoute -InterfaceIndex $index -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty NextHop | Sort-Object -Unique)
    if($route.Count -ne 1) { throw 'Selected route unavailable' }
    @{index=$index;gateway=($gateway -join ',');guid=$adapter[0].InterfaceGuid.ToString();address=$local.ToString()} | ConvertTo-Json -Compress
    exit
}
$result=@(foreach($adapter in @(Get-NetAdapter -Physical)) {
    $kind=if([int]$adapter.NdisPhysicalMedium -eq 9 -or [int]$adapter.NdisMedium -eq 16) {'wireless'} elseif([int]$adapter.NdisMedium -eq 0 -and [int]$adapter.NdisPhysicalMedium -eq 14) {'wired'} else {'unknown'}
    $addresses=@(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {$_.AddressState -eq 'Preferred' -and $_.IPAddress -notlike '169.254.*'} | Select-Object -ExpandProperty IPAddress)
    $gateway=@(Get-NetRoute -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty NextHop | Sort-Object -Unique)
    $dns=@(Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object {$_.ServerAddresses})
    @{guid=$adapter.InterfaceGuid.ToString();index=$adapter.ifIndex;kind=$kind;up=($adapter.Status -eq 'Up');addresses=$addresses;gateway=($gateway -join ',');dns=$dns}
})
ConvertTo-Json -InputObject $result -Depth 4 -Compress
