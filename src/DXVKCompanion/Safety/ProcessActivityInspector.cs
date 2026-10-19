using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DXVKCompanion.Safety
{
    public class ProcessActivityInspector : IProcessActivityInspector
    {
        private static ProcessActivityInspector? _default;
        public static ProcessActivityInspector Default => _default ??= new ProcessActivityInspector();

        private readonly Func<IEnumerable<Process>>? _processEnumerator;
        private readonly Func<Process, string?>? _mainModulePathResolver;
        private readonly Func<Process, DateTime>? _startTimeResolver;
        private readonly Func<Process, bool>? _hasExitedResolver;

        public ProcessActivityInspector(
            Func<IEnumerable<Process>>? processEnumerator = null,
            Func<Process, string?>? mainModulePathResolver = null,
            Func<Process, DateTime>? startTimeResolver = null,
            Func<Process, bool>? hasExitedResolver = null)
        {
            _processEnumerator = processEnumerator;
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
                    string baseName = Path.GetFileNameWithoutExtension(name);
                    if (!string.IsNullOrEmpty(baseName))
                    {
                        candidateSet.Add(baseName);
                    }
                }
            }

            if (candidateSet.Count == 0 && Directory.Exists(canonicalRoot))
            {
                try
                {
                    foreach (var exe in Directory.EnumerateFiles(canonicalRoot, "*.exe"))
                    {
                        string baseName = Path.GetFileNameWithoutExtension(exe);
                        if (!string.IsNullOrEmpty(baseName))
                        {
                            candidateSet.Add(baseName);
                        }
                    }
                }
                catch
                {
                    // If directory cannot be read, report Unknown
                    return ProcessActivityEvidence.Unknown;
                }
            }

            if (candidateSet.Count == 0)
            {
                return ProcessActivityEvidence.ConfirmedNotRunning;
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
                    string processName;
                    try
                    {
                        processName = process.ProcessName;
                    }
                    catch
                    {
                        continue;
                    }

                    // Candidate Relevance Filtering: Ignore completely unrelated processes
                    if (!candidateSet.Contains(processName))
                    {
                        continue;
                    }

                    bool hasExited = false;
                    try
                    {
                        hasExited = _hasExitedResolver != null ? _hasExitedResolver(process) : process.HasExited;
                    }
                    catch
                    {
                        // Ignore exit check errors
                    }

                    if (hasExited)
                    {
                        continue;
                    }

                    // Inspect candidate start time and module path
                    try
                    {
                        _ = _startTimeResolver != null ? _startTimeResolver(process) : process.StartTime;
                    }
                    catch (Win32Exception)
                    {
                        candidateAccessDenied = true;
                        continue;
                    }
                    catch (InvalidOperationException)
                    {
                        // Process exited mid-check
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
                        // Process exited mid-check
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

                    // Check if candidate process is running from the target installation root
                    if (OperationResourceIdentity.IsPathInside(mainModulePath, canonicalRoot))
                    {
                        return ProcessActivityEvidence.Running;
                    }
                }
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
