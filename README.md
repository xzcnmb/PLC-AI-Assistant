# PLC-AI-Assistant

面向西门子、欧姆龙、三菱、汇川等工业 PLC 的 MCP 工程助手框架。

项目提供统一的 MCP stdio 接口、协议只读适配器、模拟变更治理、工程工作副本、审计与厂商软件诊断。厂商工程软件接入坚持证据驱动：本机安装、版本、位数、进程握手、退出码、输出工件和 SHA-256 都必须可核对；接口名称或 DLL 存在本身不会被当作可用能力。

QQ群：**462720530**

仓库：<https://github.com/xzcnmb/PLC-AI-Assistant>

[使用说明](docs/usage.md) · [调研与架构](docs/research-and-architecture.md) · [开发契约](docs/contracts.md) · [厂商接入计划](docs/vendor-integration-plan.md)

## 当前验证结果

| 能力 | 当前状态 | 实际证据 |
|---|---|---|
| MCP stdio | 已验证 | initialize、ping、tools/list、tools/call、notification 静默处理；日志只写 stderr |
| S7comm/FINS/SLMP/Modbus TCP 只读 | Experimental | localhost 回环、帧结构和异常路径已测试；真实 CPU/固件仍需台架验证 |
| 参数写帧 | Experimental（协议层） | localhost 帧构造/响应测试；物理 Runtime 与 MCP 写入口保持关闭 |
| 模拟写入 | 已验证（模拟目标） | 类型/范围、状态哈希、一次性计划 token、进程内锁；不接触 PLC |
| SMART 工程桥 | Experimental | MicroWIN SMART V2 工作副本分析与网络验证；不连接 PLC，不处理 TIA Openness |
| CODESYS ScriptEngine | Experimental（离线工程） | 本机 CODESYS 3.5.22.30 / SP22 Patch 3 / ScriptEngine 4.2.0.0 真实 handshake、doctor、shutdown 通过；工程操作只在工作副本，结果最高 Experimental |
| Mitsubishi GX Works3 | Verified（P0 诊断） | GXW3.exe 1.128.0.1、PE x86、.NET Framework 4.8 x86 独立 metadata worker handshake/doctor/shutdown 通过；工程 API/ServiceBus 未初始化 |
| Omron Sysmac/CX | Verified（P0 诊断） | Sysmac Studio 1.60.0.64010、CX-Server 5.1.1.4 静态文件/版本/PE/哈希/COM 注册表观察；不激活 COM、不启动 IDE |
| Omron IEC/PLCopen XML、AutomationML | Experimental（结构解析） | 受限工作目录、4 MiB/节点/深度上限、DTD/XXE 防护、结构枚举、稳定比较与扩展 hash；不是 Sysmac 编译器 |
| 跨品牌编译/下载/RUN-STOP/Force/在线写入 | Unsupported | 没有可执行入口；危险能力在 Runtime/Worker 层硬拒绝 |

## 已验证的三菱诊断链

GX Works3 的主程序是本机 32 位程序：

```text
D:\gwork2\GPPW3\GXW3.exe
FileVersion: 1.128.0.1
PE machine: 0x014c (x86)
```

独立探针位于 `src/PlcMcp.GxWorks3.Worker`，目标是 .NET Framework 4.8 x86。它只做静态元数据诊断：读取 GXW3.exe、完整 `Service\Service.config` 和有限的厂商程序集元数据；XML 解析禁用 DTD 和外部实体，ReflectionOnly 读取不执行厂商代码。真实本机测试顺序为：

```text
handshake -> doctor -> shutdown
```

退出码为 0，stdout 只有 JSON-RPC，stderr 只有诊断日志，报告中的 `engineeringApiVerified` 保持 `false`。它不会启动 GXW3.exe，不加载 ServiceBus，不打开工程，不连接 PLC。

主 MCP 进程通过下面的成组参数启用唯一的三菱工具 `plc_gxworks3_doctor`：

```cmd
dotnet PlcMcp.Server.dll ^
  --gxworks3-probe D:\PLCMCP\src\PlcMcp.GxWorks3.Worker\bin\Release\net48\PlcMcp.GxWorks3.Worker.exe ^
  --gxworks3-probe-sha256 <探针exe的SHA-256> ^
  --gxworks3-exe D:\gwork2\GPPW3\GXW3.exe ^
  --gxworks3-version 1.128.0.1
```

四个参数必须同时出现、每个只能出现一次、路径必须是绝对路径，探针 exe 启动前会再次做 SHA-256 校验。没有完整配置时，三菱工具不会注册。`plc_gxworks3_inspect/export/compile` 当前不会注册，因为 GX Works3 内部 ServiceBus 和工程服务还没有被证明可以从独立进程安全初始化。

## 已验证的 CODESYS 离线链

本机 CODESYS 版本为 3.5.22.30 / SP22 Patch 3 / x64，官方脚本引擎为 IronPython 2.7。旧版命令行解析器要求保留引号，不能把参数拆成 .NET `ArgumentList`：

```text
CODESYS.exe --noUI --noConsole --skipProjectRecovery --skipUnlicensedPlugins --profile="CODESYS V3.5 SP22 Patch 3" --runscript="D:\PLCMCP\scripts\codesys_worker.py"
```

`codesys_worker.py` 在 CODESYS 进程内通过 stdin/stdout 行 JSON-RPC 提供 `handshake`、`doctor`、`submit`、`status`、`artifacts`、`cancel`、`shutdown`。真实测试确认 `projects/system/online` 全局对象存在；Worker 不调用 ScriptOnline。支持的离线语义为工作副本 inspect、PLCopen XML export、build/clean/rebuild；所有结果要求工件存在并通过 SHA-256 校验，状态最高为 Experimental。

启用 CODESYS 需要同时提供 `--codesys-exe`、`--codesys-profile`、`--codesys-version`、`--codesys-script`、`--codesys-script-sha256`、`--codesys-project-root`。doctor 通过后才注册 `plc_codesys_inspect`、`plc_codesys_export`、`plc_codesys_compile`；`plc_codesys_doctor` 用于只读证据检查。login、logout、start、stop、reset、force、download、upload、save、import、delete、move 和所有 PLC 在线操作在宿主与脚本双层拒绝。

## 已验证的欧姆龙离线链

本机已安装 Sysmac Studio 1.60.0.64010 与 CX-Server 5.1.1.4。`plc_omron_doctor` 只读取安装文件、FileVersionInfo、真实 PE 位数、SHA-256、32/64 位注册表视图和 COM 注册信息；不会调用 `Type.GetTypeFromProgID`，不会实例化 COM，不启动 Sysmac/CX-Server，不连接 PLC。

在配置了受限 `--smart-project-root` 后，主 MCP 还注册两个标准交换文件工具：

- `plc_omron_exchange_inspect`：读取 IEC 61131-10/PLCopen XML 或 AutomationML CAEX，输出项目/POU/变量/类型/设备结构和 source SHA-256。
- `plc_omron_exchange_compare`：对两个受限文件做稳定排序的结构差异与原始扩展 hash 比较。

该解析器限制文件 4 MiB、XML 深度 64、节点 50,000，禁用 DTD/外部实体和网络 schema，不导入工程，不调用私有 Sysmac API。输出 `validationLevel=structural`、`isVendorCompiler=false`。ACE、nexcc、SysmacDiff、NEX online、CX-Server 在线通信、下载、写变量、RUN/STOP、Force、Raw CGI、密码和任意脚本执行全部保持 Unsupported。

## MCP 工具概览

常用工具：

- `plc_list_targets`、`plc_get_capabilities`、`plc_list_tags`、`plc_browse_symbols`
- `plc_probe_target`、`plc_read_tags`
- `plc_plan_write`、`plc_apply_write`（仅模拟目标）
- `plc_doctor`、`plc_get_capabilities_report`、`plc_get_audit`
- `plc_lint_program`、`plc_compare_projects`、`plc_monitor_window`
- `plc_hmi_validate`、`plc_hmi_generate`
- 条件注册：`plc_smart_inspect`、`plc_smart_validate`、`plc_codesys_*`、`plc_gxworks3_doctor`、`plc_omron_doctor`、`plc_omron_exchange_*`

条件工具只在对应路径、版本、哈希、工作根和 doctor 门禁满足时出现。工具 annotations 只是客户端提示，真正的拒绝规则在 Worker、Runtime 和安全策略层。

## 构建、测试和启动

```cmd
dotnet build D:\PLCMCP\PlcMcp.slnx -c Release
dotnet test D:\PLCMCP\PlcMcp.slnx -c Release
dotnet D:\PLCMCP\src\PlcMcp.Server\bin\Release\net8.0\PlcMcp.Server.dll
```

本次最终 Release 验收：

- `PlcMcp.Tests`: 245 passed
- `PlcMcp.Engineering.Tests`: 174 passed
- 总计：419 passed，0 failed，0 skipped
- Framework GX probe 在本机真实 GX Works3 安装上 handshake/doctor/shutdown 通过
- CODESYS ScriptEngine 在本机真实安装上 handshake/doctor/shutdown 通过
- 未启动 Sysmac Studio、GX Works3 主界面、CX-Server COM 或任何 PLC 连接

无参数启动只加载模拟目标和基础 MCP 工具，不建立 PLC 网络连接。配置错误会直接退出，不静默回退到模拟目标。反馈问题时请脱敏工程文件、IP、凭据和客户项目内容。

## 仍然明确不支持

本项目当前没有跨品牌通用编译器，也没有现场下载、RUN/STOP、Force、真实写参数、在线编辑、原生 HMI 发布、密码管理、内存清除/格式化或持续 SCADA 订阅入口。要从 `Unsupported` 升级任何厂商工程能力，必须提供精确版本、worker handshake、工程副本前后 hash、实际产物 hash、回读/重开证据和独立台架验证；方法名、COM 注册、安装目录或反编译结果都不够。
