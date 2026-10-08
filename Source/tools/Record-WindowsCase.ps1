param(
    [Parameter(Mandatory = $true)][string]$Report,
    [Parameter(Mandatory = $true)][string]$Case,
    [Parameter(Mandatory = $true)][ValidateSet('pass', 'fail', 'pending')][string]$Status,
    [string]$Observed = '',
    [string[]]$Evidence = @()
)
$ErrorActionPreference = 'Stop'
# Record a tester's own observation. This script does not play the game or
# infer outcomes. One case is changed; every other case retains its status.
$path = [IO.Path]::GetFullPath($Report)
$folder = [IO.Path]::GetDirectoryName($path)
$value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
$matches = @($value.cases | Where-Object { $_.id -eq $Case })
if ($matches.Count -ne 1) { throw 'Unknown or duplicated acceptance case.' }
if ($Status -eq 'pass' -and (-not $Observed.Trim() -or $Evidence.Count -eq 0)) {
    throw 'Passing a case requires a concrete observation and retained evidence.'
}
foreach ($relative in $Evidence) {
    if ([IO.Path]::IsPathRooted($relative)) { throw 'Evidence paths must be relative to the report folder.' }
    $file = [IO.Path]::GetFullPath((Join-Path $folder $relative))
    if (-not $file.StartsWith($folder + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw ('Missing, empty or escaping evidence file: ' + $relative)
    }
}
$matches[0].status = $Status.ToLowerInvariant()
$matches[0].observed = $Observed
$matches[0].evidence = $Evidence
$temporary = $path + '.pending'
try {
    $value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding UTF8
    # Existing report is retained if writing the temporary report fails.
    [IO.File]::Replace($temporary, $path, $null)
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
Write-Host ('Recorded ' + $Case + ': ' + $Status + '. Other outcomes were unchanged.')
