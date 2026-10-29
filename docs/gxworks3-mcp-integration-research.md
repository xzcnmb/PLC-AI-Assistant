# GX Works3 调研与 MCP 接入清单

> 调研对象：本机 Mitsubishi GX Works3。本文记录本机安装证据、已发现的内部接口线索，以及接入 `PlcMcp` 的分阶段建议。
>
> **重要范围声明：** 本文不是 Mitsubishi 官方 SDK 文档。接口名称来自本机安装目录中的配置、Managed 程序集和反编译结果；内部接口可能随 GX Works3 版本变化。调研期间没有连接 PLC，也没有执行下载、写入、RUN/STOP、Force 或内存清除操作。

## 1. 本机环境证据

| 项目 | 结果 |
|---|---|
| 产品 | Mitsubishi GX Works3 |
| 注册表安装证据 | 32 位视图 `HKLM\SOFTWARE\WOW6432Node\MITSUBISHI\SWnDN-GPPW3`；`CurrentVersion\InstallPath=D:\gwork2\GPPW3\`、`MajorVersion=1`、`MinorVersion=128J` |
| `GXW3.exe` 文件版本 | `1.128.0.1` |
| `GXW3.exe` PE 位数 | x86 / 32-bit（PE machine `0x14c`） |
| 实际安装根目录 | `D:\gwork2\` |
| GX Works3 主程序 | `D:\gwork2\GPPW3\GXW3.exe` |
| 厂商 | `MITSUBISHI ELECTRIC CORPORATION` |
| 检测到的 MX Component ProgID | 未确认（常见 `ActUtlType`/`ActProgType` 项未注册） |

本机 32 位注册表视图存在 Mitsubishi 软件分支，包括 `SWnDN-GPPW3`、`MelsecEasysocket`、`EZSocketMc` 等，但这些注册项不能单独证明存在面向第三方的稳定公共 API。64 位进程直接读取 `SOFTWARE\MITSUBISHI` 可能看不到 GX Works3，检测器必须显式打开 `RegistryView.Registry32`。此前流传的 `1.128.04519` 没有在当前核验的注册表键或文件资源中复现，不能作为本机事实。

## 2. 已发现的 GX Works3 内部服务

`D:\gwork2\GPPW3\Service\Service.config`（完整文件，已核对）和 `Service\Managed` 下的程序集显示 GX Works3 使用内部服务/代理分层。可按以下领域归类。注意：Service.config 没有 XmlImportExport 或 Online DeviceMonitor 服务键；这些接口不能因 DLL 存在就视为可独立获取。

### 2.1 工程数据与项目操作

相关程序集/线索：

```text
Service\Managed\Melco.GXW3.Controller.Project.Managed.DataOperationController.dll
```

核心接口线索：

```text
Melco.GXW3.Controller.Project.Managed.DataOperation.IDataOperation
```

已看到的能力方向：

- 创建、移动、重命名、删除工程数据
- 创建程序、Worksheet、SFC/Zoom 数据
- 获取设备范围和 IO 容量
- 查询参数数据组和参数项
- 检查单元标签使用情况
- 生成 FB
- 读取样例注释、运动注释和设备信息

该接口同时包含修改能力，因此不能原样映射成 MCP 通用 RPC。首期只允许只读查询，所有创建、移动、删除、重命名操作保持禁用。

### 2.2 PLC 格式与外部数据

相关程序集/线索：

```text
Service\Managed\Melco.GXW3.Controller.ExternalData.Managed.PLCFormat.dll
```

核心接口线索：

```text
Melco.GXW3.Controller.ExternalData.Managed.PLCFormat.IPLCFormatController
```

已看到的能力方向：

- 获取程序二进制数据
- 获取 FB 文件数据
- 获取 CPU、System、Safety CPU、Safety Unit 数据
- 读取和处理 Header
- 转换 SFC Step 范围
- 处理程序附加信息和 CRC
- 获取程序块密码信息
- 处理 Memory Dump

候选只读方法方向：

```text
GetPLCData_Program
GetPLCData_FBFile
GetPLCData_CPU
GetPLCData_System
GetHeaderData
TrimHeaderData
```

密码列表、Memory Dump 设置和本地设备/文件寄存器写入暂不接入。

### 2.3 在线设备监视

相关程序集/线索：

```text
Service\Managed\Melco.GXW3.Controller.Online.Managed.OnlineOperation.DeviceMonitor.dll
```

核心接口线索：

```text
Melco.GXW3.Controller.Online.Managed.OnlineOperation.DeviceMonitor.IDeviceMonitorController
```

已看到的能力方向：

- 启动、停止设备监视
- 连续设备读取
- 随机 Bit/Word/DWord/QWord 读取
- 程序监视块注册和释放
- Safety Device 监视状态读取

典型方法方向：

```text
StartMonitor
ReadDevice
StartMonitorRandom
ReadDeviceRandom
ProgramMonitorRegistBlock
SetProgramIDBlock
```

这些是在线能力，必须在独立的安全策略、目标 PLC 确认、超时和审计机制之后才能接入。

### 2.4 在线内存操作

相关程序集/线索：

```text
Service\Managed\Melco.GXW3.Controller.Online.Managed.OnlineOperation.MemoryOperation.dll
```

核心接口线索：

```text
IMemoryOperationController
```

已看到的能力方向：

- 读取 PLC 运行状态
- 获取驱动器容量
- 读取驱动器文件
- 读取 Label Memory
- 写入 Label Memory
- 清除/格式化内存
- 设置标题
- 用户认证

首期只考虑运行状态、驱动器容量和受限只读数据；写入、清除、格式化和标题设置保持硬拒绝。

### 2.5 监控与比较服务

安装目录还包含 Monitor、RealTimeMonitor、WaveMonitor、BinaryWatch、Checker、Comparison，以及标签、注释、ReferenceData 和符号索引相关代理程序集。它们适合作为后续离线检查、比较、标签提取和在线监视的候选来源，但尚未完成独立启动、参数完整性和跨版本兼容性验证。

### 2.6 反编译接口进一步核对

对本机以下 Managed 程序集做了只读元数据/接口反编译；这些文件的 `TargetFrameworkAttribute` 均为 `.NETFramework,Version=v4.0`，CLR 元数据版本为 `v4.0.30319`：

```text
Service\Managed\Melco.GXW3.Controller.Project.Managed.DataOperationController.dll
Service\Managed\Melco.GXW3.Controller.ExternalData.Managed.PLCFormat.dll
Service\Managed\Melco.GXW3.Controller.Project.Managed.XmlImportExport.dll
Service\Managed\Melco.GXW3.Controller.Checker.Managed.dll
Service\Managed\Melco.GXW3.Controller.Monitor.Managed.dll
```

| 接口 | 确认到的代表方法/能力 | 安全说明 |
|---|---|---|
| `IDataOperation` | `GetDeviceRange`、`GetNumOfIOAccessibleInfo`、参数组/简单参数信息、`IsUnitLabelUsed`；也有 `CreateData`、`MoveData`、`RenameData`、`DeleteData`、Worksheet/Zoom 创建和 FB 生成 | 混合只读与工程变更；必须按方法白名单，不能整体开放 |
| `IPLCFormatController` | `GetPLCData_Program`、`GetPLCData_FBFile`、`GetPLCData_CPU`、`GetPLCData_System`、`GetPLCData_SafetyCPU`、`GetPLCData_SafetyUnit`、`GetHeaderData`、`TrimHeaderData` | 适合作为离线导出候选；同时含 `SetPLCData_MemoryDump*` 写入和 `GetBlockPasswordList` 敏感能力 |
| `IXmlImportExport` | 多个 `Export`/`ExportObject`/`ExportObjectEncrypted`、`Import`/`ImportProjectEncrypted`、`DecryptXmlFile` | 仅显式白名单的导出考虑开放；导入和解密需独立风险评估，首期禁用 |
| `ICheckerController` | `DeviceCheck`、`InstructionCheck`、`CheckDataName`、`CheckLabelName`、参数检查 | 离线校验候选；应先校验 CPU/工程上下文并映射返回错误 |
| `ILabelReferenceDataController` | `SeeDeviceAssign`、`SeeRowDataByLabelID`、`GetAllLabelNameAndType`、`GetRowDataAllGlobalAndLocal`、注释读取 | 可做标签只读提取；该接口亦含 `UpdateElementwiseCommentByInstanceName`，不可整体透传 |
| `IMonitorController` | 监视启动/停止、`MMRandomDeviceStart/GetValue/Stop`、SFC 活动步读取；亦有 `ModifyValue`、`ModifyStepActivity`、`ChangeBitLabelValue` | 这是在线服务且同一接口含写值/改步功能；首期不要用这个接口实现读功能，优先研究 `DeviceMonitor` 只读子接口 |
| `IDeviceMonitorController` | `StartMonitor`、`ReadDevice`、`StartMonitorRandom`、`ReadDeviceRandom`、停止监视 | 在线读取线索；必须先证明 ServiceBus 初始化、连接对象来源和只读语义 |
| `ICommonOperationController` | `GetPlcRunStatus`、`GetPlcMemoryCapacity`、文件列表读取；亦有 `SetPlcStatusMode`、`ExecutePlcMemoryFormat`、`ExecutePlcMemoryClear`、写 Label Data | 状态/容量读取候选；模式切换、格式化、清除、Label 写入绝对禁止首期开放 |
| `IWriteDeviceController` | `WriteDevice`、`SetInitialParameter` | 在线写设备接口；硬拒绝，不注册 MCP |
| `IPLCDataOperationReadController` / `IPLCDataOperationWriteController` | PLC 上传/下载读写、在线文件读取、CPU 状态和程序传输流程 | 名称不能当权限界限：Write controller 也含 `PLCWrite`、`SetStatus`、`PLCDriveDeleteFileGet` 等，整体禁止 |
| `ISecurityController` | `AdministerMemCardFilePassword`、`AdministerBlockPassword` | 密码管理接口；不注册、不记录秘密、不尝试绕过授权 |

#### 接口存在不等于 ServiceBus 中可获取

`Service\Service.config` 同时列出 Managed 和 Native 服务。核对到：

- `ExternalData.Managed.PLCFormat.IPLCFormatController` 与 Native PLCFormat 服务均有配置条目。
- `ReferenceData.Managed.Label.ILabelReferenceDataController`、`Checker.Managed.ICheckerController`、`Monitor.Managed.IMonitorController` 有 Managed 服务条目。
- 工程数据配置条目是 `Project.Native.DataOperation.IDataOperation`；仅在 Managed 程序集中反编译到的 `Project.Managed.DataOperation.IDataOperation`，不能据此认定它被该 ServiceBus 配置实例化。
- XML 导入导出、在线 DeviceMonitor 等程序集接口未在所核对的 `Service.config` 片段中建立已可调用的第三方入口证据。
- `Melco.GXW3.ServiceBus.dll` 的 `IServiceBroker` 是 internal；它包含加载 Native/Managed 服务实现的方法。当前尚未找到/验证一条受支持、独立于 GX Works3 主程序的第三方初始化路径。

因此，后续实现必须先验证“能启动服务并取得接口”，不能从 DLL、接口名或配置条目直接推断运行成功，也不能将内部接口描述成官方 SDK。

## 3. 与当前 PlcMcp 架构的接入边界

当前仓库已经有外部工程 Worker 基础设施：

```text
src/PlcMcp.Engineering/Workers/External/ExternalEngineeringWorker.cs
src/PlcMcp.Engineering/Workers/External/ExternalWorkerProtocol.cs
src/PlcMcp.Engineering/Workers/External/ExternalWorkerSecurityPolicy.cs
src/PlcMcp.Engineering/Workers/External/VendorProfileDetectors.cs
```

建议架构：

```text
MCP Server
    |
    | JSON-RPC / framed worker protocol
    v
GX Works3 Worker（独立进程）
    |
    | 受控加载 Managed/Native 程序集
    v
GX Works3 工程副本 / 受控 ServiceBus
```

MCP Server 不应直接加载 GX Works3 的 Managed、C++/CLI 或 Native DLL。Worker 应负责位数匹配、程序集加载、工程副本、超时、崩溃清理和工件校验；Server 只处理 MCP 工具、参数验证、授权和结果映射。

当前检测器已有 GX Works3 默认路径线索，但本机实际路径是 `D:\gwork2\GPPW3\GXW3.exe`。接入前需补充配置化路径、注册表发现、版本检测和文件哈希校验，不能只依赖固定的 `Program Files` 路径。

## 4. MCP 工具清单

### P0：本机诊断与离线只读

| 工具 | 目的 | 风险 | 建议 |
|---|---|---:|---|
| `gxworks3_doctor` | 检查安装路径、版本、位数、依赖 DLL、程序集哈希 | 低 | 首期实现 |
| `gxworks3_project_inspect` | 读取工程结构、对象和基础元数据 | 低 | 首期实现 |
| `gxworks3_extract_labels` | 提取标签、注释、引用和设备映射 | 低 | 首期实现 |
| `gxworks3_export_program` | 导出程序数据或中间格式 | 中 | 先做副本和哈希校验 |
| `gxworks3_export_fb` | 导出 FB 数据 | 中 | 先验证往返完整性 |
| `gxworks3_export_cpu_data` | 导出 CPU/System 数据 | 中 | 限制输出大小 |
| `gxworks3_validate_project` | 调用离线检查/诊断服务 | 低 | 对照 GX Works3 UI 结果 |
| `gxworks3_compare_projects` | 比较工程、程序、标签、参数和注释 | 低 | 推荐优先开发 |

### P1：在线只读

| 工具 | 目的 | 必要保护 |
|---|---|---|
| `gxworks3_read_plc_status` | 读取 PLC 运行状态 | 目标 PLC 确认、超时、审计 |
| `gxworks3_read_device_batch` | 批量读取 Bit/Word/DWord/QWord | 地址验证、数量限制 |
| `gxworks3_monitor_devices` | 启停设备监视并读取采样 | 会话生命周期、取消和断线清理 |
| `gxworks3_read_drive_capacity` | 读取驱动器容量 | 只读、输出大小限制 |
| `gxworks3_read_drive_data` | 读取驱动器文件 | 路径白名单、大小限制 |
| `gxworks3_read_label_memory` | 读取 Label Memory | 只读、明确 PLC 目标 |

### P2：暂不开放

以下能力不要在第一版 MCP 中注册：

- `gxworks3_write_device`
- `gxworks3_write_label_memory`
- PLC 下载/上传覆盖操作
- `gxworks3_run`、`gxworks3_stop`
- Force I/O 或解除 Force
- 内存清除和格式化
- 设置 PLC/工程标题
- 获取或导出程序块密码
- Memory Dump 写入
- 任何未确认目标 PLC 身份的在线操作

`readOnlyHint`、`destructiveHint` 等 MCP 工具注解只是客户端提示，不能代替服务端授权和硬安全策略。

## 5. Worker 安全要求

GX Works3 Worker 至少应实现：

- 固定可执行文件路径和程序集版本检查
- x86/x64 位数匹配检查
- 独立进程隔离
- 单调用超时和最大输出限制
- 工程/输出目录白名单
- 默认使用工程副本，不直接覆盖原工程
- 工件 SHA-256 校验
- 进程崩溃后的临时目录清理
- 在线操作的目标 PLC 指纹确认
- 在线操作前后的审计记录
- 明确的取消、断线和资源释放路径
- 统一错误码，不把内部路径、堆栈和敏感信息直接返回客户端

工程修改类 API 即使在离线场景也应先禁用。若以后开放，必须采用显式操作名、用户确认、备份/回滚和落盘校验，不能把内部 `IDataOperation` 直接透传。

## 6. 分阶段实施计划

### P0：诊断 Worker

1. 增加 GX Works3 注册表和配置路径发现。
2. 检查 `GXW3.exe`、Managed/Native DLL 和版本哈希。
3. 确认 Worker 位数与依赖加载方式。
4. 实现 `gxworks3_doctor`，不连接 PLC、不保存工程。

### P1：工程副本读取

1. 确认是否可以脱离主界面初始化内部服务。
2. 打开工程副本并枚举工程对象。
3. 导出程序、FB、CPU/System 数据。
4. 记录输出工件哈希并验证关闭/重新打开。

### P2：离线分析

1. 标签、注释、交叉引用提取。
2. 工程验证和诊断结果映射。
3. 工程/程序/标签/参数差异比较。
4. 用真实样本验证导出结果与 GX Works3 UI 一致。

### P3：在线只读

1. 确认通信配置和登录要求。
2. 实现 PLC 身份/型号/序列信息确认。
3. 实现状态读取和受限批量设备读取。
4. 加入超时、断线、取消和审计测试。

### P4：在线写入（暂缓）

首期不实现。未来若确有需求，必须单独审批，并具备目标 PLC 指纹、用户确认、权限隔离、变更前快照、审计和回滚/恢复方案。

## 7. 当前未验证事项

以下事项不能仅凭反编译结果假设为可用：

1. 内部 ServiceBus 能否脱离 `GXW3.exe` 主界面独立初始化。
2. Managed 程序集依赖的 Native DLL 和正确加载顺序。
3. Worker 的位数要求。
4. 工程文件锁定、保存和副本打开行为。
5. `ulong ElementID` 与工程对象的稳定映射方式。
6. PLC 通信配置、登录认证和授权要求。
7. 设备地址语法及不同 CPU 的差异。
8. 内部错误码和异常信息的稳定性。
9. GX Works3 不同版本之间的程序集接口兼容性。
10. 程序/FB/CPU 数据导出的格式语义和往返完整性。
11. 在线读操作是否会隐式改变监视状态或工程状态。
12. 厂商是否对这些内部接口提供支持。

## 8. 结论

GX Works3 本机内部服务足以支持一条有价值的离线 MCP 接入路线：安装诊断、工程结构读取、标签提取、程序/FB/CPU 数据导出、离线检查和工程比较。当前证据不足以把这些内部代理当作稳定公共 SDK，也不足以安全开放在线写入能力。

推荐先完成 P0–P2，并将所有 GX Works3 能力标记为 Experimental，使用独立 Worker、工程副本和严格的输出校验。在线监视只能在完成目标 PLC 确认和通信安全验证后进入 P3；下载、写入、RUN/STOP、Force、内存清除和格式化不应进入第一版。

给后续实现 Agent 的接入步骤、仓库文件落点、方法级拒绝清单和验收条件见 [`gxworks3-agent-handoff.md`](gxworks3-agent-handoff.md)。
