// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using BabbleFrameBridge.Shared;

namespace BabbleFrameBridge.App;

/// <summary>Silent installer operations, before a window or application mutex is created.</summary>
public static class SteamVrMaintenance
{
    public static bool TryRun(string[] args, string root, Action refresh, Action unregister)
    {
        var shouldRefresh = args.Contains("--refresh-steamvr-registration");
        var shouldUnregister = args.Contains("--unregister-steamvr");
        if (!shouldRefresh && !shouldUnregister) return false;
        if (shouldRefresh && shouldUnregister) throw new ArgumentException("Choose one SteamVR maintenance operation.");
        if (shouldRefresh)
        {
            var settings = AppSettings.Load(root);
            if (settings.StartWithSteamVr) refresh();
        }
        else
        {
            // Clear the saved opt-in even if a removed or damaged runtime cannot
            // finish cleanup. Reinstalling must not silently opt in again.
            Exception? cleanupError = null;
            try { unregister(); }
            catch (Exception error) { cleanupError = error; }
            try
            {
                var settings = AppSettings.Load(root);
                if (settings.StartWithSteamVr)
                {
                    settings.StartWithSteamVr = false;
                    settings.Save(root);
                }
            }
            catch (Exception settingsError) when (cleanupError is not null)
            {
                throw new AggregateException("SteamVR cleanup and saved-preference cleanup both failed.", cleanupError, settingsError);
            }
            if (cleanupError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
        return true;
    }
}
