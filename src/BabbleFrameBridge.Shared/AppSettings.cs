// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BabbleFrameBridge.Shared;

[JsonConverter(typeof(TrackingInputSourceConverter))]
public enum TrackingInputSource
{
    HeadsetRuntime = 0,
    BaballoniaDesktop = 1
}

/// <summary>Keep existing profiles readable while writing the current source names.</summary>
public sealed class TrackingInputSourceConverter : JsonConverter<TrackingInputSource>
{
    public override TrackingInputSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var name = reader.GetString()?.Trim();
            if (string.Equals(name, "HeadsetRuntime", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "SteamFrameRuntime", StringComparison.OrdinalIgnoreCase) || name == "0")
                return TrackingInputSource.HeadsetRuntime;
            if (string.Equals(name, "BaballoniaDesktop", StringComparison.OrdinalIgnoreCase) || name == "1")
                return TrackingInputSource.BaballoniaDesktop;
        }
        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            if (number == 0) return TrackingInputSource.HeadsetRuntime;
            if (number == 1) return TrackingInputSource.BaballoniaDesktop;
        }
        throw new JsonException("Choose a supported tracking input source.");
    }

    public override void Write(Utf8JsonWriter writer, TrackingInputSource value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            TrackingInputSource.HeadsetRuntime => "HeadsetRuntime",
            TrackingInputSource.BaballoniaDesktop => "BaballoniaDesktop",
            _ => throw new JsonException("Choose a supported tracking input source.")
        });
    }
}

public sealed class AppSettings
{
    public TrackingInputSource InputSource { get; set; } = TrackingInputSource.HeadsetRuntime;
    public string OutputHost { get; set; } = "127.0.0.1";
    public int OutputPort { get; set; } = 9000;
    public int ReceivePort { get; set; } = 9001;
    public bool EyeEnabled { get; set; } = true;
    public bool FaceEnabled { get; set; } = true;
    public int LinkPort { get; set; } = 6767;
    public bool AutoStart { get; set; }

    public static AppSettings Load(string root)
    {
        var path = Path.Combine(root, "settings.json");
        if (!File.Exists(path)) return new AppSettings();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The saved settings are empty.");
        settings.Validate();
        return settings;
    }

    public void Save(string root)
    {
        Validate();
        ModuleStore.WriteAtomic(Path.Combine(Path.GetFullPath(root), "settings.json"), this);
    }

    public void Validate()
    {
        if (!Enum.IsDefined(InputSource))
            throw new ArgumentException("Choose a supported tracking input source.");
        if (!IPAddress.TryParse(OutputHost, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("The VRChat destination must be an IPv4 address, such as 127.0.0.1.");
        if (OutputPort is < 1 or > 65535 || ReceivePort is < 1 or > 65535 || LinkPort is < 1 or > 65535)
            throw new ArgumentException("Ports must be between 1 and 65535.");
        if (OutputPort == ReceivePort)
            throw new ArgumentException("The VRChat output and feedback ports must be different.");
        if (!EyeEnabled && !FaceEnabled)
            throw new ArgumentException("Enable eye tracking, face tracking, or both.");
    }
}
