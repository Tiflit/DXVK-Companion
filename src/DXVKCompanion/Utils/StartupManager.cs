using System;
using Microsoft.Win32;

namespace DXVKCompanion.Utils
{
    public interface IStartupRegistryKey : IDisposable
    {
        void SetValue(string name, string value);
        void DeleteValue(string name, bool throwOnMissingValue);
    }

    public interface IStartupRegistry
    {
        IStartupRegistryKey? OpenRunKey(bool writable);
    }

    internal class WindowsStartupRegistry : IStartupRegistry
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public IStartupRegistryKey? OpenRunKey(bool writable)
        {
            var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable);
            return key != null ? new WindowsStartupRegistryKey(key) : null;
        }
    }

    internal class WindowsStartupRegistryKey : IStartupRegistryKey
    {
        private readonly RegistryKey _key;

        public WindowsStartupRegistryKey(RegistryKey key)
        {
            _key = key;
        }

        public void SetValue(string name, string value)
        {
            _key.SetValue(name, value);
        }

        public void DeleteValue(string name, bool throwOnMissingValue)
        {
            _key.DeleteValue(name, throwOnMissingValue);
        }

        public void Dispose()
        {
            _key.Dispose();
        }
    }

    // Rewritten to use the registry directly instead of Windows Script Host COM interop —
    // WSH needs an extra COM reference configured in the .csproj, which contradicted the
    // README's "zero external dependencies" goal and would fail to build without that setup.
    public class StartupManager
    {
        public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = "DXVK Companion";

        private readonly IStartupRegistry _registry;
        private readonly string _exePath;

        public StartupManager()
            : this(new WindowsStartupRegistry(), null)
        {
        }

        public StartupManager(IStartupRegistry registry, string? exePath = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _exePath = exePath ?? (System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "DXVK-Companion.exe");
        }

        public bool EnableStartup(out string? errorMessage)
        {
            errorMessage = null;
            try
            {
                using var key = _registry.OpenRunKey(writable: true);
                if (key == null)
                {
                    errorMessage = "Windows startup registry key was not found.";
                    return false;
                }

                key.SetValue(ValueName, $"\"{_exePath}\"");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to register Windows startup ({ex.GetType().Name}).";
                return false;
            }
        }

        public void EnableStartup()
        {
            EnableStartup(out _);
        }

        public bool DisableStartup(out string? errorMessage)
        {
            errorMessage = null;
            try
            {
                using var key = _registry.OpenRunKey(writable: true);
                if (key == null)
                {
                    // Missing Run key while disabling is idempotent success
                    return true;
                }

                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to remove Windows startup ({ex.GetType().Name}).";
                return false;
            }
        }

        public void DisableStartup()
        {
            DisableStartup(out _);
        }
    }
}
