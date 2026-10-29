"""Standard MCP stdio interface for the existing authenticated Runtime protocol.
Credentials are supplied only through RUNTIMEBRIDGE_TOKEN.
"""
import json
import os
import sys
import runtimebridge as bridge
import base64
from pathlib import Path

ACTIONS = {
    "runtime_execute": (None, "Compile and execute one-off C# against the connected Player profile. Can change game state.", {"code": {"type": "string"}, "profile": {"type": "string"}}),
    "runtime_patch_source": (None, "Compile saved project sources and apply a structurally verified method patch.", {"assembly": {"type": "string"}, "project": {"type": "string"}, "profile": {"type": "string"}, "compileOnly": {"type": "boolean"}}),
    "runtime_status": ("runtime.status", "Read connected Player identity and readiness.", {}),
    "runtime_diagnose": (None, "Compare live identities with the exact local build baseline and report native patch availability.", {"profile": {"type": "string"}}),
    "runtime_hierarchy": ("runtime.hierarchy", "List scene GameObjects. Handles belong to this live service.", {"query": {"type": "string"}, "offset": {"type": "integer"}, "limit": {"type": "integer"}}),
    "runtime_inspect": ("runtime.inspect", "Read an object and component fields without evaluating arbitrary getters.", {"objectToken": {"type": "string"}}),
    "runtime_get_member": ("runtime.member.get", "Read a public member. Property getters require explicit riskAccepted and may have side effects.", {"objectToken": {"type": "string"}, "member": {"type": "string"}, "riskAccepted": {"type": "boolean"}}),
    "runtime_set_member": ("runtime.member.set", "Set a public member. This changes the running game.", {"objectToken": {"type": "string"}, "member": {"type": "string"}, "value": {}, "riskAccepted": {"type": "boolean"}}),
    "runtime_invoke": ("runtime.method.invoke", "Invoke an exact public instance signature. This may change game state.", {"objectToken": {"type": "string"}, "signature": {"type": "string"}, "arguments": {"type": "array"}, "riskAccepted": {"type": "boolean"}}),
    "runtime_assemblies": ("runtime.assemblies", "Inspect loaded assembly identities; identity is not a guarantee of patch eligibility.", {}),
    "runtime_patch_capabilities": ("runtime.patch.capabilities", "Read native patch capability and remaining attempts.", {}),
    "runtime_patch_history": ("runtime.patch.history", "Read patch outcomes.", {}),
    "runtime_profiler_start": ("runtime.profiler.start", "Start a bounded named-marker recorder. Marker must already be registered.", {"marker": {"type": "string"}, "category": {"type": "string"}, "capacity": {"type": "integer"}, "durationSeconds": {"type": "integer"}}),
    "runtime_profiler_read": ("runtime.profiler.read", "Read raw named-marker samples; not a CPU hierarchy/GPU timeline.", {"sessionId": {"type": "string"}}),
    "runtime_profiler_stop": ("runtime.profiler.stop", "Stop/dispose recorder and optionally release session.", {"sessionId": {"type": "string"}, "release": {"type": "boolean"}}),
    "runtime_logs": ("runtime.logs", "Read bounded Player logs.", {"logType": {"type": "string"}, "tail": {"type": "integer"}}),
}
REQUIRED = {
    "runtime_execute": ["code"],
    "runtime_patch_source": ["assembly"],
    "runtime_inspect": ["objectToken"],
    "runtime_get_member": ["objectToken", "member"],
    "runtime_set_member": ["objectToken", "member", "value", "riskAccepted"],
    "runtime_invoke": ["objectToken", "signature", "arguments", "riskAccepted"],
    "runtime_profiler_start": ["marker"],
    "runtime_profiler_read": ["sessionId"],
    "runtime_profiler_stop": ["sessionId"],
}
MUTATING = {"runtime_get_member", "runtime_execute", "runtime_patch_source", "runtime_set_member", "runtime_invoke", "runtime_profiler_start", "runtime_profiler_stop"}

def profile_path(arguments, client):
    path = arguments.get("profile") or os.environ.get("RUNTIMEBRIDGE_PROFILE")
    if path: return Path(path)
    project = arguments.get("project") or os.environ.get("RUNTIMEBRIDGE_PROJECT")
    if not project: raise bridge.BridgeError("Set RUNTIMEBRIDGE_PROJECT or provide a matching profile.")
    status = client.command("runtime.status", {})["Data"]
    import uuid
    build = uuid.UUID(status["buildId"]).hex
    return Path(project) / ".runtimebridge/profiles" / build / "profile.json"

def tools_list():
    return [{"name": name, "description": item[1],
             "inputSchema": {"type": "object", "properties": item[2], "required": REQUIRED.get(name, []), "additionalProperties": False},
             "annotations": {"readOnlyHint": name not in MUTATING, "destructiveHint": name in {"runtime_execute", "runtime_patch_source", "runtime_get_member", "runtime_set_member", "runtime_invoke"}}}
            for name, item in ACTIONS.items()]

def dispatch(request, client):
    method, params = request.get("method"), request.get("params") or {}
    if method == "initialize":
        versions = {"2024-11-05", "2025-03-26", "2025-06-18"}
        return {"protocolVersion": params.get("protocolVersion") if params.get("protocolVersion") in versions else "2024-11-05",
                "capabilities": {"tools": {}}, "serverInfo": {"name": "aibridge-runtime", "version": "1.2.0"}}
    if method == "ping": return {}
    if method == "tools/list": return {"tools": tools_list()}
    if method != "tools/call": raise ValueError("Unsupported MCP method")
    name = params.get("name")
    if name not in ACTIONS: raise ValueError("Unknown tool")
    arguments = params.get("arguments", {})
    if not isinstance(arguments, dict): raise ValueError("arguments must be an object")
    if set(arguments) - set(ACTIONS[name][2]): raise ValueError("Unknown argument")
    if any(key not in arguments for key in REQUIRED.get(name, [])): raise ValueError("Missing required argument")
    try:
        if name == "runtime_diagnose":
            status = client.command("runtime.status", {})["Data"]
            assemblies = client.command("runtime.assemblies", {})["Data"]["items"]
            candidates, profile_error = [], None
            try:
                path = profile_path(arguments, client)
                profile = bridge.read_json(path)
                bridge.validate_profile_target(client, profile)
                index = path.parent / "patch-baselines/index.json"
                candidates = bridge.read_json(index).get("candidates", []) if index.is_file() else []
            except (bridge.BridgeError, ValueError, OSError) as error:
                profile_error = str(error)
            for assembly in assemblies:
                assembly["localBaselineRecorded"] = any(c.get("assemblyName") == assembly.get("name") and c.get("mvid") == assembly.get("mvid") for c in candidates)
            data = {"buildId": status.get("buildId"), "localProfileError": profile_error,
                    "patchCapabilities": client.command("runtime.patch.capabilities", {})["Data"], "assemblies": assemblies,
                    "recordedBaselinesNotLoaded": sorted({c.get("assemblyName") for c in candidates if c.get("assemblyName")} - {a.get("name") for a in assemblies})}
        elif name == "runtime_execute":
            path = profile_path(arguments, client)
            profile, _, _ = bridge.target_references(path)
            bridge.validate_profile_target(client, profile)
            code = arguments["code"]
            if not isinstance(code, str) or len(code) > 262144: raise bridge.BridgeError("Invalid or oversized code.")
            payload = bridge.compile_snippet(code, path)
            data = client.command("runtime.code.execute", {"assemblyBase64": base64.b64encode(payload).decode("ascii"), "sha256": bridge.sha256(payload), "riskAccepted": True})
        elif name == "runtime_patch_source":
            from patchsource_pipeline import prepare_and_apply
            project = arguments.get("project") or os.environ.get("RUNTIMEBRIDGE_PROJECT")
            if not project: raise bridge.BridgeError("A project root is required.")
            data = prepare_and_apply(bridge, client, project, arguments["assembly"], profile_path(arguments, client), arguments.get("compileOnly", False))
        else:
            data = client.command(ACTIONS[name][0], arguments)
        return {"content": [{"type": "text", "text": json.dumps(data, ensure_ascii=False)}], "isError": False}
    except bridge.BridgeError as error:
        return {"content": [{"type": "text", "text": str(error)}], "isError": True}

def main():
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    token = os.environ.get("RUNTIMEBRIDGE_TOKEN", "")
    client = bridge.Client(os.environ.get("RUNTIMEBRIDGE_URL", "http://127.0.0.1:27182"), token,
                           int(os.environ.get("RUNTIMEBRIDGE_TIMEOUT", "30")))
    for line in sys.stdin:
        request = None
        try:
            request = json.loads(line)
            if "id" not in request: continue
            result = {"jsonrpc": "2.0", "id": request["id"], "result": dispatch(request, client)}
        except Exception as error:
            result = {"jsonrpc": "2.0", "id": request.get("id") if isinstance(request, dict) else None,
                      "error": {"code": -32602, "message": str(error)}}
        output = json.dumps(result, ensure_ascii=False)
        if token: output = output.replace(token, "<REDACTED>")
        print(output, flush=True)

if __name__ == "__main__":
    main()
