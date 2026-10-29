# Changelog

## [1.3.0] - 2026-10-29

### 新增

- MCP 接入 Player 截图、uGUI 快照/查询/遮挡检查及点击，直接返回 PNG 图片。
- 新增同帧 runtime.observe，组合设备状态、UI、日志和可选对象字段，附采集帧与服务会话。
- 新增 runtime_wait/runtime_assert，支持有界条件比较、超时实际值和会话切换拒绝。
- 所有 MCP 工具提供结构化结果及输出约定，UI 内部失败正确转为工具错误。截图下载增加认证、大小与哈希检查。

### 验证与兼容

- 首次使用统一观察和等待/断言需重新构建 Development Player；MCP 工具数为 24。
- Unity 6000.3.6f1 编译与 Play Mode 交互闭环 19 项检查通过；Python 回归 99 项，98 通过、1 项 Windows 符号链接权限不足跳过。
- 本轮未重新构建 Android/iOS Player，也未在实体手机上验收；详见 VALIDATION.md。


## [1.2.1] - 2026-10-29

### 修复

- Release 构建通过程序集编译约束排除整个 Runtime，自动移除构建场景副本中的 Bridge 组件及空运行对象；Development Player 保留原有调试能力。
- 配置移出 Resources，仅在开发构建时临时预加载；旧配置保留 GUID 自动迁移。成功、失败、取消及域重载后恢复预加载资源和 Editor 开发开关，无需为发布包手动关闭 Auto Start。
- HybridCLR 与调试编译器的额外 linker 保留规则仅用于开发包；Release 原生编译前撤销已识别的插件改动，无法安全恢复时停止构建。
- 检查本轮程序集、场景、Resources、预加载资源和 AssetBundle 依赖，以及最终 Mono/IL2CPP、APK/AAB 产物；识别 StreamingAssets 中改名文件与压缩 Bundle 的 Runtime/配置残留，并阻止出包。
- 按本次构建参数对齐开发开关及导出 profile 的 DEVELOPMENT_BUILD 宏，避免自定义构建参数与 Editor 状态不一致带入脚本索引或错误宏。

### 兼容与验证

- 业务程序集直接引用 Runtime 时，相关代码或 asmdef 应使用相同的 UNITY_EDITOR || DEVELOPMENT_BUILD 编译条件。
- Unity 6000.3.6f1 编译通过；91 项主机回归通过；Windows Mono 开发包及正式包实际构建、运行与状态恢复验收通过，真实压缩 Bundle 残留被拒绝。
- 本次未重新构建 Android/iOS IL2CPP Player；原生恢复与 IL2CPP 元数据检测由主机回归覆盖，详见 [VALIDATION.md](VALIDATION.md)。

## [1.2.0] - 2026-10-29

### 新增

- Runtime 自动注册真机场景对象查询与 Inspector 接口，支持字段检查、公共成员读写及精确方法签名调用，无需业务项目新增接入代码。
- 新增指定 Marker 的 ProfilerRecorder 采集会话，支持读取原始样本、到期停止与资源释放。
- 新增加载程序集的版本、MVID 与身份诊断，MCP 可结合本地构建基线检查热更新能力。
- 新增标准 stdio MCP 服务，提供 16 个工具，覆盖一次性 C#、源码热更新、对象检查、诊断与 Profiler。
- CLI 新增 call 命令，可使用 JSON 参数调用 Runtime 接口。

### 修复

- 相同源码、编译器与引用生成相同的一次性代码 DLL，复用设备程序集缓存，避免重复执行相同代码消耗新的加载名额。
- 开发构建保留 Profiler 相关类型，供真机一次性代码与采集接口使用。

### 兼容与验证

- 首次启用新 Runtime 接口需更新一次 Development Player；后续调用无需重新打包。
- 对象修改、属性 getter 与方法调用遵守代码执行开关，并要求显式风险参数；已销毁对象的旧句柄失效。
- Profiler 返回指定 Marker 或计数器的原始样本，不提供完整 CPU 调用树或 GPU 时间线。DHE 热修复后的计时可能反映解释执行，不能直接代表原 AOT 方法耗时。
- Unity 6000.3.6f1 编译及 Android ARM64/MuMu 接口验收通过；Python 回归 78 项通过、1 项因 Windows 符号链接权限跳过。详细范围见 [VALIDATION.md](VALIDATION.md)。
- 旧 runtime.perf 的间隔采样统计限制仍存在，本版本新增 Profiler 接口直接返回 Recorder 缓冲样本。

## [1.1.0] - 2026-10-28

- 新增 Prepare Current Project 通用接入流程，创建或更新配置并通过 Runtime 引导自动启动，无需业务项目编写启动脚本。
- 增加项目 HybridCLR 配置检测、DHE 方法热修复与开发构建原生支持适配。
- 构建后自动导出匹配 Player 的引用配置与热修复基线，并校验程序集身份。
