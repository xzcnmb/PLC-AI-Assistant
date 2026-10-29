# PLC-AI-Assistant 使用说明

QQ 交流群：**462720530**。讨论内容包括 PLC 协议适配、AI/MCP 集成、工程自动化及使用反馈。

## 1. 准备环境

- 推荐 Windows 10/11，安装 .NET SDK 10 和 .NET 8 Runtime。项目运行目标为 net8.0；SDK 10 用来读取仓库的 `.slnx` 解决方案。
- SDK 8 用户可直接构建 `src/PlcMcp.Server/PlcMcp.Server.csproj`、测试 `tests/PlcMcp.Tests/PlcMcp.Tests.csproj`。
- 首次 restore 需要访问 NuGet。运行时直接依赖 S7netplus 0.20.0。
- 模拟模式无需 PLC、厂商 IDE、管理员权限或任何现场网连接。

## 2. 获取、构建与测试

```cmd
git clone https://github.com/xzcnmb/PLC-AI-Assistant.git
cd PLC-AI-Assistant
dotnet restore PlcMcp.slnx
dotnet build PlcMcp.slnx -c Release --no-restore
dotnet test PlcMcp.slnx -c Release --no-build
```

产物位置：`src/PlcMcp.Server/bin/Release/net8.0/PlcMcp.Server.dll`。不要单独移动 DLL：它依赖同目录的其他程序集、deps.json 和 runtimeconfig.json。

## 3. 启动模拟模式

```cmd
dotnet src/PlcMcp.Server/bin/Release/net8.0/PlcMcp.Server.dll
```

程序等待 MCP stdin 消息属于正常行为，不会打开窗口或网页。启动说明写 stderr，stdout 只写 JSON-RPC。默认目标：sim-siemens、sim-omron、sim-mitsubishi、sim-inovance。

它们是内存标签模拟器，不是 IEC 61131-3 程序或 PLC 扫描周期模拟器。PressureActual 不会因修改 PressureSetpoint 自动模拟物理过程。

## 4. 接入 AI 客户端

在支持 stdio MCP 的客户端中新增服务，把路径换成你的绝对路径：

```json
{
  "mcpServers": {
    "plc-ai-assistant": {
      "command": "dotnet",
      "args": [
        "C:\\Projects\\PLC-AI-Assistant\\src\\PlcMcp.Server\\bin\\Release\\net8.0\\PlcMcp.Server.dll"
      ]
    }
  }
}
```

无需配置模型 API Key；MCP 服务本身不调用大模型。推理由客户端提供。不同客户端的配置文件位置不同，使用其添加 MCP 服务界面即可。

可对 AI 说：

- “列出 PLC 目标和各自支持的能力。”
- “读取 sim-siemens 的目标压力和实际压力，注明质量码。”
- “为 sim-siemens 的目标压力 4.5 bar 生成计划，再应用到模拟目标，最后回读。”
- “尝试超量程值 999999，确认服务拒绝。”

## 5. 配置物理只读目标

复制 `profiles/readonly.example.json` 为 `profiles/local.readonly.json`。样例使用文档保留地址；核对目标 IP、CPU 家族、rack/slot/unit、地址映射、单位和字节序后再使用。`profiles/local*.json` 已被 Git 忽略。

```cmd
dotnet src/PlcMcp.Server/bin/Release/net8.0/PlcMcp.Server.dll --config profiles/local.readonly.json
```

也可在 MCP 客户端 args 后追加 `--config` 和配置文件绝对路径。配置模式不会同时加载四个默认模拟目标。

加载配置和列出能力不会自动连接 PLC。调用 `plc_probe_target` 或 `plc_read_tags` 时才连接指定目标。当前没有扫描网段工具。

- 西门子：family 必须是支持的精确系列；S7-200 SMART 的 VW100/VD100 映射 DB1。S7comm-plus 和优化 DB 不支持。
- 欧姆龙：FINS/TCP，本地网络、DM/CIO/W/H/A；CIO0.00 为位地址。32/64 位需指定 byteOrder。
- 三菱：仅 SLMP binary 3E，端口按 GX 工程设置；X/Y/B/W 编号按十六进制，D/R/M 按十进制。
- 汇川/通用 Modbus：HR100 为零基保持寄存器 100；IR/C/DI 分别为输入寄存器/线圈/离散输入。厂商 D/MW 到 Modbus 的映射必须从目标工程确认。
- 所有物理标签 canWrite=false；配置模式不注册 plan/apply 写工具。

`experimental` 表示代码和 localhost 协议夹具通过，仍待型号/固件台架验证。不要把 TCP_CONNECTED 解读成 PLC RUN；不同协议的 probe 信息层级不同。

## 6. 返回与错误处理

正常结果见 `result.content[0].text` 的 JSON，工具错误见 `result.isError=true`。v0.1 还保留根级领域字段。值带 dataType、unit、quality、timestamp。多个标签逐次采样，不保证同一扫描周期。

常见情况：

| 现象 | 检查方式 |
|---|---|
| 启动后无窗口 | 正常，stdio 服务由 MCP 客户端驱动 |
| stdout 出现编译日志 | 先 build，再启动 DLL；不要让客户端用未编译的 dotnet run |
| 配置被拒绝 | 查看 stderr 中具体字段、未知属性、地址/类型、rack/slot/unit 和 canWrite |
| OPC UA/CIP 不支持 | 当前未安装这些后端，选择已实现协议；不能靠修改 capability 字段解锁 |
| 超时 | timeoutMs 是连接加整批读取总预算；减小标签批次或调整到 100～30000 ms |
| 中文别名重复 | 同一 changes 中规范名与别名指向同一标签会被拒绝 |
| 计划无法再次应用 | token 错误、过期、取消、状态变化或成功后的计划均不可复用，重新规划 |

## 7. 本机工程分析与 SMART 可选桥

无需厂商软件也可用 `plc_lint_program` 预检 ST/SCL 代码块、`plc_compare_projects` 比对 PLCopen XML、`plc_doctor` 查看本机软件检测。静态预检不等于编译；PLCopen XML 解析禁止 DTD/外部实体。

本机若已安装 STEP 7-MicroWIN SMART 和独立的 `smart200_mcp` Python 包，可在 MCP 客户端 args 中加 `--smart-project-root` 与一个**绝对目录**，只授权该目录中的 SMART 工程只读/离线分析：

```json
{
  "mcpServers": {
    "plc-ai-assistant-smart": {
      "command": "dotnet",
      "args": [
        "C:\\Projects\\PLC-AI-Assistant\\src\\PlcMcp.Server\\bin\\Release\\net8.0\\PlcMcp.Server.dll",
        "--smart-project-root",
        "D:\\PLCProjects\\Review"
      ]
    }
  }
}
```

启用后 `plc_smart_inspect` 读取 V2 项目概览，V3 加密段会报告离线无法解析；`plc_smart_validate` 对指定 POU 做网络有效性验证。它们在独立工作副本上运行，validate 会拉起本机 MicroWIN 独立实例，可能需要交互式桌面会话；不会下载到 PLC。未设置根目录或依赖不可用时工具不会注册。不要把客户生产工程复制到 Git 仓库，样例的 `--config` 只读 PLC 模式与 `--smart-project-root` 可同时使用。

持久治理目前支持 `plc_get_job` / `plc_get_audit` 查询；审计哈希链坏损时显式拒绝读/追加，残缺尾行只能手工显式修复。模拟写入仍使用内存审计；外部企业身份提供方、持久审批消费和物理执行路径没有接线。

## 8. 有限只读监控与离线 HMI 组态

调用 `plc_monitor_window` 并传入 `targetId`、`tags`，可选 `intervalMs` (50～60000，真实目标最少 200)、`durationSeconds` (当前 stdio 单次最多 15 秒)、`maxSamples` (最多 20)。长采样可能阻塞同一 stdio 服务的其他请求，因此该工具仅用于短窗口检查；运行时底层的 10 分钟会话 API 尚未作为 MCP 后台订阅开放。返回每次采样的质量码、时间、变化标记与摘要。服务绝不保证多个点位处于同一个 PLC 扫描周期，因此 `snapshotGuarantee` 始终为 `none`；这不是持续 SCADA 采集服务。

HMI 组态输入是厂商中立 JSON manifest，文件必须放在已配置的 `--smart-project-root` 工程目录里（该选项在此也作为离线工程根目录）。`plc_hmi_validate` 将它与 MCP 目标的标签表比对；`plc_hmi_generate` 校验通过后，向 Server 的 `data/workspaces/hmi/<id>` 新目录输出 JSON/CSV 及 SHA-256。目标厂商必须与 HMI manifest 一致，不能用空 PLC 标签集合跳过绑定检查。它不是 WinCC/GOT/NA 原生工程，也不会发布至触摸屏。

代码库中已有外部 JSON-RPC worker 的 handshake/doctor/submit/status/artifacts/cancel 进程协议和进程超时/输出限制，但**当前 Server 尚无外部 worker 配置入口，也未包含 TIA/CODESYS/GX/Sysmac/InoProShop 的真实工程 worker**。此代码仅供集成开发和 mock 测试，不能将 worker 自报完成视作厂商编译证据。更多实现边界见 [架构设计](research-and-architecture.md)。

## 9. 当前不能做的操作

跨品牌程序自动编译下载、厂商原生硬件/HMI 组态、在线编辑、真实写参数、CPU RUN/STOP、强制 IO、OPC UA 安全会话和持续订阅均未完成。四协议参数写帧只在 localhost 回环测试，`canWrite` 对物理目标仍须 false。SMART 工程离线分析不代表现场程序下载能力。

使用问题可在仓库提交 Issue，附软件版本、已脱敏配置、错误信息、复现步骤和期望行为。也欢迎加入 QQ 群 **462720530** 交流。

## 10. 汇川运行时（Inovance runtime）

`profiles/inovance.example.json` 是汇川 H5U/H3U 的只读示例（文档保留地址）。目标 `vendor` 为 `Inovance`、`transport` 为 `ModbusTcp`，`unit` 为 Modbus 从站地址（1～255）。未设置 `model` 时按 `family` 命中软元件映射表，当前表只收录 `H3U`/`H5U`；型号缺失时加载即报错并列出受支持型号，不会回退猜测。

软元件地址经**显式映射表**（代码 `InovanceSoftAddress`，型号 → Modbus 区域/基址）转换为 Modbus TCP，不做隐式换算：

| 软元件 | 种类 | Modbus 区域 | 基址 | 编号进位 | 编号上限 |
|---|---|---|---|---|---|
| X | 位 | 离散输入（FC2） | 0 | 八进制 | 1023 |
| Y | 位 | 线圈（FC1） | 0 | 八进制 | 1023 |
| M | 位 | 线圈（FC1） | 1024 | 十进制 | 8191 |
| S | 位 | 线圈（FC1） | 9216 | 十进制 | 4095 |
| T | 位 | 线圈（FC1） | 13312 | 十进制 | 1023 |
| C | 位 | 线圈（FC1） | 14336 | 十进制 | 1023 |
| D | 字 | 保持寄存器（FC3） | 0 | 十进制 | 32767 |
| R | 字 | 保持寄存器（FC3） | 32768 | 十进制 | 32767 |
| DD | 双字 | 保持寄存器（FC3，D 区） | 0 | 十进制 | 32767 |

- `HR/IR/DI` 是 Modbus 原生零基地址，直通不转换，也不查映射表。注意 `C` 前缀在汇川目标上按汇川计数器软元件解析（见上表），不是通用 Modbus 线圈简写；通用线圈写法请在 Generic 厂商目标上使用。
- 位软元件配 `Bool`，字/双字配数值类型；`DD` 只接受 32/64 位类型。类型不匹配、软元件编号越界、映射后的 Modbus 地址越界都会在连接前明确报错。
- 端序沿用四协议约定：16 位值无需指定；32/64 位值必须显式 `byteOrder`（`BigEndian`/ABCD、`LittleEndian`/DCBA、`WordSwap`/CDAB、`ByteSwap`/BADC），不按型号猜测。
- 上表是随代码发布的**未在真机验证的文档默认值**。上线前必须对照目标工程里的 Modbus 地址映射逐项核对；不一致时以工程为准，并同步修改代码中的表（`InovanceSoftAddress`）。因此该通道能力标注 `Experimental`：localhost 协议夹具通过，型号/固件台架待验。
- 与其它物理协议一致：所有标签 `canWrite=false`，配置模式不注册 plan/apply 写工具；`plc_probe_target` 的 `TCP_CONNECTED` 只表示 TCP 可达，不代表 CPU RUN。协议目录中对应条目为 `inovance-h-easy`。

## 11. 汇川 AutoShop 桥与本地模拟器（Inovance AutoShop bridge & simulator）

AutoShop（V4.12.0.2）集成所用接口为**逆向所得、无厂商背书、版本锁定 4.12.0.2**。工具默认全部不注册，须在 MCP 客户端 args 中显式加 `--autoshop-bridge` 才注册（可再加 `--autoshop-bridge-path` 指定 ITP 注册路径、`--autoshop-compile-rva <hex>` 覆盖注入桥的编译入口槽，默认为 F1 实证地址 0x15A2EC0、`--autoshop-allow-physical-write` 显式放开物理目标写——默认关，打开后真机写仍需运维审批、`--autoshop-allow-physical-download` 显式放开物理目标下载/RUN-STOP——默认关，打开后仍需运维审批，且本版 D1 协议客户端的**深层回环红线仍拒绝对非回环端点发包**，故该开关实际只改变治理层拒绝文案，真机下载路径依旧不可达、未验证）：

```json
{
  "mcpServers": {
    "plc-ai-assistant-autoshop": {
      "command": "dotnet",
      "args": [
        "C:\\Projects\\PLC-AI-Assistant\\src\\PlcMcp.Server\\bin\\Release\\net8.0\\PlcMcp.Server.dll",
        "--autoshop-bridge"
      ]
    }
  }
}
```

启用后注册十二个工具（前四个为环境/模拟器证据，后八个为阶段三注入桥工程/运行工具）：

- `plc_autoshop_inspect`：本机 AutoShop 安装与 InoVarSharedService 的只读环境证据（exe 路径/版本、服务进程、注册表端口、TCP 监听、ITP register 握手）。
- `plc_autoshop_read_shared_variables`：经 ITP 握手做受控只读观察，只统计推送报文数量；变量值解码为 `Unsupported`（二进制推送布局未验证，也不注册发布方），任何情况下不写、不强制、不下载、不编译、不控 RUN/STOP。
- `plc_autoshop_status`：聚合只读状态——环境检测 + ITP 握手 + 本机 H5U 软件模拟器发现（`h5u_daemon` 进程与回环 127.0.0.1:502（Modbus TCP）/127.0.0.1:12939（AutoShop 原生协议）探活）+ 能力矩阵（诚实标注 `Experimental`/`Unsupported` 与原因）。
- `plc_autoshop_simulator`：管理本机 H5U 软件模拟器进程（仅限本地 Windows 进程，不是 PLC 动作）：`{action:'status'}` 只读发现；`{action:'start'}` 在 daemon 未运行时以 Simulator 目录为工作目录启动 `h5u_daemon.exe`（已有实例运行时不再启动第二个，带重复启动防护与有界启动验证，日志落盘 Server 数据目录 `data/autoshop-simulator/simulator.log`）；`{action:'stop'}` 只能停止本服务自己启动并记录在案的 PID（PID 归属保护，先核对进程名与记录的 exe 路径，禁止裸 taskkill；外部启动的 daemon 一律拒绝）。本版 start/stop 使用最小进程管理实现（代码中留有指向 P2 `SimulatorLauncher` 接口的 TODO），stop 的 PID 归属保护在 `data/autoshop-simulator/ownership.json` 持久化。

阶段三注入桥工程工具（同样默认不注册，须先 `tools/AutoShopInjectBridge/build.cmd` 构建原生注入器/桥 DLL，产物在 `%LOCALAPPDATA%\AutoShopInjectBridge-run\`）：

- `plc_autoshop_generate_project`（`readOnlyHint=false`）：在指定本地目录离线构造"可打开工程"（E3 构造法）：按 Template A1 实测结构生成工程 XML（`Version=1`、`GCMModal`、默认声明 MAIN.LD/VarList.gdt/Config.sdt）→ UCodeCrypt 加密为 `<名>.hcp` → 声明文件写入同目录（磁盘内容=含 31 字节 AutoShop 头的完整字节）→ HcppWriter 打包 `.hcpp` 容器 → 离线往返校验（容器解析 + 解密 + XML 解析）。传 `sourceHcpp` 时按字节忠实解包真实工程包（复用其 Main blob 与文件内容，如厂商模板副本）。能力 `Experimental`：文件内容占位符能否被 AutoShop 接受未验证，打开验证由 open_project 负责。
- `plc_autoshop_open_project`（`readOnlyHint=false`）：打开本地工程目录（E3 方法）：目录含 `.hcpp` 时每次打开前重新解包**全新 `.hcp`**（AutoShop 打开时会原地解密消费 `.hcp`）→ 以 `AutoShop.exe <绝对路径>.hcp` 启动**自启、记账、握手核验**的实验实例（注入桥由启动器统一负责）→ 有界轮询主窗口标题/模态对话框。失败对话框按逆向错误串映射为六态（属性 7596 / 版本 7597 / 文件列表 7598 / 打开工程失败 62220 / 文件不存在 / 打开文件出错），失败即停止该实例；成功保留实例供 monitor/compile 使用。工程目录禁止位于 AutoShop 安装目录内（app 会在工程旁写 BackUP/Temp/config.ini）。
- `plc_autoshop_compile`（`readOnlyHint=false`，能力 `Experimental`）：对**当前已打开工程**（open_project 成功后的会话门）经注入桥在 AutoShop 进程内执行 **F1 实证的编译链**：工程门 0x161B620（this=*(root+0x24)，经访问器 0x1581C20；调用形态由厂商调用点反汇编实证）先行检查——无工程时返回明确错误（rc=-32002），避免厂商的 62220 模态弹窗路径——随后 root 0x1581A70 → cmpMgr 0x1581CA0 → 编译入口 0x15A2EC0（无参 `__thiscall` 异步提交）。地址可用 `--autoshop-compile-rva <hex>` 覆盖（注入器另有 `--compile-rva none` 关闭编译面）。桥先做 exe 基址（0x400000，RELOCS_STRIPPED 无 ASLR，升级即失效）与 PE 边界校验，**每个厂商调用独立 SEH 隔离**并把步进标记/异常码写进错误返回（F3 冒烟曾用此定位并修正一处地址算术 bug：配置值是 VA，须按 `base+(VA-0x400000)` 解析）。→ 轮询 `<工程目录>\Compile\*.compile` 与 `<名>.foid` → 用 UCodeCrypt 解密每个 `.compile` 给出解密摘要。**编译是离线 IDE 行为，零 PLC 动作**；旧版桥 DLL/显式关闭/工程门拒绝时工具如实转述逐字原因、不伪装成功。
- `plc_autoshop_monitor_read`（`readOnlyHint=true`，只读）：经注入桥 set_target 到回环 127.0.0.1（固定端口，工具无目标参数）→ 转述 `read_status` 调用回执 + **进程内元件读取**：桥用非桩原语（tagSoftAddr 5 参构造 + `CMap::SetAt`，AutoShop 自用的填表函数）在 AutoShop 进程内真实填元件表（M/X/Y/S=位 type 1、D/R/T/C=字 type 4）→ `ReadElemValue(mode=0,level=0)` 受理（rc=0）→ settle 窗口内**双槽轮询**：厂商应答实际写入的 **tag+0x0C CString 交付槽** 与 +0x20 double 镜像，并以 **delivered=N** 判别（任一交付槽变化 = 应答读闭合）。**如实裁决（G1，docs/inovance-autoshop-monitor-values.md）**：值回传路径已完全定位（结构→字段→回调全链路）；此前"裸 HWND 投递实验崩溃"的根因已解释——是把 HWND 句柄值误当 `CWnd*` 解引用（+0x20），shim 形态无故障但同样无回传；**当前卡点收敛为：进程内会话建立未逆向**（注入的 AutoShop 进程内以太网 worker 一根字节都不上网，12940 回环代理零连接记录）。因此当前后端矩阵下 delivered 恒 0，返回的 values 只是**调用级证据**（map 填表 + 受理回执），**不是 PLC 数据**，0 值既可能是"寄存器为 0"也可能是"无应答"；若未来任何回环后端 delivered>0，同一调用面自动给出厂商栈读到的真值（仍 Experimental，非真机）。
- `plc_autoshop_download`（`readOnlyHint=false`、`destructiveHint=true`，G2/G3）：把**重打包的编译产物 `.hcpp`（PPCH 容器）**经 D1 12939 原生协议（登录→type2 会话→type5 分块 ACK→0x0F 完成）下载进**回环 H5U 软件模拟器工作区**，随后触发装载（0xFF000010←1）并发 RUN 控制字（0xFF000020←1），即 `AutoShopDownloadClosedLoop.RunAgainstDaemonAsync` 编排的最小闭环；入参 `projectDir`（绝对路径、不得在安装目录内）——含编译产物时取**最新 `.hcpp`**（重打包下载件），无编译产物时如实注明"可能是未编译源容器"；非 PPCH magic 的裸件被拒绝。返回**逐步骤证据**：payload 路径/长度/md5、登录与传输回执、装载步的诚实裁决（模拟器 `load_down_file_start` 为桩，应答 status **0x82**——下载件**不会被激活为运行程序**，工具绝不宣称装载成功）、RUN/STOP 应答（RUN 实证让寄存器变活：G2 记录 Modbus D100 0→4660，那是出厂默认 app 在跑）。`includeStop` 可选（默认 false：保持运行以便监视）。**治理**：下载是 PLC 动作——目标**硬限 127.0.0.1**（两个协议客户端构造即拒非回环，红线不随开关解除）；真机目标需 `--autoshop-allow-physical-download`（默认关）+ 运维审批，且真机装载链（非桩 + 完整下载状态机）未验证，如实标 **Experimental(真机待验)**；传输失败绝不发 RUN；厂商进程内 `CCommunicateEth::Download` 导出已被 G2 实证为死通道（受理但零帧），桥门默认关、导出不实调。
- `plc_autoshop_run_stop`（`readOnlyHint=false`、`destructiveHint=true`，G2/G3）：`{action:'run'|'stop'}` 向回环模拟器寄存器链写 **0xFF000020←1/0** 控制字，返回**精确线帧 hex**、daemon 应答裁决（[06] ACK）与 **Modbus D100 前后值**（127.0.0.1:502 读回，`readRegisterEvidence` 默认开）作为"寄存器变活"证据——G2 实证 RUN 后 D100 由 0→4660；STOP 同样 ACK 且模拟器**保持末值**（不是清零，如实记录）。证据读失败只降级为注记（裁决以原生协议 ACK 为准），不伪装也不撤销。**仅本地模拟器演示**：硬限回环，物理 CPU RUN/STOP 未被逆向、维持 Unsupported；非法 action 直接拒绝并零发包。与 download 同受 `--autoshop-allow-physical-download`（默认关）+ 审批治理。
- `plc_autoshop_axis_config`（`readOnlyHint=false`）：读写本地 `.hcpp` 工程包内的轴组态（`xml\axis.xml` 成员，F2 离线管线 `AxisPackageEditor`）：不带 `edits` 时返回轴列表（结构级视图，RE 推断 schema）；带 `edits`（键格式 `"<轴名>.<字段>"`，如 `X1.ForwardLimitValue`；支持字段：Name/AxisType/OutputDev/InputDev/SlaveName=字符串、Mode/Row=int、EnableLimitSet/IsVirtual/IsContour=bool、ForwardLimitValue/NegativeLimitValue/RotationPeriod=double）时执行 读→改→重打包→读回校验，可选 `open=true` 在自启实例上重开验证。**只写工程文件，零 PLC 动作**。容器无 `xml\axis.xml` 成员时如实报 "无轴工程"（H3U 类工程常态），带 edits 拒绝执行。能力 `Experimental`：结构级——轴 schema 为逆向所得（无真实 H5U 轴样本），厂商对编辑值的消费未验证。
- `plc_autoshop_write_register`（`readOnlyHint=false`、`destructiveHint=true`）：对**已配置的运行时目标**（`targetId`，须为 Inovance + ModbusTcp）经 Modbus TCP 写单个软元件并回读闭环证据：FC5（单线圈：M/X/Y/S/T/C 位元件）或 FC6（单寄存器：D/R 字元件，原始值 0..65535），地址经**显式汇川软元件映射表**转换（不猜测；DD 双字需 FC16，本工具不支持）。**物理写默认禁止**：默认仅允许 `IsSimulation` 且 host 在 127.0.0.0/8 的目标（本机 H5U 软件模拟器）；真机/非回环目标需要显式启动开关 `--autoshop-allow-physical-write`（默认关）+ 运维审批（对齐 contracts.md 物理写政策）。映射表是**未经真机验证的文档默认值**——物理使用前必须对照目标工程的 Modbus 地址映射逐项核对；模拟器结果不证明真机行为。

工程闭环流程示例（六步闭环：生成 → 打开 → 编译 → 下载 → 运行 → 监视，全程仅本机 IDE 实例与 127.0.0.1 模拟器）：

```
0. 前置：{tool:"plc_autoshop_simulator", action:"start"}
   → 启动本机 h5u_daemon（127.0.0.1:502 Modbus + 127.0.0.1:12939 原生协议），下载/运行/监视的共同后端
1. {tool:"plc_autoshop_generate_project", projectDir:"D:\\PLCProjects\\e4demo", model:"H3U"}
   → 返回 .hcp/.hcpp 路径、文件清单与离线往返校验摘要
   （如需已知可打开的内容：projectDir 指向含厂商模板 .hcpp 副本的目录，或传 sourceHcpp 指向模板副本）
2. {tool:"plc_autoshop_open_project", projectDir:"D:\\PLCProjects\\e4demo"}
   → 自启实验实例 + 注入桥 + 轮询窗口标题；成功返回 pid 与主窗口标题，失败返回映射后的错误态与对话框证据
3. {tool:"plc_autoshop_compile"}
   → 进程内编译链（异步提交）+ Compile 目录产物轮询/解密摘要（实验实例 pid 需保持，产物 25–40 s 内收取；
     编译同时重打包出可下载的 project.hcpp——下一步的载荷）
4. {tool:"plc_autoshop_download", projectDir:"D:\\PLCProjects\\e4demo"}
   → D1 协议把重打包 .hcpp 下载进回环模拟器工作区（G2 实证字节一致落盘）+ 装载触发（如实回 0x82 桩态：
     模拟器不激活下载件）+ RUN（寄存器变活实证）；逐步骤证据原样返回；真机需 --autoshop-allow-physical-download + 审批（本版深层回环红线仍拒绝非回环发包）
5. {tool:"plc_autoshop_run_stop", action:"run"}（下载步已含 RUN；此处可独立 run/stop）
   → 0xFF000020 控制字 + 线帧 hex + [06] ACK + Modbus D100 前后值证据（G2：RUN 后 D100 0→4660；STOP 保持末值不清零）
6. {tool:"plc_autoshop_monitor_read", addresses:"D100,M0"}
   → 回环 set_target + read_status 回执 + 进程内元件读取（双槽轮询 + delivered 判别；当前后端 delivered=0，
     值为调用级证据，见上文 G1 如实裁决）
```

辅助工具（不在六步闭环内）：

```
- {tool:"plc_autoshop_axis_config", projectDir:"D:\\PLCProjects\\e4demo",
    edits:{"X1.ForwardLimitValue":4321.5}, open:true}
  → 轴组态读→改→重打包→读回 + （可选）自启实例重开验证；无轴工程时如实报"无轴工程"
- {tool:"plc_autoshop_write_register", targetId:"sim-inovance", address:"D100", value:1234}
  → Modbus FC6 写 + FC3 回读（默认仅回环模拟器目标；真机需 --autoshop-allow-physical-write + 审批）
```

产物位置：`.hcp`/`.hcpp`/声明文件在 `projectDir`；编译产物在 `<工程目录>\Compile\<名>.compile`（UCode 加密，解密摘要见返回值）与 `<工程目录>\<名>.foid`；实验实例记账在 `%TEMP%\plcmcp-handoff\inject-bridge\owned-instances.jsonl`；工具日志落盘 Server 数据目录 `data/autoshop-inject/autoshop-inject.log`（JSONL，含动作/成败/pid/工程目录/消息）。

注入桥工程工具的限制：只注入**本工具自启**的实验实例（注入器无"注入已有 PID"动词；PID 归属核验 + 整树终止 + 日志落盘，服务关闭时清理）；工程路径与 `sourceHcpp` 须为绝对路径、不得位于安装目录内（打开工程允许读取安装目录内的厂商模板副本作 `sourceHcpp`，生成物只写工程目录；容器成员允许 `xml\axis.xml` 这类**工程目录内**的相对子目录名，拒绝根路径/`..`/盘符逃逸）；注入器把 AutoShop 命令行经 ANSI 转换，**非 ASCII 路径会被改写**——非 ASCII 工程名时工具自动改用 ASCII 安全的 `.hcp` 文件名（project.hcp），工程目录本身须 ASCII；`.hcp` 每次打开被原地解密消费，须 `.hcpp` 源或重新生成；打开会把工程路径追加到 `HKCU\...\Recent File List`（良性副作用，本工具不做注册表回滚）；**经 AutoShop 进程内的元件写/强制本版不注册工具**（厂商以太网写导出为桩 0xF390、Force 实弹崩宿主——F1 实证；桥写/强制/下载三层门默认关、厂商导出永不实调——G2 实证进程内 Download 为死通道）；**程序下载与模拟器 RUN/STOP 走 D1 12939 协议层**（`plc_autoshop_download` / `plc_autoshop_run_stop`，注册即硬限 127.0.0.1，与注入桥写面无关）；模拟器元件写走 Modbus TCP 502（`plc_autoshop_write_register` / `plc_plan_write`+`plc_apply_write`）。

本机冒烟实证（F3，2026-10-28，AutoShop V4.12.0.2 x86 + 重建后的注入器/桥 DLL 1.1.0，全程零物理 PLC 动作、实例自启自清）：generate（Template A1 副本为 sourceHcpp，字节忠实 11 文件，往返校验通过）→ open（11 s 窗口取证 `AutoShop V4.12.0.2 <工程目录> - [MAIN]`，注入桥握手核验通过）→ **compile 成功**（桥受理编译链提交 `submitted`，随即 `Compile\SBR_001.compile`（480 B）+ `ecttrans.foid`（9 B）+ 重打包 `project.hcpp`（15 711 B）落盘，`.compile` 用 UCodeCrypt 解密摘要 `53410300020001500001500F27000007`（与 E3 ×3 的产物形态一致））→ monitor_read（set_target 127.0.0.1:12939 stored；read_status execMode=0/gcmState=0 rc=0；read_elements count=2 **受理但值回填未闭环**——Modbus 502 基线 FC6 写 D100=0x1234 回读一致后，进程内读值仍为 0；frame-HWND 投递实验触发结构化异常，桥 SEH 兜住宿主存活）→ stop（归属核验 + 整树终止成功，无残留进程；实验实例/daemon 全部清理）。`plc_autoshop_write_register` 本体在离线测试中用注入的假 Modbus 服务端验证（回环模拟器目标 FC5/FC6 写+回读一致、物理目标默认拒绝、`--autoshop-allow-physical-write` 打开后放行）；本冒烟的 Modbus 基线由等价的裸 FC6/FC3 回环收发完成（FC6 写 D100=4660 回读一致）。证据文件：`%TEMP%\plcmcp-handoff\f3-smoke\run-final5.txt`（同链 xunit 版 `RealChain_GenerateOpenCompileMonitor_OnLocalAutoShop`，`PLCMCP_LOCAL_SIMULATOR=1` 下通过）。

闭环下载/运行冒烟实证（G2，2026-10-29，全程仅 127.0.0.1，实验 daemon 用 Simulator 目录的**工作区副本**、安装目录零写入并有 md5+mtime 复核，daemon/抓包代理干净退出；G3 集成不再重复实跑，直接引用该证据，详见 docs/inovance-autoshop-download-closedloop.md §三）：D1 协议把编译产物重打包 `project.hcpp`（15,745 B）下载进工作区——**daemon 侧 md5 与源字节完全一致**（f463cbaa…），旧件自动改名 `project_backup.hcpp`（备份舞蹈），daemon 日志记录 `Download project\project.hcpp start./finished.`；装载触发 `0xFF000010←1` 应答 **status 0x82（桩，如实：下载件不激活）**；`RUN 0xFF000020←1` 应答 **[06] ACK 且 Modbus D100 由 0→4660、R100 0→4660——寄存器变活首次实证**（出厂默认 app 执行）；`STOP←0` 同样 ACK、末值保持不清零。G3 将这两段接入 MCP：`plc_autoshop_download` 编排同一序列并逐步骤原样回执（0x82 桩态不会被说成装载成功），`plc_autoshop_run_stop` 附带 D100 前后值证据；离线测试以注入假通道重放上述实测帧/应答（帧字节锚定 + 回环硬拒 + 物理开关默认关）。

模拟器端到端示例：`plc_autoshop_simulator {action:'start'}`（或自行手动启动 daemon）确认 127.0.0.1:502 监听后，加载 `profiles/inovance.simulator.example.json`（指向 127.0.0.1:502 的本机模拟器演示配置，含协调人实证过的 D100/M0/X0/R100 只读点位），再对 `plc_probe_target` / `plc_read_tags` / `plc_monitor_window` 使用该目标。**该配置仅用于本机模拟器演示**：连接实体 PLC 前必须把 host 换成真机地址并逐项核对软元件→Modbus 映射；模拟器上验证通过不代表真机已验证。

能力矩阵与限制（诚实声明，均可在 `plc_autoshop_status` 中实时查看）：

| 能力 | 状态 | 原因 |
|---|---|---|
| 环境检测 / 模拟器发现 | `Experimental` | 仅本机文件系统/注册表/TCP/进程证据；无厂商背书 |
| ITP register 握手 | `Experimental`（应答时）/ `Unsupported` | 端点不可达或未应答时无验证证据 |
| Modbus TCP 运行时读取 | `Experimental` | 模拟器 502 监听时经现有 `plc_read_tags` 可用；仅本机模拟器实证，真机待验 |
| 有限只读监控 | `Experimental` | 依赖已配置且可达的目标；非原子扫描快照 |
| 注入桥工程打开（open_project） | `Experimental` | E3 构造法在本机对模板类工程实证打开；自启/记账/清理实例，非 PLC 动作 |
| 注入桥工程生成（generate_project） | `Experimental` | 离线构造 + 离线往返校验通过；生成内容能否被 AutoShop 接受未验证 |
| 注入桥只读监视（monitor_read） | `Experimental` | 进程内读已实证受理（map 经非桩原语真实填表、ReadElemValue rc=0）+ G1 双槽轮询与 delivered 判别；但**值回填未闭环**——回传路径已定位（tag+0x0C 交付槽），裸 HWND 误用已解释（CWnd* vs HWND），**卡点=进程内会话建立未逆向**（注入进程零线流量实证）——当前 delivered 恒 0，返回值是调用级证据，不是 PLC 数据 |
| 模拟器下载传输（download，D1 协议） | `Experimental`（仅回环模拟器）/ 真机 `Unsupported` | G2 实证 project.hcpp 字节级落盘（md5 一致）+ 逐步骤回执；目标硬限 127.0.0.1（协议客户端构造即拒非回环）；真机需 `--autoshop-allow-physical-download`（默认关）+ 审批，且真机下载状态机未验证——本版开关打开也仍被深层红线拒绝 |
| 模拟器 RUN/STOP（run_stop） | `Experimental`（仅回环模拟器）/ 真机 `Unsupported` | G2 实证控制字 0xFF000020←1 ACK [06] 且 **Modbus D100 0→4660 寄存器变活**（出厂默认 app）；STOP ACK 且保持末值；硬件 CPU RUN/STOP 路径未逆向 |
| 程序装载（program load） | `Unsupported` | 模拟器 `load_down_file_start` 为桩：装载触发应答 status **0x82**，下载件不激活为运行程序（G2+D1 实证）；真机装载链未验证；工具绝不宣称装载成功 |
| 经厂商导出的进程内下载 | `Unsupported` | G2 独立 harness 实证 `CCommunicateEth::Download` 受理但零传输帧（哨兵不变），DownloadPlcSoft 独立实调段错误危及宿主；桥 download 门默认关、导出不实调，闭环只走 D1 协议层 |
| 注入桥编译（compile） | `Experimental` | F1 实证编译链进程内放行（本机冒烟：提交受理 + SBR_001.compile/ecttrans.foid/重打包 hcpp 落盘 + 解密摘要）；离线 IDE 行为零 PLC 动作；版本锁定 4.12.0.2 |
| 注入桥轴组态（axis_config） | `Experimental` | 结构级读写（F2 管线离线往返 + 打开级验证）；轴 schema 为逆向推断，厂商对编辑值的消费未验证（无真实 H5U 轴样本）；无轴工程如实上报 |
| 模拟器写寄存器（write_register） | `Experimental`（仅模拟器）/ 真机 `Unsupported` | Modbus FC5/FC6 写+回读闭环；默认仅回环模拟器目标，真机需 `--autoshop-allow-physical-write`（默认关）+ 审批；映射表为未验证文档默认值 |
| 共享变量值读取 | `Unsupported` | ITP 二进制推送布局未验证，未实现值解码 |
| 经 AutoShop 的元件写 / 强制 | `Unsupported` | 厂商以太网写导出为桩（0xF390，F1 实证）、Force 实弹崩宿主（不可捕获）；本版不注册经 AutoShop 的写/强制工具，桥三层门默认关；模拟器元件写走 Modbus TCP 502（见 write_register 行），程序下载/运行走 D1 12939 协议层（见 download/run_stop 行） |
| 真机 PLC 下载 / 真机 CPU RUN-STOP / 真机元件写 | `Unsupported` | 一切实验仅 127.0.0.1：D1 协议客户端与 Modbus 写服务均硬拒非回环目标（物理开关默认关且打开后深层红线仍拒）；真机下载状态机、装载链、CPU RUN/STOP 通道与映射表均未验证 |
| 插件框架 / 原生通信栈直调 | `Unsupported` | 仅归档情报/证据；布局与版本强绑定，本版无 MCP 工具 |

治理边界：模拟器 start/stop 属本地软件进程操作（非 PLC 动作），无需审批 token，但有重复启动防护、日志落盘、有界超时，失败不会伪装成功；所有探活仅限 127.0.0.1，任何非回环连接都被拒绝。`plc_autoshop_download`/`plc_autoshop_run_stop` 属 PLC 动作（destructive）：**默认仅回环模拟器目标可达**，真机目标须 `--autoshop-allow-physical-download`（默认关）+ 运维审批，且本版协议客户端的深层回环红线对非回环端点一律拒发——真机下载/RUN-STOP 在本 Server 实际不可达、维持未验证声明；经 AutoShop 的元件写/强制仍无任何执行入口。注入实例与实验 daemon 一律自启自清；下载载荷只落模拟器工作区副本，安装目录零写入（G2 复核）。
