"""Real PE fixtures for method-body planning; no candidate assembly is executed."""
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

TOOLS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("patch_plan_bridge", TOOLS / "runtimebridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


@unittest.skipUnless(shutil.which("dotnet"), "Local .NET SDK required")
class PatchPlanTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="runtimebridge-plan-")
        cls.root = Path(cls.temporary.name)
        cls.images = {}
        sources = {
            "numeric-base": "public class Sample { public int Read() => 100; public bool Same() => false; }",
            "numeric-next": "public class Sample { public int Read() => 666; public bool Same() => false; }",
            "numeric-same": "// Different source file and MVID, identical executable bodies.\npublic class Sample { public int Read() => 100; public bool Same() => false; }",
            "strings-base": 'public class Sample { public string First() => "one"; public string Second() => "two"; }',
            "strings-next": 'public class Sample { public string First() => "one-much-longer"; public string Second() => "two"; }',
            "overload-base": 'public class Sample { private int Hidden() => 1; public int Read(int n) => n + 1; public int Read(string n) => n.Length; }',
            "overload-next": 'public class Sample { private int Hidden() => 2; public int Read(int n) => n + 2; public int Read(string n) => n.Length; }',
            "field-added": "public class Sample { public int Added; public int Read() => 666; public bool Same() => false; }",
            "method-added": "public class Sample { public int Read() => 666; public bool Same() => false; public int Added() => 1; }",
            "generic-base": 'public class Sample { public T Echo<T>(T value) => value; public object Read() => Echo<object>("same"); }',
            "generic-next": 'public class Sample { public T Echo<T>(T value) => value; public object Read() => Echo<string>("same"); }',
            "exception-base": 'public class Sample { public int Read(string value) { try { return value.Length; } catch(System.Exception) { return 1; } } }',
            "exception-next": 'public class Sample { public int Read(string value) { try { return value.Length; } catch(System.NullReferenceException) { return 1; } } }',
            "async-base": 'public class Sample { public async System.Threading.Tasks.Task<int> ReadAsync() { await System.Threading.Tasks.Task.Delay(1); return 100; } }',
            "async-next": 'public class Sample { public async System.Threading.Tasks.Task<int> ReadAsync() { await System.Threading.Tasks.Task.Delay(1); return 666; } }',
            "rva-base": 'public class Sample { public byte[] Read() => new byte[] {31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46}; }',
            "locals-base": 'public class Sample { public object Read() { int value = 1; return value; } }',
            "locals-next": 'public class Sample { public object Read() { long value = 1; return value; } }',
            "switch-base": 'public class Sample { public int Read(int value) { switch(value) { case 0: return 10; case 1: return 11; case 2: return 12; case 3: return 13; case 4: return 14; default: return 20; } } }',
            "switch-next": 'public class Sample { public int Read(int value) { switch(value) { case 0: return 10; case 1: return 11; case 2: return 99; case 3: return 13; case 4: return 14; default: return 20; } } }',
        }
        for name, source in sources.items():
            directory = cls.root / name
            directory.mkdir()
            (directory / "Fixture.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework>'
                '<AssemblyName>PatchPlanFixture</AssemblyName><GenerateAssemblyInfo>false</GenerateAssemblyInfo>'
                '<Deterministic>true</Deterministic><Optimize>false</Optimize></PropertyGroup></Project>', encoding="utf-8")
            (directory / "Sample.cs").write_text(source, encoding="utf-8")
            subprocess.run(["dotnet", "build", str(directory / "Fixture.csproj"), "--nologo", "--verbosity", "quiet"],
                           capture_output=True, check=True)
            cls.images[name] = directory / "bin/Debug/net8.0/PatchPlanFixture.dll"
        data = cls.images["rva-base"].read_bytes()
        marker = bytes(range(31, 47))
        if data.count(marker) != 1:
            raise AssertionError("Expected one initialized RVA data blob")
        altered = cls.root / "rva-mutated.dll"
        altered.write_bytes(data.replace(marker, bytes([99]) + marker[1:]))
        cls.images["rva-mutated"] = altered

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def plan(self, baseline, candidate):
        return bridge.metadata_tool("plan", self.images[baseline], self.images[candidate])

    def test_numeric_change_selects_only_changed_method(self):
        plan = self.plan("numeric-base", "numeric-next")
        self.assertTrue(plan["compatible"], plan)
        self.assertEqual([(m["typeName"], m["name"]) for m in plan["changedMethods"]], [("Sample", "Read")])
        self.assertEqual(plan["changedMethods"][0]["token"] & 0xff000000, 0x06000000)
        self.assertNotEqual(plan["baselineBodySha256"], plan["candidateBodySha256"])

    def test_different_mvid_with_identical_bodies_is_not_a_change(self):
        plan = self.plan("numeric-base", "numeric-same")
        self.assertTrue(plan["compatible"], plan)
        self.assertNotEqual(plan["baselineMvid"], plan["candidateMvid"])
        self.assertEqual(plan["changedMethods"], [])
        self.assertEqual(plan["baselineBodySha256"], plan["candidateBodySha256"])

    def test_same_file_has_no_changes(self):
        self.assertEqual(self.plan("numeric-base", "numeric-base")["changedMethods"], [])

    def test_user_string_content_changes_not_string_heap_offsets(self):
        plan = self.plan("strings-base", "strings-next")
        self.assertTrue(plan["compatible"], plan)
        self.assertEqual([m["name"] for m in plan["changedMethods"]], ["First"])

    def test_private_and_overloaded_methods_use_unique_tokens(self):
        plan = self.plan("overload-base", "overload-next")
        self.assertTrue(plan["compatible"], plan)
        self.assertEqual([m["name"] for m in plan["changedMethods"]], ["Hidden", "Read"])
        self.assertEqual(len({m["token"] for m in plan["changedMethods"]}), 2)

    def test_field_change_produces_no_applicable_plan(self):
        plan = self.plan("numeric-base", "field-added")
        self.assertFalse(plan["compatible"])
        self.assertEqual(plan["changedMethods"], [])

    def test_method_addition_produces_no_applicable_plan(self):
        plan = self.plan("numeric-base", "method-added")
        self.assertFalse(plan["compatible"])
        self.assertEqual(plan["changedMethods"], [])

    def test_generic_methodspec_operand_is_compared(self):
        plan = self.plan("generic-base", "generic-next")
        self.assertTrue(plan["compatible"], plan)
        self.assertEqual([m["name"] for m in plan["changedMethods"]], ["Read"])

    def test_exception_handler_type_is_compared(self):
        plan = self.plan("exception-base", "exception-next")
        self.assertTrue(plan["compatible"], plan)
        self.assertEqual([m["name"] for m in plan["changedMethods"]], ["Read"])

    def test_existing_async_state_machine_body_is_selected(self):
        plan = self.plan("async-base", "async-next")
        self.assertTrue(plan["compatible"], plan)
        methods = plan["changedMethods"]
        self.assertEqual(len(methods), 1, plan)
        self.assertEqual(methods[0]["name"], "MoveNext")
        self.assertIn("+<ReadAsync>", methods[0]["typeName"])

    def test_initialized_field_data_change_is_not_method_body_patch(self):
        plan = self.plan("rva-base", "rva-mutated")
        self.assertFalse(plan["compatible"], plan)
        self.assertEqual(plan["changedMethods"], [])
        self.assertTrue(any("PrivateImplementationDetails" in value for value in plan["differences"]))

    def test_local_signature_change_is_selected(self):
        plan = self.plan("locals-base", "locals-next")
        self.assertTrue(plan["compatible"], plan)
        self.assertEqual([m["name"] for m in plan["changedMethods"]], ["Read"])

    def test_switch_targets_are_decoded(self):
        plan = self.plan("switch-base", "switch-next")
        self.assertTrue(plan["compatible"], plan)
        self.assertEqual([m["name"] for m in plan["changedMethods"]], ["Read"])

    def test_compare_remains_compatible(self):
        comparison = bridge.metadata_tool("compare", self.images["numeric-base"], self.images["numeric-next"])
        self.assertTrue(comparison["compatible"], comparison)
        self.assertNotIn("changedMethods", comparison)


if __name__ == "__main__":
    unittest.main()
