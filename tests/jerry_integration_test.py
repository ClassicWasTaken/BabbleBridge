# Copyright (c) 2026 Frame Bridge contributors.
# SPDX-License-Identifier: Apache-2.0

"""Exercise Jerry-style upper and lower face OSC through a real imported module.

Uses only a temporary module/profile/avatar copy and random loopback ports. It
does not discover or send to a running VRChat session. No headset is required.
The fixture uses the raw FT/v2 parameters and status booleans exposed by Jerry's
ARKit/Unified Expressions prefabs, including their remote binary parameters.

    python tests/jerry_integration_test.py --worker <worker.exe> --module-zip <module.zip>
"""

import argparse
import json
import math
import os
from pathlib import Path
import tempfile
import time
import zipfile

from integration_test import Worker, await_condition, osc_bundle, osc_message, read_osc, udp_socket


FLOATS = (
    "FT/v2/EyeLeftX", "FT/v2/EyeRightX", "FT/v2/EyeY",
    "FT/v2/EyeLidLeft", "FT/v2/EyeLidRight",
    "FT/v2/EyeSquintLeft", "FT/v2/EyeSquintRight",
    "FT/v2/BrowExpressionLeft", "FT/v2/BrowExpressionRight",
    "FT/v2/PupilDilation", "FT/v2/JawOpen",
)
BINARIES = (
    "FT/v2/EyeSquintLeft", "FT/v2/EyeSquintRight",
    "FT/v2/BrowExpressionLeft", "FT/v2/BrowExpressionRight",
    "FT/v2/PupilDilation",
)


class JerryInput:
    def __init__(self, source, legacy_port, link_port, receive_port, capture):
        self.source = source
        self.socket = udp_socket()
        self.peer = ("127.0.0.1", link_port if source == "frame" else legacy_port)
        self.avatar_peer = ("127.0.0.1", receive_port)
        self.capture = capture
        self.values = {}
        self.eye_fallback = []
        self.native_packets = 0
        self.next_data = self.next_control = self.next_avatar = 0
        self.tracking = {
            "/LeftEyeX": 0.3, "/RightEyeX": 0.25,
            "/LeftEyeY": -0.1, "/RightEyeY": -0.1,
            "/LeftEyeLid": 0.8, "/RightEyeLid": 0.6,
            "/LeftEyeWiden": 0.65, "/RightEyeWiden": 0.35,
            "/LeftEyeSquint": 0.35, "/RightEyeSquint": 0.65,
            "/LeftEyeBrow": 0.4, "/RightEyeBrow": 0.8,
            "/LeftEyeDilation": 0.8, "/RightEyeDilation": 0.2,
            "/jawOpen": 0.65,
        }

    def tick(self):
        now = time.monotonic()
        if now >= self.next_avatar:
            self.socket.sendto(osc_message("/avatar/change", "avtr_jerry_upper_test"), self.avatar_peer)
            self.next_avatar = now + 0.8
        if self.source == "frame" and now >= self.next_control:
            common = {"proto": "bfr-link", "v": 1, "sid": "jerry-upper-test", "eye": True, "face": True}
            for message in (
                dict(common, type="beacon", name="Simulated Steam Frame", version="test"),
                dict(common, type="ping", seq=1, path="lan", active=True),
            ):
                self.socket.sendto(json.dumps(message).encode(), self.peer)
            self.next_control = now + 0.2
        if now >= self.next_data:
            if self.source == "frame":
                self.socket.sendto(osc_bundle(self.tracking), self.peer)
            else:
                # The desktop Baballonia receiver accepts individual OSC floats.
                for address, value in self.tracking.items():
                    self.socket.sendto(osc_message(address, value), self.peer)
            self.next_data = now + 1 / 60
        while True:
            try:
                self.socket.recvfrom(4096)
            except ConnectionResetError:
                continue
            except BlockingIOError:
                break
        while True:
            try:
                packet, _ = self.capture.recvfrom(65535)
            except BlockingIOError:
                break
            if packet.startswith(b"/tracking/face/v1\0"):
                self.native_packets += 1
            for address, value in read_osc(packet):
                self.values[address] = value
                if address.startswith("/tracking/eye/"):
                    self.eye_fallback.append(address)

    def has(self, name, expected):
        actual = self.values.get("/avatar/parameters/" + name)
        if isinstance(expected, bool):
            return actual is expected
        return isinstance(actual, float) and math.isclose(actual, expected, abs_tol=0.0003)

    def has_all(self, expected):
        return all(self.has(name, value) for name, value in expected.items())

    def binary(self, name, expected, negative=None):
        return all(self.has(name + str(bit), bool(expected & bit)) for bit in (1, 2, 4)) and (
            negative is None or self.has(name + "Negative", negative))

    def close(self):
        self.socket.close()


def run_case(executable, archive, source):
    capture = udp_socket()
    reservations = [udp_socket() for _ in range(3)]
    receive_port, link_port, legacy_port = [item.getsockname()[1] for item in reservations]
    for item in reservations:
        item.close()
    checks = []
    try:
        with tempfile.TemporaryDirectory(prefix="bridge-jerry-" + source + "-") as temporary:
            root = Path(temporary).resolve()
            module_dir = root / "module"
            module_dir.mkdir()
            with zipfile.ZipFile(archive) as package:
                package.extractall(module_dir)
            manifest = json.loads((module_dir / "module.json").read_text(encoding="utf-8-sig"))
            module = module_dir / manifest["DllFileName"]
            config_path = module_dir / "BabbleConfig.json"
            config = json.loads(config_path.read_text(encoding="utf-8-sig"))
            # The supplied v4 ZIP can contain a face-only default. Desktop mode
            # must claim eyes as well, without relying on a headset handshake.
            config.update(Host="127.0.0.1", IsEyeSupported=source != "desktop", IsFaceSupported=True,
                          LinkEnabled=True, LinkPort=link_port, Port=legacy_port)
            config_path.write_text(json.dumps(config), encoding="utf-8")
            (root / "settings.json").write_text(json.dumps({
                "OutputHost": "127.0.0.1", "OutputPort": capture.getsockname()[1],
                "ReceivePort": receive_port, "LinkPort": link_port, "AutoStart": False,
                "InputSource": "BaballoniaDesktop" if source == "desktop" else "SteamFrameRuntime",
            }), encoding="utf-8")
            avatar_folder = root / "LocalLow" / "VRChat" / "VRChat" / "OSC" / "usr_jerry_test" / "Avatars"
            avatar_folder.mkdir(parents=True)
            names = [(name, "Float") for name in FLOATS]
            names += [(name, "Bool") for name in ("EyeTrackingActive", "LipTrackingActive")]
            names += [(name + str(bit), "Bool") for name in BINARIES for bit in (1, 2, 4)]
            names += [("FT/v2/BrowExpression" + side + "Negative", "Bool") for side in ("Left", "Right")]
            fixture = {
                "id": "avtr_jerry_upper_test", "name": "Jerry upper-face integration", "fullFaceTracking": False,
                "parameters": [{"name": name, "input": {
                    "address": "/avatar/parameters/" + name, "type": kind}} for name, kind in names],
            }
            (avatar_folder / "avtr_jerry_upper_test.json").write_text(json.dumps(fixture), encoding="utf-8")
            frame = JerryInput(source, legacy_port, link_port, receive_port, capture)
            worker = Worker(executable, module, root, dict(os.environ, LOCALAPPDATA=str(root / "Local")))
            try:
                await_condition("module initialization", lambda: worker.any(
                    lambda item: item.get("type") == "ready"), frame, worker)
                assert worker.any(lambda item: item.get("type") == "ready" and item.get("eye") and item.get("face")), (
                    source + " input must claim both eye and mouth tracking\n" + worker.diagnostics())
                if source == "desktop":
                    saved = json.loads(config_path.read_text(encoding="utf-8-sig"))
                    assert saved["IsEyeSupported"] is True and saved["IsFaceSupported"] is True, saved
                    assert saved["LinkEnabled"] is False, "Desktop source must not depend on BFR Link"
                    checks.append("desktop: source selection overrides the module's eye-disabled default and disables BFR Link")
                await_condition("Jerry status booleans and upper/lower raw OSC", lambda: frame.has_all({
                    "EyeTrackingActive": True, "LipTrackingActive": True,
                    "FT/v2/EyeLeftX": 0.3, "FT/v2/EyeRightX": 0.25, "FT/v2/EyeY": -0.1,
                    "FT/v2/EyeLidLeft": 0.7625, "FT/v2/EyeLidRight": 0.5375,
                    "FT/v2/EyeSquintLeft": 0.35, "FT/v2/EyeSquintRight": 0.65,
                    "FT/v2/BrowExpressionLeft": -0.3, "FT/v2/BrowExpressionRight": -0.6,
                    "FT/v2/PupilDilation": 0.5, "FT/v2/JawOpen": 0.65,
                }), frame, worker)
                checks.append(source + ": Jerry eye/mouth enable flags, prefixed gaze, lids/widen, squint, brow and jaw output together")
                # Native eye fallback is valid before an avatar has been discovered.
                # Once these Jerry keys are bound, only their custom eye output may run.
                frame.eye_fallback.clear()
                await_condition("Jerry remote binary parameters", lambda:
                                frame.binary("FT/v2/EyeSquintLeft", 2)
                                and frame.binary("FT/v2/EyeSquintRight", 5)
                                and frame.binary("FT/v2/BrowExpressionLeft", 2, True)
                                and frame.binary("FT/v2/BrowExpressionRight", 4, True)
                                and frame.binary("FT/v2/PupilDilation", 4), frame, worker)
                checks.append(source + ": binary squint, signed brow and pupil parameters encode correctly")
                assert not frame.eye_fallback, "Jerry eye keys must suppress conflicting native-eye endpoints: " + repr(frame.eye_fallback)
                assert not frame.native_packets, "Legacy Jerry avatar must not select native full-face OSC"
                frame.tracking.update({
                    "/LeftEyeX": -0.35, "/RightEyeX": 0.4,
                    "/LeftEyeY": 0.2, "/RightEyeY": 0.2,
                    "/LeftEyeLid": 0.0, "/RightEyeLid": 1.0,
                    "/LeftEyeWiden": 0.2, "/RightEyeWiden": 0.8,
                    "/LeftEyeDilation": 1.0, "/RightEyeDilation": 1.0,
                    "/jawOpen": 0.37,
                })
                await_condition("independent blink and changing gaze/widen/pupil/jaw", lambda: frame.has_all({
                    "FT/v2/EyeLeftX": -0.35, "FT/v2/EyeRightX": 0.4, "FT/v2/EyeY": 0.2,
                    "FT/v2/EyeLidLeft": 0.05, "FT/v2/EyeLidRight": 0.95,
                    "FT/v2/PupilDilation": 1.0, "FT/v2/JawOpen": 0.37,
                }), frame, worker)
                frame.tracking.update({"/LeftEyeDilation": 0.0, "/RightEyeDilation": 0.0})
                await_condition("pupil constriction and zero-valued binary clearing", lambda:
                                frame.has("FT/v2/PupilDilation", 0.0)
                                and frame.binary("FT/v2/PupilDilation", 0), frame, worker)
                checks.append(source + ": independent blink, gaze/widen changes and pupil dilation/constriction preserve mouth output")
                assert not frame.eye_fallback and not frame.native_packets, "Jerry output routing changed unexpectedly"
            finally:
                worker.close()
                frame.close()
    finally:
        capture.close()
    return checks


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--module-zip", type=Path, required=True)
    args = parser.parse_args()
    executable, archive = args.worker.resolve(strict=True), args.module_zip.resolve(strict=True)
    checks = run_case(executable, archive, "desktop") + run_case(executable, archive, "frame")
    print(json.dumps({"result": "PASS", "checks": checks}, indent=2))


if __name__ == "__main__":
    main()
