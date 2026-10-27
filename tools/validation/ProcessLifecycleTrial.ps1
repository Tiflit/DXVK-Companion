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
        "Status",
        "Test-Drainer",
        "Test-ParseTarListing",
        "Test-Provenance",
        "Test-BoundedChild",
        "Test-ResourceObservation"
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
    [string]$ReadinessRecordPath,

    [Parameter(Mandatory = $false)]
    [string]$ReadinessJson,

    [Parameter(Mandatory = $false)]
    [string]$SupervisorSessionId,

    [Parameter(Mandatory = $false)]
    [string]$ChildFileName,

    [Parameter(Mandatory = $false)]
    [string]$ChildArguments,

    [Parameter(Mandatory = $false)]
    [string]$StderrLogPath,

    [Parameter(Mandatory = $false)]
    [long]$TestMonotonicTick = -1,

    [Parameter(Mandatory = $false)]
    [long]$TestMonotonicFrequency = -1,

    [Parameter(Mandatory = $false)]
    [string]$TestCurrentUtc,

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
    [string]$TestInputText,

    [Parameter(Mandatory = $false)]
    [string]$TestInputPath,

    [Parameter(Mandatory = $false)]
    [string]$LogPath,

    [Parameter(Mandatory = $false)]
    [int]$TimeoutMs = 5000,

    [Parameter(Mandatory = $false)]
    [long]$MaxBytes = 104857600,

    [Parameter(Mandatory = $false)]
    [string[]]$Lines,

    [Parameter(Mandatory = $false)]
    [string]$ListingFilePath,

    [Parameter(Mandatory = $false)]
    [long]$MaxEntries = 500,

    [Parameter(Mandatory = $false)]
    [long]$MaxTotalBytes = 104857600,

    [Parameter(Mandatory = $false)]
    [switch]$SimulateFault,

    [Parameter(Mandatory = $false)]
    [switch]$PureDataCheck,

    [Parameter(Mandatory = $false)]
    [string]$Role,

    [Parameter(Mandatory = $false)]
    [int]$SamplePid = -1,

    [Parameter(Mandatory = $false)]
    [long]$SampleWorkingSetBytes = -1,

    [Parameter(Mandatory = $false)]
    [long]$SamplePeakWorkingSetBytes = -1,

    [Parameter(Mandatory = $false)]
    [long]$SampleCpuMilliseconds = -1,

    [Parameter(Mandatory = $false)]
    [string]$InvocationId,

    [Parameter(Mandatory = $false)]
    [switch]$ProcessExited,

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
        private StreamWriter _writer;
        private readonly CancellationTokenSource _cts;
        private Task _drainTask;

        public volatile bool ReadyReceived;
        public volatile bool StoppedReceived;
        public int LinesRead;
        public long BytesRead;
        public long LostBytes;
        public int LostLines;
        public volatile bool IsCompleted;
        public volatile bool HasFault;
        public string FaultMessage;
        public long BytesWritten;
        public readonly long MaxBytes;

        public SafeProcessStreamDrainer(StreamReader reader, string logFilePath, Encoding encoding, long maxBytes)
        {
            if (reader == null) throw new ArgumentNullException("reader");

            _reader = reader;
            MaxBytes = maxBytes > 0 ? maxBytes : 104857600L;
            _cts = new CancellationTokenSource();

            if (string.IsNullOrWhiteSpace(logFilePath))
            {
                _writer = null;
                HasFault = true;
                FaultMessage = "Log file path is null or empty.";
                return;
            }

            try
            {
                var dir = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var fs = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(fs, encoding ?? new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                _writer = null;
                HasFault = true;
                FaultMessage = "Log writer initialization failed for '" + logFilePath + "': " + ex.Message;
            }
        }

        public SafeProcessStreamDrainer(StreamReader reader, string logFilePath, Encoding encoding)
            : this(reader, logFilePath, encoding, 104857600L)
        {
        }

        public SafeProcessStreamDrainer(Stream inputStream, string logFilePath, Encoding encoding, long maxBytes)
            : this(OpenStreamReader(inputStream, encoding), logFilePath, encoding, maxBytes)
        {
        }

        public SafeProcessStreamDrainer(Stream inputStream, string logFilePath, Encoding encoding)
            : this(inputStream, logFilePath, encoding, 104857600L)
        {
        }

        private static StreamReader OpenStreamReader(Stream inputStream, Encoding encoding)
        {
            if (inputStream == null) throw new ArgumentNullException("inputStream");
            return new StreamReader(inputStream, encoding ?? Encoding.UTF8);
        }

        public void Start()
        {
            if (_drainTask != null) return;
            _drainTask = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;

                        var lineByteCount = (long)Encoding.UTF8.GetByteCount(line) + 2;
                        Interlocked.Add(ref BytesRead, lineByteCount);
                        Interlocked.Increment(ref LinesRead);

                        if (_writer != null && !HasFault)
                        {
                            if (BytesWritten + lineByteCount > MaxBytes)
                            {
                                HasFault = true;
                                FaultMessage = "Maximum log stream size exceeded (" + MaxBytes + " bytes).";
                                try { _writer.Flush(); _writer.Dispose(); } catch {}
                                _writer = null;
                                Interlocked.Add(ref LostBytes, lineByteCount);
                                Interlocked.Increment(ref LostLines);
                                continue;
                            }

                            try
                            {
                                await _writer.WriteLineAsync(line).ConfigureAwait(false);
                                BytesWritten += lineByteCount;

                                if (line.IndexOf("READY", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    ReadyReceived = true;
                                }
                                if (line.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    StoppedReceived = true;
                                }
                            }
                            catch (Exception writeEx)
                            {
                                HasFault = true;
                                if (string.IsNullOrEmpty(FaultMessage))
                                {
                                    FaultMessage = "Log write failed: " + writeEx.Message;
                                }
                                try { _writer.Dispose(); } catch {}
                                _writer = null;
                                Interlocked.Add(ref LostBytes, lineByteCount);
                                Interlocked.Increment(ref LostLines);
                            }
                        }
                        else
                        {
                            Interlocked.Add(ref LostBytes, lineByteCount);
                            Interlocked.Increment(ref LostLines);
                        }
                    }
                    if (!HasFault)
                    {
                        IsCompleted = true;
                    }
                }
                catch (Exception ex)
                {
                    HasFault = true;
                    if (string.IsNullOrEmpty(FaultMessage))
                    {
                        FaultMessage = ex.Message;
                    }
                }
            });
        }

        public bool WaitForDrain(int timeoutMs)
        {
            if (_drainTask == null) return true;
            try
            {
                bool finished = _drainTask.Wait(timeoutMs);
                return finished && !HasFault;
            }
            catch (Exception ex)
            {
                HasFault = true;
                FaultMessage = ex.Message;
                return false;
            }
        }

        public void Dispose()
        {
            try { if (_cts != null) _cts.Cancel(); } catch {}
            try { if (_drainTask != null) _drainTask.Wait(1000); } catch {}
            try { if (_writer != null) { _writer.Flush(); _writer.Dispose(); } } catch {}
            try { if (_reader != null) _reader.Dispose(); } catch {}
            try { if (_cts != null) _cts.Dispose(); } catch {}
        }
    }

    public sealed class FaultingStream : Stream
    {
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { throw new IOException("Simulated stream fault during read."); }
    }

    public sealed class SafeInteractiveLineReader
    {
        private readonly TextReader _reader;
        private Task<string> _activeReadTask;

        public volatile bool HasFault;
        public string FaultMessage;

        public SafeInteractiveLineReader()
            : this(Console.In)
        {
        }

        public SafeInteractiveLineReader(TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            _reader = reader;
        }

        public bool PollReadLine(int timeoutMs, out string line)
        {
            line = null;
            if (_activeReadTask == null)
            {
                _activeReadTask = Task.Run(new Func<string>(ReadNextLine));
            }

            if (!_activeReadTask.Wait(timeoutMs))
            {
                return false;
            }

            try
            {
                line = _activeReadTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                HasFault = true;
                FaultMessage = ex.Message;
            }
            finally
            {
                _activeReadTask = null;
            }
            return true;
        }

        private string ReadNextLine()
        {
            try
            {
                return _reader.ReadLine();
            }
            catch (Exception ex)
            {
                HasFault = true;
                FaultMessage = ex.Message;
                return null;
            }
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

# Companion Candidate Pins (Run 40425446883 attempt 1)
$script:FROZEN_COMPANION_SRC_COMMIT   = "1bc6eab56f678c44159a39a5327b5b5329580fa3"
$script:FROZEN_COMPANION_ARTIFACT_ID  = "12699816316"
$script:FROZEN_COMPANION_ZIP_SIZE     = 66410332
$script:FROZEN_COMPANION_ZIP_SHA256   = "a824946d6ed68850b8fae29e34596d82f8115057aa26e0071c66708bd0ae39c9"
$script:FROZEN_COMPANION_PROVENANCE_ARTIFACT_ID = "12699801248"
$script:FROZEN_COMPANION_PROVENANCE_SIZE        = 324
$script:FROZEN_COMPANION_PROVENANCE_SHA256     = "3d60d599740dbfcefb9b06fd824385bae5e8de2d520520cd9488309c636f74a4"
$script:FROZEN_COMPANION_EXE_SIZE     = 71772767
$script:FROZEN_COMPANION_EXE_SHA256   = "f600b07d2d09c1300054db8beea8e1930582c3fbdd0a85aff92cf3d31b52ae2b"
$script:FROZEN_COMPANION_RUN_ID       = "40425446883"

# Official Upstream DXVK Release Pins
$script:FROZEN_DXVK_311_RELEASE_ID    = "381956359"
$script:FROZEN_DXVK_311_ASSET_ID      = "566164743"
$script:FROZEN_DXVK_311_SIZE          = 18041512
$script:FROZEN_DXVK_311_SHA256        = "40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608"

$script:FROZEN_DXVK_31_RELEASE_ID     = "357524779"
$script:FROZEN_DXVK_31_ASSET_ID       = "534051057"
$script:FROZEN_DXVK_31_SIZE           = 18056721
$script:FROZEN_DXVK_31_SHA256         = "30f9cc326874be344285582275446968cfa4c069db31ce56df312d6644179154"

# Strict Batch Budgets and Declared Numeric Limits
$script:MAX_TRIAL_DURATION_SECONDS      = 7200    # 120 minutes total
$script:MAX_HELPER_STARTS               = 4
$script:MAX_COMPANION_STARTS            = 8
$script:MAX_FIXTURE_ROOT_BYTES          = 2147483648   # 2 GiB
$script:HELPER_WATCHDOG_SECONDS         = 1500        # 25 minutes
$script:MAX_HELPER_WORKING_SET_BYTES    = 52428800    # 50 MiB
$script:MAX_COMPANION_WORKING_SET_BYTES = 1073741824   # 1 GiB (Issue #304 profile amendment)
$script:MAX_HELPER_CPU_MILLISECONDS     = 600000      # 10 minutes CPU
$script:MAX_COMPANION_CPU_MILLISECONDS  = 1800000     # 30 minutes CPU

# Required Adoption Attestation Token and Adopted Profile Relative Path
$script:REQUIRED_ADOPTION_TOKEN       = "ADOPTED-PROCESS-LIFECYCLE-V1"
$script:ADOPTED_PROFILE_RELATIVE_PATH = "docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md"

# Retained Process Handles, Stream Drainers, and Supervisor State
$script:HelperProcess = $null
$script:CompanionProcess = $null
$script:HelperStdoutDrainer = $null
$script:HelperStderrDrainer = $null
$script:CompanionStdoutDrainer = $null
$script:CompanionStderrDrainer = $null
$script:RetainedTimedOutProcesses = [System.Collections.ArrayList]::new()
$script:SettledRetainedProcessRecords = [System.Collections.ArrayList]::new()
$script:SupervisorMonotonicTimer = $null
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false

function Get-SafePropertyValue($Object, [string]$PropertyName) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Set-SafePropertyValue($Object, [string]$PropertyName, $Value) {
    if ($null -eq $Object) { return }
    if ($null -ne $Object.PSObject.Properties[$PropertyName]) {
        $Object.$PropertyName = $Value
    } else {
        $Object.PSObject.Properties.Add((New-Object System.Management.Automation.PSNoteProperty($PropertyName, $Value)))
    }
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
    [long]$MaxTotalBytes = 524288000
) {
    if (-not (Test-Path -LiteralPath $ZipFilePath -PathType Leaf)) {
        return @{ IsValid = $false; Reason = "Zip archive not found: $ZipFilePath" }
    }

    $seenEntries = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    [long]$totalUncompressedBytes = 0
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
    [long]$MaxTotalBytes = 524288000
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
        return [long]0
    }
    [long]$total = 0
    $items = Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File -Force -ErrorAction SilentlyContinue
    foreach ($item in $items) {
        $total += $item.Length
    }
    return $total
}

# ==============================================================================
# State and Batch Guard Functions
# ==============================================================================

function Get-ProfileDocumentPath {
    if (-not [string]::IsNullOrWhiteSpace($PSCommandPath) -and (Test-Path -LiteralPath $PSCommandPath -PathType Leaf)) {
        $scriptDir = [System.IO.Path]::GetDirectoryName($PSCommandPath)
        $repoRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($scriptDir, "..", ".."))
        return [System.IO.Path]::Combine($repoRoot, "docs", "validation", "PACKAGED-PROCESS-LIFECYCLE-V1.md")
    }
    return [System.IO.Path]::GetFullPath("docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md")
}

function Get-ProfileDocumentSha256 {
    $profilePath = Get-ProfileDocumentPath
    if (Test-Path -LiteralPath $profilePath -PathType Leaf) {
        return Compute-FileSha256 -FilePath $profilePath
    }
    return ""
}

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
    $obj = ConvertFrom-Json -InputObject $raw

    $defaults = @{
        BatchStartTimeUtc          = ""
        TrialStartUtc              = ""
        SetupElapsedMilliseconds   = 0L
        ElapsedMilliseconds        = 0L
        HelperStartAttempts        = 0
        CompanionStartAttempts     = 0
        MaxHelperStarts            = $script:MAX_HELPER_STARTS
        MaxCompanionStarts         = $script:MAX_COMPANION_STARTS
        MaxDurationSeconds         = $script:MAX_TRIAL_DURATION_SECONDS
        MaxFootprintBytes          = $script:MAX_FIXTURE_ROOT_BYTES
        UsedSlots                  = @()
        CurrentCheckpoint          = ""
        LastCheckpointOutcome      = "None"
        LastFailedCheckpoint       = $null
        LastFailedReason           = $null
        TerminalHold               = $false
        TerminalHoldReason         = $null
        CurrentReadiness           = "NotReady"
        ReadinessReason            = ""
        CurrentFootprintBytes      = 0L
        ActiveProcesses            = @()
        ProcessMemoryObservations  = @()
        FirstResourceHold          = $null
        SetupScriptSha256          = $null
        SetupProfileSha256         = $null
        AdoptedProfilePath         = $null
        AdoptedProfileSha256       = $null
        AdoptedScriptSha256        = $null
        AdoptedToken               = $null
        AdoptedSupervisorPid       = $null
        AdoptedSupervisorSessionId = $null
        MonotonicOriginTimestamp   = $null
        MonotonicFrequency         = $null
        LastMonotonicTimestamp     = $null
        AccumulatedMonotonicMs     = $null
        LastWallClockUtc           = $null
        LastMonotonicSessionPid    = $null
        IsEnded                    = $false
        TestPinsUsed               = $false
        ActiveHelperPid             = $null
        ActiveHelperSlot            = $null
        ActiveHelperInvocationId    = $null
        ActiveHelperStartTimeUtc    = $null
        LastStoppedHelperSlot       = $null
        NonQualifyingPureDataCheck  = $false
        ActiveCompanionPid          = $null
        ActiveCompanionInvocationId = $null
        ActiveSupervisorPid         = $null
        ActiveSupervisorSessionId   = $null
        Checkpoints                = @()
    }

    foreach ($key in $defaults.Keys) {
        if ($null -eq $obj.PSObject.Properties[$key]) {
            $obj.PSObject.Properties.Add((New-Object System.Management.Automation.PSNoteProperty($key, $defaults[$key])))
        }
    }

    return $obj
}

function Save-StateData([string]$FixtureRoot, $StateObject) {
    $statePath = Get-StateFilePath -FixtureRoot $FixtureRoot
    $json = ConvertTo-Json -InputObject $StateObject -Depth 10
    [System.IO.File]::WriteAllText($statePath, $json, $script:Utf8NoBom)
}

function Get-EffectiveReferenceUtc {
    if (-not [string]::IsNullOrWhiteSpace($TestCurrentUtc)) {
        return [DateTime]::Parse($TestCurrentUtc, $null, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    }
    return [DateTime]::UtcNow
}

function Get-EffectiveMonotonicTick {
    if ($TestMonotonicTick -ge 0) {
        return $TestMonotonicTick
    }
    return [System.Diagnostics.Stopwatch]::GetTimestamp()
}

function Get-EffectiveMonotonicFrequency {
    if ($TestMonotonicFrequency -gt 0) {
        return $TestMonotonicFrequency
    }
    return [System.Diagnostics.Stopwatch]::Frequency
}

function Validate-CooperativeReadinessInternal(
    [string]$FixtureRoot,
    [object]$StateObject,
    [string]$Attestation = "",
    [string]$ExplicitReadinessPath = "",
    [string]$ExplicitReadinessJson = "",
    [DateTime]$ReferenceUtc = ([DateTime]::UtcNow)
) {
    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    $rawJson = $null

    if (-not [string]::IsNullOrWhiteSpace($ExplicitReadinessJson)) {
        $rawJson = $ExplicitReadinessJson
    } elseif (-not [string]::IsNullOrWhiteSpace($ExplicitReadinessPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitReadinessPath -PathType Leaf)) {
            return @{ IsValid = $false; Reason = "Execution refused: explicit readiness record file not found at '$ExplicitReadinessPath'." }
        }
        $rawJson = [System.IO.File]::ReadAllText($ExplicitReadinessPath, $script:Utf8NoBom)
    } else {
        $defaultPath = [System.IO.Path]::Combine($canonRoot, "control", "readiness.json")
        if (-not (Test-Path -LiteralPath $defaultPath -PathType Leaf)) {
            return @{ IsValid = $false; Reason = "Execution refused: missing explicit owner readiness record (expected control/readiness.json or -ReadinessRecordPath)." }
        }
        $rawJson = [System.IO.File]::ReadAllText($defaultPath, $script:Utf8NoBom)
    }

    $rec = $null
    try {
        $rec = ConvertFrom-Json -InputObject $rawJson
    } catch {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record is invalid JSON: $_" }
    }

    if ($null -eq $rec) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record is empty." }
    }

    $recToken = "$(Get-SafePropertyValue -Object $rec -PropertyName 'AdoptionToken')"
    if ($recToken -ne $script:REQUIRED_ADOPTION_TOKEN) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record AdoptionToken '$recToken' does not match required token '$($script:REQUIRED_ADOPTION_TOKEN)'." }
    }
    if (-not [string]::IsNullOrWhiteSpace($Attestation) -and $Attestation -ne $recToken) {
        return @{ IsValid = $false; Reason = "Execution refused: adoption attestation parameter does not match readiness record AdoptionToken." }
    }

    $recProfile = "$(Get-SafePropertyValue -Object $rec -PropertyName 'ProfileId')"
    if ($recProfile -ne "process-lifecycle-v1") {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record ProfileId '$recProfile' does not match 'process-lifecycle-v1'." }
    }

    $recProfilePath = "$(Get-SafePropertyValue -Object $rec -PropertyName 'AdoptedProfilePath')".Replace('\', '/').Trim()
    if ([string]::IsNullOrWhiteSpace($recProfilePath) -or $recProfilePath -ne $script:ADOPTED_PROFILE_RELATIVE_PATH) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record AdoptedProfilePath ('$recProfilePath') does not match required profile path '$($script:ADOPTED_PROFILE_RELATIVE_PATH)'." }
    }

    $currentProfileSha = (Get-ProfileDocumentSha256).ToLowerInvariant()
    $recProfileSha = "$(Get-SafePropertyValue -Object $rec -PropertyName 'AdoptedProfileSha256')".ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($recProfileSha) -or [string]::IsNullOrWhiteSpace($currentProfileSha) -or $recProfileSha -ne $currentProfileSha) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record AdoptedProfileSha256 ('$recProfileSha') does not match current profile document SHA256 ('$currentProfileSha')." }
    }
    $setupProfileSha = "$(Get-SafePropertyValue -Object $StateObject -PropertyName 'SetupProfileSha256')".ToLowerInvariant()
    if (-not [string]::IsNullOrWhiteSpace($setupProfileSha) -and $recProfileSha -ne $setupProfileSha) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record AdoptedProfileSha256 ('$recProfileSha') does not match batch SetupProfileSha256 ('$setupProfileSha')." }
    }

    $currentScriptSha = if (-not [string]::IsNullOrWhiteSpace($PSCommandPath) -and (Test-Path -LiteralPath $PSCommandPath -PathType Leaf)) {
        Compute-FileSha256 -FilePath $PSCommandPath
    } else {
        ""
    }
    $recScriptSha = "$(Get-SafePropertyValue -Object $rec -PropertyName 'AdoptedScriptSha256')".ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($recScriptSha) -or $recScriptSha -ne $currentScriptSha.ToLowerInvariant()) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record AdoptedScriptSha256 ('$recScriptSha') does not match running script SHA256 ('$currentScriptSha'). Auto-adoption is prohibited." }
    }
    $setupScriptSha = "$(Get-SafePropertyValue -Object $StateObject -PropertyName 'SetupScriptSha256')".ToLowerInvariant()
    if (-not [string]::IsNullOrWhiteSpace($setupScriptSha) -and $recScriptSha -ne $setupScriptSha) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record AdoptedScriptSha256 ('$recScriptSha') does not match batch SetupScriptSha256 ('$setupScriptSha')." }
    }

    $recRootRaw = "$(Get-SafePropertyValue -Object $rec -PropertyName 'FixtureRoot')"
    if ([string]::IsNullOrWhiteSpace($recRootRaw)) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record is missing FixtureRoot binding." }
    }
    $recRootCanon = [System.IO.Path]::GetFullPath($recRootRaw).TrimEnd('\', '/')
    if (-not [string]::Equals($recRootCanon, $canonRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record FixtureRoot ('$recRootCanon') does not match active FixtureRoot ('$canonRoot')." }
    }

    $recBatchUtc = "$(Get-SafePropertyValue -Object $rec -PropertyName 'BatchStartTimeUtc')"
    if ([string]::IsNullOrWhiteSpace($recBatchUtc) -or $recBatchUtc -ne "$($StateObject.BatchStartTimeUtc)") {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record BatchStartTimeUtc ('$recBatchUtc') does not match batch state ('$($StateObject.BatchStartTimeUtc)')." }
    }

    $activePidObj = Get-SafePropertyValue -Object $StateObject -PropertyName "ActiveSupervisorPid"
    $activeSessionId = "$(Get-SafePropertyValue -Object $StateObject -PropertyName 'ActiveSupervisorSessionId')"
    if ($null -eq $activePidObj -or "$activePidObj" -eq "" -or [int]$activePidObj -le 0 -or [string]::IsNullOrWhiteSpace($activeSessionId)) {
        return @{ IsValid = $false; Reason = "Execution refused: no active supervisor session is bound in state.json (ActiveSupervisorPid/ActiveSupervisorSessionId missing)." }
    }
    $activePid = [int]$activePidObj

    $recPid = Get-SafePropertyValue -Object $rec -PropertyName "SupervisorPid"
    $parsedRecPid = 0
    if ($null -eq $recPid -or "$recPid" -eq "" -or -not [int]::TryParse("$recPid", [ref]$parsedRecPid) -or $parsedRecPid -le 0) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record is missing a valid positive SupervisorPid binding." }
    }
    if ($parsedRecPid -ne $activePid) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record SupervisorPid ($parsedRecPid) does not match active supervisor session PID ($activePid)." }
    }

    $recSessionId = "$(Get-SafePropertyValue -Object $rec -PropertyName 'SupervisorSessionId')"
    if ([string]::IsNullOrWhiteSpace($recSessionId)) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record is missing mandatory SupervisorSessionId binding." }
    }
    if ($recSessionId -ne $activeSessionId) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record SupervisorSessionId ('$recSessionId') does not match active supervisor session ID ('$activeSessionId')." }
    }

    $ownerAvail = Get-SafePropertyValue -Object $rec -PropertyName "OwnerAvailable"
    if ($ownerAvail -ne $true) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record does not confirm OwnerAvailable = true." }
    }

    $closedGames = Get-SafePropertyValue -Object $rec -PropertyName "GamesAndCompanionClosed"
    if ($closedGames -ne $true) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record does not confirm GamesAndCompanionClosed = true." }
    }

    $confirmedRaw = "$(Get-SafePropertyValue -Object $rec -PropertyName 'ConfirmedAtUtc')"
    if ([string]::IsNullOrWhiteSpace($confirmedRaw)) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record is missing ConfirmedAtUtc timestamp." }
    }
    $confirmedUtc = [DateTime]::MinValue
    try {
        $confirmedUtc = [DateTime]::Parse($confirmedRaw, $null, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    } catch {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record ConfirmedAtUtc ('$confirmedRaw') is not a valid ISO-8601 timestamp." }
    }

    $ageSeconds = ($ReferenceUtc - $confirmedUtc).TotalSeconds
    if ($ageSeconds -lt -300) {
        return @{ IsValid = $false; Reason = "Execution refused: readiness record ConfirmedAtUtc ('$confirmedRaw') is in the future relative to reference clock." }
    }
    if ($ageSeconds -gt 1800) {
        return @{ IsValid = $false; Reason = "Execution refused: stale readiness confirmation (ConfirmedAtUtc '$confirmedRaw' is $([int]$ageSeconds)s old; max freshness is 1800s)." }
    }

    return @{
        IsValid                    = $true
        Reason                     = "Ready"
        AdoptedProfilePath         = $recProfilePath
        AdoptedProfileSha256       = $recProfileSha
        AdoptedScriptSha256        = $recScriptSha
        AdoptedToken               = $recToken
        AdoptedSupervisorPid       = $parsedRecPid
        AdoptedSupervisorSessionId = $recSessionId
    }
}

function Register-ResourceHoldInternal(
    [Parameter(Mandatory = $true)] $State,
    [Parameter(Mandatory = $true)] [string]$Metric,
    [Parameter(Mandatory = $false)] $ObservedValue = $null,
    [Parameter(Mandatory = $false)] $ConfiguredLimit = $null,
    [Parameter(Mandatory = $false)] [string]$Unit = "",
    [Parameter(Mandatory = $false)] $Role = $null,
    [Parameter(Mandatory = $false)] $TargetPid = $null,
    [Parameter(Mandatory = $true)] [string]$Reason = "",
    [Parameter(Mandatory = $true)] [string]$CheckpointName = "",
    [Parameter(Mandatory = $false)] [string]$InvocationId = $null
) {
    $State.TerminalHold = $true
    if ($null -eq $State.TerminalHoldReason -or [string]::IsNullOrWhiteSpace("$($State.TerminalHoldReason)")) {
        $State.TerminalHoldReason = $Reason
    }

    if ($null -eq $State.FirstResourceHold) {
        $nowUtc = Get-EffectiveReferenceUtc
        $effectiveInvId = if (-not [string]::IsNullOrWhiteSpace($InvocationId)) { $InvocationId } else { $null }
        $State.FirstResourceHold = [PSCustomObject]@{
            TimestampUtc    = $nowUtc.ToString("o")
            Metric          = $Metric
            Role            = $Role
            Pid             = $TargetPid
            InvocationId    = $effectiveInvId
            ObservedValue   = $ObservedValue
            ConfiguredLimit = $ConfiguredLimit
            Unit            = $Unit
            Reason          = $Reason
            Checkpoint      = $CheckpointName
        }
    }

    $State.LastCheckpointOutcome = "Failed"
    $State.LastFailedCheckpoint = $CheckpointName
    $State.LastFailedReason = $Reason
}

function Update-ProcessMemoryObservationInternal(
    [Parameter(Mandatory = $true)] $State,
    [Parameter(Mandatory = $true)] [string]$Role,
    [Parameter(Mandatory = $true)] [int]$TargetPid,
    [Parameter(Mandatory = $false)] $Slot = $null,
    [Parameter(Mandatory = $true)] [long]$WorkingSetBytes,
    [Parameter(Mandatory = $false)] $PeakWorkingSetBytes = $null,
    [Parameter(Mandatory = $false)] [bool]$OsPeakAvailable = $false,
    [Parameter(Mandatory = $false)] [long]$CpuMilliseconds = 0L,
    [Parameter(Mandatory = $false)] $SampledAtUtc = $null,
    [Parameter(Mandatory = $false)] [string]$InvocationId = $null
) {
    $cap = if ($Role -eq "Companion") { $script:MAX_COMPANION_WORKING_SET_BYTES } else { $script:MAX_HELPER_WORKING_SET_BYTES }
    $effectiveTime = if ($null -ne $SampledAtUtc -and ($SampledAtUtc -is [DateTime])) { $SampledAtUtc } else { [DateTime]::UtcNow }
    $timeStr = $effectiveTime.ToString("o")

    # Determine caller or state invocation identity
    $effectiveInvId = $InvocationId
    if ([string]::IsNullOrWhiteSpace($effectiveInvId)) {
        if ($Role -eq "Helper") {
            $stateHelperInvId = Get-SafePropertyValue -Object $State -PropertyName "ActiveHelperInvocationId"
            if (-not [string]::IsNullOrWhiteSpace("$stateHelperInvId")) {
                $effectiveInvId = "$stateHelperInvId"
            }
        } elseif ($Role -eq "Companion") {
            $stateCompInvId = Get-SafePropertyValue -Object $State -PropertyName "ActiveCompanionInvocationId"
            if (-not [string]::IsNullOrWhiteSpace("$stateCompInvId")) {
                $effectiveInvId = "$stateCompInvId"
            }
        }
    }

    $retainedList = [System.Collections.ArrayList]::new()
    $existing = $null
    $matchingRolePidCount = 0
    if ($null -ne $State.ProcessMemoryObservations) {
        foreach ($item in (Get-SafeEnumerable $State.ProcessMemoryObservations)) {
            if ($null -ne $item) {
                $itemPid = Get-SafePropertyValue -Object $item -PropertyName "Pid"
                $itemRole = Get-SafePropertyValue -Object $item -PropertyName "Role"
                $itemLive = Get-SafePropertyValue -Object $item -PropertyName "IsLive"
                $itemInvId = Get-SafePropertyValue -Object $item -PropertyName "InvocationId"

                if ($null -ne $itemPid -and [int]$itemPid -eq $TargetPid -and "$itemRole" -eq $Role) {
                    $matchingRolePidCount++
                    # Match existing only if it is currently live (never revive an exited/settled record)
                    $isLiveBool = ($null -ne $itemLive -and [bool]$itemLive -eq $true)
                    if ($isLiveBool) {
                        if (-not [string]::IsNullOrWhiteSpace($effectiveInvId)) {
                            if ("$itemInvId" -eq "$effectiveInvId") {
                                $existing = $item
                            }
                        } else {
                            $existing = $item
                        }
                    }
                }
                [void]$retainedList.Add($item)
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($effectiveInvId)) {
        if ($null -ne $existing -and -not [string]::IsNullOrWhiteSpace("$(Get-SafePropertyValue -Object $existing -PropertyName 'InvocationId')")) {
            $effectiveInvId = "$(Get-SafePropertyValue -Object $existing -PropertyName 'InvocationId')"
        } else {
            $effectiveInvId = "$Role-$TargetPid-inv-$($matchingRolePidCount + 1)"
        }
    }

    if ($null -ne $existing) {
        $existing.SampleCount = [int]$existing.SampleCount + 1
        $existing.LastSampledAtUtc = $timeStr
        $existing.LastSampledWorkingSetBytes = $WorkingSetBytes
        $existing.CurrentWorkingSetBytes = $WorkingSetBytes
        if ($WorkingSetBytes -gt [long]$existing.MaxSampledWorkingSetBytes) {
            $existing.MaxSampledWorkingSetBytes = $WorkingSetBytes
        }
        if ($OsPeakAvailable -and $null -ne $PeakWorkingSetBytes) {
            $existing.OsPeakAvailable = $true
            if ($null -eq $existing.OsReportedPeakWorkingSetBytes -or [long]$PeakWorkingSetBytes -gt [long]$existing.OsReportedPeakWorkingSetBytes) {
                $existing.OsReportedPeakWorkingSetBytes = [long]$PeakWorkingSetBytes
            }
        }
        $existing.TotalCpuMilliseconds = $CpuMilliseconds
        if ($CpuMilliseconds -gt [long]$existing.MaxCpuMilliseconds) {
            $existing.MaxCpuMilliseconds = $CpuMilliseconds
        }
        $existing.IsLive = $true
        if ([string]::IsNullOrWhiteSpace("$(Get-SafePropertyValue -Object $existing -PropertyName 'InvocationId')")) {
            Set-SafePropertyValue -Object $existing -PropertyName "InvocationId" -Value $effectiveInvId
        }
    } else {
        $peakVal = if ($OsPeakAvailable -and $null -ne $PeakWorkingSetBytes) { [long]$PeakWorkingSetBytes } else { $null }
        $newRecord = [PSCustomObject]@{
            InvocationId                  = $effectiveInvId
            Role                          = $Role
            Slot                          = $Slot
            Pid                           = $TargetPid
            ConfiguredWorkingSetCapBytes  = $cap
            Unit                          = "bytes"
            SampleCount                   = 1
            FirstSampledAtUtc             = $timeStr
            LastSampledAtUtc              = $timeStr
            LastSampledWorkingSetBytes    = $WorkingSetBytes
            CurrentWorkingSetBytes        = $WorkingSetBytes
            MaxSampledWorkingSetBytes     = $WorkingSetBytes
            OsReportedPeakWorkingSetBytes = $peakVal
            OsPeakAvailable               = $OsPeakAvailable
            TotalCpuMilliseconds         = $CpuMilliseconds
            MaxCpuMilliseconds            = $CpuMilliseconds
            IsLive                        = $true
        }
        [void]$retainedList.Add($newRecord)
    }

    $State.ProcessMemoryObservations = @($retainedList)
}

function Evaluate-ProcessResourceThresholdsInternal(
    [Parameter(Mandatory = $true)] $State,
    [Parameter(Mandatory = $true)] [string]$Role,
    [Parameter(Mandatory = $true)] [int]$TargetPid,
    [Parameter(Mandatory = $true)] [long]$WorkingSetBytes,
    [Parameter(Mandatory = $false)] [long]$CpuMilliseconds = 0L,
    [Parameter(Mandatory = $false)] [string]$InvocationId = $null
) {
    if ($Role -eq "Helper") {
        if ($WorkingSetBytes -gt $script:MAX_HELPER_WORKING_SET_BYTES) {
            Register-ResourceHoldInternal `
                -State $State `
                -Metric "WorkingSet" `
                -ObservedValue $WorkingSetBytes `
                -ConfiguredLimit $script:MAX_HELPER_WORKING_SET_BYTES `
                -Unit "bytes" `
                -Role "Helper" `
                -TargetPid $TargetPid `
                -Reason "Helper working set ($WorkingSetBytes bytes) exceeds limit ($($script:MAX_HELPER_WORKING_SET_BYTES) bytes)." `
                -CheckpointName "HelperMemoryExhausted" `
                -InvocationId $InvocationId
        }
        if ($CpuMilliseconds -gt $script:MAX_HELPER_CPU_MILLISECONDS) {
            Register-ResourceHoldInternal `
                -State $State `
                -Metric "CpuTime" `
                -ObservedValue $CpuMilliseconds `
                -ConfiguredLimit $script:MAX_HELPER_CPU_MILLISECONDS `
                -Unit "milliseconds" `
                -Role "Helper" `
                -TargetPid $TargetPid `
                -Reason "Helper CPU time ($CpuMilliseconds ms) exceeds limit ($($script:MAX_HELPER_CPU_MILLISECONDS) ms)." `
                -CheckpointName "HelperCpuExhausted" `
                -InvocationId $InvocationId
        }
    } elseif ($Role -eq "Companion") {
        if ($WorkingSetBytes -gt $script:MAX_COMPANION_WORKING_SET_BYTES) {
            Register-ResourceHoldInternal `
                -State $State `
                -Metric "WorkingSet" `
                -ObservedValue $WorkingSetBytes `
                -ConfiguredLimit $script:MAX_COMPANION_WORKING_SET_BYTES `
                -Unit "bytes" `
                -Role "Companion" `
                -TargetPid $TargetPid `
                -Reason "Companion working set ($WorkingSetBytes bytes) exceeds limit ($($script:MAX_COMPANION_WORKING_SET_BYTES) bytes)." `
                -CheckpointName "CompanionMemoryExhausted" `
                -InvocationId $InvocationId
        }
        if ($CpuMilliseconds -gt $script:MAX_COMPANION_CPU_MILLISECONDS) {
            Register-ResourceHoldInternal `
                -State $State `
                -Metric "CpuTime" `
                -ObservedValue $CpuMilliseconds `
                -ConfiguredLimit $script:MAX_COMPANION_CPU_MILLISECONDS `
                -Unit "milliseconds" `
                -Role "Companion" `
                -TargetPid $TargetPid `
                -Reason "Companion CPU time ($CpuMilliseconds ms) exceeds limit ($($script:MAX_COMPANION_CPU_MILLISECONDS) ms)." `
                -CheckpointName "CompanionCpuExhausted" `
                -InvocationId $InvocationId
        }
    }
}

function Settle-ProcessMemoryObservationsInternal(
    [Parameter(Mandatory = $true)] $State,
    [Parameter(Mandatory = $false)] $ActiveProcesses = @()
) {
    if ($null -eq $State.ProcessMemoryObservations) { return }
    $activePids = [System.Collections.ArrayList]::new()
    $activeInvIds = [System.Collections.ArrayList]::new()
    if ($null -ne $ActiveProcesses) {
        foreach ($ap in (Get-SafeEnumerable $ActiveProcesses)) {
            if ($null -ne $ap) {
                $pidVal = Get-SafePropertyValue -Object $ap -PropertyName "Pid"
                if ($null -ne $pidVal) { [void]$activePids.Add([int]$pidVal) }
                $invVal = Get-SafePropertyValue -Object $ap -PropertyName "InvocationId"
                if ($null -ne $invVal -and -not [string]::IsNullOrWhiteSpace("$invVal")) {
                    [void]$activeInvIds.Add("$invVal")
                }
            }
        }
    }

    foreach ($obs in (Get-SafeEnumerable $State.ProcessMemoryObservations)) {
        if ($null -ne $obs) {
            $obsPid = Get-SafePropertyValue -Object $obs -PropertyName "Pid"
            $obsInvId = Get-SafePropertyValue -Object $obs -PropertyName "InvocationId"
            $isStillActive = $false
            if ($null -ne $obsInvId -and -not [string]::IsNullOrWhiteSpace("$obsInvId") -and $activeInvIds.Count -gt 0) {
                if ($activeInvIds.Contains("$obsInvId")) {
                    $isStillActive = $true
                }
            } elseif ($null -ne $obsPid) {
                if ($activePids.Contains([int]$obsPid)) {
                    $isStillActive = $true
                }
            }
            if (-not $isStillActive) {
                $obs.IsLive = $false
                $obs.CurrentWorkingSetBytes = $null
            }
        }
    }
}

function Invoke-SuperviseObservationCycle([string]$FixtureRoot) {
    $canon = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    $state = Get-StateData -FixtureRoot $canon

    $nowUtc = Get-EffectiveReferenceUtc
    $currentTick = Get-EffectiveMonotonicTick
    $currentFreq = Get-EffectiveMonotonicFrequency

    # 1. Real monotonic elapsed accounting from Setup start with conservative wall-clock cross-check
    $priorElapsedMs = if ($null -ne $state.ElapsedMilliseconds -and [long]$state.ElapsedMilliseconds -gt 0) { [long]$state.ElapsedMilliseconds } else { 0L }
    $priorAccumMonoMs = if ($null -ne $state.AccumulatedMonotonicMs -and [long]$state.AccumulatedMonotonicMs -gt 0) { [long]$state.AccumulatedMonotonicMs } else { $priorElapsedMs }

    $monoValid = $true
    $monoInvalidReason = $null

    if ($null -eq $state.MonotonicOriginTimestamp -or [long]$state.MonotonicOriginTimestamp -le 0) {
        $monoValid = $false
        $monoInvalidReason = "Missing or non-positive MonotonicOriginTimestamp in state.json."
    } elseif ($null -eq $state.MonotonicFrequency -or [long]$state.MonotonicFrequency -le 0) {
        $monoValid = $false
        $monoInvalidReason = "Missing or non-positive MonotonicFrequency in state.json."
    } elseif ($null -eq $state.LastMonotonicTimestamp -or [long]$state.LastMonotonicTimestamp -lt [long]$state.MonotonicOriginTimestamp) {
        $monoValid = $false
        $monoInvalidReason = "Invalid LastMonotonicTimestamp (precedes MonotonicOriginTimestamp) in state.json."
    } elseif ([long]$state.MonotonicFrequency -ne $currentFreq) {
        $monoValid = $false
        $monoInvalidReason = "MonotonicFrequency changed ($($state.MonotonicFrequency) vs $currentFreq); monotonic continuity invalidated."
    } elseif ($currentTick -lt [long]$state.LastMonotonicTimestamp) {
        $monoValid = $false
        $monoInvalidReason = "Monotonic timestamp regressed ($currentTick < $($state.LastMonotonicTimestamp)); monotonic continuity invalidated."
    }

    $wallTotalMs = 0L
    $wallDeltaMs = 0L
    if ([string]::IsNullOrWhiteSpace($state.TrialStartUtc)) {
        $monoValid = $false
        $monoInvalidReason = "Missing TrialStartUtc in state.json."
    } else {
        try {
            $trialStart = [DateTime]::Parse($state.TrialStartUtc, $null, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
            $wallTotalMs = [Math]::Max(0L, [long]($nowUtc - $trialStart).TotalMilliseconds)
        } catch {
            $monoValid = $false
            $monoInvalidReason = "Malformed TrialStartUtc in state.json: $_"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($state.LastWallClockUtc)) {
        try {
            $lastWall = [DateTime]::Parse($state.LastWallClockUtc, $null, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
            $wallDeltaMs = [Math]::Max(0L, [long]($nowUtc - $lastWall).TotalMilliseconds)
        } catch {}
    }

    if (-not $monoValid) {
        # Fail closed on missing or invalid monotonic state without resetting prior elapsed time
        $conservativeElapsed = [Math]::Max($priorElapsedMs, [Math]::Max($priorAccumMonoMs, ($priorElapsedMs + $wallDeltaMs)))
        $conservativeElapsed = [Math]::Max($conservativeElapsed, $wallTotalMs)
        $state.ElapsedMilliseconds = $conservativeElapsed
        $state.AccumulatedMonotonicMs = $conservativeElapsed
        Register-ResourceHoldInternal `
            -State $state `
            -Metric "MonotonicClock" `
            -ObservedValue $null `
            -ConfiguredLimit $null `
            -Unit "" `
            -Reason "Monotonic clock integrity failure: $monoInvalidReason" `
            -CheckpointName "MonotonicClockIntegrity"
    } else {
        $freq = [double]([long]$state.MonotonicFrequency)
        $originTick = [long]$state.MonotonicOriginTimestamp
        $lastTick = [long]$state.LastMonotonicTimestamp

        $deltaMonoMs = [long][Math]::Floor((($currentTick - $lastTick) * 1000.0) / $freq)
        $originMonoMs = [long][Math]::Floor((($currentTick - $originTick) * 1000.0) / $freq)

        # Across supervisor restarts (different PID), conservatively cover any positive wall-clock gap
        $effectiveDeltaMs = $deltaMonoMs
        if ($null -ne $state.LastMonotonicSessionPid -and [int]$state.LastMonotonicSessionPid -ne $PID) {
            $effectiveDeltaMs = [Math]::Max($deltaMonoMs, $wallDeltaMs)
        }

        $newAccumMonoMs = [Math]::Max(($priorAccumMonoMs + $effectiveDeltaMs), $originMonoMs)
        $newElapsedMs = [Math]::Max($priorElapsedMs, [Math]::Max($newAccumMonoMs, $wallTotalMs))

        $state.AccumulatedMonotonicMs = $newElapsedMs
        $state.ElapsedMilliseconds = $newElapsedMs
        $state.LastMonotonicTimestamp = $currentTick
        $state.LastWallClockUtc = $nowUtc.ToString("o")
        $state.LastMonotonicSessionPid = $PID
    }

    $elapsedSeconds = [long]($state.ElapsedMilliseconds / 1000)

    # Check trial duration limit
    if ($elapsedSeconds -gt $script:MAX_TRIAL_DURATION_SECONDS) {
        Register-ResourceHoldInternal `
            -State $state `
            -Metric "Duration" `
            -ObservedValue $elapsedSeconds `
            -ConfiguredLimit $script:MAX_TRIAL_DURATION_SECONDS `
            -Unit "seconds" `
            -Reason "Trial duration budget ($($script:MAX_TRIAL_DURATION_SECONDS)s) exceeded ($($elapsedSeconds)s)." `
            -CheckpointName "TrialDurationExhausted"
    }

    # 2. Check root storage footprint
    $currentFootprint = Measure-DirectoryFootprintBytes -DirectoryPath $canon
    $state.CurrentFootprintBytes = $currentFootprint
    if ($currentFootprint -gt $script:MAX_FIXTURE_ROOT_BYTES) {
        Register-ResourceHoldInternal `
            -State $state `
            -Metric "Footprint" `
            -ObservedValue $currentFootprint `
            -ConfiguredLimit $script:MAX_FIXTURE_ROOT_BYTES `
            -Unit "bytes" `
            -Reason "Fixture root footprint ($currentFootprint bytes) exceeds 2 GiB limit ($($script:MAX_FIXTURE_ROOT_BYTES) bytes)." `
            -CheckpointName "FootprintExhausted"
    }

    # 3. Sample active child processes (CPU time & working set) and evaluate thresholds
    $activeProcesses = @()
    if ($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) {
        try {
            $script:HelperProcess.Refresh()
            $cpuMs = [long]$script:HelperProcess.TotalProcessorTime.TotalMilliseconds
            $wsBytes = [long]$script:HelperProcess.WorkingSet64
            $peakWs = $null
            $peakWsOk = $false
            try {
                $peakWs = [long]$script:HelperProcess.PeakWorkingSet64
                $peakWsOk = $true
            } catch {
                $peakWs = $null
                $peakWsOk = $false
            }
            $stateHelperInvId = Get-SafePropertyValue -Object $state -PropertyName "ActiveHelperInvocationId"
            $helperInvId = if (-not [string]::IsNullOrWhiteSpace("$stateHelperInvId")) {
                "$stateHelperInvId"
            } else {
                "Helper-slot-$($state.ActiveHelperSlot)-start-$($state.HelperStartAttempts)"
            }
            $activeProcesses += [PSCustomObject]@{
                Role                 = "Helper"
                Slot                 = $state.ActiveHelperSlot
                Pid                  = $script:HelperProcess.Id
                InvocationId         = $helperInvId
                TotalCpuMilliseconds = $cpuMs
                WorkingSetBytes      = $wsBytes
                PeakWorkingSetBytes  = $peakWs
                SampledAtUtc         = $nowUtc.ToString("o")
            }

            Update-ProcessMemoryObservationInternal `
                -State $state `
                -Role "Helper" `
                -TargetPid $script:HelperProcess.Id `
                -Slot $state.ActiveHelperSlot `
                -WorkingSetBytes $wsBytes `
                -PeakWorkingSetBytes $peakWs `
                -OsPeakAvailable $peakWsOk `
                -CpuMilliseconds $cpuMs `
                -SampledAtUtc $nowUtc `
                -InvocationId $helperInvId

            Evaluate-ProcessResourceThresholdsInternal `
                -State $state `
                -Role "Helper" `
                -TargetPid $script:HelperProcess.Id `
                -WorkingSetBytes $wsBytes `
                -CpuMilliseconds $cpuMs `
                -InvocationId $helperInvId
        } catch {}
    }

    if ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited) {
        try {
            $script:CompanionProcess.Refresh()
            $cpuMs = [long]$script:CompanionProcess.TotalProcessorTime.TotalMilliseconds
            $wsBytes = [long]$script:CompanionProcess.WorkingSet64
            $peakWs = $null
            $peakWsOk = $false
            try {
                $peakWs = [long]$script:CompanionProcess.PeakWorkingSet64
                $peakWsOk = $true
            } catch {
                $peakWs = $null
                $peakWsOk = $false
            }
            $stateCompInvId = Get-SafePropertyValue -Object $state -PropertyName "ActiveCompanionInvocationId"
            $compInvId = if (-not [string]::IsNullOrWhiteSpace("$stateCompInvId")) {
                "$stateCompInvId"
            } else {
                "Companion-start-$($state.CompanionStartAttempts)"
            }
            $activeProcesses += [PSCustomObject]@{
                Role                 = "Companion"
                Pid                  = $script:CompanionProcess.Id
                InvocationId         = $compInvId
                TotalCpuMilliseconds = $cpuMs
                WorkingSetBytes      = $wsBytes
                PeakWorkingSetBytes  = $peakWs
                SampledAtUtc         = $nowUtc.ToString("o")
            }

            Update-ProcessMemoryObservationInternal `
                -State $state `
                -Role "Companion" `
                -TargetPid $script:CompanionProcess.Id `
                -Slot $null `
                -WorkingSetBytes $wsBytes `
                -PeakWorkingSetBytes $peakWs `
                -OsPeakAvailable $peakWsOk `
                -CpuMilliseconds $cpuMs `
                -SampledAtUtc $nowUtc `
                -InvocationId $compInvId

            Evaluate-ProcessResourceThresholdsInternal `
                -State $state `
                -Role "Companion" `
                -TargetPid $script:CompanionProcess.Id `
                -WorkingSetBytes $wsBytes `
                -CpuMilliseconds $cpuMs `
                -InvocationId $compInvId
        } catch {}
    }
    $state.ActiveProcesses = $activeProcesses
    Settle-ProcessMemoryObservationsInternal -State $state -ActiveProcesses $activeProcesses

    # 4. Fail-closed target & settings validation
    $companionSettingsPath = [System.IO.Path]::Combine($canon, "companion", "settings.json")
    if (Test-Path -LiteralPath $companionSettingsPath -PathType Leaf) {
        $settingsRaw = [System.IO.File]::ReadAllText($companionSettingsPath, $script:Utf8NoBom)
        $settingsObj = ConvertFrom-Json -InputObject $settingsRaw
        if ("$($settingsObj.GlobalPolicy)" -ne "Manual") {
            Register-ResourceHoldInternal `
                -State $state `
                -Metric "Settings" `
                -ObservedValue "$($settingsObj.GlobalPolicy)" `
                -ConfiguredLimit "Manual" `
                -Unit "" `
                -Reason "Settings GlobalPolicy altered from Manual to $($settingsObj.GlobalPolicy)." `
                -CheckpointName "SettingsTampered"
        }
    }

    # 5. Cooperative readiness and revision-binding evaluation (never auto-adopt running script)
    $readinessCheck = Validate-CooperativeReadinessInternal `
        -FixtureRoot $canon `
        -StateObject $state `
        -Attestation $AdoptionAttestation `
        -ExplicitReadinessPath $ReadinessRecordPath `
        -ExplicitReadinessJson $ReadinessJson `
        -ReferenceUtc $nowUtc

    if ($readinessCheck.IsValid) {
        $state.AdoptedProfilePath = $readinessCheck.AdoptedProfilePath
        $state.AdoptedProfileSha256 = $readinessCheck.AdoptedProfileSha256
        $state.AdoptedScriptSha256 = $readinessCheck.AdoptedScriptSha256
        $state.AdoptedToken = $readinessCheck.AdoptedToken
        $state.AdoptedSupervisorPid = $readinessCheck.AdoptedSupervisorPid
        $state.AdoptedSupervisorSessionId = $readinessCheck.AdoptedSupervisorSessionId
    } else {
        $state.AdoptedProfilePath = $null
        $state.AdoptedProfileSha256 = $null
        $state.AdoptedScriptSha256 = $null
        $state.AdoptedToken = $null
        $state.AdoptedSupervisorPid = $null
        $state.AdoptedSupervisorSessionId = $null
    }

    $isReady = $true
    $readinessReason = "Ready"

    if ($state.TerminalHold) {
        $isReady = $false
        $readinessReason = $state.TerminalHoldReason
    } elseif ($state.LastCheckpointOutcome -eq "Failed") {
        $isReady = $false
        $readinessReason = "Prior checkpoint verification failed: $($state.LastFailedReason)"
    } elseif ([int]$state.HelperStartAttempts -ge $script:MAX_HELPER_STARTS) {
        $isReady = $false
        $readinessReason = "Helper start limit reached ($($state.HelperStartAttempts)/$($script:MAX_HELPER_STARTS))."
    } elseif ([int]$state.CompanionStartAttempts -ge $script:MAX_COMPANION_STARTS) {
        $isReady = $false
        $readinessReason = "Companion start limit reached ($($state.CompanionStartAttempts)/$($script:MAX_COMPANION_STARTS))."
    } elseif ($elapsedSeconds -gt $script:MAX_TRIAL_DURATION_SECONDS) {
        $isReady = $false
        $readinessReason = "Trial duration budget exceeded."
    } elseif (-not $readinessCheck.IsValid) {
        $isReady = $false
        $readinessReason = $readinessCheck.Reason
    }

    $state.CurrentReadiness = if ($isReady) { "Ready" } else { "NotReady" }
    $state.ReadinessReason = $readinessReason

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

    if ($state.TerminalHold) {
        return @{ CanProceed = $false; Reason = "Terminal hold active: $($state.TerminalHoldReason)" }
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

    $nowUtc = Get-EffectiveReferenceUtc

    # Duration Guard (Monotonic + Wall clock conservative maximum)
    $startTime = [DateTime]::Parse($state.BatchStartTimeUtc, $null, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    $wallElapsedSeconds = ($nowUtc - $startTime).TotalSeconds
    $monoElapsedSeconds = ([double]([long]$state.ElapsedMilliseconds)) / 1000.0
    $elapsedSeconds = [Math]::Max($wallElapsedSeconds, $monoElapsedSeconds)
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

    # Cooperative Readiness & Revision-Binding Guard (evaluated before Process.Start)
    $readinessCheck = Validate-CooperativeReadinessInternal `
        -FixtureRoot $FixtureRoot `
        -StateObject $state `
        -Attestation $Attestation `
        -ExplicitReadinessPath $ReadinessRecordPath `
        -ExplicitReadinessJson $ReadinessJson `
        -ReferenceUtc $nowUtc
    if (-not $readinessCheck.IsValid) {
        return @{ CanProceed = $false; Reason = $readinessCheck.Reason }
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

function Parse-TarListingInternal(
    [string[]]$Lines,
    [long]$MaxEntries = 500,
    [long]$MaxTotalBytes = 104857600
) {
    if ($null -eq $Lines) { throw "Lines array cannot be null." }

    [long]$totalBytes = 0
    $hasD3d11 = $false
    $hasDxgi = $false
    $seenPaths = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    $entryCount = 0

    foreach ($rawLine in $Lines) {
        if ([string]::IsNullOrWhiteSpace($rawLine)) { continue }
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        $typeChar = $line[0]
        if ($typeChar -ne '-' -and $typeChar -ne 'd') {
            throw "DXVK tar archive contains forbidden entry type '$typeChar': $line"
        }

        $tokens = $line -split '\s+'
        if ($tokens.Length -lt 6) {
            throw "Malformed tar listing row (insufficient tokens): $line"
        }

        [long]$entryBytes = 0
        $pathToken = $null

        if ($typeChar -eq '-') {
            $parsedSize = $false
            if ($tokens.Length -ge 9 -and [long]::TryParse($tokens[4], [ref]$entryBytes)) {
                if ($entryBytes -lt 0) { throw "Negative entry size in tar listing: $tokens[4]" }
                $parsedSize = $true
                $pathToken = ($tokens[8..($tokens.Length - 1)] -join " ")
            } elseif ($tokens.Length -ge 6 -and $tokens[1].Contains("/") -and [long]::TryParse($tokens[2], [ref]$entryBytes)) {
                if ($entryBytes -lt 0) { throw "Negative entry size in tar listing: $tokens[2]" }
                $parsedSize = $true
                $pathToken = ($tokens[5..($tokens.Length - 1)] -join " ")
            } elseif ($tokens.Length -ge 8 -and [long]::TryParse($tokens[4], [ref]$entryBytes)) {
                if ($entryBytes -lt 0) { throw "Negative entry size in tar listing: $tokens[4]" }
                $parsedSize = $true
                $pathToken = ($tokens[7..($tokens.Length - 1)] -join " ")
            } elseif ([long]::TryParse($tokens[4], [ref]$entryBytes)) {
                if ($entryBytes -lt 0) { throw "Negative entry size in tar listing: $tokens[4]" }
                $parsedSize = $true
                $pathToken = $tokens[-1]
            }

            if (-not $parsedSize) {
                throw "Malformed or unknown entry size in tar listing: $line"
            }
        } else {
            # Directory entry
            $entryBytes = 0
            if ($tokens.Length -ge 9) {
                $pathToken = ($tokens[8..($tokens.Length - 1)] -join " ")
            } else {
                $pathToken = $tokens[-1]
            }
        }

        if ([string]::IsNullOrWhiteSpace($pathToken)) {
            throw "Malformed tar listing row (missing path): $line"
        }

        if ($pathToken.Contains("..") -or $pathToken.StartsWith("/") -or $pathToken.StartsWith("\")) {
            throw "DXVK tar archive contains illegal traversal path: $pathToken"
        }

        $cleanPath = $pathToken.Trim()
        while ($cleanPath.StartsWith("./") -or $cleanPath.StartsWith(".\")) {
            $cleanPath = $cleanPath.Substring(2)
        }
        $normalizedPath = $cleanPath.Replace('\', '/').TrimStart('/').TrimEnd('/')
        if ($seenPaths.Contains($normalizedPath)) {
            throw "DXVK tar archive contains duplicate or colliding entry: $pathToken"
        }
        [void]$seenPaths.Add($normalizedPath)

        $entryCount++
        if ($entryCount -gt $MaxEntries) {
            throw "DXVK tar archive exceeds maximum allowed entry count ($MaxEntries): $entryCount"
        }

        $totalBytes += $entryBytes
        if ($totalBytes -gt $MaxTotalBytes) {
            throw "DXVK tar archive expanded size ($totalBytes bytes) exceeds limit ($MaxTotalBytes bytes)."
        }

        if ($pathToken -like "*/x64/d3d11.dll") { $hasD3d11 = $true }
        if ($pathToken -like "*/x64/dxgi.dll") { $hasDxgi = $true }
    }

    if (-not $hasD3d11 -or -not $hasDxgi) {
        throw "DXVK tar archive does not contain required x64/d3d11.dll and x64/dxgi.dll."
    }

    return [PSCustomObject]@{
        EntryCount = $entryCount
        TotalBytes = $totalBytes
        HasD3d11 = $hasD3d11
        HasDxgi = $hasDxgi
        Entries = @($seenPaths)
    }
}

function Extract-CompanionProvenance(
    [string]$ProvenancePath,
    [string]$DestinationDir,
    [bool]$TestPinsUsed = $false,
    $TestPins = $null
) {
    if (-not (Test-Path -LiteralPath $ProvenancePath -PathType Leaf)) {
        throw "Companion provenance file not found: $ProvenancePath"
    }

    $expectedProvSize = if ($TestPinsUsed -and $null -ne $TestPins -and $TestPins.CompanionProvenanceSize) { [long]$TestPins.CompanionProvenanceSize } else { $script:FROZEN_COMPANION_PROVENANCE_SIZE }
    $expectedProvHash = if ($TestPinsUsed -and $null -ne $TestPins -and $TestPins.CompanionProvenanceSha256) { $TestPins.CompanionProvenanceSha256 } else { $script:FROZEN_COMPANION_PROVENANCE_SHA256 }

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
        Expand-SafeZipArchiveInternal -ZipFilePath $ProvenancePath -DestinationDirectory $DestinationDir -MaxEntries 10 -MaxTotalBytes 1048576
        $provJsonPath = [System.IO.Path]::Combine($DestinationDir, "build-provenance.json")
        if (-not (Test-Path -LiteralPath $provJsonPath -PathType Leaf)) {
            $provJsonPath = [System.IO.Path]::Combine($DestinationDir, "provenance.json")
        }
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

    $actualCommit = if ($null -ne $provObj.PSObject.Properties['head_sha']) {
        "$($provObj.head_sha)"
    } elseif ($null -ne $provObj.PSObject.Properties['sha']) {
        "$($provObj.sha)"
    } elseif ($null -ne $provObj.PSObject.Properties['commit']) {
        "$($provObj.commit)"
    } else {
        $null
    }

    if ([string]::IsNullOrWhiteSpace($actualCommit)) {
        throw "Companion provenance manifest does not contain a head_sha, sha, or commit property."
    }

    $actualRunId = if ($null -ne $provObj.PSObject.Properties['run_id']) { "$($provObj.run_id)" } else { "" }

    if (-not $TestPinsUsed) {
        if ($actualCommit -ne $script:FROZEN_COMPANION_SRC_COMMIT) {
            throw "Companion provenance commit mismatch: expected $($script:FROZEN_COMPANION_SRC_COMMIT), got '$actualCommit'"
        }
        if ($actualRunId -ne $script:FROZEN_COMPANION_RUN_ID) {
            throw "Companion provenance run_id mismatch: expected $($script:FROZEN_COMPANION_RUN_ID), got '$actualRunId'"
        }
    } else {
        if ($null -ne $TestPins -and $null -ne (Get-SafePropertyValue -Object $TestPins -PropertyName "CompanionCommit") -and $actualCommit -ne $TestPins.CompanionCommit) {
            throw "Companion provenance commit mismatch: expected $($TestPins.CompanionCommit), got '$actualCommit'"
        }
        if ($null -ne $TestPins -and $null -ne (Get-SafePropertyValue -Object $TestPins -PropertyName "CompanionRunId") -and $actualRunId -ne "$($TestPins.CompanionRunId)") {
            throw "Companion provenance run_id mismatch: expected $($TestPins.CompanionRunId), got '$actualRunId'"
        }
    }
}

function Read-SharedLogFileText([string]$LogPath) {
    if ([string]::IsNullOrWhiteSpace($LogPath) -or -not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        return ""
    }
    try {
        $fs = New-Object System.IO.FileStream($LogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $sr = New-Object System.IO.StreamReader($fs, $script:Utf8NoBom)
            return $sr.ReadToEnd()
        } finally {
            $fs.Dispose()
        }
    } catch {
        return ""
    }
}

function Write-ResidualChildRecord([object]$RetainedRecord, [string]$FixtureRoot = "") {
    if ($null -eq $RetainedRecord) { return }
    $recordObj = [PSCustomObject]@{
        Pid             = $RetainedRecord.Pid
        FileName        = $RetainedRecord.FileName
        Arguments       = $RetainedRecord.Arguments
        StdoutLogPath   = $RetainedRecord.StdoutLogPath
        StderrLogPath   = $RetainedRecord.StderrLogPath
        StartedAtUtc    = $RetainedRecord.StartedAtUtc
        TimedOut        = [bool]$RetainedRecord.TimedOut
        TimedOutAtUtc   = $RetainedRecord.TimedOutAtUtc
        PostStartError  = $RetainedRecord.PostStartError
        Settled         = [bool]$RetainedRecord.Settled
        SettledAtUtc    = $RetainedRecord.SettledAtUtc
        FinalExitCode   = $RetainedRecord.FinalExitCode
        StdoutDrained   = [bool]$RetainedRecord.StdoutDrained
        StderrDrained   = [bool]$RetainedRecord.StderrDrained
        StdoutBytes     = if ($null -ne $RetainedRecord.StdoutDrainer) { [long]$RetainedRecord.StdoutDrainer.BytesWritten } else { 0L }
        StderrBytes     = if ($null -ne $RetainedRecord.StderrDrainer) { [long]$RetainedRecord.StderrDrainer.BytesWritten } else { 0L }
        StdoutBytesRead = if ($null -ne $RetainedRecord.StdoutDrainer) { [long]$RetainedRecord.StdoutDrainer.BytesRead } else { 0L }
        StderrBytesRead = if ($null -ne $RetainedRecord.StderrDrainer) { [long]$RetainedRecord.StderrDrainer.BytesRead } else { 0L }
        StdoutLostBytes = if ($null -ne $RetainedRecord.StdoutDrainer) { [long]$RetainedRecord.StdoutDrainer.LostBytes } else { 0L }
        StderrLostBytes = if ($null -ne $RetainedRecord.StderrDrainer) { [long]$RetainedRecord.StderrDrainer.LostBytes } else { 0L }
        StdoutLinesRead = if ($null -ne $RetainedRecord.StdoutDrainer) { [int]$RetainedRecord.StdoutDrainer.LinesRead } else { 0 }
        StderrLinesRead = if ($null -ne $RetainedRecord.StderrDrainer) { [int]$RetainedRecord.StderrDrainer.LinesRead } else { 0 }
        StdoutLostLines = if ($null -ne $RetainedRecord.StdoutDrainer) { [int]$RetainedRecord.StdoutDrainer.LostLines } else { 0 }
        StderrLostLines = if ($null -ne $RetainedRecord.StderrDrainer) { [int]$RetainedRecord.StderrDrainer.LostLines } else { 0 }
        HasDrainFault   = [bool]$RetainedRecord.HasDrainFault
        FaultMessage    = $RetainedRecord.FaultMessage
        SupervisorPid   = $PID
    }
    $json = ConvertTo-Json -InputObject $recordObj -Depth 5

    $pathsToWrite = [System.Collections.ArrayList]::new()
    if (-not [string]::IsNullOrWhiteSpace($RetainedRecord.ResidualRecordPath)) {
        [void]$pathsToWrite.Add($RetainedRecord.ResidualRecordPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($RetainedRecord.StderrLogPath)) {
        $stderrResidual = "$($RetainedRecord.StderrLogPath.TrimEnd('\', '/')).residual.json"
        if ($pathsToWrite -notcontains $stderrResidual) {
            [void]$pathsToWrite.Add($stderrResidual)
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($FixtureRoot) -and (Test-Path -LiteralPath $FixtureRoot -PathType Container)) {
        $logsDir = [System.IO.Path]::Combine($FixtureRoot, "logs")
        if (Test-Path -LiteralPath $logsDir -PathType Container) {
            $rootResidualPath = [System.IO.Path]::Combine($logsDir, "retained-child-$($RetainedRecord.Pid).residual.json")
            if ($pathsToWrite -notcontains $rootResidualPath) {
                [void]$pathsToWrite.Add($rootResidualPath)
            }
        }
    }

    foreach ($p in $pathsToWrite) {
        try {
            $dir = [System.IO.Path]::GetDirectoryName($p)
            if (-not [string]::IsNullOrEmpty($dir) -and -not (Test-Path -LiteralPath $dir -PathType Container)) {
                [void][System.IO.Directory]::CreateDirectory($dir)
            }
            [System.IO.File]::WriteAllText($p, $json, $script:Utf8NoBom)
        } catch {}
    }
}

function Wait-AndSettleRetainedBoundedProcesses([string]$FixtureRoot = "") {
    if ($null -eq $script:RetainedTimedOutProcesses -or $script:RetainedTimedOutProcesses.Count -eq 0) {
        return
    }

    foreach ($rec in @($script:RetainedTimedOutProcesses)) {
        if ($null -eq $rec) { continue }
        # Ensure both redirected streams have an active runspace-free fallback drainer even if initialization faulted
        if ($null -ne $rec.Process) {
            if ($null -eq $rec.StdoutDrainer) {
                try {
                    $rec.StdoutDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($rec.Process.StandardOutput, "", $script:Utf8NoBom, $script:MAX_PROCESS_LOG_BYTES)
                } catch {}
            }
            if ($null -ne $rec.StdoutDrainer) {
                try { $rec.StdoutDrainer.Start() } catch {}
            }
            if ($null -eq $rec.StderrDrainer) {
                try {
                    $rec.StderrDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($rec.Process.StandardError, "", $script:Utf8NoBom, $script:MAX_PROCESS_LOG_BYTES)
                } catch {}
            }
            if ($null -ne $rec.StderrDrainer) {
                try { $rec.StderrDrainer.Start() } catch {}
            }
        }
        Write-ResidualChildRecord -RetainedRecord $rec -FixtureRoot $FixtureRoot
    }

    while ($true) {
        $anyAlive = $false
        foreach ($rec in @($script:RetainedTimedOutProcesses)) {
            if ($null -eq $rec -or $null -eq $rec.Process) { continue }
            try {
                if (-not $rec.Process.HasExited) {
                    $anyAlive = $true
                    [void]$rec.Process.WaitForExit(200)
                }
            } catch {}
        }
        if (-not $anyAlive) {
            break
        }
        if (-not [string]::IsNullOrWhiteSpace($FixtureRoot)) {
            try {
                $stateFile = [System.IO.Path]::Combine($FixtureRoot, "state.json")
                if (Test-Path -LiteralPath $stateFile -PathType Leaf) {
                    Invoke-SuperviseObservationCycle -FixtureRoot $FixtureRoot
                }
            } catch {}
        }
    }

    foreach ($rec in @($script:RetainedTimedOutProcesses)) {
        if ($null -eq $rec) { continue }
        $outDrained = $false
        $errDrained = $false
        if ($null -ne $rec.StdoutDrainer) {
            try { $outDrained = [bool]$rec.StdoutDrainer.WaitForDrain(5000) } catch {}
        }
        if ($null -ne $rec.StderrDrainer) {
            try { $errDrained = [bool]$rec.StderrDrainer.WaitForDrain(5000) } catch {}
        }
        $outFault = if ($null -ne $rec.StdoutDrainer) { [bool]$rec.StdoutDrainer.HasFault } else { $false }
        $errFault = if ($null -ne $rec.StderrDrainer) { [bool]$rec.StderrDrainer.HasFault } else { $false }
        $hasFault = (-not $outDrained) -or (-not $errDrained) -or $outFault -or $errFault
        $faultMsg = if ($outFault) {
            $rec.StdoutDrainer.FaultMessage
        } elseif ($errFault) {
            $rec.StderrDrainer.FaultMessage
        } elseif (-not $outDrained -or -not $errDrained) {
            "Stream drain timed out during retained child settlement."
        } else {
            $rec.FaultMessage
        }

        $finalExit = $null
        if ($null -ne $rec.Process) {
            try {
                if ($rec.Process.HasExited) {
                    $finalExit = $rec.Process.ExitCode
                }
            } catch {}
        }

        $rec.StdoutDrained = $outDrained
        $rec.StderrDrained = $errDrained
        $rec.HasDrainFault = $hasFault
        $rec.FaultMessage  = $faultMsg
        $rec.FinalExitCode = $finalExit
        $rec.Settled       = $true
        $rec.SettledAtUtc  = [DateTime]::UtcNow.ToString("o")

        Write-ResidualChildRecord -RetainedRecord $rec -FixtureRoot $FixtureRoot

        $settledSummary = [PSCustomObject]@{
            Pid                = $rec.Pid
            FileName           = $rec.FileName
            TimedOut           = [bool]$rec.TimedOut
            PostStartError     = $rec.PostStartError
            Settled            = $true
            SettledAtUtc       = $rec.SettledAtUtc
            FinalExitCode      = $finalExit
            StdoutDrained      = $outDrained
            StderrDrained      = $errDrained
            StdoutBytesRead    = if ($null -ne $rec.StdoutDrainer) { [long]$rec.StdoutDrainer.BytesRead } else { 0L }
            StderrBytesRead    = if ($null -ne $rec.StderrDrainer) { [long]$rec.StderrDrainer.BytesRead } else { 0L }
            StdoutLostBytes    = if ($null -ne $rec.StdoutDrainer) { [long]$rec.StdoutDrainer.LostBytes } else { 0L }
            StderrLostBytes    = if ($null -ne $rec.StderrDrainer) { [long]$rec.StderrDrainer.LostBytes } else { 0L }
            HasDrainFault      = $hasFault
            ResidualRecordPath = $rec.ResidualRecordPath
        }
        [void]$script:SettledRetainedProcessRecords.Add($settledSummary)

        if ($null -ne $rec.StdoutDrainer) {
            try { $rec.StdoutDrainer.Dispose() } catch {}
        }
        if ($null -ne $rec.StderrDrainer) {
            try { $rec.StderrDrainer.Dispose() } catch {}
        }
        if ($null -ne $rec.Process) {
            try { $rec.Process.Dispose() } catch {}
        }
        [void]$script:RetainedTimedOutProcesses.Remove($rec)
    }
}

function Invoke-BoundedChildProcess(
    [string]$FileName,
    [string]$Arguments = "",
    [string]$StdoutLogPath,
    [string]$StderrLogPath,
    [int]$TimeoutMs = 30000,
    [long]$MaxLogBytes = 104857600,
    [string]$WorkingDirectory = "",
    [bool]$SimulatePostStartFault = $false
) {
    if ([string]::IsNullOrWhiteSpace($FileName)) {
        throw "Child process FileName is required."
    }
    if ([string]::IsNullOrWhiteSpace($StdoutLogPath) -or [string]::IsNullOrWhiteSpace($StderrLogPath)) {
        throw "Both StdoutLogPath and StderrLogPath are required."
    }

    foreach ($logFile in @($StdoutLogPath, $StderrLogPath)) {
        try {
            $dir = [System.IO.Path]::GetDirectoryName($logFile)
            if (-not [string]::IsNullOrEmpty($dir) -and -not (Test-Path -LiteralPath $dir -PathType Container)) {
                [void][System.IO.Directory]::CreateDirectory($dir)
            }
            if (Test-Path -LiteralPath $logFile -PathType Leaf) {
                [System.IO.File]::WriteAllText($logFile, "", $script:Utf8NoBom)
            }
        } catch {}
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FileName
    $psi.Arguments = $Arguments
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory) -and (Test-Path -LiteralPath $WorkingDirectory -PathType Container)) {
        $psi.WorkingDirectory = $WorkingDirectory
    }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    if (-not $proc.Start()) {
        throw "Failed to start bounded child process '$FileName'."
    }

    # Immediately register the live child in $script:RetainedTimedOutProcesses BEFORE drainer creation/start
    # so any post-start exception or timeout still retains ownership until the child exits naturally.
    $residualRecordPath = "$($StdoutLogPath.TrimEnd('\', '/')).residual.json"
    $retainedRecord = [PSCustomObject]@{
        Process            = $proc
        Pid                = $proc.Id
        FileName           = $FileName
        Arguments          = $Arguments
        StdoutDrainer      = $null
        StderrDrainer      = $null
        StdoutLogPath      = $StdoutLogPath
        StderrLogPath      = $StderrLogPath
        ResidualRecordPath = $residualRecordPath
        StartedAtUtc       = [DateTime]::UtcNow.ToString("o")
        TimedOut           = $false
        TimedOutAtUtc      = $null
        PostStartError     = $null
        Settled            = $false
        SettledAtUtc       = $null
        FinalExitCode      = $null
        StdoutDrained      = $false
        StderrDrained      = $false
        HasDrainFault      = $false
        FaultMessage       = $null
    }
    [void]$script:RetainedTimedOutProcesses.Add($retainedRecord)

    $outDrainer = $null
    $errDrainer = $null
    $exited = $false
    try {
        $outDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($proc.StandardOutput, $StdoutLogPath, $script:Utf8NoBom, $MaxLogBytes)
        $retainedRecord.StdoutDrainer = $outDrainer
        $errDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($proc.StandardError, $StderrLogPath, $script:Utf8NoBom, $MaxLogBytes)
        $retainedRecord.StderrDrainer = $errDrainer
        $outDrainer.Start()
        $errDrainer.Start()

        if ($outDrainer.HasFault -or $errDrainer.HasFault) {
            $initFaultMsg = if ($outDrainer.HasFault) { $outDrainer.FaultMessage } else { $errDrainer.FaultMessage }
            $retainedRecord.HasDrainFault = $true
            $retainedRecord.FaultMessage = $initFaultMsg
            $retainedRecord.PostStartError = $initFaultMsg
            Write-ResidualChildRecord -RetainedRecord $retainedRecord -FixtureRoot $FixtureRoot
        }

        if ($SimulatePostStartFault) {
            throw "Simulated post-start fault after child process start (PID $($proc.Id))."
        }

        $exited = $proc.WaitForExit($TimeoutMs)
    } catch {
        $retainedRecord.PostStartError = "$_"
        Write-ResidualChildRecord -RetainedRecord $retainedRecord -FixtureRoot $FixtureRoot
        throw
    }

    if (-not $exited) {
        # Zero force-termination: retain process and drainers in script state without calling Kill() or disposing
        $retainedRecord.TimedOut = $true
        $retainedRecord.TimedOutAtUtc = [DateTime]::UtcNow.ToString("o")
        Write-ResidualChildRecord -RetainedRecord $retainedRecord -FixtureRoot $FixtureRoot

        $stdoutText = Read-SharedLogFileText -LogPath $StdoutLogPath
        $stderrText = Read-SharedLogFileText -LogPath $StderrLogPath

        return [PSCustomObject]@{
            Completed          = $false
            TimedOut           = $true
            Retained           = $true
            Pid                = $proc.Id
            ExitCode           = $null
            StdoutDrained      = $false
            StderrDrained      = $false
            StdoutFault        = [bool]$outDrainer.HasFault
            StderrFault        = [bool]$errDrainer.HasFault
            HasDrainFault      = ($outDrainer.HasFault -or $errDrainer.HasFault)
            FaultMessage       = if ($outDrainer.HasFault) { $outDrainer.FaultMessage } else { $errDrainer.FaultMessage }
            StdoutBytesRead    = [long]$outDrainer.BytesRead
            StderrBytesRead    = [long]$errDrainer.BytesRead
            StdoutLostBytes    = [long]$outDrainer.LostBytes
            StderrLostBytes    = [long]$errDrainer.LostBytes
            StdoutText         = $stdoutText
            StderrText         = $stderrText
            StdoutLogPath      = $StdoutLogPath
            StderrLogPath      = $StderrLogPath
            ResidualRecordPath = $residualRecordPath
        }
    }

    [void]$script:RetainedTimedOutProcesses.Remove($retainedRecord)

    $outDrained = $outDrainer.WaitForDrain(5000)
    $errDrained = $errDrainer.WaitForDrain(5000)
    $hasFault = (-not $outDrained) -or (-not $errDrained) -or $outDrainer.HasFault -or $errDrainer.HasFault
    $faultMsg = if ($outDrainer.HasFault) {
        $outDrainer.FaultMessage
    } elseif ($errDrainer.HasFault) {
        $errDrainer.FaultMessage
    } elseif (-not $outDrained -or -not $errDrained) {
        "Stream drain timed out after process exit."
    } else {
        $null
    }

    $exitCode = $proc.ExitCode
    $pidVal = $proc.Id

    if ($hasFault) {
        $retainedRecord.StdoutDrained = [bool]$outDrained
        $retainedRecord.StderrDrained = [bool]$errDrained
        $retainedRecord.HasDrainFault = $true
        $retainedRecord.FaultMessage  = $faultMsg
        $retainedRecord.FinalExitCode = $exitCode
        $retainedRecord.Settled       = $true
        $retainedRecord.SettledAtUtc  = [DateTime]::UtcNow.ToString("o")
        Write-ResidualChildRecord -RetainedRecord $retainedRecord -FixtureRoot $FixtureRoot

        $settledSummary = [PSCustomObject]@{
            Pid                = $pidVal
            FileName           = $FileName
            TimedOut           = $false
            PostStartError     = $retainedRecord.PostStartError
            Settled            = $true
            SettledAtUtc       = $retainedRecord.SettledAtUtc
            FinalExitCode      = $exitCode
            StdoutDrained      = [bool]$outDrained
            StderrDrained      = [bool]$errDrained
            StdoutBytesRead    = [long]$outDrainer.BytesRead
            StderrBytesRead    = [long]$errDrainer.BytesRead
            StdoutLostBytes    = [long]$outDrainer.LostBytes
            StderrLostBytes    = [long]$errDrainer.LostBytes
            HasDrainFault      = $true
            ResidualRecordPath = $residualRecordPath
        }
        [void]$script:SettledRetainedProcessRecords.Add($settledSummary)
    }

    $outDrainer.Dispose()
    $errDrainer.Dispose()
    $proc.Dispose()

    $stdoutText = Read-SharedLogFileText -LogPath $StdoutLogPath
    $stderrText = Read-SharedLogFileText -LogPath $StderrLogPath

    return [PSCustomObject]@{
        Completed          = $true
        TimedOut           = $false
        Retained           = $false
        Pid                = $pidVal
        ExitCode           = $exitCode
        StdoutDrained      = [bool]$outDrained
        StderrDrained      = [bool]$errDrained
        StdoutFault        = [bool]$outDrainer.HasFault
        StderrFault        = [bool]$errDrainer.HasFault
        HasDrainFault      = [bool]$hasFault
        FaultMessage       = $faultMsg
        StdoutBytesRead    = [long]$outDrainer.BytesRead
        StderrBytesRead    = [long]$errDrainer.BytesRead
        StdoutLostBytes    = [long]$outDrainer.LostBytes
        StderrLostBytes    = [long]$errDrainer.LostBytes
        StdoutText         = $stdoutText
        StderrText         = $stderrText
        StdoutLogPath      = $StdoutLogPath
        StderrLogPath      = $StderrLogPath
        ResidualRecordPath = if ($hasFault) { $residualRecordPath } else { $null }
    }
}

function Extract-BoundedDxvkTarGz(
    [string]$TarGzPath,
    [string]$Version,
    [string]$DestinationDir,
    [string]$LogsDir = "",
    [int]$TimeoutMs = 30000,
    [bool]$TestPinsUsed = $false,
    [object]$TestPins = $null
) {
    if (-not (Test-Path -LiteralPath $TarGzPath -PathType Leaf)) {
        throw "DXVK archive not found: $TarGzPath"
    }

    $expectedSize = if ($Version -eq "v3.1.1") {
        if ($TestPinsUsed -and $null -ne (Get-SafePropertyValue -Object $TestPins -PropertyName "Dxvk311Size")) { [long]$TestPins.Dxvk311Size } else { $script:FROZEN_DXVK_311_SIZE }
    } else {
        if ($TestPinsUsed -and $null -ne (Get-SafePropertyValue -Object $TestPins -PropertyName "Dxvk31Size")) { [long]$TestPins.Dxvk31Size } else { $script:FROZEN_DXVK_31_SIZE }
    }
    $expectedHash = if ($Version -eq "v3.1.1") {
        if ($TestPinsUsed -and $null -ne (Get-SafePropertyValue -Object $TestPins -PropertyName "Dxvk311Sha256")) { "$($TestPins.Dxvk311Sha256)" } else { $script:FROZEN_DXVK_311_SHA256 }
    } else {
        if ($TestPinsUsed -and $null -ne (Get-SafePropertyValue -Object $TestPins -PropertyName "Dxvk31Sha256")) { "$($TestPins.Dxvk31Sha256)" } else { $script:FROZEN_DXVK_31_SHA256 }
    }

    $tarItem = Get-Item -LiteralPath $TarGzPath -Force
    if ($tarItem.Length -ne $expectedSize) {
        throw "DXVK $Version archive size mismatch: expected $expectedSize, got $($tarItem.Length)"
    }
    $actualHash = Compute-FileSha256 -FilePath $TarGzPath
    if ($actualHash -ne $expectedHash) {
        throw "DXVK $Version archive SHA256 mismatch: expected $expectedHash, got $actualHash"
    }

    $effectiveLogsDir = if (-not [string]::IsNullOrWhiteSpace($LogsDir)) {
        $LogsDir
    } else {
        $parentLogs = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($DestinationDir, "..", "..", "logs"))
        if (Test-Path -LiteralPath $parentLogs -PathType Container) { $parentLogs } else { $DestinationDir }
    }
    [void][System.IO.Directory]::CreateDirectory($effectiveLogsDir)

    $listingPath = [System.IO.Path]::Combine($effectiveLogsDir, "dxvk-$Version-listing.txt")
    $inspectErrPath = [System.IO.Path]::Combine($effectiveLogsDir, "dxvk-$Version-inspect-stderr.txt")

    $tarExe = if ($TestPinsUsed -and -not [string]::IsNullOrWhiteSpace("$(Get-SafePropertyValue -Object $TestPins -PropertyName 'TarExecutable')")) {
        "$($TestPins.TarExecutable)"
    } else {
        "tar.exe"
    }
    $inspectArgs = if ($TestPinsUsed -and -not [string]::IsNullOrWhiteSpace("$(Get-SafePropertyValue -Object $TestPins -PropertyName 'TarInspectArguments')")) {
        "$($TestPins.TarInspectArguments)"
    } else {
        "-ztvf `"$TarGzPath`""
    }
    $effectiveTimeoutMs = if ($TestPinsUsed -and $null -ne (Get-SafePropertyValue -Object $TestPins -PropertyName "TarTimeoutMs")) {
        [int]$TestPins.TarTimeoutMs
    } else {
        $TimeoutMs
    }

    $inspectRes = Invoke-BoundedChildProcess `
        -FileName $tarExe `
        -Arguments $inspectArgs `
        -StdoutLogPath $listingPath `
        -StderrLogPath $inspectErrPath `
        -TimeoutMs $effectiveTimeoutMs `
        -SimulatePostStartFault:([bool]$SimulateFault)

    if ($inspectRes.TimedOut) {
        throw "DXVK tar archive inspection timed out after ${effectiveTimeoutMs}ms (PID $($inspectRes.Pid) retained without force-kill; logs at $listingPath)."
    }
    if ($inspectRes.HasDrainFault) {
        throw "DXVK tar archive inspection stream drain fault: $($inspectRes.FaultMessage)"
    }
    if ($inspectRes.ExitCode -ne 0) {
        throw "Failed to inspect tar archive '$TarGzPath' (exit code $($inspectRes.ExitCode)): $($inspectRes.StderrText)"
    }

    $lines = $inspectRes.StdoutText -split '\r?\n'
    $parseResult = Parse-TarListingInternal -Lines $lines -MaxEntries 500 -MaxTotalBytes 104857600

    [void][System.IO.Directory]::CreateDirectory($DestinationDir)
    $cleanVer = $Version.TrimStart('v')
    $prefix = "dxvk-$cleanVer"

    $extractOutPath = [System.IO.Path]::Combine($effectiveLogsDir, "dxvk-$Version-extract-stdout.txt")
    $extractLogPath = [System.IO.Path]::Combine($effectiveLogsDir, "dxvk-$Version-extract-stderr.txt")
    $extractArgs = if ($TestPinsUsed -and -not [string]::IsNullOrWhiteSpace("$(Get-SafePropertyValue -Object $TestPins -PropertyName 'TarExtractArguments')")) {
        "$($TestPins.TarExtractArguments)"
    } else {
        "-xzf `"$TarGzPath`" -C `"$DestinationDir`" --strip-components 2 `"$prefix/x64/d3d11.dll`" `"$prefix/x64/dxgi.dll`""
    }

    $extractRes = Invoke-BoundedChildProcess `
        -FileName $tarExe `
        -Arguments $extractArgs `
        -StdoutLogPath $extractOutPath `
        -StderrLogPath $extractLogPath `
        -TimeoutMs $effectiveTimeoutMs

    if ($extractRes.TimedOut) {
        throw "DXVK tar extraction timed out after ${effectiveTimeoutMs}ms (PID $($extractRes.Pid) retained without force-kill; logs at $extractLogPath)."
    }
    if ($extractRes.HasDrainFault) {
        throw "DXVK tar extraction stream drain fault: $($extractRes.FaultMessage)"
    }
    if ($extractRes.ExitCode -ne 0) {
        throw "tar.exe extraction failed with exit code $($extractRes.ExitCode): $($extractRes.StderrText)"
    }

    $extD3d11 = [System.IO.Path]::Combine($DestinationDir, "d3d11.dll")
    $extDxgi = [System.IO.Path]::Combine($DestinationDir, "dxgi.dll")
    if (-not (Test-Path -LiteralPath $extD3d11 -PathType Leaf) -or -not (Test-Path -LiteralPath $extDxgi -PathType Leaf)) {
        throw "Extracted DXVK reference DLLs not found in $DestinationDir."
    }

    $refManifest = [PSCustomObject]@{
        Version = $Version
        ArchiveSha256 = $actualHash
        ArchiveSizeBytes = $tarItem.Length
        ExtractedAtUtc = [DateTime]::UtcNow.ToString("o")
        Files = @(
            [PSCustomObject]@{ RelativePath = "d3d11.dll"; SizeBytes = (Get-Item -LiteralPath $extD3d11).Length; Sha256 = Compute-FileSha256 -FilePath $extD3d11 },
            [PSCustomObject]@{ RelativePath = "dxgi.dll"; SizeBytes = (Get-Item -LiteralPath $extDxgi).Length; Sha256 = Compute-FileSha256 -FilePath $extDxgi }
        )
    }
    $refManifestJson = ConvertTo-Json -InputObject $refManifest -Depth 5
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($DestinationDir, "manifest.json"), $refManifestJson, $script:Utf8NoBom)
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
    $setupStartUtcObj = Get-EffectiveReferenceUtc
    $setupStartUtc = $setupStartUtcObj.ToString("o")
    $monotonicOriginTick = Get-EffectiveMonotonicTick
    $monotonicFreq = Get-EffectiveMonotonicFrequency
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
    # Parse TestPins if supplied
    $testPins = $null
    $testPinsUsed = $false
    if (-not [string]::IsNullOrWhiteSpace($TestPinsJson)) {
        $testPins = ConvertFrom-Json -InputObject $TestPinsJson
        $testPinsUsed = $true
    }

    # F4: Validate archive size AND hash for Helper
    $helperZipItem = Get-Item -LiteralPath $HelperZipPath -Force
    $expectedHelperZipSize = if ($testPinsUsed -and $null -ne (Get-SafePropertyValue -Object $testPins -PropertyName "HelperZipSize")) { [long]$testPins.HelperZipSize } else { $script:FROZEN_HELPER_ZIP_SIZE }
    if ($helperZipItem.Length -ne $expectedHelperZipSize) {
        throw "Helper archive size mismatch: expected $expectedHelperZipSize bytes, got $($helperZipItem.Length)"
    }
    $expectedHelperZipHash = if ($testPinsUsed -and $null -ne (Get-SafePropertyValue -Object $testPins -PropertyName "HelperZipSha256")) { $testPins.HelperZipSha256 } else { $script:FROZEN_HELPER_ZIP_SHA256 }
    $actualHelperZipHash = Compute-FileSha256 -FilePath $HelperZipPath
    if ($actualHelperZipHash -ne $expectedHelperZipHash) {
        throw "Helper archive SHA256 mismatch: expected $expectedHelperZipHash, got $actualHelperZipHash"
    }

    # F4: Validate archive size AND hash for Companion
    $companionZipItem = Get-Item -LiteralPath $CompanionZipPath -Force
    $expectedCompanionZipSize = if ($testPinsUsed -and $null -ne (Get-SafePropertyValue -Object $testPins -PropertyName "CompanionZipSize")) { [long]$testPins.CompanionZipSize } else { $script:FROZEN_COMPANION_ZIP_SIZE }
    if ($companionZipItem.Length -ne $expectedCompanionZipSize) {
        throw "Companion archive size mismatch: expected $expectedCompanionZipSize bytes, got $($companionZipItem.Length)"
    }
    $expectedCompanionZipHash = if ($testPinsUsed -and $null -ne (Get-SafePropertyValue -Object $testPins -PropertyName "CompanionZipSha256")) { $testPins.CompanionZipSha256 } else { $script:FROZEN_COMPANION_ZIP_SHA256 }
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

    # Validate helper package provenance manifest (C1)
    $helperProvPath = [System.IO.Path]::Combine($helperExtractDir, "provenance.json")
    if (-not (Test-Path -LiteralPath $helperProvPath -PathType Leaf)) {
        if (-not $testPinsUsed) {
            throw "Extracted helper package does not contain required provenance.json"
        }
    } else {
        try {
            $helperProvRaw = [System.IO.File]::ReadAllText($helperProvPath, $script:Utf8NoBom)
            $helperProvObj = ConvertFrom-Json -InputObject $helperProvRaw
        } catch {
            throw "Extracted helper package provenance.json is invalid JSON: $_"
        }

        if (-not $testPinsUsed) {
            if ($helperProvObj.SourceSha256 -ne $script:FROZEN_HELPER_SRC_SHA256) {
                throw "Helper provenance SourceSha256 mismatch: expected $($script:FROZEN_HELPER_SRC_SHA256), got '$($helperProvObj.SourceSha256)'"
            }
            if ($helperProvObj.CheckoutCommit -ne $script:FROZEN_HELPER_CHECKOUT_COMMIT) {
                throw "Helper provenance CheckoutCommit mismatch: expected $($script:FROZEN_HELPER_CHECKOUT_COMMIT), got '$($helperProvObj.CheckoutCommit)'"
            }
            if ($helperProvObj.CheckoutTree -ne $script:FROZEN_HELPER_TREE) {
                throw "Helper provenance CheckoutTree mismatch: expected $($script:FROZEN_HELPER_TREE), got '$($helperProvObj.CheckoutTree)'"
            }
            if ("$($helperProvObj.GithubRunId)" -ne $script:FROZEN_HELPER_RUN_ID) {
                throw "Helper provenance GithubRunId mismatch: expected $($script:FROZEN_HELPER_RUN_ID), got '$($helperProvObj.GithubRunId)'"
            }

            # Verify files inventory contains QueueFixture.exe
            $helperExeFoundInInventory = $false
            if ($helperProvObj.Files) {
                foreach ($f in $helperProvObj.Files) {
                    if ($f.Name -eq "QueueFixture.exe") {
                        $helperExeFoundInInventory = $true
                        if ($f.Sha256 -ne $script:FROZEN_HELPER_EXE_SHA256) {
                            throw "Helper provenance file inventory QueueFixture.exe Sha256 mismatch: expected $($script:FROZEN_HELPER_EXE_SHA256), got '$($f.Sha256)'"
                        }
                        if ($f.SizeBytes -ne $script:FROZEN_HELPER_EXE_SIZE) {
                            throw "Helper provenance file inventory QueueFixture.exe SizeBytes mismatch: expected $($script:FROZEN_HELPER_EXE_SIZE), got $($f.SizeBytes)"
                        }
                    }
                }
            }
            if (-not $helperExeFoundInInventory) {
                throw "Helper provenance file inventory does not contain QueueFixture.exe"
            }
        } else {
            if ($null -ne $testPins -and $null -ne (Get-SafePropertyValue -Object $testPins -PropertyName "HelperProvenance")) {
                $hp = $testPins.HelperProvenance
                $hpSrc = Get-SafePropertyValue -Object $hp -PropertyName "SourceSha256"
                $actSrc = Get-SafePropertyValue -Object $helperProvObj -PropertyName "SourceSha256"
                if ($hpSrc -and $actSrc -ne $hpSrc) {
                    throw "Helper provenance SourceSha256 mismatch: expected $hpSrc, got '$actSrc'"
                }
                $hpCommit = Get-SafePropertyValue -Object $hp -PropertyName "CheckoutCommit"
                $actCommit = Get-SafePropertyValue -Object $helperProvObj -PropertyName "CheckoutCommit"
                if ($hpCommit -and $actCommit -ne $hpCommit) {
                    throw "Helper provenance CheckoutCommit mismatch: expected $hpCommit, got '$actCommit'"
                }
                $hpTree = Get-SafePropertyValue -Object $hp -PropertyName "CheckoutTree"
                $actTree = Get-SafePropertyValue -Object $helperProvObj -PropertyName "CheckoutTree"
                if ($hpTree -and $actTree -ne $hpTree) {
                    throw "Helper provenance CheckoutTree mismatch: expected $hpTree, got '$actTree'"
                }
            }
        }
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
    $expectedHelperExeSize = if ($testPinsUsed -and $null -ne (Get-SafePropertyValue -Object $testPins -PropertyName "HelperExeSize")) { [long]$testPins.HelperExeSize } else { $script:FROZEN_HELPER_EXE_SIZE }
    if ($helperExeItem.Length -ne $expectedHelperExeSize) {
        throw "Extracted QueueFixture.exe size mismatch: expected $expectedHelperExeSize, got $($helperExeItem.Length)"
    }
    $expectedHelperExeHash = if ($testPinsUsed -and $null -ne (Get-SafePropertyValue -Object $testPins -PropertyName "HelperExeSha256")) { $testPins.HelperExeSha256 } else { $script:FROZEN_HELPER_EXE_SHA256 }
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
    $expectedCompExeSize = if ($testPinsUsed -and $null -ne (Get-SafePropertyValue -Object $testPins -PropertyName "CompanionExeSize")) { [long]$testPins.CompanionExeSize } else { $script:FROZEN_COMPANION_EXE_SIZE }
    if ($compExeItem.Length -ne $expectedCompExeSize) {
        throw "Extracted DXVK-Companion.exe size mismatch: expected $expectedCompExeSize, got $($compExeItem.Length)"
    }
    $expectedCompExeHash = if ($testPinsUsed -and $null -ne (Get-SafePropertyValue -Object $testPins -PropertyName "CompanionExeSha256")) { $testPins.CompanionExeSha256 } else { $script:FROZEN_COMPANION_EXE_SHA256 }
    $actualCompExeHash = Compute-FileSha256 -FilePath $companionExe
    if ($actualCompExeHash -ne $expectedCompExeHash) {
        throw "Extracted DXVK-Companion.exe SHA256 mismatch: expected $expectedCompExeHash, got $actualCompExeHash"
    }

    # Official reference tar extraction (if provided)
    if (-not [string]::IsNullOrWhiteSpace($Dxvk311TarGzPath)) {
        $ref311Dir = [System.IO.Path]::Combine($referenceDir, "v3.1.1", "x64")
        Extract-BoundedDxvkTarGz -TarGzPath $Dxvk311TarGzPath -Version "v3.1.1" -DestinationDir $ref311Dir -LogsDir $logsDir -TestPinsUsed $testPinsUsed -TestPins $testPins
    }
    if (-not [string]::IsNullOrWhiteSpace($Dxvk31TarGzPath)) {
        $ref31Dir = [System.IO.Path]::Combine($referenceDir, "v3.1", "x64")
        Extract-BoundedDxvkTarGz -TarGzPath $Dxvk31TarGzPath -Version "v3.1" -DestinationDir $ref31Dir -LogsDir $logsDir -TestPinsUsed $testPinsUsed -TestPins $testPins
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

    # Initialize state.json with monotonic timings, start attempts, and cooperative readiness (no auto-adoption)
    $scriptSha = if (-not [string]::IsNullOrWhiteSpace($PSCommandPath) -and (Test-Path -LiteralPath $PSCommandPath -PathType Leaf)) {
        Compute-FileSha256 -FilePath $PSCommandPath
    } else {
        $null
    }
    $profileSha = Get-ProfileDocumentSha256
    $initialFootprint = Measure-DirectoryFootprintBytes -DirectoryPath $rootFull
    $afterSetupTick = Get-EffectiveMonotonicTick
    if ($afterSetupTick -lt $monotonicOriginTick) {
        $afterSetupTick = $monotonicOriginTick
    }
    $setupMonoElapsedMs = [Math]::Max([long]$stopwatch.ElapsedMilliseconds, [long][Math]::Floor((($afterSetupTick - $monotonicOriginTick) * 1000.0) / [double]$monotonicFreq))
    $afterSetupUtc = (Get-EffectiveReferenceUtc).ToString("o")

    $initialState = [PSCustomObject]@{
        BatchStartTimeUtc          = $setupStartUtc
        TrialStartUtc              = $setupStartUtc
        SetupElapsedMilliseconds   = $setupMonoElapsedMs
        ElapsedMilliseconds        = $setupMonoElapsedMs
        MonotonicOriginTimestamp   = $monotonicOriginTick
        MonotonicFrequency         = $monotonicFreq
        LastMonotonicTimestamp     = $afterSetupTick
        AccumulatedMonotonicMs     = $setupMonoElapsedMs
        LastWallClockUtc           = $afterSetupUtc
        LastMonotonicSessionPid    = $PID
        HelperStartAttempts        = 0
        CompanionStartAttempts     = 0
        MaxHelperStarts            = $script:MAX_HELPER_STARTS
        MaxCompanionStarts         = $script:MAX_COMPANION_STARTS
        MaxDurationSeconds         = $script:MAX_TRIAL_DURATION_SECONDS
        MaxFootprintBytes          = $script:MAX_FIXTURE_ROOT_BYTES
        UsedSlots                  = @()
        CurrentCheckpoint          = "Setup"
        LastCheckpointOutcome      = "Success"
        LastFailedCheckpoint       = $null
        LastFailedReason           = $null
        TerminalHold               = $false
        TerminalHoldReason         = $null
        CurrentReadiness           = "NotReady"
        ReadinessReason            = "Pending explicit owner adoption and readiness confirmation"
        CurrentFootprintBytes      = $initialFootprint
        ActiveProcesses            = @()
        ProcessMemoryObservations  = @()
        FirstResourceHold          = $null
        SetupScriptSha256          = $scriptSha
        SetupProfileSha256         = $profileSha
        AdoptedProfilePath         = $null
        AdoptedProfileSha256       = $null
        AdoptedScriptSha256        = $null
        AdoptedToken               = $null
        AdoptedSupervisorPid       = $null
        AdoptedSupervisorSessionId = $null
        IsEnded                    = $false
        TestPinsUsed               = $testPinsUsed
        ActiveHelperPid             = $null
        ActiveHelperSlot            = $null
        ActiveHelperInvocationId    = $null
        ActiveHelperStartTimeUtc    = $null
        LastStoppedHelperSlot       = $null
        NonQualifyingPureDataCheck  = $false
        ActiveCompanionPid          = $null
        ActiveCompanionInvocationId = $null
        ActiveSupervisorPid         = $null
        ActiveSupervisorSessionId   = $null
        Checkpoints                = @("Setup")
    }
    Save-StateData -FixtureRoot $rootFull -StateObject $initialState

    return @{
        Success = $true
        Message = "Setup completed successfully."
        FixtureRoot = $rootFull
        TestPinsUsed = $testPinsUsed
    }
    } finally {
        Wait-AndSettleRetainedBoundedProcesses -FixtureRoot $rootFull
    }
}

# ==============================================================================
# Snapshot and Verification Functions
# ==============================================================================

function Take-SnapshotInternal([string]$FixtureRoot, [string]$CheckpointName) {
    $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
    $cleanName = Validate-CheckpointName -Name $CheckpointName

    $state = Get-StateData -FixtureRoot $canonRoot
    if ($state.TerminalHold) {
        throw "Snapshot refused: Supervisor is in terminal hold state: $($state.TerminalHoldReason)"
    }
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
    if ($state.TerminalHold) {
        throw "Verification refused: Supervisor is in terminal hold state: $($state.TerminalHoldReason)"
    }
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
                $pendingAction = Get-SafePropertyValue -Object $inst -PropertyName "PendingAction"
                if ($null -eq $pendingAction) {
                    throw "Pending checkpoint verification failed: no PendingAction recorded."
                }
                $pendingType = Get-SafePropertyValue -Object $pendingAction -PropertyName "Type"
                $validTypes = @("Install", "Update", "Reapply", "Restore")
                if ($validTypes -notcontains $pendingType) {
                    throw "Pending checkpoint verification failed: unexpected pending action type '$pendingType'."
                }

                $instManagedVer = Get-SafePropertyValue -Object $inst -PropertyName "ManagedDxvkVersion"
                if ($pendingType -eq "Install") {
                    if ([string]::IsNullOrWhiteSpace($instManagedVer)) {
                        $baseCheck = Compare-TargetBaselineInternal -FixtureRoot $canonRoot
                        if (-not $baseCheck.IsMatch) {
                            throw "Pending Install checkpoint verification failed: target was mutated ($($baseCheck.Reason))"
                        }
                    } else {
                        $managedCheck = Compare-TargetManagedReferenceInternal -FixtureRoot $canonRoot -ManagedVersion "$instManagedVer"
                        if (-not $managedCheck.IsMatch) {
                            throw "Pending Install checkpoint verification failed: target does not match currently managed $instManagedVer state ($($managedCheck.Reason))"
                        }
                    }
                } else {
                    if ([string]::IsNullOrWhiteSpace($instManagedVer)) {
                        throw "Pending $pendingType checkpoint verification failed: installation has no ManagedDxvkVersion."
                    }
                    $managedCheck = Compare-TargetManagedReferenceInternal -FixtureRoot $canonRoot -ManagedVersion "$instManagedVer"
                    if (-not $managedCheck.IsMatch) {
                        throw "Pending $pendingType checkpoint verification failed: target does not match currently managed $instManagedVer state ($($managedCheck.Reason))"
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
                        $targetDxvkVer = Get-SafePropertyValue -Object $pendingAction -PropertyName "TargetDxvkVersion"
                        if ($targetDxvkVer -ne $ExpectedVersion) {
                            throw "Pending checkpoint verification failed: expected target version '$ExpectedVersion', got '$targetDxvkVer'."
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
        if ($PureDataCheck) {
            $state.LastCheckpointOutcome = "PureDataCheck"
            $state.NonQualifyingPureDataCheck = $true
        } else {
            $state.LastCheckpointOutcome = "Success"
        }
        Save-StateData -FixtureRoot $canonRoot -StateObject $state
        return @{
            Success = $true
            Message = "Checkpoint '$cleanName' verified successfully as $ExpectedType."
            CheckpointName = $cleanName
            ExpectedType = $ExpectedType
            PureDataCheck = [bool]$PureDataCheck
            QualifiedResult = (-not $PureDataCheck)
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

    $script:HelperProcess = $proc

    $script:HelperStdoutDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($proc.StandardOutput, $stdoutLogPath, $script:Utf8NoBom)
    $script:HelperStderrDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($proc.StandardError, $stderrLogPath, $script:Utf8NoBom)
    $script:HelperStdoutDrainer.Start()
    $script:HelperStderrDrainer.Start()

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
    $helperStartAttempts = [int]$state.HelperStartAttempts
    Set-SafePropertyValue -Object $state -PropertyName "ActiveHelperInvocationId" -Value "Helper-slot-$Slot-start-$helperStartAttempts"
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

    # Owner-first Companion exit ordering on holds: if TerminalHold is active while Companion is running, require stop-companion first
    $companionActive = ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited) -or ($null -ne $state.ActiveCompanionPid)
    if ($state.TerminalHold -and $companionActive) {
        throw "Terminal hold active: close Companion normally (stop-companion) before stopping helper."
    }

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

    if ($null -ne $script:HelperStdoutDrainer) {
        $outDrained = $script:HelperStdoutDrainer.WaitForDrain(5000)
        if (-not $outDrained -or $script:HelperStdoutDrainer.HasFault) {
            throw "Helper stdout stream drain failed: $($script:HelperStdoutDrainer.FaultMessage)"
        }
    }
    if ($null -ne $script:HelperStderrDrainer) {
        $errDrained = $script:HelperStderrDrainer.WaitForDrain(5000)
        if (-not $errDrained -or $script:HelperStderrDrainer.HasFault) {
            throw "Helper stderr stream drain failed: $($script:HelperStderrDrainer.FaultMessage)"
        }
    }

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
    $activeHelperInvId = Get-SafePropertyValue -Object $state -PropertyName "ActiveHelperInvocationId"
    $exitRecord = [PSCustomObject]@{
        Slot         = $Slot
        Pid          = $script:HelperProcess.Id
        InvocationId = $activeHelperInvId
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
    Set-SafePropertyValue -Object $state -PropertyName "ActiveHelperInvocationId" -Value $null
    $state.LastStoppedHelperSlot = $Slot
    $retainedActive = [System.Collections.ArrayList]::new()
    if ($null -ne $state.ActiveProcesses) {
        foreach ($ap in (Get-SafeEnumerable $state.ActiveProcesses)) {
            if ($null -ne $ap) {
                $apRole = Get-SafePropertyValue -Object $ap -PropertyName "Role"
                if ("$apRole" -ne "Helper") {
                    [void]$retainedActive.Add($ap)
                }
            }
        }
    }
    $state.ActiveProcesses = @($retainedActive)
    Settle-ProcessMemoryObservationsInternal -State $state -ActiveProcesses $state.ActiveProcesses
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

    # Retain process handle in supervisor state immediately after start
    $script:CompanionProcess = $proc

    $script:CompanionStdoutDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($proc.StandardOutput, $compStdoutLog, $script:Utf8NoBom)
    $script:CompanionStderrDrainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($proc.StandardError, $compStderrLog, $script:Utf8NoBom)
    $script:CompanionStdoutDrainer.Start()
    $script:CompanionStderrDrainer.Start()

    $state = Get-StateData -FixtureRoot $canonRoot
    $compStartAttempts = [int]$state.CompanionStartAttempts
    $state.ActiveCompanionPid = $proc.Id
    Set-SafePropertyValue -Object $state -PropertyName "ActiveCompanionInvocationId" -Value "Companion-start-$compStartAttempts"
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

    if ($null -ne $script:CompanionStdoutDrainer) {
        $outDrained = $script:CompanionStdoutDrainer.WaitForDrain(5000)
        if (-not $outDrained -or $script:CompanionStdoutDrainer.HasFault) {
            throw "Companion stdout stream drain failed: $($script:CompanionStdoutDrainer.FaultMessage)"
        }
    }
    if ($null -ne $script:CompanionStderrDrainer) {
        $errDrained = $script:CompanionStderrDrainer.WaitForDrain(5000)
        if (-not $errDrained -or $script:CompanionStderrDrainer.HasFault) {
            throw "Companion stderr stream drain failed: $($script:CompanionStderrDrainer.FaultMessage)"
        }
    }

    $exitCode = $script:CompanionProcess.ExitCode

    if ($null -ne $script:CompanionStdoutDrainer) { $script:CompanionStdoutDrainer.Dispose(); $script:CompanionStdoutDrainer = $null }
    if ($null -ne $script:CompanionStderrDrainer) { $script:CompanionStderrDrainer.Dispose(); $script:CompanionStderrDrainer = $null }
    $script:CompanionProcess.Dispose()
    $script:CompanionProcess = $null

    $state = Get-StateData -FixtureRoot $canonRoot
    $state.ActiveCompanionPid = $null
    Set-SafePropertyValue -Object $state -PropertyName "ActiveCompanionInvocationId" -Value $null
    $retainedActive = [System.Collections.ArrayList]::new()
    if ($null -ne $state.ActiveProcesses) {
        foreach ($ap in (Get-SafeEnumerable $state.ActiveProcesses)) {
            if ($null -ne $ap) {
                $apRole = Get-SafePropertyValue -Object $ap -PropertyName "Role"
                if ("$apRole" -ne "Companion") {
                    [void]$retainedActive.Add($ap)
                }
            }
        }
    }
    $state.ActiveProcesses = @($retainedActive)
    Settle-ProcessMemoryObservationsInternal -State $state -ActiveProcesses $state.ActiveProcesses
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

    # Prevent duplicate supervisor sessions on the same fixture root and establish mandatory session binding.
    # Never reuse a prebound session or trust a caller-supplied SupervisorSessionId to bypass PID ownership.
    $state = Get-StateData -FixtureRoot $canonRoot
    if ($null -ne $state.ActiveSupervisorPid -and "$($state.ActiveSupervisorPid)" -ne "") {
        $existingPid = [int]$state.ActiveSupervisorPid
        if ($existingPid -ne $PID) {
            $existingProc = Get-Process -Id $existingPid -ErrorAction SilentlyContinue
            if ($null -ne $existingProc -and -not $existingProc.HasExited) {
                throw "Another supervisor session is already active on fixture root '$canonRoot' (PID $existingPid)."
            }
        }
    }
    $sessionPid = $PID
    $sessionId = [Guid]::NewGuid().ToString("N")

    $state.ActiveSupervisorPid = $sessionPid
    $state.ActiveSupervisorSessionId = $sessionId
    Save-StateData -FixtureRoot $canonRoot -StateObject $state

    $controlDir = Validate-RootBoundPath -RootPath $canonRoot -CandidatePath ([System.IO.Path]::Combine($canonRoot, "control"))
    [void][System.IO.Directory]::CreateDirectory($controlDir)
    $sessionRecordPath = [System.IO.Path]::Combine($controlDir, "supervisor-session.json")
    $sessionRecord = [PSCustomObject]@{
        SupervisorPid       = $sessionPid
        SupervisorSessionId = $sessionId
        FixtureRoot         = $canonRoot
        BatchStartTimeUtc   = $state.BatchStartTimeUtc
        RecordedAtUtc       = (Get-EffectiveReferenceUtc).ToString("o")
    }
    [System.IO.File]::WriteAllText($sessionRecordPath, (ConvertTo-Json -InputObject $sessionRecord -Depth 5), $script:Utf8NoBom)

    Write-Host "======================================================================"
    Write-Host "DXVK Companion Process-Lifecycle Supervisor (Retained Session)"
    Write-Host "Fixture Root: $canonRoot"
    Write-Host "Supervisor PID: $sessionPid"
    Write-Host "Supervisor Session ID: $sessionId"
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

                # Normalize potential @(...) array wrapping and quotes passed from CLI
                if ($trimmed.StartsWith("@(") -and $trimmed.EndsWith(")")) {
                    $trimmed = $trimmed.Substring(2, $trimmed.Length - 3).Trim()
                }
                while (($trimmed.StartsWith('"') -and $trimmed.EndsWith('"')) -or ($trimmed.StartsWith("'") -and $trimmed.EndsWith("'"))) {
                    if ($trimmed.Length -le 2) { $trimmed = ""; break }
                    $trimmed = $trimmed.Substring(1, $trimmed.Length - 2).Trim()
                }
                if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) { continue }

                Invoke-SuperviseObservationCycle -FixtureRoot $canonRoot
                $st = Get-StateData -FixtureRoot $canonRoot

                Write-Host "[SUPERVISOR EXEC] $trimmed"
                $parts = $trimmed -split '\s+'
                $cmd = $parts[0].ToLowerInvariant()

                # If in terminal hold, refuse test actions (only allow stopping and status)
                if ($st.TerminalHold -and @("start-helper", "start-companion", "snapshot", "verify") -contains $cmd) {
                    throw "Action refused: Supervisor is in terminal hold state: $($st.TerminalHoldReason)"
                }

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
                    "status" {
                        $stState = Get-StateData -FixtureRoot $canonRoot
                        $res = @{ Success = $true; State = $stState }
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

            # If any child process remains running after command sequence finishes:
            $hasActiveChildren = ($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) -or
                                 ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited)
            if ($hasActiveChildren) {
                Write-Host "[SUPERVISOR HOLD] Command sequence completed while child processes are still running. Retaining process handles and observation loop until children exit..."
                while (($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) -or
                       ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited)) {
                    Invoke-SuperviseObservationCycle -FixtureRoot $canonRoot
                    Start-Sleep -Milliseconds 500
                }
                Write-Host "[SUPERVISOR HOLD] All child processes have exited."
            }

            return @{
                Success = $true
                Message = "All supervisor commands executed successfully."
                Results = $executionResults
            }
        } else {
            Write-Host "Entering interactive supervisor mode. Type 'help' for commands, 'exit' to quit."
            $lineReader = New-Object DXVKCompanion.Validation.SafeInteractiveLineReader
            while ($true) {
                Invoke-SuperviseObservationCycle -FixtureRoot $canonRoot
                $st = Get-StateData -FixtureRoot $canonRoot
                $prompt = "Trial [Helper=$($st.HelperStartAttempts)/4, Comp=$($st.CompanionStartAttempts)/8, Readiness=$($st.CurrentReadiness)] > "
                Write-Host -NoNewline $prompt

                # Bounded asynchronous console read using compiled C# SafeInteractiveLineReader (no PowerShell runspace on worker thread)
                $inputLine = $null
                while (-not $lineReader.PollReadLine(200, [ref]$inputLine)) {
                    Invoke-SuperviseObservationCycle -FixtureRoot $canonRoot
                }
                if ($lineReader.HasFault) {
                    throw "Interactive input reader fault: $($lineReader.FaultMessage)"
                }

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

                    if ($st.TerminalHold -and @("start-helper", "start-companion", "snapshot", "verify") -contains $cmd) {
                        throw "Action refused: Supervisor is in terminal hold state: $($st.TerminalHoldReason)"
                    }

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
    } catch {
        $hasActive = ($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) -or
                     ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited)
        if ($hasActive) {
            Write-Host "[SUPERVISOR HOLD] Error encountered while child processes active ($($_)). Maintaining handle retention and observation loop until children exit..."
            while (($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) -or
                   ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited)) {
                Invoke-SuperviseObservationCycle -FixtureRoot $canonRoot
                Start-Sleep -Milliseconds 500
            }
        }
        throw
    } finally {
        Wait-AndSettleRetainedBoundedProcesses -FixtureRoot $canonRoot
        $hasActiveChildren = ($null -ne $script:HelperProcess -and -not $script:HelperProcess.HasExited) -or
                             ($null -ne $script:CompanionProcess -and -not $script:CompanionProcess.HasExited)
        if (-not $hasActiveChildren) {
            $cleanState = Get-StateData -FixtureRoot $canonRoot
            if ($null -ne $cleanState.ActiveSupervisorPid -and "$($cleanState.ActiveSupervisorPid)" -ne "" -and
                [int]$cleanState.ActiveSupervisorPid -eq $sessionPid -and
                "$($cleanState.ActiveSupervisorSessionId)" -eq $sessionId) {
                $cleanState.ActiveSupervisorPid = $null
                $cleanState.ActiveSupervisorSessionId = $null
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
        "Test-Drainer" {
            if ([string]::IsNullOrWhiteSpace($LogPath)) {
                throw "LogPath parameter is required for Test-Drainer."
            }
            $stream = $null
            if ($SimulateFault) {
                $stream = New-Object DXVKCompanion.Validation.FaultingStream
            } elseif (-not [string]::IsNullOrWhiteSpace($TestInputPath)) {
                $stream = [System.IO.File]::OpenRead($TestInputPath)
            } elseif (-not [string]::IsNullOrWhiteSpace($TestInputText)) {
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($TestInputText)
                $stream = New-Object System.IO.MemoryStream($bytes, $false)
            } else {
                $bytes = New-Object byte[] 0
                $stream = New-Object System.IO.MemoryStream($bytes, $false)
            }

            $drainer = New-Object DXVKCompanion.Validation.SafeProcessStreamDrainer($stream, $LogPath, $script:Utf8NoBom, $MaxBytes)
            $drained = $false
            try {
                $drainer.Start()
                $drained = $drainer.WaitForDrain($TimeoutMs)
            } finally {
                $drainer.Dispose()
                if ($null -ne $stream) {
                    try { $stream.Dispose() } catch {}
                }
            }

            $logContent = ""
            if (Test-Path -LiteralPath $LogPath -PathType Leaf) {
                $logContent = [System.IO.File]::ReadAllText($LogPath, $script:Utf8NoBom)
            }

            $result = @{
                Success             = $true
                LinesRead           = $drainer.LinesRead
                ReadyReceived       = [bool]$drainer.ReadyReceived
                StoppedReceived     = [bool]$drainer.StoppedReceived
                IsCompleted         = [bool]$drainer.IsCompleted
                HasFault            = [bool]$drainer.HasFault
                FaultMessage        = $drainer.FaultMessage
                BytesWritten        = $drainer.BytesWritten
                DrainedSuccessfully = [bool]$drained
                LogContent          = $logContent
            }
        }
        "Test-ParseTarListing" {
            $inputLines = $Lines
            if (-not [string]::IsNullOrWhiteSpace($ListingFilePath)) {
                if (-not (Test-Path -LiteralPath $ListingFilePath -PathType Leaf)) {
                    throw "Listing file not found: $ListingFilePath"
                }
                $inputLines = [System.IO.File]::ReadAllLines($ListingFilePath, $script:Utf8NoBom)
            }
            if ($null -eq $inputLines) {
                $inputLines = @()
            }
            $parsed = Parse-TarListingInternal -Lines $inputLines -MaxEntries $MaxEntries -MaxTotalBytes $MaxTotalBytes
            $result = @{
                Success    = $true
                EntryCount = $parsed.EntryCount
                TotalBytes = $parsed.TotalBytes
                HasD3d11   = $parsed.HasD3d11
                HasDxgi    = $parsed.HasDxgi
                Entries    = $parsed.Entries
            }
        }
        "Test-Provenance" {
            if ([string]::IsNullOrWhiteSpace($PathA)) { throw "PathA (ProvenancePath) is required for Test-Provenance." }
            if ([string]::IsNullOrWhiteSpace($PathB)) { throw "PathB (DestinationDir) is required for Test-Provenance." }

            $pinsUsed = -not [string]::IsNullOrWhiteSpace($TestPinsJson)
            $pins = $null
            if ($pinsUsed) {
                $pins = ConvertFrom-Json -InputObject $TestPinsJson
            }

            Extract-CompanionProvenance -ProvenancePath $PathA -DestinationDir $PathB -TestPinsUsed $pinsUsed -TestPins $pins
            $result = @{
                Success = $true
                Message = "Companion provenance validated successfully."
            }
        }
        "Test-BoundedChild" {
            if ([string]::IsNullOrWhiteSpace($ChildFileName)) { throw "ChildFileName is required for Test-BoundedChild." }
            if ([string]::IsNullOrWhiteSpace($LogPath)) { throw "LogPath is required for Test-BoundedChild." }
            $errLog = if (-not [string]::IsNullOrWhiteSpace($StderrLogPath)) { $StderrLogPath } else { "$LogPath.stderr.log" }
            $workDir = if (-not [string]::IsNullOrWhiteSpace($TargetPath)) { $TargetPath } else { [System.IO.Path]::GetDirectoryName($LogPath) }

            $childRes = Invoke-BoundedChildProcess `
                -FileName $ChildFileName `
                -Arguments $ChildArguments `
                -WorkingDirectory $workDir `
                -StdoutLogPath $LogPath `
                -StderrLogPath $errLog `
                -TimeoutMs $TimeoutMs `
                -MaxLogBytes $MaxBytes `
                -SimulatePostStartFault:([bool]$SimulateFault)

            $retainedAtTimeoutCount = [int]$script:RetainedTimedOutProcesses.Count
            if ($childRes.TimedOut -or $retainedAtTimeoutCount -gt 0) {
                Wait-AndSettleRetainedBoundedProcesses -FixtureRoot $FixtureRoot
            }

            $isChildSuccess = ([bool]$childRes.Completed) -and (-not [bool]$childRes.TimedOut) -and (-not [bool]$childRes.HasDrainFault) -and ($childRes.ExitCode -eq 0)
            $msg = if ($childRes.TimedOut) {
                "Bounded child process timed out after ${TimeoutMs}ms (PID $($childRes.Pid) retained without force-kill until natural exit)."
            } elseif ($childRes.HasDrainFault) {
                "Bounded child stream drain fault: $($childRes.FaultMessage)"
            } else {
                "Bounded child process completed with exit code $($childRes.ExitCode)."
            }

            $result = @{
                Success                  = $isChildSuccess
                Message                  = $msg
                Completed                = [bool]$childRes.Completed
                TimedOut                 = [bool]$childRes.TimedOut
                ExitCode                 = $childRes.ExitCode
                ProcessId                = $childRes.Pid
                Retained                 = [bool]$childRes.Retained
                RetainedProcessCount     = $retainedAtTimeoutCount
                SettledRetainedProcesses = @($script:SettledRetainedProcessRecords)
                ResidualRecordPath       = $childRes.ResidualRecordPath
                StdoutDrained            = [bool]$childRes.StdoutDrained
                StderrDrained            = [bool]$childRes.StderrDrained
                StdoutFault              = [bool]$childRes.StdoutFault
                StderrFault              = [bool]$childRes.StderrFault
                HasDrainFault            = [bool]$childRes.HasDrainFault
                StdoutBytesRead          = [long]$childRes.StdoutBytesRead
                StderrBytesRead          = [long]$childRes.StderrBytesRead
                StdoutLostBytes          = [long]$childRes.StdoutLostBytes
                StderrLostBytes          = [long]$childRes.StderrLostBytes
                StdoutLogPath            = $childRes.StdoutLogPath
                StderrLogPath            = $childRes.StderrLogPath
            }
        }
        "Test-ResourceObservation" {
            if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { throw "FixtureRoot parameter is required for Test-ResourceObservation." }
            $canonRoot = Validate-RootBoundPath -RootPath $FixtureRoot -CandidatePath $FixtureRoot
            $st = Get-StateData -FixtureRoot $canonRoot

            if ($ProcessExited) {
                $st.ActiveProcesses = @()
                Settle-ProcessMemoryObservationsInternal -State $st -ActiveProcesses @()
            } else {
                if ([string]::IsNullOrWhiteSpace($Role)) { throw "Role parameter ('Helper' or 'Companion') is required for Test-ResourceObservation." }
                if ($SamplePid -le 0) { throw "SamplePid parameter must be positive for Test-ResourceObservation." }
                if ($SampleWorkingSetBytes -lt 0) { throw "SampleWorkingSetBytes must be non-negative for Test-ResourceObservation." }

                $effectiveSlot = if ($Role -eq "Helper") { $Slot } else { $null }
                $effectiveInvId = if (-not [string]::IsNullOrWhiteSpace($InvocationId)) { $InvocationId } else { $null }
                $osPeakOk = ($SamplePeakWorkingSetBytes -ge 0)
                $peakWs = if ($osPeakOk) { $SamplePeakWorkingSetBytes } else { $null }
                $cpuMs = if ($SampleCpuMilliseconds -ge 0) { $SampleCpuMilliseconds } else { 0L }
                $refUtc = Get-EffectiveReferenceUtc

                Update-ProcessMemoryObservationInternal `
                    -State $st `
                    -Role $Role `
                    -TargetPid $SamplePid `
                    -Slot $effectiveSlot `
                    -WorkingSetBytes $SampleWorkingSetBytes `
                    -PeakWorkingSetBytes $peakWs `
                    -OsPeakAvailable $osPeakOk `
                    -CpuMilliseconds $cpuMs `
                    -SampledAtUtc $refUtc `
                    -InvocationId $effectiveInvId

                Evaluate-ProcessResourceThresholdsInternal `
                    -State $st `
                    -Role $Role `
                    -TargetPid $SamplePid `
                    -WorkingSetBytes $SampleWorkingSetBytes `
                    -CpuMilliseconds $cpuMs `
                    -InvocationId $effectiveInvId

                # Find the assigned invocation ID on the active observation record
                $assignedInvId = $effectiveInvId
                if ([string]::IsNullOrWhiteSpace($assignedInvId) -and $null -ne $st.ProcessMemoryObservations) {
                    foreach ($obs in (Get-SafeEnumerable $st.ProcessMemoryObservations)) {
                        if ($null -ne $obs) {
                            $obsPid = Get-SafePropertyValue -Object $obs -PropertyName "Pid"
                            $obsLive = Get-SafePropertyValue -Object $obs -PropertyName "IsLive"
                            if ($null -ne $obsPid -and [int]$obsPid -eq $SamplePid -and [bool]$obsLive -eq $true) {
                                $assignedInvId = "$(Get-SafePropertyValue -Object $obs -PropertyName 'InvocationId')"
                            }
                        }
                    }
                }

                $retainedActive = [System.Collections.ArrayList]::new()
                if ($null -ne $st.ActiveProcesses) {
                    foreach ($ap in (Get-SafeEnumerable $st.ActiveProcesses)) {
                        if ($null -ne $ap) {
                            $apPid = Get-SafePropertyValue -Object $ap -PropertyName "Pid"
                            if ($null -ne $apPid -and [int]$apPid -ne $SamplePid) {
                                [void]$retainedActive.Add($ap)
                            }
                        }
                    }
                }
                [void]$retainedActive.Add([PSCustomObject]@{
                    Role                 = $Role
                    Slot                 = $effectiveSlot
                    Pid                  = $SamplePid
                    InvocationId         = $assignedInvId
                    TotalCpuMilliseconds = $cpuMs
                    WorkingSetBytes      = $SampleWorkingSetBytes
                    PeakWorkingSetBytes  = $peakWs
                    SampledAtUtc         = $refUtc.ToString("o")
                })
                $st.ActiveProcesses = @($retainedActive)
            }

            Save-StateData -FixtureRoot $canonRoot -StateObject $st
            $result = @{
                Success                   = $true
                Message                   = "Resource observation evaluated."
                TerminalHold              = [bool]$st.TerminalHold
                TerminalHoldReason        = $st.TerminalHoldReason
                FirstResourceHold         = $st.FirstResourceHold
                ProcessMemoryObservations = $st.ProcessMemoryObservations
                ActiveProcesses           = $st.ActiveProcesses
            }
        }
    }
} catch {
    $result = @{
        Success = $false
        Message = "$_"
    }
} finally {
    Wait-AndSettleRetainedBoundedProcesses -FixtureRoot $FixtureRoot
    if ($null -ne $result -and $null -ne $script:SettledRetainedProcessRecords -and $script:SettledRetainedProcessRecords.Count -gt 0) {
        if ($result -is [hashtable]) {
            $result["SettledRetainedProcesses"] = @($script:SettledRetainedProcessRecords)
        }
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
