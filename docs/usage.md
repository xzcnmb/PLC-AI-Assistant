# 使用说明与接口参考

## 1. 运行环境要求

- .NET 8 SDK（当前项目使用 .NET 8 目标框架开发）
- Windows 10/11 x64 环境
- 编码支持：UTF-8 终端输入与输出

## 2. 编译与运行

在项目根目录构建解决方案：

```cmd
dotnet build D:\PLCMCP\PlcMcp.slnx -c Release
```

运行全部测试（包含适配器契约、模拟状态机、安全策略与 MCP 协议测试）：

```cmd
dotnet test D:\PLCMCP\PlcMcp.slnx -c Release
```

启动 MCP 服务（采用默认配置，仅加载 4 个模拟目标）：

```cmd
dotnet D:\PLCMCP\src\PlcMcp.Server\bin\Release\net8.0\PlcMcp.Server.dll
```

## 3. 只读配置模式

若要连接真实物理设备，需通过 `--config` 指定只读配置文件，示例如下：

```cmd
dotnet D:\PLCMCP\src\PlcMcp.Server\bin\Release\net8.0\PlcMcp.Server.dll --config D:\PLCMCP\profiles\readonly.example.json
```

在只读配置模式下：
- 所有物理目标的 `canWrite` 属性必须显式设置为 `false`；
- 写操作工具 `plc_plan_write` 与 `plc_apply_write` 将不会在工具列表中注册；
- 目标发生连接失败时，将诚实返回错误信息，绝不会静默回退至模拟目标。

## 4. 目标与变量定义

每个目标具备唯一 `id`、对应厂商 `vendor`、通信端点 `endpoint` 及支持的运行时协议列表。
变量支持规范名称（Canonical Name）与中英文别名（Aliases）。在工具调用中传入别名会自动映射至底层物理地址。

## 5. 模拟写入计划与消费流

针对模拟目标，写入流程分为两阶段：
1. `plc_plan_write`：规划写入意图，校验数据类型与量程，计算当前目标状态哈希，生成一次性消费凭证 `approvalToken`。
2. `plc_apply_write`：消费凭证，比对状态哈希防止并发漂移，若通过则写入模拟存储。

## 6. 在线采样窗口 (`plc_monitor_window`)

用于有限时长与频次的只读变量采样，参数范围：
- `intervalMs`：采样周期，物理目标不得小于 200 ms；
- `durationSeconds`：单次最长采样窗口（当前 stdio 最大 15 秒）；
- `maxSamples`：单次最多采样数量（最大 20 次）。

服务返回每次采样的质量码、时间戳与变化摘要。`snapshotGuarantee` 恒为 `none`，不保证单次采样为 PLC 单个扫描周期的一致快照。

## 7. 厂商软件诊断 (`plc_doctor`)

只读检测本机安装的厂商工程软件（MicroWIN SMART、TIA Portal、GX Works3、Sysmac Studio、CODESYS、InoProShop、PLCSIM、GX Simulator、CX-Server）。该工具仅执行无副作用的文件路径与注册表检查，绝不启动 IDE，不加载受保护的厂商程序集，不连接物理设备。

## 8. 有限只读监控与离线 HMI 组态

调用 `plc_monitor_window` 并传入 `targetId`、`tags`，可选 `intervalMs` (50～60000，真实目标最少 200)、`durationSeconds` (当前 stdio 单次最多 15 秒)、`maxSamples` (最多 20)。长采样可能阻塞同一 stdio 服务的其他请求，因此该工具仅用于短窗口检查；运行时底层的 10 分钟会话 API 尚未作为 MCP 后台订阅开放。返回每次采样的质量码、时间、变化标记与摘要。服务绝不保证多个点位处于同一个 PLC 扫描周期，因此 `snapshotGuarantee` 始终为 `none`；这不是持续 SCADA 采集服务。

HMI 组态输入是厂商中立 JSON manifest，文件必须放在已配置的 `--smart-project-root` 工程目录里（该选项在此也作为离线工程根目录）。`plc_hmi_validate` 将它与 MCP 目标的标签表比对；`plc_hmi_generate` 校验通过后，向 Server 的 `data/workspaces/hmi/<id>` 新目录输出 JSON/CSV 及 SHA-256。目标厂商必须与 HMI manifest 一致，不能用空 PLC 标签集合跳过绑定检查。它不是 WinCC/GOT/NA 原生工程，也不会发布至触摸屏。

## 9. CODESYS 离线 ScriptEngine worker

本机已验证 CODESYS Development System 3.5.22.30 / SP22 Patch 3 的真实启动方式。CODESYS 的旧命令行解析器要求使用保留引号的 raw command line，不能用 .NET `ArgumentList` 代替：

```text
CODESYS.exe --noUI --noConsole --skipProjectRecovery --skipUnlicensedPlugins --profile="CODESYS V3.5 SP22 Patch 3" --runscript="D:\PLC-AI-Assistant\scripts\codesys_worker.py"
```

`codesys_worker.py` 在 CODESYS IronPython 2.7 ScriptEngine 内提供行 JSON-RPC：`handshake`、`doctor`、`submit`、`status`、`artifacts`、`cancel`、`shutdown`。真实验收已确认 `projects/system/online` 全局对象存在，ScriptEngine 4.2.0.0 返回 handshake，脚本不会调用 `ScriptOnline`，并能优雅退出。宿主固定 CODESYS 可执行文件、Profile、脚本 SHA-256 和受限工程根目录；工程操作只使用一次性工作副本，结果工件必须存在并通过 SHA-256 回读验证，信任等级最高为 `Experimental`。

启用入口需要同时提供：`--codesys-exe`、`--codesys-profile`、`--codesys-version`、`--codesys-script`、`--codesys-script-sha256`、`--codesys-project-root`。配置后 `plc_codesys_doctor` 返回只读版本/Profile/脚本/握手证据；握手通过才注册 `plc_codesys_inspect`、`plc_codesys_export`、`plc_codesys_compile`。compile 仅允许 `build`、`clean`、`rebuild`。任何 login/logout/start/stop/reset/force/download/upload/save/import/delete/move、真实 PLC 连接和在线操作都在宿主与脚本双层硬拒绝。

CODESYS ScriptEngine 是 IDE 内 IronPython 入口；本机没有公开可核实的 `CODESYSScriptEngine.dll` COM 外部自动化宿主，所以接入采用受控 `--runscript` 子进程与 stdin/stdout RPC，而不是猜测 COM API。真实工程编译/导出还需要在临时 scratch 或用户授权副本上单独验收，不能把安装文件或方法名当作厂商编译证据。

更多边界见 [架构设计](research-and-architecture.md) 和 [厂商接入计划](vendor-integration-plan.md)。

## 10. 当前不能做的操作

跨品牌程序自动编译下载、厂商原生硬件/HMI 组态、在线编辑、真实写参数、CPU RUN/STOP、强制 IO、OPC UA 安全会话和持续订阅均未完成。四协议参数写帧只在 localhost 回环测试，`canWrite` 对物理目标仍须 false。SMART 工程离线分析不代表现场程序下载能力。

使用问题可在仓库提交 Issue，附软件版本、已脱敏配置、错误信息、复现步骤和期望行为。也欢迎加入 QQ 群 **462720530** 交流。
