"""Standard MCP stdio interface for the existing authenticated Runtime protocol.
Credentials are supplied only through RUNTIMEBRIDGE_TOKEN.
"""
import json
import os
import sys
import runtimebridge as bridge
import runtime_interaction as interaction
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

TEXT = {"type": "string", "minLength": 1, "maxLength": 512}
UI_LIMIT = {"type": "integer", "minimum": 1, "maximum": 500, "description": "Maximum returned buttons; summary reports truncation."}
CONDITION = {
    "type": "object", "additionalProperties": False,
    "properties": {
        "source": {"type": "string", "enum": ["status", "ui", "hierarchy", "member", "logs"]},
        "params": {"type": "object", "description": "Read parameters for source: ui requires keyword; member requires objectToken/member. No actions or code."},
        "path": {"type": "string", "maxLength": 512, "description": "Dot-separated JSON keys/array indices, e.g. activeScene, summary.clickableCount, value, logs.0.message."},
        "operator": {"type": "string", "enum": ["eq", "ne", "gt", "gte", "lt", "lte", "contains", "exists", "notExists"]},
        "expected": {"description": "Required except for exists/notExists. Ordered comparisons use numbers."}
    }, "required": ["source", "path", "operator"]
}
ACTIONS.update({
    "runtime_screenshot": ("runtime.screenshot", "Capture the Player framebuffer and return an authenticated PNG image with frame/orientation metadata. Does not capture Android system dialogs.", {}),
    "runtime_ui_snapshot": ("runtime.ui.snapshot", "Inspect uGUI Buttons, their text, bounds, interactability and raycast blockers. This is not a complete UI Toolkit/text tree.", {"maxResults": UI_LIMIT, "includeDisabled": {"type": "boolean"}}),
    "runtime_ui_find": ("runtime.ui.find", "Find uGUI Buttons by keyword. Use summary counts for existence checks; returned buttons may be truncated.", {"keyword": TEXT, "maxResults": UI_LIMIT, "includeDisabled": {"type": "boolean"}}),
    "runtime_ui_raycast": ("runtime.ui.raycast", "Inspect hit targets/blockers at bottom-left-origin Unity screen pixels or a current target path/instanceId.", {"x": {"type": "number", "minimum": 0}, "y": {"type": "number", "minimum": 0}, "path": TEXT, "instanceId": {"type": "integer"}, "maxResults": UI_LIMIT}),
    "runtime_ui_click": ("runtime.ui.click", "Send an EventSystem pointer click after raycast checks. Requires riskAccepted=true. This is not OS touch injection, and click delivery does not verify a business result; follow with runtime_wait/runtime_assert.", {"x": {"type": "number", "minimum": 0}, "y": {"type": "number", "minimum": 0}, "path": TEXT, "instanceId": {"type": "integer"}, "riskAccepted": {"type": "boolean"}}),
    "runtime_observe": ("runtime.observe", "Capture status, uGUI Buttons, recent logs, optional object fields and PNG in one Unity frame. Requires an updated Development Player. No arbitrary property getters are evaluated. Omit screenshot for headless/background operation.", {"includeScreenshot": {"type": "boolean"}, "includeUi": {"type": "boolean"}, "maxResults": UI_LIMIT, "includeDisabled": {"type": "boolean"}, "logCount": {"type": "integer", "minimum": 0, "maximum": 200}, "logType": {"type": "string", "enum": ["all", "Log", "Warning", "Error", "Assert", "Exception"]}, "sinceFrame": {"type": "integer", "minimum": 0}, "objectTokens": {"type": "array", "maxItems": 16, "items": TEXT}}),
    "runtime_wait": (None, 'Poll a read condition until matched or timeout. Example: {"condition":{"source":"ui","params":{"keyword":"Start"},"path":"summary.clickableCount","operator":"gt","expected":0},"timeoutSeconds":10}. Returns last observation on failure; rejects session changes. Member property getters require explicit riskAccepted=true in condition.params.', {"condition": CONDITION, "timeoutSeconds": {"type": "number", "minimum": 0.1, "maximum": 60}, "intervalMs": {"type": "integer", "minimum": 100, "maximum": 5000}}),
    "runtime_assert": (None, 'Check a read condition once. Example: {"condition":{"source":"status","path":"activeScene","operator":"eq","expected":"Battle"}}. Failed assertions are tool errors with actual value. Member property getters require riskAccepted=true in condition.params.', {"condition": CONDITION}),
})
REQUIRED.update({"runtime_ui_find": ["keyword"], "runtime_ui_click": ["riskAccepted"],
                 "runtime_wait": ["condition"], "runtime_assert": ["condition"]})
MUTATING.update({"runtime_ui_click", "runtime_wait", "runtime_assert"})
OUTPUT_SCHEMA = {"type": "object", "properties": {"success": {"type": "boolean"}, "data": {}},
                 "required": ["success", "data"], "additionalProperties": False}


def tool_result(data, is_error=False, png=None):
    content = [{"type": "text", "text": json.dumps(data, ensure_ascii=False)}]
    if png is not None:
        content.append({"type": "image", "data": base64.b64encode(png).decode("ascii"), "mimeType": "image/png"})
    return {"content": content, "structuredContent": {"success": not is_error, "data": data}, "isError": is_error}


def validate_ui_target(name, arguments):
    if name not in {"runtime_ui_click", "runtime_ui_raycast"}: return
    if not (arguments.get("path") or arguments.get("instanceId") or "x" in arguments and "y" in arguments):
        raise ValueError("Provide path, nonzero instanceId, or both x and y.")
    if name == "runtime_ui_click" and arguments.get("riskAccepted") is not True:
        raise ValueError("Click requires riskAccepted=true.")


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
             "outputSchema": OUTPUT_SCHEMA,
             "annotations": {"readOnlyHint": name not in MUTATING, "destructiveHint": name in {"runtime_execute", "runtime_patch_source", "runtime_get_member", "runtime_set_member", "runtime_invoke", "runtime_ui_click", "runtime_wait", "runtime_assert"}}}
            for name, item in ACTIONS.items()]

def dispatch(request, client):
    method, params = request.get("method"), request.get("params") or {}
    if method == "initialize":
        versions = {"2024-11-05", "2025-03-26", "2025-06-18"}
        return {"protocolVersion": params.get("protocolVersion") if params.get("protocolVersion") in versions else "2024-11-05",
                "capabilities": {"tools": {}}, "serverInfo": {"name": "aibridge-runtime", "version": "1.3.0"}}
    if method == "ping": return {}
    if method == "tools/list": return {"tools": tools_list()}
    if method != "tools/call": raise ValueError("Unsupported MCP method")
    name = params.get("name")
    if name not in ACTIONS: raise ValueError("Unknown tool")
    arguments = params.get("arguments", {})
    if not isinstance(arguments, dict): raise ValueError("arguments must be an object")
    if set(arguments) - set(ACTIONS[name][2]): raise ValueError("Unknown argument")
    if any(key not in arguments for key in REQUIRED.get(name, [])): raise ValueError("Missing required argument")
    interaction.validate(arguments, {"type": "object", "properties": ACTIONS[name][2]})
    validate_ui_target(name, arguments)
    try:
        if name in {"runtime_wait", "runtime_assert"}:
            checked = interaction.check(client, arguments["condition"], arguments.get("timeoutSeconds", 10) if name == "runtime_wait" else 0, arguments.get("intervalMs", 250))
            return tool_result(checked, not checked["matched"])
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
        payload = data.get("Data", data) if isinstance(data, dict) else data
        if name in {"runtime_ui_click", "runtime_ui_raycast"} and (payload.get("success") is False or name == "runtime_ui_click" and payload.get("clicked") is False):
            return tool_result({"code": "UI_ACTION_FAILED", "message": payload.get("error", "No click handler received the click."), "observation": payload}, True)
        png = None
        if name in {"runtime_screenshot", "runtime_observe"}:
            artifact = payload if name == "runtime_screenshot" else payload.get("screenshot")
            if artifact is not None:
                try:
                    png = client.read_screenshot(artifact)
                except bridge.BridgeError as error:
                    return tool_result({"code": "SCREENSHOT_DOWNLOAD_FAILED", "message": str(error), "observation": data}, True)
        return tool_result(data, png=png)
    except interaction.InteractionError as error:
        return tool_result(error.data, True)
    except bridge.BridgeError as error:
        return tool_result({"code": "RUNTIME_ERROR", "message": str(error)}, True)
    except ValueError as error:
        return tool_result({"code": "INVALID_CONDITION", "message": str(error)}, True)

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
