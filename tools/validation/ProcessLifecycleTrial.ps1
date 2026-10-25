# ProcessLifecycleTrial.ps1
# Supervised packaged process-lifecycle trial tooling and guards for DXVK Companion.
# Governed by kernel v0.4 and docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md.
# Conforms to Stage B candidate specification (Issue #301).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet(
        "Setup",
        "Start-Helper",
        "Stop-Helper",
        "Start-Companion",
        "Take-Snapshot",
        "Verify-Checkpoint",
        "Status",
        "End-Batch",
        "Test-SafeZipArchive",
        "Test-PathContainment",
        "Compare-TargetBaseline",
        "Compare-SemanticLibraryJson",
        "Test-BatchGuard"
    )]
    [string]$Action,

    [Parameter(Mandatory = $false)]
    [string]$FixtureRoot,

    [Parameter(Mandatory = $false)]
    [string]$HelperZipPath,

    [Parameter(Mandatory = $false)]
    [string]$CompanionZipPath,

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
    [switch]$JsonOutput
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

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
$script:MAX_FIXTURE_ROOT_BYTES        = 2147483648  # 2 GiB
$script:HELPER_WATCHDOG_SECONDS       = 1500        # 25 minutes

# Required Adoption Attestation Token
$script:REQUIRED_ADOPTION_TOKEN       = "ADOPTED-PROCESS-LIFECYCLE-V1"

# ==============================================================================
# Pure Data and File Helper Functions
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

function Test-PathEscape([string]$BasePath, [string]$CandidatePath) {
    if ([string]::IsNullOrWhiteSpace($BasePath) -or [string]::IsNullOrWhiteSpace($CandidatePath)) {
        return $true
    }
    $resolvedBase = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/')
    $resolvedCandidate = [System.IO.Path]::GetFullPath($CandidatePath).TrimEnd('\', '/')

    if (-not $resolvedCandidate.StartsWith($resolvedBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true # Escaped base directory
    }

    # Verify no path segment contains reparse points / symlinks
    if (Test-Path -LiteralPath $CandidatePath) {
        $current = Get-Item -LiteralPath $CandidatePath -Force -ErrorAction SilentlyContinue
        while ($null -ne $current -and $current.FullName.Length -ge $resolvedBase.Length) {
            if ($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                return $true # Reparse point traversal detected
            }
            $current = $current.Parent
        }
    }

    return $false
}

function Test-SafeZipArchiveInternal(
    [string]$ZipFilePath,
    [int]$MaxEntries = 1024,
    [long]$MaxTotalBytes = 524288000
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

                if ($name.StartsWith("/") -or $name.StartsWith("\")) {
                    return @{ IsValid = $false; Reason = "Archive contains root-relative path: $name" }
                }

                if ($name.Contains(":")) {
                    return @{ IsValid = $false; Reason = "Archive contains drive colon or alternate data stream: $name" }
                }

                $segments = $name -split '[\\/]'
                foreach ($segment in $segments) {
                    if ($segment -eq "..") {
                        return @{ IsValid = $false; Reason = "Archive entry contains path traversal segment (..): $name" }
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
    [long]$MaxTotalBytes = 524288000
) {
    $inspection = Test-SafeZipArchiveInternal -ZipFilePath $ZipFilePath -MaxEntries $MaxEntries -MaxTotalBytes $MaxTotalBytes
    if (-not $inspection.IsValid) {
        throw "Safe extraction refused: $($inspection.Reason)"
    }

    $destFull = [System.IO.Path]::GetFullPath($DestinationDirectory).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $destFull)) {
        [void][System.IO.Directory]::CreateDirectory($destFull)
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipFilePath)
    try {
        foreach ($entry in $archive.Entries) {
            $entryPath = [System.IO.Path]::Combine($destFull, $entry.FullName)
            $resolvedEntry = [System.IO.Path]::GetFullPath($entryPath)

            if (-not $resolvedEntry.StartsWith($destFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Extraction attempted to write outside destination: $($entry.FullName)"
            }

            if ($entry.FullName.EndsWith("/") -or $entry.FullName.EndsWith("\")) {
                [void][System.IO.Directory]::CreateDirectory($resolvedEntry)
            } else {
                $parentDir = [System.IO.Path]::GetDirectoryName($resolvedEntry)
                if (-not [string]::IsNullOrEmpty($parentDir) -and -not (Test-Path -LiteralPath $parentDir)) {
                    [void][System.IO.Directory]::CreateDirectory($parentDir)
                }
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $resolvedEntry, $true)
            }
        }
    } finally {
        $archive.Dispose()
    }
}

# ==============================================================================
# State and Batch Guard Functions
# ==============================================================================

function Get-StateFilePath([string]$FixtureRoot) {
    return [System.IO.Path]::Combine($FixtureRoot, "state.json")
}

function Get-StateData([string]$FixtureRoot) {
    $statePath = Get-StateFilePath -FixtureRoot $FixtureRoot
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        throw "Batch state file not found: $statePath"
    }
    $raw = [System.IO.File]::ReadAllText($statePath)
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

    # Strict Test Pins Guard: Test pins must NEVER be allowed to launch real processes.
    if ($state.TestPinsUsed) {
        return @{ CanProceed = $false; Reason = "Execution refused: fixture root was prepared with test pins and cannot be launched." }
    }

    # Adoption Attestation Guard
    if ($Attestation -ne $script:REQUIRED_ADOPTION_TOKEN) {
        return @{ CanProceed = $false; Reason = "Execution refused: missing or invalid adoption attestation token." }
    }

    # Duration Guard
    $startTime = [DateTime]::Parse($state.BatchStartTimeUtc)
    $elapsedSeconds = ([DateTime]::UtcNow - $startTime).TotalSeconds
    if ($elapsedSeconds -gt $script:MAX_TRIAL_DURATION_SECONDS) {
        return @{ CanProceed = $false; Reason = "Batch duration cap ($($script:MAX_TRIAL_DURATION_SECONDS)s) exceeded ($([int]$elapsedSeconds)s)." }
    }

    if ($IsCompanion) {
        if ($state.CompanionStartCount -ge $script:MAX_COMPANION_STARTS) {
            return @{ CanProceed = $false; Reason = "Maximum Companion start count ($($script:MAX_COMPANION_STARTS)) reached." }
        }
        if ($null -ne $state.ActiveCompanionPid -and $state.ActiveCompanionPid -gt 0) {
            try {
                $existingProc = [System.Diagnostics.Process]::GetProcessById($state.ActiveCompanionPid)
                if (-not $existingProc.HasExited) {
                    return @{ CanProceed = $false; Reason = "Companion process ($($state.ActiveCompanionPid)) is already running." }
                }
            } catch {
                # Process not running
            }
        }
    } else {
        # Helper start
        if ($Slot -lt 1 -or $Slot -gt 4) {
            return @{ CanProceed = $false; Reason = "Invalid helper slot: $Slot. Expected 1..4." }
        }

        if ($state.HelperStartCount -ge $script:MAX_HELPER_STARTS) {
            return @{ CanProceed = $false; Reason = "Maximum helper start count ($($script:MAX_HELPER_STARTS)) reached." }
        }

        if ($null -ne $state.ActiveHelperPid -and $state.ActiveHelperPid -gt 0) {
            try {
                $existingProc = [System.Diagnostics.Process]::GetProcessById($state.ActiveHelperPid)
                if (-not $existingProc.HasExited) {
                    return @{ CanProceed = $false; Reason = "Helper process ($($state.ActiveHelperPid)) is already running." }
                }
            } catch {
                # Process not running
            }
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
    $targetDir = [System.IO.Path]::Combine($FixtureRoot, "target")
    $manifestPath = [System.IO.Path]::Combine($FixtureRoot, "baseline", "baseline_manifest.json")

    if (-not (Test-Path -LiteralPath $targetDir -PathType Container)) {
        return @{ IsMatch = $false; Reason = "Target directory not found: $targetDir" }
    }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return @{ IsMatch = $false; Reason = "Baseline manifest not found: $manifestPath" }
    }

    $manifest = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($manifestPath))

    # Assert dxgi.dll is absent
    $dxgiPath = [System.IO.Path]::Combine($targetDir, "dxgi.dll")
    if (Test-Path -LiteralPath $dxgiPath) {
        return @{ IsMatch = $false; Reason = "dxgi.dll unexpectedly present in target directory." }
    }

    # Assert each baseline file matches
    foreach ($entry in $manifest.Files) {
        $filePath = [System.IO.Path]::Combine($targetDir, $entry.RelativePath)
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            return @{ IsMatch = $false; Reason = "Baseline file missing in target: $($entry.RelativePath)" }
        }

        $currentSize = (Get-Item -LiteralPath $filePath).Length
        if ($currentSize -ne $entry.SizeBytes) {
            return @{ IsMatch = $false; Reason = "Baseline file size mismatch for $($entry.RelativePath): expected $($entry.SizeBytes), got $currentSize" }
        }

        $currentHash = Compute-FileSha256 -FilePath $filePath
        if ($currentHash -ne $entry.Sha256) {
            return @{ IsMatch = $false; Reason = "Baseline file SHA256 mismatch for $($entry.RelativePath): expected $($entry.Sha256), got $currentHash" }
        }
    }

    # Assert no unexpected extra files exist in target
    $targetFiles = Get-ChildItem -LiteralPath $targetDir -File
    $expectedNames = @($manifest.Files | ForEach-Object { $_.RelativePath })
    foreach ($file in $targetFiles) {
        if ($expectedNames -notcontains $file.Name) {
            return @{ IsMatch = $false; Reason = "Unexpected file present in target: $($file.Name)" }
        }
    }

    return @{ IsMatch = $true; Reason = "Target strictly matches immutable baseline." }
}

function Normalize-InstallationRecord($inst) {
    if ($null -eq $inst) { return $null }

    $pendingType = "None"
    $targetVersion = $null
    if ($null -ne $inst.PendingAction) {
        $pendingType = "$($inst.PendingAction.Type)"
        $targetVersion = $inst.PendingAction.TargetDxvkVersion
    }

    $policyMode = "Disabled"
    $pinnedVersion = $null
    if ($null -ne $inst.ManagementPolicy) {
        $policyMode = "$($inst.ManagementPolicy.Mode)"
        $pinnedVersion = $inst.ManagementPolicy.PinnedDxvkVersion
    }

    return [PSCustomObject]@{
        Id                      = "$($inst.Id)"
        DisplayName             = "$($inst.DisplayName)"
        InstallationPath        = [System.IO.Path]::GetFullPath("$($inst.InstallationPath)").TrimEnd('\', '/')
        ManagementPolicyMode    = $policyMode
        PinnedVersion           = $pinnedVersion
        ManagedDxvkVersion      = if ($null -eq $inst.ManagedDxvkVersion) { $null } else { "$($inst.ManagedDxvkVersion)" }
        ManagedDxvkArchitecture = if ($null -eq $inst.ManagedDxvkArchitecture) { $null } else { "$($inst.ManagedDxvkArchitecture)" }
        RestorationState        = "$($inst.RestorationState)"
        ConflictFlags           = "$($inst.ConflictFlags)"
        PendingType             = $pendingType
        TargetVersion           = $targetVersion
    }
}

function Compare-SemanticLibraryJsonInternal([string]$JsonA, [string]$JsonB) {
    try {
        $objA = if (Test-Path -LiteralPath $JsonA -PathType Leaf) {
            ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($JsonA))
        } else {
            ConvertFrom-Json -InputObject $JsonA
        }

        $objB = if (Test-Path -LiteralPath $JsonB -PathType Leaf) {
            ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($JsonB))
        } else {
            ConvertFrom-Json -InputObject $JsonB
        }

        if ($objA.SchemaVersion -ne $objB.SchemaVersion) {
            return @{ IsEquivalent = $false; Reason = "SchemaVersion mismatch: $($objA.SchemaVersion) vs $($objB.SchemaVersion)" }
        }

        $listA = @($objA.Installations)
        $listB = @($objB.Installations)

        if ($listA.Count -ne $listB.Count) {
            return @{ IsEquivalent = $false; Reason = "Installations count mismatch: $($listA.Count) vs $($listB.Count)" }
        }

        for ($i = 0; $i -lt $listA.Count; $i++) {
            $normA = Normalize-InstallationRecord $listA[$i]
            $normB = Normalize-InstallationRecord $listB[$i]

            foreach ($prop in @("DisplayName", "InstallationPath", "ManagementPolicyMode", "PinnedVersion", "ManagedDxvkVersion", "ManagedDxvkArchitecture", "RestorationState", "ConflictFlags", "PendingType", "TargetVersion")) {
                $valA = $normA.$prop
                $valB = $normB.$prop
                if ($valA -ne $valB) {
                    return @{ IsEquivalent = $false; Reason = "Property '$prop' mismatch in installation $($normA.Id): '$valA' vs '$valB'" }
                }
            }
        }

        return @{ IsEquivalent = $true; Reason = "Library JSON structures are semantically equivalent." }
    } catch {
        return @{ IsEquivalent = $false; Reason = "Semantic comparison failed with exception: $_" }
    }
}

# ==============================================================================
# Action Implementations
# ==============================================================================

function Invoke-SetupAction {
    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
        throw "FixtureRoot parameter is required for Setup."
    }
    if ([string]::IsNullOrWhiteSpace($HelperZipPath)) {
        throw "HelperZipPath parameter is required for Setup."
    }
    if ([string]::IsNullOrWhiteSpace($CompanionZipPath)) {
        throw "CompanionZipPath parameter is required for Setup."
    }

    $rootFull = [System.IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\', '/')

    # Refuse pre-existing non-empty directory
    if (Test-Path -LiteralPath $rootFull) {
        $existingItems = Get-ChildItem -LiteralPath $rootFull -Force
        if ($existingItems.Count -gt 0) {
            throw "Setup refused: FixtureRoot already exists and is not empty: $rootFull"
        }
    } else {
        [void][System.IO.Directory]::CreateDirectory($rootFull)
    }

    # Parse TestPins if supplied
    $testPins = $null
    $testPinsUsed = $false
    if (-not [string]::IsNullOrWhiteSpace($TestPinsJson)) {
        $testPins = ConvertFrom-Json -InputObject $TestPinsJson
        $testPinsUsed = $true
    }

    # Verify Helper Zip Hash
    $expectedHelperZipHash = if ($testPinsUsed -and $testPins.HelperZipSha256) { $testPins.HelperZipSha256 } else { $script:FROZEN_HELPER_ZIP_SHA256 }
    $actualHelperZipHash = Compute-FileSha256 -FilePath $HelperZipPath
    if ($actualHelperZipHash -ne $expectedHelperZipHash) {
        throw "Helper archive SHA256 mismatch: expected $expectedHelperZipHash, got $actualHelperZipHash"
    }

    # Verify Companion Zip Hash
    $expectedCompanionZipHash = if ($testPinsUsed -and $testPins.CompanionZipSha256) { $testPins.CompanionZipSha256 } else { $script:FROZEN_COMPANION_ZIP_SHA256 }
    $actualCompanionZipHash = Compute-FileSha256 -FilePath $CompanionZipPath
    if ($actualCompanionZipHash -ne $expectedCompanionZipHash) {
        throw "Companion archive SHA256 mismatch: expected $expectedCompanionZipHash, got $actualCompanionZipHash"
    }

    # Create subdirectories
    $targetDir = [System.IO.Path]::Combine($rootFull, "target")
    $controlDir = [System.IO.Path]::Combine($rootFull, "control")
    $baselineDir = [System.IO.Path]::Combine($rootFull, "baseline")
    $companionDir = [System.IO.Path]::Combine($rootFull, "companion")
    $runtimeDir = [System.IO.Path]::Combine($rootFull, "runtime")
    $snapshotsDir = [System.IO.Path]::Combine($rootFull, "snapshots")
    $logsDir = [System.IO.Path]::Combine($rootFull, "logs")
    $profilesDir = [System.IO.Path]::Combine($companionDir, "Profiles")
    $backupsDir = [System.IO.Path]::Combine($profilesDir, "Backups")

    foreach ($dir in @($targetDir, $controlDir, $baselineDir, $companionDir, $runtimeDir, $snapshotsDir, $logsDir, $profilesDir, $backupsDir)) {
        if (-not (Test-Path -LiteralPath $dir)) {
            [void][System.IO.Directory]::CreateDirectory($dir)
        }
    }

    # Extract helper archive safely
    $helperTempDir = [System.IO.Path]::Combine($rootFull, "helper_temp")
    Expand-SafeZipArchiveInternal -ZipFilePath $HelperZipPath -DestinationDirectory $helperTempDir
    try {
        $extractedHelperExe = [System.IO.Path]::Combine($helperTempDir, "QueueFixture.exe")
        if (-not (Test-Path -LiteralPath $extractedHelperExe -PathType Leaf)) {
            throw "Extracted helper archive does not contain QueueFixture.exe"
        }

        # Verify extracted helper executable hash
        $expectedHelperExeHash = if ($testPinsUsed -and $testPins.HelperExeSha256) { $testPins.HelperExeSha256 } else { $script:FROZEN_HELPER_EXE_SHA256 }
        $actualHelperExeHash = Compute-FileSha256 -FilePath $extractedHelperExe
        if ($actualHelperExeHash -ne $expectedHelperExeHash) {
            throw "Extracted QueueFixture.exe SHA256 mismatch: expected $expectedHelperExeHash, got $actualHelperExeHash"
        }

        # Copy QueueFixture.exe to target
        $targetHelperExe = [System.IO.Path]::Combine($targetDir, "QueueFixture.exe")
        [System.IO.File]::Copy($extractedHelperExe, $targetHelperExe, $true)
    } finally {
        if (Test-Path -LiteralPath $helperTempDir) {
            [System.IO.Directory]::Delete($helperTempDir, $true)
        }
    }

    # Extract companion archive safely into companionDir
    Expand-SafeZipArchiveInternal -ZipFilePath $CompanionZipPath -DestinationDirectory $companionDir
    $companionExe = [System.IO.Path]::Combine($companionDir, "DXVK-Companion.exe")
    if (-not (Test-Path -LiteralPath $companionExe -PathType Leaf)) {
        throw "Extracted companion archive does not contain DXVK-Companion.exe"
    }

    # Verify extracted companion executable hash
    $expectedCompanionExeHash = if ($testPinsUsed -and $testPins.CompanionExeSha256) { $testPins.CompanionExeSha256 } else { $script:FROZEN_COMPANION_EXE_SHA256 }
    $actualCompanionExeHash = Compute-FileSha256 -FilePath $companionExe
    if ($actualCompanionExeHash -ne $expectedCompanionExeHash) {
        throw "Extracted DXVK-Companion.exe SHA256 mismatch: expected $expectedCompanionExeHash, got $actualCompanionExeHash"
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
        [System.IO.File]::Copy($src, $dst, $true)

        $manifestFiles += [PSCustomObject]@{
            RelativePath = $item
            SizeBytes    = (Get-Item -LiteralPath $src).Length
            Sha256       = Compute-FileSha256 -FilePath $src
        }
    }

    $baselineManifest = [PSCustomObject]@{
        CreatedAtUtc = [DateTime]::UtcNow.ToString("o")
        DxgiAbsent   = $true
        Files        = $manifestFiles
    }
    $manifestJson = ConvertTo-Json -InputObject $baselineManifest -Depth 5
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($baselineDir, "baseline_manifest.json"), $manifestJson, [System.Text.Encoding]::UTF8)

    # Seed Companion Storage: settings.json and game-library.json
    $settingsPath = [System.IO.Path]::Combine($companionDir, "settings.json")
    $settingsObj = [PSCustomObject]@{
        GlobalPolicy              = "Manual"
        AutoEnableDxvkForNewGames = $false
        LaunchOnStartup           = $false
    }
    [System.IO.File]::WriteAllText($settingsPath, (ConvertTo-Json -InputObject $settingsObj), [System.Text.Encoding]::UTF8)

    $gameLibraryPath = [System.IO.Path]::Combine($profilesDir, "game-library.json")
    $installationRecord = [PSCustomObject]@{
        Id                      = "queue-fixture-trial-target"
        InstallationPath        = $targetDir
        DisplayName             = "QueueFixture Trial Target"
        IsHidden                = $false
        ManagementPolicy        = [PSCustomObject]@{
            Mode              = "Disabled"
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
                DetectionEvidence      = @("Explicit seeded process-lifecycle trial target")
            }
        )
        ManagedFiles            = @()
        PendingAction           = $null
        LastCancelledAction     = $null
        LastCancellationReason  = $null
        LastCancellationOutcome = $null
        LastRefusalReason       = $null
        CreatedUtc              = [DateTime]::UtcNow.ToString("o")
        LastSeenUtc             = $null
    }

    $libraryObj = [PSCustomObject]@{
        SchemaVersion = 1
        Installations = @($installationRecord)
    }
    [System.IO.File]::WriteAllText($gameLibraryPath, (ConvertTo-Json -InputObject $libraryObj -Depth 10), [System.Text.Encoding]::UTF8)

    # Initialize state.json
    $initialState = [PSCustomObject]@{
        BatchStartTimeUtc      = [DateTime]::UtcNow.ToString("o")
        HelperStartCount       = 0
        CompanionStartCount    = 0
        MaxHelperStarts        = $script:MAX_HELPER_STARTS
        MaxCompanionStarts     = $script:MAX_COMPANION_STARTS
        MaxDurationSeconds     = $script:MAX_TRIAL_DURATION_SECONDS
        CurrentCheckpoint      = "Setup"
        LastCheckpointOutcome  = "Success"
        IsEnded                = $false
        TestPinsUsed           = $testPinsUsed
        ActiveHelperPid        = $null
        ActiveCompanionPid     = $null
        Checkpoints            = @("Setup")
    }
    Save-StateData -FixtureRoot $rootFull -StateObject $initialState

    return @{
        Success = $true
        Message = "Setup completed successfully."
        FixtureRoot = $rootFull
        TestPinsUsed = $testPinsUsed
    }
}

function Invoke-StartHelperAction {
    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { throw "FixtureRoot parameter is required." }
    if ($Slot -lt 1 -or $Slot -gt 4) { throw "Slot must be between 1 and 4." }

    $guard = Test-BatchGuardInternal -FixtureRoot $FixtureRoot -Slot $Slot -IsCompanion $false -Attestation $AdoptionAttestation
    if (-not $guard.CanProceed) {
        throw "Start-Helper refused: $($guard.Reason)"
    }

    $targetDir = [System.IO.Path]::Combine($FixtureRoot, "target")
    $helperExe = [System.IO.Path]::Combine($targetDir, "QueueFixture.exe")

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

    # Bounded wait for READY signal on standard output
    $readyTask = $proc.StandardOutput.ReadLineAsync()
    if (-not $readyTask.Wait(5000)) {
        try { $proc.Kill() } catch { }
        throw "Helper process failed to emit READY within 5000ms."
    }

    $firstLine = $readyTask.Result
    if ($firstLine -ne "READY") {
        throw "Helper process emitted unexpected output: '$firstLine' (expected 'READY')."
    }

    # Update state
    $state = Get-StateData -FixtureRoot $FixtureRoot
    $state.HelperStartCount = [int]$state.HelperStartCount + 1
    $state.ActiveHelperPid = $proc.Id
    Save-StateData -FixtureRoot $FixtureRoot -StateObject $state

    return @{
        Success = $true
        Message = "Helper started successfully in slot $Slot with PID $($proc.Id)."
        Pid = $proc.Id
        Slot = $Slot
    }
}

function Invoke-StopHelperAction {
    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { throw "FixtureRoot parameter is required." }
    if ($Slot -lt 1 -or $Slot -gt 4) { throw "Slot must be between 1 and 4." }

    $controlDir = [System.IO.Path]::Combine($FixtureRoot, "control")
    $markerPath = [System.IO.Path]::Combine($controlDir, "stop-$Slot.request")

    if (Test-Path -LiteralPath $markerPath) {
        throw "Stop marker already exists: $markerPath"
    }

    # Create stop marker regular file
    [System.IO.File]::WriteAllText($markerPath, "STOP`n", [System.Text.Encoding]::ASCII)

    # If helper process is recorded in state, wait for exit
    $state = Get-StateData -FixtureRoot $FixtureRoot
    $pid = $state.ActiveHelperPid
    $exitCode = -1

    if ($null -ne $pid -and $pid -gt 0) {
        try {
            $proc = [System.Diagnostics.Process]::GetProcessById($pid)
            if (-not $proc.WaitForExit(10000)) {
                throw "Helper process ($pid) failed to exit within 10000ms after stop marker created."
            }
            $exitCode = $proc.ExitCode
        } catch [System.ArgumentException] {
            # Already exited
            $exitCode = 0
        }
    }

    $state.ActiveHelperPid = $null
    Save-StateData -FixtureRoot $FixtureRoot -StateObject $state

    if ($exitCode -ne 0 -and $exitCode -ne -1) {
        throw "Helper process exited with non-zero code: $exitCode (expected 0)."
    }

    return @{
        Success = $true
        Message = "Stop marker created and helper confirmed stopped."
        Slot = $Slot
        ExitCode = $exitCode
    }
}

function Invoke-StartCompanionAction {
    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { throw "FixtureRoot parameter is required." }

    $guard = Test-BatchGuardInternal -FixtureRoot $FixtureRoot -IsCompanion $true -Attestation $AdoptionAttestation
    if (-not $guard.CanProceed) {
        throw "Start-Companion refused: $($guard.Reason)"
    }

    $companionDir = [System.IO.Path]::Combine($FixtureRoot, "companion")
    $companionExe = [System.IO.Path]::Combine($companionDir, "DXVK-Companion.exe")
    $runtimeDir = [System.IO.Path]::Combine($FixtureRoot, "runtime")

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $companionExe
    $psi.Arguments = if ($NoAutoDiscovery) { "--no-auto-discovery" } else { "" }
    $psi.WorkingDirectory = $companionDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $false

    # Set child environment override for single-file bundle extraction
    $psi.EnvironmentVariables["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = $runtimeDir

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    if (-not $proc.Start()) {
        throw "Failed to start Companion process."
    }

    $state = Get-StateData -FixtureRoot $FixtureRoot
    $state.CompanionStartCount = [int]$state.CompanionStartCount + 1
    $state.ActiveCompanionPid = $proc.Id
    Save-StateData -FixtureRoot $FixtureRoot -StateObject $state

    return @{
        Success = $true
        Message = "Companion started successfully with PID $($proc.Id)."
        Pid = $proc.Id
    }
}

function Invoke-TakeSnapshotAction {
    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { throw "FixtureRoot parameter is required." }
    if ([string]::IsNullOrWhiteSpace($CheckpointName)) { throw "CheckpointName parameter is required." }

    $snapshotsDir = [System.IO.Path]::Combine($FixtureRoot, "snapshots")
    $checkpointDir = [System.IO.Path]::Combine($snapshotsDir, $CheckpointName)

    if (Test-Path -LiteralPath $checkpointDir) {
        throw "Snapshot checkpoint directory already exists: $checkpointDir"
    }
    [void][System.IO.Directory]::CreateDirectory($checkpointDir)

    $targetDir = [System.IO.Path]::Combine($FixtureRoot, "target")
    $profilesDir = [System.IO.Path]::Combine($FixtureRoot, "companion", "Profiles")
    $controlDir = [System.IO.Path]::Combine($FixtureRoot, "control")

    $snapTarget = [System.IO.Path]::Combine($checkpointDir, "target")
    $snapProfiles = [System.IO.Path]::Combine($checkpointDir, "Profiles")
    $snapControl = [System.IO.Path]::Combine($checkpointDir, "control")

    # Copy trees
    Copy-Item -LiteralPath $targetDir -Destination $snapTarget -Recurse -Force
    Copy-Item -LiteralPath $profilesDir -Destination $snapProfiles -Recurse -Force
    Copy-Item -LiteralPath $controlDir -Destination $snapControl -Recurse -Force

    # Compute manifest of all files in snapshot
    $files = Get-ChildItem -LiteralPath $checkpointDir -Recurse -File
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
        CheckpointName = $CheckpointName
        TakenAtUtc     = [DateTime]::UtcNow.ToString("o")
        Files          = $fileRecords
    }

    $manifestPath = [System.IO.Path]::Combine($checkpointDir, "manifest.json")
    [System.IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $snapshotManifest -Depth 5), [System.Text.Encoding]::UTF8)

    # Record checkpoint in state
    $state = Get-StateData -FixtureRoot $FixtureRoot
    $state.CurrentCheckpoint = $CheckpointName
    $checkpointsList = @($state.Checkpoints)
    if ($checkpointsList -notcontains $CheckpointName) {
        $checkpointsList += $CheckpointName
    }
    $state.Checkpoints = $checkpointsList
    Save-StateData -FixtureRoot $FixtureRoot -StateObject $state

    return @{
        Success = $true
        Message = "Snapshot '$CheckpointName' captured successfully."
        CheckpointDirectory = $checkpointDir
        TotalFiles = $fileRecords.Count
    }
}

function Invoke-VerifyCheckpointAction {
    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { throw "FixtureRoot parameter is required." }
    if ([string]::IsNullOrWhiteSpace($CheckpointName)) { throw "CheckpointName parameter is required." }
    if ([string]::IsNullOrWhiteSpace($ExpectedType)) { throw "ExpectedType parameter is required (Pending, Applied, Restored)." }

    $targetDir = [System.IO.Path]::Combine($FixtureRoot, "target")
    $gameLibraryPath = [System.IO.Path]::Combine($FixtureRoot, "companion", "Profiles", "game-library.json")

    if (-not (Test-Path -LiteralPath $gameLibraryPath -PathType Leaf)) {
        throw "game-library.json not found: $gameLibraryPath"
    }
    $library = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($gameLibraryPath))
    $inst = $library.Installations[0]

    switch ($ExpectedType) {
        "Pending" {
            # Target files must match unmodified baseline
            $baseCheck = Compare-TargetBaselineInternal -FixtureRoot $FixtureRoot
            if (-not $baseCheck.IsMatch) {
                throw "Pending checkpoint verification failed: $($baseCheck.Reason)"
            }
            if ($null -eq $inst.PendingAction) {
                throw "Pending checkpoint verification failed: no PendingAction recorded in game-library.json."
            }
            if (-not [string]::IsNullOrEmpty($ExpectedVersion) -and $inst.PendingAction.TargetDxvkVersion -ne $ExpectedVersion) {
                throw "Pending checkpoint verification failed: expected version '$ExpectedVersion', got '$($inst.PendingAction.TargetDxvkVersion)'."
            }
        }
        "Applied" {
            $dxgiPath = [System.IO.Path]::Combine($targetDir, "dxgi.dll")
            $d3d11Path = [System.IO.Path]::Combine($targetDir, "d3d11.dll")
            if (-not (Test-Path -LiteralPath $dxgiPath -PathType Leaf)) {
                throw "Applied verification failed: dxgi.dll is absent from target."
            }
            if (-not (Test-Path -LiteralPath $d3d11Path -PathType Leaf)) {
                throw "Applied verification failed: d3d11.dll is absent from target."
            }
            if (-not [string]::IsNullOrEmpty($ExpectedVersion) -and $inst.ManagedDxvkVersion -ne $ExpectedVersion) {
                throw "Applied verification failed: expected managed version '$ExpectedVersion', got '$($inst.ManagedDxvkVersion)'."
            }
            if ($null -ne $inst.PendingAction) {
                throw "Applied verification failed: pending action still present after exit dispatch."
            }
        }
        "Restored" {
            $baseCheck = Compare-TargetBaselineInternal -FixtureRoot $FixtureRoot
            if (-not $baseCheck.IsMatch) {
                throw "Restored checkpoint verification failed: $($baseCheck.Reason)"
            }
            if ($null -ne $inst.ManagedDxvkVersion) {
                throw "Restored checkpoint verification failed: ManagedDxvkVersion is '$($inst.ManagedDxvkVersion)' (expected null)."
            }
            if ($null -ne $inst.PendingAction) {
                throw "Restored checkpoint verification failed: lingering PendingAction found."
            }
        }
        Default {
            throw "Unrecognized ExpectedType: $ExpectedType"
        }
    }

    return @{
        Success = $true
        Message = "Checkpoint '$CheckpointName' verified successfully as $ExpectedType."
        CheckpointName = $CheckpointName
        ExpectedType = $ExpectedType
    }
}

function Invoke-StatusAction {
    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { throw "FixtureRoot parameter is required." }
    $state = Get-StateData -FixtureRoot $FixtureRoot
    return @{
        Success = $true
        Message = "Status retrieved."
        State = $state
    }
}

function Invoke-EndBatchAction {
    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { throw "FixtureRoot parameter is required." }
    $state = Get-StateData -FixtureRoot $FixtureRoot
    $state.IsEnded = $true
    Save-StateData -FixtureRoot $FixtureRoot -StateObject $state
    return @{
        Success = $true
        Message = "Batch marked as ended. No further starts permitted."
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
        "Start-Helper" {
            $result = Invoke-StartHelperAction
        }
        "Stop-Helper" {
            $result = Invoke-StopHelperAction
        }
        "Start-Companion" {
            $result = Invoke-StartCompanionAction
        }
        "Take-Snapshot" {
            $result = Invoke-TakeSnapshotAction
        }
        "Verify-Checkpoint" {
            $result = Invoke-VerifyCheckpointAction
        }
        "Status" {
            $result = Invoke-StatusAction
        }
        "End-Batch" {
            $result = Invoke-EndBatchAction
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
