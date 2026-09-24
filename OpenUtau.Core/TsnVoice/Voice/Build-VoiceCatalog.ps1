# Rebuilds list.json from database_190826.json and regenerates catalog.json.
# Fix rules applied (documented, deterministic):
#  - Voice id = voice file stem; one stem per database voice (verified 1:1).
#  - Version normalization: leading X.Y.Z is kept as `version`; the full original
#    string is kept as `label`. Entries without a numeric prefix get version 0.0.0
#    (sorted last; download URL will fail with a clear message).
#  - Exact (version, fileName, md5) duplicates collapse; same version with a
#    different md5 is kept as a separate variant (manager disambiguates by md5).
#  - Display name = speaker.name, falling back to voices.name.
#  - Portraits/backgrounds are discovered from Voice/Singer/<voice-id>/ like upstream.
[CmdletBinding()]
param(
    [string] $DatabasePath = '',
    [string] $OutputRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$voiceRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = $voiceRoot
}
if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $voiceRoot '../../..'))
    $DatabasePath = Join-Path (Split-Path -Parent $repoRoot) 'database_190826.json'
}
$DatabasePath = [IO.Path]::GetFullPath($DatabasePath)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

function Assert-Token([string] $Value, [string] $Field) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[A-Za-z0-9._-]+$') {
        throw "Invalid ${Field}: '$Value'"
    }
}

function Normalize-Version([string] $Raw) {
    $match = [regex]::Match($Raw, '^\d+\.\d+\.\d+')
    if ($match.Success) {
        return $match.Value
    }
    return '0.0.0'
}

function Find-Portrait([string] $VoiceId) {
    $directory = Join-Path (Join-Path $OutputRoot 'Singer') $VoiceId
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        return $null
    }
    $portrait = Get-ChildItem -LiteralPath $directory -File |
        Where-Object {
            $_.Extension -in '.png', '.jpg', '.jpeg' -and
            $_.BaseName -ne 'background'
        } |
        Sort-Object Name |
        Select-Object -First 1
    if ($null -eq $portrait) {
        return $null
    }
    return "Singer/$VoiceId/$($portrait.Name)"
}

function Find-Background([string] $VoiceId) {
    $directory = Join-Path (Join-Path $OutputRoot 'Singer') $VoiceId
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        return $null
    }
    foreach ($name in 'background.png', 'background.jpg') {
        $background = Get-ChildItem -LiteralPath $directory -File |
            Where-Object { $_.Name -eq $name } |
            Select-Object -First 1
        if ($null -ne $background) {
            return "Singer/$VoiceId/$($background.Name)"
        }
    }
    return $null
}

$db = Get-Content -LiteralPath $DatabasePath -Raw -Encoding UTF8 | ConvertFrom-Json
$listEntries = New-Object System.Collections.Generic.List[object]
$catalogVoices = New-Object System.Collections.Generic.List[object]
$voiceIds = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$fixes = New-Object System.Collections.Generic.List[string]

foreach ($license in $db.user_info.licenses) {
    foreach ($voice in $license.voices) {
        $stems = @($voice.voicefiles |
            ForEach-Object { [IO.Path]::GetFileNameWithoutExtension([string]$_.name) } |
            Sort-Object -Unique)
        if ($stems.Count -ne 1) {
            throw "Database voice has $($stems.Count) file stems: $($stems -join ',')"
        }
        $voiceId = [string]$stems[0]
        Assert-Token $voiceId 'voice id'
        if (-not $voiceIds.Add($voiceId)) {
            throw "Duplicate voice id '$voiceId'"
        }

        foreach ($file in $voice.voicefiles) {
            $fileName = [string]$file.name
            if ([IO.Path]::GetFileName($fileName) -cne $fileName -or
                -not $fileName.EndsWith('.tsnvoice', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Invalid voice file name: '$fileName'"
            }
            $md5 = ([string]$file.hash).ToLowerInvariant()
            if ($md5 -notmatch '^[a-f0-9]{32}$') {
                throw "Invalid MD5 '$md5' for '$voiceId'"
            }
        }

        $displayName = [string]$voice.speaker.name
        if ([string]::IsNullOrWhiteSpace($displayName)) {
            $displayName = [string]$voice.name
            $fixes.Add("[$voiceId] empty speaker name, fell back to voice name")
        }
        if ([string]::IsNullOrWhiteSpace($displayName)) {
            throw "Voice '$voiceId' has no display name"
        }
        $language = [string]$voice.language.name
        Assert-Token $language "language of '$voiceId'"

        $isValid = [bool]$license.is_valid -and [bool]$voice.active
        $purchaseLink = if ($null -ne $license.information_link) {
            [string]$license.information_link
        } else {
            ''
        }
        $listEntries.Add([ordered]@{
            voices = $voice
            is_valid = $isValid
            purchase_link = $purchaseLink
        })

        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        $versions = New-Object System.Collections.Generic.List[object]
        foreach ($file in $voice.voicefiles) {
            $normalized = Normalize-Version ([string]$file.version)
            if ($normalized -eq '0.0.0') {
                $fixes.Add("[$voiceId] non-numeric version '$($file.version)', stored as 0.0.0 variant")
            }
            $key = "$normalized|$([string]$file.name)|$(([string]$file.hash).ToLowerInvariant())"
            if (-not $seen.Add($key)) {
                $fixes.Add("[$voiceId] collapsed exact duplicate file entry '$($file.name)' $($file.version)")
                continue
            }
            $versions.Add([ordered]@{
                version = $normalized
                label = [string]$file.version
                fileName = [string]$file.name
                md5 = ([string]$file.hash).ToLowerInvariant()
            })
        }
        $sorted = @($versions | Sort-Object `
            @{ Expression = { [Version]$_.version }; Descending = $true }, `
            @{ Expression = { [string]$_.label } })
        $catalogVoices.Add([ordered]@{
            id = $voiceId
            name = $displayName.Trim()
            language = $language
            portrait = Find-Portrait $voiceId
            background = Find-Background $voiceId
            versions = @($sorted)
        })
    }
}

$catalog = [ordered]@{
    schemaVersion = 1
    name = 'TsnVoice Voice Manager'
    downloadBaseUrl = 'https://cdn.voisona.com/voice/'
    voices = @($catalogVoices | Sort-Object { [string]$_.id })
}

$listPath = Join-Path $OutputRoot 'list.json'
$catalogPath = Join-Path $OutputRoot 'catalog.json'
$noBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($listPath,
    (($listEntries.ToArray() | ConvertTo-Json -Depth 12) + [Environment]::NewLine), $noBom)
[IO.File]::WriteAllText($catalogPath,
    (($catalog | ConvertTo-Json -Depth 8) + [Environment]::NewLine), $noBom)
Write-Host "Wrote $listPath with $($listEntries.Count) entries."
Write-Host "Wrote $catalogPath with $($catalogVoices.Count) voices."
if ($fixes.Count -gt 0) {
    Write-Host "Fix notes ($($fixes.Count)):"
    $fixes | Sort-Object -Unique | ForEach-Object { Write-Host "  - $_" }
}
