// Copyright (c) 2026 Face Tracking Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BabbleFrameBridge.Shared;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;
using VRCFaceTracking.Core.Services;
using VRCFaceTracking.Core.Types;

namespace BabbleFrameBridge.Worker;

internal sealed record ModuleInputSelection(string Path, ModuleKind Kind, string Name, int? InputPort);

/// <summary>Two isolated producers, one mapper and one VRChat connection.</summary>
internal static class SplitModuleCoordinator
{
    internal const double FreshnessSeconds = .75;
    private static readonly bool[] EyeShapes = Enumerable.Range(0, (int)UnifiedExpressions.Max + 1)
        .Select(index => ((UnifiedExpressions)index).ToString() is var name
            && (name.StartsWith("Eye", StringComparison.Ordinal) || name.StartsWith("Brow", StringComparison.Ordinal))).ToArray();

    public static async Task<int> RunAsync(ModuleInputSelection eye, ModuleInputSelection face, string dataRoot,
        AppSettings settings, bool allParameters, bool noDiscovery, CancellationToken token)
    {
        Preflight(eye, face, settings);
        using var children = new WorkerChildJob();
        await using var eyes = new InputWorker(eye, "Eyes", dataRoot, children, token);
        await using var mouth = new InputWorker(face, "Face", dataRoot, children, token);
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(new JsonLogProvider()));
        var mutator = new UnifiedTrackingMutator(logs.CreateLogger<UnifiedTrackingMutator>(), new NoSettings()) { Enabled = false };
        var target = new WorkerOscTarget { DestinationAddress = settings.OutputHost, OutPort = settings.OutputPort, InPort = settings.ReceivePort };
        using var send = new OscSendService(logs.CreateLogger<OscSendService>(), target);
        using var sender = new ParameterSenderService(send);
        using var native = new NativeFaceSender(target, logs.CreateLogger<NativeFaceSender>());
        await using var router = new AvatarRouter(logs, target, sender, settings, allParameters, native, noDiscovery);
        long messages = 0;
        send.OnMessagesDispatched = count => Interlocked.Add(ref messages, count);
        var hadEyes = false;
        Action<UnifiedTrackingData> merge = data =>
        {
            var eyeInput = eyes.Current;
            var faceInput = mouth.Current;
            var eyeLive = Active(eyeInput, true);
            var faceLive = Active(faceInput, false);
            if (eyeLive)
            {
                var snapshot = eyeInput!.Snapshot;
                CopyEye(snapshot.Left, ref data.Eye.Left); CopyEye(snapshot.Right, ref data.Eye.Right);
                data.Eye.NormalizedPupilRange = snapshot.PupilMinimum is { } min && snapshot.PupilMaximum is { } max ? (min, max) : null;
            }
            else if (hadEyes) data.Eye = new UnifiedEyeData();
            hadEyes = eyeLive;
            for (var index = 0; index < data.Shapes.Length; index++)
                data.Shapes[index].Weight = EyeShapes[index]
                    ? eyeLive ? eyeInput!.Snapshot.Shapes[index] : 0
                    : faceLive ? faceInput!.Snapshot.Shapes[index] : 0;
            SetStatus("EyeStatus", eyeLive); SetStatus("ExpressionStatus", faceLive);
        };
        UnifiedTracking.OnUnifiedDataUpdated = merge + UnifiedTracking.OnUnifiedDataUpdated;
        var started = false;
        var announced = false;
        var timer = Stopwatch.StartNew();
        var nextStatus = TimeSpan.Zero;
        long lastMessages = 0, lastFrames = 0;
        var lastStatus = TimeSpan.Zero;
        try
        {
            eyes.Start(); mouth.Start();
            while (!token.IsCancellationRequested)
            {
                if (!announced && (eyes.Settled || mouth.Settled))
                {
                    Program.Emit(new { type = "ready", source = "Combined", combined = true,
                        eye = Active(eyes.Current, true), face = Active(mouth.Current, false),
                        eyeSource = eye.Name, faceSource = face.Name,
                        message = "Separate eye and face inputs selected. Each source connects independently." });
                    announced = true;
                }
                if (eyes.Exhausted && mouth.Exhausted)
                    throw new InvalidOperationException("Neither tracking module could start. Eyes: " + eyes.Error + " Face: " + mouth.Error);
                if (!started && (Active(eyes.Current, true) || Active(mouth.Current, false)))
                {
                    native.Start();
                    await router.StartAsync(token);
                    await sender.StartAsync(token);
                    started = true;
                }
                if (timer.Elapsed >= nextStatus)
                {
                    var eyeSample = eyes.Current; var faceSample = mouth.Current;
                    var eyeAvailable = Active(eyeSample, true); var faceAvailable = Active(faceSample, false);
                    var elapsed = Math.Max(.01, (timer.Elapsed - lastStatus).TotalSeconds);
                    var total = Interlocked.Read(ref messages) + native.FramesSent;
                    var frames = sender.FramesProcessed;
                    var data = UnifiedTracking.Data;
                    Program.Emit(new
                    {
                        type = "status", source = "Combined", combined = true, inputPort = 0,
                        eye = eyeAvailable, face = faceAvailable, eyeAvailable, faceAvailable,
                        eyeSource = eye.Name, faceSource = face.Name,
                        eyeSourceState = eyes.State, faceSourceState = mouth.State,
                        eyeError = eyes.Error, faceError = mouth.Error,
                        eyeWorkerPid = eyes.ProcessId, faceWorkerPid = mouth.ProcessId,
                        eyeInputPort = eyes.InputPort, faceInputPort = mouth.InputPort,
                        connected = eyeAvailable || faceAvailable,
                        receivingKnown = Known(eyeSample) && Known(faceSample), receiving = eyeAvailable || faceAvailable,
                        eyeLeft = Finite(data.Eye.Left.Openness), eyeRight = Finite(data.Eye.Right.Openness),
                        eyeWideLeft = Finite(data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight),
                        eyeWideRight = Finite(data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight),
                        pupilLeft = data.Eye.NormalizePupilDiameter(data.Eye.Left.PupilDiameter_MM),
                        pupilRight = data.Eye.NormalizePupilDiameter(data.Eye.Right.PupilDiameter_MM),
                        pupilAvailable = eyeAvailable && eyeSample!.Snapshot.PupilAvailable,
                        widenAvailable = eyeAvailable && eyeSample!.Snapshot.WidenAvailable,
                        jawOpen = Finite(data.Shapes[(int)UnifiedExpressions.JawOpen].Weight),
                        rate = Math.Round((total - lastMessages) / elapsed), messages = total,
                        trackingRate = Math.Round((frames - lastFrames) / elapsed), targetRate = ParameterSenderService.TargetRateHz,
                        avatar = router.AvatarName, parameters = router.ParameterCount,
                        nativeFaceTracking = native.Enabled, nativeFrames = native.FramesSent,
                        outputHost = target.DestinationAddress, outputPort = target.OutPort
                    });
                    lastMessages = total; lastFrames = frames; lastStatus = timer.Elapsed;
                    nextStatus = timer.Elapsed + TimeSpan.FromMilliseconds(250);
                }
                await Task.Delay(10, token);
            }
            return 0;
        }
        finally
        {
            if (started)
            {
                try { await sender.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
            }
            UnifiedTracking.OnUnifiedDataUpdated = (UnifiedTracking.OnUnifiedDataUpdated - merge) ?? (_ => { });
        }
    }

    private static bool Active(InputSample? sample, bool eye) => sample is not null
        && Stopwatch.GetElapsedTime(sample.Received).TotalSeconds <= FreshnessSeconds
        && (eye ? sample.Snapshot.Eye : sample.Snapshot.Face) && sample.Snapshot.Connected
        && (!sample.Snapshot.ReceivingKnown || sample.Snapshot.Receiving);
    private static bool Known(InputSample? sample) => sample is not null && sample.Snapshot.ReceivingKnown;
    private static float Finite(float value) => float.IsFinite(value) ? value : 0;
    private static void CopyEye(ModuleEyeSnapshot source, ref UnifiedSingleEyeData target)
    { target.Gaze = new Vector2(source.X, source.Y); target.Openness = source.Openness; target.PupilDiameter_MM = source.Pupil; }
    private static void SetStatus(string property, bool active) => typeof(VRCFaceTracking.Core.Library.UnifiedLibManager)
        .GetProperty(property, BindingFlags.Static | BindingFlags.Public)?.SetValue(null,
            active ? VRCFaceTracking.Core.Library.ModuleState.Active : VRCFaceTracking.Core.Library.ModuleState.Uninitialized);

    private static void Preflight(ModuleInputSelection eye, ModuleInputSelection face, AppSettings settings)
    {
        if (string.Equals(Path.GetDirectoryName(eye.Path), Path.GetDirectoryName(face.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Separate tracking modules need separate library profiles so their files and settings cannot conflict. Import each module into its own profile.");
        var listeners = Listeners(eye, settings).Select(endpoint => (Selection: eye, Endpoint: endpoint))
            .Concat(Listeners(face, settings).Select(endpoint => (Selection: face, Endpoint: endpoint))).ToArray();
        for (var index = 0; index < listeners.Length; index++)
        {
            var item = listeners[index];
            if (item.Endpoint.Port is < 1 or > 65535 || item.Endpoint.Port == settings.OutputPort || item.Endpoint.Port == settings.ReceivePort)
                throw new InvalidOperationException(item.Selection.Name + " has an input port that conflicts with VRChat's connection. Choose distinct ports.");
            for (var earlier = 0; earlier < index; earlier++)
            {
                var other = listeners[earlier];
                if (item.Endpoint.Port == other.Endpoint.Port && (item.Endpoint.Address.Equals(IPAddress.Any)
                    || other.Endpoint.Address.Equals(IPAddress.Any) || item.Endpoint.Address.Equals(other.Endpoint.Address)))
                    throw new InvalidOperationException($"{eye.Name} and {face.Name} both need UDP {item.Endpoint.Port}. "
                        + "Choose different module input ports. Headset Runtime also opens Baballonia's desktop OSC listener; change that profile's BabbleConfig.json Port before combining it with another desktop input.");
            }
        }
        foreach (var item in listeners)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
                socket.Bind(item.Endpoint);
            }
            catch (SocketException) { throw new InvalidOperationException($"{item.Selection.Name} needs UDP {item.Endpoint.Port}, which is already in use. Close the other tracker or change this module's input port."); }
        }
    }

    private static IEnumerable<IPEndPoint> Listeners(ModuleInputSelection selection, AppSettings settings)
    {
        var babble = selection.Kind is ModuleKind.BabbleHeadsetRuntime or ModuleKind.BaballoniaDesktop;
        if (selection.InputPort is not null && !babble) throw new ArgumentException(selection.Name + " manages its input automatically.");
        var directory = Path.GetDirectoryName(selection.Path)!;
        JsonObject Config(string filename) => File.Exists(Path.Combine(directory, filename))
            ? JsonNode.Parse(File.ReadAllText(Path.Combine(directory, filename))) as JsonObject
                ?? throw new InvalidDataException(selection.Name + " has an invalid configuration.") : new JsonObject();
        if (babble)
        {
            var config = Config("BabbleConfig.json");
            if (selection.Kind == ModuleKind.BabbleHeadsetRuntime)
                yield return new IPEndPoint(IPAddress.Any, selection.InputPort ?? ModuleCatalog.Get(selection.Kind).InputPort);
            yield return new IPEndPoint(IPAddress.Parse(config["Host"]?.GetValue<string>() ?? "127.0.0.1"),
                selection.Kind == ModuleKind.BaballoniaDesktop ? selection.InputPort ?? 8888 : config["Port"]?.GetValue<int>() ?? 8888);
        }
        else if (selection.Kind == ModuleKind.SteamLink) yield return new IPEndPoint(IPAddress.Loopback, 9015);
        else if (selection.Kind == ModuleKind.EyeTrackVR)
        {
            var config = Config("ETVRModuleConfig.json");
            yield return new IPEndPoint(IPAddress.Parse(config["ListeningAddress"]?.GetValue<string>() ?? "0.0.0.0"), config["PortNumber"]?.GetValue<int>() ?? 8889);
        }
    }
}

internal sealed record InputSample(ModuleChannelSnapshot Snapshot, long Received);

internal sealed class InputWorker(ModuleInputSelection selection, string role, string dataRoot,
    WorkerChildJob job, CancellationToken parentToken) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
    private InputSample? current;
    private Process? process;
    private Task? supervisor;
    private volatile string state = "Starting";
    private volatile string error = "";
    private bool requestedRestart;
    private long readyAt;
    private int inputPort, exhausted;
    public InputSample? Current => Volatile.Read(ref current);
    public string State => Current is { } sample
        ? Stopwatch.GetElapsedTime(sample.Received).TotalSeconds > SplitModuleCoordinator.FreshnessSeconds ? "Stale"
            : sample.Snapshot.ReceivingKnown && !sample.Snapshot.Receiving ? "Waiting" : state : state;
    public string Error => error;
    public bool Settled => state is "Ready" or "Waiting";
    public bool Exhausted => Volatile.Read(ref exhausted) != 0;
    public int ProcessId { get { try { return process is { HasExited: false } child ? child.Id : 0; } catch (InvalidOperationException) { return 0; } } }
    public int InputPort => Volatile.Read(ref inputPort);
    public void Start() => supervisor = Supervise();

    private async Task Supervise()
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            Process? child = null;
            Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
            using var readStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            requestedRestart = false;
            Volatile.Write(ref current, null); readyAt = 0;
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the tracking worker."))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(Program).Assembly.Location);
                foreach (var argument in new[] { "--module", selection.Path, "--kind", selection.Kind.ToString(), "--module-name", selection.Name,
                    "--data", dataRoot, "--input-only", "--no-discovery", "--parent", Environment.ProcessId.ToString() }) start.ArgumentList.Add(argument);
                if (selection.InputPort is { } port) { start.ArgumentList.Add("--input-port"); start.ArgumentList.Add(port.ToString()); }
                child = Process.Start(start) ?? throw new InvalidOperationException("Input worker did not start.");
                process = child;
                job.Add(child);
                stdout = ReadOutput(child.StandardOutput, readStop.Token);
                stderr = ReadErrors(child.StandardError, readStop.Token);
                var startedAt = Stopwatch.GetTimestamp();
                while (!child.HasExited && !stop.IsCancellationRequested)
                {
                    if (stdout.IsFaulted) throw stdout.Exception!.GetBaseException();
                    if (state == "Ready" && Stopwatch.GetElapsedTime(Current?.Received ?? readyAt).TotalSeconds > 2.5
                        || state == "Starting" && Stopwatch.GetElapsedTime(startedAt).TotalSeconds > 35)
                        throw new TimeoutException("The input module stopped publishing tracking snapshots.");
                    await Task.Delay(50, stop.Token);
                }
                if (!stop.IsCancellationRequested) await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1));
                if (!stop.IsCancellationRequested && !requestedRestart && error.Length == 0)
                    error = "Input worker exited (code " + child.ExitCode + ").";
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception exception) { error = exception.GetBaseException().Message; }
            finally
            {
                readStop.Cancel();
                Volatile.Write(ref current, null);
                if (child is not null) await Close(child);
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (Exception) { /* A reader failure was already reported above; draining it must not terminate supervision. */ }
                // No buffered snapshot from the retired process may revive a
                // disconnected channel while its replacement is starting.
                Volatile.Write(ref current, null);
                process = null;
            }
            if (stop.IsCancellationRequested) break;
            state = "Restarting";
            Program.Emit(new { type = "log", level = "Warning", source = role,
                message = role + " source interrupted: " + (requestedRestart ? "tracking mode changed." : error) });
            if (!requestedRestart && ++failures > 3) { state = "Failed"; Volatile.Write(ref exhausted, 1); break; }
            try { await Task.Delay(requestedRestart ? 250 : Math.Min(3000, failures * 500), stop.Token); }
            catch (OperationCanceledException) { break; }
            state = "Starting"; error = "";
        }
    }

    private async Task ReadOutput(StreamReader reader, CancellationToken token)
    {
        long sequence = 0;
        var rateStarted = Stopwatch.GetTimestamp(); var lines = 0;
        await foreach (var line in BoundedLines(reader, token))
        {
            if (Stopwatch.GetElapsedTime(rateStarted).TotalSeconds >= 1) { lines = 0; rateStarted = Stopwatch.GetTimestamp(); }
            if (++lines > 300) throw new InvalidDataException("The input module exceeded its IPC message limit.");
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
                switch (type.GetString())
                {
                    case "input":
                        var snapshot = root.Deserialize<ModuleChannelSnapshot>();
                        if (snapshot is null || !snapshot.IsValid((int)UnifiedExpressions.Max + 1) || snapshot.Sequence <= sequence)
                            throw new InvalidDataException("The input module sent an invalid tracking snapshot.");
                        sequence = snapshot.Sequence;
                        Volatile.Write(ref current, new InputSample(snapshot, Stopwatch.GetTimestamp()));
                        break;
                    case "ready":
                        state = "Ready"; readyAt = Stopwatch.GetTimestamp();
                        if (root.TryGetProperty("inputPort", out var port) && port.TryGetInt32(out var value)) Volatile.Write(ref inputPort, value);
                        break;
                    case "waiting": state = "Waiting"; break;
                    case "restart": requestedRestart = true; break;
                    case "fatal":
                        error = root.TryGetProperty("message", out var message) ? message.GetString() ?? "Input module failed." : "Input module failed.";
                        state = "Failed"; break;
                    case "log":
                        if (root.TryGetProperty("message", out var log) && log.ValueKind == JsonValueKind.String && log.GetString() is { } text)
                            Program.Emit(new { type = "log", source = role + ": " + selection.Name, message = text[..Math.Min(500, text.Length)] });
                        break;
                }
            }
        }
    }

    private async Task ReadErrors(StreamReader reader, CancellationToken token)
    {
        await foreach (var line in BoundedLines(reader, token))
            if (line.Length > 0) Program.Emit(new { type = "log", level = "Warning", source = role, message = line[..Math.Min(line.Length, 500)] });
    }

    private static async IAsyncEnumerable<string> BoundedLines(StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new char[1024]; var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n') { yield return line.ToString(); line.Clear(); }
                else if (character != '\r')
                {
                    if (line.Length >= ModuleChannelSnapshot.MaximumLineLength) throw new InvalidDataException("The input module exceeded its IPC line limit.");
                    line.Append(character);
                }
            }
    }

    private static async Task Close(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                try { await child.StandardInput.WriteLineAsync("stop"); await child.StandardInput.FlushAsync(); }
                catch (Exception error) when (error is IOException or InvalidOperationException) { }
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            }
        }
        catch (InvalidOperationException) { }
        finally { child.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (supervisor is not null) await supervisor;
        stop.Dispose();
    }
}
