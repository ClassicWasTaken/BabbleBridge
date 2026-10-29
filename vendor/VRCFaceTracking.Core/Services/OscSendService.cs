// Babble Bridge modification notice (2026-10-29): snapshot packets before async
// sends and serialize destination changes with delivery to prevent corruption.
// Upstream: Project-Babble/VRCFaceTracking b4b39da5c9fe048c960aa3b9e56742834df6ff36.
// Copyright 2024 benaclejames. Apache-2.0; see vendor/LICENSE and NOTICE.txt.
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.OSC;
namespace VRCFaceTracking.Core.Services;
public class OscSendService : IDisposable
{
    private readonly ILogger<OscSendService> _logger;
    private readonly IOscTarget _oscTarget;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private Socket? sendSocket;
    private IPEndPoint? currentEndpoint;
    private bool disposed;
    public Action<int> OnMessagesDispatched = _ => { };
    public OscSendService(ILogger<OscSendService> logger, IOscTarget oscTarget)
    { _logger = logger; _oscTarget = oscTarget; }
    // Encode synchronously: callers can release their frame lock when this returns.
    public Task Send(OscMessage message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var buffer = new byte[4096];
        var length = fti_osc.create_osc_message(buffer, ref message._meta);
        if (length <= 0 || length > buffer.Length) throw new InvalidDataException("OSC message exceeds the packet buffer.");
        return SendPackets(new[] { (buffer[..length], 1) }, ct);
    }
    public Task Send(OscMessage[] messages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var metas = messages.Select(message => message._meta).ToArray();
        var packets = new List<(byte[] Bytes, int Count)>();
        var index = 0;
        while (index < metas.Length)
        {
            ct.ThrowIfCancellationRequested();
            var before = index;
            var buffer = new byte[4096];
            var length = fti_osc.create_osc_bundle(buffer, metas, metas.Length, ref index);
            if (length <= 0 || length > buffer.Length || index <= before || index > metas.Length)
                throw new InvalidDataException("OSC bundle serialization made no progress or exceeded the packet buffer.");
            packets.Add((buffer[..length], index - before));
        }
        return SendPackets(packets, ct);
    }
    private async Task SendPackets(IEnumerable<(byte[] Bytes, int Count)> packets, CancellationToken ct)
    {
        await sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var host = string.IsNullOrWhiteSpace(_oscTarget.DestinationAddress) ? "127.0.0.1" : _oscTarget.DestinationAddress;
            var endpoint = new IPEndPoint(IPAddress.Parse(host), _oscTarget.OutPort);
            if (!endpoint.Equals(currentEndpoint))
            {
                sendSocket?.Dispose();
                sendSocket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                sendSocket.Connect(endpoint);
                currentEndpoint = endpoint;
                _oscTarget.IsConnected = true;
            }
            foreach (var packet in packets)
            {
                var count = await sendSocket!.SendAsync(packet.Bytes, SocketFlags.None, ct).ConfigureAwait(false);
                if (count != packet.Bytes.Length) throw new IOException("Incomplete OSC datagram.");
                OnMessagesDispatched(packet.Count);
            }
        }
        catch (SocketException e)
        {
            _oscTarget.IsConnected = false;
            currentEndpoint = null;
            _logger.LogWarning("OSC delivery failed: {Message}", e.Message);
            throw;
        }
        finally { sendGate.Release(); }
    }
    public void Dispose()
    {
        sendGate.Wait();
        try
        {
            if (disposed) return;
            disposed = true;
            sendSocket?.Dispose();
            sendSocket = null;
            currentEndpoint = null;
            _oscTarget.IsConnected = false;
        }
        finally { sendGate.Release(); }
    }
}
