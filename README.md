# PLC-AI-Assistant

**面向西门子、欧姆龙、三菱、汇川等 PLC 的 AI 工程助手与 MCP 服务框架。**

让支持 MCP 的 AI 客户端通过统一接口查询控制器能力、浏览变量、读取数据，并在模拟环境中验证变更计划。长期目标是接入厂商工程软件，覆盖程序编写、编译、调试、下载、硬件与 HMI 组态。

当前版本仍属**开发中原型**：四协议只读通信与模拟写入之外，新增了四协议参数写帧（**仅协议层/localhost 验证，物理写入口仍禁用**）、限速只读监控、安全的工程文件副本、ST 启发式预检、PLCopen XML 解析、厂商软件 doctor、HMI 离线组态校验/厂商中立产物、持久审计/作业与可选 MicroWIN SMART 离线工程桥。真实 PLC 下载、RUN/STOP、强制、厂商原生 HMI/硬件组态和跨品牌工程编译仍未完成，不能替代完整的 PLC 工程与现场验收流程。

**QQ 交流群：462720530** — 欢迎交流 PLC 与 AI/MCP 集成、多品牌协议适配、工程自动化和使用反馈。反馈现场问题时请脱敏工程文件、网络地址与设备凭据。

项目仓库：<https://github.com/xzcnmb/PLC-AI-Assistant>

[快速开始与使用说明](docs/usage.md) · [调研与设计](docs/research-and-architecture.md) · [开发契约](docs/contracts.md)

调研、品牌差异和后续工程方案见 [调研与架构决策](docs/research-and-architecture.md)，目录责任和接口规则见 [契约边界](docs/contracts.md)。

## 当前实际能力

| 功能 | 状态与证据 |
|---|---|
| MCP stdio | 支持 initialize、ping、tools/list、tools/call；协议基线 2024-11-05 |
| 四品牌内存目标 | Siemens/Omron/Mitsubishi/Inovance 标签示例，值标记 simulated；不执行 PLC 程序 |
| S7comm 读取 | S7netplus 0.20.0，SMART V→DB1、DB/M/I/Q；localhost 协商和读取已测，真机待验 |
| FINS/TCP 读取 | 节点协商、DM/CIO/W/H/A 字/位读取；localhost 已测，真机待验 |
| SLMP 3E binary 读取 | D/R/W 字与 M/X/Y/B 位；localhost 已测，真机待验 |
| Modbus TCP 读取 | HR/IR 数值、C/DI 位，零基地址；localhost 读取/异常/分段/超时已测 |
| 四协议参数写帧 | 写帧/响应/超时在 localhost 测试；**未接入物理 Runtime 与 MCP，真实目标不可写** |
| 符号浏览 | 读取配置 manifest，支持中英文别名；不是从 PLC 自动上传符号 |
| 模拟写入 | 范围/类型、一次性计划、状态哈希、执行前意图日志；仅影响内存 |
| 工程离线工具 | `plc_doctor`、ST 预检、PLCopen XML 比对、隔离工作副本；不是厂商编译 |
| SMART 本机工程桥 | `--smart-project-root` 明确授权后可离线检查 V2、验证网络；需要本机 MicroWIN SMART 及可选 smart200_mcp，不连接 PLC |
| 治理基座 | 审计哈希链、作业崩溃隔离、目标租约、外部审批契约；真实物理动作尚未接线 |
| 有限时长在线监控 | `plc_monitor_window` 对已配置目标做有界、限速只读采样；批读不是 PLC 原子扫描快照 |
| 通用 HMI 离线组态 | 校验变量绑定、报警、配方、多语言；在受控目录生成厂商中立 JSON/CSV 包及 SHA-256；**不能生成或下发 WinCC/GOT/NA 原生工程** |
| 外部厂商 Worker 协议 | 已实现显式白名单进程的 JSON-RPC 握手/任务/超时/工件接口；未安装 TIA/GX/Sysmac/InoProShop 或具体厂商实现时均 Unsupported |
| OPC UA/CIP/持续订阅 | 尚未实现客户端及订阅后端 |
| 跨品牌工程编译、现场下载、RUN/STOP、Force、厂商原生 HMI/硬件组态 | **未实现**，无执行入口 |

真实协议能力均为 `experimental`，没有连接或写入现场设备。不能用本机测试替代精确 CPU/固件/IDE 版本的台架验收。S7-200 SMART 的工程软件是 Micro/WIN SMART，不是 TIA Openness。本机只发现 MicroWIN SMART V2.8，未发现其他品牌 IDE；安装探测不等于授权、编译能力或实际设备兼容性。SMART 离线桥通过另行安装的 `smart200_mcp` Python 环境工作，该依赖与西门子 DLL 不随仓库发布。

## 构建和启动

项目目标框架 net8.0；本机使用 SDK 10.0.401 和 .NET 8 运行时验证。`.slnx` 需要支持该格式的 SDK（本机为 10）。SDK 8 可直接构建 Server 和 Tests 的 csproj。运行依赖 S7netplus，测试依赖 xUnit。

```cmd
dotnet build D:\PLCMCP\PlcMcp.slnx -c Release
dotnet test D:\PLCMCP\PlcMcp.slnx -c Release
dotnet D:\PLCMCP\src\PlcMcp.Server\bin\Release\net8.0\PlcMcp.Server.dll
```

无参数只加载 `sim-siemens`、`sim-omron`、`sim-mitsubishi`、`sim-inovance`，不创建任何 PLC 网络连接。启动 DLL 避免 `dotnet run` 的构建输出进入 MCP stdout。

MCP 客户端配置示例（只作示例，不会自动修改客户端设置）：

```json
{
  "mcpServers": {
    "plc-mcp": {
      "command": "dotnet",
      "args": ["D:\\PLCMCP\\src\\PlcMcp.Server\\bin\\Release\\net8.0\\PlcMcp.Server.dll"]
    }
  }
}
```

## 显式配置只读目标

[配置样例](profiles/readonly.example.json) 使用文档保留地址，启动时不会连接。先换成实际目标、经过核对的地址和字节序，再由明确的 `probe/read` 工具调用建立连接：

```cmd
dotnet D:\PLCMCP\src\PlcMcp.Server\bin\Release\net8.0\PlcMcp.Server.dll --config D:\PLCMCP\profiles\readonly.example.json
```

配置模式替换默认模拟目标；物理目标写工具不注册。错误配置直接退出，禁止静默回退为模拟。所有物理标签 `canWrite` 必须为 false。不根据配置里的品牌名猜测协议或字节序。

- S7：准确指定 family、rack、slot；支持 SMART 的 V 地址映射，优化 DB/S7comm-plus 不支持。
- FINS：仅本地网络 TCP，32/64 位变量显式设置 byteOrder；没有 UDP、EM bank 或跨网路由配置。
- SLMP：仅 binary 3E、本地站；X/Y/W/B 号码按十六进制，D/R/M 按十进制；端口由工程配置决定。
- Modbus：`HR100` 是零基保持寄存器 100，`IR100` 输入寄存器，`C100` 线圈，`DI100` 离散输入；不会把汇川 MW/D 地址自动换算。32/64 位值要求 byteOrder。
- byteOrder：BigEndian/ABCD、LittleEndian/DCBA、WordSwap/CDAB、ByteSwap/BADC；按工程数据定义核对。

每批最多 128 个标签；单次网络读超时 100～30000 ms，默认 3000 ms，超时关闭该连接，不自动重复写或重复提交。当前每批重建连接、逐标签读取，多点不是一致扫描快照，尚未实现连接池、PDU 批量合并或速率调节。

## MCP 工具

| 工具 | 输入/作用 |
|---|---|
| plc_list_targets | 列出本实例加载的目标和能力 |
| plc_get_capabilities | 可选 targetId；目标的能力状态是实际后端状态 |
| plc_list_tags / plc_browse_symbols | targetId、可选 filter；manifest 符号及别名 |
| plc_probe_target | targetId；S7 协商或 TCP 可达性，不冒充 CPU 模式/身份 |
| plc_read_tags | targetId、tags；返回类型、单位、质量、时间 |
| plc_plan_write | **模拟模式** targetId、changes、可选 lifetimeMinutes（最多 5） |
| plc_apply_write | **模拟模式** planId、approvalToken，同一进程内消费 |
| plc_doctor / plc_get_capabilities_report | 只读扫描本机软件版本与能力证据（不连接 PLC） |
| plc_lint_program / plc_compare_projects | ST 启发式预检、PLCopen XML 文件比对（不是厂商编译） |
| plc_get_audit / plc_get_job | 读取持久审计链和作业状态；坏审计日志会显式报错 |
| plc_smart_inspect / plc_smart_validate | 仅在显式启用 SMART 工程目录且本机桥可用时出现；先建立工作副本，不连接 PLC |
| plc_monitor_window | 有限窗口、限速只读采样；返回质量码、陈旧时间和变化摘要 |
| plc_hmi_validate / plc_hmi_generate | 校验离线 manifest；生成通用 JSON/CSV 到受控工作区，不发布 HMI |

stdin/stdout 每行一个 JSON 消息；日志仅 stderr。请先 initialize，再发送 initialized 通知。示例：

```jsonl
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"example","version":"1"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"plc_read_tags","arguments":{"targetId":"sim-siemens","tags":["目标压力","RunMode"]}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"plc_plan_write","arguments":{"targetId":"sim-siemens","changes":{"目标压力":"4.5"}}}}
```

工具响应为 `result.content:[{type:"text",text:"<JSON payload>"}]`、`result.isError`。v0.1 同时保留顶层领域字段以兼容现有调用；两份数据不是两次执行。工具 annotations 是客户端提示，真正拒绝规则在 Runtime/adapter 内。

## 模拟计划的准确语义

规划不改变标签。标签名和别名归一化，同一标签重复出现会拒绝。数值先转换为 PLC 类型再检查 min/max，null、NaN、Infinity、非法类型和小数截断被拒绝。目标级锁避免同一实例的调用交错，应用前重新比较被修改标签的状态哈希。

`approvalToken` 是模拟计划消费 token，**不构成人工身份认证或人工审批证明**。计划最长 5 分钟；正确或错误 token 的一次尝试都会消费计划。状态变化、过期或重复使用会失败，需重新规划。不会授予生产权限。

每项写入前记录 `write_intent`，成功后记录 `write_tag`。逐标签执行不保证整批原子提交；失败返回已完成的 Values 和数量，无自动回滚。模拟写入沿用内存计划与内存审计，进程退出后丢失；新建的 `FileAuditLog` 与 `FileJobStateMachine` 供工程作业治理使用，已做坏链拒绝追加和崩溃作业隔离，但尚未接管模拟写入的审计。SHA-256 哈希链没有外部锚点，不能防止掌握整个文件写入权限者重写历史。Physical write、force、RUN/STOP 和工程下载没有可执行路径。SMART 工程 worker 可以在独立工作副本调用本机编译器做验证，但此能力不等同跨品牌编译。

## 测试与局限

`tests/PlcMcp.Tests` 包含类型/范围、别名、状态漂移、MCP notification/annotations、协议固定帧与 loopback TCP 测试。所有 TCP 测试仅监听 127.0.0.1 临时端口，验证只读请求，不涉及现场网。

测试覆盖协议固定帧与本机回环（含只写参数区报文）、治理失败路径、工作副本、防 XXE、SMART 工程桥以及 MCP 工具。最终验收结果以本次 Release 构建和 `dotnet test` 的实际输出为准。构建后可运行 `python scripts/smoke_test.py` 验证独立进程的 stdio；脚本不会调用物理目标 probe/read。

内存模拟与自建协议服务器不能证明厂商互操作性；后续需用厂商仿真器和真实 CPU 做独立对照。除可选 MicroWIN SMART 桥外，其他品牌工程后端仍需安装授权 IDE、精确版本的 worker，并完成编译、往返、落盘和目标回读验证。仓库未集成 CODESYS/TIA/GX/Sysmac 自动编译下载；资源复用与许可见 [第三方说明](THIRD-PARTY-NOTICES.md)。
