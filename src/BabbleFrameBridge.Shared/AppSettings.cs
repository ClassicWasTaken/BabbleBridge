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
    public bool StartWithSteamVr { get; set; }
    public bool CloseWithSteamVr { get; set; }
    public bool StartMinimized { get; set; }
    public bool KittyEnabled { get; set; }
    public bool KittySoundsEnabled { get; set; } = true;

    public static AppSettings Load(string root)
    {
        var directory = Path.GetFullPath(root);
        var path = Path.Combine(directory, "settings.json");
        var backupPath = path + ".backup";
        if (!File.Exists(path) && !File.Exists(backupPath)) return new AppSettings();
        Exception? firstError = null;
        foreach (var candidate in new[] { path, backupPath })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate))
                    ?? throw new InvalidDataException("The saved settings are empty.");
                settings.Validate();
                if (candidate == backupPath)
                {
                    EnsureSafeWritePaths(directory, path, backupPath);
                    // Keep both the known-good backup and the invalid original. Do not
                    // let File.Replace copy the corrupt primary over the valid backup.
                    if (File.Exists(path)) File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                    ModuleStore.WriteAtomic(path, settings, keepBackup: false);
                }
                return settings;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            { firstError ??= error; }
        }
        throw new InvalidDataException("The saved connection settings could not be loaded or recovered. Your settings file and backup have been preserved. Restore a valid settings.json or settings.json.backup in the app data folder, then reopen the app.", firstError);
    }

    public void Save(string root)
    {
        Validate();
        var directory = Path.GetFullPath(root);
        var path = Path.Combine(directory, "settings.json");
        EnsureSafeWritePaths(directory, path, path + ".backup");
        ModuleStore.WriteAtomic(path, this);
    }

    private static void EnsureSafeWritePaths(params string[] paths)
    {
        foreach (var path in paths)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Saved settings cannot be repaired or written through symbolic links or junctions.");
        }
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
