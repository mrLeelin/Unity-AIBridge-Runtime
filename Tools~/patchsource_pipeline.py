"""Compile saved Player source and apply a structurally verified method patch."""
import base64
import hashlib
import json
from pathlib import Path
import re
import uuid

import sourcepatch


def _data(response):
    value = response.get("Data", response.get("data"))
    if not isinstance(value, dict):
        raise ValueError("Runtime returned an invalid response object")
    return value


def _guid(value):
    return str(uuid.UUID(str(value)))


def _write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def _reference_scope(original_path, names, destination):
    """Retain original Player fingerprints for exactly the references used by this compilation."""
    original = json.loads(original_path.read_text(encoding="utf-8-sig"))
    available = {Path(name).stem: name for name in original.get("references", []) if isinstance(name, str)}
    fingerprints = {item.get("name"): item for item in original.get("assemblyFingerprints", []) if isinstance(item, dict)}
    ordered = sorted(set(names))
    if any(name not in available or name not in fingerprints for name in ordered):
        raise ValueError("A required compiler reference is absent from the matching Player profile")
    scope = dict(original)
    scope["referenceDirectory"] = str((original_path.parent / original.get("referenceDirectory", "references")).resolve())
    scope["references"] = [available[name] for name in ordered]
    scope["assemblyFingerprints"] = [fingerprints[name] for name in ordered]
    _write(destination, scope)
    return destination


def _remote_code(api, client, profile_path, directory, source, verified):
    key = hashlib.sha256(source.encode("utf-8") + profile_path.read_bytes()).hexdigest()
    cache = directory / "probes"
    cache.mkdir(parents=True, exist_ok=True)
    dll = cache / (key + ".dll")
    digest = cache / (key + ".sha256")
    if dll.is_file() and digest.is_file() and api.sha256(dll.read_bytes()) == digest.read_text().strip():
        payload = dll.read_bytes()
    else:
        payload = api._compile_verified_snippet(source, verified[1], verified[2], dll)
        digest.write_text(api.sha256(payload), encoding="ascii")
    result = client.command("runtime.code.execute", {
        "assemblyBase64": base64.b64encode(payload).decode("ascii"),
        "sha256": api.sha256(payload), "riskAccepted": True})
    returned = _data(result).get("returnValue")
    if not isinstance(returned, dict):
        raise ValueError("Runtime returned an invalid assembly probe")
    return returned


def _identity_source(assembly):
    literal = json.dumps(assembly, ensure_ascii=True)
    return """
var matches = System.AppDomain.CurrentDomain.GetAssemblies();
System.Reflection.Assembly target = null;
foreach (var item in matches)
{
    if (item.GetName().Name != ASSEMBLY_LITERAL) continue;
    if (target != null) return new { assemblyName = ASSEMBLY_LITERAL, identityErrorType = "AmbiguousAssembly", identityError = "Multiple target assemblies are loaded." };
    target = item;
}
if (target == null) return new { assemblyName = ASSEMBLY_LITERAL, identityErrorType = "AssemblyNotLoaded", identityError = "Target assembly is not loaded." };
System.Guid mvid;
try { mvid = target.ManifestModule.ModuleVersionId; }
catch (System.Exception exception)
{
    // 不把预期的不支持状态抛到旧版 Player 的异常格式化路径，避免读取堆栈时再次失败。
    return new { assemblyName = ASSEMBLY_LITERAL, identityErrorType = exception.GetType().FullName, identityError = exception.Message };
}
if (mvid == System.Guid.Empty) return new { assemblyName = ASSEMBLY_LITERAL, identityErrorType = "MissingMvid", identityError = "The target has no valid baseline MVID." };
return new { assemblyName = target.GetName().Name, mvid = mvid.ToString("D") };
""".replace("ASSEMBLY_LITERAL", literal)


def _require_mvid(identity, assembly):
    if identity.get("assemblyName") != assembly:
        raise ValueError("The runtime assembly probe returned a different target")
    error = identity.get("identityError")
    if error:
        if identity.get("identityErrorType") == "System.NotSupportedException":
            raise ValueError(assembly + " is an unsupported AOT/DHE target in this Player: its original MVID cannot be read. "
                             "Use a supported interpreter assembly, or rebuild with DHE and matching native reflection support. " + str(error))
        raise ValueError("Cannot identify " + assembly + ": " + str(error))
    return _guid(identity.get("mvid"))


def _history(entries, assembly, mvid):
    return [entry for entry in entries if entry.get("applied") is True
            and entry.get("assemblyName") == assembly
            and _guid(entry.get("baselineMvid")) == mvid]


def _selections(changed, history):
    selected = {}
    for method in changed:
        name = method["typeName"]
        if name == "<Module>":
            raise ValueError("Module initializer patches cannot be bound through Runtime reflection")
        selected.setdefault(name, {"tokens": set(), "signatures": set()})["tokens"].add(int(method["token"]))
    # Previously modified methods must be restored too when source is changed back to the baseline.
    for entry in history:
        for target in entry.get("methods", []):
            selected.setdefault(target["name"], {"tokens": set(), "signatures": set()})["signatures"].update(target["signatures"])
    return selected


def _catalogue_source(assembly, selections):
    groups = []
    for name, selected in sorted(selections.items()):
        tokens = ",".join(str(value) for value in sorted(selected["tokens"]))
        signatures = ",".join(json.dumps(value, ensure_ascii=True) for value in sorted(selected["signatures"]))
        groups.append("""
{
    var targetType = target.GetType(TYPE_LITERAL, true);
    var wantedTokens = new int[] { TOKEN_VALUES };
    var wantedSignatures = new string[] { SIGNATURE_VALUES };
    var methods = new System.Collections.Generic.List<System.Reflection.MethodBase>();
    methods.AddRange(targetType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly));
    methods.AddRange(targetType.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
    if (targetType.TypeInitializer != null) methods.Add(targetType.TypeInitializer);
    foreach (var method in methods)
    {
        var signature = method.ToString();
        if (method is System.Reflection.ConstructorInfo && signature.StartsWith("Void ")) signature = signature.Substring(5);
        bool wanted = System.Array.IndexOf(wantedTokens, method.MetadataToken) >= 0 || System.Array.IndexOf(wantedSignatures, signature) >= 0;
        if (wanted) found.Add(new { typeName = targetType.FullName, name = method.Name, token = method.MetadataToken, signature });
    }
}
""".replace("TYPE_LITERAL", json.dumps(name, ensure_ascii=True)).replace("TOKEN_VALUES", tokens).replace("SIGNATURE_VALUES", signatures))
    return """
System.Reflection.Assembly target = null;
foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
    if (assembly.GetName().Name == ASSEMBLY_LITERAL)
    {
        if (target != null) throw new System.InvalidOperationException("Multiple target assemblies are loaded.");
        target = assembly;
    }
if (target == null) throw new System.InvalidOperationException("Target assembly is no longer loaded.");
var found = new System.Collections.Generic.List<object>();
GROUPS
return new { assemblyName = target.GetName().Name, mvid = target.ManifestModule.ModuleVersionId.ToString("D"), methods = found };
""".replace("ASSEMBLY_LITERAL", json.dumps(assembly, ensure_ascii=True)).replace("GROUPS", "\n".join(groups))


def _bind_catalogue(catalogue, assembly, mvid, changed, selections):
    if catalogue.get("assemblyName") != assembly or _guid(catalogue.get("mvid")) != mvid:
        raise ValueError("The loaded target identity changed before method binding")
    methods = catalogue.get("methods")
    if not isinstance(methods, list):
        raise ValueError("Runtime returned an invalid method catalogue")
    indexed = {}
    for method in methods:
        key = (method.get("typeName"), int(method.get("token", 0)))
        if key in indexed or not method.get("signature"):
            raise ValueError("Runtime returned ambiguous method descriptors")
        indexed[key] = method
    for method in changed:
        actual = indexed.get((method["typeName"], int(method["token"])))
        if actual is None or actual.get("name") != method["name"]:
            raise ValueError("A changed method could not be bound on the Player: " + method["typeName"] + "." + method["name"])
    for name, selected in selections.items():
        signatures = {item["signature"] for item in methods if item["typeName"] == name}
        if not selected["signatures"].issubset(signatures):
            raise ValueError("Previously patched methods cannot be safely restored: " + name)
    return [{"name": name, "methods": [{"signature": signature} for signature in sorted({m["signature"] for m in methods if m["typeName"] == name})]}
            for name in sorted(selections)]


def prepare_and_apply(api, client, project_root, assembly, profile_path, compile_only=False):
    """No patch request is sent until all local and remote binding checks pass."""
    try:
        return _prepare_and_apply(api, client, Path(project_root).resolve(), assembly,
                                  Path(profile_path).resolve(), compile_only)
    except api.BridgeError:
        raise
    except Exception as error:
        raise api.BridgeError("Source patch failed: " + str(error)) from error


def _prepare_and_apply(api, client, project, assembly, profile_path, compile_only):
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.-]*", assembly or ""):
        raise ValueError("Select a valid source assembly")
    if not (project / "Assets").is_dir() or not (project / "ProjectSettings/ProjectVersion.txt").is_file():
        raise ValueError("A Unity project root is required")
    original_profile = json.loads(profile_path.read_text(encoding="utf-8-sig"))
    build_id = uuid.UUID(original_profile["buildId"]).hex
    working = project / ".runtimebridge/source-patches" / build_id / assembly
    working.mkdir(parents=True, exist_ok=True)
    probe_profile = _reference_scope(profile_path, {"mscorlib"}, working / "probe-profile.json")
    verified = api.target_references(probe_profile)
    profile = verified[0]
    api.validate_profile_target(client, profile)
    capabilities = _data(client.command("runtime.patch.capabilities", {}))
    if not capabilities.get("available") or not capabilities.get("prepared"):
        raise ValueError(capabilities.get("reason") or "Target method hotfix is unavailable")
    identity = _remote_code(api, client, probe_profile, working, _identity_source(assembly), verified)
    mvid = _require_mvid(identity, assembly)
    baseline = sourcepatch.find_baseline(project, profile_path, assembly, mvid,
                                        lambda path: api.metadata_tool("inspect", path), _verified_profile=verified)
    baseline_info = api.metadata_tool("inspect", baseline)
    available = {Path(name).stem: name for name in original_profile.get("references", [])}
    directory = (profile_path.parent / original_profile.get("referenceDirectory", "references")).resolve()
    required = set(sourcepatch.required_reference_names(project, assembly, profile_path, _verified_profile=verified))
    contracts = sourcepatch.contract_reference_names(project, assembly, profile_path, _verified_profile=verified)
    queue = list(baseline_info.get("assemblyReferences", []))
    selected = {"mscorlib"}
    visited = set(selected)
    extra = []
    while queue:
        name = queue.pop()
        if name in visited:
            continue
        visited.add(name)
        if len(visited) > 256:
            raise ValueError("The assembly dependency graph is too large")
        if name.casefold() in contracts:
            continue
        if name in available:
            selected.add(name)
            queue.extend(api.metadata_tool("inspect", directory / available[name]).get("assemblyReferences", []))
        elif name in required:
            dependency = _remote_code(api, client, probe_profile, working, _identity_source(name), verified)
            path = sourcepatch.find_baseline(project, profile_path, name, _require_mvid(dependency, name),
                                            lambda value: api.metadata_tool("inspect", value), _verified_profile=verified)
            extra.append(path)
            queue.extend(api.metadata_tool("inspect", path).get("assemblyReferences", []))
        else:
            raise ValueError("A baseline dependency is missing from this Player: " + name)
    compiler_profile = _reference_scope(profile_path, selected, working / "compiler-profile.json")
    compiler_verified = api.target_references(compiler_profile)
    # Distinct compiler sessions permit A -> B -> A, while the semantic journal below avoids redundant reapplication.
    output = working / "attempts" / uuid.uuid4().hex
    candidate = sourcepatch.compile_candidate(project, assembly, compiler_profile, output, extra_references=extra, _verified_profile=compiler_verified)
    plan = api.metadata_tool("plan", baseline, candidate)
    _write(output / "plan.json", plan)
    if not plan.get("compatible"):
        raise ValueError("Only method-body changes can be applied: " + "; ".join(plan.get("differences", [])))
    if plan.get("assemblyName") != assembly or _guid(plan.get("baselineMvid")) != mvid:
        raise ValueError("The candidate plan does not match the loaded baseline")
    changed = plan.get("changedMethods")
    if not isinstance(changed, list):
        raise ValueError("The metadata planner did not return a method list")
    history = _history(_data(client.command("runtime.patch.history", {})).get("entries", []), assembly, mvid)
    selections = _selections(changed, history)
    if not selections:
        return {"Success": True, "Data": {"returnValue": "当前源码与基线一致，无需应用补丁。", "sourcePatch": {"assemblyName": assembly, "unchanged": True}}}
    body_hash = plan.get("candidateBodySha256")
    if not re.fullmatch(r"[0-9a-fA-F]{64}", body_hash or ""):
        raise ValueError("The metadata planner did not return a semantic body fingerprint")
    journal_path = working / ("applied-" + uuid.UUID(mvid).hex + ".json")
    if journal_path.is_file() and history:
        try:
            previous = json.loads(journal_path.read_text(encoding="utf-8"))
        except (ValueError, OSError):
            previous = {}
        latest = history[-1]
        if (previous.get("bodyHash") == body_hash and previous.get("sha256") == latest.get("sha256")
                and previous.get("patchId") == latest.get("patchId") and previous.get("methods") == latest.get("methods")):
            return {"Success": True, "Data": {"deduplicated": True, "patch": latest,
                    "sourcePatch": {"assemblyName": assembly, "alreadyCurrent": True, "message": "当前源码已应用到这个 Player。"}}}
    catalogue = _remote_code(api, client, probe_profile, working, _catalogue_source(assembly, selections), verified)
    types = _bind_catalogue(catalogue, assembly, mvid, changed, selections)
    if not types or any(not target["methods"] for target in types):
        raise ValueError("No supported runtime method could be selected")
    payload = api.dll_bytes(candidate)
    if api.sha256(payload) != plan.get("candidateSha256"):
        raise ValueError("Candidate bytes changed after structural verification")
    patch_id = "source-" + build_id[:8] + "-" + api.sha256(payload)[:20]
    manifest = {"patchId": patch_id, "assemblyName": assembly, "baselineDll": str(baseline.resolve()),
                "baselineMvid": mvid, "types": types}
    manifest_path = output / "patch.json"
    _write(manifest_path, manifest)
    parameters = api.patch_parameters(candidate, manifest_path)
    detail = {"assemblyName": assembly, "buildId": build_id, "baselineMvid": mvid,
              "dll": str(candidate), "manifest": str(manifest_path),
              "methodCount": sum(len(item["methods"]) for item in types)}
    if compile_only:
        return {"Success": True, "Data": {"returnValue": "编译和校验完成，尚未应用。", "sourcePatch": detail}}
    api.validate_profile_target(client, profile)
    response = client.command("runtime.patch.apply", parameters)
    _data(response)["sourcePatch"] = detail
    applied = _data(response).get("patch", {})
    if applied.get("applied") is True:
        _write(journal_path, {"bodyHash": body_hash, "sha256": applied.get("sha256"),
                              "patchId": applied.get("patchId"), "methods": applied.get("methods")})
    _write(output / "result.json", response)
    return response
