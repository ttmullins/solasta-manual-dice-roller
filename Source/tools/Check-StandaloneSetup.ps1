param([Parameter(Mandatory = $true)][string]$GamePath, [string]$ModPath)
$ErrorActionPreference = 'Stop'

# Read-only setup check. This script never restores, replaces or deletes a DLL.
$managed = Join-Path $GamePath 'Solasta_Data\Managed'
$requirements = @(
    @{ Name = 'Clean Solasta game'; Path = (Join-Path $managed 'Assembly-CSharp.dll'); Hash = 'a87e45cbd187e42e9d64a610850b39edba4896168b14d71ca71c5d32cf9e96ee' },
    @{ Name = 'Unity Mod Manager 0.32.4'; Path = (Join-Path $managed 'UnityModManager\UnityModManager.dll'); Hash = '1387468bc3af41c50fe51859a3bb7af4922891aa8f13a6187e7a348ceaabfd88' },
    @{ Name = 'Harmony 2.3.6'; Path = (Join-Path $managed 'UnityModManager\0Harmony.dll'); Hash = 'df043f05c6e43602fa8e8f38f52a3a2cf962aab7a65a9d85573ea25f01a63e80' },
    @{ Name = 'Pristine Photon PUN'; Path = (Join-Path $managed 'PhotonUnityNetworking.dll'); Hash = 'f1efa0cebf758bd79d43e202757d39b00e27384c65084ed14a9697af8bc3e32b' },
    @{ Name = 'Pristine Photon Realtime'; Path = (Join-Path $managed 'PhotonRealtime.dll'); Hash = '1cb825bb0f955fef65a29b2981797214d08fa2137b3738c4924b709b7d28a29d' },
    @{ Name = 'Pristine Photon Client'; Path = (Join-Path $managed 'Photon3Unity3D.dll'); Hash = '5619e0aec897562522ce9adc87b5b80e3e5e9b1b137cbae866f8812e874e7fbe' }
)
$failed = $false
foreach ($item in $requirements) {
    if (-not (Test-Path -LiteralPath $item.Path -PathType Leaf)) {
        Write-Host ('MISSING: ' + $item.Name + ' at ' + $item.Path)
        $failed = $true
        continue
    }
    $actual = (Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -eq $item.Hash) {
        Write-Host ('OK: ' + $item.Name)
    } else {
        Write-Host ('DIFFERENT: ' + $item.Name + ' SHA256=' + $actual)
        $failed = $true
    }
}
if ($ModPath) {
    try {
        $info = Get-Content -LiteralPath (Join-Path $ModPath 'Info.json') -Raw | ConvertFrom-Json
        $manifest = Get-Content -LiteralPath (Join-Path $ModPath 'build-manifest.json') -Raw | ConvertFrom-Json
        $pluginHash = (Get-FileHash -LiteralPath (Join-Path $ModPath 'ManualDiceRoller.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        $infoHash = (Get-FileHash -LiteralPath (Join-Path $ModPath 'Info.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($info.Id -ne 'ManualDiceRoller' -or $info.EntryMethod -ne 'ManualDiceRoller.Plugin.Load' -or
            $info.ManagerVersion -ne '0.32.4' -or $pluginHash -ne $manifest.plugin_sha256 -or
            $infoHash -ne $manifest.info_sha256 -or $info.Version -ne $manifest.metadata.Version) {
            throw 'Mod Info/DLL/build-manifest mismatch.'
        }
        Write-Host ('OK: installed mod artifact and metadata match audited version ' + $info.Version)
    } catch {
        Write-Host ('DIFFERENT/MISSING: installed mod verification: ' + $_.Exception.Message)
        $failed = $true
    }
}
if ($failed) {
    Write-Host 'Restore any previous ManualRoll replacement using its clean backup or Steam Verify, then install the supported official UMM distribution. This check changed no files.'
    exit 1
}
Write-Host 'The analyzed game and supported loader/Harmony files match. Disk verification does not prove live Mono loading or gameplay. This check changed no files.'
exit 0
