---
name: aibridge-runtime
description: 独立连接 Unity 手机测试包或 Player，观察画面与 uGUI、点击并验证结果、检查对象与日志、执行 C# 或用 HybridCLR 修复方法。用于真机代码执行、实时方法补丁和 Runtime Bridge；操作期间无需 Unity Editor。Editor 场景和 Prefab 编辑使用 aibridge。
---

# AI Bridge Runtime

## 定位工具

使用本包的 `Tools~/runtimebridge.py`，要求 Python 3；源码编译和补丁结构校验还要求 .NET 8+ SDK。项目内嵌包路径是 `Packages/com.sh.aibridge.runtime/Tools~/runtimebridge.py`；UPM 缓存或本地 file: 安装时，定位实际包根目录。先运行 `python <工具路径> --help` 核对参数。

不依赖 AI Bridge Editor 的命令队列或 `.aibridge/cli`。执行命令时无需启动 Editor。初次制作测试包和导出精确 Player 引用需要 Unity 构建流程。

## 连接手机

测试包需要启用 Runtime 自有配置：`Window > AI Bridge Runtime > Settings`，创建 `Assets/AIBridgeRuntime/Configuration/AIBridgeRuntimeConfig.asset`，显式开启 Auto Start 后构建 Development Player。配置仅在开发构建期间加入预加载资源；旧 Resources 配置会自动迁移。Release 自动剥离 Runtime，无需关闭 Auto Start；构建检查发现程序集、配置或资源包残留时必须停止并处理来源依赖。默认 HTTP 仅绑定 127.0.0.1，关闭 LAN 广播。Token 不得写入聊天、日志或版本控制；命令使用 `RUNTIMEBRIDGE_TOKEN` 环境变量。

```powershell
python $tool device --port 27182
python $tool status
```

`device` 只设置 adb 端口转发，不安装或重启游戏。必须先核对 status 的平台、产品、buildId、ready 和 runtimeCodeExecute 能力。先让用户启动目标测试包，不能把离线目标当成接口故障。需要时用 `--url` 指定 HTTP 地址。

## AI 操作闭环

通过 Tools~/mcp_runtime.py 接入 MCP，环境变量配置见包根 README.md。先用 runtime_status 确认设备/buildId/ready，再用 runtime_observe 同帧获取画面、UI、日志和可选对象字段。

使用 runtime_ui_find 定位当前 uGUI Button，检查 clickable 和遮挡信息；runtime_ui_raycast 可复核命中目标。发送 runtime_ui_click 后，用 runtime_wait 等待目标状态，再用 runtime_assert 验证实际结果，最后观察并保存证据。点击事件送达与业务成功分别记录。参数示例和 condition 语法见 README.md 的“AI 观察、操作与结果验证”。

首次启用统一观察与等待/断言，需要重新构建 Development Player。检查 serviceSessionId；遇到 SESSION_CHANGED 时重新观察并获取句柄。WAIT_TIMEOUT/ASSERTION_FAILED 的 lastObservation 是失败证据。成员属性 getter 需显式 riskAccepted=true，轮询可能重复执行 getter。操作超时后核对当前状态，再决定下一步。

截图以 MCP 图片返回。统一观察中查看 capturedFrame/captureEndFrame，各对象分别检查 success/error。无图形界面或后台检查使用 includeScreenshot=false。UI 查询覆盖 uGUI Button，点击走 EventSystem，系统弹窗或触摸手势需要另选实际支持的工具。

## 执行临时代码

```powershell
python $tool exec --dll <已编译DLL> --entry-type CodeExecutor --method-name Execute
python $tool exec --profile <对应Player的profile.json> --code 'return 42;'
python $tool exec --profile <对应Player的profile.json> --file <代码片段.csx>
python $tool logs
```

源码只在电脑上编译。profile 来自 `.runtimebridge/profiles/<PlayerBuildGUID>/profile.json`，包含精确 Player 引用；不得改用 Editor ScriptAssemblies，也不得跳过 buildId/平台不匹配。较长代码写入本地代码文件，用 --file 传递。代码异常、返回值和日志来自手机，不把 HTTP 发送成功当成业务执行成功。

## 替换已有方法

```powershell
python $tool patch-capabilities
python $tool patch --dll <业务程序集补丁DLL> --manifest <patch.json>
python $tool patch-history
```

patch.json 格式及加固方式见包根 README.md。必须包含真实 baselineDll、assemblyName、明确类型和精确方法 signature。CLI 用 System.Reflection.Metadata 比较完整结构后才发送补丁，手机再检查载荷 SHA256、已加载程序集 MVID 和精确方法目标。不得只把 structureVerified=true 当成结构检查，也不得用新增名称的临时程序集冒充原业务程序集补丁。

Hotfix 只支持方法体变化。修改字段、类型结构、函数签名或生成类型时，改用完整 DHE 更新并重启测试包。加固补丁需要 --validation-dll 明文对应物和加固流程生成的哈希绑定；没有对应物时不要绕过校验。

Editor 下 HybridCLR Hotfix 是空实现，接口会明确拒绝，不得把 Editor 协议测试写成真机修复成功。方法内联必须在目标代码执行前关闭；包的自动启动链路会提前 PrepareRuntime，显式启动时由项目在加载目标前准备。补丁不是状态回滚，不能卸载旧程序集；达到累计次数上限应重启测试包。

## 验证

执行前后读取返回值和手机状态，确认真实行为改变。读取日志和 patch-history 确认失败没有被记为成功。DLL/manifest 不兼容必须停止，不能重新发送另一个未校验补丁。电脑端假接口与模拟 HybridCLR 回归只验证协议/校验逻辑，Android IL2CPP 原生方法替换需要独立验收。
