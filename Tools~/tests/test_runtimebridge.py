import base64
import contextlib
import hashlib
from http.server import BaseHTTPRequestHandler, HTTPServer
import importlib.util
import io
import json
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import threading
import unittest
from unittest import mock

TOOLS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("runtimebridge", TOOLS / "runtimebridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class ClientTests(unittest.TestCase):
    def test_directives_do_not_remove_using_inside_strings(self):
        source = '// leading comment\nusing System;\nvar text = "Refusing to overwrite"; return text;'
        wrapped = bridge.wrap_snippet(source)
        self.assertIn('var text = "Refusing to overwrite";', wrapped)
        self.assertLess(wrapped.index("using System;"), wrapped.index("class CodeExecutor"))
        self.assertIn("using var resource = Acquire();", bridge.wrap_snippet("using var resource = Acquire(); return 1;"))

    def test_code_requires_explicit_player_profile(self):
        with self.assertRaisesRegex(bridge.BridgeError, "--profile is required"):
            bridge.compile_snippet("return 42;", None)

    def test_editor_profile_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            profile = Path(temporary) / "profile.json"
            profile.write_text(json.dumps({"target": "WindowsEditor", "buildId": "fixture", "references": []}))
            with self.assertRaisesRegex(bridge.BridgeError, "Player target profile"):
                bridge.target_references(profile)

    def test_reference_fingerprint_missing_and_changed_bytes_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            reference = root / "Reference.dll"
            original = b"reference fixture bytes"
            reference.write_bytes(original)
            profile = root / "profile.json"
            data = {"target": "Android", "buildId": "fixture-build", "references": [reference.name], "defineSymbols": []}
            profile.write_text(json.dumps(data))
            with self.assertRaisesRegex(bridge.BridgeError, "requires assemblyFingerprints"):
                bridge.target_references(profile)
            data["assemblyFingerprints"] = []
            profile.write_text(json.dumps(data))
            with self.assertRaisesRegex(bridge.BridgeError, "one-to-one coverage"):
                bridge.target_references(profile)
            data["assemblyFingerprints"] = [{"name": reference.stem, "sha256": hashlib.sha256(original).hexdigest()}]
            profile.write_text(json.dumps(data))
            bridge.target_references(profile)
            reference.write_bytes(b"changed after export")
            with self.assertRaisesRegex(bridge.BridgeError, "SHA256 mismatch"):
                bridge.target_references(profile)

    def test_pending_http_result_and_authorization(self):
        observed = {}

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                observed["authorization"] = self.headers.get("Authorization")
                payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                observed["payload"] = payload
                self.respond(202, {"completed": False, "resultUrl": "/aibridge/results/" + payload["Id"]})

            def do_GET(self):
                self.respond(200, {"CommandId": observed["payload"]["Id"], "Success": True, "Completed": True, "Data": {"returnValue": 42}})

            def respond(self, code, payload):
                body = json.dumps(payload).encode()
                self.send_response(code)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *_):
                pass

        server = HTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            dll = b"trusted fixture DLL"
            result = bridge.Client(f"http://127.0.0.1:{server.server_port}", "fixture-token").command("runtime.code.execute", {"assemblyBase64": base64.b64encode(dll).decode(), "sha256": hashlib.sha256(dll).hexdigest(), "riskAccepted": True})
            self.assertEqual(result["Data"]["returnValue"], 42)
            self.assertEqual(observed["authorization"], "Bearer fixture-token")
            self.assertNotIn("Token", observed["payload"])
            sent = observed["payload"]["Params"]
            self.assertEqual(hashlib.sha256(base64.b64decode(sent["assemblyBase64"])).hexdigest(), sent["sha256"])
        finally:
            server.shutdown()
            server.server_close()

    def test_failure_json_redacts_token(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            result = bridge.main(["exec", "--code", "return 42;", "--token", "fixture-secret"])
        self.assertEqual(result, 1)
        self.assertNotIn("fixture-secret", output.getvalue())
        self.assertFalse(json.loads(output.getvalue())["success"])

    def test_mismatched_connected_player_build_rejected(self):
        class FakeClient:
            def command(self, action, parameters):
                self.last_action = action
                return {"Data": {"buildId": "actual-player", "platform": "Android"}}
        client = FakeClient()
        with self.assertRaisesRegex(bridge.BridgeError, "buildId does not match"):
            bridge.validate_profile_target(client, {"buildId": "wrong-player", "target": "Android"})
        self.assertEqual(client.last_action, "runtime.status")

    def test_matching_player_and_guid_format_accepted(self):
        class FakeClient:
            def command(self, action, parameters):
                return {"Data": {"buildId": "ab12-cd34", "platform": "WindowsPlayer"}}
        bridge.validate_profile_target(FakeClient(), {"buildId": "AB12CD34", "target": "StandaloneWindows64"})


class DiscoveryTests(unittest.TestCase):
    def test_adb_listing_preserves_authorization_and_connection_states(self):
        devices = bridge.parse_adb_devices("* daemon started successfully\nList of devices attached\nusb-1 device product:sample model:Phone device:sample transport_id:1\nusb-2 unauthorized usb:2-1\n127.0.0.1:7555 offline\nusb-3 no permissions (missing rules)\n")
        self.assertEqual([device["state"] for device in devices], ["device", "unauthorized", "offline", "no permissions"])
        self.assertEqual(devices[0], {"serial": "usb-1", "state": "device", "details": "product:sample model:Phone device:sample transport_id:1"})
        self.assertEqual(devices[3]["details"], "(missing rules)")
        self.assertEqual(bridge.parse_adb_devices("List of devices attached\n\n"), [])

    def test_adb_error_and_command_failure_are_not_empty_success(self):
        for output in ("adb: failed to connect", "List of devices attached\nerror: server error", "unexpected output"):
            with self.assertRaises(bridge.BridgeError):
                bridge.parse_adb_devices(output)
        output = io.StringIO()
        with mock.patch.object(bridge, "run_process", side_effect=bridge.BridgeError("adb failed")), contextlib.redirect_stdout(output):
            result = bridge.main(["devices", "--adb", "C:/custom path/adb.exe"])
        self.assertEqual(result, 1)
        self.assertFalse(json.loads(output.getvalue())["success"])

    def test_devices_command_invokes_adb_without_shell_or_interpolated_arguments(self):
        output = io.StringIO()
        with mock.patch.object(bridge, "run_process", return_value="List of devices attached\nserial unauthorized") as run, contextlib.redirect_stdout(output):
            result = bridge.main(["devices", "--adb", "C:/custom path/adb.exe"])
        self.assertEqual(result, 0)
        run.assert_called_once_with(["C:/custom path/adb.exe", "devices", "-l"], timeout=15)
        self.assertEqual(json.loads(output.getvalue())["devices"][0]["state"], "unauthorized")

    @staticmethod
    def response(request_id="nonce", **overrides):
        response = {
            "protocol": "aibridge-runtime-discovery", "version": 1,
            "requestId": request_id, "targetId": "fixture-target", "transport": "http",
            "reachableUrl": "http://untrusted-advertised-host:27182", "projectName": "Fixture",
            "requiresToken": True, "authToken": "must-not-be-returned",
            "capabilities": {"runtimeCodeExecute": True, "authToken": "must-not-be-returned"},
        }
        response.update(overrides)
        return json.dumps(response).encode()

    def test_udp_protocol_nonce_and_source_validation(self):
        source = ("192.168.1.25", 27183)
        result = bridge.parse_discovery_response(self.response(), source, "nonce", 27183)
        self.assertEqual(result["reachableUrl"], "http://192.168.1.25:27182")
        self.assertEqual(result["capabilities"], {"runtimeCodeExecute": True})
        self.assertNotIn("must-not-be-returned", json.dumps(result))
        self.assertIsNone(bridge.parse_discovery_response(self.response("wrong-nonce"), source, "nonce", 27183))
        self.assertIsNone(bridge.parse_discovery_response(self.response(protocol="wrong-protocol"), source, "nonce", 27183))
        self.assertIsNone(bridge.parse_discovery_response(self.response(version=2), source, "nonce", 27183))
        self.assertIsNone(bridge.parse_discovery_response(self.response(), (source[0], 27184), "nonce", 27183))
        self.assertIsNone(bridge.parse_discovery_response(self.response(), source, "nonce", 27183, loopback_only=True))
        self.assertIsNone(bridge.parse_discovery_response(self.response(reachableUrl="http://secret@host:27182"), source, "nonce", 27183))

    def test_udp_scan_correlates_request_and_deduplicates_replies(self):
        server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        server.bind(("127.0.0.1", 0))
        server.settimeout(2)
        port = server.getsockname()[1]
        observed = {}

        def respond():
            data, remote = server.recvfrom(65535)
            request = json.loads(data)
            observed.update(request)
            server.sendto(self.response("unrelated-nonce"), remote)
            valid = self.response(request["requestId"])
            server.sendto(valid, remote)
            server.sendto(valid, remote)

        thread = threading.Thread(target=respond, daemon=True)
        thread.start()
        try:
            targets = bridge.discover_targets(port, timeout=0.25, broadcast="127.0.0.1")
            self.assertEqual(observed["protocol"], "aibridge-runtime-discovery")
            self.assertEqual(len(observed["requestId"]), 32)
            self.assertNotIn("token", observed)
            self.assertEqual(len(targets), 1)
            self.assertEqual(targets[0]["reachableUrl"], "http://127.0.0.1:27182")
        finally:
            server.close()
            thread.join(timeout=2)

    def test_discovery_timeout_and_destination_are_bounded(self):
        for timeout in (0, -1, 61, float("nan"), float("inf")):
            with self.assertRaises(bridge.BridgeError):
                bridge.discover_targets(timeout=timeout)
        with self.assertRaises(bridge.BridgeError):
            bridge.discover_targets(broadcast="not-an-ip")


@unittest.skipUnless(shutil.which("dotnet"), "Local .NET SDK required")
class MetadataTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="runtimebridge-fixtures-")
        cls.root = Path(cls.temporary.name)
        cls.baseline = cls.build("baseline", "public class Sample { public int Value; public int Damage(int amount) => amount + 10; }")
        cls.body = cls.build("body", "public class Sample { public int Value; public int Damage(int amount) => amount + 20; }")
        cls.field = cls.build("field", "public class Sample { public int Value; public int NewField; public int Damage(int amount) => amount + 20; }")
        cls.closure = cls.build("closure", "public class Sample { public int Value; public int Damage(int amount) { System.Func<int> action = () => amount + 20; return action(); } }")
        cls.type_order_baseline = cls.build("type-order-baseline", "public class First { public int One() => 1; } public class Second { public int Two() => 2; }")
        cls.type_order_candidate = cls.build("type-order-candidate", "public class Second { public int Two() => 2; } public class First { public int One() => 1; }")
        cls.method_order_baseline = cls.build("method-order-baseline", "public class Sample { public int One() => 1; public int Two() => 2; }")
        cls.method_order_candidate = cls.build("method-order-candidate", "public class Sample { public int Two() => 2; public int One() => 1; }")

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    @classmethod
    def build(cls, name, source):
        directory = cls.root / name
        directory.mkdir()
        (directory / "Fixture.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>PatchFixture</AssemblyName><GenerateAssemblyInfo>false</GenerateAssemblyInfo><Deterministic>true</Deterministic></PropertyGroup></Project>')
        (directory / "Sample.cs").write_text(source)
        subprocess.run(["dotnet", "build", str(directory / "Fixture.csproj"), "--nologo", "--verbosity", "quiet"], check=True, capture_output=True)
        return directory / "bin/Debug/net8.0/PatchFixture.dll"

    def test_body_only_change_allowed_and_mvid_derived(self):
        result = bridge.metadata_tool("compare", self.baseline, self.body)
        self.assertTrue(result["compatible"], result)
        self.assertNotEqual(result["baselineMvid"], result["candidateMvid"])
        self.assertEqual(result["assemblyName"], "PatchFixture")

    def test_added_field_rejected(self):
        result = bridge.metadata_tool("compare", self.baseline, self.field)
        self.assertFalse(result["compatible"])
        self.assertIn("Changed type/member definition: Sample", result["differences"])

    def test_added_compiler_generated_closure_type_rejected(self):
        result = bridge.metadata_tool("compare", self.baseline, self.closure)
        self.assertFalse(result["compatible"])
        self.assertTrue(any(message.startswith("Added type: Sample+") for message in result["differences"]), result)

    def test_existing_type_reorder_rejected_for_global_metadata_token_shift(self):
        result = bridge.metadata_tool("compare", self.type_order_baseline, self.type_order_candidate)
        self.assertFalse(result["compatible"], result)
        self.assertIn("Changed type/member definition: First", result["differences"])
        self.assertIn("Changed type/member definition: Second", result["differences"])

    def test_existing_method_reorder_rejected(self):
        result = bridge.metadata_tool("compare", self.method_order_baseline, self.method_order_candidate)
        self.assertFalse(result["compatible"], result)
        self.assertIn("Changed type/member definition: Sample", result["differences"])

    def test_patch_validated_before_network_and_encryption_binding_required(self):
        manifest = self.root / "patch.json"
        manifest.write_text(json.dumps({"baselineDll": str(self.baseline), "types": [{"name": "Sample", "methods": [{"signature": "Int32 Damage(Int32)"}]}]}))
        parameters = bridge.patch_parameters(self.body, manifest)
        self.assertTrue(parameters["structureVerified"])
        self.assertTrue(parameters["riskAccepted"])
        self.assertTrue(parameters["baselineMvid"])
        with self.assertRaisesRegex(bridge.BridgeError, "Encrypted patches require"):
            bridge.patch_parameters(self.body, manifest, validation_dll=self.body)

    def test_snippet_compiles_against_explicit_profile(self):
        dotnet = bridge.dotnet_path()
        runtime_root = sorted((Path(dotnet).parent / "packs/Microsoft.NETCore.App.Ref").glob("8.*/ref/net8.0"))[-1]
        profile = self.root / "profile.json"
        references = list(runtime_root.glob("*.dll"))
        profile.write_text(json.dumps({"target": "FixturePlayer", "buildId": "unit-test-only", "referenceDirectory": str(runtime_root), "references": [path.name for path in references], "assemblyFingerprints": [{"name": path.stem, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in references], "defineSymbols": []}))
        data = bridge.compile_snippet('using System; var text = "Refusing to fail"; return text;', profile)
        self.assertTrue(data.startswith(b"MZ"))
        repeated = bridge.compile_snippet('using System; var text = "Refusing to fail"; return text;', profile)
        self.assertEqual(data, repeated, "identical probes must reuse the Player assembly cache")


if __name__ == "__main__":
    unittest.main()
