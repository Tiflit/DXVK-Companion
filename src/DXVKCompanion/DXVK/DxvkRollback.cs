using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkRollback
    {
        private readonly FileUtils _files;
        private readonly MultiFileTransactionEngine _transactionEngine;
        private readonly GameLibraryStore _gameLibraryStore;

        public DxvkRollback(
            MultiFileTransactionEngine? transactionEngine = null,
            GameLibraryStore? gameLibraryStore = null,
            FileUtils? files = null)
        {
            _transactionEngine = transactionEngine ?? new MultiFileTransactionEngine(GameLibraryPaths.BackupsDir);
            _gameLibraryStore = gameLibraryStore ?? new GameLibraryStore();
            _files = files ?? new FileUtils();
        }

        public DxvkRollback(FileUtils files)
            : this(null, null, files)
        {
        }

        public async Task<bool> RestoreOriginalDllsAsync(GameProfile profile)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string gameDir = Path.GetDirectoryName(profile.ExePath) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                        return false;

                    var installation = _gameLibraryStore.FindByInstallationPath(gameDir);

                    if (installation != null && installation.ConflictFlags != InstallationConflictFlags.None)
                    {
                        Logger.Log($"DxvkRollback: refusing rollback on {installation.DisplayName}; installation has conflict flags: {installation.ConflictFlags}.");
                        return false;
                    }

                    if (installation != null && installation.ManagedFiles.Count > 0)
                    {
                        var filesToRestore = new List<MultiFileTransactionFile>();

                        foreach (var mf in installation.ManagedFiles)
                        {
                            if (mf.OriginalState == FileOriginalState.Unknown)
                            {
                                Logger.Log($"DxvkRollback: skipping file with unknown original state: {mf.RelativePath}");
                                continue;
                            }

                            var targetPath = Path.Combine(gameDir, mf.RelativePath);
                            SafetyFileIdentity? expectedTargetIdentity = null;
                            if (File.Exists(targetPath))
                            {
                                expectedTargetIdentity = FileIdentity.Capture(targetPath);
                            }

                            var safetyOriginalState = mf.OriginalState switch
                            {
                                FileOriginalState.Existing => OriginalFileState.Existing,
                                FileOriginalState.Missing => OriginalFileState.DidNotExist,
                                _ => OriginalFileState.Unknown
                            };

                            filesToRestore.Add(new MultiFileTransactionFile
                            {
                                RelativePath = mf.RelativePath,
                                OriginalState = safetyOriginalState,
                                BackupRelativePath = mf.BackupRelativePath,
                                ExpectedTargetIdentity = expectedTargetIdentity
                            });
                        }

                        if (filesToRestore.Count > 0)
                        {
                            var request = new MultiFileTransactionRequest
                            {
                                InstallationRoot = gameDir,
                                Operation = TransactionOperation.Restore,
                                Files = filesToRestore
                            };

                            var result = _transactionEngine.Execute(request);
                            if (result.State != TransactionState.Committed || result.Outcome != TransactionOutcome.Success)
                            {
                                Logger.Log($"DxvkRollback: restore transaction failed ({result.State}/{result.Outcome}): {result.Message}");
                                return false;
                            }

                            foreach (var mf in installation.ManagedFiles)
                            {
                                mf.CurrentState = ManagedFileState.Consistent;
                                mf.ManagedDxvkVersion = null;
                                mf.LastVerifiedUtc = DateTime.UtcNow;
                            }

                            installation.ManagedDxvkVersion = null;
                            installation.RestorationState = RestorationState.Restored;
                            installation.LastSeenUtc = DateTime.UtcNow;
                            _gameLibraryStore.Save(installation);

                            Logger.Log($"DxvkRollback: successfully restored {filesToRestore.Count} files for {profile.ExeName} via safe transaction {result.TransactionId}.");
                        }
                    }
                    else
                    {
                        // Fallback for pre-existing legacy .bak files if present
                        string d3d9 = Path.Combine(gameDir, "d3d9.dll");
                        string d3d11 = Path.Combine(gameDir, "d3d11.dll");
                        string dxgi = Path.Combine(gameDir, "dxgi.dll");

                        RestoreLegacyBakIfExists(d3d9);
                        RestoreLegacyBakIfExists(d3d11);
                        // Self-clean any dxvk.conf left in game directory in legacy fallback mode
                        string confPath = Path.Combine(gameDir, "dxvk.conf");
                        if (File.Exists(confPath))
                        {
                            try { File.Delete(confPath); }
                            catch (Exception ex)
                            {
                                Logger.Log($"DxvkRollback: could not delete legacy dxvk.conf: {ex.Message}");
                            }
                        }
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"DxvkRollback: unexpected error restoring {profile.ExeName}: {ex.GetType().Name} - {ex.Message}");
                    return false;
                }
            });
        }

        private static void RestoreLegacyBakIfExists(string dllPath)
        {
            string bak = dllPath + ".bak";
            if (File.Exists(bak))
            {
                try
                {
                    if (File.Exists(dllPath)) File.Delete(dllPath);
                    File.Move(bak, dllPath);
                }
                catch (Exception ex)
                {
                    Logger.Log($"DxvkRollback: failed restoring legacy bak {bak}: {ex.Message}");
                }
            }
        }
    }
}
