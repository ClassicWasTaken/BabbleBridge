// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BabbleFrameBridge.Shared;
using Microsoft.Extensions.Logging;
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
        ParameterSenderService? sender = null;
        AvatarRouter? router = null;
        NativeFaceSender? nativeFace = null;
        Action<UnifiedTrackingData>? prepareEyes = null;
        try
        {
            string Arg(string name, string? fallback = null)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback ?? throw new ArgumentException($"Missing {name}");
            }
            var modulePath = Path.GetFullPath(Arg("--module"));
            var dataRoot = Path.GetFullPath(Arg("--data"));
            if (!File.Exists(modulePath)) throw new FileNotFoundException("The selected module DLL was not found.", modulePath);
            Directory.CreateDirectory(dataRoot);
            var settings = AppSettings.Load(dataRoot);
            settings.Validate();
            if (args.Contains("--output-port")) settings.OutputPort = int.Parse(Arg("--output-port"));
            settings.Validate();

            var moduleDirectory = Path.GetDirectoryName(modulePath)!;
            Directory.SetCurrentDirectory(moduleDirectory);
            // The Frame path is always enabled. Preserve the module's negotiated eye/face claims across restarts.
            var configPath = Path.Combine(moduleDirectory, "BabbleConfig.json");
            var config = File.Exists(configPath) ? JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new() : new JsonObject();
            config["Host"] ??= "127.0.0.1";
            config["Port"] ??= 8888;
            config["IsEyeSupported"] ??= true;
            config["IsFaceSupported"] ??= true;
            config["LinkEnabled"] = true;
            config["LinkPort"] = settings.LinkPort;
            File.WriteAllText(configPath, config.ToJsonString(new() { WriteIndented = true }));
            CheckPort(settings.LinkPort, IPAddress.Any);
            CheckPort(config["Port"]!.GetValue<int>(), IPAddress.Loopback);

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
            if (args.Contains("--parent"))
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
            var send = new OscSendService(logs.CreateLogger<OscSendService>(), target);
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
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(modulePath);
            var types = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(ExtTrackingModule).IsAssignableFrom(t)).ToArray();
            if (types.Length != 1) throw new InvalidDataException("The package must contain exactly one Baballonia tracking module.");
            module = (ExtTrackingModule)Activator.CreateInstance(types[0])!;
            module.Logger = logs.CreateLogger("Baballonia");
            var claims = module.Initialize(true, true);
            module.Status = ModuleState.Active;
            // The VRCFT mapping exposes these static status values to EyeTrackingActive / ExpressionTrackingActive.
            SetCoreStatus("EyeStatus", claims.eyeSuccess);
            SetCoreStatus("ExpressionStatus", claims.expressionSuccess);
            if (module.GetType().GetField("babbleLink", BindingFlags.Instance | BindingFlags.NonPublic) != null && GetField(module, "babbleLink") == null)
                throw new InvalidOperationException("Baballonia could not start BFR Link. Close other tracking apps using UDP " + settings.LinkPort + ".");
            // A newer compatible SDK may expose the reinitialization event; support it without requiring that API in v5.4.1.
            var reinitEvent = module.GetType().GetEvent("ReinitializeRequested", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Action requestRestart = () => { Emit(new { type = "restart", message = "The headset changed its eye or mouth tracking mode." }); stop.Cancel(); };
            if (reinitEvent?.EventHandlerType == typeof(Action)) reinitEvent.AddEventHandler(module, requestRestart);

            // Set the source policy before every mapper/sender callback, including the first frame.
            // The disabled mutator passes through this same data object.
            prepareEyes = data => data.Eye.NormalizedPupilRange = GetFrameEyeInputs(module).NormalizedDilation ? (2f, 8f) : null;
            UnifiedTracking.OnUnifiedDataUpdated = prepareEyes + UnifiedTracking.OnUnifiedDataUpdated;
            nativeFace = new NativeFaceSender(target, logs.CreateLogger<NativeFaceSender>());
            nativeFace.Start();
            router = new AvatarRouter(logs, target, sender, settings, args.Contains("--all-parameters"), nativeFace, args.Contains("--no-discovery"));
            await router.StartAsync(stop.Token);
            await sender.StartAsync(stop.Token);
            Emit(new { type = "ready", eye = claims.eyeSuccess, face = claims.expressionSuccess, message = "Module initialized; waiting for Steam Frame." });
            var clock = Stopwatch.StartNew();
            long lastMessages = 0;
            long lastPackets = 0;
            var lastDataAt = DateTime.MinValue;
            double lastStatus = -1;
            while (!stop.IsCancellationRequested)
            {
                module.Update();
                if (clock.Elapsed.TotalSeconds - lastStatus < .25) continue;
                var elapsed = clock.Elapsed.TotalSeconds - Math.Max(0, lastStatus);
                lastStatus = clock.Elapsed.TotalSeconds;
                var link = GetField(module, "babbleLink");
                var sid = GetField(link, "_sid") as string;
                var lastSeen = GetField(link, "_lastSeen") is DateTime date ? date : DateTime.MinValue;
                var packetCount = GetField(link, "_rx") is long rx ? rx : 0;
                var connected = sid != null && DateTime.UtcNow - lastSeen < TimeSpan.FromSeconds(3.5);
                if (packetCount != lastPackets) { lastDataAt = DateTime.UtcNow; lastPackets = packetCount; }
                var dataAge = lastDataAt == DateTime.MinValue ? -1 : Math.Max(0, (DateTime.UtcNow - lastDataAt).TotalSeconds);
                var eyeInputs = GetFrameEyeInputs(module);
                var total = Interlocked.Read(ref messages) + nativeFace.FramesSent;
                Emit(new
                {
                    type = "status", eye = claims.eyeSuccess, face = claims.expressionSuccess,
                    connected, receiving = connected && dataAge is >= 0 and < 2.5,
                    headset = connected ? GetField(link, "_headsetName") as string ?? "Steam Frame" : "",
                    path = connected ? GetField(link, "_activePath") as string ?? "" : "",
                    packets = packetCount, packetAge = dataAge,
                    eyeLeft = Finite(UnifiedTracking.Data.Eye.Left.Openness), eyeRight = Finite(UnifiedTracking.Data.Eye.Right.Openness),
                    eyeWideLeft = Finite(UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight),
                    eyeWideRight = Finite(UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight),
                    pupilLeft = UnifiedTracking.Data.Eye.NormalizePupilDiameter(UnifiedTracking.Data.Eye.Left.PupilDiameter_MM),
                    pupilRight = UnifiedTracking.Data.Eye.NormalizePupilDiameter(UnifiedTracking.Data.Eye.Right.PupilDiameter_MM),
                    pupilAvailable = eyeInputs.Pupil, widenAvailable = eyeInputs.Widen,
                    jawOpen = Finite(UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.JawOpen].Weight),
                    messages = total, rate = Math.Round((total - lastMessages) / Math.Max(.01, elapsed)),
                    avatar = router.AvatarName, parameters = router.ParameterCount,
                    nativeFaceTracking = nativeFace.Enabled, nativeFrames = nativeFace.FramesSent,
                    outputHost = target.DestinationAddress, outputPort = target.OutPort
                });
                lastMessages = total;
                var restartMethod = module.GetType().GetMethod("RestartNeeded", BindingFlags.Instance | BindingFlags.NonPublic);
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
            Emit(new { type = "fatal", message = e.Message });
            return 1;
        }
        finally
        {
            stop.Cancel();
            nativeFace?.Dispose();
            if (prepareEyes != null) UnifiedTracking.OnUnifiedDataUpdated -= prepareEyes;
            if (sender != null) { try { await sender.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
            if (router != null) await router.DisposeAsync();
            if (module != null) { try { module.Teardown(); } catch (Exception e) { Emit(new { type = "log", message = "Module shutdown: " + e.Message }); } }
        }
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : 0;
    private static (bool NormalizedDilation, bool Pupil, bool Widen) GetFrameEyeInputs(ExtTrackingModule module)
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
        catch (SocketException) { throw new InvalidOperationException($"UDP {port} is already in use. Close VRCFaceTracking, Baballonia, or another bridge before starting this app."); }
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
