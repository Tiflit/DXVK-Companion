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

    public class SettingsChangeCoordinator
    {
        private readonly SettingsStore _settings;
        private readonly StartupManager _startup;

        public SettingsChangeCoordinator(SettingsStore settings, StartupManager? startup = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _startup = startup ?? new StartupManager();
        }

        public GlobalManagementPolicy ActivePolicy => _settings.GlobalPolicy;
        public bool ActiveLaunchOnStartup => _settings.LaunchOnStartup;

        public SettingsOperationResult ChangePolicy(GlobalManagementPolicy proposedPolicy)
        {
            if (proposedPolicy == _settings.GlobalPolicy)
            {
                return new SettingsOperationResult(
                    SettingsOperationStatus.Success,
                    null,
                    _settings.GlobalPolicy,
                    _settings.LaunchOnStartup,
                    ShouldRevertUI: false);
            }

            var priorPolicy = _settings.GlobalPolicy;

            // Persist the proposed policy before committing shared in-memory policy and UI selection.
            // On failure, retain previously active policy; the automated consumer must not observe an uncommitted policy.
            var clone = _settings.Clone();
            clone.GlobalPolicy = proposedPolicy;

            if (clone.Save(out var saveError))
            {
                _settings.GlobalPolicy = proposedPolicy;
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
                // Registry operation failed: retain previous preference/UI state, do not save requested preference,
                // and explain that the change was not confirmed.
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
                return new SettingsOperationResult(
                    SettingsOperationStatus.Success,
                    null,
                    _settings.GlobalPolicy,
                    requestedLaunchOnStartup,
                    ShouldRevertUI: false);
            }
            else
            {
                // If save fails, retain applied startup state in memory/UI and explicitly warn
                // that Windows startup changed but saving its preference failed.
                string warningMessage = !string.IsNullOrWhiteSpace(saveError)
                    ? $"Windows startup was updated, but saving your preference failed: {saveError}"
                    : "Windows startup was updated, but saving your preference failed.";

                return new SettingsOperationResult(
                    SettingsOperationStatus.Warning,
                    warningMessage,
                    _settings.GlobalPolicy,
                    requestedLaunchOnStartup,
                    ShouldRevertUI: false);
            }
        }
    }
}
