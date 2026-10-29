# Changelog

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
