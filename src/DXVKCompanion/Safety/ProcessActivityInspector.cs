using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DXVKCompanion.Safety
{
    internal class ProcessActivityInspector : IProcessActivityInspector
    {
        private static ProcessActivityInspector? _default;
        public static ProcessActivityInspector Default => _default ??= new ProcessActivityInspector();

        private readonly Func<IEnumerable<Process>>? _processEnumerator;
        private readonly Func<Process, string>? _processNameResolver;
        private readonly Func<Process, string?>? _mainModulePathResolver;
        private readonly Func<Process, DateTime>? _startTimeResolver;
        private readonly Func<Process, bool>? _hasExitedResolver;

        public ProcessActivityInspector(
            Func<IEnumerable<Process>>? processEnumerator = null,
            Func<Process, string>? processNameResolver = null,
            Func<Process, string?>? mainModulePathResolver = null,
            Func<Process, DateTime>? startTimeResolver = null,
            Func<Process, bool>? hasExitedResolver = null)
        {
            _processEnumerator = processEnumerator;
            _processNameResolver = processNameResolver;
            _mainModulePathResolver = mainModulePathResolver;
            _startTimeResolver = startTimeResolver;
            _hasExitedResolver = hasExitedResolver;
        }

        public ProcessActivityEvidence GetActivityEvidence(
            string installationRoot,
            IEnumerable<string>? candidateExecutableNames = null)
        {
            if (string.IsNullOrWhiteSpace(installationRoot))
            {
                return ProcessActivityEvidence.ConfirmedNotRunning;
            }

            string canonicalRoot;
            try
            {
                canonicalRoot = OperationResourceIdentity.ResolveCanonicalPath(installationRoot);
            }
            catch
            {
                return ProcessActivityEvidence.Unknown;
            }

            var candidateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (candidateExecutableNames != null)
            {
                foreach (var name in candidateExecutableNames)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string fileName = Path.GetFileName(name);
                    string baseName = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? fileName.Substring(0, fileName.Length - 4)
                        : fileName;
                    if (!string.IsNullOrEmpty(baseName))
                    {
                        candidateSet.Add(baseName);
                    }
                }
            }

            // Refuse an insufficient candidate boundary: implicit directory discovery is removed per #178
            if (candidateSet.Count == 0)
            {
                return ProcessActivityEvidence.Unknown;
            }

            IEnumerable<Process> processes;
            bool disposeProcesses = false;
            try
            {
                if (_processEnumerator != null)
                {
                    processes = _processEnumerator();
                }
                else
                {
                    processes = Process.GetProcesses();
                    disposeProcesses = true;
                }
            }
            catch
            {
                return ProcessActivityEvidence.Unknown;
            }

            bool candidateAccessDenied = false;

            try
            {
                foreach (var process in processes)
                {
                    string? rawName = null;
                    try
                    {
                        rawName = _processNameResolver != null
                            ? _processNameResolver(process)
                            : process.ProcessName;
                    }
                    catch (Win32Exception)
                    {
                        // Could not query process name due to permission denial: cannot rule out candidate
                        candidateAccessDenied = true;
                        continue;
                    }
                    catch (InvalidOperationException)
                    {
                        bool confirmedExited = false;
                        try
                        {
                            confirmedExited = _hasExitedResolver != null ? _hasExitedResolver(process) : process.HasExited;
                        }
                        catch { }

                        if (!confirmedExited)
                        {
                            candidateAccessDenied = true;
                        }
                        continue;
                    }
                    catch
                    {
                        candidateAccessDenied = true;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(rawName))
                    {
                        candidateAccessDenied = true;
                        continue;
                    }

                    // Normalize actual image name as well as candidates (strip only .exe, preserving embedded dots)
                    string rawFileName = Path.GetFileName(rawName);
                    string processName = rawFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? rawFileName.Substring(0, rawFileName.Length - 4)
                        : rawFileName;

                    // Candidate Relevance Filtering: Ignore completely unrelated processes
                    if (!candidateSet.Contains(processName))
                    {
                        continue;
                    }

                    // Candidate process detected: verify lifetime and path
                    bool hasExited = false;
                    try
                    {
                        hasExited = _hasExitedResolver != null ? _hasExitedResolver(process) : process.HasExited;
                    }
                    catch
                    {
                        candidateAccessDenied = true;
                        continue;
                    }

                    if (hasExited)
                    {
                        continue;
                    }

                    int initialPid;
                    DateTime initialStartTime;
                    try
                    {
                        initialPid = process.Id;
                        initialStartTime = _startTimeResolver != null ? _startTimeResolver(process) : process.StartTime;
                    }
                    catch (Win32Exception)
                    {
                        candidateAccessDenied = true;
                        continue;
                    }
                    catch (InvalidOperationException)
                    {
                        bool confirmedExited = false;
                        try
                        {
                            confirmedExited = _hasExitedResolver != null ? _hasExitedResolver(process) : process.HasExited;
                        }
                        catch { }

                        if (!confirmedExited)
                        {
                            candidateAccessDenied = true;
                        }
                        continue;
                    }
                    catch
                    {
                        candidateAccessDenied = true;
                        continue;
                    }

                    string? mainModulePath = null;
                    try
                    {
                        mainModulePath = _mainModulePathResolver != null
                            ? _mainModulePathResolver(process)
                            : process.MainModule?.FileName;
                    }
                    catch (Win32Exception)
                    {
                        candidateAccessDenied = true;
                        continue;
                    }
                    catch (InvalidOperationException)
                    {
                        bool confirmedExited = false;
                        try
                        {
                            confirmedExited = _hasExitedResolver != null ? _hasExitedResolver(process) : process.HasExited;
                        }
                        catch { }

                        if (!confirmedExited)
                        {
                            candidateAccessDenied = true;
                        }
                        continue;
                    }
                    catch
                    {
                        candidateAccessDenied = true;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(mainModulePath))
                    {
                        candidateAccessDenied = true;
                        continue;
                    }

                    // Recheck lifetime consistently to detect PID reuse/relaunch mid-inspection
                    try
                    {
                        if (process.Id != initialPid)
                        {
                            candidateAccessDenied = true;
                            continue;
                        }

                        var currentStartTime = _startTimeResolver != null ? _startTimeResolver(process) : process.StartTime;
                        if (currentStartTime != initialStartTime)
                        {
                            candidateAccessDenied = true;
                            continue;
                        }

                        bool exitedAfter = _hasExitedResolver != null ? _hasExitedResolver(process) : process.HasExited;
                        if (exitedAfter)
                        {
                            continue;
                        }
                    }
                    catch (Win32Exception)
                    {
                        candidateAccessDenied = true;
                        continue;
                    }
                    catch (InvalidOperationException)
                    {
                        bool confirmedExited = false;
                        try { confirmedExited = _hasExitedResolver != null ? _hasExitedResolver(process) : process.HasExited; } catch { }
                        if (!confirmedExited)
                        {
                            candidateAccessDenied = true;
                        }
                        continue;
                    }
                    catch
                    {
                        candidateAccessDenied = true;
                        continue;
                    }

                    // Check if candidate process is running from the target installation root
                    try
                    {
                        if (OperationResourceIdentity.IsPathInside(mainModulePath, canonicalRoot))
                        {
                            return ProcessActivityEvidence.Running;
                        }
                    }
                    catch
                    {
                        // Path canonicalization or verification failure: do not swallow as safely outside
                        candidateAccessDenied = true;
                        continue;
                    }
                }
            }
            catch
            {
                // Lazy enumeration failure
                return ProcessActivityEvidence.Unknown;
            }
            finally
            {
                if (disposeProcesses)
                {
                    foreach (var p in processes)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }

            if (candidateAccessDenied)
            {
                return ProcessActivityEvidence.Unknown;
            }

            return ProcessActivityEvidence.ConfirmedNotRunning;
        }
    }
}
