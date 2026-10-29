# 汇川（AutoShop / H-Easy）集成任务书

本文件是汇川支持的边界任务书：先固化反编译证据，再划分各实现代理的目录所有权与验收标准。
生成日期：2026-10-23。反编译对象：本机 AutoShop V4.12.0.2（`D:\Inovance Control\AutoShop`，进程运行中）。

## 一、反编译结论：AutoShop 暴露的可复用接口

| # | 接口 | 证据 | MCP 可用性判断 |
|---|---|---|---|
| 1 | **插件框架** `PluginInterface`（Plugin.dll，x86） | 导出符号：`PluginManager::LoadLibiaryFromFolder/RegisterInterface/GetPluginMgr/GetPluginsByName`、`EventCenter::ListenEvent/PublishEvent/RarseEvent`；插件 DLL 契约 = 7 个导出：`GetPlugInName/GetPlugInType/GetPlugInVersion/GetInterface/QueryInterface/GetAuthor/GetCopyRight`（实例：ACmd.dll、APipe.dll）；同目录 Plugin*.pdb 含完整符号（源路径 `E:\ReleaseCode\autoshop_main\...`） | 官方扩展点，但 `PluginInfo`/`PluginEnum`/`CPluginBase` 布局需从 PDB 重建，且与版本强绑定；**本版只归档情报，不实现插件** |
| 2 | **PLC 通信栈** `CCommunicateEth`（CommunicateEthernet.dll，x86） | 静态导出：`ReadElemValue`、`ReadSeriesWordElem`、`ReadWatchElemValue`、`WriteElemValue`、`Force/Unforce`、`GetExecMode`、`GetPlcLable`、`GetGCMState/Info/Clock`、`SetGCMClock`、`ReadSysCfg/GetConfigInfo`、`UploadScopMonData/StopUploadScopMonData`、`UploadHistoryFaultData/UploadSysRunLog/UploadModuleFaultInfo`、`Download/EnableUpload/UploadModbus/UploadModbusTcp/UploadModuleConfig`、`ProgramValidate/CompleteDownloadCmd`、`PrepareFPGA/SwitchUpgradeMode`、`SettingPassword/CheckPassword`、`SimpleCommand/SingleCommand/SimpleCommonCmd/SingleSimpleCommand` 等 50+；签名依赖 MFC（`CStringT<wchar>`、`CSoftAddrMap`、`CWnd*`）与枚举 `EnumExecuteMode/EnumCommandLevel/EnumCommunicaterCmd` | 功能完整但无头文件、需自建 x86 C++ 垫片且强绑版本；**本版只归档签名，不直接调用** |
| 3 | **共享变量服务** InoVarSharedService（Windows 服务，x86，运行中） | TCP `127.0.0.1:13939` LISTENING；IOCP 服务器（`CCommunicateBase`）；JSON 报文（jsoncpp），`CPacketHandler::HandlerRegister/HandlerRegAnswer/DelRegisterInfo/IsValidCompletePacket`、`CVarDataMgr`；官方测试客户端 TestITPClient.exe（字段 `varModify`/`VarSize`/`defaultValue`）；端口经注册表 `GetPort→GetAvaliablePort→SaveServicePort` 持久化；日志 `AutoVarShared\SharedVaribleLog\*.log` | **本版集成路径**：ITP 为本地回环 JSON 协议，先逆向握手与读变量报文，只读接入工程桥 |
| 4 | 内置 AI 助手（ACmd/AICom/APipe + InterfaceMgr::`CAIComMgr` + UDP 65241 + AIAssistantELinkCmd.xml） | 指令索引 XML、聊天皮肤、插件导出（见 #1） | 疑似对接云端大模型、随版本漂移；**不作为 MCP 集成路径** |
| 5 | 开放运行协议 | H5U/H3U 等支持 Modbus TCP（工程内配置映射）；仓库已有 ModbusTcpAdapter | **本版集成路径**：汇川软元件→Modbus 映射层，纯协议、不依赖厂商 DLL |

## 二、范围与原则（对齐 docs/research-and-architecture.md）

- Inovance H/Easy 线：**先做导出物/变量映射与只读运行时，不承诺自动编译下载**（research 文档第 36 行结论不变）。
- 物理写入口仍禁用；适配器最多提供写帧（协议层/localhost 测试），与现有四协议写帧政策一致。
- 能力声明诚实：真机未验的一律 `Experimental`，未实现的一律 `Unsupported`。
- 测试不得依赖本机 AutoShop 安装；一切 IDE/服务路径、端口可注入。

## 三、目录所有权（本任务书派发的三个代理互不重叠）

| 代理 | 负责内容 | 允许编辑 | 禁止编辑 |
|---|---|---|---|
| A：运行时适配器 | 汇川软元件→Modbus TCP 映射与适配器、示例只读配置、目录条目、测试 | `src/PlcMcp.Adapters/**`、`src/PlcMcp.Core/Catalog/VendorCatalog.cs`、`tests/PlcMcp.Tests/**`、`profiles/inovance.example.json`、`docs/usage.md`（只加“汇川运行时”小节） | `src/PlcMcp.Engineering/**`、`src/PlcMcp.Server/**`、README.md |
| B：AutoShop 工程桥 | ITP 协议逆向与文档、外部 Worker、本机 doctor 检测、MCP 工具、测试 | `src/PlcMcp.Engineering/Workers/AutoShop/**`、`src/PlcMcp.Engineering/Doctor/VendorDoctor.cs`、`src/PlcMcp.Server/**`、`tests/PlcMcp.Engineering.Tests/**`、`docs/inovance-itp-protocol.md`（新建） | `src/PlcMcp.Adapters/**`、`src/PlcMcp.Core/**`、README.md |
| C：反编译情报归档 | 插件框架/通信栈符号与签名的证据文档 | `docs/inovance-autoshop-re-notes.md`（新建） | 一切源码/测试/其他文档 |

通用规则（继承 docs/contracts.md）：

1. 先读 `docs/contracts.md` 与本任务书，再动手。
2. 需要修改 `src/PlcMcp.Contracts/**` 或非本代理目录时，**停止并在最终报告里说明接口需求**，由集成负责人协调；不自行越界。
3. 不修改 README.md（验收阶段统一更新能力表）。
4. 每个公开能力必须对应真实后端代码路径与测试；禁止“品牌名推断能力”。
5. 提交代码前自跑 `dotnet build D:\PLCmcp\PlcMcp.slnx -c Release` 与相关测试工程，构建失败视为未完成。

## 四、各代理任务书

### 代理 A：汇川运行时适配器（Inovance runtime adapter）

目标：让 `sim-inovance` 之外出现真实、可配置、只读的汇川运行时通道。

1. 实现 `InovanceSoftAddress` 解析与映射器（纯函数，可单测）：
   - 软元件：位 `X/Y/M/S/T/C`、字 `D/R`、双字 `DD` 等（按 H5U/H3U 手册语义），显式映射表（型号→Modbus 区域/基址/端序），**不做隐式换算**，映射表缺失即报错。
   - 字节序沿用四协议约定：BigEndian/ABCD、LittleEndian/DCBA、WordSwap/CDAB、ByteSwap/BADC。
2. 实现 `InovanceModbusTcpAdapter`：复用/委托现有 ModbusTcpAdapter 的编解码与连接语义（批次预算、超时关闭、质量码、逐标签、错误校验），符合 `IPlcProtocolAdapter` 契约；写路径仅帧生成（localhost 测试用），物理写入口不暴露。
3. `VendorCatalog.cs` 增加 Inovance H/Easy 协议目录条目，能力如实标注。
4. `profiles/inovance.example.json`：文档保留地址示例，注释清楚映射表与端序如何指定；物理目标写工具不注册。
5. 测试：映射器单元测试（边界、端序、位/字/双字、非法软元件）、localhost 模拟服务器集成测试（参照 ModbusTcp 既有测试模式）、错误路径（映射缺失、地址越界、超时）。
6. `docs/usage.md` 加“汇川运行时（Inovance runtime）”小节：配置字段、映射表、限制。

### 代理 B：AutoShop 工程桥（AutoShop engineering bridge）

目标：按 Siemens SmartBridge 同等安全姿态，接入本机 AutoShop 的共享变量服务（只读），并让 doctor 能给出有证据的汇川环境报告。

1. ITP 协议逆向（实证）：
   - 依据本任务书第一节第 3 条与现有符号/字符串情报，对本机 `127.0.0.1:13939` 做**只读探测**（连接、发 register 类 JSON、观察应答；禁止影响 AutoShop UI、工程文件或 PLC）。
   - 逆向结果写 `docs/inovance-itp-protocol.md`：报文 JSON 结构、字段语义、时序、错误码、观察证据（日志/抓包片段），并明确“未验证项”。
2. 实现 ITP 客户端与外部 Worker（放在 `src/PlcMcp.Engineering/Workers/AutoShop/`），复用现有 `ExternalWorkerProtocol`（白名单进程、JSON-RPC 握手、任务/超时/工件）；默认禁用，仅显式 `--autoshop-bridge` 类开关启用；**只读**：读共享变量/监控值，不提供写、Force、下载、编译入口。
3. `VendorDoctor.cs` 增加 AutoShop 检测：exe 版本、安装路径、InoVarSharedService 服务状态与监听端口（注册表/TCP 证据），可注入路径便于测试；检测结果不含“已支持编译下载”之类推断。
4. MCP 工具：`plc_autoshop_inspect`（环境/服务证据）与受控的变量读取工具，遵循现有工具 schema 与 annotations 风格；未启用桥时不注册。
5. 测试：ITP 客户端对 mock TCP 服务器（JSON 报文回放）、doctor 检测逻辑（注入假路径/假注册表）、worker 握手与超时；不依赖本机 AutoShop。
6. 明确在代码与文档中保留“AutoShop API 为逆向所得、无厂商背书、版本锁定”声明。

### 代理 C：反编译情报归档

目标：把反编译证据固化成仓库文档，供后续插件式集成参考。

写 `docs/inovance-autoshop-re-notes.md`，内容必须包含：

1. 环境：AutoShop 4.12.0.2 安装路径、位数（x86）、进程/服务、端口证据。
2. 插件框架：契约导出函数、`PluginInterface` 命名空间符号清单（含 mangled name）、`PluginInfo` 构造签名、PDB 位置与源路径线索、已知插件实例（ACmd/APipe）。
3. 通信栈：`CCommunicateEth` 完整导出清单（含装饰名与参数线索）、依赖类型（MFC/枚举）、InterfaceMgr 相关类（`CAIComMgr`、`CH530CompileFactory` 等）。
4. 共享变量服务：IOCP 结构、注册表端口机制、日志格式样例。
5. 风险与限制：无头文件、版本强绑定、x86 进程内插件、无厂商背书；建议的后续验证路线（PDB 类型重建、垫片 DLL、UIA 备选）。
6. 来源：dumpbin 输出摘要、文件时间戳；注明哪些是符号事实、哪些是推断。

## 五、验收标准（由集成负责人执行）

1. `dotnet build D:\PLCmcp\PlcMcp.slnx -c Release` 通过。
2. 全部测试工程 `dotnet test` 通过（含新增）。
3. 目录所有权无越界；README 未动；docs 更新与实现一致。
4. 能力声明诚实：无“真机已验”“支持下载”类越界表述；物理写入口仍不可达。
5. 反编译情报文档与任务书第一节证据一致，且标明事实/推断。

---

# 阶段二：工程闭环（编译 / 监视 / 程序生成 / 上传下载 / 轴组态）

目标：把 AutoShop 桥从“环境检测 + 握手”推进到常用工程功能。三条并行路线，最终由集成代理收敛为受治理的 MCP 工具。

现场事实（2026-10-24 摸底）：
- AutoShop V4.12.0.2 运行中（PID 24492，主窗口标题无工程名）；本机为全新安装，`HKCU\Software\Inovance Control\AutoShop\Recent File List` 全空，磁盘未见 *.hcp 工程文件。
- InoVarSharedService 运行中（TCP 127.0.0.1:13939）；插件 SDK 位于 `D:\Inovance Control\AutoShop\AutoVarShared\`（Plugin.dll + PDB + PluginCommunicate.dll）；插件加载目录与宿主尚未实证（S2 已实证：本机无第三方插件宿主，插件框架仅 InoVarSharedService 内部使用）。
- 通信栈 `CCommunicateEth` 80+ 静态导出（读/写/Force/状态/监控流/下载/上传，见第一节）。
- 本机未见实体 PLC 连接证据；是否可用 AutoShop 在线模拟器待实证。

**2026-10-24 下午更新（协调人实证，结论改变了上面的“模拟器待实证”）**：

1. **UI 自动化通道全被封死**：SendInput 鼠标/键盘、WM_COMMAND 直投、菜单消息对 AutoShop 全部无效（像素差分 0%）；仅能从资源段提取加速键表与菜单命令 ID 映射（新建工程=0x805A、全部编译=0x80E1、监控=0x807D、下载=0x8072、F5 运行 PLC=0x81F4、F6 停止 PLC=0x81F5、添加轴=0x835D 等，完整映射在 docs/inovance-autoshop-uia-map.md 与 handoff）。向日葵远程控制运行中，疑为输入过滤来源；不得擅自结束它（用户可能远程在用）。
2. **工程格式种子已到手（无需 UI）**：工程源文件扩展名 **.hcp**（shell 注册 AutoShop.Document）；打包/下载工程 **.hcpp**（magic "PPCH"）；官方模板库 `D:\Inovance Control\AutoShop\Template_Project\...\Template A1..E\*.hcpp` 与中文模板（含"连续式立包机"）；工程内含 `xml\axis.xml`（轴组态 XML）、FileList.txt、NetConfig.cfg 等；AutoShop.exe 存在命令行开关 `-open/-downfile/-offlinedebug/-downprj/-Debug`（语义待逆向）。
3. **H5U 软件模拟器已实证可用（重大）**：`D:\Inovance Control\AutoShop\Simulator\h5u_daemon.exe` 以 Simulator 目录为工作目录启动即运行（无需 AutoShop 主程序、无需 UI），监听 **TCP 502（Modbus TCP，全区域 FC1/2/3/4 只读 + FC5/6 写回读一致）** 与 **TCP 127.0.0.1:12939（AutoShop 原生协议，厂商通信栈 IsEthOnLine=ONLINE、GetExecMode/GetGcmState/GetPlcLable rc=0）**。MCP 端到端已实证：Inovance 目标 profile（host=127.0.0.1:502）→ `plc_probe_target` TCP_CONNECTED、`plc_read_tags` D100/M0/X0/R100 全部 quality=good。**监视（monitoring）链路从此有真实后端**。原生桥新增 `PlcMcpComm_SetTarget/IsOnline`（tools/AutoShopNativeBridge 1.1.0）支撑对 12939 的实连。
4. 模拟器运行时程序默认 STOP（寄存器全 0）；启动运行（RUN）的命令路径与 .hcpp 编译闭环仍待逆向（CompileSystem.dll 是运行时内置函数库，不是离线编译器；CMD_EXECUTE.dll 是软 PLC 指令解释器）。

共享交接目录：`C:\Users\18735\AppData\Local\Temp\plcmcp-handoff\`（各代理把跨代理事实写入 `handoff.json` 与 `autoshop-samples\`，只增不改他代理键；示例工程一律放该目录，禁止写入仓库）。

## 目录所有权（阶段二）

| 代理 | 目标 | 允许编辑 | 禁止编辑 |
|---|---|---|---|
| S2：原生插件桥 | 进程内插件 + 通信栈直调 + 管道 IPC | `tools/AutoShopNativeBridge/**`（新建）、`src/PlcMcp.Engineering/Workers/AutoShop/NativeBridge/**`、`tests/PlcMcp.Engineering.Tests/NativeBridgeTests.cs`、`docs/inovance-autoshop-native-bridge.md` | 其他一切 |
| S3：UIA 自动化层 | 窗口/菜单测绘、样本工程、触发编译、模拟器探查；第二波：破输入通道打编译闭环 | `src/PlcMcp.Engineering/Workers/AutoShop/Uia/**`、`tests/PlcMcp.Engineering.Tests/AutoShopUiaTests.cs`、`docs/inovance-autoshop-uia-map.md` | 其他一切 |
| S2：原生插件桥 | 进程内插件、通信栈直调、通信配置逆向；第二波兼做工程格式种子侦察（.hcpp 情报续查） | `src/PlcMcp.Engineering/Workers/AutoShop/ProjectFormat/**`（种子侦察阶段）、`tests/PlcMcp.Engineering.Tests/AutoShopProjectFormatTests.cs`、`docs/inovance-autoshop-project-format.md` | 其他一切 |
| S1：工程格式与生成器（待样本就绪） | 工程文件格式深度逆向、程序/变量/轴组态生成器（样本就绪后另行派发） | 同 ProjectFormat 目录 | 其他一切 |
| S4：MCP 集成与治理（待 S1/S2/S3） | 工具注册、下载/强制审批门控、usage 文档 | `src/PlcMcp.Server/**`、`docs/usage.md`、`tests/PlcMcp.Tests/AutoShopToolTests.cs` | Adapters/Core/Contracts |

## 安全红线（所有代理必须遵守）

1. 编译/生成/格式往返 = 安全，可自由实验。
2. 监视/读取 = 只读，可自由实验（含模拟器）。
3. **下载/Force/写入到实体 PLC：本阶段禁止执行**，只允许对 AutoShop 在线模拟器（若有）做闭环验证；实现侧提供能力与门控，但真实动作必须等 S4 的审批门控完成后才可打开。
4. 重启 InoVarSharedService 允许（可回滚）；重启 AutoShop 仅在其无未保存工程时允许，重启前记录窗口状态。
5. 禁止删除/改写用户数据与安装目录内任何文件（插件 DLL 只允许**复制**到候选插件目录做加载实验，实验后从原安装目录移除并记录）。

## 各代理里程碑（不达标即如实报告，禁止伪报）

### S2：原生插件桥（x86 进程内）
- M1 插件加载实证：7 导出契约 DLL 被宿主（AutoShop 或 InoVarSharedService，实测确定）加载，落盘标记日志为证；确定真实插件加载目录与加载时机。
- M2 宿主接口发现：反汇编宿主对插件 GetInterface/QueryInterface 的调用路径，确定插件能拿到的宿主服务（至少列出可枚举者）；拿不到就如实记录，并转走通信栈直调路线。
- M3 通信栈直调：重建 `CSoftAddrMap` 等关键结构布局（依据宿主内调用点反汇编 + 结构使用模式），x86 垫片以静态导入调用 `CCommunicateEth::ReadElemValue/ReadSeriesWordElem/GetExecMode/GetPlcLable` 等只读方法；有模拟器则闭环验证读取，无则编译级验证 + 帧级单测。
- M4 IPC：插件/垫片 → 命名管道 → C# worker（复用 ExternalWorkerProtocol 安全姿态），C# 侧 client/worker/模型 + mock 管道测试。
- 交付物落盘前自验构建（原生工程用本机 VS2022 v143 x86 编译，输出二进制不入仓库，只入 `tools/AutoShopNativeBridge/` 源码 + 构建脚本）。

### S3：UIA 自动化层
- M1 窗口/控件测绘：主窗口层级、菜单路径（工程/编译/下载/监视/轴组态）、快捷键（F7?）、DuiLib 无 HWND 控件的坐标/OCR 应对策略；全部写入 `docs/inovance-autoshop-uia-map.md` 并同步 handoff.json。
- M2 样本工程：经 UI 新建 H5U、H3U 空工程各一，保存到 handoff 样本目录；若能低成本插入一段 ST/LD 与一个变量、一条轴组态记录则做，不强求。
- M3 编译触发与结果抓取：找到编译入口并成功触发一次编译，抓到编译结果文本（窗口文本/剪贴板/OCR 任一，注明方式与置信度），空工程编译通过为证。
- M4 模拟器探查：H5U 在线模拟是否存在、如何启动、虚拟 PLC 端点是什么；结论写入 handoff.json。
- C# 侧交付：窗口查找/菜单调用/对话框处理/文本抓取的基础库（UI 无关部分单测；对真实 UI 的操作脚本放工具目录并注明不可进 CI）。

### S1：工程格式与生成器（在 S3 产出样本后启动，届时另行派发）
- 基于 S3 的 H5U/H3U 空工程样本做格式逆向；目标：能离线生成含 ST/LD 程序、全局变量、注释、轴组态的可打开工程；并以“AutoShop 打开→编译通过”做往返验收（与 S3 的编译触发能力配合）。
- 提供变量导出/读取（manifest 提取），支撑监视与符号浏览。

### S4：MCP 集成与治理（在 S1/S2/S3 完成后启动，届时另行派发）
- 工具：`plc_autoshop_compile`、`plc_autoshop_generate_project`、`plc_autoshop_monitor_window`（只读）、`plc_autoshop_download`（审批门控）、轴组态读取/生成；全部默认禁用，显式开关启用。
- 下载/Force 必须走 DeploymentPlan + 审批模式（对齐 contracts.md 第 56-58 行），不得绕过；模拟器闭环可用于测试。
- 更新 `docs/usage.md` 与能力表；测试离线可跑。

## 阶段二验收标准
1. 构建/测试全绿（含原生工程可复现构建）。
2. 每个 MCP 工具都有真实后端路径与测试；未实现项如实 Unsupported。
3. 空工程编译、程序生成→打开→编译的往返、监视读取（模拟器或 ITP 证据）至少其一有实证记录。
4. 红线零违反；越界文件零出现。

---

# 阶段三：功能对齐（官方软件能做的，MCP 都要能做）

目标：编译、下载/上传、写参数、强制、监视表、轴组态——全部以受治理的 MCP 工具形式落地。

关键事实（阶段二收官）：
- 输入过滤封死 UI 自动化；CLI 无编译开关（-open 不存在，裸路径参数可打开但止步于"加载Hcp文件中的工程属性信息错误"）。
- **解密已通**（CUCodeCrypt：11B ADD/XOR、密钥 "SclSoftware"、明文 UTF-16LE XML，D3 三路实证）；**12939 下载协议已通**（D1：文件传输会话+登录，字节级落盘实证）；**通信栈只读直调已通**（S2 垫片，IsEthOnLine=ONLINE）。
- 编译在 AutoShop 进程内（STCompiler.dll 无导出）；装载在模拟器为桩。
- **本阶段主攻方向 = 进程内注入桥**：往独立 AutoShop 实例注入自有 DLL（绕过输入过滤，直调进程内通信栈/编译管线），经命名管道与 MCP worker 通信；治理上延续审批门控（下载/强制/写参数需 DeploymentPlan+审批；默认仅回环/模拟器目标可自动放行）。

## 目录所有权（阶段三）

| 代理 | 目标 | 允许编辑 |
|---|---|---|
| E3：打开失败根因 + 编译管线逆向 | .hcp 打开失败条件定位并给出"能通过打开校验的容器/XML 构造法"；全部编译(0x80E1)处理链→编译函数签名→产物路径；进程内命令分发机制 | `docs/inovance-autoshop-open-and-compile.md`（新建）、`tools/AutoShopNativeBridge/src/**`（仅文档/脚本） |
| E2：进程内注入桥 | x86 注入器 + 桥 DLL（管道 IPC、只读/写/强制/下载操作面、回环硬校验）+ C# client/worker | `tools/AutoShopInjectBridge/**`（新建）、`src/PlcMcp.Engineering/Workers/AutoShop/InjectBridge/**`、`tests/PlcMcp.Engineering.Tests/InjectBridgeTests.cs`、`docs/inovance-autoshop-inject-bridge.md` |
| E4：MCP 集成 | 编译/下载/写参数/强制/监视工具的注册、审批门控、usage 文档 | `src/PlcMcp.Server/**`、`docs/usage.md`、`tests/PlcMcp.Tests/AutoShopInjectToolsTests.cs` |

红线（阶段三新增）：注入只允许在**本代理启动的独立 AutoShop 实例**上进行，禁止注入用户交互中的实例（PID 归属保护同 P2 模式）；下载/强制/写参数默认仅回环与本地模拟器，真机目标必须显式审批配置；实验后实例干净退出；不修改安装目录。
