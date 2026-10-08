param(
    [Parameter(Mandatory = $true)][string]$GamePath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$ModPath,
    [string[]]$LogPaths = @(),
    [string]$PeerName,
    [ValidateSet('host','client')][string]$PeerRole,
    [switch]$AllowClosedGame
)
$ErrorActionPreference = 'Stop'

# PowerShell 5.1-compatible, read-only game diagnostics. All writes go to the
# specified report directory outside the game. No assemblies are executed.
$game = [IO.Path]::GetFullPath($GamePath).TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
if ($output.Equals($game, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($game + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an output directory outside the game folder.'
}
if (-not $ModPath) { $ModPath = Join-Path $game 'Mods\ManualDiceRoller' }
$mod = [IO.Path]::GetFullPath($ModPath)
$managed = Join-Path $game 'Solasta_Data\Managed'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$logsOutput = Join-Path $output 'logs'
New-Item -ItemType Directory -Path $logsOutput -Force | Out-Null

function File-Record([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @{ path = $Path; exists = $false }
    }
    $item = Get-Item -LiteralPath $Path
    $value = @{ path = $item.FullName; exists = $true; bytes = $item.Length;
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant();
        last_write_utc = $item.LastWriteTimeUtc.ToString('o') }
    try {
        $assembly = [Reflection.AssemblyName]::GetAssemblyName($item.FullName)
        $value.assembly_identity = $assembly.FullName
        $value.assembly_version = $assembly.Version.ToString()
        $value.processor_architecture = $assembly.ProcessorArchitecture.ToString()
    } catch {
        $value.assembly_metadata_available = $false
    }
    return $value
}

$files = @{
    game = (File-Record (Join-Path $managed 'Assembly-CSharp.dll'))
    umm = (File-Record (Join-Path $managed 'UnityModManager\UnityModManager.dll'))
    harmony = (File-Record (Join-Path $managed 'UnityModManager\0Harmony.dll'))
    plugin = (File-Record (Join-Path $mod 'ManualDiceRoller.dll'))
    info = (File-Record (Join-Path $mod 'Info.json'))
    manifest = (File-Record (Join-Path $mod 'build-manifest.json'))
    unity_player = (File-Record (Join-Path $game 'UnityPlayer.dll'))
    photon_pun = (File-Record (Join-Path $managed 'PhotonUnityNetworking.dll'))
    photon_realtime = (File-Record (Join-Path $managed 'PhotonRealtime.dll'))
    photon_client = (File-Record (Join-Path $managed 'Photon3Unity3D.dll'))
}
$report = [ordered]@{
    schema_version = 1; collected_utc = [DateTime]::UtcNow.ToString('o');
    platform = 'windows'; qualification = 'diagnostics_only; manual acceptance outcomes remain pending';
    os_version = [Environment]::OSVersion.VersionString;
    powershell_version = $PSVersionTable.PSVersion.ToString();
    collector_process_is_64bit = [Environment]::Is64BitProcess;
    collector_runtime_version = [Environment]::Version.ToString();
    game_path = $game; mod_path = $mod; files = $files;
    peer_name = $PeerName; peer_role = $PeerRole;
    processes = @(); logs = @(); runtime_markers = @(); network_markers = @(); setup_errors = @()
}
$expected = @{
    game = 'a87e45cbd187e42e9d64a610850b39edba4896168b14d71ca71c5d32cf9e96ee'
    umm = '1387468bc3af41c50fe51859a3bb7af4922891aa8f13a6187e7a348ceaabfd88'
    harmony = 'df043f05c6e43602fa8e8f38f52a3a2cf962aab7a65a9d85573ea25f01a63e80'
    photon_pun = 'f1efa0cebf758bd79d43e202757d39b00e27384c65084ed14a9697af8bc3e32b'
    photon_realtime = '1cb825bb0f955fef65a29b2981797214d08fa2137b3738c4924b709b7d28a29d'
    photon_client = '5619e0aec897562522ce9adc87b5b80e3e5e9b1b137cbae866f8812e874e7fbe'
}
foreach ($key in $expected.Keys) {
    if (-not $files[$key].exists -or $files[$key].sha256 -ne $expected[$key]) {
        $report.setup_errors += ('Missing/different supported file: ' + $key)
    }
}
try {
    $info = Get-Content -LiteralPath $files.info.path -Raw | ConvertFrom-Json
    $manifest = Get-Content -LiteralPath $files.manifest.path -Raw | ConvertFrom-Json
    $report.mod_version = $info.Version
    if ($info.Id -ne 'ManualDiceRoller' -or $info.EntryMethod -ne 'ManualDiceRoller.Plugin.Load' -or
        $info.ManagerVersion -ne '0.32.4' -or $manifest.plugin_sha256 -ne $files.plugin.sha256 -or
        $manifest.info_sha256 -ne $files.info.sha256) {
        $report.setup_errors += 'Info/plugin/build-manifest mismatch.'
    }
} catch { $report.setup_errors += ('Unable to read mod metadata: ' + $_.Exception.Message) }

foreach ($process in @(Get-Process -Name Solasta -ErrorAction SilentlyContinue)) {
    $record = @{ id = $process.Id; native_modules = @(); module_observation_error = $null }
    try {
        $record.start_utc = $process.StartTime.ToUniversalTime().ToString('o')
        $record.executable_path = $process.MainModule.FileName
        foreach ($module in $process.Modules) {
            if ($module.ModuleName -match '^(UnityPlayer|GameAssembly|mono.*)\.dll$') {
                $record.native_modules += (File-Record $module.FileName)
            }
        }
    } catch { $record.module_observation_error = $_.Exception.Message }
    $report.processes += $record
}
if (-not $report.processes -and -not $AllowClosedGame) {
    $report.setup_errors += 'No running Solasta process observed; collect while the qualified game session is running.'
}

$index = 0
foreach ($path in $LogPaths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $report.setup_errors += ('Explicit log path missing: ' + $path)
        continue
    }
    $index++
    $destination = Join-Path $logsOutput ('log-' + $index + '.txt')
    # Snapshot explicit logs only. The collector does not search personal files.
    $text = Get-Content -LiteralPath $path -Raw
    [IO.File]::WriteAllText($destination, $text, [Text.UTF8Encoding]::new($false))
    $record = File-Record $destination
    $record.source_path = [IO.Path]::GetFullPath($path)
    $record.relative_path = 'logs/log-' + $index + '.txt'
    $report.logs += $record
    foreach ($line in ($text -split "`r?`n")) {
        $networkPrefix = 'MDR_NETWORK '
        $networkPosition = $line.IndexOf($networkPrefix, [StringComparison]::Ordinal)
        if ($networkPosition -ge 0) {
            try {
                $networkMarker = $line.Substring($networkPosition + $networkPrefix.Length) | ConvertFrom-Json
                $report.network_markers += @{ log = $record.relative_path; value = $networkMarker }
            } catch { $report.setup_errors += 'Malformed MDR_NETWORK JSON in an explicitly supplied log.' }
        }
        $prefix = 'MDR_DIAGNOSTICS '
        $position = $line.IndexOf($prefix, [StringComparison]::Ordinal)
        if ($position -lt 0) { continue }
        try {
            $marker = $line.Substring($position + $prefix.Length) | ConvertFrom-Json
            $report.runtime_markers += @{ log = $record.relative_path; value = $marker }
        } catch { $report.setup_errors += 'Malformed MDR_DIAGNOSTICS JSON in an explicitly supplied log.' }
    }
}

$casesPath = Join-Path $PSScriptRoot 'acceptance-cases.json'
$cases = Get-Content -LiteralPath $casesPath -Raw | ConvertFrom-Json
$acceptance = [ordered]@{
    schema_version = 1; profile = $cases.profile; tester = ''; session_started_utc = '';
    plugin_sha256 = $files.plugin.sha256; game_sha256 = $files.game.sha256;
    mod_version = $report.mod_version; diagnostic_report = 'windows-diagnostics.json';
    cases = @($cases.cases | ForEach-Object { [ordered]@{ id = $_.id; requirement = $_.requirement;
        status = 'pending'; observed = ''; evidence = @() } })
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'windows-diagnostics.json') -Encoding UTF8
$acceptancePath = Join-Path $output 'windows-acceptance.json'
if (-not (Test-Path -LiteralPath $acceptancePath)) {
    $acceptance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $acceptancePath -Encoding UTF8
}
Write-Host ('Captured read-only diagnostics to ' + $output)
Write-Host 'The acceptance checklist remains pending. Disk hashes and developer tests do not prove live Mono gameplay.'
if ($report.setup_errors.Count -gt 0) {
    $report.setup_errors | ForEach-Object { Write-Host ('CHECK: ' + $_) }
    exit 1
}
exit 0
