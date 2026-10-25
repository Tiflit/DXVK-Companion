# ProcessLifecycleTrial.ps1
# Supervised packaged process-lifecycle trial tooling and guards for DXVK Companion.
# Governed by kernel v0.4 and docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md.
# Conforms to Stage B candidate specification (Issue #301 / PR #302 Repair).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet(
        "Setup",
        "Supervise",
        "Test-SafeZipArchive",
        "Test-PathContainment",
        "Compare-TargetBaseline",
        "Compare-SemanticLibraryJson",
        "Test-BatchGuard",
        "Take-Snapshot",
        "Verify-Checkpoint",
        "Status"
    )]
    [string]$Action,

    [Parameter(Mandatory = $false)]
    [string]$FixtureRoot,

    [Parameter(Mandatory = $false)]
    [string]$HelperZipPath,

    [Parameter(Mandatory = $false)]
    [string]$CompanionZipPath,

    [Parameter(Mandatory = $false)]
    [string]$CompanionProvenancePath,

    [Parameter(Mandatory = $false)]
    [string]$Dxvk31TarGzPath,

    [Parameter(Mandatory = $false)]
    [string]$Dxvk311TarGzPath,

    [Parameter(Mandatory = $false)]
    [int]$Slot = 0,

    [Parameter(Mandatory = $false)]
    [string]$CheckpointName,

    [Parameter(Mandatory = $false)]
    [string]$ExpectedType,

    [Parameter(Mandatory = $false)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $false)]
    [string]$AdoptionAttestation,

    [Parameter(Mandatory = $false)]
    [switch]$NoAutoDiscovery,

    [Parameter(Mandatory = $false)]
    [string]$ZipPath,

    [Parameter(Mandatory = $false)]
    [string]$TargetPath,

    [Parameter(Mandatory = $false)]
    [string]$PathA,

    [Parameter(Mandatory = $false)]
    [string]$PathB,

    [Parameter(Mandatory = $false)]
    [string]$TestPinsJson,

    [Parameter(Mandatory = $false)]
    [string[]]$Commands,

    [Parameter(Mandatory = $false)]
    [string]$CommandFile,

    [Parameter(Mandatory = $false)]
    [switch]$JsonOutput
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue

# ==============================================================================
# Frozen Pins and Resource Limits (Issue #301)
# ==============================================================================

# Helper Artifact Pins (PR #300 / Run 40220299641 attempt 1)
$script:FROZEN_HELPER_ARTIFACT_ID     = "12609386490"
$script:FROZEN_HELPER_ZIP_SIZE        = 81717
$script:FROZEN_HELPER_ZIP_SHA256      = "f75ec55738e6ca3c1a0cd0a342aefeaa0fb3addd03538f03f2ac7a43295750de"
$script:FROZEN_HELPER_EXE_SIZE        = 138240
$script:FROZEN_HELPER_EXE_SHA256      = "f720cecae0f13eeef6d9b5e2d259b51df97b37bc3347b241071e25e8c1548d70"
$script:FROZEN_HELPER_SRC_SHA256      = "19229f59a4ab2504634cf9cd1bfff131a89a68e539808ab68c5459451e6ce0cc"
$script:FROZEN_HELPER_CHECKOUT_COMMIT = "a62356d0acb918aec957463a0b670747763ae544"
$script:FROZEN_HELPER_TREE            = "4ebbd76b0f30ca7768d737361ea8b540da5a8a2e"
$script:FROZEN_HELPER_RUN_ID          = "40220299641"

# Companion Candidate Pins (Run 40025768457 attempt 1)
$script:FROZEN_COMPANION_SRC_COMMIT   = "46e6f22363cddcfa88d805b588b6ac30173bc9dc"
$script:FROZEN_COMPANION_ARTIFACT_ID  = "12532606945"
$script:FROZEN_COMPANION_ZIP_SIZE     = 66410238
$script:FROZEN_COMPANION_ZIP_SHA256   = "32660fd3e3ed87385e47940d490db2e6ab93daad7e993fe4225dd157232b3b14"
$script:FROZEN_COMPANION_PROVENANCE_ARTIFACT_ID = "12531339870"
$script:FROZEN_COMPANION_PROVENANCE_SIZE        = 323
$script:FROZEN_COMPANION_PROVENANCE_SHA256     = "c9b46817bc60e89b3848ecf2705cb929b718cf950689a15071c65933d2a0537d"
$script:FROZEN_COMPANION_EXE_SIZE     = 71772524
$script:FROZEN_COMPANION_EXE_SHA256   = "25012c67ade281e0d4585282bf485e4783a1f7c486018f8d1a038c5f2dc18ca4"
$script:FROZEN_COMPANION_RUN_ID       = "40025768457"

# Official Upstream DXVK Release Pins
$script:FROZEN_DXVK_311_RELEASE_ID    = "381956359"
$script:FROZEN_DXVK_311_ASSET_ID      = "566164743"
$script:FROZEN_DXVK_311_SIZE          = 18041512
$script:FROZEN_DXVK_311_SHA256        = "40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608"

$script:FROZEN_DXVK_31_RELEASE_ID     = "357524779"
$script:FROZEN_DXVK_31_ASSET_ID       = "534051057"
$script:FROZEN_DXVK_31_SIZE           = 18056721
$script:FROZEN_DXVK_31_SHA256         = "30f9cc326874be344285582275446968cfa4c069db31ce56df312d6644179154"

# Strict Batch Budgets
$script:MAX_TRIAL_DURATION_SECONDS    = 7200    # 120 minutes total
$script:MAX_HELPER_STARTS             = 4
$script:MAX_COMPANION_STARTS          = 8
$script:MAX_FIXTURE_ROOT_BYTES        = 2147483648L  # 2 GiB
$script:HELPER_WATCHDOG_SECONDS       = 1500        # 25 minutes

# Required Adoption Attestation Token
$script:REQUIRED_ADOPTION_TOKEN       = "ADOPTED-PROCESS-LIFECYCLE-V1"

# Retained Process Handles and Supervisor State
$script:HelperProcess = $null
$script:CompanionProcess = $null
$script:SupervisorMonotonicTimer = $null

# ==============================================================================
# Pure Data and Path Helper Functions
# ==============================================================================

function Compute-FileSha256([string]$FilePath) {
    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw "File not found for hash calculation: $FilePath"
    }
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($FilePath)
        try {
            $hashBytes = $hasher.ComputeHash($stream)
            return (-join ($hashBytes | ForEach-Object { $_.ToString("x2") }))
        } finally {
            $stream.Dispose()
        }
    } finally {
        $hasher.Dispose()
    }
}

function Validate-CheckpointName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) {
        throw "Checkpoint name cannot be empty."
    }
    if ($Name.Length -gt 64) {
        throw "Checkpoint name exceeds maximum length of 64 characters: '$Name'."
    }
    if (-not ($Name -match '^[a-zA-Z0-9_\-]+$')) {
        throw "Invalid checkpoint name: '$Name'. Must contain only alphanumeric characters, underscores, and hyphens."
    }
    return $Name
}

function Validate-RootBoundPath([string]$RootPath, [string]$CandidatePath) {
    if ([string]::IsNullOrWhiteSpace($RootPath) -or [string]::IsNullOrWhiteSpace($CandidatePath)) {
        throw "Root path and candidate path must be specified."
    }
    $canonRoot = [System.IO.Path]::GetFullPath($RootPath).TrimEnd('\', '/')
    $rootWithSep = $canonRoot + [System.IO.Path]::DirectorySeparatorChar

    $canonCandidate = [System.IO.Path]::GetFullPath($CandidatePath).TrimEnd('\', '/')

    # Prefix check with directory separator to prevent sibling match (e.g. C:\RootOther vs C:\Root)
    if (-not ($canonCandidate.StartsWith($rootWithSep, [System.StringComparison]::OrdinalIgnoreCase) -or $canonCandidate -eq $canonRoot)) {
        throw "Path escape detected: '$canonCandidate' is outside root '$canonRoot'."
    }

    # Reparse point check: check existing ancestors all the way to root
    $probe = $canonCandidate
    while (-not [string]::IsNullOrEmpty($probe) -and -not (Test-Path -LiteralPath $probe)) {
        $parent = [System.IO.Path]::GetDirectoryName($probe)
        if ($parent -eq $probe) { break }
        $probe = $parent
    }

    while (-not [string]::IsNullOrEmpty($probe)) {
        if (Test-Path -LiteralPath $probe) {
            $item = Get-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
            if ($null -ne $item -and ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
                throw "Reparse point detected in path: '$probe'. Traversal is prohibited."
            }
        }
        $parent = [System.IO.Path]::GetDirectoryName($probe)
        if ($parent -eq $probe) { break }
        $probe = $parent
    }

    return $canonCandidate
}

function Test-PathEscape([string]$BasePath, [string]$CandidatePath) {
    try {
        [void](Validate-RootBoundPath -RootPath $BasePath -CandidatePath $CandidatePath)
        return $false
    } catch {
        return $true
    }
}

function Test-SafeZipArchiveInternal(
    [string]$ZipFilePath,
    [int]$MaxEntries = 1024,
    [long]$MaxTotalBytes = 524288000L
) {
    if (-not (Test-Path -LiteralPath $ZipFilePath -PathType Leaf)) {
        return @{ IsValid = $false; Reason = "Zip archive not found: $ZipFilePath" }
    }

    $seenEntries = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $totalUncompressedBytes = 0L
    $entryCount = 0

    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipFilePath)
        try {
            foreach ($entry in $archive.Entries) {
                $entryCount++
                if ($entryCount -gt $MaxEntries) {
                    return @{ IsValid = $false; Reason = "Archive exceeds maximum entry count of $MaxEntries." }
                }

                $name = $entry.FullName
                if ([string]::IsNullOrWhiteSpace($name)) {
                    return @{ IsValid = $false; Reason = "Archive contains empty entry name." }
                }

                # Check for symlink / special entry attributes in zip header (0xA000 = S_IFLNK)
                $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xF000
                if ($unixMode -eq 0xA000) {
                    return @{ IsValid = $false; Reason = "Archive contains symbolic link entry: $name" }
                }

                if ($name.StartsWith("/") -or $name.StartsWith("\")) {
                    return @{ IsValid = $false; Reason = "Archive contains root-relative path: $name" }
                }

                if ($name.Contains(":")) {
                    return @{ IsValid = $false; Reason = "Archive contains drive colon or alternate data stream: $name" }
                }

                # Windows illegal characters
                $illegalChars = @('<', '>', '"', '|', '?', '*')
                foreach ($c in $illegalChars) {
                    if ($name.Contains($c)) {
                        return @{ IsValid = $false; Reason = "Archive entry contains invalid character '$c': $name" }
                    }
                }

                $segments = $name -split '[\\/]'
                foreach ($segment in $segments) {
                    if ($segment -eq "..") {
                        return @{ IsValid = $false; Reason = "Archive entry contains path traversal segment (..): $name" }
                    }
                    if ($segment.EndsWith(" ") -or $segment.EndsWith(".")) {
                        return @{ IsValid = $false; Reason = "Archive entry contains Windows-aliased trailing space or dot: $name" }
                    }
                }

                $normalized = $name.Replace('/', '\').TrimEnd('\')
                if ($seenEntries.Contains($normalized)) {
                    return @{ IsValid = $false; Reason = "Archive contains duplicate/colliding entry name: $name" }
                }
                [void]$seenEntries.Add($normalized)

                $totalUncompressedBytes += $entry.Length
                if ($totalUncompressedBytes -gt $MaxTotalBytes) {
                    return @{ IsValid = $false; Reason = "Archive uncompressed size exceeds limit of $MaxTotalBytes bytes." }
                }
            }

            return @{
                IsValid = $true
                Reason = "Archive passed safe inspection."
                EntryCount = $entryCount
                TotalBytes = $totalUncompressedBytes
            }
        } finally {
            $archive.Dispose()
        }
    } catch {
        return @{ IsValid = $false; Reason = "Archive inspection failed with exception: $_" }
    }
}

function Expand-SafeZipArchiveInternal(
    [string]$ZipFilePath,
    [string]$DestinationDirectory,
    [int]$MaxEntries = 1024,
    [long]$MaxTotalBytes = 524288000L
) {
    $inspection = Test-SafeZipArchiveInternal -ZipFilePath $ZipFilePath -MaxEntries $MaxEntries -MaxTotalBytes $MaxTotalBytes
    if (-not $inspection.IsValid) {
        throw "Safe extraction refused: $($inspection.Reason)"
    }

    $destFull = Validate-RootBoundPath -RootPath $DestinationDirectory -CandidatePath $DestinationDirectory
    if (-not (Test-Path -LiteralPath $destFull)) {
        [void][System.IO.Directory]::CreateDirectory($destFull)
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipFilePath)
    try {
        foreach ($entry in $archive.Entries) {
            $entryPath = [System.IO.Path]::Combine($destFull, $entry.FullName)
            $resolvedEntry = Validate-RootBoundPath -RootPath $destFull -CandidatePath $entryPath

            if ($entry.FullName.EndsWith("/") -or $entry.FullName.EndsWith("\")) {
                if (-not (Test-Path -LiteralPath $resolvedEntry)) {
                    [void][System.IO.Directory]::CreateDirectory($resolvedEntry)
                }
            } else {
                $parentDir = [System.IO.Path]::GetDirectoryName($resolvedEntry)
                if (-not [string]::IsNullOrEmpty($parentDir) -and -not (Test-Path -LiteralPath $parentDir)) {
                    [void][System.IO.Directory]::CreateDirectory($parentDir)
                }

                # Use FileMode::CreateNew to guarantee no existing files are ever overwritten
                $outStream = [System.IO.FileStream]::new(
                    $resolvedEntry,
                    [System.IO.FileMode]::CreateNew,
                    [System.IO.FileAccess]::Write,
                    [System.IO.FileShare]::None
                )
                try {
                    $inStream = $entry.Open()
                    try {
                        $inStream.CopyTo($outStream)
                    } finally {
                        $inStream.Dispose()
                    }
                } finally {
                    $outStream.Dispose()
                }
            }
        }
    } finally {
        $archive.Dispose()
    }
}

function Measure-DirectoryFootprintBytes([string]$DirectoryPath) {
    if (-not (Test-Path -LiteralPath $DirectoryPath -PathType Container)) {
        return 0L
    }
    $total = 0L
    $items = Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File -Force -ErrorAction SilentlyContinue
    foreach ($item in $items) {
        $total += $item.Length
    }
    return $total
}

# ==============================================================================
# State and Batch Guard Functions
# ==============================================================================

function Get-StateFilePath([string]$FixtureRoot) {
    $canon = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    return [System.IO.Path]::Combine($canon, "state.json")
}

function Get-StateData([string]$FixtureRoot) {
    $statePath = Get-StateFilePath -FixtureRoot $FixtureRoot
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        throw "Batch state file not found: $statePath"
    }
    $raw = [System.IO.File]::ReadAllText($statePath, [System.Text.Encoding]::UTF8)
    return ConvertFrom-Json -InputObject $raw
}

function Save-StateData([string]$FixtureRoot, $StateObject) {
    $statePath = Get-StateFilePath -FixtureRoot $FixtureRoot
    $json = ConvertTo-Json -InputObject $StateObject -Depth 10
    [System.IO.File]::WriteAllText($statePath, $json, [System.Text.Encoding]::UTF8)
}

function Test-BatchGuardInternal(
    [string]$FixtureRoot,
    [int]$Slot = 0,
    [bool]$IsCompanion = $false,
    [string]$Attestation = ""
) {
    if (-not (Test-Path -LiteralPath $FixtureRoot -PathType Container)) {
        return @{ CanProceed = $false; Reason = "Fixture root directory does not exist: $FixtureRoot" }
    }

    $state = $null
    try {
        $state = Get-StateData -FixtureRoot $FixtureRoot
    } catch {
        return @{ CanProceed = $false; Reason = "Failed to read state.json: $_" }
    }

    if ($state.IsEnded) {
        return @{ CanProceed = $false; Reason = "Batch is marked ended. Further starts are refused." }
    }

    if ($state.LastCheckpointOutcome -eq "Failed") {
        return @{ CanProceed = $false; Reason = "Prior checkpoint failed. Further starts are refused." }
    }

    # Strict Test Pins Guard
    if ($state.TestPinsUsed) {
        return @{ CanProceed = $false; Reason = "Execution refused: fixture root was initialized with test pins and cannot be launched." }
    }

    # Adoption Attestation Guard
    if ($Attestation -ne $script:REQUIRED_ADOPTION_TOKEN) {
        return @{ CanProceed = $false; Reason = "Execution refused: missing or invalid adoption attestation token." }
    }

    # Duration Guard (Wall clock + Monotonic)
    $startTime = [DateTime]::Parse($state.BatchStartTimeUtc)
    $elapsedSeconds = ([DateTime]::UtcNow - $startTime).TotalSeconds
    if ($elapsedSeconds -gt $script:MAX_TRIAL_DURATION_SECONDS) {
        return @{ CanProceed = $false; Reason = "Batch duration cap ($($script:MAX_TRIAL_DURATION_SECONDS)s) exceeded ($([int]$elapsedSeconds)s)." }
    }

    # Root footprint cap check (2 GiB)
    $currentFootprint = Measure-DirectoryFootprintBytes -DirectoryPath $FixtureRoot
    if ($currentFootprint -ge $script:MAX_FIXTURE_ROOT_BYTES) {
        return @{ CanProceed = $false; Reason = "Fixture root footprint cap ($($script:MAX_FIXTURE_ROOT_BYTES) bytes) exceeded ($currentFootprint bytes)." }
    }

    if ($IsCompanion) {
        if ($state.CompanionStartAttempts -ge $script:MAX_COMPANION_STARTS) {
            return @{ CanProceed = $false; Reason = "Maximum Companion start attempt count ($($script:MAX_COMPANION_STARTS)) reached." }
        }
        if ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited) {
            return @{ CanProceed = $false; Reason = "Companion process is already running." }
        }
    } else {
        # Helper start
        if ($Slot -lt 1 -or $Slot -gt 4) {
            return @{ CanProceed = $false; Reason = "Invalid helper slot: $Slot. Expected 1..4." }
        }

        # Slot non-reuse enforcement
        $usedSlots = @($state.UsedSlots)
        if ($usedSlots -contains $Slot) {
            return @{ CanProceed = $false; Reason = "Slot $Slot has already been used in this batch. Slot reuse is prohibited." }
        }

        if ($state.HelperStartAttempts -ge $script:MAX_HELPER_STARTS) {
            return @{ CanProceed = $false; Reason = "Maximum helper start attempt count ($($script:MAX_HELPER_STARTS)) reached." }
        }

        if ($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) {
            return @{ CanProceed = $false; Reason = "Helper process is already running." }
        }

        # Check for pre-existing stop marker
        $stopMarkerPath = [System.IO.Path]::Combine($FixtureRoot, "control", "stop-$Slot.request")
        if (Test-Path -LiteralPath $stopMarkerPath) {
            return @{ CanProceed = $false; Reason = "Stop marker already exists: stop-$Slot.request. Startup refused." }
        }
    }

    return @{ CanProceed = $true; Reason = "Batch guard passed." }
}

# ==============================================================================
# Baseline and Semantic Comparison Functions
# ==============================================================================

function Compare-TargetBaselineInternal([string]$FixtureRoot) {
    $targetDir = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath ([System.IO.Path]::Combine($FixtureRoot, "target"))
    $manifestPath = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath ([System.IO.Path]::Combine($FixtureRoot, "baseline", "baseline_manifest.json"))

    if (-not (Test-Path -LiteralPath $targetDir -PathType Container)) {
        return @{ IsMatch = $false; Reason = "Target directory not found: $targetDir" }
    }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return @{ IsMatch = $false; Reason = "Baseline manifest not found: $manifestPath" }
    }

    $manifest = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8))

    # Assert dxgi.dll is absent
    $dxgiPath = [System.IO.Path]::Combine($targetDir, "dxgi.dll")
    if (Test-Path -LiteralPath $dxgiPath) {
        return @{ IsMatch = $false; Reason = "dxgi.dll unexpectedly present in target directory." }
    }

    # Assert each baseline file matches exactly
    foreach ($entry in $manifest.Files) {
        $filePath = [System.IO.Path]::Combine($targetDir, $entry.RelativePath)
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            return @{ IsMatch = $false; Reason = "Baseline file missing in target: $($entry.RelativePath)" }
        }

        $currentSize = (Get-Item -LiteralPath $filePath -Force).Length
        if ($currentSize -ne $entry.SizeBytes) {
            return @{ IsMatch = $false; Reason = "Baseline file size mismatch for $($entry.RelativePath): expected $($entry.SizeBytes), got $currentSize" }
        }

        $currentHash = Compute-FileSha256 -FilePath $filePath
        if ($currentHash -ne $entry.Sha256) {
            return @{ IsMatch = $false; Reason = "Baseline file SHA256 mismatch for $($entry.RelativePath): expected $($entry.Sha256), got $currentHash" }
        }
    }

    # Assert no unexpected extra files exist in target (including subdirectories or hidden files)
    $targetFiles = Get-ChildItem -LiteralPath $targetDir -Recurse -File -Force
    $expectedNames = @($manifest.Files | ForEach-Object { $_.RelativePath.Replace('/', '\') })
    foreach ($file in $targetFiles) {
        $rel = $file.FullName.Substring($targetDir.Length).TrimStart('\', '/')
        if ($expectedNames -notcontains $rel) {
            return @{ IsMatch = $false; Reason = "Unexpected file present in target: $rel" }
        }
    }

    return @{ IsMatch = $true; Reason = "Target strictly matches immutable baseline." }
}

function Get-SafePropertyValue($Object, [string]$PropertyName) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Compare-SemanticLibraryJsonInternal([string]$JsonA, [string]$JsonB) {
    try {
        $objA = if (Test-Path -LiteralPath $JsonA -PathType Leaf) {
            ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($JsonA, [System.Text.Encoding]::UTF8))
        } else {
            ConvertFrom-Json -InputObject $JsonA
        }

        $objB = if (Test-Path -LiteralPath $JsonB -PathType Leaf) {
            ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($JsonB, [System.Text.Encoding]::UTF8))
        } else {
            ConvertFrom-Json -InputObject $JsonB
        }

        $schemaA = Get-SafePropertyValue -Object $objA -PropertyName "SchemaVersion"
        $schemaB = Get-SafePropertyValue -Object $objB -PropertyName "SchemaVersion"
        if ($schemaA -ne $schemaB) {
            return @{ IsEquivalent = $false; Reason = "SchemaVersion mismatch: $schemaA vs $schemaB" }
        }

        $listRawA = Get-SafePropertyValue -Object $objA -PropertyName "Installations"
        $listRawB = Get-SafePropertyValue -Object $objB -PropertyName "Installations"
        $listA = if ($null -eq $listRawA) { @() } else { @($listRawA) }
        $listB = if ($null -eq $listRawB) { @() } else { @($listRawB) }

        if ($listA.Count -ne $listB.Count) {
            return @{ IsEquivalent = $false; Reason = "Installations count mismatch: $($listA.Count) vs $($listB.Count)" }
        }

        # Index by stable Id
        $mapB = @{}
        foreach ($inst in $listB) {
            $instId = Get-SafePropertyValue -Object $inst -PropertyName "Id"
            $mapB["$instId"] = $inst
        }

        foreach ($instA in $listA) {
            $key = "$(Get-SafePropertyValue -Object $instA -PropertyName 'Id')"
            if (-not $mapB.ContainsKey($key)) {
                return @{ IsEquivalent = $false; Reason = "Installation Id '$key' missing in second object." }
            }
            $instB = $mapB[$key]

            # Compare scalar properties
            $props = @(
                "DisplayName", "IsHidden", "ManagedDxvkVersion", "ManagedDxvkArchitecture",
                "RestorationState", "ConflictFlags", "LastCancellationReason",
                "LastCancellationOutcome", "LastRefusalReason"
            )
            foreach ($p in $props) {
                $rawA = Get-SafePropertyValue -Object $instA -PropertyName $p
                $rawB = Get-SafePropertyValue -Object $instB -PropertyName $p
                $vA = if ($null -eq $rawA) { "" } else { "$rawA" }
                $vB = if ($null -eq $rawB) { "" } else { "$rawB" }
                if ($vA -ne $vB) {
                    return @{ IsEquivalent = $false; Reason = "Property '$p' mismatch on installation '$key': '$vA' vs '$vB'" }
                }
            }

            # Canonical path comparison
            $pathAVal = Get-SafePropertyValue -Object $instA -PropertyName "InstallationPath"
            $pathBVal = Get-SafePropertyValue -Object $instB -PropertyName "InstallationPath"
            $pathA = if ($null -eq $pathAVal) { "" } else { [System.IO.Path]::GetFullPath("$pathAVal").TrimEnd('\', '/') }
            $pathB = if ($null -eq $pathBVal) { "" } else { [System.IO.Path]::GetFullPath("$pathBVal").TrimEnd('\', '/') }
            if ($pathA -ne $pathB) {
                return @{ IsEquivalent = $false; Reason = "InstallationPath mismatch on '$key': '$pathA' vs '$pathB'" }
            }

            # ManagementPolicy comparison
            $policyA = Get-SafePropertyValue -Object $instA -PropertyName "ManagementPolicy"
            $policyB = Get-SafePropertyValue -Object $instB -PropertyName "ManagementPolicy"
            $modeA = if ($null -eq $policyA) { "" } else { "$(Get-SafePropertyValue -Object $policyA -PropertyName 'Mode')" }
            $modeB = if ($null -eq $policyB) { "" } else { "$(Get-SafePropertyValue -Object $policyB -PropertyName 'Mode')" }
            if ($modeA -ne $modeB) {
                return @{ IsEquivalent = $false; Reason = "ManagementPolicy.Mode mismatch on '$key': '$modeA' vs '$modeB'" }
            }

            # Configuration comparison
            $configA = Get-SafePropertyValue -Object $instA -PropertyName "Configuration"
            $configB = Get-SafePropertyValue -Object $instB -PropertyName "Configuration"
            if ($null -ne $configA -and $null -ne $configB) {
                $hudA = Get-SafePropertyValue -Object $configA -PropertyName "HudEnabled"
                $hudB = Get-SafePropertyValue -Object $configB -PropertyName "HudEnabled"
                $flA = Get-SafePropertyValue -Object $configA -PropertyName "FrameLimit"
                $flB = Get-SafePropertyValue -Object $configB -PropertyName "FrameLimit"
                $fleA = Get-SafePropertyValue -Object $configA -PropertyName "FrameLimitEnabled"
                $fleB = Get-SafePropertyValue -Object $configB -PropertyName "FrameLimitEnabled"
                if ($hudA -ne $hudB -or $flA -ne $flB -or $fleA -ne $fleB) {
                    return @{ IsEquivalent = $false; Reason = "Configuration mismatch on installation '$key'." }
                }
            }

            # PendingAction comparison
            $pendingActionA = Get-SafePropertyValue -Object $instA -PropertyName "PendingAction"
            $pendingActionB = Get-SafePropertyValue -Object $instB -PropertyName "PendingAction"
            $pendingA = if ($null -eq $pendingActionA) { "None" } else { "$(Get-SafePropertyValue -Object $pendingActionA -PropertyName 'Type'):$(Get-SafePropertyValue -Object $pendingActionA -PropertyName 'TargetDxvkVersion')" }
            $pendingB = if ($null -eq $pendingActionB) { "None" } else { "$(Get-SafePropertyValue -Object $pendingActionB -PropertyName 'Type'):$(Get-SafePropertyValue -Object $pendingActionB -PropertyName 'TargetDxvkVersion')" }
            if ($pendingA -ne $pendingB) {
                return @{ IsEquivalent = $false; Reason = "PendingAction mismatch on '$key': '$pendingA' vs '$pendingB'" }
            }

            # Executables comparison by RelativePath
            $execsRawA = Get-SafePropertyValue -Object $instA -PropertyName "Executables"
            $execsRawB = Get-SafePropertyValue -Object $instB -PropertyName "Executables"
            $execsA = if ($null -eq $execsRawA) { @() } else { @($execsRawA) }
            $execsB = if ($null -eq $execsRawB) { @() } else { @($execsRawB) }
            if ($execsA.Count -ne $execsB.Count) {
                return @{ IsEquivalent = $false; Reason = "Executables count mismatch on '$key'." }
            }
            $execMapB = @{}
            foreach ($e in $execsB) {
                $rel = Get-SafePropertyValue -Object $e -PropertyName "RelativePath"
                $execMapB["$rel"] = $e
            }
            foreach ($eA in $execsA) {
                $eKey = "$(Get-SafePropertyValue -Object $eA -PropertyName 'RelativePath')"
                if (-not $execMapB.ContainsKey($eKey)) {
                    return @{ IsEquivalent = $false; Reason = "Executable '$eKey' missing on '$key'." }
                }
                $eB = $execMapB[$eKey]
                $apiA = Get-SafePropertyValue -Object $eA -PropertyName "LastKnownApi"
                $apiB = Get-SafePropertyValue -Object $eB -PropertyName "LastKnownApi"
                $archA = Get-SafePropertyValue -Object $eA -PropertyName "LastKnownArchitecture"
                $archB = Get-SafePropertyValue -Object $eB -PropertyName "LastKnownArchitecture"
                if ("$apiA" -ne "$apiB" -or "$archA" -ne "$archB") {
                    return @{ IsEquivalent = $false; Reason = "Executable metadata mismatch for '$eKey'." }
                }
            }

            # ManagedFiles comparison by RelativePath
            $mFilesRawA = Get-SafePropertyValue -Object $instA -PropertyName "ManagedFiles"
            $mFilesRawB = Get-SafePropertyValue -Object $instB -PropertyName "ManagedFiles"
            $mFilesA = if ($null -eq $mFilesRawA) { @() } else { @($mFilesRawA) }
            $mFilesB = if ($null -eq $mFilesRawB) { @() } else { @($mFilesRawB) }
            if ($mFilesA.Count -ne $mFilesB.Count) {
                return @{ IsEquivalent = $false; Reason = "ManagedFiles count mismatch on '$key'." }
            }
        }

        return @{ IsEquivalent = $true; Reason = "Library JSON structures are semantically equivalent." }
    } catch {
        return @{ IsEquivalent = $false; Reason = "Semantic comparison failed with exception: $_" }
    }
}

# ==============================================================================
# Setup Action
# ==============================================================================

function Invoke-SetupAction {
    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { throw "FixtureRoot parameter is required for Setup." }
    if ([string]::IsNullOrWhiteSpace($HelperZipPath)) { throw "HelperZipPath parameter is required for Setup." }
    if ([string]::IsNullOrWhiteSpace($CompanionZipPath)) { throw "CompanionZipPath parameter is required for Setup." }

    $rootFull = [System.IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\', '/')

    # F2: Require fresh absent destination root (refuse even if existing directory is empty)
    if (Test-Path -LiteralPath $rootFull) {
        throw "Setup refused: FixtureRoot already exists ($rootFull). A fresh absent destination is required."
    }

    # Validate ancestor containment and absence of reparse points
    $parentProbe = [System.IO.Path]::GetDirectoryName($rootFull)
    while (-not [string]::IsNullOrEmpty($parentProbe)) {
        if (Test-Path -LiteralPath $parentProbe) {
            $item = Get-Item -LiteralPath $parentProbe -Force -ErrorAction SilentlyContinue
            if ($null -ne $item -and ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
                throw "Reparse point detected in ancestor path: '$parentProbe'. Traversal is prohibited."
            }
        }
        $nextParent = [System.IO.Path]::GetDirectoryName($parentProbe)
        if ($nextParent -eq $parentProbe) { break }
        $parentProbe = $nextParent
    }

    # Start monotonic budget and UTC timer BEFORE any setup writes
    $setupStartUtc = [DateTime]::UtcNow.ToString("o")
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    # Parse TestPins if supplied
    $testPins = $null
    $testPinsUsed = $false
    if (-not [string]::IsNullOrWhiteSpace($TestPinsJson)) {
        $testPins = ConvertFrom-Json -InputObject $TestPinsJson
        $testPinsUsed = $true
    }

    # F4: Validate archive size AND hash for Helper
    $helperZipItem = Get-Item -LiteralPath $HelperZipPath -Force
    $expectedHelperZipSize = if ($testPinsUsed -and $testPins.HelperZipSize) { [long]$testPins.HelperZipSize } else { $script:FROZEN_HELPER_ZIP_SIZE }
    if ($helperZipItem.Length -ne $expectedHelperZipSize) {
        throw "Helper archive size mismatch: expected $expectedHelperZipSize bytes, got $($helperZipItem.Length)"
    }
    $expectedHelperZipHash = if ($testPinsUsed -and $testPins.HelperZipSha256) { $testPins.HelperZipSha256 } else { $script:FROZEN_HELPER_ZIP_SHA256 }
    $actualHelperZipHash = Compute-FileSha256 -FilePath $HelperZipPath
    if ($actualHelperZipHash -ne $expectedHelperZipHash) {
        throw "Helper archive SHA256 mismatch: expected $expectedHelperZipHash, got $actualHelperZipHash"
    }

    # F4: Validate archive size AND hash for Companion
    $companionZipItem = Get-Item -LiteralPath $CompanionZipPath -Force
    $expectedCompanionZipSize = if ($testPinsUsed -and $testPins.CompanionZipSize) { [long]$testPins.CompanionZipSize } else { $script:FROZEN_COMPANION_ZIP_SIZE }
    if ($companionZipItem.Length -ne $expectedCompanionZipSize) {
        throw "Companion archive size mismatch: expected $expectedCompanionZipSize bytes, got $($companionZipItem.Length)"
    }
    $expectedCompanionZipHash = if ($testPinsUsed -and $testPins.CompanionZipSha256) { $testPins.CompanionZipSha256 } else { $script:FROZEN_COMPANION_ZIP_SHA256 }
    $actualCompanionZipHash = Compute-FileSha256 -FilePath $CompanionZipPath
    if ($actualCompanionZipHash -ne $expectedCompanionZipHash) {
        throw "Companion archive SHA256 mismatch: expected $expectedCompanionZipHash, got $actualCompanionZipHash"
    }

    # Validate Companion Provenance if provided
    if (-not [string]::IsNullOrWhiteSpace($CompanionProvenancePath)) {
        $provItem = Get-Item -LiteralPath $CompanionProvenancePath -Force
        $expectedProvSize = if ($testPinsUsed -and $testPins.CompanionProvenanceSize) { [long]$testPins.CompanionProvenanceSize } else { $script:FROZEN_COMPANION_PROVENANCE_SIZE }
        if ($provItem.Length -ne $expectedProvSize) {
            throw "Companion provenance size mismatch: expected $expectedProvSize bytes, got $($provItem.Length)"
        }
        $expectedProvHash = if ($testPinsUsed -and $testPins.CompanionProvenanceSha256) { $testPins.CompanionProvenanceSha256 } else { $script:FROZEN_COMPANION_PROVENANCE_SHA256 }
        $actualProvHash = Compute-FileSha256 -FilePath $CompanionProvenancePath
        if ($actualProvHash -ne $expectedProvHash) {
            throw "Companion provenance SHA256 mismatch: expected $expectedProvHash, got $actualProvHash"
        }
    }

    # Create root and subdirectories
    [void][System.IO.Directory]::CreateDirectory($rootFull)

    $targetDir = [System.IO.Path]::Combine($rootFull, "target")
    $controlDir = [System.IO.Path]::Combine($rootFull, "control")
    $baselineDir = [System.IO.Path]::Combine($rootFull, "baseline")
    $companionDir = [System.IO.Path]::Combine($rootFull, "companion")
    $runtimeDir = [System.IO.Path]::Combine($rootFull, "runtime")
    $snapshotsDir = [System.IO.Path]::Combine($rootFull, "snapshots")
    $logsDir = [System.IO.Path]::Combine($rootFull, "logs")
    $referenceDir = [System.IO.Path]::Combine($rootFull, "reference")
    $profilesDir = [System.IO.Path]::Combine($companionDir, "Profiles")
    $backupsDir = [System.IO.Path]::Combine($profilesDir, "Backups")

    foreach ($dir in @($targetDir, $controlDir, $baselineDir, $companionDir, $runtimeDir, $snapshotsDir, $logsDir, $referenceDir, $profilesDir, $backupsDir)) {
        [void][System.IO.Directory]::CreateDirectory($dir)
    }

    # F2: Extract helper directly to a dedicated directory and copy executable; no deletion in finally
    $helperExtractDir = [System.IO.Path]::Combine($rootFull, "helper_package")
    Expand-SafeZipArchiveInternal -ZipFilePath $HelperZipPath -DestinationDirectory $helperExtractDir

    $extractedHelperExe = [System.IO.Path]::Combine($helperExtractDir, "QueueFixture.exe")
    if (-not (Test-Path -LiteralPath $extractedHelperExe -PathType Leaf)) {
        throw "Extracted helper package does not contain QueueFixture.exe"
    }

    # Validate extracted helper executable size and hash
    $helperExeItem = Get-Item -LiteralPath $extractedHelperExe -Force
    $expectedHelperExeSize = if ($testPinsUsed -and $testPins.HelperExeSize) { [long]$testPins.HelperExeSize } else { $script:FROZEN_HELPER_EXE_SIZE }
    if ($helperExeItem.Length -ne $expectedHelperExeSize) {
        throw "Extracted QueueFixture.exe size mismatch: expected $expectedHelperExeSize, got $($helperExeItem.Length)"
    }
    $expectedHelperExeHash = if ($testPinsUsed -and $testPins.HelperExeSha256) { $testPins.HelperExeSha256 } else { $script:FROZEN_HELPER_EXE_SHA256 }
    $actualHelperExeHash = Compute-FileSha256 -FilePath $extractedHelperExe
    if ($actualHelperExeHash -ne $expectedHelperExeHash) {
        throw "Extracted QueueFixture.exe SHA256 mismatch: expected $expectedHelperExeHash, got $actualHelperExeHash"
    }

    # Copy QueueFixture.exe to target (using create-new write)
    $targetHelperExe = [System.IO.Path]::Combine($targetDir, "QueueFixture.exe")
    [System.IO.File]::Copy($extractedHelperExe, $targetHelperExe, $false)

    # Extract companion archive safely into companionDir
    Expand-SafeZipArchiveInternal -ZipFilePath $CompanionZipPath -DestinationDirectory $companionDir
    $companionExe = [System.IO.Path]::Combine($companionDir, "DXVK-Companion.exe")
    if (-not (Test-Path -LiteralPath $companionExe -PathType Leaf)) {
        throw "Extracted companion archive does not contain DXVK-Companion.exe"
    }

    # Validate extracted companion executable size and hash
    $compExeItem = Get-Item -LiteralPath $companionExe -Force
    $expectedCompExeSize = if ($testPinsUsed -and $testPins.CompanionExeSize) { [long]$testPins.CompanionExeSize } else { $script:FROZEN_COMPANION_EXE_SIZE }
    if ($compExeItem.Length -ne $expectedCompExeSize) {
        throw "Extracted DXVK-Companion.exe size mismatch: expected $expectedCompExeSize, got $($compExeItem.Length)"
    }
    $expectedCompExeHash = if ($testPinsUsed -and $testPins.CompanionExeSha256) { $testPins.CompanionExeSha256 } else { $script:FROZEN_COMPANION_EXE_SHA256 }
    $actualCompExeHash = Compute-FileSha256 -FilePath $companionExe
    if ($actualCompExeHash -ne $expectedCompExeHash) {
        throw "Extracted DXVK-Companion.exe SHA256 mismatch: expected $expectedCompExeHash, got $actualCompExeHash"
    }

    # Official reference tar extraction (if provided)
    if (-not [string]::IsNullOrWhiteSpace($Dxvk311TarGzPath)) {
        $tarItem = Get-Item -LiteralPath $Dxvk311TarGzPath -Force
        if ($tarItem.Length -ne $script:FROZEN_DXVK_311_SIZE) {
            throw "DXVK v3.1.1 archive size mismatch: expected $($script:FROZEN_DXVK_311_SIZE), got $($tarItem.Length)"
        }
        $tarHash = Compute-FileSha256 -FilePath $Dxvk311TarGzPath
        if ($tarHash -ne $script:FROZEN_DXVK_311_SHA256) {
            throw "DXVK v3.1.1 archive SHA256 mismatch: expected $($script:FROZEN_DXVK_311_SHA256), got $tarHash"
        }
        $ref311Dir = [System.IO.Path]::Combine($referenceDir, "v3.1.1", "x64")
        [void][System.IO.Directory]::CreateDirectory($ref311Dir)
        & tar.exe -xzf $Dxvk311TarGzPath -C $ref311Dir --strip-components 2 "dxvk-3.1.1/x64/d3d11.dll" "dxvk-3.1.1/x64/dxgi.dll"
    }
    if (-not [string]::IsNullOrWhiteSpace($Dxvk31TarGzPath)) {
        $tarItem = Get-Item -LiteralPath $Dxvk31TarGzPath -Force
        if ($tarItem.Length -ne $script:FROZEN_DXVK_31_SIZE) {
            throw "DXVK v3.1 archive size mismatch: expected $($script:FROZEN_DXVK_31_SIZE), got $($tarItem.Length)"
        }
        $tarHash = Compute-FileSha256 -FilePath $Dxvk31TarGzPath
        if ($tarHash -ne $script:FROZEN_DXVK_31_SHA256) {
            throw "DXVK v3.1 archive SHA256 mismatch: expected $($script:FROZEN_DXVK_31_SHA256), got $tarHash"
        }
        $ref31Dir = [System.IO.Path]::Combine($referenceDir, "v3.1", "x64")
        [void][System.IO.Directory]::CreateDirectory($ref31Dir)
        & tar.exe -xzf $Dxvk31TarGzPath -C $ref31Dir --strip-components 2 "dxvk-3.1/x64/d3d11.dll" "dxvk-3.1/x64/dxgi.dll"
    }

    # Seed baseline files in target
    $inertD3d11Path = [System.IO.Path]::Combine($targetDir, "d3d11.dll")
    $dxvkConfPath = [System.IO.Path]::Combine($targetDir, "dxvk.conf")
    $sentinelPath = [System.IO.Path]::Combine($targetDir, "sentinel.txt")

    $inertD3d11Bytes = [System.Text.Encoding]::ASCII.GetBytes("INERT_D3D11_NATIVE_BASELINE_STUB_NEVER_LOADED")
    [System.IO.File]::WriteAllBytes($inertD3d11Path, $inertD3d11Bytes)

    $dxvkConfContent = "# Unrelated noncanonical dxvk.conf for process lifecycle trial`nd3d11.fakeOption = True`n"
    [System.IO.File]::WriteAllText($dxvkConfPath, $dxvkConfContent, [System.Text.Encoding]::UTF8)

    $sentinelContent = "TRIAL_BASELINE_SENTINEL_ROOT_IMMUTABLE`n"
    [System.IO.File]::WriteAllText($sentinelPath, $sentinelContent, [System.Text.Encoding]::UTF8)

    # Save immutable copies in baseline/ and compute baseline_manifest.json
    $manifestFiles = @()
    foreach ($item in @("QueueFixture.exe", "d3d11.dll", "dxvk.conf", "sentinel.txt")) {
        $src = [System.IO.Path]::Combine($targetDir, $item)
        $dst = [System.IO.Path]::Combine($baselineDir, $item)
        [System.IO.File]::Copy($src, $dst, $false)

        $manifestFiles += [PSCustomObject]@{
            RelativePath = $item
            SizeBytes    = (Get-Item -LiteralPath $src -Force).Length
            Sha256       = Compute-FileSha256 -FilePath $src
        }
    }

    $baselineManifest = [PSCustomObject]@{
        CreatedAtUtc = $setupStartUtc
        DxgiAbsent   = $true
        Files        = $manifestFiles
    }
    $manifestJson = ConvertTo-Json -InputObject $baselineManifest -Depth 5
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($baselineDir, "baseline_manifest.json"), $manifestJson, [System.Text.Encoding]::UTF8)

    # F4: Seed Companion Storage with REAL store schema (game-library.json AND games.json)
    $settingsPath = [System.IO.Path]::Combine($companionDir, "settings.json")
    $settingsObj = [PSCustomObject]@{
        GlobalPolicy              = "Manual"
        AutoEnableDxvkForNewGames = $false
        LaunchOnStartup           = $false
    }
    [System.IO.File]::WriteAllText($settingsPath, (ConvertTo-Json -InputObject $settingsObj), [System.Text.Encoding]::UTF8)

    # F4: Mode = UseGlobal (inherits GlobalPolicy = Manual)
    $installationRecord = [PSCustomObject]@{
        Id                      = "queue-fixture-trial-target"
        InstallationPath        = $targetDir
        DisplayName             = "QueueFixture Trial Target"
        IsHidden                = $false
        ManagementPolicy        = [PSCustomObject]@{
            Mode              = "UseGlobal"
            PinnedDxvkVersion = $null
        }
        ManagedDxvkVersion      = $null
        ManagedDxvkArchitecture = "x64"
        RestorationState        = "None"
        ConflictFlags           = "None"
        Configuration           = [PSCustomObject]@{
            HudEnabled        = $false
            FrameLimit        = 0
            FrameLimitEnabled = $false
        }
        Executables             = @(
            [PSCustomObject]@{
                RelativePath           = "QueueFixture.exe"
                DisplayName            = "QueueFixture.exe"
                LastKnownApi           = "DX11"
                ApiConfidence          = "High"
                LastKnownArchitecture  = "x64"
                LastSeenUtc            = $null
                DetectionEvidence      = @("Seeded process-lifecycle trial target")
            }
        )
        ManagedFiles            = @()
        PendingAction           = $null
        LastCancelledAction     = $null
        LastCancellationReason  = $null
        LastCancellationOutcome = $null
        LastRefusalReason       = $null
        CreatedUtc              = $setupStartUtc
        LastSeenUtc             = $null
    }

    $libraryObj = [PSCustomObject]@{
        SchemaVersion = 1
        Installations = @($installationRecord)
    }
    $libraryJson = ConvertTo-Json -InputObject $libraryObj -Depth 10

    $gameLibraryPath = [System.IO.Path]::Combine($profilesDir, "game-library.json")
    [System.IO.File]::WriteAllText($gameLibraryPath, $libraryJson, [System.Text.Encoding]::UTF8)

    $legacyGamesPath = [System.IO.Path]::Combine($profilesDir, "games.json")
    [System.IO.File]::WriteAllText($legacyGamesPath, $libraryJson, [System.Text.Encoding]::UTF8)

    # Initialize state.json with monotonic timings and start attempts
    $initialState = [PSCustomObject]@{
        BatchStartTimeUtc        = $setupStartUtc
        SetupElapsedMilliseconds = $stopwatch.ElapsedMilliseconds
        HelperStartAttempts      = 0
        CompanionStartAttempts   = 0
        MaxHelperStarts          = $script:MAX_HELPER_STARTS
        MaxCompanionStarts       = $script:MAX_COMPANION_STARTS
        MaxDurationSeconds       = $script:MAX_TRIAL_DURATION_SECONDS
        MaxFootprintBytes        = $script:MAX_FIXTURE_ROOT_BYTES
        UsedSlots                = @()
        CurrentCheckpoint        = "Setup"
        LastCheckpointOutcome    = "Success"
        LastFailedCheckpoint     = $null
        IsEnded                  = $false
        TestPinsUsed             = $testPinsUsed
        ActiveHelperPid          = $null
        ActiveCompanionPid       = $null
        Checkpoints              = @("Setup")
    }
    Save-StateData -FixtureRoot $rootFull -StateObject $initialState

    return @{
        Success = $true
        Message = "Setup completed successfully."
        FixtureRoot = $rootFull
        TestPinsUsed = $testPinsUsed
    }
}

# ==============================================================================
# Snapshot and Verification Functions
# ==============================================================================

function Take-SnapshotInternal([string]$FixtureRoot, [string]$CheckpointName) {
    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    $cleanName = Validate-CheckpointName -Name $CheckpointName

    $snapshotsDir = [System.IO.Path]::Combine($canonRoot, "snapshots")
    $checkpointDir = [System.IO.Path]::Combine($snapshotsDir, $cleanName)

    if (Test-Path -LiteralPath $checkpointDir) {
        throw "Snapshot checkpoint directory already exists: $checkpointDir"
    }
    [void][System.IO.Directory]::CreateDirectory($checkpointDir)

    $targetDir = [System.IO.Path]::Combine($canonRoot, "target")
    $profilesDir = [System.IO.Path]::Combine($canonRoot, "companion", "Profiles")
    $controlDir = [System.IO.Path]::Combine($canonRoot, "control")

    $snapTarget = [System.IO.Path]::Combine($checkpointDir, "target")
    $snapProfiles = [System.IO.Path]::Combine($checkpointDir, "Profiles")
    $snapControl = [System.IO.Path]::Combine($checkpointDir, "control")

    Copy-Item -LiteralPath $targetDir -Destination $snapTarget -Recurse -Force
    Copy-Item -LiteralPath $profilesDir -Destination $snapProfiles -Recurse -Force
    Copy-Item -LiteralPath $controlDir -Destination $snapControl -Recurse -Force

    $files = Get-ChildItem -LiteralPath $checkpointDir -Recurse -File -Force
    $fileRecords = @()
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($checkpointDir.Length).TrimStart('\', '/')
        $fileRecords += [PSCustomObject]@{
            RelativePath = $rel
            SizeBytes    = $f.Length
            Sha256       = Compute-FileSha256 -FilePath $f.FullName
        }
    }

    $snapshotManifest = [PSCustomObject]@{
        CheckpointName = $cleanName
        TakenAtUtc     = [DateTime]::UtcNow.ToString("o")
        Files          = $fileRecords
    }

    $manifestPath = [System.IO.Path]::Combine($checkpointDir, "manifest.json")
    [System.IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $snapshotManifest -Depth 5), [System.Text.Encoding]::UTF8)

    # Record in state
    $state = Get-StateData -FixtureRoot $canonRoot
    $state.CurrentCheckpoint = $cleanName
    $checkpointsList = @($state.Checkpoints)
    if ($checkpointsList -notcontains $cleanName) {
        $checkpointsList += $cleanName
    }
    $state.Checkpoints = $checkpointsList
    Save-StateData -FixtureRoot $canonRoot -StateObject $state

    return @{
        Success = $true
        Message = "Snapshot '$cleanName' captured successfully."
        CheckpointDirectory = $checkpointDir
        TotalFiles = $fileRecords.Count
    }
}

function Verify-CheckpointInternal(
    [string]$FixtureRoot,
    [string]$CheckpointName,
    [string]$ExpectedType,
    [string]$ExpectedVersion = ""
) {
    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    $cleanName = Validate-CheckpointName -Name $CheckpointName

    $targetDir = [System.IO.Path]::Combine($canonRoot, "target")
    $gameLibraryPath = [System.IO.Path]::Combine($canonRoot, "companion", "Profiles", "game-library.json")

    # F4: Repeated live read to verify stability of store file
    if (-not (Test-Path -LiteralPath $gameLibraryPath -PathType Leaf)) {
        throw "game-library.json not found: $gameLibraryPath"
    }
    $read1 = [System.IO.File]::ReadAllText($gameLibraryPath, [System.Text.Encoding]::UTF8)
    Start-Sleep -Milliseconds 500
    $read2 = [System.IO.File]::ReadAllText($gameLibraryPath, [System.Text.Encoding]::UTF8)
    if ($read1 -ne $read2) {
        throw "Live library store is unstable across repeated reads. Inspection uncertainty detected."
    }

    $library = ConvertFrom-Json -InputObject $read1
    $inst = $library.Installations[0]

    $verificationPassed = $false
    $failReason = ""

    try {
        switch ($ExpectedType) {
            "Pending" {
                # Target files must be unmodified relative to appropriate previous checkpoint
                # If slot 1 pending, compare to baseline
                $baseCheck = Compare-TargetBaselineInternal -FixtureRoot $canonRoot
                if (-not $baseCheck.IsMatch) {
                    throw "Pending checkpoint verification failed: target was mutated ($($baseCheck.Reason))"
                }

                # Helper liveness verified via retained process handle
                if ($null -eq $script:HelperProcess -or $script:HelperProcess.HasExited) {
                    throw "Pending checkpoint verification failed: helper process is not actively running."
                }

                if ($null -eq $inst.PendingAction) {
                    throw "Pending checkpoint verification failed: no PendingAction recorded."
                }
                if ($inst.PendingAction.Type -ne "Install" -and $inst.PendingAction.Type -ne "Update") {
                    throw "Pending checkpoint verification failed: unexpected pending action type '$($inst.PendingAction.Type)'."
                }
                if (-not [string]::IsNullOrEmpty($ExpectedVersion) -and $inst.PendingAction.TargetDxvkVersion -ne $ExpectedVersion) {
                    throw "Pending checkpoint verification failed: expected target version '$ExpectedVersion', got '$($inst.PendingAction.TargetDxvkVersion)'."
                }
            }
            "Applied" {
                # Helper must be confirmed stopped
                if ($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) {
                    throw "Applied checkpoint verification failed: helper process is still running."
                }

                $dxgiPath = [System.IO.Path]::Combine($targetDir, "dxgi.dll")
                $d3d11Path = [System.IO.Path]::Combine($targetDir, "d3d11.dll")
                if (-not (Test-Path -LiteralPath $dxgiPath -PathType Leaf)) {
                    throw "Applied checkpoint verification failed: dxgi.dll is absent from target."
                }
                if (-not (Test-Path -LiteralPath $d3d11Path -PathType Leaf)) {
                    throw "Applied checkpoint verification failed: d3d11.dll is absent from target."
                }

                # F4: Compare target DLLs against official reference bytes if reference directory exists
                if (-not [string]::IsNullOrEmpty($ExpectedVersion)) {
                    $refVersionDir = [System.IO.Path]::Combine($canonRoot, "reference", $ExpectedVersion, "x64")
                    if (Test-Path -LiteralPath $refVersionDir) {
                        $refD3d11 = [System.IO.Path]::Combine($refVersionDir, "d3d11.dll")
                        $refDxgi = [System.IO.Path]::Combine($refVersionDir, "dxgi.dll")
                        if (Test-Path -LiteralPath $refD3d11) {
                            $targetHash = Compute-FileSha256 -FilePath $d3d11Path
                            $refHash = Compute-FileSha256 -FilePath $refD3d11
                            if ($targetHash -ne $refHash) {
                                throw "Applied target d3d11.dll does not match official $ExpectedVersion reference payload."
                            }
                        }
                        if (Test-Path -LiteralPath $refDxgi) {
                            $targetHash = Compute-FileSha256 -FilePath $dxgiPath
                            $refHash = Compute-FileSha256 -FilePath $refDxgi
                            if ($targetHash -ne $refHash) {
                                throw "Applied target dxgi.dll does not match official $ExpectedVersion reference payload."
                            }
                        }
                    }

                    if ($inst.ManagedDxvkVersion -ne $ExpectedVersion) {
                        throw "Applied verification failed: expected managed version '$ExpectedVersion', got '$($inst.ManagedDxvkVersion)'."
                    }
                }

                # Verify native backup is preserved in Profiles/Backups
                $backupsDir = [System.IO.Path]::Combine($canonRoot, "companion", "Profiles", "Backups")
                $backupFiles = Get-ChildItem -LiteralPath $backupsDir -Recurse -File -Force -ErrorAction SilentlyContinue
                if ($backupFiles.Count -eq 0) {
                    throw "Applied checkpoint verification failed: no native backup found in Backups directory."
                }

                if ($null -ne $inst.PendingAction) {
                    throw "Applied checkpoint verification failed: pending action was not cleared after dispatch."
                }
            }
            "Restored" {
                # Target must match original baseline exactly
                $baseCheck = Compare-TargetBaselineInternal -FixtureRoot $canonRoot
                if (-not $baseCheck.IsMatch) {
                    throw "Restored checkpoint verification failed: $($baseCheck.Reason)"
                }
                if ($null -ne $inst.ManagedDxvkVersion) {
                    throw "Restored verification failed: ManagedDxvkVersion is '$($inst.ManagedDxvkVersion)' (expected null)."
                }
                if ($null -ne $inst.PendingAction) {
                    throw "Restored verification failed: lingering PendingAction found."
                }
            }
            Default {
                throw "Unrecognized ExpectedType: $ExpectedType"
            }
        }
        $verificationPassed = $true
    } catch {
        $verificationPassed = $false
        $failReason = "$_"
    }

    # F3: Persist failed checkpoint outcome in state.json before returning
    $state = Get-StateData -FixtureRoot $canonRoot
    if ($verificationPassed) {
        $state.LastCheckpointOutcome = "Success"
        Save-StateData -FixtureRoot $canonRoot -StateObject $state
        return @{
            Success = $true
            Message = "Checkpoint '$cleanName' verified successfully as $ExpectedType."
            CheckpointName = $cleanName
            ExpectedType = $ExpectedType
        }
    } else {
        $state.LastCheckpointOutcome = "Failed"
        $state.LastFailedCheckpoint = $cleanName
        Save-StateData -FixtureRoot $canonRoot -StateObject $state
        throw "Verification failed for checkpoint '$cleanName': $failReason"
    }
}

# ==============================================================================
# Retained Process Supervisor (F1)
# ==============================================================================

function Supervise-HelperStart([string]$FixtureRoot, [int]$Slot, [string]$Attestation) {
    $guard = Test-BatchGuardInternal -FixtureRoot $FixtureRoot -Slot $Slot -IsCompanion $false -Attestation $Attestation
    if (-not $guard.CanProceed) {
        throw "Start-Helper refused: $($guard.Reason)"
    }

    $targetDir = [System.IO.Path]::Combine($FixtureRoot, "target")
    $helperExe = [System.IO.Path]::Combine($targetDir, "QueueFixture.exe")

    # F3: Re-validate executable size and hash against frozen constants immediately before launch
    $exeItem = Get-Item -LiteralPath $helperExe -Force
    if ($exeItem.Length -ne $script:FROZEN_HELPER_EXE_SIZE) {
        throw "Launch refused: QueueFixture.exe size ($($exeItem.Length)) does not match frozen pin ($($script:FROZEN_HELPER_EXE_SIZE))."
    }
    $actualHash = Compute-FileSha256 -FilePath $helperExe
    if ($actualHash -ne $script:FROZEN_HELPER_EXE_SHA256) {
        throw "Launch refused: QueueFixture.exe SHA256 ($actualHash) does not match frozen pin ($($script:FROZEN_HELPER_EXE_SHA256))."
    }

    # F1: Count attempted starts BEFORE Process.Start
    $state = Get-StateData -FixtureRoot $FixtureRoot
    $state.HelperStartAttempts = [int]$state.HelperStartAttempts + 1
    Save-StateData -FixtureRoot $FixtureRoot -StateObject $state

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $helperExe
    $psi.Arguments = "--slot $Slot"
    $psi.WorkingDirectory = $targetDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    if (-not $proc.Start()) {
        throw "Failed to start helper process."
    }

    # Retain the real process handle in supervisor state
    $script:HelperProcess = $proc

    # Bounded wait for READY signal on standard output (no kill on timeout)
    $readyTask = $proc.StandardOutput.ReadLineAsync()
    if (-not $readyTask.Wait(5000)) {
        throw "Helper process failed to emit READY within 5000ms. Residual process retained."
    }

    $firstLine = $readyTask.Result
    if ($firstLine -ne "READY") {
        throw "Helper process emitted unexpected output: '$firstLine' (expected 'READY')."
    }

    # Update state
    $state = Get-StateData -FixtureRoot $FixtureRoot
    $usedSlots = @($state.UsedSlots)
    if ($usedSlots -notcontains $Slot) {
        $usedSlots += $Slot
    }
    $state.UsedSlots = $usedSlots
    $state.ActiveHelperPid = $proc.Id
    Save-StateData -FixtureRoot $FixtureRoot -StateObject $state

    return @{
        Success = $true
        Message = "Helper started successfully in slot $Slot with PID $($proc.Id)."
        Pid = $proc.Id
        Slot = $Slot
    }
}

function Supervise-HelperStop([string]$FixtureRoot, [int]$Slot) {
    if ($null -eq $script:HelperProcess) {
        throw "No retained helper process handle in supervisor."
    }
    if ($script:HelperProcess.HasExited) {
        throw "Helper process has already exited unexpectedly."
    }

    $controlDir = [System.IO.Path]::Combine($FixtureRoot, "control")
    $markerPath = [System.IO.Path]::Combine($controlDir, "stop-$Slot.request")

    if (Test-Path -LiteralPath $markerPath) {
        throw "Stop marker already exists: $markerPath"
    }

    # Create stop marker regular file
    [System.IO.File]::WriteAllText($markerPath, "STOP`n", [System.Text.Encoding]::ASCII)

    # Bounded wait on retained process handle (zero force-kill)
    if (-not $script:HelperProcess.WaitForExit(10000)) {
        throw "Helper process failed to exit within 10000ms after stop marker created. Process retained."
    }

    $exitCode = $script:HelperProcess.ExitCode
    $stdoutRemaining = $script:HelperProcess.StandardOutput.ReadToEnd()

    if ($exitCode -ne 0) {
        throw "Helper process exited with non-zero code: $exitCode (expected 0)."
    }
    if (-not $stdoutRemaining.Contains("STOPPED")) {
        throw "Helper process exit output did not contain 'STOPPED'."
    }

    $script:HelperProcess.Dispose()
    $script:HelperProcess = $null

    $state = Get-StateData -FixtureRoot $FixtureRoot
    $state.ActiveHelperPid = $null
    Save-StateData -FixtureRoot $FixtureRoot -StateObject $state

    return @{
        Success = $true
        Message = "Helper slot $Slot stopped cleanly with exit code 0."
        ExitCode = 0
    }
}

function Supervise-CompanionStart([string]$FixtureRoot, [bool]$NoAutoDiscovery, [string]$Attestation) {
    $guard = Test-BatchGuardInternal -FixtureRoot $FixtureRoot -IsCompanion $true -Attestation $Attestation
    if (-not $guard.CanProceed) {
        throw "Start-Companion refused: $($guard.Reason)"
    }

    $companionDir = [System.IO.Path]::Combine($FixtureRoot, "companion")
    $companionExe = [System.IO.Path]::Combine($companionDir, "DXVK-Companion.exe")
    $runtimeDir = [System.IO.Path]::Combine($FixtureRoot, "runtime")

    # F3: Re-validate executable size and hash against frozen constants immediately before launch
    $exeItem = Get-Item -LiteralPath $companionExe -Force
    if ($exeItem.Length -ne $script:FROZEN_COMPANION_EXE_SIZE) {
        throw "Launch refused: DXVK-Companion.exe size ($($exeItem.Length)) does not match frozen pin ($($script:FROZEN_COMPANION_EXE_SIZE))."
    }
    $actualHash = Compute-FileSha256 -FilePath $companionExe
    if ($actualHash -ne $script:FROZEN_COMPANION_EXE_SHA256) {
        throw "Launch refused: DXVK-Companion.exe SHA256 ($actualHash) does not match frozen pin ($($script:FROZEN_COMPANION_EXE_SHA256))."
    }

    # Re-validate settings.json identity
    $settingsPath = [System.IO.Path]::Combine($companionDir, "settings.json")
    $settingsRaw = [System.IO.File]::ReadAllText($settingsPath, [System.Text.Encoding]::UTF8)
    $settingsObj = ConvertFrom-Json -InputObject $settingsRaw
    if ("$($settingsObj.GlobalPolicy)" -ne "Manual") {
        throw "Launch refused: settings.json GlobalPolicy is '$($settingsObj.GlobalPolicy)' (expected 'Manual')."
    }

    # F1: Count attempted starts BEFORE Process.Start
    $state = Get-StateData -FixtureRoot $FixtureRoot
    $state.CompanionStartAttempts = [int]$state.CompanionStartAttempts + 1
    Save-StateData -FixtureRoot $FixtureRoot -StateObject $state

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $companionExe
    $psi.Arguments = if ($NoAutoDiscovery) { "--no-auto-discovery" } else { "" }
    $psi.WorkingDirectory = $companionDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $false
    $psi.EnvironmentVariables["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = $runtimeDir

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    if (-not $proc.Start()) {
        throw "Failed to start Companion process."
    }

    # Retain process handle in supervisor state
    $script:CompanionProcess = $proc

    $state = Get-StateData -FixtureRoot $FixtureRoot
    $state.ActiveCompanionPid = $proc.Id
    Save-StateData -FixtureRoot $FixtureRoot -StateObject $state

    return @{
        Success = $true
        Message = "Companion started successfully with PID $($proc.Id)."
        Pid = $proc.Id
    }
}

function Supervise-CompanionStop([string]$FixtureRoot) {
    if ($null -eq $script:CompanionProcess) {
        return @{ Success = $true; Message = "Companion is not currently running." }
    }

    if (-not $script:CompanionProcess.HasExited) {
        # Bounded wait for operator to close Companion GUI normally
        Write-Host "Waiting up to 30s for operator to close Companion GUI normally..."
        if (-not $script:CompanionProcess.WaitForExit(30000)) {
            throw "Companion process did not exit within 30000ms. Force kill is prohibited; residual process retained."
        }
    }

    $exitCode = $script:CompanionProcess.ExitCode
    $script:CompanionProcess.Dispose()
    $script:CompanionProcess = $null

    $state = Get-StateData -FixtureRoot $FixtureRoot
    $state.ActiveCompanionPid = $null
    Save-StateData -FixtureRoot $FixtureRoot -StateObject $state

    return @{
        Success = $true
        Message = "Companion stopped with exit code $exitCode."
        ExitCode = $exitCode
    }
}

function Invoke-SuperviseAction {
    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { throw "FixtureRoot parameter is required for Supervise." }
    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot

    Write-Host "======================================================================"
    Write-Host "DXVK Companion Process-Lifecycle Supervisor (Retained Session)"
    Write-Host "Fixture Root: $canonRoot"
    Write-Host "======================================================================"

    $commandList = @()
    if ($null -ne $Commands -and $Commands.Length -gt 0) {
        $commandList = @($Commands)
    } elseif (-not [string]::IsNullOrWhiteSpace($CommandFile)) {
        if (-not (Test-Path -LiteralPath $CommandFile -PathType Leaf)) {
            throw "Command file not found: $CommandFile"
        }
        $commandList = Get-Content -LiteralPath $CommandFile
    }

    $executionResults = @()

    if ($commandList.Count -gt 0) {
        foreach ($line in $commandList) {
            $trimmed = $line.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) { continue }

            Write-Host "[SUPERVISOR EXEC] $trimmed"
            $parts = $trimmed -split '\s+'
            $cmd = $parts[0].ToLowerInvariant()

            $res = $null
            switch ($cmd) {
                "start-helper" {
                    $s = [int]$parts[1]
                    $res = Supervise-HelperStart -FixtureRoot $canonRoot -Slot $s -Attestation $AdoptionAttestation
                }
                "stop-helper" {
                    $s = [int]$parts[1]
                    $res = Supervise-HelperStop -FixtureRoot $canonRoot -Slot $s
                }
                "start-companion" {
                    $noAuto = ($parts -contains "--no-auto-discovery")
                    $res = Supervise-CompanionStart -FixtureRoot $canonRoot -NoAutoDiscovery $noAuto -Attestation $AdoptionAttestation
                }
                "stop-companion" {
                    $res = Supervise-CompanionStop -FixtureRoot $canonRoot
                }
                "snapshot" {
                    $name = $parts[1]
                    $res = Take-SnapshotInternal -FixtureRoot $canonRoot -CheckpointName $name
                }
                "verify" {
                    $name = $parts[1]
                    $type = $parts[2]
                    $ver = if ($parts.Length -gt 3) { $parts[3] } else { "" }
                    $res = Verify-CheckpointInternal -FixtureRoot $canonRoot -CheckpointName $name -ExpectedType $type -ExpectedVersion $ver
                }
                "end-batch" {
                    $state = Get-StateData -FixtureRoot $canonRoot
                    $state.IsEnded = $true
                    Save-StateData -FixtureRoot $canonRoot -StateObject $state
                    $res = @{ Success = $true; Message = "Batch marked ended." }
                }
                Default {
                    throw "Unrecognized supervisor command: $trimmed"
                }
            }
            $executionResults += $res
        }

        return @{
            Success = $true
            Message = "All supervisor commands executed successfully."
            Results = $executionResults
        }
    } else {
        Write-Host "Entering interactive supervisor mode. Type 'help' for commands, 'exit' to quit."
        while ($true) {
            $state = Get-StateData -FixtureRoot $canonRoot
            $prompt = "Trial [Helper=$($state.HelperStartAttempts)/4, Comp=$($state.CompanionStartAttempts)/8] > "
            Write-Host -NoNewline $prompt
            $inputLine = [Console]::ReadLine()
            if ([string]::IsNullOrWhiteSpace($inputLine)) { continue }
            $trimmed = $inputLine.Trim()
            if ($trimmed -eq "exit" -or $trimmed -eq "quit") {
                if ($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) {
                    Write-Warning "Helper process is still running. Please stop it first."
                    continue
                }
                if ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited) {
                    Write-Warning "Companion process is still running. Please close it first."
                    continue
                }
                break
            }
            # Execute interactive command
            try {
                $parts = $trimmed -split '\s+'
                $cmd = $parts[0].ToLowerInvariant()
                switch ($cmd) {
                    "start-helper" {
                        $s = [int]$parts[1]
                        $res = Supervise-HelperStart -FixtureRoot $canonRoot -Slot $s -Attestation $AdoptionAttestation
                        Write-Host "[SUCCESS] $($res.Message)"
                    }
                    "stop-helper" {
                        $s = [int]$parts[1]
                        $res = Supervise-HelperStop -FixtureRoot $canonRoot -Slot $s
                        Write-Host "[SUCCESS] $($res.Message)"
                    }
                    "start-companion" {
                        $noAuto = ($parts -contains "--no-auto-discovery")
                        $res = Supervise-CompanionStart -FixtureRoot $canonRoot -NoAutoDiscovery $noAuto -Attestation $AdoptionAttestation
                        Write-Host "[SUCCESS] $($res.Message)"
                    }
                    "stop-companion" {
                        $res = Supervise-CompanionStop -FixtureRoot $canonRoot
                        Write-Host "[SUCCESS] $($res.Message)"
                    }
                    "snapshot" {
                        $name = $parts[1]
                        $res = Take-SnapshotInternal -FixtureRoot $canonRoot -CheckpointName $name
                        Write-Host "[SUCCESS] $($res.Message)"
                    }
                    "verify" {
                        $name = $parts[1]
                        $type = $parts[2]
                        $ver = if ($parts.Length -gt 3) { $parts[3] } else { "" }
                        $res = Verify-CheckpointInternal -FixtureRoot $canonRoot -CheckpointName $name -ExpectedType $type -ExpectedVersion $ver
                        Write-Host "[SUCCESS] $($res.Message)"
                    }
                    "status" {
                        $st = Get-StateData -FixtureRoot $canonRoot
                        Write-Host (ConvertTo-Json -InputObject $st -Depth 5)
                    }
                    "end-batch" {
                        $state = Get-StateData -FixtureRoot $canonRoot
                        $state.IsEnded = $true
                        Save-StateData -FixtureRoot $canonRoot -StateObject $state
                        Write-Host "[SUCCESS] Batch ended."
                    }
                    "help" {
                        Write-Host "Available commands:"
                        Write-Host "  start-helper <1..4>"
                        Write-Host "  stop-helper <1..4>"
                        Write-Host "  start-companion [--no-auto-discovery]"
                        Write-Host "  stop-companion"
                        Write-Host "  snapshot <CheckpointName>"
                        Write-Host "  verify <CheckpointName> <Pending|Applied|Restored> [<Version>]"
                        Write-Host "  status"
                        Write-Host "  end-batch"
                        Write-Host "  exit"
                    }
                    Default {
                        Write-Warning "Unrecognized command: $cmd"
                    }
                }
            } catch {
                Write-Error "$_"
            }
        }
        return @{ Success = $true; Message = "Interactive supervisor exited." }
    }
}

# ==============================================================================
# Script Dispatch Entry Point
# ==============================================================================

$result = $null

try {
    switch ($Action) {
        "Setup" {
            $result = Invoke-SetupAction
        }
        "Supervise" {
            $result = Invoke-SuperviseAction
        }
        "Test-SafeZipArchive" {
            $chk = Test-SafeZipArchiveInternal -ZipFilePath $ZipPath
            $result = @{
                Success = $chk.IsValid
                Message = $chk.Reason
                Data    = $chk
            }
        }
        "Test-PathContainment" {
            $isEscape = Test-PathEscape -BasePath $PathA -CandidatePath $PathB
            $result = @{
                Success = (-not $isEscape)
                Message = if ($isEscape) { "Path escapes base root or contains reparse point." } else { "Path is strictly contained." }
            }
        }
        "Compare-TargetBaseline" {
            $cmp = Compare-TargetBaselineInternal -FixtureRoot $FixtureRoot
            $result = @{
                Success = $cmp.IsMatch
                Message = $cmp.Reason
            }
        }
        "Compare-SemanticLibraryJson" {
            $cmp = Compare-SemanticLibraryJsonInternal -JsonA $PathA -JsonB $PathB
            $result = @{
                Success = $cmp.IsEquivalent
                Message = $cmp.Reason
            }
        }
        "Test-BatchGuard" {
            $guard = Test-BatchGuardInternal -FixtureRoot $FixtureRoot -Slot $Slot -IsCompanion $NoAutoDiscovery -Attestation $AdoptionAttestation
            $result = @{
                Success = $guard.CanProceed
                Message = $guard.Reason
            }
        }
        "Take-Snapshot" {
            $result = Take-SnapshotInternal -FixtureRoot $FixtureRoot -CheckpointName $CheckpointName
        }
        "Verify-Checkpoint" {
            $result = Verify-CheckpointInternal -FixtureRoot $FixtureRoot -CheckpointName $CheckpointName -ExpectedType $ExpectedType -ExpectedVersion $ExpectedVersion
        }
        "Status" {
            $st = Get-StateData -FixtureRoot $FixtureRoot
            $result = @{
                Success = $true
                Message = "Status retrieved."
                State = $st
            }
        }
    }
} catch {
    $result = @{
        Success = $false
        Message = "$_"
    }
}

if ($JsonOutput) {
    ConvertTo-Json -InputObject $result -Depth 5
} else {
    if ($result.Success) {
        Write-Host "[SUCCESS] $($result.Message)"
    } else {
        Write-Error "[FAILURE] $($result.Message)"
    }
}

if (-not $result.Success) {
    exit 1
}
exit 0
