using System;
using Microsoft.Win32;

namespace CS2Soundboard.Services;

/// <summary>Per-user "start with Windows" via HKCU\...\Run (no admin rights required).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CS2Soundboard";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Returns null on success or an error message.</summary>
    public static string? SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return "Could not determine the application path.";
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not update the Windows startup setting: {ex.Message}";
        }
    }
}
