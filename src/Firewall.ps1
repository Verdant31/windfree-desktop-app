[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Apply', 'Verify', 'Remove')]
    [string]$Mode = 'Verify',
    [string]$AppRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function ConvertTo-IPv4Number([string]$Address) {
    $bytes = [Net.IPAddress]::Parse($Address).GetAddressBytes()
    if ($bytes.Length -ne 4) { throw 'Only IPv4 appliance addresses are supported.' }
    return [uint64]$bytes[0] * 16777216 + [uint64]$bytes[1] * 65536 + [uint64]$bytes[2] * 256 + [uint64]$bytes[3]
}

function ConvertFrom-IPv4Number([uint64]$Number) {
    return '{0}.{1}.{2}.{3}' -f (($Number -shr 24) -band 255), (($Number -shr 16) -band 255), (($Number -shr 8) -band 255), ($Number -band 255)
}

function Get-BlockedIPv4Ranges([string[]]$Addresses) {
    $exceptions = @([pscustomobject]@{Start = [uint64]2130706432; End = [uint64]2147483647})
    foreach ($address in $Addresses) {
        $number = ConvertTo-IPv4Number $address
        $exceptions += [pscustomobject]@{Start = $number; End = $number}
    }
    $cursor = [uint64]0
    foreach ($exception in @($exceptions | Sort-Object Start)) {
        if ($exception.Start -gt $cursor) {
            '{0}-{1}' -f (ConvertFrom-IPv4Number $cursor), (ConvertFrom-IPv4Number ($exception.Start - 1))
        }
        if ($exception.End -ge $cursor) { $cursor = $exception.End + 1 }
    }
    if ($cursor -le 4294967295) {
        '{0}-{1}' -f (ConvertFrom-IPv4Number $cursor), '255.255.255.255'
    }
}

function Normalize-Address([string]$Address) {
    if ($Address.Contains('/')) {
        $parts = $Address.Split('/')
        return ([Net.IPAddress]::Parse($parts[0]).ToString() + '/' + $parts[1])
    }
    return $Address.ToLowerInvariant()
}

function Test-AddressSet($Actual, $Expected) {
    $left = @($Actual | ForEach-Object { Normalize-Address ([string]$_) } | Sort-Object -Unique)
    $right = @($Expected | ForEach-Object { Normalize-Address ([string]$_) } | Sort-Object -Unique)
    return (($left -join ',') -eq ($right -join ','))
}

function Get-RuleSnapshot($Rule) {
    $application = $Rule | Get-NetFirewallApplicationFilter
    $address = $Rule | Get-NetFirewallAddressFilter
    $port = $Rule | Get-NetFirewallPortFilter
    return @{
        Name = [string]$Rule.Name; DisplayName = [string]$Rule.DisplayName
        Description = [string]$Rule.Description; Group = [string]$Rule.Group
        Enabled = $Rule.Enabled.ToString(); Direction = $Rule.Direction.ToString()
        Action = $Rule.Action.ToString(); Profile = $Rule.Profile.ToString()
        Program = [string]$application.Program; Protocol = [string]$port.Protocol
        RemoteAddress = @($address.RemoteAddress)
    }
}

function Set-OwnedRule($Specification) {
    $parameters = @{}
    foreach ($key in $Specification.Keys) { $parameters[$key] = $Specification[$key] }
    $parameters.NewDisplayName = $parameters.DisplayName
    $parameters.Remove('DisplayName')
    $parameters.Remove('Group')
    Set-NetFirewallRule @parameters -PolicyStore PersistentStore | Out-Null
}

function Assert-Policy($Specifications, [string]$Group) {
    $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore)
    if (@($profiles | Where-Object { $_.Enabled.ToString() -ne 'True' }).Count -gt 0) {
        throw 'A firewall profile is disabled. No complete protection can be confirmed.'
    }
    if ((Get-Service -Name MpsSvc).Status.ToString() -ne 'Running') {
        throw 'Windows Firewall service is not running.'
    }
    $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Group $Group -ErrorAction SilentlyContinue)
    if ($rules.Count -ne $Specifications.Count) { throw 'The effective policy has an unexpected rule count.' }
    foreach ($specification in $Specifications) {
        $rule = @($rules | Where-Object { $_.Name -eq $specification.Name })
        if ($rule.Count -ne 1) { throw ('Missing effective rule: ' + $specification.Name) }
        $actual = Get-RuleSnapshot $rule[0]
        $protocol = if ($specification.Protocol -eq 'UDP') { @('UDP', '17') } else { @('Any', '256') }
        if ($actual.Enabled -ne 'True' -or $actual.Direction -ne 'Outbound' -or
            $actual.Action -ne $specification.Action -or $actual.Profile -ne 'Any' -or
            $actual.Program -ne $specification.Program -or $actual.Protocol -notin $protocol -or
            !(Test-AddressSet $actual.RemoteAddress $specification.RemoteAddress)) {
            throw ('Effective rule does not match the requested restrictions: ' + $specification.Name)
        }
    }
    return @($profiles | ForEach-Object {
        [pscustomobject]@{name = $_.Name; enabled = $_.Enabled.ToString(); localRules = $_.AllowLocalFirewallRules.ToString()}
    })
}

try {
    $AppRoot = (Resolve-Path -LiteralPath $AppRoot).Path.TrimEnd('\')
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $rootHash = ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($AppRoot.ToLowerInvariant())))).Replace('-', '').Substring(0, 12)
    } finally { $hasher.Dispose() }
    $group = 'Brisa.LocalOnly.' + $rootHash
    $catalog = Get-Content -LiteralPath (Join-Path $AppRoot 'data\devices.json') -Raw | ConvertFrom-Json
    $addresses = @($catalog.devices | ForEach-Object {
        $number = ConvertTo-IPv4Number ([string]$_.ip)
        $private = ($number -ge 167772160 -and $number -le 184549375) -or
            ($number -ge 2886729728 -and $number -le 2887778303) -or
            ($number -ge 3232235520 -and $number -le 3232301055)
        if (!$private) { throw 'An appliance address is outside the private IPv4 ranges.' }
        [Net.IPAddress]::Parse([string]$_.ip).ToString()
    } | Sort-Object -Unique)
    if ($addresses.Count -lt 1 -or $addresses.Count -gt 16) { throw 'Unexpected appliance address count.' }
    $blockedIPv4 = @(Get-BlockedIPv4Ranges $addresses)
    $blockedIPv6 = @('::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')
    $programs = @(
        [pscustomobject]@{Tag = 'App'; Path = (Join-Path $AppRoot 'Brisa.exe')},
        [pscustomobject]@{Tag = 'Python'; Path = (Join-Path $AppRoot 'runtime\python.exe')},
        [pscustomobject]@{Tag = 'PythonW'; Path = (Join-Path $AppRoot 'runtime\pythonw.exe')}
    )
    $specifications = @()
    foreach ($program in $programs) {
        if (!(Test-Path -LiteralPath $program.Path -PathType Leaf)) { throw ('Missing executable: ' + $program.Path) }
        foreach ($kind in @('DenyOtherIPv4', 'DenyIPv6', 'AllowAppliances')) {
            $specifications += @{
                Name = $group + '.' + $program.Tag + '.' + $kind
                DisplayName = 'Brisa - ' + $program.Tag + ' - ' + $kind
                Description = 'Brisa local-only policy; private appliance IPs from data/devices.json; loopback stays available.'
                Group = $group; Enabled = 'True'; Direction = 'Outbound'; Profile = 'Any'
                Program = $program.Path
                Action = $(if ($kind -eq 'AllowAppliances') { 'Allow' } else { 'Block' })
                Protocol = $(if ($kind -eq 'AllowAppliances') { 'UDP' } else { 'Any' })
                RemoteAddress = $(if ($kind -eq 'DenyOtherIPv4') { $blockedIPv4 } elseif ($kind -eq 'DenyIPv6') { $blockedIPv6 } else { $addresses })
            }
        }
    }
    $result = [ordered]@{
        mode = $Mode; success = $false; appRoot = $AppRoot; group = $group
        allowedApplianceIPv4 = $addresses; blockedIPv4Ranges = $blockedIPv4
        blockedIPv6Ranges = $blockedIPv6
        externalIPv6 = 'blocked'; loopbackIPv4 = 'available for internal communication'
        programs = @($programs.Path); ruleCount = $specifications.Count
        timestampUtc = [DateTime]::UtcNow.ToString('o')
    }
    if ($Mode -eq 'Plan') {
        $result.success = $true
    } elseif ($Mode -eq 'Verify') {
        $result.profiles = @(Assert-Policy $specifications $group)
        $result.success = $true
    } else {
        $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Administrator confirmation is required to change Windows Firewall rules.'
        }
        Import-Module NetSecurity
        if ($Mode -eq 'Remove') {
            $ownedRules = @(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -ErrorAction SilentlyContinue)
            foreach ($ownedRule in $ownedRules) {
                if (!$ownedRule.Name.StartsWith($group + '.')) { throw 'Unexpected rule in the Brisa group; nothing removed.' }
            }
            $ownedRules | Remove-NetFirewallRule
            $result.removedRuleCount = $ownedRules.Count
            $result.success = $true
        } else {
            $profilesBefore = @(Get-NetFirewallProfile -PolicyStore ActiveStore)
            if (@($profilesBefore | Where-Object { $_.Enabled.ToString() -ne 'True' }).Count -gt 0) {
                throw 'A firewall profile is disabled. No profiles or rules were changed.'
            }
            $previous = @()
            foreach ($specification in $specifications) {
                $old = @(Get-NetFirewallRule -PolicyStore PersistentStore -Name $specification.Name -ErrorAction SilentlyContinue)
                if ($old.Count -gt 0) {
                    if ($old[0].Group -ne $group) { throw 'Rule-name collision with a different group.' }
                    $previous += Get-RuleSnapshot $old[0]
                }
            }
            if ($ReportPath) {
                @{group = $group; previousRules = $previous} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ($ReportPath + '.before.json') -Encoding UTF8
            }
            $created = @()
            try {
                foreach ($specification in $specifications) {
                    if (@($previous | Where-Object { $_.Name -eq $specification.Name }).Count -gt 0) {
                        Set-OwnedRule $specification
                    } else {
                        New-NetFirewallRule @specification -PolicyStore PersistentStore | Out-Null
                        $created += $specification.Name
                    }
                }
                $result.profiles = @(Assert-Policy $specifications $group)
                $result.success = $true
            } catch {
                $applyError = $_
                foreach ($createdName in $created) {
                    Remove-NetFirewallRule -PolicyStore PersistentStore -Name $createdName -ErrorAction SilentlyContinue
                }
                foreach ($previousRule in $previous) {
                    Set-OwnedRule $previousRule
                }
                throw $applyError
            }
        }
    }
    $json = $result | ConvertTo-Json -Depth 8
    if ($ReportPath) { $json | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
    Write-Output $json
} catch {
    $failure = @{mode = $Mode; success = $false; error = $_.Exception.Message; timestampUtc = [DateTime]::UtcNow.ToString('o')} | ConvertTo-Json
    if ($ReportPath) { $failure | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
    Write-Output $failure
    exit 1
}
