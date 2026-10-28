import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
from unittest import mock
import uuid

TOOLS = Path(__file__).resolve().parents[1]
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))
import sourcepatch

spec = importlib.util.spec_from_file_location("sourcepatch_test_bridge", TOOLS / "runtimebridge.py")
real_bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(real_bridge)


class SourcePatchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="runtime-sourcepatch-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "project with spaces"
        self.root.mkdir()
        self.version = "6000.3.6f1"
        self.editor = Path(self.temp.name) / self.version / "Editor/Data"
        for relative in ("NetCoreRuntime/dotnet.exe", "DotNetSdkRoslyn/csc.dll", "Tools/Generator.dll", "Managed/old-mscorlib.dll"):
            path = self.editor / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"compiler fixture")
        self.profile_path = self.root / ".runtimebridge/profiles/build/profile.json"
        self.profile_path.parent.mkdir(parents=True)
        self.refdir = self.profile_path.parent / "references"
        self.refdir.mkdir()
        self.profile = {"target": "Android", "platform": "Android", "buildId": uuid.uuid4().hex,
                        "unityVersion": self.version, "referenceDirectory": "references",
                        "references": [], "assemblyFingerprints": [],
                        "defineSymbols": ["UNITY_ANDROID", "DEVELOPMENT_BUILD", "FEATURE_GAME"]}
        for name, content in (("mscorlib", b"exact Player core"), ("Game.Runtime", b"exact Player game")):
            path = self.refdir / (name + ".dll")
            path.write_bytes(content)
            self.profile["references"].append(path.name)
            self.profile["assemblyFingerprints"].append({"name": name, "sha256": hashlib.sha256(content).hexdigest()})
        self.save_profile()
        self.source_a = self.root / "Assets/Hotfix/First.cs"
        self.source_b = self.root / "LocalPackages/Game/Second.cs"
        self.source_a.parent.mkdir(parents=True)
        self.source_b.parent.mkdir(parents=True)
        self.source_a.write_text("public class First { public int Value() => 666; }")
        self.source_b.write_text("public class Second { }")
        self.rsp = self.root / "Library/Bee/artifacts/hashPDevDbg.dag/Game.Hotfix.rsp"
        self.rsp.parent.mkdir(parents=True)
        self.additional = self.rsp.with_name("Game.Hotfix.UnityAdditionalFile.txt")
        self.additional.write_text(self.root.as_posix())
        self.lines = ["-target:library", '-out:"Library/Bee/artifacts/hashPDevDbg.dag/Game.Hotfix.dll"',
                      '-refout:"Library/Bee/artifacts/hashPDevDbg.dag/Game.Hotfix.ref.dll"',
                      "-define:UNITY_ANDROID", "-define:DEVELOPMENT_BUILD", "-define:FEATURE_GAME",
                      '-r:"' + (self.editor / "Managed/old-mscorlib.dll").as_posix() + '"',
                      '-r:"Library/Bee/artifacts/hashPDevDbg.dag/Other.Hotfix.ref.dll"',
                      '-r:"Library/PackageCache/vendor/Unused.Package.dll"',
                      '-analyzer:"' + (self.editor / "Tools/Generator.dll").as_posix() + '"',
                      '"Assets/Hotfix/First.cs"', '"LocalPackages/Game/Second.cs"',
                      '-langversion:9.0', '/debug:portable', '/optimize-', '/deterministic',
                      '/additionalfile:"Library/Bee/artifacts/hashPDevDbg.dag/Game.Hotfix.UnityAdditionalFile.txt"']
        self.save_rsp()
        self.out = self.root / ".runtimebridge/source-patches/build/Game.Hotfix/candidate"
        self.bridge_patch = mock.patch.object(sourcepatch, "_bridge", return_value=real_bridge)
        self.bridge_patch.start()
        self.addCleanup(self.bridge_patch.stop)

    def save_rsp(self):
        self.rsp.write_text("\n".join(self.lines), encoding="utf-8")

    def save_profile(self):
        self.profile_path.write_text(json.dumps(self.profile), encoding="utf-8")

    def compiler(self, arguments, cwd, **options):
        self.arguments = arguments
        self.workspace = Path(cwd)
        self.compiled_response = Path(arguments[-1][1:]).read_text()
        (self.out / "Game.Hotfix.dll").write_bytes(b"compiled candidate")
        return types.SimpleNamespace(returncode=0, stdout="", stderr="")

    def compile(self):
        with mock.patch("subprocess.run", side_effect=self.compiler) as compiler:
            result = sourcepatch.compile_candidate(self.root, "Game.Hotfix", self.profile_path, self.out)
        return result, compiler

    def test_multisource_snapshot_uses_saved_source_and_exact_player_refs(self):
        before_a, before_b = self.source_a.read_bytes(), self.source_b.read_bytes()
        dll, compiler = self.compile()
        self.assertEqual(dll, self.out / "Game.Hotfix.dll")
        self.assertEqual(self.source_a.read_bytes(), before_a)
        self.assertEqual(self.source_b.read_bytes(), before_b)
        self.assertEqual((self.workspace / "Assets/Hotfix/First.cs").read_bytes(), before_a)
        self.assertEqual((self.workspace / "LocalPackages/Game/Second.cs").read_bytes(), before_b)
        self.assertIn('"Assets/Hotfix/First.cs"', self.compiled_response)
        self.assertIn('"LocalPackages/Game/Second.cs"', self.compiled_response)
        self.assertNotIn("old-mscorlib.dll", self.compiled_response)
        self.assertNotIn("Other.Hotfix.ref.dll", self.compiled_response)
        self.assertNotIn("Unused.Package.dll", self.compiled_response)
        self.assertIn((self.out / "references/mscorlib.dll").as_posix(), self.compiled_response)
        self.assertEqual((self.out / "references/mscorlib.dll").read_bytes(), b"exact Player core")
        self.assertNotIn("shell", compiler.call_args.kwargs)
        self.assertEqual(self.arguments[0], str(self.editor / "NetCoreRuntime/dotnet.exe"))

    def test_flags_and_project_relative_source_generator_data_are_preserved(self):
        self.compile()
        for line in ("-define:FEATURE_GAME", "-langversion:9.0", "/debug:portable", "/optimize-", "/deterministic"):
            self.assertIn(line, self.compiled_response)
        self.assertIn((self.editor / "Tools/Generator.dll").as_posix(), self.compiled_response)
        additional = self.out / "compiler-support/Game.Hotfix.UnityAdditionalFile.txt"
        self.assertEqual(additional.read_text(), self.workspace.as_posix())

    def test_player_netstandard_contracts_are_kept_instead_of_runtime_implementations(self):
        for name in ("netstandard", "mscorlib"):
            contract = self.editor / "NetStandard/ref/2.1.0" / (name + ".dll")
            contract.parent.mkdir(parents=True, exist_ok=True)
            contract.write_bytes(("Player SDK " + name).encode())
            self.lines.append('-r:"' + contract.as_posix() + '"')
        self.save_rsp()
        self.assertEqual(sourcepatch.contract_reference_names(self.root, "Game.Hotfix", self.profile_path),
                         {"netstandard", "mscorlib"})
        self.compile()
        self.assertEqual((self.out / "references/mscorlib.dll").read_bytes(), b"Player SDK mscorlib")
        self.assertEqual((self.out / "references/netstandard.dll").read_bytes(), b"Player SDK netstandard")

    def test_missing_player_sdk_contract_is_rejected_before_compile(self):
        self.lines.append('-r:"' + (self.editor / "NetStandard/ref/2.1.0/netstandard.dll").as_posix() + '"')
        self.save_rsp()
        with mock.patch("subprocess.run") as compiler, self.assertRaisesRegex(sourcepatch.SourcePatchError, "runtime reference DLL"):
            self.compile()
        compiler.assert_not_called()

    def test_missing_reference_candidates_exclude_sdk_package_and_ref_suffix(self):
        self.assertEqual(sourcepatch.required_reference_names(self.root, "Game.Hotfix", self.profile_path), ["Other.Hotfix"])

    def test_existing_project_reference_uses_real_assembly_identity(self):
        path = self.rsp.parent / "Other.Hotfix.ref.dll"
        path.write_bytes(b"reference assembly fixture")
        with mock.patch.object(real_bridge, "metadata_tool", return_value={"assemblyName": "Other.RealName"}) as inspect:
            self.assertEqual(sourcepatch.required_reference_names(self.root, "Game.Hotfix", self.profile_path), ["Other.RealName"])
        inspect.assert_called_once_with("inspect", path)

    def test_changed_reference_bytes_during_snapshot_cannot_reach_compiler(self):
        real_read = Path.read_bytes
        def mutate_read(path):
            if path == self.refdir / "mscorlib.dll":
                return b"racing reference replacement"
            return real_read(path)
        with mock.patch.object(Path, "read_bytes", mutate_read), mock.patch("subprocess.run") as compiler:
            with self.assertRaisesRegex(sourcepatch.SourcePatchError, "Reference bytes changed"):
                sourcepatch.compile_candidate(self.root, "Game.Hotfix", self.profile_path, self.out)
        compiler.assert_not_called()

    def test_editor_defines_are_rejected_before_compiler_runs(self):
        self.lines.append("-define:UNITY_EDITOR_WIN")
        self.save_rsp()
        with mock.patch("subprocess.run") as compiler, self.assertRaisesRegex(sourcepatch.SourcePatchError, "UNITY_EDITOR"):
            sourcepatch.compile_candidate(self.root, "Game.Hotfix", self.profile_path, self.out)
        compiler.assert_not_called()

    def test_editor_reference_context_is_not_silently_accepted(self):
        self.lines.append('-r:"Library/ScriptAssemblies/Game.Editor.dll"')
        self.save_rsp()
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "Editor assemblies"):
            self.compile()

    def test_wrong_platform_and_non_development_responses_are_rejected(self):
        for invalid in ("UNITY_IOS", "UNITY_STANDALONE_WIN"):
            self.lines = [line.replace("UNITY_ANDROID", invalid) for line in self.lines]
            self.save_rsp()
            with self.assertRaisesRegex(sourcepatch.SourcePatchError, "Android Development"):
                self.compile()

    def test_changed_define_context_is_rejected(self):
        self.lines.append("-define:UNRECORDED_FEATURE")
        self.save_rsp()
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "defines differ"):
            self.compile()

    def test_platform_binding_supports_desktop_and_ios_without_android_defines(self):
        original_lines = list(self.lines)
        for target, symbol in (("StandaloneWindows64", "UNITY_STANDALONE_WIN"),
                               ("StandaloneLinux64", "UNITY_STANDALONE_LINUX"),
                               ("StandaloneOSX", "UNITY_STANDALONE_OSX"), ("iOS", "UNITY_IOS")):
            self.profile["target"] = self.profile["platform"] = target
            self.profile["defineSymbols"] = [symbol, "DEVELOPMENT_BUILD", "FEATURE_GAME"]
            self.save_profile()
            self.lines = [line.replace("UNITY_ANDROID", symbol) for line in original_lines]
            self.save_rsp()
            self.compile()
            self.assertIn("-define:" + symbol, self.compiled_response)
            self.assertNotIn("UNITY_ANDROID", self.compiled_response)

    def test_mac_editor_contents_use_the_exact_version_and_extensionless_host(self):
        data = Path(self.temp.name) / self.version / "Unity.app/Contents"
        for relative in ("NetCoreRuntime/dotnet", "DotNetSdkRoslyn/csc.dll"):
            path = data / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"mac compiler fixture")
        host, compiler = sourcepatch._editor_data(['-analyzer:"' + (data / "Tools/Generator.dll").as_posix() + '"'], self.profile)
        self.assertEqual(host, data / "NetCoreRuntime/dotnet")
        self.assertEqual(compiler, data / "DotNetSdkRoslyn/csc.dll")

    def test_profile_bytes_are_validated_before_compilation(self):
        (self.refdir / "mscorlib.dll").write_bytes(b"wrong Player")
        with mock.patch("subprocess.run") as compiler, self.assertRaisesRegex(real_bridge.BridgeError, "SHA256 mismatch"):
            sourcepatch.compile_candidate(self.root, "Game.Hotfix", self.profile_path, self.out)
        compiler.assert_not_called()

    def test_source_escape_is_rejected(self):
        external = Path(self.temp.name) / "Outside.cs"
        external.write_text("class Outside {}")
        self.lines.append('"' + external.as_posix() + '"')
        self.save_rsp()
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "inside the project"):
            self.compile()
        self.assertFalse(self.out.exists())

    def test_source_symlink_escape_is_rejected_when_supported(self):
        outside = Path(self.temp.name) / "Outside.cs"
        outside.write_text("class Outside {}")
        link = self.source_a.parent / "Link.cs"
        try:
            link.symlink_to(outside)
        except OSError:
            self.skipTest("OS does not allow creating file symlinks")
        self.lines.append('"Assets/Hotfix/Link.cs"')
        self.save_rsp()
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "inside the project"):
            self.compile()

    def test_wrong_assembly_output_and_recursive_inputs_are_rejected(self):
        self.lines[1] = '-out:"Library/Bee/artifacts/hashPDevDbg.dag/Other.dll"'
        self.save_rsp()
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "output does not match"):
            self.compile()
        self.lines[1] = '-out:"Library/Bee/artifacts/hashPDevDbg.dag/Game.Hotfix.dll"'
        self.lines.append('-recurse:"Assets/**/*.cs"')
        self.save_rsp()
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "Unsupported.*recurse"):
            self.compile()

    def test_output_and_assembly_paths_cannot_escape(self):
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "without a directory"):
            sourcepatch.compile_candidate(self.root, "../Game", self.profile_path, self.out)
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "inside this project"):
            sourcepatch.compile_candidate(self.root, "Game.Hotfix", self.profile_path, Path(self.temp.name) / "outside")

    def test_unity_compiler_version_must_match_profile(self):
        self.profile["unityVersion"] = "6000.3.99f1"
        self.save_profile()
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "exact Unity"):
            self.compile()

    def test_compile_failure_does_not_return_an_old_candidate(self):
        self.out.mkdir(parents=True)
        (self.out / "Game.Hotfix.dll").write_bytes(b"old candidate")
        with mock.patch("subprocess.run", return_value=types.SimpleNamespace(returncode=1, stdout="error CS1002", stderr="")):
            with self.assertRaisesRegex(sourcepatch.SourcePatchError, "CS1002"):
                sourcepatch.compile_candidate(self.root, "Game.Hotfix", self.profile_path, self.out)

    def test_extra_verified_runtime_references_are_snapshotted(self):
        extra = self.root / ".runtimebridge/baseline/Other.Hotfix.dll"
        extra.parent.mkdir(parents=True)
        extra.write_bytes(b"verified loaded dependency")
        with mock.patch("subprocess.run", side_effect=self.compiler):
            sourcepatch.compile_candidate(self.root, "Game.Hotfix", self.profile_path, self.out, [extra])
        self.assertIn((self.out / "references/Other.Hotfix.dll").as_posix(), self.compiled_response)
        self.assertEqual((self.out / "references/Other.Hotfix.dll").read_bytes(), extra.read_bytes())

    def test_internal_verified_profile_reuse_does_not_skip_frozen_byte_validation(self):
        verified = real_bridge.target_references(self.profile_path)
        with mock.patch.object(real_bridge, "target_references", side_effect=AssertionError("profile must be reused")), mock.patch("subprocess.run", side_effect=self.compiler):
            self.assertEqual(sourcepatch.required_reference_names(self.root, "Game.Hotfix", self.profile_path, _verified_profile=verified), ["Other.Hotfix"])
            dll = sourcepatch.compile_candidate(self.root, "Game.Hotfix", self.profile_path, self.out, _verified_profile=verified)
            self.assertTrue(dll.is_file())
            (self.refdir / "mscorlib.dll").write_bytes(b"changed after session verification")
            with self.assertRaisesRegex(sourcepatch.SourcePatchError, "Reference bytes changed"):
                sourcepatch.compile_candidate(self.root, "Game.Hotfix", self.profile_path, self.out, _verified_profile=verified)

    def baseline_inspector(self, identities):
        def inspect(path):
            content = Path(path).read_bytes()
            item = identities.get(content, {"assemblyName": "Wrong", "mvid": uuid.uuid4().hex})
            return dict(item, sha256=hashlib.sha256(content).hexdigest())
        return inspect

    def test_baseline_uses_loaded_identity_not_the_newest_same_named_dll(self):
        mvid = str(uuid.uuid4())
        assets = self.root / "Assets/Resources/Game.Hotfix.dll.bytes"
        assets.parent.mkdir(parents=True)
        assets.write_bytes(b"packaged original")
        current = self.root / "HybridCLRData/HotUpdateDlls/Android/Game.Hotfix.dll"
        current.parent.mkdir(parents=True)
        current.write_bytes(b"new source candidate")
        inspect = self.baseline_inspector({b"packaged original": {"assemblyName": "Game.Hotfix", "mvid": mvid},
                                           b"new source candidate": {"assemblyName": "Game.Hotfix", "mvid": str(uuid.uuid4())}})
        frozen = sourcepatch.find_baseline(self.root, self.profile_path, "Game.Hotfix", mvid, inspect)
        self.assertEqual(frozen.read_bytes(), b"packaged original")
        self.assertTrue(frozen.is_relative_to(self.root / ".runtimebridge/source-patches/baselines"))
        assets.write_bytes(b"later asset replaced")
        self.assertEqual(sourcepatch.find_baseline(self.root, self.profile_path, "Game.Hotfix", mvid, inspect), frozen)

    def test_baseline_assembly_name_and_mvid_must_both_match(self):
        directory = self.profile_path.parent / "patch-baselines"
        directory.mkdir()
        (directory / "Other.dll").write_bytes(b"other identity")
        mvid = str(uuid.uuid4())
        inspect = self.baseline_inspector({b"other identity": {"assemblyName": "Other", "mvid": mvid}})
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "No original Player DLL"):
            sourcepatch.find_baseline(self.root, self.profile_path, "Game.Hotfix", mvid, inspect)

    def test_ambiguous_or_empty_mvid_baselines_are_rejected(self):
        directory = self.profile_path.parent / "patch-baselines"
        directory.mkdir()
        (directory / "First.dll").write_bytes(b"one")
        (directory / "Second.dll").write_bytes(b"two")
        mvid = str(uuid.uuid4())
        inspect = self.baseline_inspector({content: {"assemblyName": "Game.Hotfix", "mvid": mvid} for content in (b"one", b"two")})
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "ambiguous"):
            sourcepatch.find_baseline(self.root, self.profile_path, "Game.Hotfix", mvid, inspect)
        with self.assertRaisesRegex(sourcepatch.SourcePatchError, "nonzero"):
            sourcepatch.find_baseline(self.root, self.profile_path, "Game.Hotfix", "0" * 32, inspect)


if __name__ == "__main__":
    unittest.main()
