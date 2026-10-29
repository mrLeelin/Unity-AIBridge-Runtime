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

The desktop metadata tests reject shifted metadata tokens, changed references and incompatible member structure. The handler tests above use a simulated HybridCLR API. The additional Android Player verification below exercises native method replacement and does not replace these host checks.

## Android Player verification: 2026-10-28

The consuming `LFrameworkAIBridge` project built an ARM64 IL2CPP Development APK with Unity 6000.3.6f1 and HybridCLR 8.11.1, installed it in MuMu Android 12, and passed all 21 end-to-end checks. This is an Android emulator Player run, not an Editor simulation or a physical-phone test.

- Successful build: zero build errors; build GUID `bf7738eb30b04b45bf8afd24eea21645`.
- The package automatically exported the matching Player profile after final build success. The test did not call the explicit-export fallback.
- The package's Development-only linker processor retained async compiler support. The consuming fixture no longer supplied duplicate compiler-preservation rules.
- C# execution returned `42`; a snippet containing `await Task.Delay(100)` returned `43`.
- The loaded interpreter assembly reported the packaged baseline MVID. An intentionally incorrect MVID was rejected on the Player before any native patch attempt.
- A valid method-body patch changed the existing object's return value from `100` to `200`, with the same process PID `4778`, the same fixture session and baseline MVID, and a call count increasing from `12` to `19`.
- Repeating the patch was deduplicated; history recorded one native attempt. Captured runtime logs contained no Error/Exception/Assert entries. Before/after screenshots were visually checked.
- The Player profile/linker lifecycle suite passed 21 additional host checks, including deferred final-result publication, captured-reference isolation, domain-reload recovery, fingerprint tampering and release-build exclusion.

Evidence is retained in the consuming project's `.runtimebridge/evidence/mumu-final/` and `.runtimebridge/evidence/completion.json`; the APK is `.runtimebridge/builds/AIBridgeSmoke.apk`. Its SHA256 is `5840455DB49BB1AAE458FB2A630D160B741F4C276F6E12F3B46FF4B7B98A772E`.

The consuming project supplies matching AOT metadata and a project-local native reflection patch for interpreter `ManifestModule.ModuleVersionId`. That patch reads the GUID from the loaded original PE and keeps the baseline guard intact. It is retained under `Tools/AIBridgeSmoke/native` in the consuming project, not silently installed by this package. This run validates a loaded **interpreter hot-update assembly**; DHE/AOT target assemblies, other Unity/runtime versions and physical phones still require their own validation.

Run the host checks from the repository root:

```powershell
python -m unittest discover -s 'Tools~/tests' -v
dotnet run --project 'Runtime/Hotfix/Tests~/HotfixRegression.csproj' --verbosity quiet
dotnet run --project 'Editor/Tests~/RuntimeWindowBackend.Tests.csproj' --verbosity quiet
dotnet run --project 'Editor/BuildProfileTests~/BuildProfile.Tests.csproj' --verbosity quiet
```


## 2026-10-28：源码编译后直接应用到 Android

- Runtime Console 的“编译并应用”已在连接的 Android 测试 Player 完成编译、基线识别、方法绑定及应用，没有重新选择 DLL/清单。
- 原对象 `ReadValue` 从 100 变为 666，Session 保持 `091ac5db26044f7195f5db51c7c55c30`，Calls 从 12440 增至 12717。
- 目标程序集按项目保存；新建窗口实例可恢复 `BridgeSmoke.Hotfix`。高级手动区域同步本次 DLL 和清单。
- Unity 6000.3.6f1 编译通过；Editor Play Mode 启停检查没有新增错误，结束后恢复 Edit Mode。
- Python 回归：69 项检查，68 项通过、1 项因 Windows 不允许创建符号链接而跳过。
- 修复元数据工具缓存构建重复包含 obj 源文件的问题，并保留精确 Unity Player RSP 的 NetStandard 编译契约，避免 AssemblyRef 改变。
- 证据在测试项目 `.runtimebridge/evidence/no-reselect/`。本次不覆盖物理手机专项验证及重启自动恢复补丁。


## 2026-10-28：Runtime AOT 身份探针与异常返回

- 复现 `BridgeSmoke.Runtime` 更新卡在 `runtime.code.execute`，设备日志出现 `Couldn't extract exception string ... NotSupportedException`。
- 最小探针捕获原始原因：当前 Player 的 `ModuleVersionId` 仅支持解释器 image，不提供这个 AOT image 的原始 MVID。
- 身份探针以数据返回预期错误，电脑端在编译及补丁应用前给出明确提示；已在原测试 APK 中验证不再超时，解释器目标的现有更新流程仍正常。
- Runtime 异常返回对 StackTrace 读取增加兜底；对应源码已通过 Unity 编译，旧 APK 需要下次重新构建后才包含通用兜底。
- Python 检查 70 项（69 通过、1 项符号链接能力不足而跳过），Runtime 回归 30 项全部通过。新增 AOT 错误提示和 StackTrace 二次异常用例均先复现失败，再验证修复通过。
- `BridgeSmoke.Hotfix` 在测试构建脚本 `ApplySmokeAssemblySettings` 中自动加入 hotUpdateAssemblies，运行时从 Resources 动态 Assembly.Load；当前项目设置不能替代已安装 APK 的构建时设置。
- 本次未把普通 AOT Runtime 转为可热修复程序集；启用该能力仍需 DHE/解释器构建及精确基线身份支持。


## 1.1.0 通用零项目代码接入（2026-10-28）

- 新增 Prepare Current Project 菜单和公开 batchmode 入口，仅创建插件配置；启动、预处理、原生兼容、构建引用和基线归档由包内代码完成。项目现有热更列表及业务加载流程不由插件改写。
- 干净 Unity 6000.3.6f1 工程仅依赖打包后的 com.sh.aibridge.runtime；无 LFramework、无 HybridCLR、Assets 下 C# 文件数为 0。首次验收发现遗漏物理、截图和图像编码模块依赖，补齐 package.json 后批处理导入、编译、准备项目退出码为 0。
- 原始 DHE 验收：SampleGame.Logic 名称不含 Hotfix，业务类不含 Bridge 初始化、基线或更新代码。正常构建回调自动准备原生身份索引；原对象 41 -> 43，句柄及 MVID 不变，Calls 1 -> 2。
- 零业务启动场景验收：场景仅有相机，没有 SmokeFixture、手写 PrepareRuntime 或 AOT 元数据加载。Player Build GUID 4e27f217f6a6443c90f440946eb0eddc；插件自行启动，原始 DHE 对象 43 -> 45，句柄 129715545151472 与 MVID 7c564491-63f6-49e8-a98a-248e9610b682 保持一致，Calls 1 -> 2。
- 真实设备路径为 Windows 构建主机、Android ARM64/MuMu、商业 HybridCLR/Unity 6000.3.6f1。iOS、Windows、Linux、macOS 平台宏绑定与 macOS 编译器路径由主机回归覆盖，未做这些平台的设备验收。
- 72 项 Python 检查（71 通过、1 项 Windows 符号链接能力不足而跳过）；30 项 Runtime 检查、22 项构建 profile 生命周期检查、15 项原生兼容检查通过。原生检查包含去掉已有手工 reader 的真实 SDK 结构、原有自定义实现保护、全局路径拒绝与关闭调试后清空旧身份。
- 构建后自动发布改用一次性 Editor update，解决后台 Inspector 不刷新导致 profile 长期留在 pending 的问题，真实构建已验证自动发布。
- 原生适配限定项目本地工具链；解释器读取实际 PE MVID，原始 DHE 只使用本轮编译输入的索引且检查原生 DHE 类型及已加载状态。普通 AOT 不会借用 DHE 身份。未知原生实现保留原状并明确报告能力边界。
- 验收临时 Assets 已移除，测试工程 HybridCLR 列表已恢复；JellybeanUnity 仅只读核对，未修改其业务代码或项目配置，未宣称其完整设备包已验收。证据位于测试工程 .runtimebridge/evidence/portable-integration/。

- 最后主场景 Play Mode 检查观察到 LFramework.Runtime.DefaultTableHelper.Start 第 52 行的空引用（LFrameworkAspect.Instance.Get<ResourceComponent>()）；已退出 Play Mode，未修改框架代码，未把主场景运行标为无错误。这与独立干净工程安装和已成功的无业务启动脚本 Android 空场景验收分别记录。

- 原生自动适配失败时只保留现有工具链并报告能力；已有生成索引如果无法安全清空，则停止构建，避免复用旧包的身份。


## 1.2.0 通用真机工具（2026-10-28）

- 自动注册对象 Hierarchy/Inspector、公共属性读写、精确方法调用、程序集身份诊断、Profiler start/read/stop 会话。业务项目无需新增接入代码。
- 对象使用绑定实际 Unity 对象的随机句柄；Editor 实验验证发现、字段/组件检查、属性写入、SetActive 调用、缺少风险确认的拒绝及销毁后的旧句柄拒绝。
- Android ARM64/MuMu 验收包 Build ID 2e4f627767b5477e86f7b4f3b6752436。通过 MCP 调度层及真实 HTTP 读取对象，Transform.localPosition 写入 1/2/3 后读回一致，SetActive 生效并恢复；读取 83 个程序集诊断项。
- 在设备上先注册 Marker，再通过新会话接口采集，得到 4 个非零样本：6441388、6119810、5926503、6047885 ns。停止接口释放 Recorder；Editor 中另验证到期停止和释放后拒绝旧 sessionId。
- MCP stdio 握手和 tools/list 已用实际子进程验证，共 16 个工具；7 项 MCP 回归通过。完整 Python 检查 79 项，78 通过、1 项 Windows 符号链接限制跳过。
- 同源、同编译器、同引用的重复代码实测生成相同 DLL，允许 Runtime 复用程序集缓存；不同代码仍受原有加载上限约束。
- 新增 Profiler 类型开发构建保留规则。返回的是指定 Marker/计数器原始样本，不含完整 CPU 调用树或 GPU Timeline；程序集执行模式未知时明确标记 unknown，不将 MVID 可读冒充可热修复。
- 首次使用这些新 Runtime 接口需要更新 Development Player。当前旧 runtime.perf 的间隔采样统计问题尚未在本次修改中重写；新接口直接返回 ProfilerRecorder 环形缓冲样本。
- 构建遇到 Demo DHE 自动配置与空验收场景不匹配，使用临时验收配置完成构建后恢复 LFramework.Hotfix 设置，并删除临时场景及 link.xml。原工作场景保持原样。证据在 .runtimebridge/evidence/runtime-tools/。
- 1.2.0 验收安装包未包含独立的 Editor/AIBridgeRuntimeWindow.cs 调试 DLL 下拉框改动；该改动不属于本次版本发布范围。


## 1.2.1 Release 自动剥离（2026-10-29）

- Unity 6000.3.6f1 编译检查通过。Windows Mono Development 和 Release Player 实际构建成功；Release 包没有 Runtime DLL、配置资产或 MonoScript 名称记录，Player 运行后没有启动 Bridge。
- 开发包实际运行并上报 ready=true，配置资产仅在开发包中预加载。自定义 BuildOptions 与 Editor 开发开关不一致的 Release 构建也通过；成功和预处理失败后均恢复原预加载资源与开关。
- 使用真实 LZMA 压缩 Bundle 验证：普通 Bundle 放行；原始压缩字节不含明文标记、解压后包含 Runtime 标记的 Bundle 被拒绝。将其改名放入 StreamingAssets 后，Release 构建在预处理阶段失败。
- 91 项主机回归通过：构建 profile/linker 生命周期 22 项、原生安装/恢复 29 项、最终产物检测 24 项、StreamingAssets 检测 16 项。临时场景 Play Mode 中 Runtime 与独立场景对象正常存在；验收资源已清理。
- Mono 验收期间临时关闭项目 HybridCLR 构建钩子，避免第三方钩子强制切换后端；验收后恢复原 Android 目标、开发开关、HybridCLR 开关、Windows Mono 后端与预加载资源。本次未重新构建 Android/iOS IL2CPP Player，也未重新验证原生 Hotfix 执行；这些平台的 Release 剥离仍需对应 Player 验收。
- 测试项目证据：.runtimebridge/verification/release-exclusion-results.json。

```powershell
dotnet run --project 'Editor/BuildProfileTests~/BuildProfile.Tests.csproj'
dotnet run --project 'Editor/NativeSupportTests~/NativeSupport.Tests.csproj' -- 'Tools~/Native'
dotnet run --project 'Editor/ReleaseValidationTests~/ReleaseValidation.Tests.csproj'
dotnet run --project 'Editor/StreamingAssetsValidationTests~/StreamingAssetsValidation.Tests.csproj'
```
