# Copyright (c) 2026 Frame Bridge contributors.
# SPDX-License-Identifier: Apache-2.0

"""Exercise the real Baballonia DLL, BFR Link and VRCFT OSC mapping on loopback.

Build/publish the worker first, then run:
    python tests/integration_test.py --worker <path/to/BabbleFrameBridge.Worker.exe>

Only a temporary module copy, settings and VRChat avatar fixture are changed.
No headset, VRChat installation, administrator privileges or Python packages are needed.
"""

import argparse
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


def osc_string(value):
    value = value.encode("ascii") + b"\0"
    return value + b"\0" * (-len(value) % 4)


def osc_message(address, value):
    if isinstance(value, str):
        return osc_string(address) + osc_string(",s") + osc_string(value)
    return osc_string(address) + osc_string(",f") + struct.pack(">f", value)


def osc_bundle(values):
    packets = [osc_message(address, value) for address, value in values.items()]
    return b"#bundle\0" + struct.pack(">Q", 1) + b"".join(
        struct.pack(">I", len(packet)) + packet for packet in packets
    )


def read_osc(packet):
    """Return address/first-argument pairs from OSC messages or nested bundles."""
    if packet.startswith(b"#bundle\0"):
        offset = 16
        while offset + 4 <= len(packet):
            length = struct.unpack_from(">I", packet, offset)[0]
            offset += 4
            if length == 0 or offset + length > len(packet):
                break
            yield from read_osc(packet[offset:offset + length])
            offset += length
        return
    if not packet.startswith(b"/"):
        return
    end = packet.find(b"\0")
    if end < 0:
        return
    address = packet[:end].decode("ascii")
    offset = (end + 4) & ~3
    end = packet.find(b"\0", offset)
    if end < 0:
        return
    tags = packet[offset:end].decode("ascii")
    offset = (end + 4) & ~3
    if tags.startswith(",f") and offset + 4 <= len(packet):
        yield address, struct.unpack_from(">f", packet, offset)[0]
    elif tags.startswith(",i") and offset + 4 <= len(packet):
        yield address, struct.unpack_from(">i", packet, offset)[0]
    elif tags.startswith(",T"):
        yield address, True
    elif tags.startswith(",F"):
        yield address, False


def udp_socket():
    result = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    if hasattr(socket, "SIO_UDP_CONNRESET"):
        result.ioctl(socket.SIO_UDP_CONNRESET, False)
    result.bind(("127.0.0.1", 0))
    result.setblocking(False)
    return result


class Worker:
    def __init__(self, executable, module, root, environment):
        command = [str(executable), "--module", str(module), "--data", str(root), "--no-discovery"]
        self.process = subprocess.Popen(
            command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", env=environment,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        self.records = []
        self.lines = []
        self.lock = threading.Lock()
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()

    def read(self):
        for raw in self.process.stdout:
            line = raw.strip()
            with self.lock:
                self.lines.append(line)
                try:
                    record = json.loads(line)
                    if isinstance(record, dict):
                        self.records.append(record)
                except ValueError:
                    pass

    def any(self, predicate):
        with self.lock:
            return any(predicate(record) for record in self.records)

    def diagnostics(self):
        with self.lock:
            return "\n".join(self.lines[-18:])

    def close(self):
        if self.process.poll() is None:
            try:
                self.process.stdin.write("stop\n")
                self.process.stdin.flush()
                self.process.wait(timeout=8)
            except (BrokenPipeError, OSError, subprocess.TimeoutExpired):
                self.process.kill()
                self.process.wait(timeout=4)
        self.reader.join(timeout=2)
        self.process.stdin.close()
        self.process.stdout.close()


class Frame:
    def __init__(self, link_port, receive_port, capture):
        self.socket = udp_socket()
        self.peer = ("127.0.0.1", link_port)
        self.avatar_peer = ("127.0.0.1", receive_port)
        self.capture = capture
        self.eye = True
        self.face = True
        self.jaw = 0.65
        self.sequence = 0
        self.offers = self.pongs = self.module_rx = 0
        self.values = {}
        self.next_control = self.next_data = self.next_avatar = 0

    def tick(self):
        now = time.monotonic()
        base = {"proto": "bfr-link", "v": 1, "sid": "bridge-integration-test",
                "eye": self.eye, "face": self.face}
        if now >= self.next_control:
            self.sequence += 1
            for message in (
                dict(base, type="beacon", name="Simulated headset", version="test-v1"),
                dict(base, type="ping", seq=self.sequence, path="lan", active=True),
            ):
                self.socket.sendto(json.dumps(message).encode(), self.peer)
            self.next_control = now + 0.2
        if now >= self.next_avatar:
            self.socket.sendto(osc_message("/avatar/change", "avtr_bridge_integration"), self.avatar_peer)
            self.next_avatar = now + 0.4
        if now >= self.next_data:
            values = {"/jawOpen": self.jaw, "/mouthSmileLeft": 0.45}
            if self.eye:
                values.update({"/LeftEyeX": 0.3, "/RightEyeX": 0.25, "/LeftEyeY": -0.1,
                               "/RightEyeY": -0.1, "/LeftEyeLid": 0.8, "/RightEyeLid": 0.6})
            self.socket.sendto(osc_bundle(values), self.peer)
            self.next_data = now + 1 / 60
        while True:
            try:
                raw, _ = self.socket.recvfrom(4096)
            except ConnectionResetError:
                continue  # Windows ICMP response while the worker is starting/restarting.
            except BlockingIOError:
                break
            try:
                message = json.loads(raw)
            except (ValueError, UnicodeDecodeError):
                continue
            if message.get("type") == "offer":
                self.offers += 1
            if message.get("type") == "pong":
                self.pongs += 1
                self.module_rx = max(self.module_rx, message.get("rx", 0))
        while True:
            try:
                raw, _ = self.capture.recvfrom(65535)
            except BlockingIOError:
                break
            self.values.update(read_osc(raw))

    def has_value(self, suffix, expected):
        value = self.values.get("/avatar/parameters/" + suffix)
        return isinstance(value, float) and math.isclose(value, expected, abs_tol=0.0002)

    def close(self):
        self.socket.close()


def await_condition(description, predicate, frame, worker, seconds=12, allow_exit=False):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        frame.tick()
        if predicate():
            return
        if worker.process.poll() is not None and not allow_exit:
            raise AssertionError("Worker exited while waiting for " + description + "\n" + worker.diagnostics())
        if worker.any(lambda record: record.get("type") == "fatal"):
            raise AssertionError("Worker reported a fatal error\n" + worker.diagnostics())
        time.sleep(0.006)
    raise AssertionError("Timed out waiting for " + description + "\n" + worker.diagnostics()
                         + "\nCaptured OSC: " + repr(frame.values))


def run(executable, archive):
    capture = udp_socket()
    receive_reservation = udp_socket()
    link_reservation = udp_socket()
    legacy_reservation = udp_socket()
    output_port = capture.getsockname()[1]
    receive_port = receive_reservation.getsockname()[1]
    link_port = link_reservation.getsockname()[1]
    legacy_port = legacy_reservation.getsockname()[1]
    receive_reservation.close()
    link_reservation.close()
    legacy_reservation.close()
    checks = []
    try:
        with tempfile.TemporaryDirectory(prefix="babble-frame-integration-") as temporary:
            root = Path(temporary).resolve()
            module_dir = root / "module"
            module_dir.mkdir()
            with zipfile.ZipFile(archive) as package:
                package.extractall(module_dir)
            manifest = json.loads((module_dir / "module.json").read_text(encoding="utf-8-sig"))
            module = module_dir / manifest["DllFileName"]
            config_path = module_dir / "BabbleConfig.json"
            config = json.loads(config_path.read_text(encoding="utf-8-sig"))
            config.update(IsEyeSupported=True, IsFaceSupported=True, LinkEnabled=True,
                          LinkPort=link_port, Port=legacy_port)
            config_path.write_text(json.dumps(config), encoding="utf-8")
            settings = {"OutputHost": "127.0.0.1", "OutputPort": output_port, "ReceivePort": receive_port,
                        "LinkPort": link_port, "EyeEnabled": True, "FaceEnabled": True, "AutoStart": False}
            (root / "settings.json").write_text(json.dumps(settings), encoding="utf-8")
            avatar_folder = root / "LocalLow" / "VRChat" / "VRChat" / "OSC" / "usr_bridge_test" / "Avatars"
            avatar_folder.mkdir(parents=True)
            names = ("v2/JawOpen", "v2/EyeLeftX", "v2/EyeOpenLeft", "v2/EyeOpenRight")
            fixture = {"id": "avtr_bridge_integration", "name": "Integration Test Avatar", "parameters": [
                {"name": name, "input": {"address": "/avatar/parameters/" + name, "type": "Float"}}
                for name in names
            ]}
            (avatar_folder / "avtr_bridge_integration.json").write_text(json.dumps(fixture), encoding="utf-8")
            environment = dict(os.environ, LOCALAPPDATA=str(root / "Local"))
            frame = Frame(link_port, receive_port, capture)
            worker = Worker(executable, module, root, environment)
            try:
                await_condition("module initialization", lambda: worker.any(
                    lambda item: item.get("type") == "ready" and item.get("eye") and item.get("face")), frame, worker)
                checks.append("original Baballonia DLL initialized with eye and face claims")
                await_condition("headset discovery, heartbeat and incoming OSC", lambda: frame.offers > 0
                                and frame.pongs > 0 and frame.module_rx > 0 and worker.any(
                                    lambda item: item.get("type") == "status" and item.get("connected")
                                    and item.get("receiving") and item.get("packets", 0) > 0), frame, worker)
                checks.append("BFR Link discovery, heartbeat, status and OSC reception")
                await_condition("VRChat avatar selection and mapped eye/jaw OSC", lambda:
                                frame.has_value("v2/JawOpen", 0.65)
                                and frame.has_value("v2/EyeLeftX", 0.3)
                                and frame.has_value("v2/EyeOpenLeft", 0.8)
                                and frame.has_value("v2/EyeOpenRight", 0.6), frame, worker)
                checks.append("VRCFT avatar mapping sends exact jaw, gaze and eyelid floats")
                frame.eye = False
                await_condition("headset mode change restart signal", lambda: worker.any(
                    lambda item: item.get("type") == "restart"), frame, worker, allow_exit=True)
                worker.process.wait(timeout=8)
                assert worker.process.returncode == 0, worker.diagnostics()
                saved = json.loads(config_path.read_text(encoding="utf-8-sig"))
                assert saved["IsEyeSupported"] is False and saved["IsFaceSupported"] is True, saved
                checks.append("headset mode change persists config and requests clean worker restart")
            finally:
                worker.close()
            frame.values.clear()
            frame.jaw = 0.42
            worker = Worker(executable, module, root, environment)
            try:
                await_condition("saved module claims after relaunch", lambda: worker.any(
                    lambda item: item.get("type") == "ready" and item.get("eye") is False
                    and item.get("face") is True), frame, worker)
                await_condition("mapped jaw OSC after relaunch", lambda: frame.has_value("v2/JawOpen", 0.42), frame, worker)
                assert not worker.any(lambda item: item.get("type") == "restart"), worker.diagnostics()
                checks.append("relaunch honors negotiated face-only config and resumes mapped OSC")
            finally:
                worker.close()
                frame.close()
    finally:
        capture.close()
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
