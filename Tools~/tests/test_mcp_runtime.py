import json
import pathlib
import sys
import unittest
from unittest import mock
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import mcp_runtime as m
import runtimebridge as b

class MCPTests(unittest.TestCase):
    def setUp(self):
        self.client = mock.Mock()
        self.client.command.return_value = {"Success": True, "Data": {"ok": True}}
    def call(self, name, arguments):
        return m.dispatch({"method":"tools/call","params":{"name":name,"arguments":arguments}},self.client)
    def test_protocol_negotiates_and_discovers_without_connecting(self):
        reply=m.dispatch({"method":"initialize","params":{"protocolVersion":"2025-03-26"}},self.client)
        self.assertEqual(reply["protocolVersion"],"2025-03-26")
        tools=m.dispatch({"method":"tools/list"},self.client)["tools"]
        self.assertGreaterEqual(len(tools),16)
        self.client.command.assert_not_called()
    def test_mutation_tools_are_not_advertised_read_only(self):
        for tool in m.tools_list():
            if tool["name"] in {"runtime_execute","runtime_invoke","runtime_get_member","runtime_set_member","runtime_patch_source"}:
                self.assertFalse(tool["annotations"]["readOnlyHint"])
                self.assertTrue(tool["annotations"]["destructiveHint"])
    def test_inspection_routes_without_inventing_parameters(self):
        result=self.call("runtime_inspect",{"objectToken":"handle"})
        self.client.command.assert_called_once_with("runtime.inspect",{"objectToken":"handle"})
        self.assertFalse(result["isError"])
    def test_missing_or_unknown_arguments_stop_before_network(self):
        for name,args in [("runtime_set_member",{"objectToken":"x"}),("runtime_inspect",{"objectToken":"x","action":"runtime.method.invoke"})]:
            with self.assertRaises(ValueError): self.call(name,args)
        self.client.command.assert_not_called()
    def test_failure_is_an_mcp_tool_error(self):
        self.client.command.side_effect=b.BridgeError("destroyed object")
        self.assertTrue(self.call("runtime_inspect",{"objectToken":"x"})["isError"])
    def test_stopping_capture_preserves_release_choice(self):
        self.call("runtime_profiler_stop",{"sessionId":"x","release":False})
        self.client.command.assert_called_once_with("runtime.profiler.stop",{"sessionId":"x","release":False})
    def test_diagnosis_reports_missing_profile_without_losing_live_capabilities(self):
        def reply(action, arguments):
            return {"Data": {"runtime.status": {"buildId": "fixture"}, "runtime.assemblies": {"items": []}, "runtime.patch.capabilities": {"available": False}}[action]}
        self.client.command.side_effect = reply
        with mock.patch.object(m, "profile_path", side_effect=b.BridgeError("missing local profile")):
            result = self.call("runtime_diagnose", {})
        data = json.loads(result["content"][0]["text"])
        self.assertEqual(data["localProfileError"], "missing local profile")
        self.assertFalse(data["patchCapabilities"]["available"])

if __name__=="__main__":unittest.main()
