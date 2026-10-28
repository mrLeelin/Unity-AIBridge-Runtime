#!/usr/bin/env python3
"""Desktop-only Runtime Bridge client. No Unity Editor process is required."""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parent
MAX_DLL_BYTES = 8 * 1024 * 1024


class BridgeError(Exception):
    pass


class JsonArgumentParser(argparse.ArgumentParser):
    def error(self, message):
        raise BridgeError(message)


class RemoteError(BridgeError):
    def __init__(self, result):
        self.result = result
        super().__init__(str(result.get("Error", result.get("error", "Remote command failed."))))


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def run_process(arguments, timeout=120):
    try:
        result = subprocess.run(arguments, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise BridgeError(f"Local tool failed: {type(error).__name__}") from error
    if result.returncode:
        raise BridgeError((result.stdout + "\n" + result.stderr).strip()[-12000:])
    return result.stdout.strip()


def dotnet_path():
    executable = shutil.which("dotnet")
    if not executable:
        raise BridgeError("The .NET SDK is required for local C# compilation and patch metadata validation.")
    return executable


def csc_path():
    dotnet = dotnet_path()
    candidates = []
    for line in run_process([dotnet, "--list-sdks"]).splitlines():
        match = re.fullmatch(r"([^ ]+) \[(.+)\]", line.strip())
        if match:
            version = tuple(int(part) for part in re.findall(r"\d+", match.group(1))[:3])
            path = Path(match.group(2)) / match.group(1) / "Roslyn/bincore/csc.dll"
            if path.is_file():
                candidates.append((version, path))
    if not candidates:
        raise BridgeError("No .NET SDK Roslyn compiler was found.")
    return dotnet, max(candidates)[1]


def target_references(profile_path):
    profile_path = Path(profile_path).resolve()
    profile = read_json(profile_path)
    target = str(profile.get("target") or profile.get("platform") or "")
    if not target or "editor" in target.lower() or not profile.get("buildId"):
        raise BridgeError("A Player target profile with target/platform and buildId is required; Editor references are unsupported.")
    directory = Path(profile.get("referenceDirectory", "."))
    if not directory.is_absolute():
        directory = profile_path.parent / directory
    references = []
    for item in profile.get("references", []):
        path = Path(item)
        if not path.is_absolute():
            path = directory / path
        path = path.resolve()
        lower = str(path).replace("\\", "/").lower()
        if "/library/scriptassemblies/" in lower or "unityeditor" in path.name.lower() or path.name.lower().endswith(".editor.dll"):
            raise BridgeError("The profile contains an Editor assembly; export references from the matching Player build.")
        if not path.is_file():
            raise BridgeError(f"Target reference does not exist: {path.name}")
        references.append(path)
    if not references:
        raise BridgeError("The target profile has no Player references.")
    reference_names = [path.stem.casefold() for path in references]
    if len(reference_names) != len(set(reference_names)):
        raise BridgeError("The target profile contains duplicate reference assembly names.")
    fingerprints = profile.get("assemblyFingerprints")
    if not isinstance(fingerprints, list):
        raise BridgeError("The target profile requires assemblyFingerprints for every Player reference.")
    fingerprint_by_name = {}
    for fingerprint in fingerprints:
        if not isinstance(fingerprint, dict) or not isinstance(fingerprint.get("name"), str):
            raise BridgeError("Invalid reference fingerprint in target profile.")
        name = fingerprint["name"].casefold()
        expected_hash = fingerprint.get("sha256")
        if name in fingerprint_by_name or not isinstance(expected_hash, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", expected_hash):
            raise BridgeError("Duplicate reference fingerprint or invalid reference SHA256 in target profile.")
        fingerprint_by_name[name] = expected_hash.lower()
    if set(reference_names) != set(fingerprint_by_name):
        raise BridgeError("Reference names and assemblyFingerprints must have exact one-to-one coverage.")
    for reference in references:
        actual_hash = hashlib.sha256()
        with reference.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                actual_hash.update(block)
        if actual_hash.hexdigest() != fingerprint_by_name[reference.stem.casefold()]:
            raise BridgeError(f"Target reference SHA256 mismatch: {reference.name}. Re-export the matching Player profile.")
    defines = profile.get("defineSymbols", [])
    if isinstance(defines, str):
        defines = [symbol for symbol in defines.split(";") if symbol]
    if any(not re.fullmatch(r"[A-Za-z_][A-Za-z_0-9]*", symbol) for symbol in defines):
        raise BridgeError("The target profile contains an invalid compiler define.")
    if any(symbol == "UNITY_EDITOR" or symbol.startswith("UNITY_EDITOR_") for symbol in defines):
        raise BridgeError("UNITY_EDITOR is not a Player compiler define.")
    return profile, references, defines


def wrap_snippet(code):
    # Only consume complete leading directives. Never remove 'using' inside code,
    # comments or strings (the Editor plugin's broad regex did exactly that).
    leading = []
    remaining = code
    trivia = re.compile(r"\A(?:\s+|//[^\n]*(?:\n|$)|/\*[\s\S]*?\*/)*")
    directive = re.compile(r"\A(?:global\s+)?using[ \t]+(?:static[ \t]+)?(?:global::)?[\w.@]+(?:[ \t]*=[ \t]*(?:global::)?[\w.@]+)?[ \t]*;")
    while True:
        skip = trivia.match(remaining).end()
        match = directive.match(remaining[skip:])
        if not match:
            break
        end = skip + match.end()
        leading.append(remaining[:end])
        remaining = remaining[end:]
    return "\n".join(leading) + "\npublic static class CodeExecutor\n{\n    public static async System.Threading.Tasks.Task<object> Execute()\n    {\n#line 1 \"runtimebridge-snippet.csx\"\n" + remaining + "\n#line default\n        return null;\n    }\n}\n"


def compile_snippet(code, profile_path, save_dll=None):
    if not profile_path:
        raise BridgeError("--profile is required for --code/--file. Export the exact target Player references first.")
    _, references, defines = target_references(profile_path)
    dotnet, compiler = csc_path()
    with tempfile.TemporaryDirectory(prefix="runtimebridge-compile-") as temporary:
        directory = Path(temporary)
        source = directory / "Snippet.cs"
        assembly = directory / ("RuntimeBridgeSnippet_" + uuid.uuid4().hex + ".dll")
        source.write_text(wrap_snippet(code), encoding="utf-8")
        arguments = ["-nologo", "-utf8output", "-target:library", "-nostdlib+", "-langversion:latest", "-optimize-", "-deterministic+", f'-out:"{assembly}"']
        arguments += [f'-r:"{reference}"' for reference in references]
        if defines:
            arguments.append("-define:" + ";".join(defines))
        arguments.append(f'"{source}"')
        response = directory / "compile.rsp"
        response.write_text("\n".join(arguments), encoding="utf-8")
        run_process([dotnet, str(compiler), "@" + str(response)])
        data = assembly.read_bytes()
        if save_dll:
            Path(save_dll).write_bytes(data)
        return data


def metadata_tool(*arguments):
    project = ROOT / "Metadata/RuntimeBridge.Metadata.csproj"
    files = [project, project.with_name("Program.cs")]
    fingerprint = hashlib.sha256(b"".join(path.read_bytes() for path in files)).hexdigest()[:16]
    cache = Path(tempfile.gettempdir()) / "runtimebridge-metadata" / fingerprint
    executable = cache / "RuntimeBridge.Metadata.dll"
    dotnet = dotnet_path()
    if not executable.is_file():
        cache.mkdir(parents=True, exist_ok=True)
        run_process([dotnet, "build", str(project), "--nologo", "--verbosity", "quiet", "-o", str(cache), "-p:BaseIntermediateOutputPath=" + str(cache / "obj") + os.sep])
    output = run_process([dotnet, str(executable), *map(str, arguments)])
    try:
        return json.loads(output)
    except json.JSONDecodeError as error:
        raise BridgeError("The metadata tool returned invalid JSON.") from error


def dll_bytes(path):
    data = Path(path).read_bytes()
    if not data or len(data) > MAX_DLL_BYTES:
        raise BridgeError("DLL must contain between 1 byte and 8 MiB.")
    return data


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def validate_profile_target(client, profile):
    response = client.command("runtime.status", {})
    status = response.get("Data", response.get("data", response))
    if not isinstance(status, dict):
        raise BridgeError("Runtime returned an invalid status result.")
    expected = str(profile.get("buildId", "")).replace("-", "").lower()
    actual = str(status.get("buildId", "")).replace("-", "").lower()
    if not actual:
        raise BridgeError("Runtime did not report a Player buildId; target profile compatibility cannot be verified.")
    if expected != actual:
        raise BridgeError("The reference profile buildId does not match the connected Player. Export/use that Player's exact build profile.")
    platform_map = {"StandaloneWindows64": "WindowsPlayer", "StandaloneWindows": "WindowsPlayer", "StandaloneOSX": "OSXPlayer", "StandaloneLinux64": "LinuxPlayer", "iOS": "IPhonePlayer"}
    target = str(profile.get("target") or profile.get("platform") or "")
    if status.get("platform") and platform_map.get(target, target) != status["platform"]:
        raise BridgeError("The reference profile platform does not match the connected Player.")


def patch_parameters(dll_path, manifest_path, validation_dll=None):
    manifest_path = Path(manifest_path).resolve()
    manifest = read_json(manifest_path)
    baseline = manifest.get("baselineDll")
    if not baseline:
        raise BridgeError("The patch manifest must specify baselineDll from the matching Player build.")
    baseline = Path(baseline)
    if not baseline.is_absolute():
        baseline = manifest_path.parent / baseline
    candidate = Path(validation_dll or dll_path)
    verification = metadata_tool("compare", baseline.resolve(), candidate.resolve())
    if not verification.get("compatible"):
        raise BridgeError("Patch changes assembly/type structure: " + "; ".join(verification.get("differences", [])))
    name = manifest.get("assemblyName", verification["assemblyName"])
    mvid = manifest.get("baselineMvid", verification["baselineMvid"])
    if name != verification["assemblyName"] or str(mvid).lower() != verification["baselineMvid"].lower():
        raise BridgeError("The patch manifest does not match the baseline assembly identity/MVID.")
    types = manifest.get("types")
    if not isinstance(types, list) or not types:
        raise BridgeError("The patch manifest must explicitly list types and method signatures.")
    for target in types:
        if not isinstance(target, dict) or not target.get("name") or not isinstance(target.get("methods"), list) or not target["methods"]:
            raise BridgeError("Every patch type must contain a name and nonempty methods array.")
        for method in target["methods"]:
            if not isinstance(method, dict) or not method.get("signature") or method.get("name"):
                raise BridgeError("Patch methods require an exact reflection signature; name-only selection is unsupported.")
    payload = dll_bytes(dll_path)
    if validation_dll:
        if manifest.get("encryptedSha256", "").lower() != sha256(payload) or manifest.get("encryptedFromSha256", "").lower() != verification["candidateSha256"]:
            raise BridgeError("Encrypted patches require encryptedSha256 and encryptedFromSha256 bindings from the trusted encryption pipeline.")
    return {
        "assemblyName": name,
        "assemblyBase64": base64.b64encode(payload).decode("ascii"),
        "sha256": sha256(payload),
        "baselineMvid": str(mvid),
        "baselineSha256": verification["baselineSha256"],
        "structureSha256": verification["structureSha256"],
        "structureVerified": True,
        "riskAccepted": True,
        "types": types,
        "patchId": manifest.get("patchId", "patch_" + uuid.uuid4().hex),
    }


class Client:
    def __init__(self, url, token="", timeout=30):
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme not in ("http", "https") or not parsed.netloc or parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise BridgeError("--url must be an HTTP(S) origin without credentials, query or fragment.")
        self.url = url.rstrip("/")
        self.token = token
        self.timeout = max(1, timeout)

    def request(self, path, data=None):
        headers = {"Accept": "application/json"}
        if self.token:
            headers["Authorization"] = "Bearer " + self.token
        if data is not None:
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(self.url + path, data=None if data is None else json.dumps(data).encode("utf-8"), headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=self.timeout + 5) as response:
                return response.status, json.load(response)
        except urllib.error.HTTPError as error:
            try:
                result = json.loads(error.read().decode("utf-8"))
            except (ValueError, UnicodeError):
                result = {"success": False, "error": "Runtime HTTP error", "httpStatus": error.code}
            raise RemoteError(result) from error
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            raise BridgeError(f"Runtime connection failed ({type(error).__name__}); confirm the Player is running and ADB forwarding is active.") from error

    def command(self, action, parameters):
        command_id = "runtimebridge_" + uuid.uuid4().hex
        payload = {"Id": command_id, "Action": action, "Params": parameters}
        deadline = time.monotonic() + self.timeout + 5
        status, result = self.request("/aibridge/commands?timeoutMs=" + str(min(self.timeout * 1000, 300000)), payload)
        while status == 202 or result.get("completed") is False or result.get("Completed") is False:
            if time.monotonic() >= deadline:
                raise BridgeError("Runtime execution is still pending; the command was not resent. Command ID: " + command_id)
            path = result.get("resultUrl") or "/aibridge/results/" + urllib.parse.quote(command_id, safe="")
            if not path.startswith("/aibridge/results/") or ".." in path or "?" in path or "#" in path:
                raise BridgeError("Runtime returned an invalid result URL.")
            time.sleep(0.1)
            status, result = self.request(path)
        if result.get("Success", result.get("success", True)) is False:
            raise RemoteError(result)
        return result


def create_parser():
    parser = JsonArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--url", default=os.environ.get("RUNTIMEBRIDGE_URL", "http://127.0.0.1:27182"))
    common.add_argument("--token", default=os.environ.get("RUNTIMEBRIDGE_TOKEN", ""))
    common.add_argument("--timeout", type=int, default=30, help="HTTP command timeout in seconds")
    commands.add_parser("status", parents=[common])
    logs = commands.add_parser("logs", parents=[common])
    logs.add_argument("--log-type", default="Error", choices=["Error", "Warning", "Log", "All"])
    logs.add_argument("--count", type=int, default=100)
    execute = commands.add_parser("exec", parents=[common])
    inputs = execute.add_mutually_exclusive_group(required=True)
    inputs.add_argument("--dll")
    inputs.add_argument("--code")
    inputs.add_argument("--file")
    execute.add_argument("--profile")
    execute.add_argument("--save-dll")
    execute.add_argument("--entry-type")
    execute.add_argument("--method-name")
    patch = commands.add_parser("patch", parents=[common])
    patch.add_argument("--dll", required=True)
    patch.add_argument("--manifest", required=True)
    patch.add_argument("--validation-dll", help="Plain candidate DLL corresponding to an externally encrypted --dll")
    commands.add_parser("patch-history", parents=[common])
    commands.add_parser("patch-capabilities", parents=[common])
    inspect = commands.add_parser("inspect-dll")
    inspect.add_argument("dll")
    device = commands.add_parser("device")
    device.add_argument("--adb", default="adb")
    device.add_argument("--serial")
    device.add_argument("--port", type=int, default=27182)
    return parser


def main(argv=None):
    args = None
    try:
        args = create_parser().parse_args(argv)
        if args.command == "inspect-dll":
            result = metadata_tool("inspect", Path(args.dll).resolve())
        elif args.command == "device":
            if not 1 <= args.port <= 65535:
                raise BridgeError("--port must be between 1 and 65535.")
            command = [args.adb] + (["-s", args.serial] if args.serial else []) + ["forward", f"tcp:{args.port}", f"tcp:{args.port}"]
            run_process(command, timeout=15)
            result = {"success": True, "forwardedPort": args.port}
        else:
            client = Client(args.url, args.token, args.timeout)
            if args.command == "exec":
                if not args.dll and not args.profile:
                    raise BridgeError("--profile is required for --code/--file. Export the exact target Player references first.")
                if args.profile:
                    profile, _, _ = target_references(args.profile)
                    validate_profile_target(client, profile)
                data = dll_bytes(args.dll) if args.dll else compile_snippet(args.code if args.code is not None else Path(args.file).read_text(encoding="utf-8-sig"), args.profile, args.save_dll)
                parameters = {"assemblyBase64": base64.b64encode(data).decode("ascii"), "sha256": sha256(data), "riskAccepted": True}
                if args.entry_type:
                    parameters["entryType"] = args.entry_type
                if args.method_name:
                    parameters["methodName"] = args.method_name
                result = client.command("runtime.code.execute", parameters)
            elif args.command == "patch":
                result = client.command("runtime.patch.apply", patch_parameters(args.dll, args.manifest, args.validation_dll))
            elif args.command == "logs":
                result = client.command("runtime.logs", {"logType": args.log_type, "tail": args.count, "includeStackTrace": True})
            else:
                action = {"status": "runtime.status", "patch-history": "runtime.patch.history", "patch-capabilities": "runtime.patch.capabilities"}[args.command]
                result = client.command(action, {})
        print(json.dumps(result, ensure_ascii=False))
        return 0
    except RemoteError as error:
        output = json.dumps(error.result, ensure_ascii=False)
    except (BridgeError, ValueError, OSError) as error:
        output = json.dumps({"success": False, "error": str(error)}, ensure_ascii=False)
    token = getattr(args, "token", "")
    if token:
        output = output.replace(token, "<REDACTED>")
    print(output)
    return 1


if __name__ == "__main__":
    sys.exit(main())
