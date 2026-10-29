# PLC-AI-Assistant

**面向西门子、欧姆龙、三菱、汇川、CODESYS 等多品牌 PLC 的 AI 工程助手与 MCP 服务框架。**

让支持 MCP 的 AI 客户端通过统一接口查询控制器能力、浏览变量、读取数据、生成与编译工程、离线诊断厂商软件环境，并在模拟/仿真环境中验证变更计划。多品牌策略坚持**证据驱动**：本机安装、版本、位数、进程握手、退出码、输出工件和 SHA-256 都必须可核对；接口名称或 DLL 存在本身不会被当作可用能力。

**QQ 交流群：462720530** — 欢迎交流 PLC 与 AI/MCP 集成、多品牌协议适配、工程自动化和使用反馈。反馈现场问题时请脱敏工程文件、网络地址与设备凭据。

仓库：<https://github.com/xzcnmb/PLC-AI-Assistant>

[使用说明](docs/usage.md) · [调研与架构](docs/research-and-architecture.md) · [开发契约](docs/contracts.md) · [厂商接入计划](docs/vendor-integration-plan.md) · [汇川集成任务书](docs/inovance-integration-plan.md)

## 整体能力总览

| 能力 | 当前状态 | 实际证据 |
|---|---|---|
| MCP stdio | 已验证 | initialize、ping、tools/list、tools/call、notification 静默处理；日志只写 stderr |
| 四品牌协议只读（S7/FINS/SLMP/Modbus TCP） | Experimental | localhost 回环、帧结构和异常路径已测试；真实 CPU/固件仍需台架验证 |
| 四协议参数写帧 | Experimental（协议层） | localhost 帧构造/响应测试；物理 Runtime 与 MCP 写入口保持关闭 |
| 模拟写入 | 已验证（模拟目标） | 类型/范围、状态哈希、一次性计划 token、进程内锁；不接触 PLC |
| 有限时长在线监控 | Experimental | `plc_monitor_window` 有界、限速只读采样；批读不是一致扫描快照 |
| 西门子 SMART 工程桥 | Experimental | MicroWIN SMART V2 工作副本分析与网络验证；不连接 PLC，不处理 TIA Openness |
| CODESYS ScriptEngine 离线链 | Experimental（离线工程） | 本机 CODESYS 3.5.22.30 / SP22 Patch 3 / ScriptEngine 4.2.0.0 真实 handshake、doctor、shutdown 通过；工程操作只在工作副本，结果最高 Experimental |
| Mitsubishi GX Works3 诊断 | Verified（P0 诊断） | GXW3.exe 1.128.0.1、PE x86、.NET Framework 4.8 x86 独立 metadata worker handshake/doctor/shutdown 通过；工程 API/ServiceBus 未初始化 |
| Omron Sysmac/CX 诊断 | Verified（P0 诊断） | Sysmac Studio 1.60.0.64010、CX-Server 5.1.1.4 静态文件/版本/PE/哈希/COM 注册表观察；不激活 COM、不启动 IDE |
| Omron IEC/PLCopen XML、AutomationML | Experimental（结构解析） | 受限工作目录、4 MiB/节点/深度上限、DTD/XXE 防护、结构枚举、稳定比较与扩展 hash；不是 Sysmac 编译器 |
| **汇川（AutoShop/H-Easy）深度集成** | 见下方专节 | 工程生成/打开/编译/下载/RUN-STOP/读写/轴组态，本机 AutoShop 4.12.0.2 + h5u_daemon 模拟器实证 |
| HMI 离线组态 | Experimental | 校验变量绑定、报警、配方、多语言；厂商中立 JSON/CSV 产物 |
| 治理基座 | 已验证 | 审计哈希链、作业崩溃隔离、目标租约、外部审批契约 |
| 跨品牌编译/下载/RUN-STOP/Force/在线写入 | Unsupported（除汇川模拟器闭环） | 没有可执行入口；危险能力在 Runtime/Worker 层硬拒绝 |

真实协议能力均为 `experimental`，没有连接或写入现场设备。不能用本机测试替代精确 CPU/固件/IDE 版本的台架验收。S7-200 SMART 的工程软件是 Micro/WIN SMART，不是 TIA Openness。

## 西门子

- **S7-1200/1500**：S7netplus 0.20.0 只读（S7comm），SMART V→DB1、DB/M/I/Q；localhost 已测，真机待验。
- **S7-200 SMART**：`--smart-project-root` 明确授权后，通过本机 MicroWIN SMART 对工作副本做离线检查（V2）与网络有效性验证；不连接 PLC，不处理 TIA Openness。

## 欧姆龙

- **FINS/TCP 只读**：节点协商、DM/CIO/W/H/A 字/位读取；localhost 已测，真机待验。
- **诊断链**：`plc_omron_doctor` 只读取安装文件、FileVersionInfo、PE 位数、SHA-256、注册表视图与 COM 注册信息；不实例化 COM、不启动 Sysmac/CX-Server、不连接 PLC。
- **标准交换文件**（配置受限于 `--smart-project-root` 后注册）：`plc_omron_exchange_inspect` 读取 IEC 61131-10/PLCopen XML 或 AutomationML CAEX，输出项目/POU/变量/类型/设备结构；`plc_omron_exchange_compare` 做稳定排序的结构差异与扩展 hash 比较。解析器限制文件 4 MiB、深度 64、节点 50,000，禁用 DTD/外部实体与网络 schema，`validationLevel=structural`、`isVendorCompiler=false`。

## 三菱

- **SLMP 3E binary 只读**：D/R/W 字与 M/X/Y/B 位；localhost 已测，真机待验。
- **诊断链**：`plc_gxworks3_doctor` 基于独立 .NET Framework 4.8 x86 探针（`src/PlcMcp.GxWorks3.Worker`），只做静态元数据诊断（GXW3.exe 与 `Service\Service.config` 等）；不启动 GXW3.exe、不加载 ServiceBus、不打开工程、不连接 PLC。启用参数：`--gxworks3-probe`、`--gxworks3-probe-sha256`、`--gxworks3-exe`、`--gxworks3-version`，四个必须同时出现，探针 exe 启动前再次做 SHA-256 校验。`plc_gxworks3_inspect/export/compile` 因 ServiceBus/工程服务未被证明可安全独立初始化，暂不注册。

## CODESYS

- **离线 ScriptEngine 链**：`codesys_worker.py` 在 CODESYS 进程内通过 stdin/stdout 行 JSON-RPC 提供 `handshake`、`doctor`、`submit`、`status`、`artifacts`、`cancel`、`shutdown`。真实测试确认 `projects/system/online` 全局对象存在；不调用 ScriptOnline。支持离线语义：工作副本 inspect、PLCopen XML export、build/clean/rebuild，所有结果要求工件存在并通过 SHA-256 校验，状态最高 Experimental。
- 启用参数：`--codesys-exe`、`--codesys-profile`、`--codesys-version`、`--codesys-script`、`--codesys-script-sha256`、`--codesys-project-root`。doctor 通过后注册 `plc_codesys_inspect`、`plc_codesys_export`、`plc_codesys_compile`；`plc_codesys_doctor` 用于只读证据检查。login/logout/start/stop/reset/force/download/upload/save/import/delete/move 等 PLC 在线操作在宿主与脚本双层拒绝。

## 汇川（AutoShop / H-Easy）深度集成

汇川方向完成了从工程到运行时的完整链路，基于对 AutoShop V4.12.0.2 的逆向（**无厂商背书、版本锁定**），全部能力在本机 + h5u_daemon 模拟器实证，真机待台架验收。

### 运行时

- `InovanceModbusTcpAdapter`：H5U/H3U 软元件（D/R/M/X/Y/S/T/C/DD）经显式映射表转 Modbus TCP，不做隐式换算，字节序显式配置；模拟器端到端实证（quality=good）。
- 示例只读配置：`profiles/inovance.example.json`、`profiles/inovance.simulator.example.json`（指向本机模拟器，真机须替换地址并核对映射）。

### 工程与编译（12 个工具，`--autoshop-bridge` 启用）

| 工具 | 作用 | 门控 |
|---|---|---|
| `plc_autoshop_status` / `plc_autoshop_inspect` | 环境/服务/模拟器体检 + 能力矩阵 | `--autoshop-bridge` |
| `plc_autoshop_simulator` | 本机 h5u_daemon 启动/停止/状态（PID 归属保护） | 同上 |
| `plc_autoshop_generate_project` | 离线生成 .hcp 工程（UCodeCrypt 加密层已破解） | 同上 |
| `plc_autoshop_open_project` | 注入桥实例打开工程（实测 7-11s） | 同上 |
| `plc_autoshop_compile` | 进程内触发全部编译，产物解密摘要 | 同上（Experimental） |
| `plc_autoshop_download` | 编译产物经 12939 协议传到本地模拟器 | 默认仅 127.0.0.1；真机需 `--autoshop-allow-physical-download`+审批 |
| `plc_autoshop_run_stop` | RUN/STOP 控制（模拟器寄存器变活实证） | 仅回环；物理 CPU 不支持 |
| `plc_autoshop_monitor_read` | 注入进程内通信栈读取（当前调用级证据） | 同上 |
| `plc_autoshop_write_register` | Modbus FC5/6 写寄存器/线圈，回读闭环 | 默认仅模拟器；真机需 `--autoshop-allow-physical-write`+审批 |
| `plc_autoshop_axis_config` | 轴组态读/改/重打包（14 类型化字段） | 同上 |
| `plc_autoshop_read_shared_variables` | ITP 共享变量有界观察（无发布方如实报） | 同上 |

完整闭环示例（模拟器）：simulator start → generate → open → compile → download → run → monitor_read，见 [docs/usage.md](docs/usage.md) 第 11 节。

**明确未实现/保持关闭**（均有实证理由）：经 AutoShop 写元件（厂商导出为桩 0xF390）；Force（实测会使 AutoShop 崩溃）；程序装载进模拟器运行时（0x82 桩）；监视表真值回填（进程内会话建立未逆向，当前为调用级证据）；实体 PLC 下载/强制/RUN-STOP（真机未验，需审批门控）。

汇川逆向证据文档集中在 `docs/inovance-*`（18 篇）：容器格式、工程加密破解、编译管线、下载协议、注入桥、模拟器、轴组态、监视值回填、CLI 语义等，见 [docs/inovance-integration-plan.md](docs/inovance-integration-plan.md)。

## MCP 工具概览

**始终注册：**

- `plc_list_targets`、`plc_get_capabilities`、`plc_list_tags`、`plc_browse_symbols`
- `plc_probe_target`、`plc_read_tags`、`plc_monitor_window`
- `plc_plan_write`、`plc_apply_write`（仅模拟目标）
- `plc_doctor`、`plc_get_capabilities_report`、`plc_get_audit`、`plc_get_job`
- `plc_lint_program`、`plc_compare_projects`
- `plc_hmi_validate`、`plc_hmi_generate`

**条件注册**（显式开关/门禁满足才出现，绝不静默）：

- 西门子：`plc_smart_inspect`、`plc_smart_validate`（`--smart-project-root`）
- CODESYS：`plc_codesys_doctor`、`plc_codesys_inspect`、`plc_codesys_export`、`plc_codesys_compile`（完整参数 + doctor 门禁）
- 三菱：`plc_gxworks3_doctor`（探针参数 + SHA-256 门禁）
- 欧姆龙：`plc_omron_doctor`、`plc_omron_exchange_inspect`、`plc_omron_exchange_compare`
- 汇川：`plc_autoshop_*` 共 12 个（`--autoshop-bridge`）

工具 annotations 只是客户端提示，真正的拒绝规则在 Worker、Runtime 和安全策略层。

## 构建、测试和启动

项目目标框架 net8.0；本机使用 SDK 10 验证（缺 .NET 8 运行时时用 `DOTNET_ROLL_FORWARD=Major` 运行测试）。GX Works3 探针是独立的 .NET Framework 4.8 x86 工程。

```cmd
dotnet build D:\PLCMCP\PlcMcp.slnx -c Release
dotnet test D:\PLCMCP\PlcMcp.slnx -c Release
dotnet D:\PLCMCP\src\PlcMcp.Server\bin\Release\net8.0\PlcMcp.Server.dll
```

测试：全量通过 / 0 失败（含 6 个环境门控用例；`PLCMCP_LOCAL_SIMULATOR=1` 时在本机执行汇川真实链路：生成→打开→编译→下载→RUN→监视）。各厂商验证链（GX Works3 探针、CODESYS ScriptEngine、汇川注入桥）均在对应真实安装上做过 handshake/doctor 实证。

原生工具（汇川 x86 注入桥、通信栈垫片、CryptoHost）：见 [tools/AutoShopInjectBridge/README.md](tools/AutoShopInjectBridge/README.md) 与 [tools/AutoShopNativeBridge/README.md](tools/AutoShopNativeBridge/README.md)，VS2022 v143 x86 构建，二进制产物不入仓库。

无参数启动只加载模拟目标和基础 MCP 工具，不建立 PLC 网络连接。配置错误会直接退出，不静默回退到模拟目标。

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

[配置样例](profiles/readonly.example.json) 使用文档保留地址，启动时不会连接。先换成实际目标、经过核对的地址和字节序，再由明确的 `probe/read` 工具调用建立连接。错误配置直接退出，禁止静默回退为模拟。

- S7：准确指定 family、rack、slot；支持 SMART 的 V 地址映射；优化 DB/S7comm-plus 不支持。
- FINS：仅本地网络 TCP，32/64 位变量显式设置 byteOrder；没有 UDP、EM bank 或跨网路由配置。
- SLMP：仅 binary 3E、本地站；X/Y/W/B 号码按十六进制，D/R/M 按十进制。
- Modbus：`HR100` 是零基保持寄存器 100；32/64 位值要求 byteOrder。
- Inovance：软元件 D/R/M/X/Y/S/T/C/DD 经显式映射表（H5U/H3U）转换，不做隐式换算；32/64 位值必须显式 byteOrder；`C` 前缀在汇川目标上按计数器软元件解析。
- byteOrder：BigEndian/ABCD、LittleEndian/DCBA、WordSwap/CDAB、ByteSwap/BADC。

每批最多 128 个标签；单次网络读超时 100～30000 ms，默认 3000 ms，超时关闭该连接，不自动重复写或重复提交。

## 治理与安全声明

- 物理写/下载/强制/RUN-STOP 默认全部关闭；打开需显式开关（如 `--autoshop-allow-physical-*`）+ 审批；汇川动作仅 127.0.0.1 回环（模拟器）可自动放行。
- 各厂商接口均以证据为准：安装探测不等于授权、编译能力或实际设备兼容性；逆向所得接口（汇川）无厂商背书、版本锁定 4.12.0.2。
- `approvalToken` 只是模拟计划消费令牌，不构成人工身份认证；计划最长 5 分钟，正确/错误 token 的一次尝试都会消费计划。
- 每项写入前记录 `write_intent`，成功后记录 `write_tag`；逐标签执行不保证整批原子提交。
- 跨品牌工程编译、现场下载、RUN/STOP、Force、厂商原生 HMI/硬件组态和真机验证仍未完成，不能替代完整的 PLC 工程与现场验收流程。使用问题可在仓库提交 Issue，附软件版本、已脱敏配置、错误信息、复现步骤和期望行为。