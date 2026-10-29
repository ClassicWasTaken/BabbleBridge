# Copyright (c) 2026 Babble Bridge contributors.
# SPDX-License-Identifier: Apache-2.0

"""Exercise user-supplied official modules without a headset or a live VRChat session.

Build the worker first. Supply one or more --babble-zip, --virtual-desktop-zip,
and --steam-link-zip arguments. Modules are copied into temporary directories;
their DLLs and ZIP files are never added to the repository. Network discovery is
disabled and VRChat output is captured on a random loopback port. Steam Link's
upstream module requires port 9015, so that test skips if another app owns it.
Virtual Desktop tests skip if its real Streamer shared memory already exists.
"""

import argparse
import ctypes
from ctypes import wintypes
import json
import math
import os
from pathlib import Path
import socket
import struct
import subprocess
import tempfile
import threading
import time
import zipfile

from integration_test import Worker, osc_bundle, osc_message, osc_string, read_osc, udp_socket


class ProviderWorker(Worker):
    def __init__(self, executable, module, root, environment, kind, extra=()):
        command = [str(executable), "--module", str(module), "--data", str(root),
                   "--no-discovery", "--kind", kind, *map(str, extra)]
        self.process = subprocess.Popen(
            command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", env=environment,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.records = []
        self.lines = []
        self.lock = threading.Lock()
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()

    def close(self):
        try:
            super().close()
        except OSError:
            # A provider can fail before the buffered stop command is flushed. Windows
            # then reports EINVAL while closing stdin even though the child has exited.
            if self.process.poll() is None:
                raise
            self.process.stdout.close()


class Fixture:
    def __init__(self, root, archive):
        self.root = root
        self.module_dir = root / "module"
        self.module_dir.mkdir(parents=True)
        with zipfile.ZipFile(archive) as package:
            assert sum(entry.file_size for entry in package.infolist()) < 256 * 1024 * 1024
            for entry in package.infolist():
                (self.module_dir / entry.filename).resolve().relative_to(self.module_dir)
            package.extractall(self.module_dir)
        self.manifest = json.loads((self.module_dir / "module.json").read_text(encoding="utf-8-sig"))
        self.module = self.module_dir / self.manifest["DllFileName"]
        self.capture = udp_socket()
        self.transmit = udp_socket()
        reservations = [udp_socket(), udp_socket(), udp_socket()]
        self.receive_port, self.link_port, self.input_port = [item.getsockname()[1] for item in reservations]
        for item in reservations:
            item.close()
        # Deliberately select the opposite legacy source to prove --kind owns provider selection.
        settings = {"InputSource": "HeadsetRuntime", "OutputHost": "127.0.0.1",
                    "OutputPort": self.capture.getsockname()[1], "ReceivePort": self.receive_port,
                    "LinkPort": self.link_port, "EyeEnabled": True, "FaceEnabled": True}
        (root / "settings.json").write_text(json.dumps(settings), encoding="utf-8")
        avatars = root / "LocalLow" / "VRChat" / "VRChat" / "OSC" / "usr_provider_test" / "Avatars"
        avatars.mkdir(parents=True)
        fixture = {"id": "avtr_provider_test", "name": "Provider Test", "parameters": [
            {"name": name, "input": {"address": "/avatar/parameters/" + name, "type": "Float"}}
            for name in ("v2/JawOpen", "v2/EyeOpenLeft", "v2/EyeOpenRight")]}
        (avatars / "avtr_provider_test.json").write_text(json.dumps(fixture), encoding="utf-8")
        self.environment = dict(os.environ, LOCALAPPDATA=str(root / "Local"))
        self.values = {}

    def worker(self, executable, kind, extra=()):
        return ProviderWorker(executable, self.module, self.root, self.environment, kind, extra)

    def tick(self):
        self.transmit.sendto(osc_message("/avatar/change", "avtr_provider_test"),
                             ("127.0.0.1", self.receive_port))
        while True:
            try:
                raw, _ = self.capture.recvfrom(65535)
            except BlockingIOError:
                break
            self.values.update(read_osc(raw))

    def send(self, port, values):
        for address, value in values.items():
            self.transmit.sendto(osc_message(address, value), ("127.0.0.1", port))

    def has_value(self, name, expected):
        actual = self.values.get("/avatar/parameters/" + name)
        return isinstance(actual, float) and math.isclose(actual, expected, abs_tol=0.001)

    def close(self):
        self.capture.close()
        self.transmit.close()


def wait_for(worker, description, predicate, tick=lambda: None, timeout=10, fatal_expected=False):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        tick()
        if predicate():
            return
        if worker.any(lambda record: record.get("type") == "fatal") and not fatal_expected:
            raise AssertionError(description + "\n" + worker.diagnostics())
        if worker.process.poll() is not None and not fatal_expected:
            raise AssertionError(description + "\n" + worker.diagnostics())
        time.sleep(0.01)
    raise AssertionError("Timed out: " + description + "\n" + worker.diagnostics())


def ready(worker, kind, port):
    return worker.any(lambda record: record.get("type") == "ready" and record.get("source") == kind
                      and record.get("inputPort") == port and record.get("eye") and record.get("face"))


def baballonia(executable, archive, root):
    fixture = Fixture(root, archive)
    config_path = fixture.module_dir / "BabbleConfig.json"
    config_path.write_text(json.dumps({"Host": "127.0.0.1", "Port": 8888,
                                      "IsEyeSupported": False, "IsFaceSupported": True}), encoding="utf-8")
    worker = fixture.worker(executable, "BaballoniaDesktop", ("--input-port", fixture.input_port))
    try:
        wait_for(worker, "Baballonia desktop readiness", lambda: ready(worker, "BaballoniaDesktop", fixture.input_port))
        config = json.loads(config_path.read_text())
        assert config["Port"] == fixture.input_port and config["LinkEnabled"] is False, config
        assert config["IsEyeSupported"] is True and config["IsFaceSupported"] is True, config

        def tick():
            fixture.tick()
            fixture.send(fixture.input_port, {"/jawOpen": 0.65, "/LeftEyeLid": 0.8, "/RightEyeLid": 0.6})

        wait_for(worker, "Baballonia upper and lower face OSC", lambda:
                 fixture.has_value("v2/JawOpen", 0.65) and fixture.has_value("v2/EyeOpenLeft", 0.8)
                 and fixture.has_value("v2/EyeOpenRight", 0.6), tick)
        return "PASS: desktop profile overrides legacy source, changes input port, and maps eyes plus mouth"
    finally:
        worker.close()
        fixture.close()


def headset_runtime(executable, archive, root):
    fixture = Fixture(root, archive)
    settings_path = root / "settings.json"
    settings = json.loads(settings_path.read_text())
    settings["InputSource"] = "BaballoniaDesktop"
    settings_path.write_text(json.dumps(settings), encoding="utf-8")
    config_path = fixture.module_dir / "BabbleConfig.json"
    config_path.write_text(json.dumps({"Host": "127.0.0.1", "Port": fixture.input_port,
                                      "IsEyeSupported": False, "IsFaceSupported": False}), encoding="utf-8")
    worker = fixture.worker(executable, "BabbleHeadsetRuntime", ("--input-port", fixture.link_port))
    last_control = 0
    sequence = 0
    try:
        wait_for(worker, "headset readiness with cached tracking disabled", lambda: worker.any(
            lambda item: item.get("type") == "ready" and item.get("source") == "BabbleHeadsetRuntime"
            and item.get("inputPort") == fixture.link_port and item.get("eye") is False
            and item.get("face") is False))

        def tick():
            nonlocal last_control, sequence
            fixture.tick()
            peer = ("127.0.0.1", fixture.link_port)
            if time.monotonic() - last_control > 0.2:
                sequence += 1
                common = {"proto": "bfr-link", "v": 1, "sid": "provider-test", "eye": True, "face": True}
                for message in (dict(common, type="beacon", name="Synthetic headset", version="test"),
                                dict(common, type="ping", seq=sequence, path="lan", active=True)):
                    fixture.transmit.sendto(json.dumps(message).encode(), peer)
                last_control = time.monotonic()
            fixture.transmit.sendto(osc_bundle({"/jawOpen": 0.65, "/LeftEyeLid": 0.8,
                                                "/RightEyeLid": 0.6}), peer)

        wait_for(worker, "headset renegotiation after cached channels disabled", lambda: worker.any(
            lambda item: item.get("type") == "restart"), tick)
        worker.process.wait(timeout=8)
        assert worker.process.returncode == 0, worker.diagnostics()
        worker.close()
        worker = fixture.worker(executable, "BabbleHeadsetRuntime", ("--input-port", fixture.link_port))
        wait_for(worker, "headset readiness after negotiated restart", lambda: ready(worker, "BabbleHeadsetRuntime", fixture.link_port))
        wait_for(worker, "headset provider discovery and eyes plus face output", lambda:
                 fixture.has_value("v2/JawOpen", 0.65) and fixture.has_value("v2/EyeOpenLeft", 0.8)
                 and fixture.has_value("v2/EyeOpenRight", 0.6) and worker.any(
                     lambda item: item.get("type") == "status" and item.get("connected")
                     and item.get("receiving") and item.get("source") == "BabbleHeadsetRuntime"), tick)
        config = json.loads(config_path.read_text())
        assert config["LinkPort"] == fixture.link_port and config["LinkEnabled"] is True, config
        return "PASS: headset recovers cached disabled channels through BFR negotiation, overrides saved desktop source, and maps eyes plus mouth"
    finally:
        worker.close()
        fixture.close()


def exclusive_socket(port):
    result = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
        result.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
    try:
        result.bind(("127.0.0.1", port))
        return result
    except OSError:
        result.close()
        return None


def steam_link(executable, archive, root):
    reservation = exclusive_socket(9015)
    if reservation is None:
        return "SKIP: another app owns Steam Link's fixed UDP 9015 input"
    fixture = Fixture(root, archive)
    config_path = fixture.module_dir / "BabbleConfig.json"
    config_path.write_text("foreign-provider-sentinel", encoding="utf-8")
    worker = fixture.worker(executable, "SteamLink")
    try:
        wait_for(worker, "Steam Link port conflict", lambda: worker.any(
            lambda record: record.get("type") == "fatal" and "9015" in record.get("message", "")),
                 fatal_expected=True)
        assert not worker.any(lambda record: record.get("type") == "ready"), worker.diagnostics()
    finally:
        worker.close()
        reservation.close()
    worker = fixture.worker(executable, "SteamLink")
    try:
        wait_for(worker, "Steam Link readiness", lambda: ready(worker, "SteamLink", 9015))

        def tick():
            fixture.tick()
            gaze = osc_string("/sl/eyeTrackedGazePoint") + osc_string(",fff") + struct.pack(">fff", 0.1, 0.0, -1.0)
            fixture.transmit.sendto(gaze, ("127.0.0.1", 9015))
            fixture.send(9015, {"/sl/xrfb/facew/JawDrop": 0.65, "/sl/xrfb/facew/EyesClosedL": 0.2,
                                "/sl/xrfb/facew/EyesClosedR": 0.4})

        wait_for(worker, "Steam Link upper and lower face OSC", lambda:
                 fixture.has_value("v2/JawOpen", 0.65) and fixture.has_value("v2/EyeOpenLeft", 0.8)
                 and fixture.has_value("v2/EyeOpenRight", 0.6), tick)
        assert config_path.read_text() == "foreign-provider-sentinel", "Worker changed foreign provider configuration"
        assert worker.any(lambda item: item.get("type") == "status" and item.get("source") == "SteamLink"
                          and item.get("pupilAvailable") is False), worker.diagnostics()
        return "PASS: Steam Link reports conflicts, initializes legacy SDK module, and maps eyes plus mouth on UDP 9015"
    finally:
        worker.close()
        fixture.close()


class SyntheticVirtualDesktop:
    """Create the documented provider objects only when the real Streamer is absent."""
    def __init__(self):
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel.CreateFileMappingW.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
                                                   wintypes.DWORD, wintypes.DWORD, wintypes.LPCWSTR]
        self.kernel.CreateFileMappingW.restype = wintypes.HANDLE
        self.kernel.MapViewOfFile.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD,
                                              wintypes.DWORD, ctypes.c_size_t]
        self.kernel.MapViewOfFile.restype = ctypes.c_void_p
        self.kernel.CreateEventW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
        self.kernel.CreateEventW.restype = wintypes.HANDLE
        self.kernel.SetEvent.argtypes = [wintypes.HANDLE]
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.UnmapViewOfFile.argtypes = [ctypes.c_void_p]
        self.mapping = self.event = self.view = None
        self.mapping = self.kernel.CreateFileMappingW(wintypes.HANDLE(-1), None, 4, 0, 65536,
                                                       "VirtualDesktop.BodyState")
        if not self.mapping:
            raise ctypes.WinError(ctypes.get_last_error())
        if ctypes.get_last_error() == 183:
            self.close()
            raise FileExistsError("The real Virtual Desktop Streamer is running")
        self.event = self.kernel.CreateEventW(None, False, False, "VirtualDesktop.BodyStateEvent")
        if not self.event or ctypes.get_last_error() == 183:
            self.close()
            raise FileExistsError("The real Virtual Desktop event already exists")
        self.view = self.kernel.MapViewOfFile(self.mapping, 6, 0, 0, 65536)
        if not self.view:
            self.close()
            raise ctypes.WinError(ctypes.get_last_error())
        state = bytearray(360)
        state[0] = 1  # valid face weights
        state[1] = 1  # valid eye-following weights
        state[292] = state[293] = 1  # valid left/right eye poses
        for offset in (296, 324):
            struct.pack_into("<ffff", state, offset, 0.0, 0.0, 0.0, 1.0)
        for index, value in ((12, 0.2), (13, 0.4), (24, 0.65), (59, 0.5), (60, 0.5)):
            struct.pack_into("<f", state, 4 + index * 4, value)
        ctypes.memmove(self.view, bytes(state), len(state))

    def tick(self):
        self.kernel.SetEvent(self.event)

    def close(self):
        if self.view:
            self.kernel.UnmapViewOfFile(self.view)
            self.view = None
        for name in ("event", "mapping"):
            handle = getattr(self, name, None)
            if handle:
                self.kernel.CloseHandle(handle)
                setattr(self, name, None)


def virtual_desktop(executable, archive, root):
    # Atomically detect existing provider objects, avoiding reads or writes to a real session.
    try:
        synthetic = SyntheticVirtualDesktop()
    except FileExistsError:
        return "SKIP: Virtual Desktop Streamer is running; its live shared memory was left untouched"
    synthetic.close()
    fixture = Fixture(root, archive)
    worker = fixture.worker(executable, "VirtualDesktop")
    try:
        wait_for(worker, "Virtual Desktop missing-Streamer failure", lambda: worker.any(
            lambda record: record.get("type") == "fatal" and "Streamer" in record.get("message", "")),
                 fatal_expected=True)
        assert not worker.any(lambda record: record.get("type") == "ready"), worker.diagnostics()
        assert not (fixture.module_dir / "BabbleConfig.json").exists(), "Worker created Babble configuration for VD"
    finally:
        worker.close()
    synthetic = SyntheticVirtualDesktop()
    worker = fixture.worker(executable, "VirtualDesktop")
    try:
        wait_for(worker, "Virtual Desktop shared-memory readiness", lambda: ready(worker, "VirtualDesktop", 0), synthetic.tick)
        last_event = 0

        def tick():
            nonlocal last_event
            fixture.tick()
            # An 80 ms source cadence makes Update wait on the provider event. The
            # independent output scheduler must continue processing at its target rate.
            if time.monotonic() - last_event > 0.08:
                synthetic.tick()
                last_event = time.monotonic()

        wait_for(worker, "Virtual Desktop shared-memory eyes and face", lambda:
                 fixture.has_value("v2/JawOpen", 0.65) and fixture.has_value("v2/EyeOpenLeft", 0.8)
                 and fixture.has_value("v2/EyeOpenRight", 0.6), tick)
        wait_for(worker, "Virtual Desktop live status", lambda: worker.any(
            lambda item: item.get("type") == "status" and item.get("source") == "VirtualDesktop"
            and item.get("receiving") and item.get("pupilAvailable") is False), tick)
        wait_for(worker, "independent 90 Hz sender with a slower blocking VD input", lambda: worker.any(
            lambda item: item.get("type") == "status" and item.get("targetRate") == 90
            and 75 <= item.get("trackingRate", 0) <= 110), tick)
        return "PASS: VD fails clearly without Streamer, maps eyes plus face from shared memory, and keeps its 90 Hz sender independent of a slow input"
    finally:
        worker.close()
        synthetic.close()
        fixture.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--babble-zip", type=Path)
    parser.add_argument("--virtual-desktop-zip", type=Path)
    parser.add_argument("--steam-link-zip", type=Path)
    args = parser.parse_args()
    assert os.name == "nt", "These provider compatibility tests require Windows"
    worker = args.worker.resolve(strict=True)
    checks = []
    with tempfile.TemporaryDirectory(prefix="babble-provider-tests-") as directory:
        root = Path(directory)
        for name, archive, test in (("headset", args.babble_zip, headset_runtime),
                                    ("baballonia", args.babble_zip, baballonia),
                                    ("steam-link", args.steam_link_zip, steam_link),
                                    ("virtual-desktop", args.virtual_desktop_zip, virtual_desktop)):
            if archive:
                checks.append(test(worker, archive.resolve(strict=True), root / name))
    assert checks, "Supply at least one official module ZIP"
    print(json.dumps({"checks": checks}, indent=2))


if __name__ == "__main__":
    main()
