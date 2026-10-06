using System;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.UI
{
    public enum SettingsOperationStatus
    {
        Success,
        Failure,
        Warning
    }

    public record SettingsOperationResult(
        SettingsOperationStatus Status,
        string? Message,
        GlobalManagementPolicy ActivePolicy,
        bool ActiveLaunchOnStartup,
        bool ShouldRevertUI = false
    );

    public class UiReentrancyGuard
    {
        public bool IsExecuting { get; private set; }

        public bool TryExecute(Action action)
        {
            if (IsExecuting) return false;
            IsExecuting = true;
            try
            {
                action();
                return true;
            }
            finally
            {
                IsExecuting = false;
            }
        }
    }

    public class SettingsChangeCoordinator
    {
        private readonly SettingsStore _settings;
        private readonly StartupManager _startup;
        private string? _unresolvedStartupSaveWarning;

        public SettingsChangeCoordinator(SettingsStore settings, StartupManager? startup = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _startup = startup ?? new StartupManager();
        }

        public GlobalManagementPolicy ActivePolicy => _settings.GlobalPolicy;
        public bool ActiveLaunchOnStartup => _settings.LaunchOnStartup;
        public bool HasUnresolvedStartupSave => _unresolvedStartupSaveWarning != null;

        public SettingsOperationResult ChangePolicy(GlobalManagementPolicy proposedPolicy)
        {
            if (proposedPolicy == _settings.GlobalPolicy)
            {
                // If there is an unresolved startup save warning, same-policy request must not falsely erase it.
                if (_unresolvedStartupSaveWarning != null)
                {
                    if (_settings.Save(out var retryError))
                    {
                        _unresolvedStartupSaveWarning = null;
                        return new SettingsOperationResult(
                            SettingsOperationStatus.Success,
                            null,
                            _settings.GlobalPolicy,
                            _settings.LaunchOnStartup,
                            ShouldRevertUI: false);
                    }
                    else
                    {
                        _unresolvedStartupSaveWarning = !string.IsNullOrWhiteSpace(retryError)
                            ? $"Windows startup was updated, but saving your preference failed: {retryError}"
                            : _unresolvedStartupSaveWarning;

                        return new SettingsOperationResult(
                            SettingsOperationStatus.Warning,
                            _unresolvedStartupSaveWarning,
                            _settings.GlobalPolicy,
                            _settings.LaunchOnStartup,
                            ShouldRevertUI: false);
                    }
                }

                return new SettingsOperationResult(
                    SettingsOperationStatus.Success,
                    null,
                    _settings.GlobalPolicy,
                    _settings.LaunchOnStartup,
                    ShouldRevertUI: false);
            }

            var priorPolicy = _settings.GlobalPolicy;

            // Persist the proposed policy before committing shared in-memory policy and UI selection.
            var clone = _settings.Clone();
            clone.GlobalPolicy = proposedPolicy;

            if (clone.Save(out var saveError))
            {
                _settings.GlobalPolicy = proposedPolicy;
                // Unrelated successful policy save persists current in-memory settings, clearing any startup save warning
                _unresolvedStartupSaveWarning = null;

                return new SettingsOperationResult(
                    SettingsOperationStatus.Success,
                    null,
                    _settings.GlobalPolicy,
                    _settings.LaunchOnStartup,
                    ShouldRevertUI: false);
            }
            else
            {
                string message = !string.IsNullOrWhiteSpace(saveError)
                    ? $"Failed to save settings: {saveError}"
                    : "Failed to save settings.";

                return new SettingsOperationResult(
                    SettingsOperationStatus.Failure,
                    message,
                    priorPolicy,
                    _settings.LaunchOnStartup,
                    ShouldRevertUI: true);
            }
        }

        public SettingsOperationResult ChangeLaunchOnStartup(bool requestedLaunchOnStartup)
        {
            if (requestedLaunchOnStartup == _settings.LaunchOnStartup)
            {
                // If a prior startup save failed and left a warning, same-state request must not falsely erase it
                if (_unresolvedStartupSaveWarning != null)
                {
                    if (_settings.Save(out var retryError))
                    {
                        _unresolvedStartupSaveWarning = null;
                        return new SettingsOperationResult(
                            SettingsOperationStatus.Success,
                            null,
                            _settings.GlobalPolicy,
                            _settings.LaunchOnStartup,
                            ShouldRevertUI: false);
                    }
                    else
                    {
                        _unresolvedStartupSaveWarning = !string.IsNullOrWhiteSpace(retryError)
                            ? $"Windows startup was updated, but saving your preference failed: {retryError}"
                            : _unresolvedStartupSaveWarning;

                        return new SettingsOperationResult(
                            SettingsOperationStatus.Warning,
                            _unresolvedStartupSaveWarning,
                            _settings.GlobalPolicy,
                            _settings.LaunchOnStartup,
                            ShouldRevertUI: false);
                    }
                }

                return new SettingsOperationResult(
                    SettingsOperationStatus.Success,
                    null,
                    _settings.GlobalPolicy,
                    _settings.LaunchOnStartup,
                    ShouldRevertUI: false);
            }

            var priorState = _settings.LaunchOnStartup;

            // Attempt requested registry operation first.
            bool registrySuccess;
            string? registryError;

            if (requestedLaunchOnStartup)
            {
                registrySuccess = _startup.EnableStartup(out registryError);
            }
            else
            {
                registrySuccess = _startup.DisableStartup(out registryError);
            }

            if (!registrySuccess)
            {
                string message = !string.IsNullOrWhiteSpace(registryError)
                    ? $"Windows startup change was not confirmed: {registryError}"
                    : "Windows startup change was not confirmed.";

                return new SettingsOperationResult(
                    SettingsOperationStatus.Failure,
                    message,
                    _settings.GlobalPolicy,
                    priorState,
                    ShouldRevertUI: true);
            }

            // Reflect the successfully applied startup state in memory/UI and attempt persistence.
            _settings.LaunchOnStartup = requestedLaunchOnStartup;

            if (_settings.Save(out var saveError))
            {
                _unresolvedStartupSaveWarning = null;
                return new SettingsOperationResult(
                    SettingsOperationStatus.Success,
                    null,
                    _settings.GlobalPolicy,
                    requestedLaunchOnStartup,
                    ShouldRevertUI: false);
            }
            else
            {
                _unresolvedStartupSaveWarning = !string.IsNullOrWhiteSpace(saveError)
                    ? $"Windows startup was updated, but saving your preference failed: {saveError}"
                    : "Windows startup was updated, but saving your preference failed.";

                return new SettingsOperationResult(
                    SettingsOperationStatus.Warning,
                    _unresolvedStartupSaveWarning,
                    _settings.GlobalPolicy,
                    requestedLaunchOnStartup,
                    ShouldRevertUI: false);
            }
        }
    }
}
