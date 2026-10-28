"""Compile saved Player sources without modifying the Unity project or loading DLLs."""
from __future__ import annotations

import hashlib
import importlib
import json
from pathlib import Path
import re
import shutil
import sys
import uuid


class SourcePatchError(ValueError):
    """The requested source patch cannot be bound to this Player safely."""


def _bridge():
    # Delayed import: runtimebridge owns the command and also imports this module.
    entry = sys.modules.get("__main__")
    if (callable(getattr(entry, "target_references", None)) and getattr(entry, "__file__", None)
            and Path(entry.__file__).resolve() == Path(__file__).with_name("runtimebridge.py").resolve()):
        return entry
    return importlib.import_module("runtimebridge")


def _assembly_name(value):
    value = str(value)
    if value.lower().endswith(".dll"):
        value = value[:-4]
    if (not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.-]*", value)
            or value.lower().startswith("unityeditor") or value.lower().endswith(".editor")):
        raise SourcePatchError("A runtime assembly name without a directory is required.")
    return value


def _key(value):
    try:
        parsed = uuid.UUID(str(value))
        return parsed.hex if parsed.int else ""
    except (ValueError, TypeError, AttributeError):
        return ""


def _within(path, directory):
    try:
        path.resolve().relative_to(directory.resolve())
        return True
    except ValueError:
        return False


def _unquote(value):
    value = value.strip()
    if value.startswith('"') and value.endswith('"'):
        return value[1:-1]
    return value


def _path(root, value):
    value = _unquote(value)
    return (root / Path(value.replace("\\", "/"))).resolve()


def _option(line):
    match = re.fullmatch(r"[-/]([A-Za-z][A-Za-z0-9_+-]*)(?::(.*))?", line)
    return (match[1].lower(), match[2]) if match else (None, None)


def _required_defines(profile):
    targets = {"Android": "UNITY_ANDROID", "iOS": "UNITY_IOS",
               "StandaloneWindows": "UNITY_STANDALONE_WIN", "StandaloneWindows64": "UNITY_STANDALONE_WIN",
               "StandaloneLinux64": "UNITY_STANDALONE_LINUX", "StandaloneOSX": "UNITY_STANDALONE_OSX"}
    target = profile.get("target")
    if target not in targets or profile.get("platform", target) != target:
        raise SourcePatchError("A supported desktop/mobile Player build profile is required.")
    return {"DEVELOPMENT_BUILD", targets[target]}


def _profile(profile_path, verified=None):
    # Internal workflow reuse only. No CLI argument exposes a validation bypass.
    profile, references, defines = verified if verified is not None else _bridge().target_references(profile_path)
    if not _key(profile.get("buildId")):
        raise SourcePatchError("The profile requires a nonzero Player build GUID.")
    if not _required_defines(profile).issubset(defines):
        raise SourcePatchError("A matching " + str(profile.get("target")) + " Development Player profile is required.")
    if not isinstance(profile.get("unityVersion"), str) or not profile["unityVersion"]:
        raise SourcePatchError("The Player profile requires its exact Unity version.")
    return profile, references, set(defines)


def _read_response(path):
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    additional = Path(str(path) + "2")
    if additional.is_file():
        lines += additional.read_text(encoding="utf-8-sig").splitlines()
    return [line.strip() for line in lines if line.strip() and not line.lstrip().startswith("#")]


def _response_context(project_root, assembly_name, profile_path, verified=None):
    root = Path(project_root).resolve()
    name = _assembly_name(assembly_name)
    profile, references, profile_defines = _profile(profile_path, verified)
    responses = list((root / "Library/Bee/artifacts").glob("*PDevDbg.dag/" + name + ".rsp"))
    responses.sort(key=lambda path: path.stat().st_mtime_ns, reverse=True)
    failures = []
    for response in responses:
        try:
            lines = _read_response(response)
            defines = set()
            outputs = []
            for line in lines:
                option, value = _option(line)
                if option in ("define", "d"):
                    defines.update(part for part in re.split(r"[;,]", _unquote(value or "")) if part)
                if option in ("out", "o"):
                    outputs.append(_path(root, value or ""))
                if option in ("r", "reference"):
                    reference = _path(root, value or "")
                    normalized = reference.as_posix().casefold()
                    if ("/library/scriptassemblies/" in normalized or "unityeditor" in reference.name.casefold()
                            or reference.stem.casefold().endswith(".editor")):
                        raise SourcePatchError("The Player response unexpectedly references Editor assemblies.")
            if any(value == "UNITY_EDITOR" or value.startswith("UNITY_EDITOR_") for value in defines):
                raise SourcePatchError("UNITY_EDITOR compiler responses cannot compile Player patches.")
            if not _required_defines(profile).issubset(defines):
                raise SourcePatchError("The compiler response is not a " + profile["target"] + " Development Player response.")
            if not defines.issubset(profile_defines):
                raise SourcePatchError("The Player compiler defines differ from the selected build profile; rebuild the test Player.")
            if len(outputs) != 1 or outputs[0].name != name + ".dll":
                raise SourcePatchError("The compiler response output does not match the target assembly.")
            return root, name, profile, references, response, lines
        except (OSError, UnicodeError, SourcePatchError) as error:
            failures.append(str(error))
    if failures:
        raise SourcePatchError(failures[0])
    raise SourcePatchError("No matching Development Player compiler response exists for " + name + "; build this target's test Player first.")


def _reference_name(path):
    value = Path(path).stem
    return value[:-4] if value.lower().endswith(".ref") else value


def required_reference_names(project_root, assembly_name, profile_path, *, _verified_profile=None):
    """Return missing project-assembly candidates, never SDK/facade/package refs.

    A Unity response includes unused automatic references. The caller must also
    intersect these names with the actual baseline AssemblyRef metadata before
    requesting dependency MVIDs from the Player.
    """
    root, name, _, references, _, lines = _response_context(project_root, assembly_name, profile_path, _verified_profile)
    available = {reference.stem.casefold() for reference in references}
    missing = set()
    for line in lines:
        option, value = _option(line)
        if option not in ("r", "reference"):
            continue
        path = _path(root, value or "")
        normalized = path.as_posix().casefold()
        relative = path.relative_to(root).as_posix() if _within(path, root) else ""
        project_output = relative.startswith("Library/Bee/PlayerScriptAssemblies/") or (
            relative.startswith("Library/Bee/artifacts/") and "PDevDbg.dag/" in relative)
        candidate = _reference_name(path)
        if (project_output and "editor" not in candidate.casefold() and "/scriptassemblies/" not in normalized
                and candidate.casefold() not in available and candidate.casefold() != name.casefold()):
            if path.is_file():
                identity = _bridge().metadata_tool("inspect", path)
                candidate = _assembly_name(identity.get("assemblyName", ""))
                if candidate.casefold() in available or candidate.casefold() == name.casefold():
                    continue
            missing.add(candidate)
    return sorted(missing, key=str.casefold)


def _editor_data(lines, profile):
    candidates = []
    for line in lines:
        option, value = _option(line)
        if option not in ("r", "reference", "analyzer") or not value:
            continue
        normalized = _unquote(value).replace("\\", "/")
        match = re.match(r"(.+/(?:Editor/Data|Unity\.app/Contents))(?:/|$)", normalized)
        if match:
            candidates.append(Path(match[1]))
    for candidate in dict.fromkeys(candidates):
        # Unity Hub installs use <exact-version>/Editor/Data. Do not silently
        # switch to the newest installed SDK or an unrelated Editor compiler.
        if profile["unityVersion"] not in {path.name for path in candidate.parents}:
            continue
        compiler = candidate / "DotNetSdkRoslyn/csc.dll"
        for executable in ("dotnet.exe", "dotnet"):
            host = candidate / "NetCoreRuntime" / executable
            if host.is_file() and compiler.is_file():
                return host, compiler
    raise SourcePatchError("The exact Unity " + profile["unityVersion"] + " Player compiler is unavailable; that version's Editor contents are required.")


def _safe_reference(path):
    path = Path(path).resolve()
    normalized = path.as_posix().casefold()
    if (not path.is_file() or path.suffix.casefold() != ".dll"
            or "/library/scriptassemblies/" in normalized or "unityeditor" in path.name.casefold()
            or path.stem.casefold().endswith(".editor")):
        raise SourcePatchError("A verified runtime reference DLL is required: " + path.name)
    return path


def _contract_references(root, lines, profile):
    # 保留原 Player 的 NetStandard 编译契约，避免把 AssemblyRef 从
    # netstandard 改成 mscorlib，导致仅修改方法体也无法通过结构校验。
    host, _ = _editor_data(lines, profile)
    directory = host.parent.parent / "NetStandard"
    contracts = {}
    for line in lines:
        option, value = _option(line)
        if option not in ("r", "reference"):
            continue
        path = _path(root, value or "")
        if _within(path, directory):
            path = _safe_reference(path)
            key = path.stem.casefold()
            if key in contracts and contracts[key] != path:
                raise SourcePatchError("Duplicate SDK contract assembly: " + path.stem)
            contracts[key] = path
    return contracts


def contract_reference_names(project_root, assembly_name, profile_path, *, _verified_profile=None):
    """Return SDK contracts from this exact Player compiler response."""
    root, _, profile, _, _, lines = _response_context(project_root, assembly_name, profile_path, _verified_profile)
    return set(_contract_references(root, lines, profile))


def compile_candidate(project_root, assembly_name, profile_path, output_dir, extra_references=(), *, _verified_profile=None):
    """Compile saved sources using real Player references and a stable workspace.

    Extra references must be immutable baseline DLLs already matched to loaded
    dependency MVIDs by the caller. This function never substitutes Editor refs.
    """
    root, name, profile, references, response, lines = _response_context(project_root, assembly_name, profile_path, _verified_profile)
    output = Path(output_dir).resolve()
    if not _within(output, root) or not _within(output, root / ".runtimebridge"):
        raise SourcePatchError("Patch compiler output must stay inside this project's .runtimebridge directory.")
    workspace = output / "source-workspace"
    if not _within(workspace, output):
        raise SourcePatchError("The compiler workspace resolves outside its owned output directory.")
    source_lines = []
    for line in lines:
        option, _ = _option(line)
        if line.startswith("@"):
            raise SourcePatchError("Nested compiler response files are unsupported.")
        if option is None:
            path = _path(root, line)
            if path.suffix.casefold() != ".cs" or not path.is_file() or not _within(path, root):
                raise SourcePatchError("Every patch source must be a saved .cs file inside the project: " + _unquote(line))
            if _within(path, root / ".runtimebridge"):
                raise SourcePatchError("Generated patch workspace files cannot be used as project source inputs.")
            source_lines.append((line, path, path.relative_to(root)))
    if not source_lines:
        raise SourcePatchError("The Player compiler response has no saved C# sources.")
    host, compiler = _editor_data(lines, profile)
    contracts = _contract_references(root, lines, profile)
    by_name = {}
    for path in [*references, *extra_references]:
        path = _safe_reference(path)
        key = path.stem.casefold()
        if key == name.casefold():
            continue
        if key in by_name and by_name[key] != path:
            raise SourcePatchError("Duplicate reference assembly name: " + path.stem)
        by_name[key] = path
    if "mscorlib" not in by_name:
        raise SourcePatchError("The target profile has no mscorlib reference.")
    by_name.update(contracts)

    # Finish all input validation before creating output directories.
    for line in lines:
        option, value = _option(line)
        if option in ("recurse", "addmodule", "link", "resource", "res", "linkresource", "win32res", "win32icon", "win32manifest"):
            raise SourcePatchError("Unsupported source-patch compiler option: " + option)
        if option in ("target", "t") and _unquote(value or "").lower() != "library":
            raise SourcePatchError("Source patches must compile a library.")
        if option in ("analyzer", "analyzerconfig", "additionalfile", "additionalfiles", "ruleset"):
            source_path = _path(root, value or "")
            if not source_path.is_file() or "/library/scriptassemblies/" in source_path.as_posix().casefold():
                raise SourcePatchError("A compiler support file is missing or comes from Editor ScriptAssemblies: " + source_path.name)

    workspace.mkdir(parents=True, exist_ok=True)
    for _, path, relative in source_lines:
        destination = workspace / relative
        if not _within(destination, workspace):
            raise SourcePatchError("A mirrored source destination escapes the compiler workspace.")
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, destination)
    frozen_refs = output / "references"
    if not _within(frozen_refs, output):
        raise SourcePatchError("The reference snapshot resolves outside its owned output directory.")
    frozen_refs.mkdir(exist_ok=True)
    expected = {item["name"].casefold(): item["sha256"].lower() for item in profile["assemblyFingerprints"]}
    frozen = []
    for key, path in sorted(by_name.items()):
        destination = frozen_refs / (path.stem + ".dll")
        if not _within(destination, frozen_refs):
            raise SourcePatchError("A reference snapshot destination escapes the compiler output.")
        content = path.read_bytes()
        actual = hashlib.sha256(content).hexdigest()
        if key not in contracts and key in expected and actual != expected[key]:
            raise SourcePatchError("Reference bytes changed during patch compilation: " + path.name)
        # Validate the exact bytes being frozen instead of re-opening every DLL
        # three times; the exported profile already supplies the expected hash.
        destination.write_bytes(content)
        if hashlib.sha256(destination.read_bytes()).hexdigest() != actual:
            raise SourcePatchError("The frozen reference bytes changed before compilation: " + path.name)
        frozen.append(destination)

    rewritten = []
    source_map = {line: relative for line, _, relative in source_lines}
    for line in lines:
        option, value = _option(line)
        if line in source_map:
            rewritten.append('"' + source_map[line].as_posix() + '"')
        elif option in ("r", "reference"):
            continue
        elif option in ("out", "o"):
            rewritten.append('-out:"' + (output / (name + ".dll")).as_posix() + '"')
        elif option == "refout":
            rewritten.append('-refout:"' + (output / (name + ".ref.dll")).as_posix() + '"')
        elif option in ("pdb", "doc", "errorlog", "generatedfilesout"):
            target = output / {"pdb": name + ".pdb", "doc": name + ".xml", "errorlog": "compiler-errors.json", "generatedfilesout": "generated"}[option]
            rewritten.append('-' + option + ':"' + target.as_posix() + '"')
        elif option in ("analyzer", "analyzerconfig", "additionalfile", "additionalfiles", "ruleset"):
            support = _path(root, value or "")
            if option in ("additionalfile", "additionalfiles") and support.name.endswith(".UnityAdditionalFile.txt"):
                destination = output / "compiler-support" / support.name
                if not _within(destination, output):
                    raise SourcePatchError("A compiler-support destination escapes its owned output directory.")
                destination.parent.mkdir(exist_ok=True)
                content = support.read_text(encoding="utf-8-sig")
                content = content.replace(str(root), str(workspace)).replace(root.as_posix(), workspace.as_posix())
                destination.write_text(content, encoding="utf-8")
                support = destination
            rewritten.append('-' + option + ':"' + support.as_posix() + '"')
        else:
            rewritten.append(line)
    rewritten += ['-r:"' + path.as_posix() + '"' for path in frozen]
    response_out = output / "compile.rsp"
    response_out.write_text("\n".join(rewritten), encoding="utf-8")
    arguments = [str(host), "exec", str(compiler), "/nostdlib", "/noconfig", "@" + str(response_out)]
    # run_process has no shell interpolation. Stable paths preserve deterministic
    # candidate hashes for duplicate-patch detection.
    import subprocess
    try:
        result = subprocess.run(arguments, cwd=workspace, capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=180)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise SourcePatchError("The Player compiler could not complete: " + type(error).__name__) from error
    if result.returncode:
        raise SourcePatchError((result.stdout + "\n" + result.stderr).strip()[-12000:])
    candidate = output / (name + ".dll")
    if not candidate.is_file():
        raise SourcePatchError("The Player compiler did not produce the requested patch DLL.")
    return candidate


def find_baseline(project_root, profile_path, assembly_name, mvid, metadata_inspect_callback, *, _verified_profile=None):
    """Find the exact loaded PE identity, then freeze it under the project cache."""
    root = Path(project_root).resolve()
    name = _assembly_name(assembly_name)
    profile_path = Path(profile_path).resolve()
    profile, _, _ = _profile(profile_path, _verified_profile)
    wanted = _key(mvid)
    if not wanted:
        raise SourcePatchError("A nonzero loaded assembly MVID is required to select its baseline.")
    candidates = []
    for directory in (profile_path.parent / "patch-baselines", root / ".runtimebridge/baseline"):
        if directory.is_dir():
            candidates.extend(directory.rglob("*.dll"))
            candidates.extend(directory.rglob("*.dll.bytes"))
    assets = root / "Assets"
    if assets.is_dir():
        candidates.extend(assets.rglob(name + ".dll.bytes"))
    hotfix = root / "HybridCLRData/HotUpdateDlls" / profile["target"]
    if hotfix.is_dir():
        candidates.extend(hotfix.rglob(name + ".dll"))
    cache = root / ".runtimebridge/source-patches/baselines" / wanted / (name + ".dll")
    if cache.is_file():
        candidates.append(cache)
    matches = {}
    for candidate in dict.fromkeys(path.resolve() for path in candidates):
        if not candidate.is_file() or "/library/scriptassemblies/" in candidate.as_posix().casefold():
            continue
        try:
            identity = metadata_inspect_callback(candidate)
        except Exception:
            continue
        if not isinstance(identity, dict) or identity.get("assemblyName") != name or _key(identity.get("mvid")) != wanted:
            continue
        digest = hashlib.sha256(candidate.read_bytes()).hexdigest()
        if identity.get("sha256") and identity["sha256"].lower() != digest:
            raise SourcePatchError("Baseline bytes changed during identity inspection.")
        matches.setdefault(digest, candidate)
    if not matches:
        raise SourcePatchError("No original Player DLL matches " + name + " / MVID " + str(mvid)
                               + ". Rebuild the test Player with a recorded baseline; do not substitute Editor DLLs.")
    if len(matches) != 1:
        raise SourcePatchError("Several different DLL contents claim the same loaded baseline MVID; baseline selection is ambiguous.")
    digest, source = next(iter(matches.items()))
    if not _within(cache, root):
        raise SourcePatchError("The baseline cache resolves outside the project.")
    cache.parent.mkdir(parents=True, exist_ok=True)
    if cache.is_file() and hashlib.sha256(cache.read_bytes()).hexdigest() != digest:
        raise SourcePatchError("The frozen loaded baseline has changed; refusing to overwrite it.")
    if source != cache:
        shutil.copyfile(source, cache)
    frozen = metadata_inspect_callback(cache)
    if (frozen.get("assemblyName") != name or _key(frozen.get("mvid")) != wanted
            or hashlib.sha256(cache.read_bytes()).hexdigest() != digest):
        raise SourcePatchError("The frozen baseline did not retain the inspected Player identity.")
    return cache
