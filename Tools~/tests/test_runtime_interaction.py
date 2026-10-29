import base64
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock
from http.server import BaseHTTPRequestHandler, HTTPServer
import threading

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import mcp_runtime as m
import runtime_interaction as i
import runtimebridge as b


class Clock:
    def __init__(self): self.now = 0
    def monotonic(self): return self.now
    def sleep(self, seconds): self.now += seconds


class Fixture:
    def __init__(self, values=None):
        self.values = iter(values or [0])
        self.calls = []
        self.session = "fixture-session"
    def command(self, action, params, **kwargs):
        self.calls.append((action, params, kwargs))
        data = {"targetId": "fixture", "buildId": "build", "serviceSessionId": self.session, "frameCount": 10}
        if action == "runtime.status": return {"Data": data}
        if action == "runtime.ui.find": return {"Data": {"summary": {"clickableCount": next(self.values)}}}
        if action == "runtime.member.get": return {"Data": {"value": False}}
        raise AssertionError(action)


class InteractionTests(unittest.TestCase):
    @staticmethod
    def call(client, name, arguments):
        return m.dispatch({"method": "tools/call", "params": {"name": name, "arguments": arguments}}, client)
    @staticmethod
    def condition(**changes):
        value = {"source": "ui", "params": {"keyword": "Start"}, "path": "summary.clickableCount", "operator": "gt", "expected": 0}
        value.update(changes)
        return value
    def test_wait_observes_transition_without_resending_mutations(self):
        clock, client = Clock(), Fixture([0, 0, 1])
        with mock.patch.object(i, "time", clock):
            reply = self.call(client, "runtime_wait", {"condition": self.condition(), "timeoutSeconds": 2})
        data = reply["structuredContent"]["data"]
        self.assertFalse(reply["isError"])
        self.assertEqual((data["attempts"], data["elapsedSeconds"], data["lastObservation"]["actual"]), (3, 0.5, 1))
        self.assertEqual({c[0] for c in client.calls}, {"runtime.status", "runtime.ui.find"})
        self.assertTrue(all(c[2]["deadline"] == 2 for c in client.calls))
    def test_timeout_returns_last_value_and_does_not_poll_past_deadline(self):
        clock, client = Clock(), Fixture([0, 0, 0])
        with mock.patch.object(i, "time", clock):
            reply = self.call(client, "runtime_wait", {"condition": self.condition(), "timeoutSeconds": 0.5})
        data = reply["structuredContent"]["data"]
        self.assertTrue(reply["isError"])
        self.assertEqual(data["code"], "WAIT_TIMEOUT")
        self.assertEqual(data["attempts"], 2)
        self.assertEqual(data["lastObservation"]["actual"], 0)
    def test_assert_keeps_false_zero_and_null_values(self):
        client = Fixture()
        condition = {"source": "member", "params": {"objectToken": "handle", "member": "Enabled"}, "path": "value", "operator": "eq", "expected": False}
        reply = self.call(client, "runtime_assert", {"condition": condition})
        self.assertFalse(reply["isError"])
        self.assertIs(reply["structuredContent"]["data"]["lastObservation"]["actual"], False)
        self.assertFalse(i.compare(False, "eq", 0))
        self.assertFalse(i.compare({"x": [False]}, "eq", {"x": [0]}))
        self.assertTrue(i.compare(0, "eq", 0.0))
        self.assertTrue(i.compare(None, "eq", None))
    def test_failed_assertion_is_tool_error(self):
        reply = self.call(Fixture([0]), "runtime_assert", {"condition": self.condition()})
        self.assertTrue(reply["isError"])
        self.assertEqual(reply["structuredContent"]["data"]["code"], "ASSERTION_FAILED")
    def test_missing_path_is_not_inequality_success(self):
        reply = self.call(Fixture([0]), "runtime_assert", {"condition": self.condition(path="missing", operator="ne", expected=1)})
        self.assertTrue(reply["isError"])
        self.assertEqual(reply["structuredContent"]["data"]["code"], "VALUE_NOT_FOUND")
    def test_session_change_rejects_otherwise_matching_result(self):
        client = Fixture([1])
        original = client.command
        def read(action, params, **kwargs):
            value = original(action, params, **kwargs)
            if action == "runtime.ui.find": client.session = "new-session"
            return value
        client.command = read
        reply = self.call(client, "runtime_assert", {"condition": self.condition()})
        self.assertEqual(reply["structuredContent"]["data"]["code"], "SESSION_CHANGED")
    def test_session_change_between_polls_rejects(self):
        clock, client = Clock(), Fixture([0, 1])
        def sleep(seconds):
            clock.now += seconds
            client.session = "new-session"
        clock.sleep = sleep
        with mock.patch.object(i, "time", clock):
            reply = self.call(client, "runtime_wait", {"condition": self.condition(), "timeoutSeconds": 1})
        self.assertEqual(reply["structuredContent"]["data"]["code"], "SESSION_CHANGED")
        self.assertEqual(reply["structuredContent"]["data"]["lastObservation"]["actual"], 0)
    def test_invalid_conditions_cannot_invoke_arbitrary_actions(self):
        client = mock.Mock()
        for condition in [self.condition(source="runtime.code.execute"), self.condition(params={"keyword": "x", "action": "runtime.ui.click"})]:
            try:
                reply = self.call(client, "runtime_assert", {"condition": condition})
                self.assertTrue(reply["isError"])
            except ValueError: pass
        client.command.assert_not_called()
    def test_invalid_input_stops_before_network(self):
        client = mock.Mock()
        cases = [("runtime_wait", {"condition": self.condition(), "timeoutSeconds": True}),
                 ("runtime_wait", {"condition": self.condition(), "intervalMs": 1}),
                 ("runtime_wait", {"condition": self.condition(), "timeoutSeconds": float("nan")}),
                 ("runtime_ui_click", {"riskAccepted": True, "x": 10}),
                 ("runtime_ui_click", {"riskAccepted": False, "path": "Canvas/Start"}),
                 ("runtime_observe", {"objectTokens": ["x"] * 17})]
        for name, arguments in cases:
            with self.assertRaises(ValueError): self.call(client, name, arguments)
        client.command.assert_not_called()
    def test_ui_click_failure_is_not_outer_protocol_success(self):
        client = mock.Mock()
        client.command.return_value = {"Success": True, "Data": {"success": False, "error": "Blocked"}}
        reply = self.call(client, "runtime_ui_click", {"path": "Canvas/Start", "riskAccepted": True})
        self.assertTrue(reply["isError"])
        self.assertEqual(reply["structuredContent"]["data"]["code"], "UI_ACTION_FAILED")
    def test_ui_routes_and_preserves_target(self):
        client = mock.Mock()
        client.command.return_value = {"Success": True, "Data": {"success": True}}
        self.call(client, "runtime_ui_raycast", {"x": 0, "y": 0})
        client.command.assert_called_once_with("runtime.ui.raycast", {"x": 0, "y": 0})
    def test_screenshot_returns_image_and_metadata(self):
        client = mock.Mock()
        metadata = {"filename": "shot.png", "sha256": "a" * 64, "fileSize": 9}
        client.command.return_value = {"Data": metadata}
        client.read_screenshot.return_value = b"png-fixture"
        reply = self.call(client, "runtime_screenshot", {})
        self.assertEqual(reply["content"][1]["type"], "image")
        self.assertEqual(base64.b64decode(reply["content"][1]["data"]), b"png-fixture")
        self.assertEqual(reply["structuredContent"]["data"]["Data"], metadata)
    def test_observe_without_image_does_not_download(self):
        client = mock.Mock()
        client.command.return_value = {"Data": {"capturedFrame": 42, "screenshot": None}}
        reply = self.call(client, "runtime_observe", {"includeScreenshot": False})
        self.assertFalse(reply["isError"])
        client.read_screenshot.assert_not_called()
    def test_image_failure_preserves_observation(self):
        client = mock.Mock()
        client.command.return_value = {"Data": {"screenshot": {"filename": "shot.png"}, "capturedFrame": 42}}
        client.read_screenshot.side_effect = b.BridgeError("not available")
        reply = self.call(client, "runtime_observe", {})
        self.assertTrue(reply["isError"])
        self.assertEqual(reply["structuredContent"]["data"]["observation"]["Data"]["capturedFrame"], 42)
    def test_schemas_cover_every_result_and_side_effect(self):
        tools = {t["name"]: t for t in m.tools_list()}
        self.assertEqual(len(tools), 24)
        for tool in tools.values(): self.assertIn("outputSchema", tool)
        for name in ("runtime_ui_click", "runtime_wait", "runtime_assert"):
            self.assertFalse(tools[name]["annotations"]["readOnlyHint"])
    def test_json_paths_and_type_errors(self):
        self.assertEqual(i.value_at({"items": [{"x": 0}]}, "items.0.x"), 0)
        self.assertIs(i.value_at([], "-1"), i.MISSING)
        self.assertTrue(i.compare(i.MISSING, "notExists", None))
        with self.assertRaises(i.InteractionError): i.compare(True, "gt", 0)
        with self.assertRaises(i.InteractionError): i.compare(1, "contains", "1")


class ScreenshotDownloadTests(unittest.TestCase):
    def setUp(self):
        self.png = b"\x89PNG\r\n\x1a\nfixture"
        self.redirect = False
        self.authorization = None
        fixture = self
        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                fixture.authorization = self.headers.get("Authorization")
                if fixture.redirect:
                    self.send_response(302)
                    self.send_header("Location", "http://127.0.0.1:1/leak")
                    self.end_headers()
                    return
                self.send_response(200)
                self.send_header("Content-Length", str(len(fixture.png)))
                self.end_headers()
                self.wfile.write(fixture.png)
            def log_message(self, *_): pass
        self.server = HTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.client = b.Client(f"http://127.0.0.1:{self.server.server_port}", "fixture-token")
        self.metadata = {"filename": "shot.png", "fileSize": len(self.png), "sha256": hashlib.sha256(self.png).hexdigest()}
    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
    def test_authenticated_download_and_hash(self):
        self.assertEqual(self.client.read_screenshot(self.metadata), self.png)
        self.assertEqual(self.authorization, "Bearer fixture-token")
    def test_path_traversal_and_oversize_are_rejected_before_request(self):
        for changes in [{"filename": "../shot.png"}, {"filename": "http://host/shot.png"}, {"fileSize": 20 * 1024 * 1024}]:
            with self.assertRaises(b.BridgeError): self.client.read_screenshot({**self.metadata, **changes})
        self.assertIsNone(self.authorization)
    def test_changed_image_rejected(self):
        self.png += b"changed"
        with self.assertRaises(b.BridgeError): self.client.read_screenshot(self.metadata)
    def test_redirect_is_not_followed(self):
        self.redirect = True
        with self.assertRaises(b.BridgeError): self.client.read_screenshot(self.metadata)


if __name__ == "__main__": unittest.main()
