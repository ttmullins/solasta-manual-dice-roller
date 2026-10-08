param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Tester,
    [Parameter(Mandatory = $true)][string]$Session,
    [Parameter(Mandatory = $true)][DateTimeOffset]$SessionStartedUtc,
    [Parameter(Mandatory = $true)][string[]]$PeerReports,
    [Parameter(Mandatory = $true)][string[]]$PeerNames,
    [Parameter(Mandatory = $true)][ValidateSet('host','client')][string[]]$PeerRoles,
    [Parameter(Mandatory = $true)][int[]]$PeerActors
)
$ErrorActionPreference = 'Stop'
# This creates a pending evidence checklist. It does not certify or operate a game.
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\','/')
$count = $PeerReports.Count
if ($count -lt 2 -or $PeerNames.Count -ne $count -or $PeerRoles.Count -ne $count -or $PeerActors.Count -ne $count) {
    throw 'Supply matching arrays for at least one actual host and one client.'
}
if (@($PeerRoles | Where-Object { $_ -eq 'host' }).Count -ne 1 -or
    @($PeerRoles | Where-Object { $_ -eq 'client' }).Count -lt 1 -or
    @($PeerActors | Select-Object -Unique).Count -ne $count -or
    @($PeerNames | Select-Object -Unique).Count -ne $count -or
    @($PeerActors | Where-Object { $_ -le 0 }).Count -gt 0) { throw 'Peer names/actors must be distinct, positive, and have one host.' }
$definition = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'multiplayer-acceptance-cases.json') -Raw | ConvertFrom-Json
$peers = @()
$identity = $null
for ($i = 0; $i -lt $count; $i++) {
    $path = [IO.Path]::GetFullPath((Join-Path $output $PeerReports[$i]))
    if (-not $path.StartsWith($output + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Peer report paths must remain within the coordinated report directory.'
    }
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $currentIdentity = [string]$report.plugin_sha256 + '/' + [string]$report.game_sha256 + '/' + [string]$report.mod_version
    if ($null -eq $identity) { $identity = $currentIdentity; $first = $report }
    if ($currentIdentity -ne $identity) { throw 'All peer reports must identify the exact same DLL/game/version.' }
    $peers += [ordered]@{ name = $PeerNames[$i]; role = $PeerRoles[$i]; actor = $PeerActors[$i];
        acceptance_report = $PeerReports[$i].Replace('\','/') }
}
$cases = @($definition.cases | ForEach-Object { [ordered]@{ id = $_.id; requirement = $_.requirement;
    peer_actors = @($PeerActors); status = 'pending'; observed = ''; evidence = @() } })
$rolls = @($definition.recorded_roll_cases | ForEach-Object {
    [ordered]@{ case_id = $_; action = ''; sequence = 0; signature = ''; owner = 0;
        expected_native_rng_draws = $null; expected_native_callbacks = $null; baseline_evidence = @();
        peers = @($PeerActors | ForEach-Object { [ordered]@{ actor = $_; faces = @(); native_result = '';
            native_rng_draws = $null; native_callbacks = $null } }) }
})
$acceptance = [ordered]@{ schema_version = 1; profile = $definition.profile; tester = $Tester;
    session_started_utc = $SessionStartedUtc.ToUniversalTime().ToString('o'); session = $Session; protocol = $definition.protocol;
    plugin_sha256 = $first.plugin_sha256; game_sha256 = $first.game_sha256; mod_version = $first.mod_version;
    peers = $peers; cases = $cases; recorded_rolls = $rolls }
$destination = Join-Path $output 'multiplayer-acceptance.json'
if (Test-Path -LiteralPath $destination) { throw 'A coordinated report already exists; preserve it or select a new test-session folder.' }
$acceptance | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $destination -Encoding UTF8
Write-Host ('Created pending multiplayer checklist at ' + $destination)
Write-Host 'Record actual observations and known-face/native-baseline comparisons. No cases were passed automatically.'
