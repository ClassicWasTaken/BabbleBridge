// Copyright (c) 2026 Face Tracking Bridge contributors.
// SPDX-License-Identifier: Apache-2.0
// Random loopback sockets only. No module, native OSC, mDNS, game or user profile is opened.
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BabbleFrameBridge.Shared;
using BabbleFrameBridge.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Services;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
async Task Until(Func<bool> ready, string message)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
    while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
    Check(ready(), message);
}
await using var fixture = new QueryFixture();
using var output = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
using var receiveReservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
var receivePort = ((IPEndPoint)receiveReservation.Client.LocalEndPoint!).Port;
receiveReservation.Dispose();
var target = new TestTarget { OutPort = ((IPEndPoint)output.Client.LocalEndPoint!).Port, InPort = receivePort };
var settings = new AppSettings { OutputPort = target.OutPort, ReceivePort = receivePort };
using var send = new OscSendService(NullLogger<OscSendService>.Instance, target);
using var sender = new ParameterSenderService(send);
using var native = new NativeFaceSender(target, NullLogger<NativeFaceSender>.Instance);
using var cancellation = new CancellationTokenSource();
await using var router = new AvatarRouter(NullLoggerFactory.Instance, target, sender, settings, false, native,
    noDiscovery: true, endpointSource: () => fixture.Endpoint);
await router.StartAsync(cancellation.Token);
await Until(() => native.Enabled && !sender.LegacyOutputEnabled, "A validated native-capable avatar did not enable the native route.");
fixture.Fail = true;
await Until(() => BabbleFrameBridge.Worker.Program.QueryFailures > 0, "The fixture did not exercise an OSCQuery HTTP failure.");
Check(native.Enabled && !sender.LegacyOutputEnabled,
    "A transient OSCQuery failure disabled a valid native route before the avatar changed.");
using var avatar = new UdpClient();
await avatar.SendAsync(Osc("/avatar/avatarfullfacetracking", ",F"), new IPEndPoint(IPAddress.Loopback, receivePort));
await Until(() => !native.Enabled && sender.LegacyOutputEnabled, "An explicit capability=false message did not switch to legacy output.");
await avatar.SendAsync(Osc("/avatar/avatarfullfacetracking", ",T"), new IPEndPoint(IPAddress.Loopback, receivePort));
await Until(() => native.Enabled && !sender.LegacyOutputEnabled, "An explicit capability=true message did not restore native output.");
await avatar.SendAsync(Osc("/avatar/change", ",s", "avtr_router_changed"), new IPEndPoint(IPAddress.Loopback, receivePort));
await Until(() => !native.Enabled && sender.LegacyOutputEnabled, "Changing avatars retained the previous avatar's native capability.");
fixture.Native = false; fixture.Fail = false;
await Until(() => router.AvatarName != "Waiting for VRChat" && fixture.Successes >= 2,
    "OSCQuery did not recover after the transient failure.");
Check(!native.Enabled && sender.LegacyOutputEnabled, "A recovered legacy-only avatar incorrectly retained native output.");
fixture.Native = true;
await Until(() => native.Enabled && !sender.LegacyOutputEnabled, "A later successful query could not refresh avatar capability.");
Check(fixture.Requests >= 4 && fixture.Failures >= 1, "The regression skipped the local HTTP recovery path.");
Console.WriteLine($"PASS {checks} avatar routing checks: native continuity, explicit overrides, avatar-change reset and query recovery.");

static byte[] Osc(string address, string tags, string? argument = null)
{
    static byte[] Text(string text) { var bytes = Encoding.UTF8.GetBytes(text); var result = new byte[(bytes.Length + 4) & ~3]; bytes.CopyTo(result, 0); return result; }
    return Text(address).Concat(Text(tags)).Concat(argument == null ? [] : Text(argument)).ToArray();
}
internal sealed class TestTarget : IOscTarget
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public bool IsConnected { get; set; }
    public int InPort { get; set; }
    public int OutPort { get; set; }
    public string DestinationAddress { get; set; } = "127.0.0.1";
}
internal sealed class QueryFixture : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private volatile bool _fail, _native = true;
    private int _requests, _failures, _successes;
    public IPEndPoint Endpoint { get; }
    public bool Fail { get => _fail; set => _fail = value; }
    public bool Native { get => _native; set => _native = value; }
    public int Requests => Volatile.Read(ref _requests);
    public int Failures => Volatile.Read(ref _failures);
    public int Successes => Volatile.Read(ref _successes);
    public QueryFixture()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        Endpoint = (IPEndPoint)reservation.LocalEndpoint; reservation.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Endpoint.Port}/"); _listener.Start();
        _loop = Run();
    }
    private async Task Run()
    {
        while (_listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync(); Interlocked.Increment(ref _requests);
                if (_fail) { context.Response.StatusCode = 503; Interlocked.Increment(ref _failures); }
                else
                {
                    var json = JsonSerializer.Serialize(new { FULL_PATH = "/avatar", CONTENTS = new Dictionary<string, object> {
                        ["change"] = new { FULL_PATH = "/avatar/change", TYPE = "s", VALUE = new[] { "avtr_router_fixture" } },
                        ["parameters"] = new { FULL_PATH = "/avatar/parameters", CONTENTS = new Dictionary<string, object>() },
                        ["avatarfullfacetracking"] = new { FULL_PATH = "/avatar/avatarfullfacetracking", TYPE = _native ? "T" : "F", VALUE = new[] { _native } }
                    } });
                    var bytes = Encoding.UTF8.GetBytes(json); context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes);
                    Interlocked.Increment(ref _successes);
                }
                context.Response.Close();
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { break; }
        }
    }
    public async ValueTask DisposeAsync() { _listener.Close(); await _loop.WaitAsync(TimeSpan.FromSeconds(2)); }
}
namespace BabbleFrameBridge.Worker
{
    internal static class Program
    {
        private static int _queryFailures;
        public static int QueryFailures => Volatile.Read(ref _queryFailures);
        public static void Emit(object message)
        {
            var text = System.Text.Json.JsonSerializer.Serialize(message);
            if (text.Contains("Waiting for VRChat OSCQuery:", StringComparison.Ordinal)) Interlocked.Increment(ref _queryFailures);
        }
    }
}
