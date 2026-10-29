// Copyright (c) 2026 Babble Bridge contributors.
// SPDX-License-Identifier: Apache-2.0
using System.Net;
using VRCFaceTracking.Core.OSC.Query.mDNS;

namespace VRCFaceTracking.Core.mDNS;

internal static class MdnsClientResolver
{
    internal static bool IsLocal(IPAddress address, IEnumerable<IPAddress> localAddresses) =>
        IPAddress.IsLoopback(address) || localAddresses.Contains(address);

    internal static bool TryResolve(DnsPacket packet, IPAddress peer, IEnumerable<IPAddress> localAddresses, out IPEndPoint endpoint)
    {
        endpoint = null!;
        if (!packet.QUERYRESPONSE || packet.OPCODE != 0 || !IsLocal(peer, localAddresses)) return false;
        var records = packet.answers.Concat(packet.additionals).Concat(packet.authorities).ToArray();
        foreach (var record in records)
        {
            if (record.Data is not PTRRecord ptr || !SameName(record.Labels, new[] { "_oscjson", "_tcp", "local" })
                || ptr.DomainLabels.Count != 4
                || (!ptr.DomainLabels[0].StartsWith("VRChat-Client", StringComparison.OrdinalIgnoreCase)
                    && !ptr.DomainLabels[0].StartsWith("ChilloutVR-GameClient", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var service in records.Where(r => SameName(r.Labels, ptr.DomainLabels)))
            {
                if (service.Data is not SRVRecord srv || srv.Port == 0) continue;
                foreach (var host in records.Where(r => SameName(r.Labels, srv.Target)))
                {
                    if (host.Data is not ARecord address || address.Address == null || !IsLocal(address.Address, localAddresses)) continue;
                    endpoint = new IPEndPoint(address.Address, srv.Port);
                    return true;
                }
            }
        }
        return false;
    }

    private static bool SameName(IEnumerable<string> left, IEnumerable<string> right) =>
        left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
}
