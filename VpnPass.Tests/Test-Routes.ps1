# Exercises the production route reconciler with mocked Windows cmdlets.
# No real network routes are read or changed by this test.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\VpnPass\Routes.ps1" -LibraryOnly
$script:count = 0
function Reset-Test {
    $script:table = @{}
    $script:created = 0; $script:removed = 0; $script:renewed = 0
    $script:failRenew = $false; $script:failRemove = $false; $script:failAdd = $false
    $script:available = $true
    $script:homeIndex = 17
}
function Get-HomeAdapters {
    if ($script:available) {
        [pscustomobject]@{ Id = 'home'; Name = 'Ethernet'; Index = $script:homeIndex; Gateway4 = '192.168.1.254'; Gateway6 = 'fe80::1'; Score = 1 }
    }
}
function Get-NetRoute { param($PolicyStore) @($script:table.Values) }
function New-NetRoute {
    param($DestinationPrefix, $InterfaceIndex, $NextHop, $RouteMetric, $PolicyStore, $ValidLifetime, $PreferredLifetime, $ErrorAction)
    if ($script:failAdd) { throw 'Simulated add failure' }
    if ($PolicyStore -ne 'ActiveStore' -or $ValidLifetime.TotalMinutes -ne 5 -or $PreferredLifetime.TotalMinutes -ne 5) { throw 'Routes must be temporary with a 5-minute expiry' }
    $route = [pscustomobject]@{ DestinationPrefix = $DestinationPrefix; InterfaceIndex = $InterfaceIndex; NextHop = $NextHop; RouteMetric = $RouteMetric; Protocol = 'NetMgmt' }
    $script:table[(Get-RouteKey $route)] = $route
    $script:created++
    $route
}
function Remove-NetRoute {
    param([Parameter(ValueFromPipeline)]$InputObject, $Confirm)
    process {
        if ($script:failRemove) { throw 'Simulated remove failure' }
        $script:table.Remove((Get-RouteKey $InputObject)); $script:removed++
    }
}
function Set-NetRoute {
    param([Parameter(ValueFromPipeline)]$InputObject, $ValidLifetime, $PreferredLifetime)
    process {
        if ($script:failRenew) { throw 'Simulated renewal failure' }
        if ($ValidLifetime.TotalMinutes -ne 5) { throw 'Wrong renewal lifetime' }
        $script:renewed++
    }
}
function Assert-Equal($Expected, $Actual) { if ($Expected -ne $Actual) { throw "Expected $Expected, got $Actual" } }
function Test-Case([string]$Name, [scriptblock]$Body) {
    Reset-Test
    & $Body
    $script:count++
    Write-Output "PASS $Name"
}
function Request($Addresses, $Owned = @(), $AdapterId = '') {
    [pscustomobject]@{ Addresses = @($Addresses); Owned = @($Owned); AdapterId = $AdapterId }
}

Test-Case 'Add IPv4 and IPv6, then off removes both' {
    $result = Sync-HomeRoutes (Request @('8.8.8.8', '2606:4700:4408::ac40:9bd1'))
    Assert-Equal 2 $result.Owned.Count
    Assert-Equal 2 $script:created
    $off = Sync-HomeRoutes (Request @() $result.Owned)
    Assert-Equal 0 $off.Owned.Count
    Assert-Equal 0 $script:table.Count
}
Test-Case 'Refresh renews routes without duplicates' {
    $result = Sync-HomeRoutes (Request @('8.8.8.8'))
    $again = Sync-HomeRoutes (Request @('8.8.8.8') $result.Owned)
    Assert-Equal 1 $script:created
    Assert-Equal 1 $script:renewed
    Assert-Equal 1 $again.Owned.Count
}
Test-Case 'Shared address has one route and survives while still requested' {
    $result = Sync-HomeRoutes (Request @('8.8.8.8', '8.8.8.8'))
    Assert-Equal 1 $script:created
    $again = Sync-HomeRoutes (Request @('8.8.8.8') $result.Owned)
    Assert-Equal 0 $script:removed
    Assert-Equal 1 $again.Owned.Count
}
Test-Case 'Pre-existing host route is never adopted or deleted' {
    $foreign = [pscustomobject]@{ DestinationPrefix = '8.8.8.8/32'; InterfaceIndex = 17; NextHop = '192.168.1.254'; RouteMetric = 37; Protocol = 'NetMgmt' }
    $script:table[(Get-RouteKey $foreign)] = $foreign
    $result = Sync-HomeRoutes (Request @('8.8.8.8'))
    Assert-Equal 0 $result.Owned.Count
    Assert-Equal 1 $result.Warnings.Count
    $null = Sync-HomeRoutes (Request @() $result.Owned)
    Assert-Equal 1 $script:table.Count
}
Test-Case 'DNS address replacement removes old and adds new' {
    $result = Sync-HomeRoutes (Request @('8.8.8.8'))
    $next = Sync-HomeRoutes (Request @('1.1.1.1') $result.Owned)
    Assert-Equal 1 $script:removed
    Assert-Equal '1.1.1.1/32' $next.Owned[0].DestinationPrefix
}
Test-Case 'Network change migrates owned routes' {
    $result = Sync-HomeRoutes (Request @('8.8.8.8'))
    $script:homeIndex = 22
    $next = Sync-HomeRoutes (Request @('8.8.8.8') $result.Owned)
    Assert-Equal 1 $script:removed
    Assert-Equal 22 $next.Owned[0].InterfaceIndex
}
Test-Case 'Unavailable chosen adapter removes stale routes' {
    $result = Sync-HomeRoutes (Request @('8.8.8.8'))
    $next = Sync-HomeRoutes (Request @('8.8.8.8') $result.Owned 'missing')
    Assert-Equal 0 $next.Owned.Count
    Assert-Equal 0 $script:table.Count
    Assert-Equal 1 $next.Warnings.Count
}
Test-Case 'Failed removal retains ownership for cleanup retry' {
    $result = Sync-HomeRoutes (Request @('8.8.8.8'))
    $script:failRemove = $true
    $off = Sync-HomeRoutes (Request @() $result.Owned)
    Assert-Equal 1 $off.Owned.Count
    $script:failRemove = $false
    $retry = Sync-HomeRoutes (Request @() $off.Owned)
    Assert-Equal 0 $retry.Owned.Count
}
Test-Case 'Failed renewal keeps cleanup ownership' {
    $result = Sync-HomeRoutes (Request @('8.8.8.8'))
    $script:failRenew = $true
    $next = Sync-HomeRoutes (Request @('8.8.8.8') $result.Owned)
    Assert-Equal 1 $next.Owned.Count
    Assert-Equal 1 $next.Warnings.Count
}
Test-Case 'Externally changed route is not renewed or removed' {
    $result = Sync-HomeRoutes (Request @('8.8.8.8'))
    $script:table.Values | ForEach-Object { $_.RouteMetric = 100 }
    $off = Sync-HomeRoutes (Request @() $result.Owned)
    Assert-Equal 0 $off.Owned.Count
    Assert-Equal 1 $script:table.Count
    Assert-Equal 0 $script:removed
}
Test-Case 'Partial add failure reports warning and no false ownership' {
    $script:failAdd = $true
    $result = Sync-HomeRoutes (Request @('8.8.8.8'))
    Assert-Equal 0 $result.Owned.Count
    Assert-Equal 1 $result.Warnings.Count
}
Write-Output "PASS: $script:count route lifecycle checks. No real routes changed."
