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
    [switch]$PureDataCheck,

    [Parameter(Mandatory = $false)]
    [switch]$JsonOutput
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue

if (-not ([System.Management.Automation.PSTypeName]"DXVKCompanion.Validation.SafeProcessStreamDrainer").Type) {
    Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DXVKCompanion.Validation
{
    public sealed class SafeProcessStreamDrainer : IDisposable
    {
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly CancellationTokenSource _cts;
        private Task _drainTask;

        public volatile bool ReadyReceived;
        public volatile bool StoppedReceived;
        public int LinesRead;

        public SafeProcessStreamDrainer(StreamReader reader, string logFilePath, Encoding encoding)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            if (string.IsNullOrEmpty(logFilePath)) throw new ArgumentNullException("logFilePath");

            _reader = reader;
            var dir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var fs = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, encoding ?? new UTF8Encoding(false)) { AutoFlush = true };
            _cts = new CancellationTokenSource();
        }

        public SafeProcessStreamDrainer(Stream inputStream, string logFilePath, Encoding encoding)
            : this(OpenStreamReader(inputStream, encoding), logFilePath, encoding)
        {
        }

        private static StreamReader OpenStreamReader(Stream inputStream, Encoding encoding)
        {
            if (inputStream == null) throw new ArgumentNullException("inputStream");
            return new StreamReader(inputStream, encoding ?? Encoding.UTF8);
        }

        public void Start()
        {
            _drainTask = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        Interlocked.Increment(ref LinesRead);
                        if (line.IndexOf("READY", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            ReadyReceived = true;
                        }
                        if (line.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            StoppedReceived = true;
                        }
                        await _writer.WriteLineAsync(line).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Stream closed, process exited, or drain canceled
                }
            });
        }

        public bool WaitForDrain(int timeoutMs)
        {
            if (_drainTask == null) return true;
            try
            {
                return _drainTask.Wait(timeoutMs);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            try { if (_cts != null) _cts.Cancel(); } catch {}
            try { if (_drainTask != null) _drainTask.Wait(1000); } catch {}
            try { if (_writer != null) _writer.Dispose(); } catch {}
            try { if (_reader != null) _reader.Dispose(); } catch {}
            try { if (_cts != null) _cts.Dispose(); } catch {}
        }
    }
}
"@ -Language CSharp
}

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

# Retained Process Handles, Stream Drainers, and Supervisor State
$script:HelperProcess = $null
$script:CompanionProcess = $null
$script:HelperStdoutDrainer = $null
$script:HelperStderrDrainer = $null
$script:CompanionStdoutDrainer = $null
$script:CompanionStderrDrainer = $null
$script:SupervisorMonotonicTimer = $null
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false

function Get-SafePropertyValue($Object, [string]$PropertyName) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Get-SafeCount($Value) {
    if ($null -eq $Value) { return 0 }
    if ($Value -is [System.Array]) { return $Value.Length }
    if ($Value -is [System.Collections.ICollection]) { return $Value.Count }
    return 1
}

function Get-SafeEnumerable($Value) {
    if ($null -eq $Value) { return [System.Collections.ArrayList]::new() }
    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        return $Value
    }
    $list = [System.Collections.ArrayList]::new()
    [void]$list.Add($Value)
    return $list
}

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
    $raw = [System.IO.File]::ReadAllText($statePath, $script:Utf8NoBom)
    return ConvertFrom-Json -InputObject $raw
}

function Save-StateData([string]$FixtureRoot, $StateObject) {
    $statePath = Get-StateFilePath -FixtureRoot $FixtureRoot
    $json = ConvertTo-Json -InputObject $StateObject -Depth 10
    [System.IO.File]::WriteAllText($statePath, $json, $script:Utf8NoBom)
}

function Invoke-SuperviseObservationCycle([string]$FixtureRoot) {
    $canon = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    $state = Get-StateData -FixtureRoot $canon

    # 1. Monotonic elapsed accounting
    if ($null -eq $script:SupervisorMonotonicTimer) {
        $script:SupervisorMonotonicTimer = [System.Diagnostics.Stopwatch]::StartNew()
    }
    $state.ElapsedMilliseconds = [long]($state.SetupElapsedMilliseconds + $script:SupervisorMonotonicTimer.ElapsedMilliseconds)
    $elapsedSeconds = [long]($state.ElapsedMilliseconds / 1000)

    if ($elapsedSeconds -gt $script:MAX_TRIAL_DURATION_SECONDS) {
        $state.LastCheckpointOutcome = "Failed"
        $state.LastFailedCheckpoint = "TrialDurationExhausted"
        $state.LastFailedReason = "Maximum trial duration ($($script:MAX_TRIAL_DURATION_SECONDS)s) exceeded."
        Save-StateData -FixtureRoot $canon -StateObject $state
        throw "Trial duration budget exceeded: $($elapsedSeconds)s > $($script:MAX_TRIAL_DURATION_SECONDS)s."
    }

    # 2. Sample active child processes (CPU time & working set)
    $activeProcesses = @()
    if ($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) {
        try {
            $script:HelperProcess.Refresh()
            $cpuMs = $script:HelperProcess.TotalProcessorTime.TotalMilliseconds
            $wsBytes = $script:HelperProcess.WorkingSet64
            $peakWs = $script:HelperProcess.PeakWorkingSet64
            $activeProcesses += [PSCustomObject]@{
                Role = "Helper"
                Slot = $state.ActiveHelperSlot
                Pid = $script:HelperProcess.Id
                TotalCpuMilliseconds = $cpuMs
                WorkingSetBytes = $wsBytes
                PeakWorkingSetBytes = $peakWs
                SampledAtUtc = [DateTime]::UtcNow.ToString("o")
            }
        } catch {}
    }

    if ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited) {
        try {
            $script:CompanionProcess.Refresh()
            $cpuMs = $script:CompanionProcess.TotalProcessorTime.TotalMilliseconds
            $wsBytes = $script:CompanionProcess.WorkingSet64
            $peakWs = $script:CompanionProcess.PeakWorkingSet64
            $activeProcesses += [PSCustomObject]@{
                Role = "Companion"
                Pid = $script:CompanionProcess.Id
                TotalCpuMilliseconds = $cpuMs
                WorkingSetBytes = $wsBytes
                PeakWorkingSetBytes = $peakWs
                SampledAtUtc = [DateTime]::UtcNow.ToString("o")
            }
        } catch {}
    }
    $state.ActiveProcesses = $activeProcesses

    # 3. Fail-closed target & settings validation
    $companionSettingsPath = [System.IO.Path]::Combine($canon, "companion", "settings.json")
    if (Test-Path -LiteralPath $companionSettingsPath -PathType Leaf) {
        $settingsRaw = [System.IO.File]::ReadAllText($companionSettingsPath, $script:Utf8NoBom)
        $settingsObj = ConvertFrom-Json -InputObject $settingsRaw
        if ("$($settingsObj.GlobalPolicy)" -ne "Manual") {
            $state.LastCheckpointOutcome = "Failed"
            $state.LastFailedCheckpoint = "SettingsTampered"
            $state.LastFailedReason = "Settings GlobalPolicy altered from Manual to $($settingsObj.GlobalPolicy)."
            Save-StateData -FixtureRoot $canon -StateObject $state
            throw "Settings tampered: GlobalPolicy is '$($settingsObj.GlobalPolicy)' (expected 'Manual')."
        }
    }

    # 4. Current readiness evaluation
    $isReady = $true
    if ($state.LastCheckpointOutcome -eq "Failed") {
        $isReady = $false
    }
    $state.CurrentReadiness = if ($isReady) { "Ready" } else { "NotReady" }

    Save-StateData -FixtureRoot $canon -StateObject $state
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

    $manifest = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($manifestPath, $script:Utf8NoBom))

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
    $targetFiles = @(Get-ChildItem -LiteralPath $targetDir -Recurse -File -Force)
    $expectedNames = @($manifest.Files | ForEach-Object { $_.RelativePath.Replace('/', '\') })
    foreach ($file in $targetFiles) {
        $rel = $file.FullName.Substring($targetDir.Length).TrimStart('\', '/')
        if ($expectedNames -notcontains $rel) {
            return @{ IsMatch = $false; Reason = "Unexpected file present in target: $rel" }
        }
    }

    # Assert no unexpected directories exist in target
    $targetDirs = @(Get-ChildItem -LiteralPath $targetDir -Recurse -Directory -Force)
    if ((Get-SafeCount $targetDirs) -gt 0) {
        $firstDir = $targetDirs[0]
        return @{ IsMatch = $false; Reason = "Unexpected directory present in target: $($firstDir.FullName)" }
    }

    return @{ IsMatch = $true; Reason = "Target strictly matches immutable baseline." }
}

function Compare-TargetManagedReferenceInternal([string]$FixtureRoot, [string]$ManagedVersion) {
    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    $targetDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "target"))
    $refDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "reference", $ManagedVersion, "x64"))
    $baselineDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "baseline"))

    if (-not (Test-Path -LiteralPath $targetDir -PathType Container)) {
        return @{ IsMatch = $false; Reason = "Target directory not found: $targetDir" }
    }
    if (-not (Test-Path -LiteralPath $refDir -PathType Container)) {
        return @{ IsMatch = $false; Reason = "Official reference directory not found for $ManagedVersion at: $refDir" }
    }

    $refD3d11 = [System.IO.Path]::Combine($refDir, "d3d11.dll")
    $refDxgi = [System.IO.Path]::Combine($refDir, "dxgi.dll")
    if (-not (Test-Path -LiteralPath $refD3d11 -PathType Leaf)) {
        return @{ IsMatch = $false; Reason = "Official reference d3d11.dll not found for $ManagedVersion" }
    }
    if (-not (Test-Path -LiteralPath $refDxgi -PathType Leaf)) {
        return @{ IsMatch = $false; Reason = "Official reference dxgi.dll not found for $ManagedVersion" }
    }

    $targetD3d11 = [System.IO.Path]::Combine($targetDir, "d3d11.dll")
    $targetDxgi = [System.IO.Path]::Combine($targetDir, "dxgi.dll")
    if (-not (Test-Path -LiteralPath $targetD3d11 -PathType Leaf)) {
        return @{ IsMatch = $false; Reason = "d3d11.dll missing in target" }
    }
    if (-not (Test-Path -LiteralPath $targetDxgi -PathType Leaf)) {
        return @{ IsMatch = $false; Reason = "dxgi.dll missing in target" }
    }

    $hashTargetD3d11 = Compute-FileSha256 -FilePath $targetD3d11
    $hashRefD3d11 = Compute-FileSha256 -FilePath $refD3d11
    if ($hashTargetD3d11 -ne $hashRefD3d11) {
        return @{ IsMatch = $false; Reason = "Target d3d11.dll SHA256 does not match official $ManagedVersion reference." }
    }

    $hashTargetDxgi = Compute-FileSha256 -FilePath $targetDxgi
    $hashRefDxgi = Compute-FileSha256 -FilePath $refDxgi
    if ($hashTargetDxgi -ne $hashRefDxgi) {
        return @{ IsMatch = $false; Reason = "Target dxgi.dll SHA256 does not match official $ManagedVersion reference." }
    }

    # Verify native baseline files: QueueFixture.exe, dxvk.conf, sentinel.txt
    foreach ($item in @("QueueFixture.exe", "dxvk.conf", "sentinel.txt")) {
        $tPath = [System.IO.Path]::Combine($targetDir, $item)
        $bPath = [System.IO.Path]::Combine($baselineDir, $item)
        if (-not (Test-Path -LiteralPath $tPath -PathType Leaf)) {
            return @{ IsMatch = $false; Reason = "Baseline file missing in target: $item" }
        }
        $tHash = Compute-FileSha256 -FilePath $tPath
        $bHash = Compute-FileSha256 -FilePath $bPath
        if ($tHash -ne $bHash) {
            return @{ IsMatch = $false; Reason = "Target $item does not match immutable baseline." }
        }
    }

    # Check for unexpected extra files in target
    $targetFiles = @(Get-ChildItem -LiteralPath $targetDir -Recurse -File -Force)
    $expectedNames = @("QueueFixture.exe", "d3d11.dll", "dxgi.dll", "dxvk.conf", "sentinel.txt")
    foreach ($f in $targetFiles) {
        $rel = $f.FullName.Substring($targetDir.Length).TrimStart('\', '/')
        if ($expectedNames -notcontains $rel) {
            return @{ IsMatch = $false; Reason = "Unexpected file present in managed target: $rel" }
        }
    }

    # Check for unexpected directories in target
    $targetDirs = @(Get-ChildItem -LiteralPath $targetDir -Recurse -Directory -Force)
    if ((Get-SafeCount $targetDirs) -gt 0) {
        $firstDir = $targetDirs[0]
        return @{ IsMatch = $false; Reason = "Unexpected directory present in target: $($firstDir.FullName)" }
    }

    return @{ IsMatch = $true; Reason = "Target strictly matches managed $ManagedVersion reference and baseline." }
}

function Compare-SemanticLibraryJsonInternal([string]$JsonA, [string]$JsonB) {
    try {
        $objA = if (Test-Path -LiteralPath $JsonA -PathType Leaf) {
            ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($JsonA, $script:Utf8NoBom))
        } else {
            ConvertFrom-Json -InputObject $JsonA
        }

        $objB = if (Test-Path -LiteralPath $JsonB -PathType Leaf) {
            ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($JsonB, $script:Utf8NoBom))
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
        $countA = Get-SafeCount $listRawA
        $countB = Get-SafeCount $listRawB

        if ($countA -ne $countB) {
            return @{ IsEquivalent = $false; Reason = "Installations count mismatch: $countA vs $countB" }
        }

        # Index by stable Id
        $mapB = @{}
        foreach ($inst in (Get-SafeEnumerable $listRawB)) {
            $instId = Get-SafePropertyValue -Object $inst -PropertyName "Id"
            $mapB["$instId"] = $inst
        }

        foreach ($instA in (Get-SafeEnumerable $listRawA)) {
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
            $pinA = if ($null -eq $policyA) { "" } else { "$(Get-SafePropertyValue -Object $policyA -PropertyName 'PinnedDxvkVersion')" }
            $pinB = if ($null -eq $policyB) { "" } else { "$(Get-SafePropertyValue -Object $policyB -PropertyName 'PinnedDxvkVersion')" }
            if ($pinA -ne $pinB) {
                return @{ IsEquivalent = $false; Reason = "ManagementPolicy.PinnedDxvkVersion mismatch on '$key': '$pinA' vs '$pinB'" }
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
            $countExecA = Get-SafeCount $execsRawA
            $countExecB = Get-SafeCount $execsRawB
            if ($countExecA -ne $countExecB) {
                return @{ IsEquivalent = $false; Reason = "Executables count mismatch on '$key'." }
            }
            $execMapB = @{}
            foreach ($e in (Get-SafeEnumerable $execsRawB)) {
                $rel = Get-SafePropertyValue -Object $e -PropertyName "RelativePath"
                $execMapB["$rel"] = $e
            }
            foreach ($eA in (Get-SafeEnumerable $execsRawA)) {
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
            $countMfA = Get-SafeCount $mFilesRawA
            $countMfB = Get-SafeCount $mFilesRawB
            if ($countMfA -ne $countMfB) {
                return @{ IsEquivalent = $false; Reason = "ManagedFiles count mismatch on '$key': $countMfA vs $countMfB." }
            }

            $mfMapB = @{}
            foreach ($mf in (Get-SafeEnumerable $mFilesRawB)) {
                $relKey = "$(Get-SafePropertyValue -Object $mf -PropertyName 'RelativePath')".Replace('/', '\').TrimStart('\').ToLowerInvariant()
                $mfMapB[$relKey] = $mf
            }

            foreach ($mfA in (Get-SafeEnumerable $mFilesRawA)) {
                $mfKey = "$(Get-SafePropertyValue -Object $mfA -PropertyName 'RelativePath')".Replace('/', '\').TrimStart('\').ToLowerInvariant()
                if (-not $mfMapB.ContainsKey($mfKey)) {
                    return @{ IsEquivalent = $false; Reason = "ManagedFile '$mfKey' missing on '$key'." }
                }
                $mfB = $mfMapB[$mfKey]

                $mfProps = @(
                    "OriginalState", "BackupRelativePath", "OriginalSha256",
                    "ExpectedManagedSha256", "ManagedDxvkVersion", "OriginalDxvkVersion",
                    "CurrentState"
                )
                foreach ($mfProp in $mfProps) {
                    $rawA = Get-SafePropertyValue -Object $mfA -PropertyName $mfProp
                    $rawB = Get-SafePropertyValue -Object $mfB -PropertyName $mfProp
                    $vA = if ($null -eq $rawA) { "" } else { "$rawA" }
                    $vB = if ($null -eq $rawB) { "" } else { "$rawB" }
                    if ($vA -ne $vB) {
                        return @{ IsEquivalent = $false; Reason = "ManagedFile '$mfKey' property '$mfProp' mismatch on '$key': '$vA' vs '$vB'." }
                    }
                }
            }
        }

        return @{ IsEquivalent = $true; Reason = "Library JSON structures are semantically equivalent." }
    } catch {
        return @{ IsEquivalent = $false; Reason = "Semantic comparison failed with exception: $_" }
    }
}

function Extract-CompanionProvenance(
    [string]$ProvenancePath,
    [string]$DestinationDir,
    [bool]$TestPinsUsed,
    $TestPins
) {
    if (-not (Test-Path -LiteralPath $ProvenancePath -PathType Leaf)) {
        throw "Companion provenance file not found: $ProvenancePath"
    }

    $expectedProvSize = if ($TestPinsUsed -and $TestPins.CompanionProvenanceSize) { [long]$TestPins.CompanionProvenanceSize } else { $script:FROZEN_COMPANION_PROVENANCE_SIZE }
    $expectedProvHash = if ($TestPinsUsed -and $TestPins.CompanionProvenanceSha256) { $TestPins.CompanionProvenanceSha256 } else { $script:FROZEN_COMPANION_PROVENANCE_SHA256 }

    $provItem = Get-Item -LiteralPath $ProvenancePath -Force
    if ($provItem.Length -ne $expectedProvSize) {
        throw "Companion provenance archive size mismatch: expected $expectedProvSize bytes, got $($provItem.Length)"
    }
    $actualHash = Compute-FileSha256 -FilePath $ProvenancePath
    if ($actualHash -ne $expectedProvHash) {
        throw "Companion provenance archive SHA256 mismatch: expected $expectedProvHash, got $actualHash"
    }

    $isZip = $false
    try {
        $fs = [System.IO.File]::OpenRead($ProvenancePath)
        try {
            $magic = New-Object byte[] 4
            $readCount = $fs.Read($magic, 0, 4)
            if ($readCount -ge 4 -and $magic[0] -eq 0x50 -and $magic[1] -eq 0x4B -and $magic[2] -eq 0x03 -and $magic[3] -eq 0x04) {
                $isZip = $true
            }
        } finally {
            $fs.Dispose()
        }
    } catch {}

    $provObj = $null
    if ($isZip) {
        [void][System.IO.Directory]::CreateDirectory($DestinationDir)
        Extract-SafeZipArchive -ZipPath $ProvenancePath -DestinationDir $DestinationDir -MaxEntryCount 5 -MaxTotalBytes 65536
        $provJsonPath = [System.IO.Path]::Combine($DestinationDir, "build-provenance.json")
        if (-not (Test-Path -LiteralPath $provJsonPath -PathType Leaf)) {
            throw "Companion provenance archive does not contain build-provenance.json"
        }
        $provObj = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($provJsonPath, $script:Utf8NoBom))
    } else {
        try {
            $provObj = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($ProvenancePath, $script:Utf8NoBom))
            [void][System.IO.Directory]::CreateDirectory($DestinationDir)
            $provJsonPath = [System.IO.Path]::Combine($DestinationDir, "build-provenance.json")
            [System.IO.File]::Copy($ProvenancePath, $provJsonPath, $true)
        } catch {
            throw "Companion provenance file is neither valid zip nor valid JSON: $_"
        }
    }

    if (-not $TestPinsUsed) {
        if ($provObj.commit -ne $script:FROZEN_COMPANION_SRC_COMMIT) {
            throw "Companion provenance commit mismatch: expected $($script:FROZEN_COMPANION_SRC_COMMIT), got $($provObj.commit)"
        }
        if ("$($provObj.run_id)" -ne $script:FROZEN_COMPANION_RUN_ID) {
            throw "Companion provenance run_id mismatch: expected $($script:FROZEN_COMPANION_RUN_ID), got $($provObj.run_id)"
        }
    }
}

function Extract-BoundedDxvkTarGz(
    [string]$TarGzPath,
    [string]$Version,
    [string]$DestinationDir
) {
    if (-not (Test-Path -LiteralPath $TarGzPath -PathType Leaf)) {
        throw "DXVK archive not found: $TarGzPath"
    }

    $expectedSize = if ($Version -eq "v3.1.1") { $script:FROZEN_DXVK_311_SIZE } else { $script:FROZEN_DXVK_31_SIZE }
    $expectedHash = if ($Version -eq "v3.1.1") { $script:FROZEN_DXVK_311_SHA256 } else { $script:FROZEN_DXVK_31_SHA256 }

    $tarItem = Get-Item -LiteralPath $TarGzPath -Force
    if ($tarItem.Length -ne $expectedSize) {
        throw "DXVK $Version archive size mismatch: expected $expectedSize, got $($tarItem.Length)"
    }
    $actualHash = Compute-FileSha256 -FilePath $TarGzPath
    if ($actualHash -ne $expectedHash) {
        throw "DXVK $Version archive SHA256 mismatch: expected $expectedHash, got $actualHash"
    }

    $tarExe = "tar.exe"
    $tempListPath = [System.IO.Path]::GetTempFileName()
    $tempErrPath = [System.IO.Path]::GetTempFileName()
    try {
        $p = Start-Process -FilePath $tarExe -ArgumentList "-ztvf `"$TarGzPath`"" -NoNewWindow -Wait -PassThru -RedirectStandardOutput $tempListPath -RedirectStandardError $tempErrPath
        if ($p.ExitCode -ne 0) {
            $err = [System.IO.File]::ReadAllText($tempErrPath)
            throw "Failed to inspect tar archive '$TarGzPath' (exit code $($p.ExitCode)): $err"
        }

        $lines = [System.IO.File]::ReadAllLines($tempListPath)
        if ($lines.Length -gt 500) {
            throw "DXVK tar archive exceeds maximum allowed entry count (500): $($lines.Length)"
        }

        [long]$totalBytes = 0
        $hasD3d11 = $false
        $hasDxgi = $false

        foreach ($line in $lines) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $typeChar = $line[0]
            if ($typeChar -ne '-' -and $typeChar -ne 'd') {
                throw "DXVK tar archive contains forbidden entry type '$typeChar': $line"
            }

            $tokens = $line -split '\s+'
            if ($tokens.Length -ge 6) {
                $sizeToken = $tokens[2]
                $pathToken = $tokens[-1]

                if ($pathToken.Contains("..") -or $pathToken.StartsWith("/") -or $pathToken.StartsWith("\")) {
                    throw "DXVK tar archive contains illegal traversal path: $pathToken"
                }

                if ($typeChar -eq '-') {
                    $entryBytes = 0L
                    if ([long]::TryParse($sizeToken, [ref]$entryBytes)) {
                        $totalBytes += $entryBytes
                    }
                }

                if ($pathToken -like "*/x64/d3d11.dll") { $hasD3d11 = $true }
                if ($pathToken -like "*/x64/dxgi.dll") { $hasDxgi = $true }
            }
        }

        if ($totalBytes -gt 104857600L) {
            throw "DXVK tar archive expanded size ($totalBytes bytes) exceeds 100 MiB limit."
        }
        if (-not $hasD3d11 -or -not $hasDxgi) {
            throw "DXVK tar archive does not contain required x64/d3d11.dll and x64/dxgi.dll."
        }

        [void][System.IO.Directory]::CreateDirectory($DestinationDir)
        $cleanVer = $Version.TrimStart('v')
        $prefix = "dxvk-$cleanVer"
        & $tarExe -xzf $TarGzPath -C $DestinationDir --strip-components 2 "$prefix/x64/d3d11.dll" "$prefix/x64/dxgi.dll"
        if ($LASTEXITCODE -ne 0) {
            throw "tar.exe extraction failed with exit code $LASTEXITCODE."
        }

        $extD3d11 = [System.IO.Path]::Combine($DestinationDir, "d3d11.dll")
        $extDxgi = [System.IO.Path]::Combine($DestinationDir, "dxgi.dll")
        if (-not (Test-Path -LiteralPath $extD3d11 -PathType Leaf) -or -not (Test-Path -LiteralPath $extDxgi -PathType Leaf)) {
            throw "Extracted DXVK reference DLLs not found in $DestinationDir."
        }

        $refManifest = [PSCustomObject]@{
            Version = $Version
            ExtractedAtUtc = [DateTime]::UtcNow.ToString("o")
            Files = @(
                [PSCustomObject]@{ RelativePath = "d3d11.dll"; SizeBytes = (Get-Item -LiteralPath $extD3d11).Length; Sha256 = Compute-FileSha256 -FilePath $extD3d11 },
                [PSCustomObject]@{ RelativePath = "dxgi.dll"; SizeBytes = (Get-Item -LiteralPath $extDxgi).Length; Sha256 = Compute-FileSha256 -FilePath $extDxgi }
            )
        }
        $refManifestJson = ConvertTo-Json -InputObject $refManifest -Depth 5
        [System.IO.File]::WriteAllText([System.IO.Path]::Combine($DestinationDir, "manifest.json"), $refManifestJson, $script:Utf8NoBom)
    } finally {
        if (Test-Path -LiteralPath $tempListPath) { Remove-Item -LiteralPath $tempListPath -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $tempErrPath) { Remove-Item -LiteralPath $tempErrPath -Force -ErrorAction SilentlyContinue }
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

    # Companion provenance: required in candidate run (unless TestPins used)
    if (-not $testPinsUsed -and [string]::IsNullOrWhiteSpace($CompanionProvenancePath)) {
        throw "CompanionProvenancePath parameter is required for production candidate setup."
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

    if (-not [string]::IsNullOrWhiteSpace($CompanionProvenancePath)) {
        $provDest = [System.IO.Path]::Combine($companionDir, "provenance")
        Extract-CompanionProvenance -ProvenancePath $CompanionProvenancePath -DestinationDir $provDest -TestPinsUsed $testPinsUsed -TestPins $testPins
    }

    # F2: Extract helper directly to a dedicated directory and copy executable; no deletion in finally
    $helperExtractDir = [System.IO.Path]::Combine($rootFull, "helper_package")
    Expand-SafeZipArchiveInternal -ZipFilePath $HelperZipPath -DestinationDirectory $helperExtractDir

    $extractedHelperExe = [System.IO.Path]::Combine($helperExtractDir, "QueueFixture.exe")
    if (-not (Test-Path -LiteralPath $extractedHelperExe -PathType Leaf)) {
        throw "Extracted helper package does not contain QueueFixture.exe"
    }

    # Validate helper package manifest if present
    $helperManifest = [System.IO.Path]::Combine($helperExtractDir, "manifest.json")
    if (Test-Path -LiteralPath $helperManifest -PathType Leaf) {
        try {
            $manifestRaw = [System.IO.File]::ReadAllText($helperManifest, $script:Utf8NoBom)
            $null = ConvertFrom-Json -InputObject $manifestRaw
        } catch {
            throw "Extracted helper package manifest.json is invalid JSON: $_"
        }
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
        $ref311Dir = [System.IO.Path]::Combine($referenceDir, "v3.1.1", "x64")
        Extract-BoundedDxvkTarGz -TarGzPath $Dxvk311TarGzPath -Version "v3.1.1" -DestinationDir $ref311Dir
    }
    if (-not [string]::IsNullOrWhiteSpace($Dxvk31TarGzPath)) {
        $ref31Dir = [System.IO.Path]::Combine($referenceDir, "v3.1", "x64")
        Extract-BoundedDxvkTarGz -TarGzPath $Dxvk31TarGzPath -Version "v3.1" -DestinationDir $ref31Dir
    }

    # Seed baseline files in target
    $inertD3d11Path = [System.IO.Path]::Combine($targetDir, "d3d11.dll")
    $dxvkConfPath = [System.IO.Path]::Combine($targetDir, "dxvk.conf")
    $sentinelPath = [System.IO.Path]::Combine($targetDir, "sentinel.txt")

    $inertD3d11Bytes = [System.Text.Encoding]::ASCII.GetBytes("INERT_D3D11_NATIVE_BASELINE_STUB_NEVER_LOADED")
    [System.IO.File]::WriteAllBytes($inertD3d11Path, $inertD3d11Bytes)

    $dxvkConfContent = "# Unrelated noncanonical dxvk.conf for process lifecycle trial`nd3d11.fakeOption = True`n"
    [System.IO.File]::WriteAllText($dxvkConfPath, $dxvkConfContent, $script:Utf8NoBom)

    $sentinelContent = "TRIAL_BASELINE_SENTINEL_ROOT_IMMUTABLE`n"
    [System.IO.File]::WriteAllText($sentinelPath, $sentinelContent, $script:Utf8NoBom)

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
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($baselineDir, "baseline_manifest.json"), $manifestJson, $script:Utf8NoBom)

    $setupProv = [PSCustomObject]@{
        CreatedAtUtc        = $setupStartUtc
        SetupElapsedMs      = $stopwatch.ElapsedMilliseconds
        HelperZipSha256     = $actualHelperZipHash
        CompanionZipSha256  = $actualCompanionZipHash
        TestPinsUsed        = $testPinsUsed
        AdoptionAttestation = $AdoptionAttestation
    }
    $setupProvJson = ConvertTo-Json -InputObject $setupProv -Depth 5
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($baselineDir, "setup_provenance.json"), $setupProvJson, $script:Utf8NoBom)

    # F4: Seed Companion Storage with REAL store schema (game-library.json AND games.json)
    $settingsPath = [System.IO.Path]::Combine($companionDir, "settings.json")
    $settingsObj = [PSCustomObject]@{
        GlobalPolicy              = "Manual"
        AutoEnableDxvkForNewGames = $false
        LaunchOnStartup           = $false
    }
    [System.IO.File]::WriteAllText($settingsPath, (ConvertTo-Json -InputObject $settingsObj), $script:Utf8NoBom)

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
    [System.IO.File]::WriteAllText($gameLibraryPath, $libraryJson, $script:Utf8NoBom)

    $legacyGamesPath = [System.IO.Path]::Combine($profilesDir, "games.json")
    $legacyProfile = [PSCustomObject]@{
        ExePath      = $targetHelperExe
        ExeName      = "QueueFixture.exe"
        Api          = 3
        Architecture = "x64"
        DxvkEnabled  = $false
        DxvkVersion  = $null
        HudEnabled   = $false
        FrameLimit   = 0
    }
    $legacyGamesJson = ConvertTo-Json -InputObject @($legacyProfile) -Depth 5
    [System.IO.File]::WriteAllText($legacyGamesPath, $legacyGamesJson, $script:Utf8NoBom)

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
        LastFailedReason         = $null
        IsEnded                  = $false
        TestPinsUsed             = $testPinsUsed
        ActiveHelperPid          = $null
        ActiveHelperSlot         = $null
        ActiveCompanionPid       = $null
        ActiveSupervisorPid      = $null
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

    $state = Get-StateData -FixtureRoot $canonRoot
    if ($state.LastCheckpointOutcome -eq "Failed") {
        throw "Snapshot refused: Fixture qualification is in terminal failure state ('$($state.LastFailedCheckpoint)')."
    }

    $snapshotsDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "snapshots"))
    $checkpointDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($snapshotsDir, $cleanName))

    if (Test-Path -LiteralPath $checkpointDir) {
        throw "Snapshot checkpoint directory already exists: $checkpointDir"
    }

    $targetDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "target"))
    $profilesDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "companion", "Profiles"))
    $controlDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "control"))

    # Projected size check
    $projTarget = Measure-DirectoryFootprintBytes -DirectoryPath $targetDir
    $projProfiles = Measure-DirectoryFootprintBytes -DirectoryPath $profilesDir
    $projControl = Measure-DirectoryFootprintBytes -DirectoryPath $controlDir
    $projectedBytes = $projTarget + $projProfiles + $projControl
    $currentFootprint = Measure-DirectoryFootprintBytes -DirectoryPath $canonRoot

    if (($currentFootprint + $projectedBytes) -gt $script:MAX_FIXTURE_ROOT_BYTES) {
        throw "Snapshot refused: projected footprint ($($currentFootprint + $projectedBytes) bytes) exceeds fixture root cap ($($script:MAX_FIXTURE_ROOT_BYTES) bytes)."
    }

    [void][System.IO.Directory]::CreateDirectory($checkpointDir)

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
    [System.IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $snapshotManifest -Depth 5), $script:Utf8NoBom)

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
        TotalFiles = (Get-SafeCount $fileRecords)
    }
}

function Verify-CheckpointInternal(
    [string]$FixtureRoot,
    [string]$CheckpointName,
    [string]$ExpectedType,
    [string]$ExpectedVersion = "",
    [int]$Slot = 0,
    [switch]$PureDataCheck
) {
    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    $cleanName = Validate-CheckpointName -Name $CheckpointName

    $state = Get-StateData -FixtureRoot $canonRoot
    if ($state.LastCheckpointOutcome -eq "Failed") {
        throw "Verification refused: Fixture qualification previously entered terminal failure at checkpoint '$($state.LastFailedCheckpoint)'."
    }

    try {
        $targetDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "target"))
        $companionDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "companion"))
        $profilesDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($companionDir, "Profiles"))
        $gameLibraryPath = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($profilesDir, "game-library.json"))
        $legacyGamesPath = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($profilesDir, "games.json"))

        # F4: Repeated live read to verify stability of store file
        if (-not (Test-Path -LiteralPath $gameLibraryPath -PathType Leaf)) {
            throw "game-library.json not found: $gameLibraryPath"
        }
        $read1 = [System.IO.File]::ReadAllText($gameLibraryPath, $script:Utf8NoBom)
        Start-Sleep -Milliseconds 500
        $read2 = [System.IO.File]::ReadAllText($gameLibraryPath, $script:Utf8NoBom)
        if ($read1 -ne $read2) {
            throw "Live library store is unstable across repeated reads. Inspection uncertainty detected."
        }

        $library = ConvertFrom-Json -InputObject $read1
        $instList = @(Get-SafeEnumerable (Get-SafePropertyValue -Object $library -PropertyName "Installations"))
        if ($instList.Length -ne 1) {
            throw "Live library store verification failed: expected exactly 1 installation, got $($instList.Length)."
        }
        $inst = $instList[0]

        switch ($ExpectedType) {
            "Pending" {
                if ($null -eq $inst.PendingAction) {
                    throw "Pending checkpoint verification failed: no PendingAction recorded."
                }
                $pendingType = $inst.PendingAction.Type
                $validTypes = @("Install", "Update", "Reapply", "Restore")
                if ($validTypes -notcontains $pendingType) {
                    throw "Pending checkpoint verification failed: unexpected pending action type '$pendingType'."
                }

                if ($pendingType -eq "Install") {
                    if ([string]::IsNullOrWhiteSpace($inst.ManagedDxvkVersion)) {
                        $baseCheck = Compare-TargetBaselineInternal -FixtureRoot $canonRoot
                        if (-not $baseCheck.IsMatch) {
                            throw "Pending Install checkpoint verification failed: target was mutated ($($baseCheck.Reason))"
                        }
                    } else {
                        $currentManagedVer = $inst.ManagedDxvkVersion
                        $managedCheck = Compare-TargetManagedReferenceInternal -FixtureRoot $canonRoot -ManagedVersion $currentManagedVer
                        if (-not $managedCheck.IsMatch) {
                            throw "Pending Install checkpoint verification failed: target does not match currently managed $currentManagedVer state ($($managedCheck.Reason))"
                        }
                    }
                } else {
                    $currentManagedVer = $inst.ManagedDxvkVersion
                    if ([string]::IsNullOrWhiteSpace($currentManagedVer)) {
                        throw "Pending $pendingType checkpoint verification failed: installation has no ManagedDxvkVersion."
                    }
                    $managedCheck = Compare-TargetManagedReferenceInternal -FixtureRoot $canonRoot -ManagedVersion $currentManagedVer
                    if (-not $managedCheck.IsMatch) {
                        throw "Pending $pendingType checkpoint verification failed: target does not match currently managed $currentManagedVer state ($($managedCheck.Reason))"
                    }
                }

                # Helper liveness verified via retained process handle unless PureDataCheck
                if (-not $PureDataCheck) {
                    if ($null -eq $script:HelperProcess -or $script:HelperProcess.HasExited) {
                        throw "Pending checkpoint verification failed: live owned helper process handle is required."
                    }
                }

                if (-not [string]::IsNullOrEmpty($ExpectedVersion)) {
                    if ($pendingType -ne "Restore") {
                        if ($inst.PendingAction.TargetDxvkVersion -ne $ExpectedVersion) {
                            throw "Pending checkpoint verification failed: expected target version '$ExpectedVersion', got '$($inst.PendingAction.TargetDxvkVersion)'."
                        }
                    }
                }
            }
            "Applied" {
                if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
                    throw "Applied checkpoint verification requires an ExpectedVersion parameter."
                }

                # Official references are MANDATORY
                $refVersionDir = [System.IO.Path]::Combine($canonRoot, "reference", $ExpectedVersion, "x64")
                if (-not (Test-Path -LiteralPath $refVersionDir -PathType Container)) {
                    throw "Applied checkpoint verification failed: official reference directory missing for $ExpectedVersion at $refVersionDir."
                }
                $refD3d11 = [System.IO.Path]::Combine($refVersionDir, "d3d11.dll")
                $refDxgi = [System.IO.Path]::Combine($refVersionDir, "dxgi.dll")
                if (-not (Test-Path -LiteralPath $refD3d11 -PathType Leaf)) {
                    throw "Applied checkpoint verification failed: official reference d3d11.dll missing at $refD3d11."
                }
                if (-not (Test-Path -LiteralPath $refDxgi -PathType Leaf)) {
                    throw "Applied checkpoint verification failed: official reference dxgi.dll missing at $refDxgi."
                }

                # Helper must be confirmed stopped
                if ($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) {
                    throw "Applied checkpoint verification failed: helper process is still running."
                }

                # Check normal exit record for helper using slot binding
                $targetSlot = if ($Slot -gt 0) {
                    $Slot
                } elseif ($null -ne $state.LastStoppedHelperSlot) {
                    [int]$state.LastStoppedHelperSlot
                } elseif ($null -ne $state.ActiveHelperSlot) {
                    [int]$state.ActiveHelperSlot
                } else {
                    1
                }
                $exitRecordPath = [System.IO.Path]::Combine($canonRoot, "control", "helper-slot-$targetSlot.exit.json")
                if (-not (Test-Path -LiteralPath $exitRecordPath -PathType Leaf)) {
                    throw "Applied checkpoint verification failed: normal exit record missing: helper-slot-$targetSlot.exit.json."
                }
                $exitRecordRaw = [System.IO.File]::ReadAllText($exitRecordPath, $script:Utf8NoBom)
                $exitRecord = ConvertFrom-Json -InputObject $exitRecordRaw
                if ($exitRecord.ExitCode -ne 0 -or -not $exitRecord.NormalStop) {
                    throw "Applied checkpoint verification failed: helper exited abnormally (ExitCode=$($exitRecord.ExitCode), NormalStop=$($exitRecord.NormalStop))."
                }

                $d3d11Path = [System.IO.Path]::Combine($targetDir, "d3d11.dll")
                $dxgiPath = [System.IO.Path]::Combine($targetDir, "dxgi.dll")
                if (-not (Test-Path -LiteralPath $d3d11Path -PathType Leaf)) {
                    throw "Applied checkpoint verification failed: d3d11.dll is absent from target."
                }
                if (-not (Test-Path -LiteralPath $dxgiPath -PathType Leaf)) {
                    throw "Applied checkpoint verification failed: dxgi.dll is absent from target."
                }

                $targetD3d11Hash = Compute-FileSha256 -FilePath $d3d11Path
                $refD3d11Hash = Compute-FileSha256 -FilePath $refD3d11
                if ($targetD3d11Hash -ne $refD3d11Hash) {
                    throw "Applied target d3d11.dll does not match official $ExpectedVersion reference payload."
                }

                $targetDxgiHash = Compute-FileSha256 -FilePath $dxgiPath
                $refDxgiHash = Compute-FileSha256 -FilePath $refDxgi
                if ($targetDxgiHash -ne $refDxgiHash) {
                    throw "Applied target dxgi.dll does not match official $ExpectedVersion reference payload."
                }

                # QueueFixture.exe, dxvk.conf, and sentinel.txt must match baseline
                $baselineDir = [System.IO.Path]::Combine($canonRoot, "baseline")
                foreach ($item in @("QueueFixture.exe", "dxvk.conf", "sentinel.txt")) {
                    $tPath = [System.IO.Path]::Combine($targetDir, $item)
                    $bPath = [System.IO.Path]::Combine($baselineDir, $item)
                    if (-not (Test-Path -LiteralPath $tPath -PathType Leaf)) {
                        throw "Applied checkpoint verification failed: target file missing: $item"
                    }
                    if ((Compute-FileSha256 -FilePath $tPath) -ne (Compute-FileSha256 -FilePath $bPath)) {
                        throw "Applied checkpoint verification failed: target $item does not match immutable baseline."
                    }
                }

                # Verify ManagedFiles ownership and native backup
                $managedFiles = @($inst.ManagedFiles)
                if ((Get-SafeCount $managedFiles) -lt 2) {
                    throw "Applied checkpoint verification failed: expected at least 2 ManagedFiles records, got $((Get-SafeCount $managedFiles))."
                }

                $d3d11Rec = $managedFiles | Where-Object { $_.RelativePath -like "*d3d11.dll" } | Select-Object -First 1
                if ($null -eq $d3d11Rec) {
                    throw "Applied checkpoint verification failed: no ManagedFile record for d3d11.dll."
                }
                if ($d3d11Rec.OriginalState -ne "Existing") {
                    throw "Applied checkpoint verification failed: d3d11.dll OriginalState is '$($d3d11Rec.OriginalState)' (expected 'Existing')."
                }
                if ([string]::IsNullOrWhiteSpace($d3d11Rec.BackupRelativePath)) {
                    throw "Applied checkpoint verification failed: d3d11.dll BackupRelativePath is empty."
                }
                $backupsDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($profilesDir, "Backups"))
                $backupFullPath = Validate-RootBoundPath -RootPath $backupsDir -CandidatePath ([System.IO.Path]::Combine($backupsDir, $d3d11Rec.BackupRelativePath))
                if (-not (Test-Path -LiteralPath $backupFullPath -PathType Leaf)) {
                    throw "Applied checkpoint verification failed: native backup file missing at $backupFullPath."
                }
                $backupHash = Compute-FileSha256 -FilePath $backupFullPath
                if ($backupHash -ne $d3d11Rec.OriginalSha256) {
                    throw "Applied checkpoint verification failed: native backup hash ($backupHash) does not match OriginalSha256 ($($d3d11Rec.OriginalSha256))."
                }

                # Verify against baseline manifest
                $baseManifestPath = [System.IO.Path]::Combine($baselineDir, "baseline_manifest.json")
                $baseManifest = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($baseManifestPath, $script:Utf8NoBom))
                $origD3d11Entry = $baseManifest.Files | Where-Object { $_.RelativePath -eq "d3d11.dll" } | Select-Object -First 1
                if ($d3d11Rec.OriginalSha256 -ne $origD3d11Entry.Sha256) {
                    throw "Applied checkpoint verification failed: d3d11.dll OriginalSha256 does not match pre-trial baseline hash."
                }

                $dxgiRec = $managedFiles | Where-Object { $_.RelativePath -like "*dxgi.dll" } | Select-Object -First 1
                if ($null -eq $dxgiRec) {
                    throw "Applied checkpoint verification failed: no ManagedFile record for dxgi.dll."
                }
                if ($dxgiRec.OriginalState -ne "Missing") {
                    throw "Applied checkpoint verification failed: dxgi.dll OriginalState is '$($dxgiRec.OriginalState)' (expected 'Missing')."
                }

                if ($inst.ManagedDxvkVersion -ne $ExpectedVersion) {
                    throw "Applied verification failed: expected managed version '$ExpectedVersion', got '$($inst.ManagedDxvkVersion)'."
                }
                if ($null -ne $inst.PendingAction) {
                    throw "Applied checkpoint verification failed: pending action was not cleared after dispatch."
                }

                # Verify games.json coherence
                if (-not (Test-Path -LiteralPath $legacyGamesPath -PathType Leaf)) {
                    throw "Applied checkpoint verification failed: games.json does not exist."
                }
                $gamesRaw = [System.IO.File]::ReadAllText($legacyGamesPath, $script:Utf8NoBom)
                $gamesList = @(Get-SafeEnumerable (ConvertFrom-Json -InputObject $gamesRaw))
                if ($gamesList.Length -ne 1) {
                    throw "Applied checkpoint verification failed: expected exactly 1 profile in games.json, got $($gamesList.Length)."
                }
                $gp = $gamesList[0]
                if ($gp.ExeName -ne "QueueFixture.exe") {
                    throw "Applied checkpoint verification failed: games.json ExeName is '$($gp.ExeName)' (expected 'QueueFixture.exe')."
                }
                if (-not $gp.DxvkEnabled) {
                    throw "Applied checkpoint verification failed: games.json DxvkEnabled is false."
                }
                if ($gp.DxvkVersion -ne $ExpectedVersion) {
                    throw "Applied checkpoint verification failed: games.json DxvkVersion is '$($gp.DxvkVersion)' (expected '$ExpectedVersion')."
                }
            }
            "Restored" {
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

                # Native backup retention: verify native backup still exists and matches baseline hash
                $managedFiles = @(Get-SafeEnumerable $inst.ManagedFiles)
                $d3d11Rec = $managedFiles | Where-Object { $_.RelativePath -like "*d3d11.dll" } | Select-Object -First 1
                if ($null -ne $d3d11Rec -and -not [string]::IsNullOrWhiteSpace($d3d11Rec.BackupRelativePath)) {
                    $backupsDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($profilesDir, "Backups"))
                    $backupFullPath = Validate-RootBoundPath -RootPath $backupsDir -CandidatePath ([System.IO.Path]::Combine($backupsDir, $d3d11Rec.BackupRelativePath))
                    if (-not (Test-Path -LiteralPath $backupFullPath -PathType Leaf)) {
                        throw "Restored checkpoint verification failed: retained native backup missing at $backupFullPath."
                    }
                    $backupHash = Compute-FileSha256 -FilePath $backupFullPath
                    $baseManifestPath = [System.IO.Path]::Combine($canonRoot, "baseline", "baseline_manifest.json")
                    $baseManifest = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($baseManifestPath, $script:Utf8NoBom))
                    $origD3d11Entry = $baseManifest.Files | Where-Object { $_.RelativePath -eq "d3d11.dll" } | Select-Object -First 1
                    if ($backupHash -ne $origD3d11Entry.Sha256) {
                        throw "Restored checkpoint verification failed: retained native backup hash mismatch: $backupHash vs $($origD3d11Entry.Sha256)."
                    }
                }

                # Verify games.json restored
                if (-not (Test-Path -LiteralPath $legacyGamesPath -PathType Leaf)) {
                    throw "Restored checkpoint verification failed: games.json does not exist."
                }
                $gamesRaw = [System.IO.File]::ReadAllText($legacyGamesPath, $script:Utf8NoBom)
                $gamesList = @(Get-SafeEnumerable (ConvertFrom-Json -InputObject $gamesRaw))
                if ($gamesList.Length -ne 1) {
                    throw "Restored checkpoint verification failed: expected exactly 1 profile in games.json, got $($gamesList.Length)."
                }
                $gp = $gamesList[0]
                if ($gp.ExeName -ne "QueueFixture.exe") {
                    throw "Restored checkpoint verification failed: games.json ExeName is '$($gp.ExeName)' (expected 'QueueFixture.exe')."
                }
                if ($gp.DxvkEnabled) {
                    throw "Restored checkpoint verification failed: games.json DxvkEnabled is true."
                }
                if ($null -ne $gp.DxvkVersion) {
                    throw "Restored checkpoint verification failed: games.json DxvkVersion is '$($gp.DxvkVersion)' (expected null)."
                }
            }
            Default {
                throw "Unrecognized ExpectedType: $ExpectedType"
            }
        }

        # Success - latch success only if prior outcome wasn't failed
        $state = Get-StateData -FixtureRoot $canonRoot
        $state.LastCheckpointOutcome = "Success"
        Save-StateData -FixtureRoot $canonRoot -StateObject $state
        return @{
            Success = $true
            Message = "Checkpoint '$cleanName' verified successfully as $ExpectedType."
            CheckpointName = $cleanName
            ExpectedType = $ExpectedType
        }
    } catch {
        $state = Get-StateData -FixtureRoot $canonRoot
        $state.LastCheckpointOutcome = "Failed"
        $state.LastFailedCheckpoint = $cleanName
        $state.LastFailedReason = "$_"
        Save-StateData -FixtureRoot $canonRoot -StateObject $state
        throw
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

    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    $targetDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "target"))
    $logsDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "logs"))
    $helperExe = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($targetDir, "QueueFixture.exe"))

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
    $state = Get-StateData -FixtureRoot $canonRoot
    $state.HelperStartAttempts = [int]$state.HelperStartAttempts + 1
    Save-StateData -FixtureRoot $canonRoot -StateObject $state

    $stdoutLogPath = [System.IO.Path]::Combine($logsDir, "helper-slot-$Slot-stdout.log")
    $stderrLogPath = [System.IO.Path]::Combine($logsDir, "helper-slot-$Slot-stderr.log")

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

    $script:HelperStdoutDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($proc.StandardOutput, $stdoutLogPath, $script:Utf8NoBom)
    $script:HelperStderrDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($proc.StandardError, $stderrLogPath, $script:Utf8NoBom)
    $script:HelperStdoutDrainer.Start()
    $script:HelperStderrDrainer.Start()

    $script:HelperProcess = $proc

    # Bounded wait for READY signal on standard output (no kill on timeout)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $script:HelperStdoutDrainer.ReadyReceived -and $sw.ElapsedMilliseconds -lt 5000) {
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 50
    }

    if (-not $script:HelperStdoutDrainer.ReadyReceived) {
        throw "Helper process failed to emit READY within 5000ms. Residual process retained."
    }

    # Update state
    $state = Get-StateData -FixtureRoot $canonRoot
    $usedSlots = @(Get-SafeEnumerable $state.UsedSlots)
    if ($usedSlots -notcontains $Slot) {
        $usedSlots += $Slot
    }
    $state.UsedSlots = $usedSlots
    $state.ActiveHelperPid = $proc.Id
    $state.ActiveHelperSlot = $Slot
    $state.ActiveHelperStartTimeUtc = [DateTime]::UtcNow.ToString("o")
    Save-StateData -FixtureRoot $canonRoot -StateObject $state

    return @{
        Success = $true
        Message = "Helper started successfully in slot $Slot with PID $($proc.Id)."
        Pid = $proc.Id
        Slot = $Slot
    }
}

function Supervise-HelperStop([string]$FixtureRoot, [int]$Slot) {
    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    $state = Get-StateData -FixtureRoot $canonRoot

    if ($null -eq $script:HelperProcess) {
        throw "No retained helper process handle in supervisor."
    }

    if ($state.ActiveHelperSlot -ne $Slot) {
        throw "Helper stop slot mismatch: requested slot $Slot, but active slot is $($state.ActiveHelperSlot)."
    }

    if ($script:HelperProcess.HasExited) {
        throw "Helper process has already exited unexpectedly."
    }

    $controlDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "control"))
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

    if ($null -ne $script:HelperStdoutDrainer) { [void]$script:HelperStdoutDrainer.WaitForDrain(5000) }
    if ($null -ne $script:HelperStderrDrainer) { [void]$script:HelperStderrDrainer.WaitForDrain(5000) }

    $exitCode = $script:HelperProcess.ExitCode
    $stoppedReceived = if ($null -ne $script:HelperStdoutDrainer) { $script:HelperStdoutDrainer.StoppedReceived } else { $false }
    $isNormalStop = ($exitCode -eq 0 -and $stoppedReceived)

    if ($exitCode -ne 0) {
        throw "Helper process exited with non-zero code: $exitCode (expected 0)."
    }
    if (-not $stoppedReceived) {
        throw "Helper process exited with code 0 but did not emit STOPPED token."
    }

    # Record confirmed normal exit record in control/helper-slot-$Slot.exit.json
    $exitRecord = [PSCustomObject]@{
        Slot         = $Slot
        Pid          = $script:HelperProcess.Id
        ExitCode     = $exitCode
        NormalStop   = $isNormalStop
        StoppedAtUtc = [DateTime]::UtcNow.ToString("o")
    }
    $exitRecordPath = [System.IO.Path]::Combine($controlDir, "helper-slot-$Slot.exit.json")
    [System.IO.File]::WriteAllText($exitRecordPath, (ConvertTo-Json -InputObject $exitRecord), $script:Utf8NoBom)

    if ($null -ne $script:HelperStdoutDrainer) { $script:HelperStdoutDrainer.Dispose(); $script:HelperStdoutDrainer = $null }
    if ($null -ne $script:HelperStderrDrainer) { $script:HelperStderrDrainer.Dispose(); $script:HelperStderrDrainer = $null }
    $script:HelperProcess.Dispose()
    $script:HelperProcess = $null

    $state = Get-StateData -FixtureRoot $canonRoot
    $state.ActiveHelperPid = $null
    $state.ActiveHelperSlot = $null
    $state.LastStoppedHelperSlot = $Slot
    Save-StateData -FixtureRoot $canonRoot -StateObject $state

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

    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    $companionDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "companion"))
    $companionExe = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($companionDir, "DXVK-Companion.exe"))
    $runtimeDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "runtime"))
    $logsDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "logs"))

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
    $settingsRaw = [System.IO.File]::ReadAllText($settingsPath, $script:Utf8NoBom)
    $settingsObj = ConvertFrom-Json -InputObject $settingsRaw
    if ("$($settingsObj.GlobalPolicy)" -ne "Manual") {
        throw "Launch refused: settings.json GlobalPolicy is '$($settingsObj.GlobalPolicy)' (expected 'Manual')."
    }

    # F1: Count attempted starts BEFORE Process.Start
    $state = Get-StateData -FixtureRoot $canonRoot
    $state.CompanionStartAttempts = [int]$state.CompanionStartAttempts + 1
    Save-StateData -FixtureRoot $canonRoot -StateObject $state

    $compStdoutLog = [System.IO.Path]::Combine($logsDir, "companion-stdout.log")
    $compStderrLog = [System.IO.Path]::Combine($logsDir, "companion-stderr.log")

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $companionExe
    $psi.Arguments = if ($NoAutoDiscovery) { "--no-auto-discovery" } else { "" }
    $psi.WorkingDirectory = $companionDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $false
    $psi.EnvironmentVariables["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = $runtimeDir

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    if (-not $proc.Start()) {
        throw "Failed to start Companion process."
    }

    $script:CompanionStdoutDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($proc.StandardOutput, $compStdoutLog, $script:Utf8NoBom)
    $script:CompanionStderrDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($proc.StandardError, $compStderrLog, $script:Utf8NoBom)
    $script:CompanionStdoutDrainer.Start()
    $script:CompanionStderrDrainer.Start()

    # Retain process handle in supervisor state
    $script:CompanionProcess = $proc

    $state = Get-StateData -FixtureRoot $canonRoot
    $state.ActiveCompanionPid = $proc.Id
    Save-StateData -FixtureRoot $canonRoot -StateObject $state

    return @{
        Success = $true
        Message = "Companion started successfully with PID $($proc.Id)."
        Pid = $proc.Id
    }
}

function Supervise-CompanionStop([string]$FixtureRoot) {
    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot

    if ($null -eq $script:CompanionProcess) {
        return @{ Success = $true; Message = "Companion is not currently running." }
    }

    if (-not $script:CompanionProcess.HasExited) {
        Write-Host "Waiting up to 30s for operator to close Companion GUI normally..."
        if (-not $script:CompanionProcess.WaitForExit(30000)) {
            throw "Companion process did not exit within 30000ms. Force kill is prohibited; residual process retained."
        }
    }

    if ($null -ne $script:CompanionStdoutDrainer) { [void]$script:CompanionStdoutDrainer.WaitForDrain(5000) }
    if ($null -ne $script:CompanionStderrDrainer) { [void]$script:CompanionStderrDrainer.WaitForDrain(5000) }

    $exitCode = $script:CompanionProcess.ExitCode

    if ($null -ne $script:CompanionStdoutDrainer) { $script:CompanionStdoutDrainer.Dispose(); $script:CompanionStdoutDrainer = $null }
    if ($null -ne $script:CompanionStderrDrainer) { $script:CompanionStderrDrainer.Dispose(); $script:CompanionStderrDrainer = $null }
    $script:CompanionProcess.Dispose()
    $script:CompanionProcess = $null

    $state = Get-StateData -FixtureRoot $canonRoot
    $state.ActiveCompanionPid = $null
    Save-StateData -FixtureRoot $canonRoot -StateObject $state

    return @{
        Success = $true
        Message = "Companion stopped with exit code $exitCode."
        ExitCode = $exitCode
    }
}

function Invoke-SuperviseAction {
    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { throw "FixtureRoot parameter is required for Supervise." }
    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot

    # Prevent duplicate supervisor sessions on the same fixture root
    $state = Get-StateData -FixtureRoot $canonRoot
    if ($null -ne $state.ActiveSupervisorPid) {
        $existingPid = [int]$state.ActiveSupervisorPid
        $existingProc = Get-Process -Id $existingPid -ErrorAction SilentlyContinue
        if ($null -ne $existingProc -and -not $existingProc.HasExited) {
            throw "Another supervisor session is already active on fixture root '$canonRoot' (PID $existingPid)."
        }
    }
    $state.ActiveSupervisorPid = $PID
    Save-StateData -FixtureRoot $canonRoot -StateObject $state

    Write-Host "======================================================================"
    Write-Host "DXVK Companion Process-Lifecycle Supervisor (Retained Session)"
    Write-Host "Fixture Root: $canonRoot"
    Write-Host "Supervisor PID: $PID"
    Write-Host "======================================================================"

    $commandList = @()
    if ($null -ne $Commands -and $Commands.Length -gt 0) {
        $commandList = @($Commands)
    } elseif (-not [string]::IsNullOrWhiteSpace($CommandFile)) {
        if (-not (Test-Path -LiteralPath $CommandFile -PathType Leaf)) {
            throw "Command file not found: $CommandFile"
        }
        $commandList = @(Get-Content -LiteralPath $CommandFile)
    }

    $executionResults = @()

    try {
        if ((Get-SafeCount $commandList) -gt 0) {
            Invoke-SuperviseObservationCycle -FixtureRoot $canonRoot
            foreach ($line in $commandList) {
                $trimmed = $line.Trim()
                if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) { continue }

                Invoke-SuperviseObservationCycle -FixtureRoot $canonRoot
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
                        $cmdSlot = if ($parts.Length -gt 4) { [int]$parts[4] } else { 0 }
                        $res = Verify-CheckpointInternal -FixtureRoot $canonRoot -CheckpointName $name -ExpectedType $type -ExpectedVersion $ver -Slot $cmdSlot -PureDataCheck:$PureDataCheck
                    }
                    "end-batch" {
                        $st = Get-StateData -FixtureRoot $canonRoot
                        $st.IsEnded = $true
                        Save-StateData -FixtureRoot $canonRoot -StateObject $st
                        $res = @{ Success = $true; Message = "Batch marked ended." }
                    }
                    Default {
                        throw "Unrecognized supervisor command: $trimmed"
                    }
                }
                $executionResults += $res
                Invoke-SuperviseObservationCycle -FixtureRoot $canonRoot
            }

            # Enforce: Never return while unowned child processes remain active!
            if (($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) -or
                ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited)) {
                throw "Supervisor command sequence completed while child process is still running. Unowned processes prohibited."
            }

            return @{
                Success = $true
                Message = "All supervisor commands executed successfully."
                Results = $executionResults
            }
        } else {
            Write-Host "Entering interactive supervisor mode. Type 'help' for commands, 'exit' to quit."
            while ($true) {
                Invoke-SuperviseObservationCycle -FixtureRoot $canonRoot
                $st = Get-StateData -FixtureRoot $canonRoot
                $prompt = "Trial [Helper=$($st.HelperStartAttempts)/4, Comp=$($st.CompanionStartAttempts)/8, Readiness=$($st.CurrentReadiness)] > "
                Write-Host -NoNewline $prompt
                $inputLine = [Console]::ReadLine()
                if ($null -eq $inputLine) {
                    Write-Host "`nEOF detected on supervisor input."
                    $hasActive = ($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) -or
                                 ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited)
                    if ($hasActive) {
                        Write-Host "Child processes are still running. Retaining supervisor ownership in observation loop until children exit..."
                        while (($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) -or
                               ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited)) {
                            Invoke-SuperviseObservationCycle -FixtureRoot $canonRoot
                            Start-Sleep -Milliseconds 500
                        }
                        Write-Host "All child processes have exited."
                    }
                    break
                }
                $trimmed = $inputLine.Trim()
                if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
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
                            $cmdSlot = if ($parts.Length -gt 4) { [int]$parts[4] } else { 0 }
                            $res = Verify-CheckpointInternal -FixtureRoot $canonRoot -CheckpointName $name -ExpectedType $type -ExpectedVersion $ver -Slot $cmdSlot -PureDataCheck:$PureDataCheck
                            Write-Host "[SUCCESS] $($res.Message)"
                        }
                        "status" {
                            $stState = Get-StateData -FixtureRoot $canonRoot
                            Write-Host (ConvertTo-Json -InputObject $stState -Depth 5)
                        }
                        "end-batch" {
                            $stState = Get-StateData -FixtureRoot $canonRoot
                            $stState.IsEnded = $true
                            Save-StateData -FixtureRoot $canonRoot -StateObject $stState
                            Write-Host "[SUCCESS] Batch ended."
                        }
                        "help" {
                            Write-Host "Available commands:"
                            Write-Host "  start-helper <1..4>"
                            Write-Host "  stop-helper <1..4>"
                            Write-Host "  start-companion [--no-auto-discovery]"
                            Write-Host "  stop-companion"
                            Write-Host "  snapshot <CheckpointName>"
                            Write-Host "  verify <CheckpointName> <Pending|Applied|Restored> [<Version>] [<Slot>]"
                            Write-Host "  status"
                            Write-Host "  end-batch"
                            Write-Host "  exit"
                        }
                        Default {
                            Write-Warning "Unrecognized command: $cmd"
                        }
                    }
                } catch {
                    Write-Host "[ERROR] $_" -ForegroundColor Red
                }
            }
            return @{ Success = $true; Message = "Interactive supervisor exited." }
        }
    } finally {
        $hasActiveChildren = ($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) -or
                             ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited)
        if (-not $hasActiveChildren) {
            $cleanState = Get-StateData -FixtureRoot $canonRoot
            if ($cleanState.ActiveSupervisorPid -eq $PID) {
                $cleanState.ActiveSupervisorPid = $null
                Save-StateData -FixtureRoot $canonRoot -StateObject $cleanState
            }
        }
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
            $result = Verify-CheckpointInternal -FixtureRoot $FixtureRoot -CheckpointName $CheckpointName -ExpectedType $ExpectedType -ExpectedVersion $ExpectedVersion -Slot $Slot -PureDataCheck:$PureDataCheck
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
