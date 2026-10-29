# AI Bridge Runtime

独立的 Unity 真机代码调试插件。仅安装 `com.sh.aibridge.runtime` 即可；不依赖 `com.sh.aibridge` Editor 自动化插件。手机运行测试包后，电脑可在 **Unity Editor 完全关闭**的情况下执行 C#、读取日志，或通过 HybridCLR 商业版修复已加载方法。

## 两种代码操作

| 操作 | 执行方式 | 边界 |
| --- | --- | --- |
| `exec` | 电脑编译独立调试 DLL，手机 `Assembly.Load` 后调用入口 | 可查询、调用和修改当前对象；不会自动替换已有方法 |
| `patch` | 电脑检查补丁结构，手机调用 `HybridCLR.RuntimeApi.HotfixAssembly` | 仅修复热更新/DHE 程序集中的方法体，拒绝类型结构变化 |

增删字段、方法、类型或修改签名，继续使用项目原有完整 DHE 更新流程，并安排重启游戏后生效。补丁不会自动迁移对象状态，也不承诺正在执行的调用和异步任务立即切换。

## 安装与首次出包

在 Unity Package Manager 中通过 **Add package from tarball** 安装发布的 `.tgz` 包；本地开发也可以通过 **Add package from disk** 选择独立插件仓库根目录的 `package.json`。安装来源不依赖任何示例工程。

1. 仅安装 Runtime 的 UPM 包。Editor 自动化包可保持未安装状态。
2. 执行 `Window > AI Bridge Runtime > Prepare Current Project`，或在 Settings 中点击 **准备当前项目**。插件创建自己的 `Assets/Resources/AIBridgeRuntimeConfig.asset` 并启用开发包自动启动，不添加项目 C#、场景组件或业务回调。
3. 根据需要调整插件配置。默认 HTTP 为 `127.0.0.1:27182`、LAN 发现关闭，会话 Token 自动生成。无需复制示例工程的构建、加载或基线脚本。
4. 使用项目原有流程构建 **Development Build**。Mono Player 可以直接执行调试 DLL；IL2CPP 动态代码执行需要匹配的 HybridCLR 原生运行时；方法热修复还需要商业版 `HotfixAssembly`。插件读取现有解释器/DHE 配置，不替项目增删热更程序集或接管业务加载流程。
5. 开发构建会自动准备受支持的本地 HybridCLR 模块身份读取支持、保存原始 Player DLL，并导出 `.runtimebridge/profiles/<PlayerBuildGUID>/profile.json`。后台构建通过一次性 Editor update 发布 profile，不依赖 Inspector 刷新。
6. 在 Runtime 窗口连接设备、选择目标程序集。后续保存源码并点击 **编译并应用**，DLL、基线、清单和补丁 ID 自动管理。

CI 可直接调用 `-executeMethod AIBridge.Runtime.Editor.AIBridgeRuntimeProjectIntegration.PrepareCurrentProject` 准备插件配置，无需新增项目 Editor 脚本。正式发布包请关闭 AutoStart；插件不会在发布 Player 中启动调试服务。

构建配置和引用导出是首次出包/更新测试包时的步骤；后续连接、执行和发送补丁不启动 Editor。引用导出失败会明确报错，不会使用 `Library/ScriptAssemblies` 替代真机引用。

启用 AutoStart 后，Development Player 中已有的旧 Runtime 场景组件也会读取这份独立配置，场景资产不会被改写。项目显式调用 `AIBridgeRuntimeBootstrap.Start(settings)` 时，以传入的设置为准。Editor Play Mode 可用场景组件做协议检查，但不会自动启动真机 Hotfix。

手机执行脚本无需携带 Roslyn 编译器。电脑端编译和补丁检查使用 Python 3 标准库与本机 .NET SDK；无需额外 Python 包或 NuGet 包。建议安装 .NET SDK 8 或更新版本，同时提供 .NET 8 运行时供元数据检查工具运行。

### IL2CPP 运行时准备

Development 构建会额外保留 C# 片段包装器使用的 async builder、awaiter 和 Task 成员，避免目标引用被裁剪后出现 `CS0656: AsyncTaskMethodBuilder<T>.AwaitOnCompleted`。发布构建不使用这份额外保留文件。

基础场景调试还保留 `GameObject`、`PrimitiveType`、`Transform`、`Light`、Renderer/Material 及创建基础几何体所需的 MeshFilter、MeshRenderer、BoxCollider 等类型，支持 `CreatePrimitive`、`LookAt` 和灯光属性设置。其他业务类型、成员和资源仍由项目负责保留与打包。片段编译以实际 Player 引用为准；主包已经裁掉的 API 不能通过改用 Editor DLL 或补充元数据来恢复，必须调整保留规则后重新构建。

插件接入不要求业务项目额外编写 AOT 元数据初始化代码。其 async/Task 调试入口所需成员由包内开发构建保留规则提供；空场景验收没有执行示例的 AOT 元数据加载代码。调用项目业务泛型 API 时，仍受项目实际保留和已加载的 AOT 元数据范围约束。补充元数据不能恢复已被裁掉的 API，也不能替代精确 Player profile。

方法热修复需要真实的 `ManifestModule.ModuleVersionId`。包内构建兼容层支持从已加载解释器 PE 读取 MVID；原始 DHE 则使用本轮 Player 编译产物生成并编进原生包的身份索引，仅对已加载且被原生运行时认定为 DHE 的 image 生效。已经加载差分 DLL 的 DHE 使用该实际 image 的 MVID，不会用原始索引冒充当前身份。普通 AOT 不会获得 DHE 的回退身份，也不会被自动转换成热更程序集。

原生兼容层只操作项目本地 HybridCLR 工具链，修改前校验实现并按哈希备份到 `.runtimebridge/native-support/backups/`。未知源码布局、共享/全局工具链或缺失的能力会明确提示并保留项目原来的工具链；不能把所有原生版本和平台都当成已经验收。关闭调试或重新构建时会清除旧 DHE 索引，避免把旧 MVID 带入新包。

源码编译按 profile 绑定 Android、iOS、Windows、Linux、macOS 的平台宏，并识别 Windows/Linux 的 Editor/Data 与 macOS 的 Unity.app/Contents 编译器。当前真实设备验收覆盖 Unity 6000.3.6f1、Windows 构建主机、Android ARM64 和商业 HybridCLR；其他平台的参数处理有主机回归检查，尚未做设备验收。

## Runtime 管理窗口

打开 **Window > AI Bridge Runtime > Runtime**。旧入口 **Window > AIBridge > Runtime** 和 **Players** 也可使用。Settings 窗口的“打开 Runtime 管理窗口”按钮会打开同一界面。

| 页签 | 功能 |
| --- | --- |
| 设备连接 | ADB 设备列表、USB 转发、手动 LAN 发现、HTTP 连接状态、场景 Runtime 配置 |
| 执行代码 | 选择对应 Player profile 后编译 C# 片段，或直接发送已有 DLL |
| 方法热修复 | 保存目标程序集，编译当前源码并直接应用到真机；高级选项可手动应用已有 DLL |
| 日志 | 按类型和条数读取手机日志 |

窗口使用本包独立桌面 CLI，Token 仅保存在窗口内存中并以密码字段显示。连接、编译与执行异步进行，不阻塞 Unity 界面。关闭窗口不会撤回已发往手机的请求。Scene Runtime 按钮仅在用户点击时修改当前场景，支持 Undo，不自动保存场景。

这个窗口是可选的图形界面；关闭 Unity Editor 后，仍可继续使用下述 CLI 连接手机。

窗口使用工作台布局：左侧切换连接、执行、热修复和日志，顶部持续显示当前目标；Python、ADB、Token、端口等参数默认收在高级连接设置中。连接成功后按 profile 文件中的真实 Build ID 与平台匹配构建引用，CLI 仍会独立检查全部指纹。代码支持 Ctrl/Cmd+Enter 执行，示例按钮只填入代码。结果优先展示返回值（包含 `0`、`false` 和 `null`），完整 JSON 可展开或复制；日志以可读条目展示并保留堆栈。

## Android USB 连接

在项目根目录的 PowerShell 中：

```powershell
$tool = 'Packages/com.sh.aibridge.runtime/Tools~/runtimebridge.py'
python $tool device
# 多设备时：python $tool device --serial <adb-serial>
python $tool devices
# 仅在手机配置已启用 LAN 时手动发现
python $tool discover --udp-port 27183 --timeout 3

# 使用 Settings 中显示的当前测试包 Token；不要把 Token 提交到仓库
$env:RUNTIMEBRIDGE_TOKEN = '<session-token>'
python $tool status
python $tool logs --log-type Error --count 100
```

`device` 等价于 `adb forward tcp:27182 tcp:27182`。它只建立端口转发，不安装、启动或重启游戏。先启动手机测试包，再确认 `status` 中的设备、游戏版本、平台和就绪状态。其他端口使用 `device --port 27182`；HTTP 地址可用各命令的 `--url` 或 `RUNTIMEBRIDGE_URL` 环境变量指定。

若在独立仓库根目录运行，改用 `$tool = 'Tools~/runtimebridge.py'`；通过 Git URL 安装时，CLI 位于该包实际解析目录的 `Tools~` 下。

命令输出 JSON，失败使用非零退出码；异步 HTTP 响应会轮询结果，客户端不会因超时自动重发补丁。帮助使用 `python $tool --help`。

## 手机执行临时代码

直接发送已编译 DLL：

```powershell
python $tool exec --dll '.runtimebridge/probes/InspectPlayer.dll' `
    --entry-type 'InspectPlayer' --method-name 'Execute'
```

使用当前测试包导出的 profile 编译并发送 C#：

```powershell
python $tool exec --profile '.runtimebridge/profiles/<build-guid>/profile.json' `
    --code 'return UnityEngine.Application.platform.ToString();'

python $tool exec --profile '.runtimebridge/profiles/<build-guid>/profile.json' `
    --file '.runtimebridge/probes/player.csx' --save-dll '.runtimebridge/probes/player.dll'
```

`--file` 是代码片段，允许开头的 `using` 指令、`return` 和 `await`。编译器生成 `CodeExecutor.Execute` 返回 `Task<object>`；异常由手机 Runtime 捕获并返回。`--profile` 必填且必须来自对应 Player，工具核对每个引用 DLL 的 SHA256 与 profile 中 `assemblyFingerprints` 一一对应，再查询手机 `status` 确认 buildId 和平台一致，之后才编译、发送。引用被替换或缺少指纹、缺少 Player buildId、版本不匹配、Editor profile、`UNITY_EDITOR` 宏和 Editor DLL 路径都会被拒绝。只有 `--dll` 模式无需本地编译；也可给 `--dll` 附加 `--profile`，执行相同版本绑定检查。

重复操作建议保存并复用固定 DLL。每次新编译都会产生新程序集，加载到进程后不能通过清理字典卸载；必要时重启测试包释放程序集内存。

## 实时替换已有方法

常用流程：连接测试 Player，在“方法热修复”中选一次目标程序集，之后保存 C# 并点击 **编译并应用**。编译完成会自动把最新方法补丁发送到当前连接的真机，无需重新选择 DLL、基线或清单。窗口会按项目记住目标程序集，并在 Unity 脚本重载后恢复原有连接、匹配当前 Player 的构建 profile。高级手动区域也会同步显示本次生成的 DLL 和清单。

自动流程使用该 Android Development Player 的编译参数、对应 Unity 版本的 NetStandard 编译契约以及已核对的运行时引用，保留源生成器需要的源码相对路径。工具根据设备上已加载程序集的真实 MVID 查找原始基线，检查结构差异、生成方法清单和补丁 ID 后再发送。重复应用相同代码会去重；将方法体改回基线后再次应用也能恢复旧方法体。

这仍是当前进程中的方法热修复，仅支持兼容的方法体变化。重启自动应用需要单独的设备缓存流程。

先对真实业务程序集做完整编译，保留原程序集名及所有类型/成员结构。不要把替换方法包装成另一个临时调试程序集。例：只将 `Game.Combat.DamageCalculator.Calculate(Int32)` 的方法体从旧逻辑改成新逻辑。

补丁清单 `patch.json`：

```json
{
  "patchId": "damage-002",
  "assemblyName": "Game.Hotfix",
  "baselineDll": "baseline/Game.Hotfix.dll",
  "types": [
    {
      "name": "Game.Combat.DamageCalculator",
      "methods": [{ "signature": "Int32 Calculate(Int32)" }]
    }
  ]
}
```

`baselineDll` 相对于清单文件，必须使用目标 Player 当前加载版本对应的基线 DLL。工具从基线提取 MVID；也可显式提供 `baselineMvid`，并验证其一致性。`signature` 使用 `MethodInfo.ToString()` 格式，必须精确匹配；同名重载不会被批量猜测。构造函数使用 `.ctor(Int32)`，静态构造函数使用 `.cctor()`。

```powershell
python $tool patch-capabilities
python $tool patch --dll '.runtimebridge/patches/Game.Hotfix.dll' `
    --manifest '.runtimebridge/patches/patch.json'
python $tool patch-history
```

发送前，工具通过独立元数据读取器比较基线和候选 DLL：程序集标识、类型、字段、方法、泛型约束、属性、事件、布局及编译器生成类型等定义必须兼容。类型和成员的实际元数据 Token 也必须一致，禁止通过调整类型/方法声明顺序而改变全局 MethodDef Token。仅方法体变化可以通过。比较使用 `System.Reflection.Metadata`，不会把候选 DLL 加载进电脑进程执行。缺少有效基线或结构不兼容时不发送请求。

手机再检查载荷 SHA256、当前目标程序集 MVID和所有方法签名。`structureVerified` 是受信任电脑端完成结构检查的声明，**不是手机独立证明**。调试 Token 持有者有代码执行权限；不要将这条调试链路当作生产补丁发布的签名验证系统。

Runtime 在目标热更新程序集加载前尝试关闭 HybridCLR 方法内联，并限制累计补丁次数。达到上限后应重启测试包；重复发送同一已应用补丁会去重。补丁历史不等于状态回滚，旧程序集内存也不会自动释放。可重新编译并应用旧方法体，但已经改变的业务状态需要单独处理。

### 商业版加固 DLL

桌面工具不内置商业版密钥，也不调用 Unity Editor 加固。使用现有受信任加固流程，在电脑上对候选 DLL 加固，并确保加固配置与目标主包匹配。

- `exec --dll` 可以直接发送外部加固后的 DLL；手机 HybridCLR 负责加载。
- `patch` 加固模式同时需要未加固候选 `--validation-dll`，用于真实结构检查。
- 清单增加 `encryptedFromSha256`（未加固候选的 SHA256）和 `encryptedSha256`（最终发送字节的 SHA256），由受信任加固流程记录。

```powershell
python $tool patch --dll '.runtimebridge/patches/Game.Hotfix.encrypted.bytes' `
    --validation-dll '.runtimebridge/patches/Game.Hotfix.dll' `
    --manifest '.runtimebridge/patches/patch.json'
```

哈希绑定可防止选错文件，不构成加密转换的密码学证明。只有原始加固 DLL、没有明文对应物及来源绑定时，工具拒绝执行结构补丁检查。

## 验证边界

桌面 HTTP 协议、代码包装、目标 profile 限制和 DLL 元数据兼容性有自动回归检查：

```powershell
python -m unittest discover -s 'Packages/com.sh.aibridge.runtime/Tools~/tests' -v
```

这些检查不等同于 Android IL2CPP 验收。`HotfixAssembly` 的 Editor 实现为空，因此 Editor 中不会把补丁调用当成真机修复成功。首次验收前须安装与项目匹配的 HybridCLR 原生运行时并成功构建测试 Player；真机方法替换必须以真实 Player 的前后行为为准。具体证据范围见 [VALIDATION.md](VALIDATION.md)，来源及许可见 [NOTICE.md](NOTICE.md) 与 [LICENSE](LICENSE)。

## 通用真机工具接口

新增接口随 Runtime 自动注册。首次使用需更新一次 Development Player；后续对象检查、修改和 Profiler 会话无需重新打包。

| 命令 | 能力 |
| --- | --- |
| runtime.hierarchy | 按名称查询场景对象，offset/limit 分页，返回绑定对象的 objectToken |
| runtime.inspect | 字段、组件、Transform、方法签名和属性列表；不自动执行 getter |
| runtime.member.get/set | 明确读取或修改公共成员；支持标量、枚举、Vector2/3、Quaternion、Color |
| runtime.method.invoke | 按完整公共实例方法签名调用；异步返回对象不代表任务完成 |
| runtime.assemblies | 加载版本、MVID、读取失败原因；不猜测每个方法的执行模式 |
| runtime.profiler.start/read/stop | 指定已注册 Marker 的原始样本，自动停止并可释放 |

对象修改、属性 getter 和方法调用要求 riskAccepted=true，并遵守 enableRuntimeCodeExecution。objectToken 随对象销毁或服务重启失效，不把可复用的 instanceId 当作修改凭据。
Profiler 上限为 8 个会话、每个 4096 个环形缓冲样本、1–60 秒自动停止；stop 默认释放。服务停止时释放所有 Recorder。此接口不提供完整 CPU 调用树或 GPU 时间线。

CLI 示例：
- python Tools~/runtimebridge.py call runtime.hierarchy --params '{"query":"Camera","limit":20}'
- python Tools~/runtimebridge.py call runtime.profiler.start --params '{"marker":"AI.MyMarker","durationSeconds":10,"capacity":600}'

### MCP 接入

Tools~/mcp_runtime.py 提供标准 JSON-RPC stdio MCP 服务，包含上述工具、一次性 C# 和源码热更新。
客户端的 command 配置为 python，args 为插件绝对路径下的 Tools~/mcp_runtime.py，环境变量：
- RUNTIMEBRIDGE_URL：连接地址，默认 http://127.0.0.1:27182。
- RUNTIMEBRIDGE_TOKEN：当前测试包 Token；不写入输出。
- RUNTIMEBRIDGE_PROJECT：Unity 工程绝对路径。
- RUNTIMEBRIDGE_PROFILE：可选；省略时按当前设备 Build ID 自动定位。

先建立 ADB 转发并调用 runtime_status。每个 MCP 实例绑定一个设备地址。
runtime_execute 与 runtime_patch_source 自动验证 profile；runtime_diagnose 组合设备能力、程序集身份与本地基线记录。没有匹配基线时仍拒绝更新。
复杂对象操作或需要等待异步任务时使用 runtime_execute。
Profiler 先注册/执行标记，再开始采集；最终 stop 释放会话。DHE 打点热修复可能改变原 AOT 方法的执行方式，报告必须标明。
同源、同编译器、同引用的一次性代码生成相同 DLL，复用设备缓存。

版本变更记录见 [CHANGELOG.md](CHANGELOG.md)。
