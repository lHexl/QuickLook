// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.

using System.Threading;

namespace QuickLook.Common.Helpers;

/// <summary>
/// Controls all persistent diagnostic logging performed by QuickLook.
/// </summary>
public static class DiagnosticLogging
{
    public const string SettingName = "EnableDiagnosticLogging";

    private static readonly object SyncRoot = new();
    private static int _isEnabled = -1;

    public static bool IsEnabled
    {
        get
        {
            int cachedValue = Volatile.Read(ref _isEnabled);
            if (cachedValue >= 0)
                return cachedValue == 1;

            lock (SyncRoot)
            {
                cachedValue = Volatile.Read(ref _isEnabled);
                if (cachedValue < 0)
                {
                    bool value = SettingHelper.Get(SettingName, false, "QuickLook");
                    cachedValue = value ? 1 : 0;
                    Volatile.Write(ref _isEnabled, cachedValue);
                }
            }

            return cachedValue == 1;
        }
        set
        {
            lock (SyncRoot)
            {
                SettingHelper.Set(SettingName, value, "QuickLook");
                Volatile.Write(ref _isEnabled, value ? 1 : 0);
            }
        }
    }
}
