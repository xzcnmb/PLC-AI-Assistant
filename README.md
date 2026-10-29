# PLC-AI-Assistant

**面向西门子、欧姆龙、三菱、汇川等 PLC 的 AI 工程助手与 MCP 服务框架。**

让支持 MCP 的 AI 客户端通过统一接口查询控制器能力、浏览变量、读取数据、生成与编译工程，并在模拟/仿真环境中验证变更计划。汇川（AutoShop H/Easy 系列）方向已完成深度集成：工程生成、打开、编译、下载、RUN/STOP、读写数据、轴组态均以受治理的 MCP 工具形式落地（本机 AutoShop 4.12.0.2 + h5u_daemon 模拟器实证，真机待台架验收）。

**QQ 交流群：462720530** — 欢迎交流 PLC 与 AI/MCP 集成、多品牌协议适配、工程自动化和使用反馈。反馈现场问题时请脱敏工程文件、网络地址与设备凭据。

项目仓库：<https://github.com/xzcnmb/PLC-AI-Assistant>

[快速开始与使用说明](docs/usage.md) · [调研与设计](docs/research-and-architecture.md) · [开发契约](docs/contracts.md) · [汇川集成任务书](docs/inovance-integration-plan.md)

## 当前实际能力

| 功能 | 状态与证据 |
|---|---|
| MCP stdio | 支持 initialize、ping、tools/list、tools/call；协议基线 2024-11-05 |
| 四品牌内存目标 | Siemens/Omron/Mitsubishi/Inovance 标签示例，值标记 simulated；不执行 PLC 程序 |
| S7comm 读取 | S7netplus 0.20.0，SMART V→DB1、DB/M/I/Q；localhost 协商和读取已测，真机待验 |
| FINS/TCP 读取 | 节点协商、DM/CIO/W/H/A 字/位读取；localhost 已测，真机待验 |
| SLMP 3E binary 读取 | D/R/W 字与 M/X/Y/B 位；localhost 已测，真机待验 |
| Modbus TCP 读取 | HR/IR 数值、C/DI 位，零基地址；localhost 读取/异常/分段/超时已测 |
| **汇川运行时读取** | `InovanceModbusTcpAdapter`：H5U/H3U 软元件（D/R/M/X/Y/S/T/C/DD）显式映射到 Modbus TCP，不做隐式换算；字节序显式配置；AutoShop 自带 h5u_daemon 模拟器端到端实证（quality=good） |
| **汇川工程生成/打开** | `.hcpp`（PPCH 容器）解析/生成（HcppReader/HcppWriter）+ CUCodeCrypt 工程加密破解（密钥 "SclSoftware"，明文 UTF-16LE XML）；`plc_autoshop_generate_project` / `plc_autoshop_open_project`，本机 AutoShop 实测打开成功 |
| **汇川编译** | 进程内编译管线（注入桥）：`plc_autoshop_compile`，编译产物 `.compile`/`.foid`/重打包工程落盘并解密一致 |
| **汇川下载/RUN-STOP** | `plc_autoshop_download`（12939 文件传输协议，字节级落盘实证）+ `plc_autoshop_run_stop`（RUN 令模拟器寄存器 0→4660 变活实证） |
| **汇川写寄存器** | `plc_autoshop_write_register`（Modbus FC5/6 回读闭环；默认仅模拟器回环，真机需显式开关+审批） |
| **汇川轴组态** | `plc_autoshop_axis_config`：结构级读/改/重打包（14 类型化字段）；厂商消费未验 |
| 模拟器管理 | `plc_autoshop_simulator` 启动/停止/健康（PID 归属保护）；`plc_autoshop_status`/`inspect` 环境体检 |
| 符号浏览 | 读取配置 manifest，支持中英文别名；不是从 PLC 自动上传符号 |
| 工程离线工具 | `plc_doctor`、ST 预检、PLCopen XML 比对、隔离工作副本 |
| SMART 本机工程桥 | `--smart-project-root` 明确授权后可离线检查 V2、验证网络；需要本机 MicroWIN SMART |
| 治理基座 | 审计哈希链、作业崩溃隔离、目标租约、外部审批契约 |
| 有限时长在线监控 | `plc_monitor_window` 有界、限速只读采样 |
| 通用 HMI 离线组态 | 校验变量绑定、报警、配方、多语言；厂商中立 JSON/CSV 产物 |

**明确未实现/保持关闭**（均有实证理由，详见 docs/）：经 AutoShop 写元件（厂商导出为桩 0xF390）；Force（实测会使 AutoShop 进程崩溃）；程序装载进模拟器运行时（0x82 桩）；监视表真值回填（进程内会话建立未逆向，当前为调用级证据）；实体 PLC 下载/强制/RUN-STOP（真机未验，需审批门控）；OPC UA/CIP/持续订阅未实现。

真实协议/工程能力均为 `experimental`：本机 AutoShop V4.12.0.2 + h5u_daemon 模拟器实证，**真机未经台架验收**；逆向所得接口无厂商背书、版本锁定 4.12.0.2，不能用本机测试替代精确 CPU/固件/IDE 版本的现场验收。S7-200 SMART 的工程软件是 Micro/WIN SMART，不是 TIA Openness。

## 构建和启动

项目目标框架 net8.0；本机使用 SDK 10 验证（缺 .NET 8 运行时时用 `DOTNET_ROLL_FORWARD=Major` 运行测试）。

```cmd
dotnet build D:\PLCMCP\PlcMcp.slnx -c Release
dotnet test D:\PLCMCP\PlcMcp.slnx -c Release
dotnet D:\PLCMCP\src\PlcMcp.Server\bin\Release\net8.0\PlcMcp.Server.dll
```

测试：763 通过 / 0 失败（+6 个环境门控用例；`PLCMCP_LOCAL_SIMULATOR=1` 时在本机执行真实链路：生成→打开→编译→下载→RUN→监视）。

原生工具（x86 注入桥、通信栈垫片、CryptoHost/解密 harness）：见 [tools/AutoShopInjectBridge/README.md](tools/AutoShopInjectBridge/README.md) 与 [tools/AutoShopNativeBridge/README.md](tools/AutoShopNativeBridge/README.md)，VS2022 v143 x86 构建，二进制产物不入仓库。

无参数只加载 `sim-*` 模拟目标，不创建任何 PLC 网络连接。启动 DLL 避免 `dotnet run` 的构建输出进入 MCP stdout。

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

## 汇川能力（AutoShop / H-Easy）

启用全部汇川工具：

```cmd
dotnet D:\PLCMCP\src\PlcMcp.Server\bin\Release\net8.0\PlcMcp.Server.dll --autoshop-bridge
```

| 工具 | 作用 | 门控 |
|---|---|---|
| `plc_autoshop_status` / `plc_autoshop_inspect` | 环境/服务/模拟器体检 + 能力矩阵 | `--autoshop-bridge` |
| `plc_autoshop_simulator` | 本机 h5u_daemon 启动/停止/状态 | 同上 |
| `plc_autoshop_generate_project` | 离线生成 .hcp 工程（含加密层） | 同上 |
| `plc_autoshop_open_project` | 注入桥实例打开工程（实测 7-11s） | 同上 |
| `plc_autoshop_compile` | 进程内触发全部编译，产物解密摘要 | 同上（Experimental） |
| `plc_autoshop_download` | 编译产物经 12939 协议传到本地模拟器 | 默认仅 127.0.0.1；真机需 `--autoshop-allow-physical-download`+审批 |
| `plc_autoshop_run_stop` | RUN/STOP 控制（模拟器寄存器变活实证） | 仅回环；物理 CPU 不支持 |
| `plc_autoshop_monitor_read` | 注入进程内通信栈读取（当前调用级证据） | 同上 |
| `plc_autoshop_write_register` | Modbus FC5/6 写寄存器/线圈，回读闭环 | 默认仅模拟器；真机需 `--autoshop-allow-physical-write`+审批 |
| `plc_autoshop_axis_config` | 轴组态读/改/重打包 | 同上 |
| `plc_autoshop_read_shared_variables` | ITP 共享变量有界观察（无发布方如实报） | 同上 |

汇川运行时只读目标示例：

```cmd
dotnet D:\PLCMCP\src\PlcMcp.Server\bin\Release\net8.0\PlcMcp.Server.dll --config D:\PLCMCP\profiles\inovance.simulator.example.json
```

完整六步闭环示例（模拟器）：start → generate → open → compile → download → run → monitor_read，见 [docs/usage.md](docs/usage.md) 第 11 节。

## 汇川逆向证据文档（docs/）

| 文档 | 内容 |
|---|---|
| [inovance-integration-plan.md](docs/inovance-integration-plan.md) | 三阶段任务书：边界、所有权、红线、验收 |
| [inovance-autoshop-re-notes.md](docs/inovance-autoshop-re-notes.md) | 反编译情报总档（插件框架/通信栈/AI 助手） |
| [inovance-autoshop-project-format.md](docs/inovance-autoshop-project-format.md) | PPCH 容器格式（头/记录流/四种记录/真实模板 97 项校验） |
| [inovance-autoshop-program-blob.md](docs/inovance-autoshop-program-blob.md) | Main blob 结构（混淆层+加密 payload 熵证据） |
| [inovance-autoshop-project-encryption.md](docs/inovance-autoshop-project-encryption.md) | CUCodeCrypt 破解（算法/密钥/验证矩阵） |
| [inovance-autoshop-open-and-compile.md](docs/inovance-autoshop-open-and-compile.md) | 打开失败根因 + 编译管线入口（进程内触发） |
| [inovance-autoshop-write-signatures.md](docs/inovance-autoshop-write-signatures.md) | 写面 11 导出签名/枚举取值表/桩裁决 |
| [inovance-autoshop-download-protocol.md](docs/inovance-autoshop-download-protocol.md) | 12939 下载协议（帧格式/命令码/登录） |
| [inovance-autoshop-download-closedloop.md](docs/inovance-autoshop-download-closedloop.md) | 下载→RUN 闭环实证（寄存器变活证据） |
| [inovance-autoshop-monitor-values.md](docs/inovance-autoshop-monitor-values.md) | 读取结果回填机制（值槽/卡点） |
| [inovance-autoshop-axis-config.md](docs/inovance-autoshop-axis-config.md) | 轴组态模型/编辑管线 |
| [inovance-autoshop-inject-bridge.md](docs/inovance-autoshop-inject-bridge.md) | 进程内注入桥架构/三层门控 |
| [inovance-autoshop-simulator.md](docs/inovance-autoshop-simulator.md) | h5u_daemon 模拟器自动化层 |
| [inovance-autoshop-uia-map.md](docs/inovance-autoshop-uia-map.md) | UI 测绘与输入过滤结论 |
| [inovance-autoshop-cli.md](docs/inovance-autoshop-cli.md) | 命令行开关语义（-open 不存在等阴性结论） |
| [inovance-autoshop-encryption.md](docs/inovance-autoshop-encryption.md) | EnDecrypt.dll 密钥导出实验（证伪） |
| [inovance-autoshop-native-bridge.md](docs/inovance-autoshop-native-bridge.md) | 插件框架/通信栈垫片方法学 |
| [inovance-itp-protocol.md](docs/inovance-itp-protocol.md) | 共享变量服务 ITP 协议 |

## 显式配置只读目标

[配置样例](profiles/readonly.example.json) 使用文档保留地址，启动时不会连接。先换成实际目标、经过核对的地址和字节序，再由明确的 `probe/read` 工具调用建立连接。错误配置直接退出，禁止静默回退为模拟。

- S7：准确指定 family、rack、slot；支持 SMART 的 V 地址映射；优化 DB/S7comm-plus 不支持。
- FINS：仅本地网络 TCP，32/64 位变量显式设置 byteOrder；没有 UDP、EM bank 或跨网路由配置。
- SLMP：仅 binary 3E、本地站；X/Y/W/B 号码按十六进制，D/R/M 按十进制。
- Modbus：`HR100` 是零基保持寄存器 100，`IR100` 输入寄存器，`C100` 线圈，`DI100` 离散输入；32/64 位值要求 byteOrder。
- Inovance：软元件 D/R/M/X/Y/S/T/C/DD 经显式映射表（H5U/H3U）转换，不做隐式换算；32/64 位值必须显式 byteOrder；`C` 前缀在汇川目标上按计数器软元件解析。
- byteOrder：BigEndian/ABCD、LittleEndian/DCBA、WordSwap/CDAB、ByteSwap/BADC。

每批最多 128 个标签；单次网络读超时 100～30000 ms，默认 3000 ms，超时关闭该连接，不自动重复写或重复提交。当前每批重建连接、逐标签读取，多点不是一致扫描快照。

## 其他 MCP 工具（四品牌通用）

| 工具 | 输入/作用 |
|---|---|
| plc_list_targets / plc_get_capabilities | 列出本实例加载的目标和能力 |
| plc_list_tags / plc_browse_symbols | manifest 符号及别名 |
| plc_probe_target | 连接可达性检查（不冒充 CPU 模式/身份） |
| plc_read_tags / plc_monitor_window | 只读读取/有限窗口限速采样 |
| plc_plan_write / plc_apply_write | **模拟模式**计划与消费 token |
| plc_doctor / plc_lint_program / plc_compare_projects | 本机软件证据/ST 预检/PLCopen XML 比对 |
| plc_get_audit / plc_get_job | 持久审计链与作业状态 |
| plc_smart_inspect / plc_smart_validate | SMART 离线桥（显式启用时注册） |
| plc_hmi_validate / plc_hmi_generate | 离线 HMI manifest 校验/通用产物 |

stdin/stdout 每行一个 JSON 消息；日志仅 stderr。工具响应为 `result.content:[{type:"text",text:"<JSON payload>"}]`、`result.isError`。工具 annotations 是客户端提示，真正拒绝规则在 Runtime/adapter 内。

## 治理与安全声明

- 物理写/下载/强制/RUN-STOP 默认全部关闭；打开需显式开关（`--autoshop-allow-physical-*`）+ 审批；汇川动作仅 127.0.0.1 回环（模拟器）可自动放行。
- 汇川接口均为**逆向所得、无厂商背书、版本锁定 AutoShop 4.12.0.2**；模拟器实证不能替代真机台架验收。
- `approvalToken` 只是模拟计划消费令牌，不构成人工身份认证；计划最长 5 分钟，正确/错误 token 的一次尝试都会消费计划。
- 每项写入前记录 `write_intent`，成功后记录 `write_tag`；逐标签执行不保证整批原子提交。
- 跨品牌工程编译、现场下载、RUN/STOP、Force、厂商原生 HMI/硬件组态和真机验证仍未完成，不能替代完整的 PLC 工程与现场验收流程。使用问题可在仓库提交 Issue，附软件版本、已脱敏配置、错误信息、复现步骤和期望行为。