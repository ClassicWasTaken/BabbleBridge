// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BabbleFrameBridge.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using VRCFaceTracking;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;
using VRCFaceTracking.Core.Services;

namespace BabbleFrameBridge.Worker;

internal static class Program
{
    private static readonly object OutputLock = new();
    internal static void Emit(object message)
    {
        lock (OutputLock)
        {
            Console.WriteLine(JsonSerializer.Serialize(message));
            Console.Out.Flush();
        }
    }

    public static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        ExtTrackingModule? module = null;
        var moduleNeedsTeardown = false;
        OscSendService? send = null;
        ParameterSenderService? sender = null;
        AvatarRouter? router = null;
        NativeFaceSender? nativeFace = null;
        Action<UnifiedTrackingData>? prepareEyes = null;
        try
        {
            var options = ParseArguments(args);
            bool Has(string name) => options.ContainsKey(name);
            string Arg(string name, string? fallback = null) => options.TryGetValue(name, out var value) && value != null
                ? value : fallback ?? throw new ArgumentException($"Missing {name}");
            var modulePath = Path.GetFullPath(Arg("--module"));
            var dataRoot = Path.GetFullPath(Arg("--data"));
            if (!File.Exists(modulePath)) throw new FileNotFoundException("The selected module DLL was not found.", modulePath);
            Directory.CreateDirectory(dataRoot);
            var settings = AppSettings.Load(dataRoot);
            settings.Validate();
            if (Has("--output-port")) settings.OutputPort = int.Parse(Arg("--output-port"));
            settings.Validate();

            var moduleDirectory = Path.GetDirectoryName(modulePath)!;
            Directory.SetCurrentDirectory(moduleDirectory);
            var explicitKind = Has("--kind");
            var kind = settings.InputSource == TrackingInputSource.BaballoniaDesktop
                ? ModuleKind.BaballoniaDesktop : ModuleKind.BabbleHeadsetRuntime;
            if (explicitKind && (!Enum.TryParse(Arg("--kind"), ignoreCase: true, out kind) || !Enum.IsDefined(kind)))
                throw new ArgumentException("Choose a supported tracking module.");
            var provider = ModuleCatalog.Get(kind);
            // This label never chooses a transport or changes module configuration.
            var moduleName = Arg("--module-name", provider.Name).Trim();
            if (moduleName.Length is 0 or > 200 || moduleName.Any(char.IsControl))
                throw new ArgumentException("The module display name must be between 1 and 200 characters.");
            var babbleModule = kind is ModuleKind.BabbleHeadsetRuntime or ModuleKind.BaballoniaDesktop;
            var headsetSource = kind == ModuleKind.BabbleHeadsetRuntime;
            var desktopSource = kind == ModuleKind.BaballoniaDesktop;
            var inputPort = !explicitKind && headsetSource ? settings.LinkPort : provider.InputPort;
            if (Has("--input-port"))
            {
                if (!babbleModule) throw new ArgumentException($"{moduleName} manages its tracking input automatically.");
                inputPort = int.Parse(Arg("--input-port"));
            }
            if (inputPort is < 0 or > 65535 || (babbleModule && inputPort == 0))
                throw new ArgumentException("The tracking input port must be between 1 and 65535.");
            if (inputPort != 0 && (inputPort == settings.OutputPort || inputPort == settings.ReceivePort))
                throw new ArgumentException("The tracking input port must differ from the VRChat send and receive ports.");

            if (babbleModule)
            {
                // The two Babble profiles share a DLL but have different listeners. Other providers
                // own their configuration and must never receive a BabbleConfig.json file.
                var configPath = Path.Combine(moduleDirectory, "BabbleConfig.json");
                var config = File.Exists(configPath) ? JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new() : new JsonObject();
                config["Host"] ??= "127.0.0.1";
                config["Port"] ??= 8888;
                if (desktopSource)
                {
                    // Legacy ZIPs can default to face only; this profile owns both tracking channels.
                    config["IsEyeSupported"] = true;
                    config["IsFaceSupported"] = true;
                    if (explicitKind || Has("--input-port")) config["Port"] = inputPort;
                    else inputPort = config["Port"]!.GetValue<int>();
                }
                else
                {
                    config["IsEyeSupported"] ??= true;
                    config["IsFaceSupported"] ??= true;
                }
                config["LinkEnabled"] = headsetSource;
                config["LinkPort"] = headsetSource ? inputPort : settings.LinkPort;
                var desktopInputPort = config["Port"]!.GetValue<int>();
                if (desktopInputPort is < 1 or > 65535 || desktopInputPort == settings.OutputPort
                    || desktopInputPort == settings.ReceivePort || (headsetSource && desktopInputPort == inputPort))
                    throw new ArgumentException("The Baballonia OSC port must differ from the Headset Link and VRChat ports.");
                File.WriteAllText(configPath, config.ToJsonString(new() { WriteIndented = true }));
                if (headsetSource) CheckPort(inputPort, IPAddress.Any);
                CheckPort(desktopInputPort, IPAddress.Parse(config["Host"]!.GetValue<string>()));
            }
            else if (kind == ModuleKind.SteamLink) CheckPort(inputPort, IPAddress.Loopback);
            else if (kind == ModuleKind.EyeTrackVR)
            {
                var configuration = Path.Combine(moduleDirectory, "ETVRModuleConfig.json");
                if (File.Exists(configuration))
                {
                    var config = JsonNode.Parse(File.ReadAllText(configuration)) as JsonObject
                        ?? throw new InvalidDataException("The EyeTrackVR module configuration is not a JSON object.");
                    inputPort = config["PortNumber"]?.GetValue<int>() ?? inputPort;
                    if (inputPort is < 1 or > 65535 || inputPort == settings.OutputPort || inputPort == settings.ReceivePort)
                        throw new ArgumentException("The EyeTrackVR input port must differ from the VRChat send and receive ports.");
                    CheckPort(inputPort, IPAddress.Parse(config["ListeningAddress"]?.GetValue<string>() ?? "0.0.0.0"));
                }
                else CheckPort(inputPort, IPAddress.Any);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var input = await Console.In.ReadLineAsync(stop.Token);
                        if (input == null || input.Equals("stop", StringComparison.OrdinalIgnoreCase)) { stop.Cancel(); break; }
                    }
                }
                catch (OperationCanceledException) { }
            });
            if (Has("--parent"))
            {
                int parent = int.Parse(Arg("--parent"));
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            using var process = Process.GetProcessById(parent);
                            if (process.HasExited) { stop.Cancel(); break; }
                            await Task.Delay(1000, stop.Token);
                        }
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or OperationCanceledException) { stop.Cancel(); }
                });
            }
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            using var logs = LoggerFactory.Create(builder => builder.AddProvider(new JsonLogProvider()));
            var mutator = new UnifiedTrackingMutator(logs.CreateLogger<UnifiedTrackingMutator>(), new NoSettings());
            mutator.Enabled = false;
            var target = new WorkerOscTarget();
            send = new OscSendService(logs.CreateLogger<OscSendService>(), target);
            long messages = 0;
            send.OnMessagesDispatched = n => Interlocked.Add(ref messages, n);
            target.DestinationAddress = settings.OutputHost;
            target.OutPort = settings.OutputPort;
            target.InPort = settings.ReceivePort;
            sender = new ParameterSenderService(send);

            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                if (name.Name is "VRCFaceTracking.Core" or "VRCFaceTracking.SDK") return null;
                var dependency = Path.Combine(moduleDirectory, name.Name + ".dll");
                return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
            };
            module = LoadModule(modulePath);
            module.Logger = logs.CreateLogger(moduleName);
            moduleNeedsTeardown = kind is not (ModuleKind.VirtualDesktop or ModuleKind.Beyond or ModuleKind.SRanipal);
            if (kind is ModuleKind.VirtualDesktop or ModuleKind.Beyond or ModuleKind.SRanipal)
            {
                // Its upstream Initialize returns no claims when the Streamer's IPC
                // objects do not exist yet. Keep selection alive without opening OSC
                // ports or sending neutral tracking until a session becomes available.
                await WaitForTrackingSoftware(kind, stop.Token);
                moduleNeedsTeardown = kind != ModuleKind.SRanipal;
            }
            var claims = kind == ModuleKind.SRanipal
                ? await InitializeSRanipal(module, stop.Token) : module.Initialize(true, true);
            moduleNeedsTeardown = true;
            // A headset can have both channels disabled in its saved configuration. Its
            // live BFR Link still needs to negotiate the next headset session's claims.
            if (!claims.eyeSuccess && !claims.expressionSuccess && !headsetSource)
                throw new InvalidOperationException(kind == ModuleKind.VirtualDesktop
                    ? "Virtual Desktop could not initialize. Start Virtual Desktop Streamer 1.30 or later, enable 'Forward tracking data to PC', and connect your headset before starting the bridge."
                    : $"{moduleName} did not enable eye or face tracking. Check the module's tracking settings and start its tracking software before trying again.");
            module.Status = ModuleState.Active;
            // The VRCFT mapping exposes these static status values to EyeTrackingActive / ExpressionTrackingActive.
            SetCoreStatus("EyeStatus", claims.eyeSuccess);
            SetCoreStatus("ExpressionStatus", claims.expressionSuccess);
            if (headsetSource)
            {
                if (module.GetType().GetField("babbleLink", BindingFlags.Instance | BindingFlags.NonPublic) == null)
                    throw new InvalidOperationException("This Babble module does not support the headset runtime. Import a current runtime-compatible module ZIP, or select its Baballonia desktop profile.");
                if (GetField(module, "babbleLink") == null)
                    throw new InvalidOperationException("Baballonia could not start BFR Link. Close other tracking apps using UDP " + inputPort + ".");
            }
            // A newer compatible SDK may expose the reinitialization event; support it without requiring that API in v5.4.1.
            var reinitEvent = module.GetType().GetEvent("ReinitializeRequested", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Action requestRestart = () =>
            {
                Emit(new { type = "restart", message = headsetSource
                    ? "The headset changed its eye or mouth tracking mode."
                    : $"{moduleName} changed its eye or mouth tracking mode." });
                stop.Cancel();
            };
            if (reinitEvent?.EventHandlerType == typeof(Action)) reinitEvent.AddEventHandler(module, requestRestart);

            // Set the source policy before every mapper/sender callback, including the first frame.
            // The disabled mutator passes through this same data object.
            if (babbleModule)
            {
                prepareEyes = data => data.Eye.NormalizedPupilRange = GetHeadsetEyeInputs(module).NormalizedDilation ? (2f, 8f) : null;
                UnifiedTracking.OnUnifiedDataUpdated = prepareEyes + UnifiedTracking.OnUnifiedDataUpdated;
            }
            nativeFace = new NativeFaceSender(target, logs.CreateLogger<NativeFaceSender>());
            nativeFace.Start();
            router = new AvatarRouter(logs, target, sender, settings, Has("--all-parameters"), nativeFace, Has("--no-discovery"));
            await router.StartAsync(stop.Token);
            await sender.StartAsync(stop.Token);
            Emit(new
            {
                type = "ready", eye = claims.eyeSuccess, face = claims.expressionSuccess,
                source = kind.ToString(), inputPort,
                message = kind switch
                {
                    ModuleKind.BabbleHeadsetRuntime => "Module initialized; waiting for the headset.",
                    ModuleKind.BaballoniaDesktop => $"Module initialized; listening for Baballonia desktop OSC on UDP {inputPort}.",
                    ModuleKind.VirtualDesktop => "Module initialized; connected to Virtual Desktop Streamer.",
                    ModuleKind.SteamLink => $"Module initialized; listening for Steam Link OSC on UDP {inputPort}.",
                    ModuleKind.SRanipal => "Module initialized; connected to the SRanipal runtime.",
                    ModuleKind.Beyond => "Module initialized; connected to Beyond Eyetracking.",
                    ModuleKind.EyeTrackVR => $"Module initialized; listening for EyeTrackVR OSC on UDP {inputPort}.",
                    _ => $"{moduleName} initialized; its tracking input is managed by the module."
                }
            });
            var clock = Stopwatch.StartNew();
            long lastMessages = 0;
            long lastFrames = 0;
            long lastPackets = 0;
            var lastDataAt = DateTime.MinValue;
            double lastStatus = -1;
            while (!stop.IsCancellationRequested)
            {
                var updateStarted = Stopwatch.GetTimestamp();
                module.Update();
                // Standard SDK modules may return immediately. Cooperate with the OS
                // rather than busy-spinning a CPU core; existing providers keep their pacing.
                if (kind == ModuleKind.Custom && Stopwatch.GetElapsedTime(updateStarted) < TimeSpan.FromMilliseconds(1))
                    Thread.Sleep(1);
                if (clock.Elapsed.TotalSeconds - lastStatus < .25) continue;
                var elapsed = clock.Elapsed.TotalSeconds - Math.Max(0, lastStatus);
                lastStatus = clock.Elapsed.TotalSeconds;
                var link = headsetSource ? GetField(module, "babbleLink") : null;
                var sid = GetField(link, "_sid") as string;
                var lastSeen = GetField(link, "_lastSeen") is DateTime date ? date : DateTime.MinValue;
                var packetCount = GetField(link, "_rx") is long rx ? rx : 0;
                var connected = headsetSource ? sid != null && DateTime.UtcNow - lastSeen < TimeSpan.FromSeconds(3.5) : kind != ModuleKind.Custom;
                if (packetCount != lastPackets) { lastDataAt = DateTime.UtcNow; lastPackets = packetCount; }
                var dataAge = lastDataAt == DateTime.MinValue ? -1 : Math.Max(0, (DateTime.UtcNow - lastDataAt).TotalSeconds);
                var eyeInputs = babbleModule ? GetHeadsetEyeInputs(module) : (NormalizedDilation: false, Pupil: false, Widen: claims.eyeSuccess);
                var receivingKnown = headsetSource || kind == ModuleKind.VirtualDesktop;
                var receiving = headsetSource ? connected && dataAge is >= 0 and < 2.5
                    : kind == ModuleKind.VirtualDesktop && GetField(module, "_isTracking") is true;
                var total = Interlocked.Read(ref messages) + nativeFace.FramesSent;
                var frames = sender.FramesProcessed;
                Emit(new
                {
                    type = "status", eye = claims.eyeSuccess, face = claims.expressionSuccess,
                    source = kind.ToString(), inputPort,
                    connected, receiving, receivingKnown,
                    headset = headsetSource && connected ? GetField(link, "_headsetName") as string ?? "Headset" : "",
                    path = headsetSource && connected ? GetField(link, "_activePath") as string ?? "" : "",
                    packets = packetCount, packetAge = dataAge,
                    eyeLeft = Finite(UnifiedTracking.Data.Eye.Left.Openness), eyeRight = Finite(UnifiedTracking.Data.Eye.Right.Openness),
                    eyeWideLeft = Finite(UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight),
                    eyeWideRight = Finite(UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight),
                    pupilLeft = UnifiedTracking.Data.Eye.NormalizePupilDiameter(UnifiedTracking.Data.Eye.Left.PupilDiameter_MM),
                    pupilRight = UnifiedTracking.Data.Eye.NormalizePupilDiameter(UnifiedTracking.Data.Eye.Right.PupilDiameter_MM),
                    pupilAvailable = eyeInputs.Pupil, widenAvailable = eyeInputs.Widen,
                    jawOpen = Finite(UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.JawOpen].Weight),
                    messages = total, rate = Math.Round((total - lastMessages) / Math.Max(.01, elapsed)),
                    trackingRate = Math.Round((frames - lastFrames) / Math.Max(.01, elapsed)),
                    targetRate = ParameterSenderService.TargetRateHz,
                    avatar = router.AvatarName, parameters = router.ParameterCount,
                    nativeFaceTracking = nativeFace.Enabled, nativeFrames = nativeFace.FramesSent,
                    outputHost = target.DestinationAddress, outputPort = target.OutPort
                });
                lastMessages = total;
                lastFrames = frames;
                var restartMethod = kind == ModuleKind.Custom ? null
                    : module.GetType().GetMethod("RestartNeeded", BindingFlags.Instance | BindingFlags.NonPublic);
                if (restartMethod?.Invoke(module, null) is true)
                {
                    requestRestart();
                    break;
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            Emit(new { type = "fatal", message = DescribeModuleFailure(e) });
            return 1;
        }
        finally
        {
            stop.Cancel();
            nativeFace?.Dispose();
            if (prepareEyes != null)
                UnifiedTracking.OnUnifiedDataUpdated = (UnifiedTracking.OnUnifiedDataUpdated - prepareEyes) ?? (_ => { });
            if (sender != null) { try { await sender.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
            if (router != null) await router.DisposeAsync();
            send?.Dispose();
            if (module != null && moduleNeedsTeardown) { try { module.Teardown(); } catch (Exception e) { Emit(new { type = "log", message = "Module shutdown: " + e.Message }); } }
        }
    }

    private static async Task WaitForTrackingSoftware(ModuleKind kind, CancellationToken cancellationToken)
    {
        var announced = false;
        bool Available() => kind switch
        {
            ModuleKind.VirtualDesktop => VirtualDesktopObjectsAvailable(),
            ModuleKind.Beyond => SharedMemoryAvailable("VRCFTMemmapData"),
            ModuleKind.SRanipal => SRanipalRuntimeAvailable(),
            _ => true
        };
        while (!Available())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!announced)
            {
                Emit(new
                {
                    type = "waiting", source = kind.ToString(), inputPort = 0,
                    eye = false, face = false, connected = false, receiving = false,
                    message = kind switch
                    {
                        ModuleKind.Beyond => "Waiting for Beyond Eyetracking. Start the Bigscreen Beyond utility, complete enrollment, and enable VRCFT output. The bridge will connect automatically.",
                        ModuleKind.SRanipal => "Waiting for SRanipal. Install and start the SRanipal runtime and connect your supported VIVE eye or facial tracker. The bridge will connect automatically.",
                        _ => "Waiting for Virtual Desktop. Start Streamer, enable 'Forward tracking data to PC', and connect your headset. The bridge will connect automatically."
                    }
                });
                announced = true;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool SharedMemoryAvailable(string name)
    {
        using var memory = OpenFileMappingW(0x0004, false, name);
        return !memory.IsInvalid;
    }

    private static bool SRanipalRuntimeAvailable()
    {
        var processes = Process.GetProcessesByName("sr_runtime");
        try { return processes.Any(process => !process.HasExited); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static async Task<(bool eyeSuccess, bool expressionSuccess)> InitializeSRanipal(ExtTrackingModule module, CancellationToken token)
    {
        string? previousMessage = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            string message;
            var attempted = false;
            try
            {
                if (!SRanipalRuntimeAvailable()) message = "Waiting for SRanipal. Start the SRanipal runtime and connect your supported VIVE eye or facial tracker.";
                else
                {
                    attempted = true;
                    var claims = module.Initialize(true, true);
                    if (claims.eyeSuccess || claims.expressionSuccess) return claims;
                    message = "Waiting for a VIVE tracker. SRanipal is running, but has not enabled eye or face tracking. Connect the device and finish its setup; the bridge will retry automatically.";
                }
            }
            catch (Exception error) when (error is DllNotFoundException or DirectoryNotFoundException)
            {
                message = "Waiting for SRanipal setup. Import the complete SRanipal module ZIP, install its required runtime and drivers, and start SRanipal. " + error.Message;
            }
            // The upstream module acquires a native-library handle during each
            // attempt. Release that handle before retrying an unavailable device.
            // SDK/API errors deliberately remain fatal rather than being hidden.
            if (attempted)
            {
                try { module.Teardown(); }
                catch (Exception error) { Emit(new { type = "log", message = "SRanipal initialization cleanup: " + error.Message }); }
            }
            if (message != previousMessage)
            {
                Emit(new { type = "waiting", source = ModuleKind.SRanipal.ToString(), inputPort = 0,
                    eye = false, face = false, connected = false, receiving = false, message });
                previousMessage = message;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    private static bool VirtualDesktopObjectsAvailable()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Virtual Desktop tracking requires Windows.");
        // Match the access required by the upstream module. Opening these existing
        // handles never creates IPC objects, maps their contents, or signals the event.
        using var memory = OpenFileMappingW(0x0006, false, "VirtualDesktop.BodyState");
        if (memory.IsInvalid) return false;
        using var changed = OpenEventW(0x00100002, false, "VirtualDesktop.BodyStateEvent");
        return !changed.IsInvalid;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenFileMappingW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenEventW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    private static Dictionary<string, string?> ParseArguments(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            string? value = null;
            switch (name)
            {
                case "--no-discovery": case "--all-parameters": break;
                case "--module": case "--data": case "--kind": case "--module-name":
                case "--input-port": case "--output-port": case "--parent":
                    if (++index >= args.Length) throw new ArgumentException($"Missing value for {name}.");
                    value = args[index];
                    break;
                default: throw new ArgumentException($"Unknown runtime option: {name}");
            }
            if (!options.TryAdd(name, value)) throw new ArgumentException($"Duplicate runtime option: {name}");
        }
        return options;
    }

    private static ExtTrackingModule LoadModule(string modulePath)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(modulePath);
        var types = assembly.GetTypes().Where(type => !type.IsAbstract && typeof(ExtTrackingModule).IsAssignableFrom(type)).ToArray();
        if (types.Length != 1)
            throw new InvalidDataException("Choose the tracking module entry DLL containing exactly one concrete VRCFaceTracking ExtTrackingModule. If the download contains several DLLs, import the author's complete module ZIP. This build supports the VRCFaceTracking 5.4.1 SDK API.");
        if (types[0].GetConstructor(Type.EmptyTypes) == null)
            throw new InvalidDataException("The VRCFaceTracking module must have a public parameterless constructor. Obtain a standard compiled module package from its author.");
        return (ExtTrackingModule)Activator.CreateInstance(types[0])!;
    }

    private static string DescribeModuleFailure(Exception error)
    {
        while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
        if (error is ReflectionTypeLoadException types)
        {
            var details = string.Join(" ", types.LoaderExceptions.Where(item => item != null).Select(item => DescribeModuleFailure(item!)).Distinct());
            return "The module could not load its tracking types. " + details;
        }
        if (error is DllNotFoundException)
            return "A native module dependency or headset driver is missing. Import the author's complete Windows x64 module ZIP and install its required drivers or runtimes. " + error.Message;
        if (error is FileNotFoundException or FileLoadException)
            return "A module dependency could not be loaded. A standalone DLL may need other files; import the author's complete module ZIP with its dependencies. " + error.Message;
        if (error is MissingMethodException or MissingFieldException or TypeLoadException)
            return "The module or one of its dependencies requires an SDK API this build does not provide. Use a module compatible with the VRCFaceTracking 5.4.1 SDK, or update Face Tracking Bridge. " + error.Message;
        if (error is BadImageFormatException)
            return "The selected module or a dependency is not a compatible Windows x64 .NET assembly. Import the author's compiled Windows x64 VRCFaceTracking module package. " + error.Message;
        return error.Message;
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : 0;
    private static (bool NormalizedDilation, bool Pupil, bool Widen) GetHeadsetEyeInputs(ExtTrackingModule module)
    {
        var osc = GetField(module, "babbleOSC");
        var gate = GetField(osc, "_applyLock");
        if (gate == null) return default;
        lock (gate)
        {
            if (GetField(osc, "_linkAddresses") is not IEnumerable<string> addresses) return default;
            var dilation = false; var diameter = false; var widen = false;
            foreach (var address in addresses)
            {
                dilation |= address.EndsWith("Dilation", StringComparison.OrdinalIgnoreCase);
                diameter |= address.EndsWith("PupilDiameter", StringComparison.OrdinalIgnoreCase);
                widen |= address.EndsWith("Widen", StringComparison.OrdinalIgnoreCase);
            }
            return (dilation && !diameter, dilation || diameter, widen);
        }
    }
    internal static object? GetField(object? instance, string field) => instance?.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);
    private static void SetCoreStatus(string property, bool active) => typeof(UnifiedLibManager).GetProperty(property, BindingFlags.Static | BindingFlags.Public)?.SetValue(null, active ? ModuleState.Active : ModuleState.Uninitialized);
    private static void CheckPort(int port, IPAddress address)
    {
        try { using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true }; socket.Bind(new IPEndPoint(address, port)); }
        catch (SocketException) { throw new InvalidOperationException($"UDP {port} is already in use. Close the other app listening on that port, such as VRCFaceTracking or another bridge."); }
    }
}

internal sealed class JsonLogProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new JsonLogger(categoryName);
    public void Dispose() { }
    private sealed class JsonLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(logLevel)) Program.Emit(new { type = "log", level = logLevel.ToString(), message = formatter(state, exception), source = category }); }
    }
}

internal sealed class NoSettings : ILocalSettingsService
{
    public Task<T> ReadSettingAsync<T>(string key, T? defaultValue = default, bool forceLocal = false) => Task.FromResult(defaultValue!);
    public Task SaveSettingAsync<T>(string key, T value, bool forceLocal = false) => Task.CompletedTask;
    public Task Save(object target) => Task.CompletedTask;
    public Task Load(object target) => Task.CompletedTask;
}

internal sealed class WorkerOscTarget : IOscTarget
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string address = "127.0.0.1";
    private int output, input;
    private bool connected;
    public string DestinationAddress { get => address; set { address = value; PropertyChanged?.Invoke(this, new(nameof(DestinationAddress))); } }
    public int OutPort { get => output; set { output = value; PropertyChanged?.Invoke(this, new(nameof(OutPort))); } }
    public int InPort { get => input; set { input = value; PropertyChanged?.Invoke(this, new(nameof(InPort))); } }
    public bool IsConnected { get => connected; set { connected = value; PropertyChanged?.Invoke(this, new(nameof(IsConnected))); } }
}
