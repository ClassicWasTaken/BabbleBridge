// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BabbleFrameBridge.App;

/// <summary>Optional SteamVR startup registration and session observation. Neither operation starts a VR session.</summary>
public sealed class SteamVrIntegration
{
    public const string ApplicationKey = "classicwastaken.facetrackingbridge";
    private static readonly object RegistrationLock = new();
    private readonly string _dataRoot;
    private readonly string _executablePath;
    private readonly Func<ISteamVrApplications> _applicationsFactory;
    private readonly Func<bool?> _sessionRunning;

    public string ManifestPath { get; }

    public SteamVrIntegration(string dataRoot, string executablePath,
        Func<ISteamVrApplications>? applicationsFactory = null, Func<bool?>? sessionRunning = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _executablePath = Path.GetFullPath(executablePath);
        ManifestPath = Path.Combine(_dataRoot, "steamvr", "FaceTrackingBridge.vrmanifest");
        _applicationsFactory = applicationsFactory ?? (() => new OpenVrApplications());
        _sessionRunning = sessionRunning ?? IsSteamVrSessionRunning;
    }

    public Task ConfigureAutoLaunchAsync(bool enabled) => Task.Run(() => ConfigureAutoLaunch(enabled));

    public void ConfigureAutoLaunch(bool enabled)
    {
        if (!enabled) { DisableAutoLaunch(); return; }
        lock (RegistrationLock)
        {
            if (!File.Exists(_executablePath))
                throw new InvalidOperationException("The app executable could not be found. Reinstall Face Tracking Bridge, then enable SteamVR startup again.");
            EnsureSafeManifestPath();
            var previousManifest = ReadOwnedManifest();
            // Connect before creating files so a missing SteamVR installation leaves no registration behind.
            using var applications = _applicationsFactory();
            var wasInstalled = applications.IsInstalled(ApplicationKey);
            var wasEnabled = wasInstalled && applications.GetAutoLaunch(ApplicationKey);
            try
            {
                WriteManifest(CreateManifest());
                applications.AddManifest(ManifestPath);
                applications.SetAutoLaunch(ApplicationKey, true);
                if (!applications.GetAutoLaunch(ApplicationKey))
                    throw new InvalidOperationException("SteamVR did not save the startup setting. Open SteamVR Settings > Startup / Shutdown > Manage Add-ons and check Face Tracking Bridge.");
            }
            catch (Exception error)
            {
                try
                {
                    if (previousManifest is not null)
                    {
                        WriteManifest(previousManifest);
                        if (wasInstalled)
                        {
                            applications.AddManifest(ManifestPath);
                            applications.SetAutoLaunch(ApplicationKey, wasEnabled);
                        }
                        else applications.RemoveManifest(ManifestPath);
                    }
                    else
                    {
                        if (applications.IsInstalled(ApplicationKey)) applications.SetAutoLaunch(ApplicationKey, false);
                        applications.RemoveManifest(ManifestPath);
                        DeleteOwnedManifest();
                    }
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException("SteamVR startup could not be saved or fully restored. Check Face Tracking Bridge in SteamVR Settings > Startup / Shutdown > Manage Add-ons. "
                        + error.Message + " Restore: " + rollbackError.Message, error);
                }
                throw new InvalidOperationException("SteamVR startup could not be saved. " + error.Message, error);
            }
        }
    }

    private void DisableAutoLaunch()
    {
        lock (RegistrationLock)
        {
            EnsureSafeManifestPath();
            var previousManifest = ReadOwnedManifest();
            ISteamVrApplications applications;
            try { applications = _applicationsFactory(); }
            catch (SteamVrUnavailableException)
            {
                // With SteamVR removed, remove our launch definition so a stale path cannot launch it later.
                DeleteOwnedManifest();
                return;
            }
            using (applications)
            {
                var wasInstalled = applications.IsInstalled(ApplicationKey);
                var wasEnabled = wasInstalled && applications.GetAutoLaunch(ApplicationKey);
                try
                {
                    if (wasInstalled) applications.SetAutoLaunch(ApplicationKey, false);
                    applications.RemoveManifest(ManifestPath);
                    DeleteOwnedManifest();
                }
                catch (Exception error)
                {
                    try
                    {
                        if (previousManifest is not null) WriteManifest(previousManifest);
                        if (wasInstalled && previousManifest is not null) applications.AddManifest(ManifestPath);
                        if (wasInstalled) applications.SetAutoLaunch(ApplicationKey, wasEnabled);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new InvalidOperationException("SteamVR startup could not be disabled or restored. Check Face Tracking Bridge in SteamVR's startup add-ons. "
                            + error.Message + " Restore: " + rollbackError.Message, error);
                    }
                    throw new InvalidOperationException("SteamVR startup could not be disabled. " + error.Message, error);
                }
            }
        }
    }

    /// <summary>Removes only this app's manifest. An uninstalled SteamVR runtime does not prevent cleanup.</summary>
    public void Unregister()
    {
        lock (RegistrationLock)
        {
            EnsureSafeManifestPath();
            _ = ReadOwnedManifest();
            Exception? registrationError = null;
            try
            {
                using var applications = _applicationsFactory();
                if (applications.IsInstalled(ApplicationKey)) applications.SetAutoLaunch(ApplicationKey, false);
                applications.RemoveManifest(ManifestPath);
            }
            catch (SteamVrUnavailableException) { /* An absent runtime cannot launch an app; remove its owned manifest below. */ }
            catch (Exception error) { registrationError = error; }
            // Removing the manifest also prevents an old registration from pointing to a usable launch definition.
            DeleteOwnedManifest();
            if (registrationError is not null)
                throw new InvalidOperationException("The app's SteamVR launch file was removed, but SteamVR could not finish unregistering it. Check Face Tracking Bridge in SteamVR's startup add-ons. "
                    + registrationError.Message, registrationError);
        }
    }

    /// <summary>Calls onExit once, after a SteamVR session has been observed and has then ended.</summary>
    public async Task MonitorSessionAsync(Action onExit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onExit);
        var tracker = new SteamVrSessionTracker();
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool? running;
            try { running = _sessionRunning(); }
            catch { running = null; } // A failed process read is not evidence that SteamVR exited.
            if (tracker.Observe(running, elapsed.Elapsed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                onExit();
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    public static bool? IsSteamVrSessionRunning()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var sessionId = current.SessionId;
            var snapshot = new List<SteamVrProcessSnapshot>();
            bool uncertain = false;
            foreach (var name in new[] { "vrserver", "vrmonitor", "vrcompositor" })
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try { if (!process.HasExited) snapshot.Add(new SteamVrProcessSnapshot(name, process.SessionId)); }
                        catch (InvalidOperationException) { /* The process exited during enumeration. */ }
                        catch { uncertain = true; }
                    }
                }
            }
            return ContainsVrSession(snapshot, sessionId) ? true : uncertain ? null : false;
        }
        catch { return null; }
    }

    internal static bool ContainsVrSession(IEnumerable<SteamVrProcessSnapshot> processes, int windowsSessionId)
    {
        bool server = false, presentation = false;
        foreach (var process in processes)
        {
            if (process.WindowsSessionId != windowsSessionId) continue;
            if (process.Name.Equals("vrserver", StringComparison.OrdinalIgnoreCase)) server = true;
            if (process.Name.Equals("vrmonitor", StringComparison.OrdinalIgnoreCase) ||
                process.Name.Equals("vrcompositor", StringComparison.OrdinalIgnoreCase)) presentation = true;
        }
        // Utility registration may run a headless vrserver for a few seconds. It is not a VR session.
        return server && presentation;
    }

    private string CreateManifest() => JsonSerializer.Serialize(new
    {
        source = "builtin",
        applications = new[] { new
        {
            app_key = ApplicationKey,
            launch_type = "binary",
            binary_path_windows = _executablePath,
            arguments = "--steamvr",
            working_directory = Path.GetDirectoryName(_executablePath),
            // Valve restricts SetApplicationAutoLaunch to manifests with this property.
            // The bridge remains an ordinary desktop app and opens no VR overlay.
            is_dashboard_overlay = true,
            strings = new { en_us = new { name = "Face Tracking Bridge", description = "Eye and face tracking bridge for VRChat." } }
        } }
    }, new JsonSerializerOptions { WriteIndented = true });

    private string? ReadOwnedManifest()
    {
        if (!File.Exists(ManifestPath)) return null;
        var contents = File.ReadAllText(ManifestPath);
        try
        {
            using var json = JsonDocument.Parse(contents);
            var applications = json.RootElement.GetProperty("applications");
            if (applications.GetArrayLength() != 1 || applications[0].GetProperty("app_key").GetString() != ApplicationKey)
                throw new InvalidDataException();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
        {
            throw new InvalidOperationException("The SteamVR registration file does not belong to Face Tracking Bridge. It was preserved: " + ManifestPath, error);
        }
        return contents;
    }

    private void EnsureSafeManifestPath()
    {
        // Do not overwrite or delete files reached through a redirected profile or registration directory.
        for (var path = Path.GetDirectoryName(ManifestPath); path is not null; path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("SteamVR settings cannot be changed through a redirected folder: " + path);
        if ((File.Exists(ManifestPath) || Directory.Exists(ManifestPath)) &&
            (File.GetAttributes(ManifestPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The SteamVR registration file is redirected and was preserved: " + ManifestPath);
    }

    private void WriteManifest(string contents)
    {
        EnsureSafeManifestPath();
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        var temporary = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.Move(temporary, ManifestPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void DeleteOwnedManifest()
    {
        EnsureSafeManifestPath();
        if (ReadOwnedManifest() is not null) File.Delete(ManifestPath);
    }
}

internal readonly record struct SteamVrProcessSnapshot(string Name, int WindowsSessionId);

/// <summary>A failed probe resets the absence timer; brief SteamVR restarts do not close the bridge.</summary>
public sealed class SteamVrSessionTracker
{
    private readonly TimeSpan _exitDelay;
    private bool _observedSession;
    private bool _reportedExit;
    private TimeSpan? _absentSince;

    public SteamVrSessionTracker(TimeSpan? exitDelay = null)
    {
        _exitDelay = exitDelay ?? TimeSpan.FromSeconds(3);
        if (_exitDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(exitDelay));
    }

    public bool Observe(bool? running, TimeSpan elapsed)
    {
        if (_reportedExit) return false;
        if (running == true) { _observedSession = true; _absentSince = null; return false; }
        if (running is null) { _absentSince = null; return false; }
        if (!_observedSession) return false;
        _absentSince ??= elapsed;
        if (elapsed - _absentSince.Value < _exitDelay) return false;
        _reportedExit = true;
        return true;
    }
}

public interface ISteamVrApplications : IDisposable
{
    bool IsInstalled(string applicationKey);
    bool GetAutoLaunch(string applicationKey);
    void SetAutoLaunch(string applicationKey, bool enabled);
    void AddManifest(string manifestPath);
    void RemoveManifest(string manifestPath);
}

public sealed class SteamVrUnavailableException : InvalidOperationException
{
    public SteamVrUnavailableException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// Minimal interop declarations for Valve's installed OpenVR API, not a bundled SDK or runtime.
/// ABI reference: https://github.com/ValveSoftware/openvr/blob/master/headers/openvr_capi.h
/// </summary>
internal sealed class OpenVrApplications : ISteamVrApplications
{
    private nint _library;
    private bool _initialized;
    private ShutdownDelegate? _shutdown;
    private readonly ManifestDelegate _addManifest = null!;
    private readonly ManifestRemoveDelegate _removeManifest = null!;
    private readonly KeyBoolDelegate _isInstalled = null!;
    private readonly KeyBoolDelegate _getAutoLaunch = null!;
    private readonly AutoLaunchDelegate _setAutoLaunch = null!;
    private readonly ErrorNameDelegate _errorName = null!;

    public OpenVrApplications()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new SteamVrUnavailableException("SteamVR startup requires the 64-bit Windows app.");
            var runtime = FindRuntimeDirectory();
            var dllPath = Path.Combine(runtime, "bin", "win64", "openvr_api.dll");
            if (!File.Exists(dllPath)) throw new SteamVrUnavailableException("The installed SteamVR API could not be found. Install or repair SteamVR, start it once, then try again.");
            // Absolute installed-runtime path; dependencies search its directory and Windows system files only.
            _library = NativeLibrary.Load(dllPath, typeof(OpenVrApplications).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            var initialize = Export<InitializeDelegate>("VR_InitInternal");
            _shutdown = Export<ShutdownDelegate>("VR_ShutdownInternal");
            var getInterface = Export<GetInterfaceDelegate>("VR_GetGenericInterface");
            int error = 0;
            // Utility may briefly start the headless settings server; it starts no drivers, headset or VR UI.
            _ = initialize(ref error, 4); // VRApplication_Utility
            if (error != 0) throw new SteamVrUnavailableException("SteamVR's settings service is unavailable (OpenVR error " + error + "). Start SteamVR once, then try the setting again.");
            _initialized = true;
            nint table = 0;
            foreach (var version in new[] { "FnTable:IVRApplications_008", "FnTable:IVRApplications_007" })
            {
                error = 0;
                table = getInterface(version, ref error);
                if (error == 0 && table != 0) break;
            }
            if (error != 0 || table == 0)
                throw new SteamVrUnavailableException("This SteamVR version does not provide the startup settings API. Update or repair SteamVR, then try again.");
            _addManifest = Slot<ManifestDelegate>(table, 0);
            _removeManifest = Slot<ManifestRemoveDelegate>(table, 1);
            _isInstalled = Slot<KeyBoolDelegate>(table, 2);
            _errorName = Slot<ErrorNameDelegate>(table, 13);
            _setAutoLaunch = Slot<AutoLaunchDelegate>(table, 17);
            _getAutoLaunch = Slot<KeyBoolDelegate>(table, 18);
        }
        catch (Exception error)
        {
            Dispose();
            if (error is SteamVrUnavailableException) throw;
            throw new SteamVrUnavailableException("SteamVR's installed API could not be loaded. Install or repair SteamVR, start it once, then try again. " + error.Message, error);
        }
    }

    internal static string FindRuntimeDirectory()
    {
        var pathsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
        try
        {
            using var paths = JsonDocument.Parse(File.ReadAllText(pathsFile));
            foreach (var runtime in paths.RootElement.GetProperty("runtime").EnumerateArray())
            {
                var candidate = runtime.GetString();
                if (candidate is not null && Path.IsPathFullyQualified(candidate))
                {
                    var fullPath = Path.GetFullPath(candidate);
                    if (File.Exists(Path.Combine(fullPath, "bin", "win64", "openvr_api.dll"))) return fullPath;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { }
        throw new SteamVrUnavailableException("SteamVR is not registered for this Windows account. Install SteamVR and start it once, then enable this setting again.");
    }

    public bool IsInstalled(string applicationKey) => _isInstalled(applicationKey);
    public bool GetAutoLaunch(string applicationKey) => _getAutoLaunch(applicationKey);
    public void SetAutoLaunch(string applicationKey, bool enabled) => Check(_setAutoLaunch(applicationKey, enabled));
    public void AddManifest(string manifestPath) => Check(_addManifest(manifestPath, false));
    public void RemoveManifest(string manifestPath)
    {
        var error = _removeManifest(manifestPath);
        if (error is not (0 or 101 or 102 or 104)) Check(error);
    }

    private void Check(int error)
    {
        if (error == 0) return;
        var description = Marshal.PtrToStringUTF8(_errorName(error));
        throw new InvalidOperationException("SteamVR reported " + (description ?? "application error " + error) + ".");
    }

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    private static T Slot<T>(nint table, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * IntPtr.Size));

    public void Dispose()
    {
        if (_initialized) { _initialized = false; _shutdown?.Invoke(); }
        if (_library != 0) { NativeLibrary.Free(_library); _library = 0; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint InitializeDelegate(ref int error, int applicationType);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ShutdownDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetInterfaceDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string version, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ManifestDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.I1)] bool temporary);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ManifestRemoveDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool KeyBoolDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AutoLaunchDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint ErrorNameDelegate(int error);
}
