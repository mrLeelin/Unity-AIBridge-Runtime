"""Bounded read/compare operations for AI-driven Player verification. No eval."""
import math
import time
import runtimebridge as bridge


class InteractionError(bridge.BridgeError):
    def __init__(self, code, message, **details):
        super().__init__(message)
        self.data = {"code": code, "message": message, **details}


def validate(value, schema, path="arguments"):
    types = {"object": dict, "array": list, "string": str, "boolean": bool,
             "integer": int, "number": (int, float), "null": type(None)}
    kind = schema.get("type")
    if kind:
        valid = isinstance(value, types[kind])
        if kind in ("integer", "number"):
            valid = valid and not isinstance(value, bool) and math.isfinite(value)
        if not valid:
            raise ValueError(f"{path} must be {kind}")
    if "enum" in schema and value not in schema["enum"]:
        raise ValueError(f"{path} must be one of {schema['enum']}")
    if isinstance(value, dict):
        properties = schema.get("properties", {})
        if any(k not in value for k in schema.get("required", [])):
            raise ValueError(f"{path} is missing required fields")
        if schema.get("additionalProperties") is False and set(value) - set(properties):
            raise ValueError(f"{path} contains unknown fields")
        for key, item in value.items():
            if key in properties:
                validate(item, properties[key], f"{path}.{key}")
    if isinstance(value, list):
        if len(value) > schema.get("maxItems", len(value)):
            raise ValueError(f"{path} has too many items")
        for index, item in enumerate(value):
            validate(item, schema.get("items", {}), f"{path}[{index}]")
    if isinstance(value, str):
        if not schema.get("minLength", 0) <= len(value) <= schema.get("maxLength", len(value)):
            raise ValueError(f"{path} has invalid length")
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if not math.isfinite(value) or not schema.get("minimum", value) <= value <= schema.get("maximum", value):
            raise ValueError(f"{path} is outside its allowed range")


PARAMETERS = {
    "status": ({}, []),
    "ui": ({"keyword": {"type": "string", "minLength": 1, "maxLength": 512},
            "maxResults": {"type": "integer", "minimum": 1, "maximum": 500},
            "includeDisabled": {"type": "boolean"}}, ["keyword"]),
    "hierarchy": ({"query": {"type": "string", "maxLength": 512},
                  "offset": {"type": "integer", "minimum": 0},
                  "limit": {"type": "integer", "minimum": 1, "maximum": 500}}, []),
    "member": ({"objectToken": {"type": "string", "minLength": 1},
               "member": {"type": "string", "minLength": 1},
               "riskAccepted": {"type": "boolean"}}, ["objectToken", "member"]),
    "logs": ({"logType": {"type": "string", "enum": ["all", "Log", "Warning", "Error", "Assert", "Exception"]},
             "tail": {"type": "integer", "minimum": 1, "maximum": 200},
             "regex": {"type": "string", "maxLength": 512},
             "sinceFrame": {"type": "integer", "minimum": 0}}, []),
}
ACTIONS = {"status": "runtime.status", "ui": "runtime.ui.find", "hierarchy": "runtime.hierarchy",
           "member": "runtime.member.get", "logs": "runtime.logs"}
MISSING = object()


def value_at(data, path):
    if not path:
        return data
    for key in path.split("."):
        if isinstance(data, dict) and key in data:
            data = data[key]
        elif isinstance(data, list) and key.isascii() and key.isdigit() and int(key) < len(data):
            data = data[int(key)]
        else:
            return MISSING
    return data


def equal(actual, expected):
    # Python 将 True == 1 视为真；验证游戏状态时必须保留 JSON 类型语义。
    if isinstance(actual, bool) or isinstance(expected, bool):
        return type(actual) is type(expected) and actual == expected
    if isinstance(actual, (int, float)) and isinstance(expected, (int, float)):
        return actual == expected
    if type(actual) is not type(expected):
        return False
    if isinstance(actual, dict):
        return actual.keys() == expected.keys() and all(equal(actual[k], expected[k]) for k in actual)
    if isinstance(actual, list):
        return len(actual) == len(expected) and all(equal(a, b) for a, b in zip(actual, expected))
    return actual == expected


def compare(actual, operator, expected):
    if operator == "exists": return actual is not MISSING and actual is not None
    if operator == "notExists": return actual is MISSING or actual is None
    if actual is MISSING:
        raise InteractionError("VALUE_NOT_FOUND", "Condition path is absent; refresh the query or correct the path.")
    if operator == "eq": return equal(actual, expected)
    if operator == "ne": return not equal(actual, expected)
    if operator == "contains":
        if isinstance(actual, str) and isinstance(expected, str): return expected in actual
        if isinstance(actual, list): return any(equal(item, expected) for item in actual)
        raise InteractionError("TYPE_MISMATCH", "contains requires a string/string or array/value pair.")
    if any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in (actual, expected)):
        raise InteractionError("TYPE_MISMATCH", "Ordered comparisons require finite numbers.")
    return {"gt": lambda: actual > expected, "gte": lambda: actual >= expected,
            "lt": lambda: actual < expected, "lte": lambda: actual <= expected}[operator]()


def identity(status):
    session = status.get("serviceSessionId")
    if not session:
        raise InteractionError("RUNTIME_UPDATE_REQUIRED", "This Player lacks session identity; rebuild the Development Player with this package.")
    return (status.get("targetId"), status.get("buildId"), session)


def check(client, condition, timeout_seconds=0, interval_ms=250):
    source = condition["source"]
    parameters = condition.get("params", {})
    properties, required = PARAMETERS[source]
    validate(parameters, {"type": "object", "properties": properties, "required": required, "additionalProperties": False}, "condition.params")
    operator = condition["operator"]
    if operator not in ("exists", "notExists") and "expected" not in condition:
        raise ValueError("condition.expected is required for this operator")
    started = time.monotonic()
    deadline = started + timeout_seconds
    initial_identity = None
    attempts = 0
    last = None
    def finished(matched, code=None):
        result = {"matched": matched, "condition": condition, "attempts": attempts,
                  "elapsedSeconds": round(time.monotonic() - started, 3), "lastObservation": last}
        if code: result["code"] = code
        return result
    def read(action, params):
        if timeout_seconds:
            return client.command(action, params, deadline=deadline)["Data"]
        return client.command(action, params)["Data"]
    while True:
        if attempts and time.monotonic() >= deadline:
            return finished(False, "WAIT_TIMEOUT" if timeout_seconds else "ASSERTION_FAILED")
        try:
            status = read("runtime.status", {})
            current_identity = identity(status)
            if initial_identity is None:
                initial_identity = current_identity
            if current_identity != initial_identity:
                raise InteractionError("SESSION_CHANGED", "Player session changed while waiting; query the current device again.", lastObservation=last)
            data = status if source == "status" else read(ACTIONS[source], parameters)
            after = status
            # 拒绝把重启期间读到的数据归到旧会话，不自动重发任何修改操作。
            if source != "status":
                after = read("runtime.status", {})
                if identity(after) != initial_identity:
                    raise InteractionError("SESSION_CHANGED", "Player session changed during observation.", lastObservation=last)
        except InteractionError:
            raise
        except bridge.BridgeError:
            if timeout_seconds and time.monotonic() >= deadline:
                return finished(False, "WAIT_TIMEOUT")
            raise
        actual = value_at(data, condition["path"])
        matched = compare(actual, operator, condition.get("expected"))
        attempts += 1
        last = {"actual": None if actual is MISSING else actual, "valuePresent": actual is not MISSING,
                "startFrame": status.get("frameCount"), "endFrame": after.get("frameCount"),
                "serviceSessionId": current_identity[2]}
        if matched:
            return finished(True)
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return finished(False, "WAIT_TIMEOUT" if timeout_seconds else "ASSERTION_FAILED")
        time.sleep(min(interval_ms / 1000, remaining))
