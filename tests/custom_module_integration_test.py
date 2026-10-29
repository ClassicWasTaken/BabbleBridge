# Copyright (c) 2026 Babble Bridge contributors.
# SPDX-License-Identifier: Apache-2.0
"""Run the hardware-free SDK fixture on random loopback ports, with discovery off.

Build tests/Fixtures/BabbleBridge.CustomModule.Fixture and publish the Worker,
then pass --worker and --module-dll. No user profile or real device is touched.
"""
import argparse
import json
import math
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import threading
import time

from integration_test import Worker, read_osc, udp_socket


class CustomWorker(Worker):
    def __init__(self, executable, module, root, extra=(), module_name="Friendly Custom Eyes"):
        command = [str(executable), "--module", str(module), "--data", str(root),
                   "--kind", "Custom", "--module-name", module_name,
                   "--no-discovery", "--all-parameters", *map(str, extra)]
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, text=True, encoding="utf-8",
                                        errors="replace", env=dict(os.environ),
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.records, self.lines = [], []
        self.lock = threading.Lock()
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()

    def close(self):
        try:
            super().close()
        except OSError:
            if self.process.poll() is None:
                raise


class Fixture:
    def __init__(self, root, module_dll, dependency=True, mode=""):
        self.root = root
        self.module_dir = root / "module"
        self.module_dir.mkdir(parents=True)
        self.module = self.module_dir / module_dll.name
        shutil.copy2(module_dll, self.module)
        if dependency:
            shutil.copy2(module_dll.with_name("BabbleBridge.CustomModule.Dependency.dll"), self.module_dir)
        if mode:
            (self.module_dir / "fixture-mode.txt").write_text(mode, encoding="utf-8")
        self.capture = udp_socket()
        self.capture.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 20)
        reservation = udp_socket()
        receive_port = reservation.getsockname()[1]
        reservation.close()
        (root / "settings.json").write_text(json.dumps({
            "InputSource": "HeadsetRuntime", "OutputHost": "127.0.0.1",
            "OutputPort": self.capture.getsockname()[1], "ReceivePort": receive_port,
            "LinkPort": receive_port, "EyeEnabled": True, "FaceEnabled": True}), encoding="utf-8")
        self.values = {}

    def collect(self):
        while True:
            try:
                packet, _ = self.capture.recvfrom(65535)
            except BlockingIOError:
                return
            self.values.update(read_osc(packet))

    def close(self):
        self.capture.close()


def wait_for(worker, description, predicate, collect=lambda: None, fatal_expected=False, timeout=8):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        collect()
        if predicate():
            return
        if worker.any(lambda row: row.get("type") == "fatal") and not fatal_expected:
            raise AssertionError(description + "\n" + worker.diagnostics())
        time.sleep(.01)
    raise AssertionError("Timed out: " + description + "\n" + worker.diagnostics())


def run(worker_exe, module_dll):
    checks = []
    with tempfile.TemporaryDirectory(prefix="babble-custom-tests-") as temporary:
        base = Path(temporary)
        fixture = Fixture(base / "working", module_dll)
        sentinel = b'{"Port":1,"LinkEnabled":"leave this unrelated file alone"}'
        config = fixture.module_dir / "BabbleConfig.json"
        config.write_bytes(sentinel)
        worker = CustomWorker(worker_exe, fixture.module, fixture.root)
        try:
            wait_for(worker, "custom SDK module readiness", lambda: worker.any(lambda row:
                     row.get("type") == "ready" and row.get("source") == "Custom"
                     and row.get("inputPort") == 0 and row.get("eye") and row.get("face")), fixture.collect)
            wait_for(worker, "custom eye and mouth OSC", lambda: all(
                math.isclose(fixture.values.get("/avatar/parameters/" + name, -10), expected, abs_tol=.001)
                for name, expected in (("v2/EyeOpenLeft", .75), ("v2/EyeOpenRight", .25), ("v2/JawOpen", .6))), fixture.collect)
            wait_for(worker, "custom status and source-owned pupil policy", lambda: worker.any(lambda row:
                row.get("type") == "status" and row.get("source") == "Custom"
                and row.get("inputPort") == 0 and row.get("receivingKnown") is False
                and row.get("connected") is False and row.get("receiving") is False
                and math.isclose(row.get("pupilLeft", -1), .25, abs_tol=.001)
                and math.isclose(row.get("pupilRight", -1), .5, abs_tol=.001)
                and row.get("trackingRate", 0) >= 70), fixture.collect)
            assert config.read_bytes() == sentinel, "Custom hosting rewrote a Babble config file."
            assert not worker.any(lambda row: row.get("type") == "ready" and "Steam Link" in row.get("message", ""))
            assert worker.any(lambda row: row.get("type") == "ready" and "Friendly Custom Eyes" in row.get("message", ""))
            checks.append("Custom SDK DLL loads its managed dependency, maps eyes and mouth, preserves module-owned pupil policy and config, and processes about90Hz")
        finally:
            worker.close()
            fixture.close()
        assert (fixture.module_dir / "fixture-torn-down.txt").is_file(), "Custom module Teardown was not called."
        checks.append("Custom module stops and tears down cleanly")

        cases = (("missing-managed", False, "", "complete module ZIP"),
                 ("missing-native", True, "missing-native", "native module dependency"),
                 ("unsupported-sdk", True, "unsupported-sdk", "5.4.1 SDK"),
                 ("disabled", True, "no-tracking", "did not enable eye or face tracking"))
        for label, dependency, mode, expected in cases:
            fixture = Fixture(base / label, module_dll, dependency, mode)
            worker = CustomWorker(worker_exe, fixture.module, fixture.root)
            try:
                wait_for(worker, label + " actionable failure", lambda: worker.any(lambda row:
                         row.get("type") == "fatal" and expected in row.get("message", "")), fatal_expected=True)
                assert not worker.any(lambda row: row.get("type") == "ready"), worker.diagnostics()
                assert not (fixture.module_dir / "BabbleConfig.json").exists(), "Custom error path created a Babble config."
            finally:
                worker.close()
                fixture.close()
            checks.append(label + " reports an actionable error without readiness")
        fixture = Fixture(base / "port-override", module_dll)
        worker = CustomWorker(worker_exe, fixture.module, fixture.root, ("--input-port", "8888"))
        try:
            wait_for(worker, "custom input stays module-owned", lambda: worker.any(lambda row:
                     row.get("type") == "fatal" and "manages its tracking input automatically" in row.get("message", "")), fatal_expected=True)
        finally:
            worker.close()
            fixture.close()
        checks.append("Custom modules reject host-owned input-port overrides")
        fixture = Fixture(base / "option-like-name", module_dll)
        worker = CustomWorker(worker_exe, fixture.module, fixture.root, module_name="--input-port")
        try:
            wait_for(worker, "display name cannot become a runtime option", lambda: worker.any(lambda row:
                     row.get("type") == "ready" and row.get("inputPort") == 0
                     and row.get("message", "").startswith("--input-port initialized")), fixture.collect)
        finally:
            worker.close()
            fixture.close()
        checks.append("A display name resembling an option never controls runtime arguments")
    return {"result": "PASS", "checks": checks}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", required=True, type=Path)
    parser.add_argument("--module-dll", required=True, type=Path)
    arguments = parser.parse_args()
    print(json.dumps(run(arguments.worker.resolve(strict=True), arguments.module_dll.resolve(strict=True)), indent=2))
