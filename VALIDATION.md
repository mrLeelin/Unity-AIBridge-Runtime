# Validation status

Validated on 2026-10-28 with Unity 6000.3.6f1 and the installed .NET SDKs:

- Runtime-only Unity compilation: passed, zero compiler errors.
- The Editor automation package can be absent; only Runtime and its build/configuration assembly remain loaded.
- Standalone desktop client to Play Mode Runtime over HTTP: a compiled probe returned `42`.
- Desktop client, discovery and metadata validation: 21 regression tests passed.
- Runtime window backend: 13 host checks passed, including quoting, Token redaction, temporary snippet cleanup and local-command isolation.
- Runtime Console opened and visually inspected in Unity. Its asynchronous connection and DLL execution path returned `42`; leaving Play Mode cleared the Editor target while preserving the dirty scene.
- Hotfix handler: 28 host regression checks passed, including Editor no-op rejection, hash/MVID/method preflight, exact constructor signatures, deduplication and attempt limits.
- Android Development conditional code: C# type checking passed against Unity runtime references. This is not an Android Player build.

The desktop metadata tests reject shifted metadata tokens, changed references and incompatible member structure. The handler tests use a simulated HybridCLR API. Android IL2CPP native method replacement has **not** been verified. Verify the installed HybridCLR native runtime, target build profiles, early preparation order, and actual before/after method behavior on a Development Player before treating this package as device-validated.

Run the host checks from the repository root:

```powershell
python -m unittest discover -s 'Tools~/tests' -v
dotnet run --project 'Runtime/Hotfix/Tests~/HotfixRegression.csproj' --verbosity quiet
dotnet run --project 'Editor/Tests~/RuntimeWindowBackend.Tests.csproj' --verbosity quiet
```
