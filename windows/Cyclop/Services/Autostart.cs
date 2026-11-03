using System;
using Microsoft.Win32;

namespace Cyclop.Services;

/// <summary>Launch at sign-in, through the per-user Run key — the same switch
/// Task Manager's Startup page flips, so the two always agree.</summary>
internal static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Cyclop";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Key);
                return key?.GetValue(Name) is string value && value.Length > 0;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(Key);
                if (value) key.SetValue(Name, Command);
                else key.DeleteValue(Name, throwOnMissingValue: false);
            }
            catch (Exception e) { Support.Log("launch at login failed: " + e.Message); }
        }
    }

    /// <summary>A moved exe would leave the entry pointing at nothing.</summary>
    public static void RefreshPathIfEnabled()
    {
        if (IsEnabled) IsEnabled = true;
    }
}
