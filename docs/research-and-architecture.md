# 多品牌 PLC MCP：调研结论与架构决策

调研日期：2026-09-23。资料入口为 websearch-deepseek，关键依赖另用公开仓库许可证、实际 NuGet API 文档和本机编译核对。搜索摘要可能混合版本和机型；下文不把摘要中的版本号、端口、字节序或未发现 API 的结论当作全品牌事实。

## 结论

可以把 PLC 工程师的大量重复工作自动化，但通用层应统一任务和证据，不统一厂商私有编译器。需要三条独立通道：运行时通信、工程软件自动化、HMI/SCADA 组态。MCP 负责向 Agent 暴露确定性能力；实时扫描、闭环控制、急停和功能安全仍由设备控制系统承担。

`plctap` 是运行通信与排障参考，不是完整工程平台。推荐独立产品内核，通过适配器复用其设计或经许可的代码，避免把编译下载硬塞进通信协议客户端。

## 常用工程任务与抽象

| 任务 | 共通对象 | 不可抹平的差异 |
|---|---|---|
| 项目建立/读取/修改 | 项目、程序块、POU、变量、类型、注释、调用图 | 工程格式、源码是否保留、加密/访问保护 |
| 编程 | ST/SCL、LD、FBD、SFC；联锁、步序、计时计数、报警、配方 | 任务调度、保持区、初始化、厂商库、ST 方言、图形网络序列化 |
| 编译和校验 | 编译任务、诊断、源快照和产物哈希 | 目标 CPU 编译器、设备包、库版本、授权 |
| 仿真/调试 | 用例、变量采样、趋势、断点/在线监视 | 软件仿真器 API、周期语义、运动轴仿真覆盖 |
| 上传/下载/在线改动 | 目标身份、差异、影响、备份、执行与回读证据 | 热下载/停机、保持值变化、源码上传能力、回滚并非总可用 |
| 硬件和网络组态 | CPU、模块、槽位、IO、总线、地址 | 订货号、GSDML/EDS/ESI、设备目录、分布式 IO/运动配置 |
| HMI/SCADA | 变量绑定、画面、报警、趋势、配方、用户 | WinCC/GOT/NA 等工程格式、控件/运行许可和下载路径 |
| 维护/排障 | CPU 状态、诊断码、通信事实、在线离线比对 | 厂商诊断结构、访问等级、时间戳和质量码 |

运动、PID、模拟量标定、通信及设备功能块需要按 PLCopen/厂商库定义模板。ST 适合先实现生成和差异；LD/FBD/SFC 不应承诺通用无损转换。公共 IR 仅覆盖经测试的语义子集，厂商扩展原样保留并标明不可移植项。

## 品牌要按产品线拆分

| 品牌/产品线 | 工程软件 | 运行通信路径 | 工程自动化判断 |
|---|---|---|---|
| Siemens S7-1200/1500 | TIA Portal / STEP 7 | S7comm（条件受限）、机型支持的 OPC UA | Openness 为主要官方路径；导入、编译、硬件/网络对象和下载按版本/许可验证 |
| Siemens S7-200 SMART | STEP 7 Micro/WIN SMART | S7comm；V 区常映射 DB1 | **不是 TIA Openness 目标**。当前已有 smart200 MCP 可参考导入/编译/网络有效性/往返导出/落盘校验，但引擎注入属于实验路线 |
| Siemens 300/400/旧 200 | STEP 7 Classic / MicroWIN / 部分 TIA 支持 | S7comm、模块相关协议 | 独立 legacy 后端；不因 S7 读通就宣称工程可操作 |
| Omron CP/CJ/CS | CX-Programmer / CX-One | FINS（须有对应接口）、Host Link 等 | 工程导出/导入和自动化能力要按版本申请核实 |
| Omron NJ/NX/NY | Sysmac Studio | CIP 符号、部分机型 OPC UA/FINS | Compolet 是通信产品；未核实到覆盖全工程的公开 Openness 等价 API，不据此断言绝对不存在 |
| Mitsubishi FX/Q/L/iQ-F/iQ-R | GX Works2 / GX Works3 | MC/SLMP、MX Component；部分设备/模块 OPC UA | MX Component 不等于 GX 编译 SDK；GX Works3 MCP 社区桥采用 UIA，需版本锁定和回读 |
| Inovance H/Easy | AutoShop | 机型支持的 Modbus TCP、EtherNet/IP 等 | AutoShop API 公开证据不足；先做导出物/变量映射，不承诺自动编译下载 |
| Inovance AM/AC | InoProShop 等 OEM 平台 | 机型支持的 OPC UA、Modbus、CIP | CODESYS 衍生关系不保证脚本/MCP 插件可用；必须检查 OEM profile、包、授权和厂商支持 |

OPC UA 是统一数据访问路径，不是保证存在的统一 PLC 下载 API。MC 端口通常可配置，不能把 44818 当作三菱通用默认；FINS 报文字和 SLMP binary 字的端序不同，多字变量须独立配置。原始 S7comm 也不等于 S7comm-plus，优化 DB 不能随意按绝对偏移读取。

## 可直接采用或作为参考的资源

| 资源 | 用法建议 | 许可/限制 |
|---|---|---|
| [plctap](https://github.com/ymxc152/plctap) | 纯编解码、目标锁、规则诊断、报文切分、MCP 工具测试 | 已核对 LICENSE：MIT，2026 plctap contributors；当前项目未复制其代码 |
| [S7netplus](https://github.com/S7NetPlus/s7netplus) | 已以 NuGet 0.20.0 使用，只调用 Open/Read | [MIT 原文](https://github.com/S7NetPlus/s7netplus/blob/main/License.txt)，须保留许可证；库本身具备写能力但本项目未暴露 |
| [python-snap7](https://github.com/gijzelaerr/python-snap7) | S7 独立交叉验证客户端 | Python 包和 native 库各自核对许可；支持能力按 CPU 验证 |
| [pymodbus](https://github.com/pymodbus-dev/pymodbus)、[NModbus](https://github.com/NModbus/NModbus) | Modbus 对照服务/替换底层 | 固定版本后核对 LICENSE，不从搜索摘要推断 |
| [pymcprotocol](https://github.com/senrust/pymcprotocol) | MC/SLMP 对照实现 | 不覆盖 GX 工程编译 |
| [OPCFoundation .NET Standard](https://github.com/OPCFoundation/UA-.NETStandard)、[asyncua](https://github.com/FreeOpcUa/opcua-asyncio) | 后续 OPC UA 证书、浏览、读取、订阅 | 校验许可证、证书信任、会话资源；不要用 SecurityPolicy None 作为通用生产默认 |
| [GX Works3 MCP bridge](https://github.com/coder007rahul/gxworks3-mcp-bridge) | 参考编辑器回读校验、UIA 编译输出 | 非厂商稳定 API；复制前核对目标 commit 的许可 |
| [CODESYS 官方 MCP 白皮书](https://api-it.codesys.com/fileadmin/user_upload/CODESYS_Group/Download/Whitepaper/Whitepaper-CODESYS-Development-System-MCP-Server-en.pdf) | 优先评估原厂已有 MCP，避免重复做 IDE 脚本桥 | 版本、PDE 许可、OEM 分发适用性须验证；并非所有 CODESYS 衍生 IDE 自动支持 |
| [CODESYS ScriptOnline](https://content.helpme-codesys.com/en/ScriptingEngine/ScriptOnline.html) | 工程后台、build、online application 与部署候选接口 | 与 IDE 内部脚本环境绑定；普通 CPython 环境不等价 |
| [IronPLC](https://github.com/ironplc/ironplc)、[Beremiz](https://github.com/beremiz/beremiz)、[OpenPLC](https://github.com/thiagoralves/OpenPLC_v3) | ST 预检、软件测试台、编译器结构参考 | 不能编译成任意品牌可下载机器码；按仓库版本核对许可证 |
| [PLCopen XML](https://www.plcopen.org/technical-activities/xml-exchange) / [OPC UA PLCopen](https://opcfoundation.cn/guifan/60_44) | 交换对象模型和符号语义参考 | 标准存在不等于各 IDE 完整实现；必须往返测试 |

扩展品牌可优先考察 Beckhoff TwinCAT Automation Interface/ADS、Rockwell Logix Designer SDK/L5X、Schneider 的具体 CODESYS/非 CODESYS 产品线，而不是单靠厂商名称分类。

官方资料索引：
- [Siemens TIA V21 技术资料](https://support.industry.siemens.com/cs/attachments/109989772/TIA_Portal_V21_technical_slides_EN.pdf)
- [Omron Sysmac Studio 产品](https://www.ia.omron.com/products/family/3077/)
- [Mitsubishi MX Component](https://www.mitsubishielectric.co.jp/fa/products/cnt/plceng/smerit/mx_component/)
- [Inovance AM300 官方产品页](https://www.inovance.com/global/content/details_815_403255.html)
- [CODESYS MCP 配置命令](https://content.helpme-codesys.com/zh-CHS/CODESYS%20Development%20System%20MCP%20Server/_idemcp_cmd_configure_mcp_client.html)

## 契约边界与实施顺序

1. Contracts 只定义 Target/Tag/Capability/Operation/Report。能力必须包含实现状态、支持版本和验证证据，不能由品牌名推断。
2. Runtime 负责目标排队、限流、取消、类型校验、计划与审计，不引用厂商 SDK。
3. Protocol adapter 只做运行通信。值同时返回数据类型、质量和采样时间；多标签非同一扫描周期的快照，必须如实说明。
4. Engineering worker 独立进程，锁定 IDE 版本/位数和项目工作副本。任务用 jobId 查询结果，不把几分钟的编译塞成无法恢复的单请求。
5. 生成链路：需求/IO/状态机→受限源码→静态分析→厂商导入编译→仿真测试→往返导出/磁盘重开→准备下载计划→受信审批→执行与回读。每步有证据，失败明确标记。
6. HMI worker 独立于 PLC worker，重用变量 manifest，但保留品牌画面格式、报警、趋势和用户管理扩展。
7. MCP Server 保持 stdio；后续需要远程时采用官方 SDK 的 Streamable HTTP 与认证，不能直接把本地匿名 stdio 逻辑暴露网络。工具 annotations 只是客户端提示，不是权限控制。

第一阶段可运行交付是只读协议基座和模拟操作链；第二阶段选一条工程闭环（优先 TIA Openness，SMART 必须另列），第三阶段 CODESYS/OEM 与其他品牌验证，第四阶段 HMI/SCADA 和持续监控。每增加一个后端，按精确型号/固件/IDE/库版本做台架验收，不给“全品牌支持”的笼统承诺。

对现场写入采用受信审批组件颁发的授权，而不是让模型同时获得和使用一个 token 就算人工批准。本版 token 仅用于模拟计划的一次性消费与状态漂移检测。工程离线编译本身不等同危险现场操作；当前缺少编译能力是因为后端未实现，不应描述成安全禁用。

## 本次证据范围

已验证：源码构建、MCP stdio 调用、模拟计划/应用、四协议 localhost 握手/报文读取与部分故障路径。
未验证：任何真实 CPU、工程 IDE 编译/下载/组态、OPC UA 安全会话、设备动作、现场安全认证。内存模拟器仅模拟标签值，不执行 PLC 扫描和 IEC 程序。开发时使用的本机 PLC 旧项目只作设计参考，未修改。
