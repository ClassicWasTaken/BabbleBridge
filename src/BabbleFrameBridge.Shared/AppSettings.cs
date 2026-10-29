// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;

namespace BabbleFrameBridge.Shared;

public sealed class AppSettings
{
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
        if (!IPAddress.TryParse(OutputHost, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("The VRChat destination must be an IPv4 address, such as 127.0.0.1.");
        if (OutputPort is < 1 or > 65535 || ReceivePort is < 1 or > 65535 || LinkPort is < 1 or > 65535)
            throw new ArgumentException("Ports must be between 1 and 65535.");
        if (ReceivePort == LinkPort || ReceivePort == 8888 || LinkPort == 8888)
            throw new ArgumentException("The receive, Frame Link, and Baballonia OSC (8888) ports must be different.");
        if (OutputPort == ReceivePort || OutputPort == LinkPort || OutputPort == 8888)
            throw new ArgumentException("The VRChat output port must differ from the app's listening ports.");
        if (!EyeEnabled && !FaceEnabled)
            throw new ArgumentException("Enable eye tracking, face tracking, or both.");
    }
}
