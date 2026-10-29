// Babble Bridge modification notice (2026-10-29): remove discovery startup gate, match DNS records, support unicast and release sockets.
// Upstream: Project-Babble/VRCFaceTracking b4b39da5c9fe048c960aa3b9e56742834df6ff36.
// Copyright 2024 benaclejames. Apache-2.0; see vendor/LICENSE and NOTICE.txt.
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.OSC.Query.mDNS;

namespace VRCFaceTracking.Core.mDNS;

public partial class MulticastDnsService : ObservableObject, IDisposable
{
    private static readonly IPAddress MulticastIp = IPAddress.Parse("224.0.0.251");
    private const int MulticastPost = 5353;
    private static readonly IPEndPoint MdnsEndpointIp4 = new(MulticastIp, MulticastPost);
    private readonly List<IPAddress> _localIpAddresses;
    private readonly ILogger<MulticastDnsService> _logger;

    private readonly Dictionary<IPAddress, UdpClient> Senders = new();
    private readonly Dictionary<UdpClient, CancellationToken> Receivers = new();
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AdvertisedService> Services = new();

    public Action OnVrcClientDiscovered = () => { };

    [ObservableProperty] private IPEndPoint _vrchatClientEndpoint;

    private static List<NetworkInterface> GetIpv4NetInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(net =>
            net.OperationalStatus == OperationalStatus.Up &&
            net.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
            net.SupportsMulticast)
        .ToList();

    // Get the first ipv4 address from a specific network interface
    private static IPAddress? GetIpv4Address(NetworkInterface net) => net.GetIPProperties()
        .UnicastAddresses
        .Select(addr => addr.Address)
        .FirstOrDefault(addr => addr.AddressFamily == AddressFamily.InterNetwork);

    public MulticastDnsService(ILogger<MulticastDnsService> logger)
    {
        _logger = logger;

        _localIpAddresses = GetIpv4NetInterfaces().Select(GetIpv4Address).OfType<IPAddress>().Distinct().ToList();

        // Create listeners for all interfaces
        var cts = lifetime;
        var receiver = new UdpClient(AddressFamily.InterNetwork);
        receiver.Client.ExclusiveAddressUse = false;
        receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        receiver.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPost));
        Receivers.Add(receiver, cts.Token);


        // For every ipv4 address discovered in the network interfaces, create a sender udp client set up grouping
        foreach (var ipAddress in _localIpAddresses)
        {
            // Add the local ip address to our multicast group
            receiver.JoinMulticastGroup(MulticastIp, ipAddress);

            var sender = new UdpClient(ipAddress.AddressFamily);
            sender.Client.ExclusiveAddressUse = false;
            sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sender.Client.Bind(new IPEndPoint(ipAddress, MulticastPost));    // Bind to the local ip address
            sender.JoinMulticastGroup(MulticastIp, ipAddress);                           // Join the multicast group
            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            Receivers.Add(sender, cts.Token);
            Senders.Add(ipAddress, sender);
        }

        foreach (var sender in Receivers)
        {
            _ = Listen(sender.Key, sender.Value);
        }
    }

    private async Task ResolveDnsQueries(DnsPacket packet, IPEndPoint remoteEndpoint)
    {
        // Answer immediately; waiting until we discover VRChat creates a circular
        // startup dependency when VRChat is also waiting to discover this bridge.
        if (packet.OPCODE != 0 || packet.QUERYRESPONSE)
        {
            return;
        }

        foreach (var question in packet.questions)
        {
            // Ensure the question has three labels. First for service name, second for protocol, third for domain
            // Ensure the question is for local domain
            // Ensure the question is for the _osc._udp service
            if (question.Labels.Count != 3
                || question.Labels[2] != "local"
                || !Services.TryGetValue($"{question.Labels[0]}.{question.Labels[1]}", out var service))
            {
                continue;
            }

            //foreach (var service in storedServices)
            {
                var qualifiedServiceName = new List<string>
                {
                    service.ServiceName,
                    question.Labels[0],
                    question.Labels[1],
                    question.Labels[2]
                };

                var serviceName = new List<string>
                {
                    service.ServiceName,
                    question.Labels[0].Trim('_'),
                    question.Labels[1].Trim('_')
                };

                var txt = new TXTRecord { Text = new List<string> { "txtvers=1" } };
                var srv = new SRVRecord {
                    Port = (ushort)service.Port,
                    Target = serviceName
                };
                var aRecord = new ARecord { Address = service.Address };
                var ptrRecord = new PTRRecord
                {
                    DomainLabels = qualifiedServiceName
                };

                var additionalRecords = new List<DnsResource>
                {
                    new (txt, qualifiedServiceName),
                    new (srv, qualifiedServiceName),
                    new (aRecord, serviceName)
                };

                var answers = new List<DnsResource> { new (ptrRecord, question.Labels) };

                var response = new DnsPacket
                {
                    CONFLICT = true,
                    ID = 0,
                    OPCODE = 0,
                    QUERYRESPONSE = true,
                    RESPONSECODE = 0,
                    TENTATIVE = false,
                    TRUNCATION = false,
                    questions = Array.Empty<DnsQuestion>(),
                    answers = answers.ToArray(),
                    authorities = Array.Empty<DnsResource>(),
                    additionals = additionalRecords.ToArray()
                };

                var bytes = response.Serialize();

                if (remoteEndpoint.Port == MulticastPost)
                {
                    foreach (var sender in Senders)
                    {
                        await sender.Value.SendAsync(bytes, bytes.Length, MdnsEndpointIp4);
                    }

                    continue;
                }

                using var unicast = new UdpClient(AddressFamily.InterNetwork);
                await unicast.SendAsync(bytes, remoteEndpoint, lifetime.Token);
            }
        }
    }

    public void SendQuery(string labels) => _ = SendQuerySafely(labels);

    private async Task SendQuerySafely(string labels)
    {
        try
        {
            var packet = new DnsPacket { QUERYRESPONSE = false, questions = new[] { new DnsQuestion(labels.Split('.').ToList(), 255, 1) } };
            var bytes = packet.Serialize();
            foreach (var sender in Senders.Values)
                await sender.SendAsync(bytes, MdnsEndpointIp4, lifetime.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception e) { _logger.LogDebug("mDNS query failed: {Message}", e.Message); }
    }

    public void ResolveVrChatClient(DnsPacket packet, IPEndPoint remoteEndpoint)
    {
        if (!MdnsClientResolver.TryResolve(packet, remoteEndpoint.Address, _localIpAddresses, out var endpoint)) return;
        if (endpoint.Equals(VrchatClientEndpoint)) return;
        VrchatClientEndpoint = endpoint;
        OnVrcClientDiscovered();
    }

    private async Task Listen(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(ct);
                // Discovery only controls a game on this computer, including loopback.
                if (!MdnsClientResolver.IsLocal(result.RemoteEndPoint.Address, _localIpAddresses)) continue;
                var packet = new DnsPacket(new BigReader(result.Buffer));
                ResolveVrChatClient(packet, result.RemoteEndPoint);
                await ResolveDnsQueries(packet, result.RemoteEndPoint);
            }
            catch (Exception e) when (ct.IsCancellationRequested && (e is OperationCanceledException or ObjectDisposedException or SocketException)) { break; }
            catch (Exception e) { _logger.LogDebug("mDNS packet ignored: {Message}", e.Message); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        foreach (var receiver in Receivers.Keys) receiver.Dispose();
        lifetime.Dispose();
    }
    public void Advertise(string serviceName, AdvertisedService advertisement)
    {
        _logger.LogDebug($"Advertising service {serviceName} with advertisement {advertisement}");

        Services[serviceName] = advertisement;
    }
}
