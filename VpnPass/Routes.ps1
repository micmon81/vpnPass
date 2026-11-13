param([string]$RequestBase64, [switch]$LibraryOnly)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Get-RouteKey($Route) {
    return "$($Route.DestinationPrefix)|$($Route.InterfaceIndex)|$($Route.NextHop)"
}

function Get-HomeAdapters {
    $defaults = @(Get-NetRoute -PolicyStore ActiveStore | Where-Object {
        $_.DestinationPrefix -in @('0.0.0.0/0', '::/0') -and $_.NextHop -notin @('0.0.0.0', '::')
    })
    $interfaces = @(Get-NetIPInterface)
    foreach ($adapter in @(Get-NetAdapter -Physical | Where-Object Status -eq 'Up')) {
        $v4 = $defaults | Where-Object { $_.InterfaceIndex -eq $adapter.ifIndex -and $_.DestinationPrefix -eq '0.0.0.0/0' } |
            Sort-Object RouteMetric | Select-Object -First 1
        $v6 = $defaults | Where-Object { $_.InterfaceIndex -eq $adapter.ifIndex -and $_.DestinationPrefix -eq '::/0' } |
            Sort-Object RouteMetric | Select-Object -First 1
        if ($null -eq $v4 -and $null -eq $v6) { continue }
        $ipInterface = $interfaces | Where-Object { $_.InterfaceIndex -eq $adapter.ifIndex -and $_.AddressFamily -eq 'IPv4' } | Select-Object -First 1
        $score = 100000
        if ($null -ne $v4) { $score = [int]$v4.RouteMetric + [int]$ipInterface.InterfaceMetric }
        [pscustomobject]@{
            Id = $adapter.InterfaceGuid.ToString(); Name = $adapter.Name; Index = [int]$adapter.ifIndex
            Gateway4 = $(if ($null -ne $v4) { $v4.NextHop } else { '' })
            Gateway6 = $(if ($null -ne $v6) { $v6.NextHop } else { '' })
            Score = $score
        }
    }
}

function Sync-HomeRoutes($Request) {
    $warnings = [Collections.Generic.List[string]]::new()
    $owned = [Collections.Generic.List[object]]::new()
    $adapters = @(Get-HomeAdapters | Sort-Object Score, Name)
    $adapter = if ([string]::IsNullOrWhiteSpace($Request.AdapterId)) {
        $adapters | Select-Object -First 1
    } else { $adapters | Where-Object Id -eq $Request.AdapterId | Select-Object -First 1 }
    $desired = @{}
    $lifetime = [TimeSpan]::FromMinutes(5)
    if (@($Request.Addresses).Count -gt 256) { throw 'Too many addresses. Maximum is 256.' }
    if (@($Request.Addresses).Count -gt 0 -and $null -eq $adapter) { $warnings.Add('The selected home network is unavailable. Routes are off until it reconnects.') }
    foreach ($address in @($Request.Addresses)) {
        $parsed = [Net.IPAddress]::Parse($address)
        $isV4 = $parsed.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork
        $prefix = $parsed.ToString() + $(if ($isV4) { '/32' } else { '/128' })
        if ($null -eq $adapter) { continue }
        $gateway = if ($isV4) { $adapter.Gateway4 } else { $adapter.Gateway6 }
        if ([string]::IsNullOrWhiteSpace($gateway)) {
            $warnings.Add("No home gateway for $prefix. That address remains on its existing route.")
            continue
        }
        $route = [pscustomobject]@{ DestinationPrefix = $prefix; InterfaceIndex = $adapter.Index; NextHop = $gateway; RouteMetric = 37 }
        $desired[(Get-RouteKey $route)] = $route
    }

    $current = @(Get-NetRoute -PolicyStore ActiveStore)
    $byKey = @{}
    foreach ($route in $current) { $byKey[(Get-RouteKey $route)] = $route }
    $previous = @{}
    foreach ($route in @($Request.Owned)) { $previous[(Get-RouteKey $route)] = $route }

    # Remove only routes recorded by this instance and still carrying our metric.
    foreach ($key in @($previous.Keys)) {
        if (-not $byKey.ContainsKey($key)) { continue }
        $route = $byKey[$key]
        if ([int]$route.RouteMetric -ne 37 -or [string]$route.Protocol -ne 'NetMgmt') {
            $warnings.Add("$($route.DestinationPrefix) changed outside vpnPass; left untouched.")
            continue
        }
        if (-not $desired.ContainsKey($key)) {
            try {
                $route | Remove-NetRoute -Confirm:$false -ErrorAction Stop
                $byKey.Remove($key)
            }
            catch {
                $owned.Add($previous[$key])
                $warnings.Add("Could not remove $($route.DestinationPrefix): $($_.Exception.Message). It will expire without renewal.")
            }
        }
    }

    foreach ($key in @($desired.Keys)) {
        $target = $desired[$key]
        try {
            if ($byKey.ContainsKey($key) -and $previous.ContainsKey($key) -and
                [int]$byKey[$key].RouteMetric -eq 37 -and [string]$byKey[$key].Protocol -eq 'NetMgmt') {
                $byKey[$key] | Set-NetRoute -ValidLifetime $lifetime -PreferredLifetime $lifetime -ErrorAction Stop | Out-Null
                $owned.Add($target)
            }
            elseif (@($byKey.Values | Where-Object DestinationPrefix -eq $target.DestinationPrefix).Count -gt 0) {
                $warnings.Add("$($target.DestinationPrefix): an existing host route was left untouched. Retry after any earlier test expires.")
            }
            else {
                $added = New-NetRoute -DestinationPrefix $target.DestinationPrefix -InterfaceIndex $target.InterfaceIndex `
                    -NextHop $target.NextHop -RouteMetric 37 -PolicyStore ActiveStore `
                    -ValidLifetime $lifetime -PreferredLifetime $lifetime -ErrorAction Stop
                $owned.Add($target)
                $byKey[$key] = $added
            }
        }
        catch {
            # Retain ownership after failed renewal so turning off can still clean up.
            if ($previous.ContainsKey($key) -and $byKey.ContainsKey($key)) { $owned.Add($previous[$key]) }
            $warnings.Add("$($target.DestinationPrefix): $($_.Exception.Message)")
        }
    }
    return [pscustomobject]@{
        Owned = @($owned.ToArray()); Warnings = @($warnings.ToArray()); Adapters = @($adapters)
        NetworkName = $(if ($null -ne $adapter) { $adapter.Name } else { '' })
        Error = ''
    }
}

if ($LibraryOnly) { return }
try {
    $request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RequestBase64)) | ConvertFrom-Json
    Sync-HomeRoutes $request | ConvertTo-Json -Depth 8 -Compress
}
catch {
    [pscustomobject]@{ Error = $_.Exception.Message } | ConvertTo-Json -Compress
    exit 1
}
