# 欧姆龙 Sysmac Studio / CX-Server 调研与 MCP 接入清单

> 调研对象：本机 OMRON Sysmac Studio、CX-Server 及其安装组件。本文记录文件系统、注册表、程序集元数据和接口反编译证据，供后续 MCP Worker 实现使用。
>
> **边界声明：** 这些接口大多是厂商内部 Framework/模块契约，不等于官方第三方 SDK。调研只做只读文件、注册表和程序集元数据检查，没有连接 PLC，没有执行下载、写入、RUN/STOP、Force、内存清除或格式化。

## 1. 本机已核实环境

| 项目 | 结果 |
|---|---|
| Sysmac Studio 产品版本 | `1.60.0000`（卸载项）；主程序文件版本 `1.60.0.64010` |
| Sysmac Studio 主程序 | `C:\Program Files\OMRON\Sysmac Studio\SysmacStudio.exe` |
| ACE 脚本宿主 | `C:\Program Files\OMRON\Sysmac Studio\Ace.ScriptHost.exe`，文件版本 `1.60.0.64010` |
| 交叉编译工具 | `builder2\nexcc.exe`，文件版本 `1.60.0.64010`、产品版本 `1.60.0.64010` |
| 工程比较工具 | `SysmacDiff.exe`，文件版本 `1.60.0.64010` |
| CX-Server | 注册表版本 `5.1.1.4`；`cdmsvr20.exe` 文件版本 `5,1,1,4` |
| CX-Server 根目录 | `C:\Program Files (x86)\OMRON\CX-Server` |
| Sysmac Framework target | `Omron.Cxap.Framework.Core.dll`、`NexCore.dll`、`NexOnline.dll`、`NexProgramming.dll` 等目标 `.NETFramework,Version=v4.6.1`，CLR `v4.0.30319` |
| ACE 帮助 | `Help\en-US\ACE API Reference Manual.chm` 存在（已由安装目录列举） |
| 标准交换样例 | `Sample\IEC 61131-10 XML\...\Sample.xml`、`Sample\IEC 62714 AutomationML\SampleProject.aml` |
| 常见 CX-Compolet / Sysmac Gateway | 本次安装审计中未确认，不得假设存在 |

安装目录包含大量 WPF/Prism、C++/CLI、原生 DLL 和模块插件。Sysmac Studio 不能被当作一个可以随意从 .NET 8 进程引用的普通类库。

## 2. 组件分层

### 2.1 Sysmac Studio Framework / Cxap

代表程序集：

```text
Omron.Cxap.Framework.Core.dll
Omron.Cxap.Framework.Implementation.dll
Omron.Cxap.Composition.Unity.dll
Omron.Cxap.Modules.Variables.Core.dll
Omron.Cxap.Modules.Drives.ClientServices.Core.dll
```

反编译到的接口领域包括：

- `Omron.Cxap.ServiceLayer.IServiceManager`
- `Omron.Cxap.ServiceLayer.IRegistrationService`
- `Omron.Cxap.DataLayer.Persistence.Core.IProjectFile`
- `Omron.Cxap.DataLayer.Persistence.Core.IRepository`
- `Omron.Cxap.DataLayer.Persistence.Core.IRepositoryExportService`
- `Omron.Cxap.DataLayer.Persistence.Core.IRepositoryImportService`
- `Omron.Cxap.DataLayer.Persistence.Core.IVersionCompatibilityService`
- `Omron.Cxap.Framework.Core.IApplicationInformationService`
- `Omron.Cxap.Framework.Core.Hosting.IHostingService`
- `Omron.Cxap.Modules.Variables.Core.IVariable`
- `Omron.Cxap.Modules.Variables.Core.IVariableGroup`
- `Omron.Cxap.Modules.Validation.Core.IValidationService`
- `Omron.Cxap.Modules.Synchronisation.Core.ISynchronisationService`
- `Omron.Cxap.Security.Core.IAccessControlService`
- `Omron.Cxap.Security.Core.IAuthenticator`

`IVariable` 是数据模型接口，公开 Name、DataTypeName、Address、InitialValue、Retained、Comment、Id、ReadOnly/Constant 等属性；`IVariableGroup` 提供变量枚举、添加、插入、删除和排序。模型存在不代表可脱离 Sysmac 容器实例化，也不代表写入 PLC。

### 2.2 NEX 工程、编程、仿真和在线模块

代表程序集：

```text
Modules\Nex\NexCore.dll
Modules\Nex\NexConfiguration.dll
Modules\Nex\NexProgramming.dll
Modules\Nex\NexSimulation.dll
Modules\Nex\NexOnline.dll
```

`NexOnline.dll` 反编译到的服务/接口域很丰富：

- 变量读取、变量备份/恢复、数据类型和结构体读取
- 控制器实体、连接和文件列表
- 监视订阅：`NexOnline.Monitor.Core.IMonitoringSource`
- PLC CGI/通信编码与解码：`NexOnline.Communication2.Plc.Core.*`
- CPU、变量、内存、文件、事件日志通信契约
- OPC UA、EtherCAT、APB、RCE、SecureSocket 等通信域
- XBus 单元上传/下载：`IXbusUnitUploadService`、`IXbusUnitDownloadService`
- 运行同步/下载：`INexSynchroniseController`、`IRunModeDownloadService`
- 在线编辑：`NexOnlineEdit` 服务域

`INexOnlineModuleServ` 本身是 `internal`，但其属性暴露了很多内部服务工厂，例如 `VariableCommunicatorFactory`、`ControllerEntityReader`、`CgiRequestSender`、`UtifManager`、`NexSynchroniseFactory`。这说明内部组合根存在，不说明有稳定的第三方入口。

重点：在线模块同时覆盖读、写、下载、恢复、删除文件、运行模式变更等操作，不能按命名空间整体接入 MCP。

### 2.3 CODESYS/第三方兼容模块（不要误判为 Omron API）

`Modules\CdsLoader\Common` 下存在：

```text
OnlineCommands.dll
OnlineExpressionInterpreter.dll
PLCopenXML.dll
ProjectArchive.dll
ProjectCompare.dll
ProjectInfoObject.dll
PlcLogicObject.dll
```

部分程序集命名空间为 `_3S.CoDeSys.*`，这更像兼容/承载层或第三方模块，不应直接宣称是 Sysmac Studio 官方公开工程 API。它们可作为标准交换/比较研究对象，但需要以真实样本和版本锁定验证。

### 2.4 CX-Server 传统通信/COM

本机注册了 32 位 COM 类，代表 ProgID：

```text
CXServer.Communications
CXServer.DeviceMessage
CXServer.RawDevice
CXServer.RawDeviceReceiver
CXServer.RawNetwork
CXServer.RawNetworkReceiver
Omron.DeviceManager
Omron.DeviceResult
Omron.FinsNetwork
Omron.Ethernet
Omron.PortManager
Omron.Network.ETNNetwork
Omron.Protocol.ETNProtocol
```

已核实的实现线索：

| ProgID/领域 | 实现位置 | 位数/进程模型 |
|---|---|---|
| `CXServer.Communications`、`DeviceMessage`、`RawDevice`、`RawNetwork` | `cdmsvr20.exe` LocalServer32 | 32 位 COM EXE/代理服务 |
| `Omron.DeviceManager`、`DeviceResult` | `CXSDI_DeviceManagement.dll`、`CXSDI_DeviceResult.dll` | 32 位 InProc COM，Apartment |
| `Omron.FinsNetwork` | `Common Files\Omron\Drivers\CXSDI_FinsNetwork.dll` | 32 位 InProc COM，Apartment |
| `Omron.Ethernet` | `CXSDI_EthernetPort.dll` | 32 位 InProc COM，Apartment |
| `Omron.PortManager` | `CXSDI_PortMan.exe` | 32 位 LocalServer |

注册信息是能力线索，不是 API 完整契约。CX-Server 主要适合传统 FINS/CS/CJ/CP 通信和点位监视；不能把它当 Sysmac Studio 工程编译器，也不能推断支持 NJ/NX 全部能力。由于是 32 位 COM，必须放在独立 x86 Worker 中，不能从当前 .NET 8 Server 直接加载。

## 3. 面向 MCP 的能力分级

### P0：安装诊断（首期）

| 工具 | 目的 | 证据/限制 |
|---|---|---|
| `plc_omron_doctor` | 检查 Sysmac/CX-Server 路径、版本、位数、Framework、组件 hash | 只读，不启动 IDE，不连接 PLC |
| `plc_omron_capabilities` | 报告实际安装的 Sysmac、ACE、NEX、CX-Server、COM ProgID | “已安装”只代表发现，不代表可调用 |

### P1：离线工程只读

| 工具 | 目的 | 推荐来源 |
|---|---|---|
| `plc_omron_export_xml` | 读取/校验 IEC 61131-10 XML 或标准交换数据 | 安装的 XML Schema、样例和受控 Export 路线 |
| `plc_omron_export_aml` | 读取/校验 AutomationML | 安装的 AML 样例/Schema；先不导入 |
| `plc_omron_project_inspect` | 工程元数据、控制器/变量/设备概览 | 先验证 Repository/ProjectFile 的独立读取路径 |
| `plc_omron_list_variables` | 列出变量名、类型、地址、注释、保持属性 | `IVariable`/变量模型；必须是工作副本只读 |
| `plc_omron_validate_project` | 离线结构/格式/校验结果 | `IValidationService` 或标准 XML Schema 校验；不可冒充官方编译结果 |
| `plc_omron_compare_projects` | 对比两个标准交换文件/工程副本 | `SysmacDiff` 或 Compare 模块仅在真实 CLI/输入输出验证后启用 |

### P2：离线编译/仿真（Experimental）

| 工具 | 条件 |
|---|---|
| `plc_omron_build_project` | 只有确认 `nexcc.exe` 参数、Profile、退出码和产物语义后才可实现；当前不把它当公开编译 API |
| `plc_omron_simulator_probe` | 只允许离线仿真探针；不能连接真实 PLC；需独立副本和进程隔离 |
| `plc_omron_ace_script` | 仅当 ACE API 帮助能确认公开入口、宿主命令行和安全沙箱后考虑；任意脚本执行不注册 MCP |

### P3：在线只读（另立任务）

| 工具 | 潜在来源 | 前置条件 |
|---|---|---|
| `plc_omron_read_controller_status` | NEX online / CX-Server | 明确 PLC 型号、网络、目标身份、超时、审计 |
| `plc_omron_read_variables` | `NexOnline` Variable/PLC CGI 或 CX-Server | 地址和数量限制；证明只读；区分 NJ/NX 与传统 PLC |
| `plc_omron_monitor_variables` | `IMonitoringSource`/监视订阅 | 会话、取消、断线和资源释放测试 |
| `plc_omron_read_event_log` | PLC event log 通信契约 | 输出上限、时间戳和目标确认 |

### P4：明确不开放

首期及默认策略硬拒绝：

- PLC/CX-Server `Write`、变量写入、在线编辑提交
- `IXbusUnitDownloadService`、`IRunModeDownloadService`、程序/单元下载
- RUN/STOP、模式切换、Force/解除 Force
- 内存卡/PLC 文件删除、格式化、恢复覆盖
- 在线变量初值/保持值修改
- 任意 ACE/IronPython/脚本执行
- `IRepositoryImportService`、工程导入覆盖和解密接口
- 安全/凭据/许可服务枚举或绕过
- 直接调用 `IPlcCgi*RawCommandService` 发送任意原始命令

## 4. 推荐接入架构

```text
MCP client
  -> PlcMcp.Server（工具 schema、参数校验、授权）
    -> Omron adapter / Engineering job（强类型 operation allowlist）
      -> ExternalEngineeringWorker / Client（JSON-RPC、超时、产物 hash）
        -> Omron worker（独立进程）
           ├─ x64 .NET Framework 4.6.1：Sysmac Framework/NEX 离线候选
           └─ x86 COM worker：CX-Server 传统通信候选
```

不要将以下内容加载进 .NET 8 MCP Server：

- `Omron.Cxap.*`、`Nex*.dll`、WPF/Prism 容器程序集
- C++/CLI、Native、32 位 CX-Server COM DLL
- Sysmac 主程序私有模块

建议按运行时/位数拆成两个 worker，而不是在一个进程里混用 x64 Framework、x86 COM、UI 线程和在线连接：

1. **OmronSysmacOfflineWorker**：目标 Framework 4.6.1，默认只读，处理标准 XML/AML、工程副本、验证和比较探针。
2. **OmronCxServerWorker**：目标 x86，显式 COM STA 线程，默认不连接；以后仅在批准的在线只读任务中使用。

## 5. 当前仓库接入点

| 文件 | 后续改动方向 |
|---|---|
| `src/PlcMcp.Engineering/Workers/External/VendorProfileDetectors.cs` | 扩展现有 `SysmacProfileDetector`：报告本机 `SysmacStudio.exe`、版本、实际组件、注册表 32/64 视图；当前默认路径可保留，但不要依赖它发现所有安装 |
| `src/PlcMcp.Engineering/Workers/External/ExternalEngineeringWorker.cs` | 复用子进程生命周期、工作副本、超时、取消和原文件完整性校验 |
| `src/PlcMcp.Engineering/Workers/External/ExternalWorkerProtocol.cs` / `ExternalWorkerClient.cs` | 沿用固定 handshake、状态、artifact、cancel 和 line-JSON 传输；不得增加任意程序集方法调用消息 |
| `src/PlcMcp.Engineering/Workers/External/ExternalWorkerSecurityPolicy.cs` | 在通用拒绝名单外增加 `WriteVariable`、`OnlineEdit`、`Download`、`Run`、`Stop`、`Force`、`MemoryClear`、`Format`、`RawCommand`、`Script`、`ImportProject` 等欧姆龙危险操作的硬拒绝 |
| `src/PlcMcp.Engineering/Workers/Codesys/*` | 仅参考现有厂商 Worker 的配置/协议组织，不复制 CODESYS API 或在线逻辑 |
| `src/PlcMcp.Engineering/Jobs/EngineeringJobs.cs` | 新增明确的 Omron offline operation 类型或强类型映射；禁止传入反编译方法名、原始命令或脚本文本 |
| `src/PlcMcp.Server/Governance/ServerGovernanceServices.cs` / `Program.cs` | 配置显式的 Sysmac/CX-Server worker 路径、版本、SHA、允许工程根；未配置时保持 `Unsupported` |
| `src/PlcMcp.Server/Mcp/McpToolRouter.cs` | 仅注册已通过 doctor 和 worker 能力门槛的 `plc_omron_*` 工具；区分 Sysmac 工程工具和 CX-Server 通信工具 |
| `tests/PlcMcp.Tests/` | 覆盖检测器、注册表 32/64 视图、x86/x64 worker 选择、operation allowlist、COM 未安装、路径/工件 hash、超时/取消/身份篡改 |
| `docs/vendor-integration-plan.md` | 保持 `Verified` / `Experimental` / `Unsupported` 分级；不要写“接口存在即完成” |

当前仓库的通用 `DefaultAllowedOperations` 包含 `InspectProject`、`ExportPou`、`ValidatePou`、`CompileProject`、`DiffProjects` 等离线语义；Omron 适配器只能把这些语义映射到已验证的实现，不能因此自动获得 Sysmac 编译或在线权限。

## 6. 交接实施顺序

### P0 — Doctor

- [ ] 发现 Sysmac Studio、ACE、`nexcc.exe`、`SysmacDiff.exe`、CX-Server。
- [ ] 报告文件版本、Framework target、PE 位数、关键 DLL hash 和缺失组件。
- [ ] 报告 COM ProgID、CLSID、实现 DLL/EXE、32 位/64 位视图；不要实例化 COM。
- [ ] Doctor 不启动 Sysmac、不打开工程、不连接 PLC。

**完成定义：** 本机实际安装能被准确报告；路径、版本、位数和组件状态可复现；不存在时返回 `Unsupported`。

### P1 — 离线格式 Worker

- [ ] 先用 fake worker 验证现有握手、身份、超时、取消、输出配额和 artifact 校验。
- [ ] 对安装样例 XML/AML 做 schema 解析和差异测试；输入只读。
- [ ] 在用户授权的工程副本上验证标准交换文件读取；原工程前后 SHA-256 必须相同。
- [ ] 失败时保留净化 stderr/错误码，不能把异常栈或客户路径全部返回 MCP。

完成条件：可重复运行、结果可回读、artifact hash 稳定、异常退出清理临时目录。

### P2 — Sysmac Framework 探针

- [ ] 建立独立 Framework 4.6.1 worker，并记录真正的依赖加载结果。
- [ ] 证明是否需要 SysmacStudio 主程序/容器/UI thread/许可证。
- [ ] 只取得一个已确认的只读服务；若无法独立获取，标记 Unsupported，不反射绕过 internal broker。

完成条件：不能只凭 `ilspycmd -l i` 或 DLL 存在把能力提升为 Experimental。

### P3 — 在线只读

另立任务并获得现场批准。分为 NEX x64 与 CX-Server x86 两条路线；各自独立目标、会话、超时、取消和审计。当前任务不实现。

## 7. 验收与测试

### 安装/发现

- Sysmac 默认路径、本机实际路径、自定义路径、注册表缺失、权限错误。
- `SysmacStudio.exe` 版本不匹配、关键 DLL 缺失、hash 不匹配。
- COM 32 位视图可见、64 位视图不可见时正确选择 x86 worker。
- 只安装 CX-Server、只安装 Sysmac、两者都没有时能力状态正确。

### Worker/协议

- 固定 worker 身份和版本；坏 handshake、错误 protocol、坏 JSON、stderr 污染 stdout 均拒绝。
- timeout/cancel/worker crash/输出超限后杀进程树并清理临时目录。
- 不允许通过 `operation`、options 或原始字符串注入 `Download`、`Write`、`RawCommand`、`Script`、`Run/Stop`。

### 文件/工件

- 工程输入仅位于 allowlisted root；拒绝相对路径逃逸、绝对 artifact、reparse/junction 越界。
- 原工程前后 SHA-256 相同；工作副本异常后锁和临时目录清理。
- 工件路径、大小、SHA-256、schema/version 元数据完整校验。

### 能力升级

从 `Unsupported` 升到 `Experimental` 至少要保存：

- 精确产品版本、文件版本、Framework/bitness、关键程序集 hash。
- doctor 和 worker handshake 的 stdout/stderr、退出码。
- 使用的精确入口和参数（不只写“接口存在”）。
- 输入工作副本前后 hash、输出工件 hash、重新打开/回读结果。
- 无真实 PLC 连接的证据（P0–P2）。

## 8. 不能据此声称已验证的事项

1. Sysmac Framework 服务容器能否脱离 `SysmacStudio.exe` 独立初始化。
2. `nexcc.exe` 的正式命令行参数、授权、输入格式、输出语义和错误码。
3. IEC XML/AML 能否完整表达目标 Sysmac 工程，而不丢失私有扩展。
4. `SysmacDiff.exe` 是否有稳定的无 UI CLI 入口。
5. `Ace.ScriptHost.exe` 的公开脚本入口、参数和沙箱边界。
6. `INexOnlineModuleServ` 等 internal 接口是否能从外部取得实例。
7. CX-Server COM 的完整 TypeLib 方法签名、线程模型细节和具体 PLC 兼容矩阵。
8. CX-Server 对 NJ/NX 的支持范围；不能用传统 FINS 结论推导 Sysmac/NEX 能力。
9. 在线读取是否会创建监视/会话副作用。
10. 厂商对反编译到的内部 Framework/模块接口是否提供支持。

## 9. 结论

本机欧姆龙环境比单一工程 API 更复杂：有 Sysmac Studio 的 .NET Framework 4.6.1 模块体系、NEX 在线通信域、标准 XML/AML 交换材料，以及独立的 32 位 CX-Server COM 传统通信栈。可行且安全的第一条路线是：

1. 本地安装/组件诊断；
2. 标准 IEC 61131-10 XML / AutomationML 的离线解析、校验和比较；
3. 经过副本与 hash 验证后，再研究 Sysmac Framework 的离线工程读取；
4. 在线读取另建 x86/x64 worker 和权限域；
5. 写入、下载、RUN/STOP、Force、Raw CGI、脚本执行和内存 destructive 操作保持硬拒绝。

不要把大量反编译接口集中成“万能 Omron RPC”。应把它们压缩成 MCP 业务语义、方法级 allowlist 和可验证的 Worker 能力；接口存在、COM 注册或程序集可反编译，都不能替代真实调用闭环。
