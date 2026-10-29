// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text;

namespace BabbleFrameBridge.Worker;

/// <summary>
/// Reads only the avatar identity and native face capability messages needed by the
/// router. OSC booleans carry their value in the type tag and have no argument bytes.
/// </summary>
internal static class AvatarOscReader
{
    private const int MaxPacketLength = 65535;
    private const int MaxDepth = 8;
    private const int MaxElements = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IEnumerable<(string Address, object Value)> Read(byte[] packet)
    {
        if (packet is null || packet.Length is 0 or > MaxPacketLength) return [];
        var result = new List<(string Address, object Value)>();
        var budget = MaxElements;
        ReadElement(packet, 0, result, ref budget);
        return result;
    }

    private static void ReadElement(ReadOnlySpan<byte> bytes, int depth, List<(string Address, object Value)> result, ref int budget)
    {
        if (depth > MaxDepth || budget-- <= 0 || bytes.Length == 0 || (bytes.Length & 3) != 0) return;
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual("#bundle\0"u8))
        {
            if (bytes.Length < 16) return;
            var offset = 16; // Bundle header and 64-bit timetag.
            while (offset < bytes.Length && budget > 0)
            {
                if (bytes.Length - offset < 4) return;
                var length = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(offset, 4));
                offset += 4;
                if (length <= 0 || (length & 3) != 0 || length > bytes.Length - offset) return;
                ReadElement(bytes.Slice(offset, length), depth + 1, result, ref budget);
                offset += length;
            }
            return;
        }

        var position = 0;
        if (!ReadString(bytes, ref position, 128, out var address)) return;
        if (address is not "/avatar/change" and not "/avatar/avatarfullfacetracking") return;
        if (!ReadString(bytes, ref position, 16, out var tags)) return;
        if (address == "/avatar/avatarfullfacetracking")
        {
            if (position != bytes.Length || tags is not ",T" and not ",F") return;
            result.Add((address, tags == ",T"));
        }
        else if (tags == ",s" && ReadString(bytes, ref position, 4096, out var value) && position == bytes.Length)
        {
            result.Add((address, value));
        }
    }

    private static bool ReadString(ReadOnlySpan<byte> bytes, ref int position, int maxLength, out string value)
    {
        value = "";
        if (position < 0 || position >= bytes.Length) return false;
        var relativeEnd = bytes[position..].IndexOf((byte)0);
        if (relativeEnd < 0 || relativeEnd > maxLength) return false;
        var end = position + relativeEnd;
        var next = (end + 4) & ~3;
        if (next > bytes.Length) return false;
        for (var i = end + 1; i < next; i++)
            if (bytes[i] != 0) return false;
        try { value = StrictUtf8.GetString(bytes.Slice(position, relativeEnd)); }
        catch (DecoderFallbackException) { return false; }
        position = next;
        return true;
    }
}
