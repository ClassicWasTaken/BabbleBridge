# Copyright (c) 2026 Frame Bridge contributors.
# SPDX-License-Identifier: Apache-2.0

"""Test native VRChat beta face OSC with the actual Baballonia DLL on isolated ports.

Build the worker, then run:
    python tests/native_integration_test.py --worker <BabbleFrameBridge.Worker.exe>

The --no-discovery worker flag prevents a live VRChat session from affecting this
fixture. Only temporary module/config copies and random loopback ports are used.
"""

import argparse
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import threading
import time
import zipfile

from integration_test import Frame, Worker, await_condition, osc_bundle, osc_message, osc_string, read_osc, udp_socket


def native_payloads(packet):
    """Decode the published OSC blob boundary, independently of the C# encoder."""
    if packet.startswith(b"#bundle\0"):
        offset = 16
        while offset + 4 <= len(packet):
            length = struct.unpack_from(">I", packet, offset)[0]
            offset += 4
            assert length > 0 and offset + length <= len(packet), "Malformed OSC bundle"
            yield from native_payloads(packet[offset:offset + length])
            offset += length
        return
    if not packet.startswith(b"/tracking/face/v1\0"):
        return
    assert packet[:20] == b"/tracking/face/v1\0\0\0", "Native face address padding"
    assert packet[20:24] == b",b\0\0", "Native face argument must be an OSC blob"
    assert len(packet) == 132, f"Native face packet length: {len(packet)}"
    assert struct.unpack_from(">I", packet, 24)[0] == 102, "Native face payload must contain 102 bytes"
    assert packet[130:] == b"\0\0", "Native face trailing blob padding"
    yield packet[28:130]


class NativeCapture:
    def __init__(self, capture):
        self.capture = capture
        self.frames = []
        self.legacy = []

    def recvfrom(self, size):
        packet, peer = self.capture.recvfrom(size)
        self.frames.extend((time.monotonic(), payload) for payload in native_payloads(packet))
        self.legacy.extend((time.monotonic(), address, value) for address, value in read_osc(packet)
                           if address.startswith(("/avatar/parameters/", "/tracking/eye/")))
        return packet, peer


class IsolatedWorker(Worker):
    def __init__(self, executable, module, root, environment):
        command = [str(executable), "--module", str(module), "--data", str(root), "--no-discovery"]
        self.process = subprocess.Popen(
            command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", env=environment,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        self.records, self.lines = [], []
        self.lock = threading.Lock()
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()

    def diagnostics(self):
        with self.lock:
            relevant = [line for line in self.lines if '"type":"status"' not in line]
            return "\n".join(relevant[-25:] + self.lines[-3:])


class NativeFrame(Frame):
    def __init__(self, link_port, receive_port, capture):
        super().__init__(link_port, receive_port, capture)
        self.next_data = self.next_avatar = float("inf")
        self.avatar_id = None
        self.capability_override = None
        self.last_avatar = None
        self.next_eye_data = 0
        self.tracking = {
            "/jawOpen": 0.65,
            "/LeftEyeX": 0.3, "/RightEyeX": 0.25,
            "/LeftEyeY": -0.1, "/RightEyeY": -0.1,
            "/LeftEyeLid": 0.8, "/RightEyeLid": 0.6,
            "/LeftEyeWiden": 0.65, "/RightEyeWiden": 0.35,
            "/LeftEyeDilation": 0.8, "/RightEyeDilation": 0.2,
        }

    def tick(self):
        now = time.monotonic()
        avatar = (self.avatar_id, self.capability_override)
        if self.avatar_id is not None and avatar != self.last_avatar:
            packet = osc_message("/avatar/change", self.avatar_id)
            if self.capability_override is not None:
                capability = osc_string("/avatar/avatarfullfacetracking") + osc_string(
                    ",T" if self.capability_override else ",F")
                packet = b"#bundle\0" + struct.pack(">Q", 1) + b"".join(
                    struct.pack(">I", len(item)) + item for item in (packet, capability))
            self.socket.sendto(packet, self.avatar_peer)
            self.last_avatar = avatar
        if now >= self.next_eye_data:
            self.socket.sendto(osc_bundle(self.tracking), self.peer)
            self.next_eye_data = now + 1 / 60
        super().tick()


def pump(frame, worker, seconds):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        frame.tick()
        assert worker.process.poll() is None, worker.diagnostics()
        assert not worker.any(lambda item: item.get("type") == "fatal"), worker.diagnostics()
        time.sleep(0.006)


def approximately(actual, expected):
    return abs(actual - expected * 255) <= 1.1


def matches(payload, expected):
    return all(approximately(payload[index], value) for index, value in expected.items())


def run(executable, archive):
    capture_socket = udp_socket()
    reservations = [udp_socket() for _ in range(3)]
    receive_port, link_port, legacy_port = [item.getsockname()[1] for item in reservations]
    for item in reservations:
        item.close()
    checks = []
    try:
        with tempfile.TemporaryDirectory(prefix="babble-frame-native-integration-") as temporary:
            root = Path(temporary).resolve()
            module_dir = root / "module"
            module_dir.mkdir()
            with zipfile.ZipFile(archive) as package:
                package.extractall(module_dir)
            manifest = json.loads((module_dir / "module.json").read_text(encoding="utf-8-sig"))
            module = module_dir / manifest["DllFileName"]
            config_path = module_dir / "BabbleConfig.json"
            config = json.loads(config_path.read_text(encoding="utf-8-sig"))
            config.update(Host="127.0.0.1", IsEyeSupported=True, IsFaceSupported=True,
                          LinkEnabled=True, LinkPort=link_port, Port=legacy_port)
            config_path.write_text(json.dumps(config), encoding="utf-8")
            settings = {
                "OutputHost": "127.0.0.1", "OutputPort": capture_socket.getsockname()[1],
                "ReceivePort": receive_port, "LinkPort": link_port,
                "EyeEnabled": True, "FaceEnabled": True, "AutoStart": False,
            }
            (root / "settings.json").write_text(json.dumps(settings), encoding="utf-8")
            avatar_folder = root / "LocalLow" / "VRChat" / "VRChat" / "OSC" / "usr_native_test" / "Avatars"
            avatar_folder.mkdir(parents=True)
            names = ("v2/JawOpen", "v2/EyeOpenLeft", "v2/EyeOpenRight", "v2/EyeWideLeft",
                     "v2/EyeWideRight", "v2/PupilDilation")
            for name, capability in (("unsupported", None), ("enabled", True), ("disabled", False)):
                avatar_id = "avtr_native_" + name
                fixture = {
                    "id": avatar_id, "name": "Native integration " + name,
                    "parameters": [{"name": parameter, "input": {
                        "address": "/avatar/parameters/" + parameter, "type": "Float"}}
                        for parameter in names],
                }
                if capability is not None:
                    fixture["fullFaceTracking"] = capability
                (avatar_folder / (avatar_id + ".json")).write_text(json.dumps(fixture), encoding="utf-8")
            environment = dict(os.environ, LOCALAPPDATA=str(root / "Local"))
            capture = NativeCapture(capture_socket)
            frame = NativeFrame(link_port, receive_port, capture)
            worker = IsolatedWorker(executable, module, root, environment)
            try:
                await_condition("real module eye/face initialization", lambda: worker.any(
                    lambda item: item.get("type") == "ready" and item.get("eye") and item.get("face")), frame, worker)
                frame.avatar_id = "avtr_native_unsupported"
                await_condition("isolated BFR heartbeat and legacy avatar mapping", lambda:
                                frame.module_rx > 0 and frame.has_value("v2/JawOpen", 0.65)
                                and frame.has_value("v2/EyeOpenLeft", 0.8)
                                and frame.has_value("v2/EyeOpenRight", 0.6), frame, worker)
                pump(frame, worker, 1.2)
                assert not capture.frames, "Avatar without native support received face blobs"
                checks.append("unsupported avatar receives legacy tracking only; live VRChat discovery is disabled")

                frame.avatar_id = "avtr_native_enabled"
                expected = {8: 0.4, 9: 0.2, 10: 0, 11: 0.6, 12: 0.6, 13: 0,
                            16: 0.35, 17: 0.65, 36: 0.65}
                await_condition("102-byte native blob with eye closure, widen, pupils and jaw", lambda:
                                bool(capture.frames) and matches(capture.frames[-1][1], expected), frame, worker)
                assert all(value == 0 for value in capture.frames[-1][1][18:26]), "Absent eyebrow channels must stay neutral"
                checks.append("supported avatar receives OSC blob with 102 bytes in the published field order")
                checks.append("normalized pupil dilation/constriction, eyelid closure and eye widen survive the real module")

                pump(frame, worker, 0.2)
                marker = time.monotonic()
                frame.tracking.update({"/LeftEyeLid": 0.0, "/RightEyeLid": 1.0,
                                       "/LeftEyeWiden": 0.1, "/RightEyeWiden": 0.9,
                                       "/LeftEyeDilation": 0.25, "/RightEyeDilation": 0.75,
                                       "/jawOpen": 0.42})
                expected = {8: 0, 9: 1, 10: 0.5, 11: 0, 12: 0, 13: 0.5,
                            16: 0.9, 17: 0.1, 36: 0.42}
                await_condition("dynamic blink, pupil reversal and eye widen update", lambda:
                                any(stamp >= marker and matches(payload, expected) for stamp, payload in capture.frames), frame, worker)
                pump(frame, worker, 0.3)
                leaked = [(address, value) for stamp, address, value in capture.legacy if stamp >= marker]
                assert not leaked, "Native mode must suppress legacy parameters: " + repr(leaked[:8])
                checks.append("blink and pupil direction changes update native output without conflicting legacy OSC")

                frame.avatar_id = "avtr_native_disabled"
                await_condition("avatar capability disables native output", lambda: worker.any(
                    lambda item: item.get("type") == "log" and "Native integration disabled" in item.get("message", "")), frame, worker)
                pump(frame, worker, 0.2)
                stopped_count = len(capture.frames)
                frame.tracking["/jawOpen"] = 0.27
                await_condition("legacy OSC continues after disabling native support", lambda:
                                frame.has_value("v2/JawOpen", 0.27)
                                and frame.has_value("v2/EyeOpenLeft", 0.0)
                                and frame.has_value("v2/EyeOpenRight", 1.0)
                                and frame.has_value("v2/PupilDilation", 0.5), frame, worker)
                pump(frame, worker, 1.2)
                assert len(capture.frames) == stopped_count, "fullFaceTracking=false must stop native face packets"
                checks.append("fullFaceTracking=false stops native blobs and preserves live legacy mapping")

                # A live beta capability message follows /avatar/change in one OSC bundle.
                # It must override a stale local config, in either direction.
                marker = time.monotonic()
                frame.capability_override = True
                expected[36] = 0.27
                await_condition("bundled live true capability overrides local false", lambda:
                                any(stamp >= marker and matches(payload, expected) for stamp, payload in capture.frames), frame, worker)
                pump(frame, worker, 1.4)
                assert capture.frames[-1][0] > time.monotonic() - 0.3, "Local config overrode live native capability"
                assert not any(stamp >= marker + 0.2 for stamp, _, _ in capture.legacy), "Live native capability leaked legacy OSC"
                checks.append("bundled boolean true capability is parsed after avatar/change and overrides stale local false")

                frame.avatar_id = "avtr_native_enabled"
                frame.capability_override = False
                frame.tracking["/jawOpen"] = 0.39
                await_condition("bundled false capability restores legacy despite local true", lambda:
                                frame.has_value("v2/JawOpen", 0.39), frame, worker)
                pump(frame, worker, 0.2)
                stopped_count = len(capture.frames)
                pump(frame, worker, 1.2)
                assert len(capture.frames) == stopped_count, "Live false capability must override local fullFaceTracking=true"
                checks.append("bundled boolean false capability overrides local true and restores legacy OSC")

                marker = time.monotonic()
                frame.socket.sendto(osc_string("/avatar/avatarfullfacetracking") + osc_string(",T"), frame.avatar_peer)
                expected[36] = 0.39
                await_condition("standalone OSC true capability enables native mode", lambda:
                                any(stamp >= marker and matches(payload, expected) for stamp, payload in capture.frames), frame, worker)
                frame.socket.sendto(osc_string("/avatar/avatarfullfacetracking") + osc_string(",F"), frame.avatar_peer)
                frame.tracking["/jawOpen"] = 0.31
                await_condition("standalone OSC false capability restores legacy", lambda:
                                frame.has_value("v2/JawOpen", 0.31), frame, worker)
                pump(frame, worker, 0.2)
                stopped_count = len(capture.frames)
                pump(frame, worker, 0.4)
                assert len(capture.frames) == stopped_count, "Standalone false capability did not disable native output"
                checks.append("standalone OSC boolean capability messages toggle native mode without changing avatar")
            finally:
                worker.close()
                frame.close()
    finally:
        capture_socket.close()
    return {"result": "PASS", "module_version": manifest.get("Version"), "checks": checks}


def main():
    source_root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--module-zip", type=Path, required=True,
                        help="Path to your separately obtained Baballonia module ZIP (not included with the app)")
    args = parser.parse_args()
    print(json.dumps(run(args.worker.resolve(strict=True), args.module_zip.resolve(strict=True)), indent=2))


if __name__ == "__main__":
    main()

