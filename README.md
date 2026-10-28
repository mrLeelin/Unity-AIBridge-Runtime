# AI Bridge Runtime

独立的 Unity 真机代码调试插件。仅安装 `com.sh.aibridge.runtime` 即可；不依赖 `com.sh.aibridge` Editor 自动化插件。手机运行测试包后，电脑可在 **Unity Editor 完全关闭**的情况下执行 C#、读取日志，或通过 HybridCLR 商业版修复已加载方法。

## 两种代码操作

| 操作 | 执行方式 | 边界 |
| --- | --- | --- |
| `exec` | 电脑编译独立调试 DLL，手机 `Assembly.Load` 后调用入口 | 可查询、调用和修改当前对象；不会自动替换已有方法 |
| `patch` | 电脑检查补丁结构，手机调用 `HybridCLR.RuntimeApi.HotfixAssembly` | 仅修复热更新/DHE 程序集中的方法体，拒绝类型结构变化 |

增删字段、方法、类型或修改签名，继续使用项目原有完整 DHE 更新流程，并安排重启游戏后生效。补丁不会自动迁移对象状态，也不承诺正在执行的调用和异步任务立即切换。

## 安装与首次出包

在 Unity Package Manager 中选择 **Add package from git URL**，使用：

```text
https://github.com/mrLeelin/Unity-AIBridge-Runtime.git#v1.0.1
```

私有仓库需要当前机器的 Git 凭据具备访问权限。也可以克隆仓库，再通过 **Add package from disk** 选择根目录的 `package.json`。

1. 仅安装 Runtime 的 UPM 包。Editor 自动化包可保持未安装状态。
2. 打开 `Window > AI Bridge Runtime > Settings`，点击 **Create Runtime Configuration**，创建 `Assets/Resources/AIBridgeRuntimeConfig.asset`。
3. 配置 `AutoStart` 和 Runtime settings。默认未启用自动启动；HTTP 为 `127.0.0.1:27182`，LAN 发现关闭，配置生成随机会话 Token。
4. 先正常安装与当前项目匹配的 HybridCLR 原生运行时，再构建 **Development Build** 测试包。Runtime 代码执行需要 HybridCLR；实时方法补丁需要支持 `HotfixAssembly` 的商业版本。
5. 成功构建且启用 AutoStart 后，包内构建工具自动导出 `.runtimebridge/profiles/<PlayerBuildGUID>/profile.json` 和该 Player 的 `references/*.dll`。

构建配置和引用导出是首次出包/更新测试包时的步骤；后续连接、执行和发送补丁不启动 Editor。引用导出失败会明确报错，不会使用 `Library/ScriptAssemblies` 替代真机引用。

启用 AutoStart 后，Development Player 中已有的旧 Runtime 场景组件也会读取这份独立配置，场景资产不会被改写。项目显式调用 `AIBridgeRuntimeBootstrap.Start(settings)` 时，以传入的设置为准。Editor Play Mode 可用场景组件做协议检查，但不会自动启动真机 Hotfix。

手机执行脚本无需携带 Roslyn 编译器。电脑端编译和补丁检查使用 Python 3 标准库与本机 .NET SDK；无需额外 Python 包或 NuGet 包。建议安装 .NET SDK 8 或更新版本，同时提供 .NET 8 运行时供元数据检查工具运行。

## Runtime 管理窗口

打开 **Window > AI Bridge Runtime > Runtime**。旧入口 **Window > AIBridge > Runtime** 和 **Players** 也可使用。Settings 窗口的“打开 Runtime 管理窗口”按钮会打开同一界面。

| 页签 | 功能 |
| --- | --- |
| 设备连接 | ADB 设备列表、USB 转发、手动 LAN 发现、HTTP 连接状态、场景 Runtime 配置 |
| 执行代码 | 选择对应 Player profile 后编译 C# 片段，或直接发送已有 DLL |
| 方法热修复 | 检查真实 Player 热修复能力，选择补丁 DLL/manifest，应用补丁和读取历史 |
| 日志 | 按类型和条数读取手机日志 |

窗口使用本包独立桌面 CLI，Token 仅保存在窗口内存中并以密码字段显示。连接、编译与执行异步进行，不阻塞 Unity 界面。关闭窗口不会撤回已发往手机的请求。Scene Runtime 按钮仅在用户点击时修改当前场景，支持 Undo，不自动保存场景。

这个窗口是可选的图形界面；关闭 Unity Editor 后，仍可继续使用下述 CLI 连接手机。

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
