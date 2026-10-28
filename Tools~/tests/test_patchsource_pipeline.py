import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
spec = importlib.util.spec_from_file_location("patchsource_pipeline", TOOLS / "patchsource_pipeline.py")
pipeline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pipeline)


class PipelineError(Exception):
    pass


class SourcePatchPipelineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="source-pipeline-")
        self.root = Path(self.temp.name)
        (self.root / "Assets").mkdir()
        (self.root / "ProjectSettings").mkdir()
        (self.root / "ProjectSettings/ProjectVersion.txt").write_text("6000.3.6f1")
        self.profile = self.root / "profile.json"
        self.mvid = "58457d29-27df-4e02-8c8c-5e075c19ba47"
        self.build = "ebdc26822c684233b1f33a396a6e2062"
        self.profile.write_text(json.dumps({"buildId": self.build, "target": "Android", "referenceDirectory": "references",
            "references": ["mscorlib.dll"], "assemblyFingerprints": [{"name": "mscorlib", "sha256": "fixture"}]}))
        self.baseline = self.root / "baseline.dll"
        self.baseline.write_bytes(b"baseline")
        self.body = "666"
        self.compatible = True
        self.changed = [{"typeName": "Sample", "name": "ReadValue", "token": 0x06000001}]
        self.entries = []
        self.applies = 0
        self.bound_token = 0x06000001
        self.runtime_mvid = self.mvid
        self.last_manifest = None
        self.api = SimpleNamespace(
            BridgeError=PipelineError,
            target_references=lambda path: (json.loads(Path(path).read_text()), [], []),
            validate_profile_target=mock.Mock(),
            metadata_tool=self.metadata,
            dll_bytes=lambda path: Path(path).read_bytes(),
            sha256=lambda data: hashlib.sha256(data).hexdigest(),
            patch_parameters=self.parameters)
        self.client = SimpleNamespace(command=self.command)
        self.patches = [
            mock.patch.object(pipeline.sourcepatch, "find_baseline", return_value=self.baseline),
            mock.patch.object(pipeline.sourcepatch, "required_reference_names", return_value=[]),
            mock.patch.object(pipeline.sourcepatch, "contract_reference_names", return_value=set()),
            mock.patch.object(pipeline.sourcepatch, "compile_candidate", side_effect=self.compile),
            mock.patch.object(pipeline, "_remote_code", side_effect=self.remote)]
        for patch in self.patches:
            patch.start()

    def tearDown(self):
        for patch in reversed(self.patches):
            patch.stop()
        self.temp.cleanup()

    def compile(self, project, assembly, profile, output, **kwargs):
        output.mkdir(parents=True)
        path = output / (assembly + ".dll")
        path.write_bytes((self.body + output.name).encode())
        return path

    def metadata(self, command, *paths):
        if command == "inspect":
            return {"assemblyName": "Fixture", "mvid": self.mvid, "assemblyReferences": []}
        return {"compatible": self.compatible, "differences": [] if self.compatible else ["Added field"],
                "assemblyName": "Fixture", "baselineMvid": self.mvid,
                "candidateSha256": hashlib.sha256(Path(paths[-1]).read_bytes()).hexdigest(),
                "candidateBodySha256": hashlib.sha256(self.body.encode()).hexdigest(),
                "changedMethods": self.changed}

    def parameters(self, dll, manifest):
        self.last_manifest = json.loads(Path(manifest).read_text())
        return dict(self.last_manifest, sha256=hashlib.sha256(Path(dll).read_bytes()).hexdigest())

    def command(self, command, arguments):
        if command == "runtime.patch.capabilities":
            return {"Success": True, "Data": {"available": True, "prepared": True}}
        if command == "runtime.patch.history":
            return {"Success": True, "Data": {"entries": self.entries.copy()}}
        if command == "runtime.patch.apply":
            self.applies += 1
            entry = dict(arguments, applied=True, methods=[
                {"name": item["name"], "signatures": [method["signature"] for method in item["methods"]]}
                for item in arguments["types"]])
            self.entries.append(entry)
            return {"Success": True, "Data": {"patch": entry, "deduplicated": False}}
        raise AssertionError(command)

    def remote(self, api, client, profile, working, source, verified):
        if "var found" not in source:
            return {"assemblyName": "Fixture", "mvid": self.runtime_mvid}
        return {"assemblyName": "Fixture", "mvid": self.runtime_mvid, "methods": [
            {"typeName": "Sample", "name": "ReadValue", "token": self.bound_token, "signature": "Int32 ReadValue()"}]}

    def run_patch(self, **kwargs):
        return pipeline.prepare_and_apply(self.api, self.client, self.root, "Fixture", self.profile, **kwargs)

    def test_compiles_binds_and_applies_without_manual_files(self):
        result = self.run_patch()
        self.assertEqual(self.applies, 1)
        self.assertTrue(result["Success"])
        self.assertEqual(self.last_manifest["types"], [{"name": "Sample", "methods": [{"signature": "Int32 ReadValue()"}]}])
        self.assertEqual(self.last_manifest["baselineMvid"], self.mvid)
        self.assertEqual(self.api.validate_profile_target.call_count, 2)

    def test_compile_only_does_not_apply(self):
        result = self.run_patch(compile_only=True)
        self.assertEqual(self.applies, 0)
        self.assertIn("尚未应用", result["Data"]["returnValue"])

    def test_structure_changes_stop_before_apply(self):
        self.compatible = False
        with self.assertRaisesRegex(PipelineError, "Only method-body"):
            self.run_patch()
        self.assertEqual(self.applies, 0)

    def test_unmodified_source_without_history_is_noop(self):
        self.changed = []
        result = self.run_patch()
        self.assertEqual(self.applies, 0)
        self.assertTrue(result["Data"]["sourcePatch"]["unchanged"])

    def test_changing_back_to_baseline_restores_prior_methods(self):
        self.run_patch()
        self.body = "100"
        self.changed = []
        self.run_patch()
        self.assertEqual(self.applies, 2)
        self.assertEqual(self.last_manifest["types"][0]["methods"][0]["signature"], "Int32 ReadValue()")

    def test_same_current_code_is_not_applied_twice(self):
        self.run_patch()
        result = self.run_patch()
        self.assertEqual(self.applies, 1)
        self.assertTrue(result["Data"]["deduplicated"])

    def test_a_to_b_to_a_generates_distinct_payload_and_applies(self):
        self.run_patch()
        first = self.entries[-1]["sha256"]
        self.body = "777"
        self.run_patch()
        self.body = "666"
        self.run_patch()
        self.assertEqual(self.applies, 3)
        self.assertNotEqual(self.entries[-1]["sha256"], first)

    def test_wrong_method_token_cannot_be_bound(self):
        self.bound_token = 0x06000002
        with self.assertRaisesRegex(PipelineError, "could not be bound"):
            self.run_patch()
        self.assertEqual(self.applies, 0)

    def test_target_changed_between_probes_is_rejected(self):
        original = self.remote
        def changed(*args):
            result = original(*args)
            if "methods" in result:
                result["mvid"] = "11111111-1111-1111-1111-111111111111"
            return result
        pipeline._remote_code.side_effect = changed
        with self.assertRaisesRegex(PipelineError, "identity changed"):
            self.run_patch()
        self.assertEqual(self.applies, 0)

    def test_invalid_target_name_is_rejected(self):
        with self.assertRaisesRegex(PipelineError, "valid source assembly"):
            pipeline.prepare_and_apply(self.api, self.client, self.root, "../Fixture", self.profile)
        self.assertEqual(self.applies, 0)

    def test_unsupported_aot_identity_is_reported_before_compilation(self):
        pipeline._remote_code.side_effect = lambda *args: {
            "assemblyName": "Fixture", "identityErrorType": "System.NotSupportedException",
            "identityError": "ModuleVersionId is supported only for loaded HybridCLR interpreter images; no original MVID is available for this AOT image."}
        with self.assertRaisesRegex(PipelineError, "Fixture.*AOT.*MVID"):
            self.run_patch()
        pipeline.sourcepatch.compile_candidate.assert_not_called()
        self.assertEqual(self.applies, 0)

    def test_modules_cannot_be_selected_as_runtime_types(self):
        self.changed = [{"typeName": "<Module>", "name": ".cctor", "token": 0x06000001}]
        with self.assertRaisesRegex(PipelineError, "Module initializer"):
            self.run_patch()
        self.assertEqual(self.applies, 0)


if __name__ == "__main__":
    unittest.main()
