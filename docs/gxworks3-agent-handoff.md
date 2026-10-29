# GX Works3 MCP 接入交接单

> 交给后续实现 Agent 的任务说明。本文是接入设计与证据索引，不代表 GX Works3 接口已成功运行，也不表示任何在线操作已获授权。
>
> 反编译结果只用来发现本机内部契约。Mitsubishi 未在本机证据中提供这些接口的第三方支持承诺；接口出现于 DLL 不等于可从独立进程取得或可安全调用。

## 0. 实施结论

先做 GX Works3 离线只读 Worker：诊断安装、打开授权范围内的工程副本、导出 XML/程序/FB/标签、离线校验和比较。采用专用进程承载 Framework 依赖，复用 PlcMcp 的外部 Worker 安全传输与 MCP 工具路由。在线读操作仅作为后续独立阶段，不是首期功能。

首期严禁实现、注册或通过参数绕过：PLC 写设备、下载、RUN/STOP/模式切换、Force、内存写入/清除/格式化、密码管理、导入工程或任何未知命令。工具注解不是权限控制。

**当前没有实现 GX Works3 Worker 或工具。** 以下是调研和接入任务单，不要将本地存在 GX Works3 当成调用闭环的证据。

### 审查后的事实修正（必须以此为准）

- 本机 `GXW3.exe` 的可复现文件版本是 `1.128.0.1`，PE machine `0x14c`，即 **x86/32-bit**；位数必须从 PE 头读取，不能从路径名称推断。
- GX Works3 安装注册表位于 32 位视图：`HKLM\SOFTWARE\WOW6432Node\MITSUBISHI\SWnDN-GPPW3`，`CurrentVersion\InstallPath=D:\gwork2\GPPW3\`、`MajorVersion=1`、`MinorVersion=128J`。64 位视图下不能假定 `SOFTWARE\MITSUBISHI\GXW3` 存在。
- `1.128.04519` 当前无法由已核验注册表键或文件资源复现；实现和文档不得继续把它写成事实。报告必须分别列出文件版本和注册表 Major/Minor 原值。
- `Service\Service.config` 已完整核对：PLCFormat Managed/Native、Label、Checker、Monitor 和 `Project.Native.DataOperation` 有映射；没有 XmlImportExport 或 Online DeviceMonitor 服务键。`IXmlImportExport` 因此保持“待证据”，不作为首期已注册服务。
- `DefaultAllowedOperations` 在现有仓库中没有调用方，不能当作现成的 GX 白名单。GX 必须新增 vendor 层 job 白名单、worker operation 白名单和 options 键白名单；任意内部方法名一律拒绝。
- 当前通用工作副本是单文件语义。GX 目录工程接入 P2 前必须新增逐文件 SHA-256 清单、前后完整性校验、重解析点/junction 拒绝和目录快照清理；在此之前不要声称 GX 工程副本已支持。

## 1. 证据摘要

| 本机事实 | 已核验结果 |
|---|---|
| GX Works3 安装根目录 | `D:\gwork2\GPPW3` |
| 主程序 | `D:\gwork2\GPPW3\GXW3.exe` |
| 注册表安装证据 | 32 位视图 `HKLM\SOFTWARE\WOW6432Node\MITSUBISHI\SWnDN-GPPW3`；`InstallPath=D:\gwork2\GPPW3\`、`MajorVersion=1`、`MinorVersion=128J` |
| `GXW3.exe` 文件版本 | `1.128.0.1` |
| `GXW3.exe` PE 位数 | x86 / 32-bit（PE machine `0x14c`） |
| MX Component 常见 ProgID | 未确认注册，不能假设 `ActUtlType`/`ActProgType` 可用 |
| 代表 Managed DLL 的 target framework | 已检查的工程数据、PLCFormat、XML、Checker、Monitor 程序集为 `.NETFramework,Version=v4.0`，CLR `v4.0.30319` |
| ServiceBus | `Melco.GXW3.ServiceBus.dll` 有 Managed/Native broker 契约；独立启动/取服务流程尚未验证 |

完整安装和接口证据见 [`gxworks3-mcp-integration-research.md`](gxworks3-mcp-integration-research.md)。本机服务键及 Managed/Native 映射见安装目录 `Service\Service.config`。

### 重要服务映射结论

- `Service.config` 显示 `ExternalData.Managed.PLCFormat.IPLCFormatController` 和 Native PLCFormat 服务都有映射；这是优先调查的离线导出方向。
- `ReferenceData.Managed.Label.ILabelReferenceDataController`、`Checker.Managed.ICheckerController`、`Monitor.Managed.IMonitorController` 有 Managed 服务键。
- 工程 `IDataOperation` 在 `Service.config` 中映射的是 **Native** 服务键。虽在 Managed DLL 看到了同名领域接口，不代表它可经配置直接创建。
- XMLImportExport 与 Online DeviceMonitor 等接口的运行时获取路径还未证实。
- `Melco.GXW3.ServiceBus.dll` 中 `IServiceBroker` 是 `internal`；已反编译的方法涉及 Managed/Native 实现加载。不要反射调用内部 broker 并宣称为受支持集成。

## 2. API 候选与权限分类

只需继续验证有 MCP 业务价值且低风险的子集；禁止把整个 Controller/接口透明转发。

### 首期可验证的离线候选

| 业务能力 | 本机接口线索 | MCP 工具候选 | 调用前置条件 |
|---|---|---|---|
| 安装诊断 | `GXW3.exe` 文件/注册表、Managed DLL 元数据 | `plc_gxworks3_doctor` | 只读检测；报告路径、版本、位数/Framework、哈希和依赖存在性；不启动 IDE、不连接 PLC |
| 工程元数据/对象枚举 | 工程服务契约；`IDataOperation` / 项目模型 | `plc_gxworks3_inspect` | 先确认合法服务获取方式、项目打开/关闭和对象 ID 生命周期 |
| PLC/程序/FB 导出 | `IPLCFormatController.GetPLCData_Program/FBFile/CPU/System/SafetyCPU/SafetyUnit`、Header | `plc_gxworks3_export` | 工程副本，输出大小上限，格式解析结果及 SHA-256；只用 getter/trim-header，不调用 `SetPLCData_MemoryDump*` |
| PLCopen XML 对象导出 | `IXmlImportExport.Export/ExportObject` | `plc_gxworks3_export_xml` | 只测试 Export 系列；不调用 Import、Encrypted Import 或 Decrypt |
| 离线检查 | `ICheckerController.DeviceCheck/InstructionCheck/CheckDataName/CheckLabelName` | `plc_gxworks3_validate` | CPU/工程上下文来源已验证；映射错误码和校验结果 |
| 标签/设备/注释读取 | `ILabelReferenceDataController.SeeDeviceAssign/SeeRowDataByLabelID/GetAllLabelNameAndType/GetRowDataAllGlobalAndLocal` | `plc_gxworks3_list_labels` | 只读白名单；不调用 `UpdateElementwiseCommentByInstanceName` |
| 工程比较 | `Comparison` 下各类型 Comparer | `plc_gxworks3_compare` | 比较算法、覆盖范围及稳定性需样本核验；先从离线文件比较开始 |

`IDataOperation` 虽有 `GetDeviceRange`、IO 容量和参数信息读取，也包含 Create/Move/Rename/Delete/GenerateFB 等修改方法。首期若无法在独立、可靠的接口边界上只调用只读方法，应完全不接入该接口，不要仅凭参数名过滤。

### 在线只读候选：只列入后续实验

| 候选能力 | 接口线索 | 风险和注意事项 |
|---|---|---|
| PLC 运行状态/容量 | `ICommonOperationController.GetPlcRunStatus/GetPlcMemoryCapacity` | 同接口还含模式切换、内存清除/格式化、文件/Label 写入；必须按方法白名单 |
| 设备批量读取 | `IDeviceMonitorController.ReadDevice/ReadDeviceRandom` | 先验证在线目标、连接 ID、CPU 地址语法、监视资源释放与只读语义 |
| 驱动器/Label Memory 读取 | `IMemoryOperationController` 及 PLC Data Read 服务 | 先确定文件路径、大小、认证与数据敏感性；不能开放相应写接口 |
| 程序监控 | `IMonitorController`、RealTimeMonitor、WaveMonitor | `IMonitorController` 同时提供 `ModifyValue`、`ModifyStepActivity`、`ChangeBitLabelValue`；不能把该接口称为只读 |

在线候选必须与离线 Worker/权限域分离，单独设计用户授权和现场联调。无现场批准、无目标 CPU 指纹、无真机验证时，保持 Unsupported，不注册在线 MCP 工具。

### 明确拒绝的接口/方法族

- `IWriteDeviceController.WriteDevice`
- `ICommonOperationController.SetPlcStatusMode/ExecutePlcMemoryFormat/ExecutePlcMemoryClear/SetWriteLabelDataParameter/ExecuteWriteLabelData`
- `IPLCDataOperationWriteController.PLCWrite/SetStatus/SetBoot/PLCDriveDeleteFileGet` 及整套传输流程
- `IMonitorController.ModifyValue* / ModifyValueEX* / ModifyStepActivity / ChangeBitLabelValue`
- `IPLCFormatController.SetPLCData_MemoryDump* / GetBlockPasswordList`
- `IXmlImportExport.Import* / DecryptXmlFile`
- `ISecurityController.Administer*Password`

这是 MCP 侧的硬拒绝名单，不可通过 `options`、原始 ServiceBus 调用或用户提供任意方法名绕过。

## 3. 推荐进程和依赖边界

```text
MCP client
   -> PlcMcp.Server (MCP tool schema、args 校验、tool handler)
      -> GxWorks3Worker adapter (Engineering 层 job allowlist / doctor)
         -> ExternalEngineeringWorker + ExternalWorkerClient (子进程、JSON-RPC、超时、限额、工件校验)
            -> 专用 GX Works3 worker process (Framework runtime、受控 vendor adapter)
               -> 仅处理隔离副本；服务获取路径验证后才加载 GX Works3 依赖
```

理由：主应用为 .NET 8（`src/PlcMcp.Engineering/PlcMcp.Engineering.csproj`），本机代表 Managed 程序集目标为 .NET Framework 4.0。不要把这些 Framework/可能依赖的 C++/CLI 或 Native DLL 引入 Server/Engineering 的 .NET 8 进程。建立小型隔离 worker 后，再实测合适 Framework runtime、CPU 位数及厂商依赖；不要预设 x86/x64 或“Framework 4.0 程序集一定可独立加载”。

内部 ServiceBus 可能需要 GX Works3 主进程环境、Native broker、UI thread、COM/授权或工作目录初始化。先做最小启动/取得单个只读 service 的探针；若必须自动化主界面，应将它作为另一条有版本锁定的 UI automation 路线，而不是假装调用 API。

## 4. PlcMcp 现有接入点

| 目标 | 文件 | 后续 Agent 要做的事 |
|---|---|---|
| 接口目录与能力状态 | `src/PlcMcp.Engineering/Workers/External/VendorProfileDetectors.cs` | 修正 `GxWorksProfileDetector` 默认路径；增加注册表/自定义路径支持；真实读取 PE 位数，不能用路径含 `x86` 推断 |
| worker 安全传输 | `src/PlcMcp.Engineering/Workers/External/ExternalEngineeringWorker.cs`、`ExternalWorkerClient.cs`、`ExternalWorkerProtocol.cs`、`ExternalWorkerSecurityPolicy.cs` | 优先组合现有 JSON-RPC 握手、身份 pin、工作副本、超时、输出限制和 Artifact 路径/SHA 校验；安全操作还要有 GX 专属 allowlist |
| 厂商 worker 模式 | `src/PlcMcp.Engineering/Workers/Codesys/CodesysWorker.cs`、`CodesysWorkerConfig.cs` | 参考“专用 worker 包装通用外部 worker + 每厂商限制 job/config”的结构；不要复制 CODESYS 参数/脚本逻辑 |
| 可用性与生命周期 | `src/PlcMcp.Server/Governance/ServerGovernanceServices.cs` | 增加可选 GX 配置/worker 和共享 workspace；默认未配置时不开放工具 |
| 配置参数 | `src/PlcMcp.Server/Program.cs` | 增加严格成组、只允许一次的 GX 配置；校验绝对路径、允许目录、版本和哈希，失败即拒绝启动/保持不可用 |
| MCP 工具注册 | `src/PlcMcp.Server/Mcp/McpToolRouter.cs` | 只在显式配置且 doctor/能力证据满足门槛时注册 `plc_gxworks3_*`；参数 schema 明确、handler 只提交已枚举 job |
| 统一 job contract | `src/PlcMcp.Engineering/Jobs/EngineeringJobs.cs` | 当前 job enum 只有 inspect/export-pou/validate/compile/diff；新增语义要用明确 job 类型或 GX worker 私有 operation 映射，禁止通用 arbitrary method |
| 工作副本/回滚边界 | `src/PlcMcp.Engineering/Workspace/`、`src/PlcMcp.Engineering/Workers/External/ExternalWorkerSecurityPolicy.cs` | 输入目录白名单，路径规范化，阻止重解析点/目录跳逸；验证源文件前后 hash；产物留在副本并验证 SHA |
| 测试 | `tests/PlcMcp.Tests/` | detector、配置、job allowlist、参数 schema、worker protocol、异常/超时、伪造身份、hash 错误、目录逃逸、源码 hash 不变均做测试 |

工具名要和仓库既有 vendor 工具区分；现有 CODESYS 工具使用 `plc_codesys_*`，Mitsubishi 建议 `plc_gxworks3_*`。不要直接向所有 PLC target 注册工程软件工具：GX Works3 安装仅是本机能力，不证明当前 MCP target 就是 Mitsubishi 或工程/CPU 兼容。

## 5. 推荐实施阶段与完成定义

### P0 — Doctor/本机发现

- [ ] 检测 `D:\gwork2\GPPW3\GXW3.exe` 以及 Program Files / 注册表 / 显式路径；不递归扫描整个磁盘。
- [ ] 回报文件版本、注册表产品版本、Managed assembly Framework、位数与必要文件哈希；不把版本号猜测成兼容性结论。
- [ ] 只读检查 worker 可执行体 allowlist、GX 安装路径与工程 root。
- [ ] `plc_gxworks3_doctor` 只做本地文件/注册表检查，不启动 IDE、不打开工程、不连接 PLC。

**完成门槛：** 已安装/缺失、路径、版本、位数、Framework 信息均诚实报告；自定义路径能被发现；doctor 无系统副作用。此时其它 GX 工具仍不可用。

### P1 — Worker 启动和无副作用探针

- [ ] 先写一个不调用 GX API 的 Framework Worker：实现 `ExternalWorkerProtocol.cs` 的 handshake/doctor，stdout 仅输出协议 JSON，诊断写 stderr。
- [ ] pinned worker name/version/protocol；传输大小上限、超时、取消、进程树终止均有假 worker 测试。
- [ ] 做不加载工程、不连接 PLC 的依赖加载探针；记录真实 stdout/stderr/exit code/位数/程序集依赖。
- [ ] 再选择并证明唯一受控的 ServiceBus 初始化与一个只读 service 获取路径。失败时标为 Unsupported，不试图调用 internal broker 绕过环境。

**完成门槛：** 握手成功、身份固定、依赖版本可报告、退出与超时可控。单有 handshake 不等于厂商接口验证成功。

### P2 — 工程副本和离线只读

- [ ] 打开受允许目录内的 disposable working copy；确认原工程不被传入给 vendor worker。
- [ ] 明确打开、关闭、锁释放、异常清理行为。
- [ ] 先选一个只读导出入口（推荐先调查 PLCopen XML export 或 PLCFormat 的程序/FB getter），验证输出格式和 hash。
- [ ] 添加 validate/labels/compare 时只调用方法级 allowlist；任何写方法默认拒绝。
- [ ] 结果携带实际工件、来源版本、运行状态、hash 和 Unsupported/Experimental 证据。

**完成门槛：** 小型匿名/授权样本可稳定重复导出；工件能解析/回读；原工程前后 SHA-256 完全相同；异常工程能干净退出。

### P3 — 在线读取（本次不做）

只有用户单独批准现场验证后另立任务；先确认 PLC 目标、CPU、站号、凭证边界、连接生命周期、只读行为和审计，不复用离线工具授权。下载/写入/模式切换/Force/内存 destructive 继续不开放。

## 6. Job/协议建议

- Host 继续使用 `ExternalEngineeringWorker` 的 `handshake`、`submit`、`status`、`artifacts`、`cancel`；worker `operation` 只接受枚举字符串，例如 `InspectProject`、`ExportPou`、`ValidatePou`、`DiffProjects`。
- 新增一个操作前：确认 `EngineeringJobType` 是否表达了同等语义。若需要不同的导出粒度，新增强类型选项并校验 enum，不传任意程序集方法名。
- Artifact 必须用受限相对路径返回，由 host `ValidateAndVerifyArtifact` 检查路径位于 workcopy、文件存在、字节数受限、SHA-256 匹配。
- worker 自报 completed 不足以宣称成功；编译类操作至少要有可校验产物/诊断。外部 worker 结果依当前实现最多为 `Experimental`，不可自我升级为 Supported。
- 失败回 MCP 时剥离敏感堆栈、凭证、客户工程绝对路径；保留可诊断错误码、工具版本及经过净化的 stderr 摘要。

## 7. 测试与验收清单

- **发现测试：** 默认路径、本机 `D:\gwork2\GPPW3`、显式路径、注册表缺失/权限错误、文件版本错误、错误位数、错误 SHA。
- **安全测试：** GX worker 任意 operation 均拒绝，除只读 allowlist 里的明确 job；禁止参数注入、相对路径穿越、junction/reparse point 越界；源码前后哈希不变。
- **协议测试：** handshake 版本/身份错误、坏 JSON、Worker 退出、超时、cancel、输出超限、重复/乱序状态、stderr 不污染 stdout。
- **Artifact 测试：** 伪造相对/绝对路径、路径逃逸、缺文件、超大小、SHA 错误均失败关闭。
- **API 测试：** 无 GX 环境时 `Unsupported`；实际 GX 版本中逐个验证初始化、只读调用、结果语义、资源关闭；失败不计为已支持。
- **现场安全：** P0–P2 测试不得建立 PLC 连接。若日志/网络监测无法证明没有连接，应停止测试并重新设计边界。
- **全套回归：** `dotnet test D:\PLCMCP\PlcMcp.slnx -c Release`（仓库已实际使用的解决方案测试命令）。

每次 capability 从 `Unsupported` 到 `Experimental` 的提升，附上版本、进程 bitness/runtime、stdout/stderr、退出码、调用的精确接口/方法、工程副本前后 hash 和工件 hash；不可只记“接口存在”。

## 8. 明确不做的事

- 不把整份 GX Works3 `Service\Managed` DLL 复制进仓库、提交制品或再分发；只允许引用用户本机受许可的安装文件。
- 不直接修改 `.gx3/.gwx` 私有工程数据库；通过经验证的厂商导出服务或标准交换格式读取。
- 不在 MCP Server 主进程里用 Reflection/COM/P/Invoke 加载全部 GX 程序集。
- 不靠 MCP `readOnlyHint`/`destructiveHint` 取代工具 allowlist、权限和 worker policy。
- 不将 `Service.config` 的映射当成服务调用成功证据；不把 C++/CLI 的 internal broker 当公开 API。
- 不把在线读写、工程 XML Import、密码接口塞进首期离线导出工具。

## 9. 调研源文件与反编译证据

本机只读取程序集元数据/接口签名，未编辑厂商文件：

```text
D:\gwork2\GPPW3\Service\Service.config
D:\gwork2\GPPW3\Service\Managed\Melco.GXW3.Controller.Project.Managed.DataOperationController.dll
D:\gwork2\GPPW3\Service\Managed\Melco.GXW3.Controller.ExternalData.Managed.PLCFormat.dll
D:\gwork2\GPPW3\Service\Managed\Melco.GXW3.Controller.Project.Managed.XmlImportExport.dll
D:\gwork2\GPPW3\Service\Managed\Melco.GXW3.Controller.Checker.Managed.dll
D:\gwork2\GPPW3\Service\Managed\Melco.GXW3.Controller.ReferenceData.Managed.Label.dll
D:\gwork2\GPPW3\Service\Managed\Melco.GXW3.Controller.Monitor.Managed.dll
D:\gwork2\GPPW3\Service\Managed\Melco.GXW3.Controller.Online.Managed.OnlineOperation.DeviceMonitor.dll
D:\gwork2\GPPW3\Service\Managed\Melco.GXW3.Controller.Online.Managed.OnlineOperation.CommonOperation.dll
D:\gwork2\GPPW3\Service\Managed\Melco.GXW3.Controller.Online.Managed.OnlineOperation.PLCDataOperation.dll
D:\gwork2\GPPW3\Service\Managed\Melco.GXW3.Controller.Online.Managed.OnlineOperation.MemoryOperation.dll
D:\gwork2\GPPW3\Melco.GXW3.ServiceBus.dll
```

更多接口方法和 `Service.config` Managed/Native 服务映射见 [`gxworks3-mcp-integration-research.md`](gxworks3-mcp-integration-research.md)。反编译清单不是完整二进制 API 数据库；本交接文档刻意只记录与 MCP 离线/只读能力直接相关的接口族与安全边界。
