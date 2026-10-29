// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BabbleFrameBridge.Shared;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using VRCFaceTracking;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.mDNS;
using VRCFaceTracking.Core.Models.ParameterDefinition.FileBased;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.OSC.Query;
using VRCFaceTracking.Core.Services;

namespace BabbleFrameBridge.Worker;

/// <summary>Read-only avatar discovery and binding around VRCFaceTracking's actual parameter mapper.</summary>
internal sealed class AvatarRouter : IAsyncDisposable
{
    private readonly ILoggerFactory logs;
    private readonly WorkerOscTarget target;
    private readonly ParameterSenderService sender;
    private readonly AppSettings settings;
    private readonly bool allParameters;
    private readonly NativeFaceSender nativeFace;
    private readonly bool noDiscovery;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource stop = new();
    private UdpClient? receiver;
    private HttpListener? listener;
    private MulticastDnsService? discovery;
    private string? pendingAvatar;
    private int pendingNativeCapability = -1;
    private int avatarRevision;
    private string configurationKey = "";
    private Task? queryLoop;
    public string AvatarName { get; private set; } = "Waiting for VRChat";
    public int ParameterCount { get; private set; }

    public AvatarRouter(ILoggerFactory logs, WorkerOscTarget target, ParameterSenderService sender, AppSettings settings, bool allParameters, NativeFaceSender nativeFace, bool noDiscovery = false)
    { this.logs = logs; this.target = target; this.sender = sender; this.settings = settings; this.allParameters = allParameters; this.nativeFace = nativeFace; this.noDiscovery = noDiscovery; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.Register(stop.Cancel);
        receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, settings.ReceivePort));
        _ = ReceiveLoop();
        if (allParameters)
        {
            sender.AllParametersRelevant = true;
            AvatarName = "All parameters (diagnostic mode)";
            ParameterCount = UnifiedTracking.AllParameters.Length;
        }
        // The bundled upstream mDNS client discovers only a game running on this PC.
        // If mDNS is unavailable, legacy /avatar/change input remains usable on ReceivePort.
        try
        {
            if (noDiscovery) { queryLoop = QueryLoop(); return Task.CompletedTask; }
            discovery = new MulticastDnsService(logs.CreateLogger<MulticastDnsService>());
            listener = new HttpListener();
            var httpPort = FindFreePort();
            listener.Prefixes.Add($"http://127.0.0.1:{httpPort}/");
            listener.Start();
            _ = HttpLoop();
            var serviceName = "VRCFT-BabbleFrame-" + Environment.ProcessId;
            discovery.Advertise("_oscjson._tcp", new AdvertisedService(serviceName, httpPort, IPAddress.Loopback));
            discovery.Advertise("_osc._udp", new AdvertisedService(serviceName, settings.ReceivePort, IPAddress.Loopback));
            discovery.SendQuery("_oscjson._tcp.local");
        }
        catch (Exception e)
        {
            Program.Emit(new { type = "log", level = "Warning", message = "OSCQuery discovery is unavailable; legacy avatar changes still work: " + e.Message });
        }
        queryLoop = QueryLoop();
        return Task.CompletedTask;
    }

    private async Task ReceiveLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var result = await receiver!.ReceiveAsync(stop.Token);
                foreach (var message in AvatarOscReader.Read(result.Buffer))
                {
                    if (message.Address == "/avatar/change" && message.Value is string id)
                    {
                        SetNativeMode(false);
                        Volatile.Write(ref pendingNativeCapability, -1);
                        pendingAvatar = id;
                        Interlocked.Increment(ref avatarRevision);
                    }
                    else if (!allParameters && message.Address == "/avatar/avatarfullfacetracking" && message.Value is bool fullFaceTracking)
                    {
                        Volatile.Write(ref pendingNativeCapability, fullFaceTracking ? 1 : 0);
                        SetNativeMode(fullFaceTracking);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception e) { Program.Emit(new { type = "log", level = "Warning", message = "Avatar message ignored: " + e.Message }); }
        }
    }

    private async Task QueryLoop()
    {
        var iterations = 0;
        while (!stop.IsCancellationRequested)
        {
            string? requestedAvatar = null;
            try
            {
                if (!allParameters)
                {
                    var id = requestedAvatar = Interlocked.Exchange(ref pendingAvatar, null);
                    var revision = Volatile.Read(ref avatarRevision);
                    var endpoint = discovery?.VrchatClientEndpoint;
                    if (endpoint != null)
                    {
                        var origin = $"http://{endpoint}";
                        // Respect an explicit manual destination. Default settings follow VRChat's advertised OSC port.
                        if (settings.OutputHost == "127.0.0.1" && settings.OutputPort == 9000)
                        {
                            var hostJson = await http.GetStringAsync(origin + "/?HOST_INFO", stop.Token);
                            using var host = JsonDocument.Parse(hostJson);
                            if (host.RootElement.TryGetProperty("OSC_PORT", out var portNode) && portNode.TryGetInt32(out var port) && port is > 0 and <= 65535 && target.OutPort != port)
                                target.OutPort = port;
                        }
                        var json = await http.GetStringAsync(origin + "/avatar", stop.Token);
                        if (revision != Volatile.Read(ref avatarRevision)) continue;
                        var node = JsonConvert.DeserializeObject<OscQueryNode>(json);
                        if (node?.Contents != null)
                        {
                            var info = new OscQueryAvatarInfo(node);
                            var native = node.Contents.TryGetValue("avatarfullfacetracking", out var capability)
                                && capability.Value?.FirstOrDefault() is bool enabled && enabled;
                            SetNativeMode(native);
                            // VALUE fields change continuously. Rebind only when avatar / addresses / types change.
                            var key = info.Id + "|" + native + "|" + string.Join("|", info.Parameters.Select(p => p.Address + ":" + p.Type));
                            if (key != configurationKey)
                            {
                                // OSCQuery already provides a complete mapping. Scanning every
                                // cached avatar for a friendly name must not delay eye tracking.
                                Bind(info.Parameters, info.Name ?? info.Id ?? "VRChat avatar", key);
                            }
                        }
                    }
                    else if (id != null)
                    {
                        var avatar = await FindAvatar(id);
                        if (revision != Volatile.Read(ref avatarRevision)) continue;
                        if (avatar != null)
                        {
                            var capability = Volatile.Read(ref pendingNativeCapability);
                            SetNativeMode(capability < 0 ? avatar.fullFaceTracking : capability == 1);
                            Bind(avatar.parameters.Where(p => p.input != null).ToArray<IParameterDefinition>(), avatar.name, id);
                        }
                        else
                            Program.Emit(new { type = "log", level = "Warning", message = "No OSC config found for this avatar. Enable OSC in VRChat, then select the avatar again." });
                    }
                }
                if (++iterations % 5 == 0 && discovery?.VrchatClientEndpoint == null) discovery?.SendQuery("_oscjson._tcp.local");
                await Task.Delay(1000, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // VRChat can restart and change OSCQuery ports. Drop the old endpoint and rediscover.
                if (discovery != null) discovery.VrchatClientEndpoint = null!;
                if (requestedAvatar != null) Interlocked.CompareExchange(ref pendingAvatar, requestedAvatar, null);
                SetNativeMode(false);
                Program.Emit(new { type = "log", level = "Warning", message = "Waiting for VRChat OSCQuery: " + e.Message });
                try { await Task.Delay(2000, stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Bind(IParameterDefinition[] parameters, string name, string key)
    {
        lock (ParameterSenderService.FrameLock)
        {
            ParameterSenderService.Clear();
            ParameterSenderService.RequestFullResend();
            var count = 0;
            foreach (var parameter in UnifiedTracking.AllParameters) count += parameter.ResetParam(parameters).Length;
            configurationKey = key;
            ParameterCount = count;
            AvatarName = string.IsNullOrWhiteSpace(name) ? "VRChat avatar" : name;
        }
        Program.Emit(new { type = "log", message = nativeFace.Enabled
            ? $"Avatar: {AvatarName}. Native face tracking enabled (VRChat SDK beta)."
            : $"Avatar: {AvatarName}. Loaded {ParameterCount} compatible tracking parameters." });
    }

    private void SetNativeMode(bool enabled)
    {
        lock (ParameterSenderService.FrameLock)
        {
            sender.LegacyOutputEnabled = !enabled;
            nativeFace.Enabled = enabled;
        }
    }

    private static async Task<AvatarConfigFile?> FindAvatar(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var root = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") + "Low", "VRChat", "VRChat", "OSC");
        if (!Directory.Exists(root)) return null;
        foreach (var user in Directory.EnumerateDirectories(root))
        {
            var directory = Path.Combine(user, "Avatars");
            if (!Directory.Exists(directory)) continue;
            // Avatar IDs are compared inside the JSON, so no untrusted path is constructed from the incoming id.
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var avatar = System.Text.Json.JsonSerializer.Deserialize<AvatarConfigFile>(await File.ReadAllTextAsync(path));
                    if (avatar?.id == id && avatar.parameters != null) return avatar;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            }
        }
        return null;
    }

    private async Task HttpLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var context = await listener!.GetContextAsync().WaitAsync(stop.Token);
                var hostInfo = context.Request.RawUrl?.Contains("HOST_INFO", StringComparison.OrdinalIgnoreCase) == true;
                object response = hostInfo
                    ? new { NAME = "Babble Bridge", OSC_IP = "127.0.0.1", OSC_PORT = settings.ReceivePort, OSC_TRANSPORT = "UDP" }
                    : new { FULL_PATH = "/", CONTENTS = new Dictionary<string, object> { ["avatar"] = new { FULL_PATH = "/avatar", CONTENTS = new Dictionary<string, object> { ["change"] = new { FULL_PATH = "/avatar/change", ACCESS = 2, TYPE = "s" } } } } };
                var bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(response));
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, stop.Token);
                context.Response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or OperationCanceledException) { break; }
            catch (Exception e) { Program.Emit(new { type = "log", level = "Warning", message = "OSCQuery response failed: " + e.Message }); }
        }
    }

    private static int FindFreePort()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        receiver?.Dispose();
        listener?.Close();
        discovery?.Dispose();
        http.Dispose();
        if (queryLoop != null) { try { await queryLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        stop.Dispose();
    }
}
