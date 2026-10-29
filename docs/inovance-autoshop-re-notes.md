# 汇川 AutoShop 反编译情报归档

本文档固化本机 AutoShop V4.12.0.2 的反编译情报，供后续插件式/桥接式集成参考。与 `docs/inovance-integration-plan.md` 第一节结论对应：**本版只归档情报，不实现插件、不直接调用厂商 DLL**。

- 归档日期：2026-10-23。
- 反编译对象：本机安装的汇川 AutoShop（深圳市汇川技术股份有限公司产品）。
- 性质声明：以下接口均为**逆向所得，无厂商背书**；所有符号来自公开 PE 导出表、PDB 线索与运行时观测，仅供互操作研究。与 `docs/contracts.md` 的能力声明纪律一致：未真机验证的能力不得宣称已支持。

标注约定（详见第 8 节）：【符号事实】= 本次会话用 dumpbin / tasklist / netstat / PowerShell / 文件内容亲自复核；【任务书情报】= 任务书或派发说明提供、本次未能独立复核；【推断】= 基于事实的推理。文中每个"导出符号 / 路径 / 端口"类事实均注明证据来源。

---

## 1. 环境与工具链证据

### 1.1 安装与版本

| 项 | 值 | 证据 |
|---|---|---|
| 安装路径 | `D:\Inovance Control\AutoShop` | 【符号事实】目录列举 |
| AutoShop.exe 文件版本 | 4.12.0.0（FileVersion/ProductVersion） | 【符号事实】PowerShell `VersionInfo` |
| 厂商 | 深圳市汇川技术股份有限公司 | 【符号事实】PowerShell `VersionInfo.CompanyName` |
| 指令集版本文件 | `LDCmdVersion.xml`：`GRY`/`GRYP` = 6.12.0.0，`MB_Master` = 6.1.0.1 | 【符号事实】文件内容；版本号与"V4.12"的对应关系为【推断】 |
| UI 框架 | DuiLib（MFC 系 x86 应用），根目录存在 `DuiLib.dll` | 【符号事实】目录列举；"MFC 应用"定性为【推断】 |

任务书所称"V4.12.0.2"与 FileVersion 4.12.0.0 的末位差异（补丁号）未在 PE 版本资源中单独体现，按【任务书情报】沿用。

### 1.2 进程与服务（运行时证据）

| 对象 | 证据 | 来源 |
|---|---|---|
| AutoShop.exe 运行中，PID 24492，监听 UDP 0.0.0.0:65241 | 【符号事实】 | `tasklist /FI "IMAGENAME eq AutoShop.exe"`、`netstat -ano` |
| InoVarSharedService 服务运行中，PID 22488，TCP 127.0.0.1:13939 LISTENING | 【符号事实】 | `netstat -ano`、`Get-CimInstance Win32_Service` |
| 服务注册：ImagePath `D:\Inovance Control\AutoShop\AutoVarShared\InoVarSharedService.exe`，Start=2（自动），LocalSystem | 【符号事实】 | `Get-ItemProperty HKLM\SYSTEM\CurrentControlSet\Services\InoVarSharedService` |
| 上述进程为 x86/WOW64 | 【推断】 | PE 头均为 `14C machine (x86)`/PE32（见 1.3），宿主 OS 为 64 位（win32 10.0.22631 x64 环境信息），x86 进程在 64 位 Windows 上必然经 WOW64 运行；任务书"WOW64=332"表述与之一致，但直接的 WOW64 标志读取因进程模块枚举访问受限未完成 |

### 1.3 各二进制 PE 位数与链接时间戳（dumpbin //headers，全部亲自复核）

| 文件 | 位数 | time date stamp（dumpbin 译文） |
|---|---|---|
| `AutoShop.exe` | x86 (14C) | Thu Jul 23 15:42:58 2026 |
| `CommunicateEthernet.dll` | x86 | Thu Jul 23 15:35:39 2026 |
| `InterfaceMgr.dll` | x86 | Thu Jul 23 15:33:36 2026 |
| `Communicater.dll` | x86 | Thu Jul 23 15:35:58 2026 |
| `CommunicaterModule.dll` | x86 | Thu Jul 23 15:34:45 2026 |
| `H530Communicate.dll` | x86 | Thu Jul 23 15:36:00 2026 |
| `AutoVarShared\Plugin.dll` | x86 | Tue Jan 20 19:49:06 2026 |
| `AutoVarShared\InoVarSharedService.exe` | x86 | Tue Jan 20 19:49:07 2026 |
| `AutoVarShared\PluginCommunicate.dll` | x86 | Tue Jan 20 19:49:08 2026 |
| `AutoVarShared\PluginGlobalFunc.dll` | x86 | Tue Jan 20 19:49:03 2026 |
| `ACmd.dll` | x86 | **Mon May 31 13:31:10 2010** |
| `APipe.dll` | x86 | **Mon May 31 13:31:13 2010** |
| `AwPluginVector.dll` | x86 | Wed Jun 16 21:05:09 2010 |

【符号事实】以上均为 dumpbin `//headers` 输出。两个构建纪元（2010 与 2026）并存：ACmd/APipe/AwPluginVector 为 2010 年链接（与任务书提示一致，2010 是真实链接时间而非默认值），其余为 2026 年构建。【推断】2010 纪元模块与 Plugin 框架 ABI 长期未变，侧面说明插件契约稳定；但也意味着不能假设全部插件同批构建。

### 1.4 工具链

- dumpbin：`C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.34.31933\bin\Hostx64\x64\dumpbin.exe`（VS2022，14.34.31933.0）。【符号事实】路径存在且可执行，本文所有 dumpbin 摘录均出自该工具。
- dnSpy：`D:\dnSpy-net-win64`。【任务书情报】仅对 .NET 程序集有用；AutoShop 全链路为原生 x86，本任务不需要（未独立复核该路径）。
- 反汇编/PDB 解析工具（IDA/Ghidra/llvm-pdbutil/DIA）本机未配置，是第 7 节路线的前置缺口。

---

## 2. 插件框架（PluginInterface）

### 2.1 框架本体 Plugin.dll

`D:\Inovance Control\AutoShop\AutoVarShared\Plugin.dll`，x86，2026-01-20 链接，**33 个导出**（dumpbin `//exports`，number of functions = number of names = 33）。【符号事实】完整清单（demangled 由 dumpbin 自带 undname 给出）：

构造/析构/赋值/比较（14 个）：

- `PluginInterface::EventCenter::EventCenter(void)`（private）、`EventCenter(EventCenter const&)`（public）、`~EventCenter(void)`、`operator=`
- `PluginInfo::PluginInfo(void)`、`PluginInfo(PluginInfo const&)`、`~PluginInfo`、`operator=`
- `PluginVersion::PluginVersion(void)`、`operator=`、`operator==(PluginVersion const&) const`、`operator<(PluginVersion const&) const`
- `std::pair<std::string const, PluginInterface::DLibrary*>::~pair(void)`

PluginManager（public，均在 `PluginInterface` 命名空间）：

- `static PluginManager* PluginManager::GetPluginMgr(void)`
- `bool PluginManager::LoadLibiary(std::string const&)`（注意导出名拼写为 `LoadLibiary`，非 `LoadLibrary`）【符号事实】
- `int PluginManager::LoadLibiaryFromFolder(std::string const&, bool)`
- `void PluginManager::RegisterInterface(PluginInfo const&)`
- `void PluginManager::GetPluginsByName(std::vector<std::string const*>&) const`
- `bool PluginManager::IsLibraryLoaded(std::string const&) const`
- `CPluginBase* PluginManager::GetPluginByEnum(PluginEnum)`
- `void PluginManager::RegisterPluginEvent(PluginEnum, CEventHandler*)` / `UnRegisterPluginEvent(PluginEnum, CEventHandler*)`
- `void PluginManager::RarseEvent(PluginEnum, LogLevel, std::string)`（导出名拼写为 `RarseEvent`）【符号事实】
- `void PluginManager::Unload(std::string const&)` / `UnloadAllLibrary(void)`
- `private: static std::string PluginManager::GetPluginLibName(std::string const&)` / `ResolvePathExtension(std::string const&)`
- `private: static PluginManager* PluginManager::m_pPluginMgr`（单例指针，数据导出）

EventCenter（private 实现 + PluginManager 转发）：

- `private: void EventCenter::ListenEvent(PluginEnum, CEventHandler*)` / `UnListenEvent(PluginEnum, CEventHandler*)`
- `private: void EventCenter::PublishEvent(PluginEnum, LogLevel, std::string)`

要点修正（相对任务书表述）：`ListenEvent/PublishEvent/UnListenEvent` 在导出表中是 `EventCenter` 的 **private** 成员；公开入口是 `PluginManager::RegisterPluginEvent/UnRegisterPluginEvent/RarseEvent` 三个转发方法。【符号事实，来自 mangled name 的访问级别标记 `AAE`(private) vs `QAE`(public)】

`PluginInfo` 构造签名（mangled：`??0PluginInfo@@QAE@W4PluginEnum@@V?$basic_string@DU?...@std@@I@Z`）：

- `public: __thiscall PluginInfo::PluginInfo(enum PluginEnum, class std::string, unsigned int)`【符号事实】——即（插件类型枚举, 名称字符串, 版本号 uint）。
- `PluginVersion` 支持 `==`/`<` 比较，说明版本比较走 `PluginVersion` 而非裸 uint。【符号事实】

### 2.2 PDB 与源路径线索

`AutoVarShared\` 下存在完整符号与导入库：`Plugin.dll/Plugin.lib/Plugin.pdb`、`PluginCommunicate.dll/.lib/.pdb`、`PluginGlobalFunc.dll/.pdb`、`PluginLogger.dll/.lib/.pdb`。【符号事实，目录列举】

从 `Plugin.pdb` 明文字符串提取到的路径线索【符号事实，`grep -a` on Plugin.pdb】：

- `E:\ReleaseCode\autoshop_main\Src\InoVarSharedService\LibRelease\Plugin.exp`
- `E:\ReleaseCode\autoshop_main\Src\InoVarSharedService\Release\Plugin.pdb`
- `E:\ReleaseCode\autoshop_main\Src\InoVarSharedService\Plugin\Plugin.def`（存在 .def，说明导出按定义文件声明）
- 链接器：`E:\VS2008\VSInstall\VC\bin\link.exe`（VS2008 时代工具链，与 2010 时间戳互证）

【推断】`Plugin.lib` 随产品发布意味着第三方垫片可以直接链接原版导入库获得精确 ABI，无需 GetProcAddress 猜约定；这是第 7 节垫片路线的现实基础。

### 2.3 插件契约（7 导出）与已知实例

插件 DLL 契约 = 恰好 7 个 C 导出（非 mangled，stdcall/cdecl 风格原名导出）【符号事实，dumpbin `//exports`】：

`GetPlugInName` / `GetPlugInType` / `GetPlugInVersion` / `GetInterface` / `QueryInterface` / `GetAuthor` / `GetCopyRight`

实例（均位于安装根目录）：

| 插件 | 导出数 | 时间戳 | 备注 |
|---|---|---|---|
| `ACmd.dll` | 7（契约全集） | 2010-05-31 13:31:10 | AI 指令插件（见第 5 节） |
| `APipe.dll` | 7（契约全集） | 2010-05-31 13:31:13 | 管道类插件 |
| `AwPluginVector.dll` | 1 | 2010-06-16 | 另一插件实例，导出内容未展开【未复核】 |
| `AICom.dll` | **0** | — | 无导出，非插件契约 DLL（见第 5 节） |

`AutoVarShared\PluginCommunicate.dll` 再导出 12 个 `PluginInterface` 符号（EventCenter/PluginInfo/PluginVersion 的构造、赋值、比较子集）【符号事实】——即服务端进程内静态携带了同一插件 ABI 的精简副本，与 `Plugin.dll` 的 33 导出是同源 ABI 的不同链接切面。

---

## 3. PLC 通信栈（CommunicateEthernet / InterfaceMgr）

### 3.1 CCommunicateEth 完整导出清单

`D:\Inovance Control\AutoShop\CommunicateEthernet.dll`，x86，2026-07-23 15:35:39 链接，**84 个导出**（dumpbin `//exports`）。【符号事实】全部方法均为 `CCommunicateEth` 的 **static**（mangled `SA`），返回 `J`（long，32 位）。除特别说明外，业务方法签名尾部统一带 `(EnumExecuteMode, EnumCommandLevel)` 两参数；带 UI 回传的带 `CWnd*`。

读/监控（与只读集成最相关）：

- `ReadElemValue(CSoftAddrMap*, CWnd*, EnumExecuteMode, EnumCommandLevel)`
- `ReadSeriesWordElem(CStringT<wchar> const&, unsigned short const*, CWordArray*, EnumExecuteMode, EnumCommandLevel)`
- `ReadWatchElemValue(CMonitElemBuffer*, CWnd*, ...)`
- `ReadFouceElemTable(CSoftAddrMap*, ...)`（导出名拼写 `Fouce`）【符号事实】
- `ReadSysCfg(unsigned char*, CWnd*, ...)` / `GetConfigInfo(EnumConfigTypeTag, _CONFIG_INFO*, ...)`
- `ReadWirelessCfg(unsigned char*, CWnd*, ...)`

写/强制（集成中禁用，仅归档）：

- `WriteElemValue(CSoftAddrMap*, CWnd*, CSoftAddrMap*, ...)`
- `Force(CSoftAddrMap*, CWnd*, CSoftAddrMap*, ..., int)` / `Unforce(CSoftAddrMap*, CWnd*, CSoftAddrMap*, ...)`
- `WriteSysCfg(unsigned char*, CWnd*, ...)`、`WriteWatchElem(unsigned char, EnumProgType, CMonitElemBuffer*, CWnd*, ...)`、`WriteScopeCofig(ScilloScopeCfg, CMonitElemBuffer*, OSCMonitorElem*, CWnd*, ...)`、`WriteCAMCofigData(const wchar_t*, CWnd*, unsigned short, ...)`、`WriteCANLinkCfg(const wchar_t*, CWnd*, EnumCommunicaterCmd, ...)`、`WriteModbusCofigData(const wchar_t*, CWnd*, ...)`、`WriteTcpModbusCofigData(const wchar_t*, CWnd*, ...)`、`WriteWirelessCfg(unsigned char*, CWnd*, ...)`、`RestoredWirelessCfg(...)`

PLC 状态/信息：

- `GetExecMode(EnumPlcRunModel*, int, ...)`
- `GetGCMState(EnumGCMState*, CWnd*, ...)` / `GetGCMInfo(tagGCMInfo*, ...)` / `GetGCMClock(tagGCMTime*, ...)` / `SetGCMClock(tagGCMTime*, ...)`
- `GetPlcLable(_PLC_LABLE*, EnumCommunicaterType, ...)`
- `GetVersionInfo(tagPLCVERSION*, int, ...)` / `GetProgInfo(_PROG_INFO*, ...)`
- `SearchPLC(tagEthernetInfo*, _PLC_LABLE*, EnumCommunicaterType, int, ...)` / `IsEthOnLine(tagEthernetInfo*)` / `IsEthOnLine()` / `isDialConnect()` / `SetCommunicatConfig(tagEthernetInfo*, HWND__*)`

上传/下载（工程面，集成中禁用，仅归档）：

- `Upload(const wchar_t*, EnumCommunicaterCmd, CWnd*, int, ...)` / `Download(const wchar_t*, EnumCommunicaterCmd, CWnd*, int, ...)`
- `DownloadPlcSoft` / `DownloadFPGA` / `DownloadDB(const wchar_t*, CWnd*, SoftElemAddrs*, int, ...)` / `DownloadModuleConfig` / `DownloadComment(const wchar_t*, unsigned short, CWnd*, int, ...)`
- `UploadModbus(const wchar_t*, CWnd*, ...)` / `UploadModbusTcp(...)` / `UploadModuleConfig(...)` / `UploadCANLink(..., EnumCommunicaterCmd, ...)` / `UploadCAM(...)` / `UploadComment(...)` / `UploadDB(...)` / `UploadElevatorLog(const wchar_t*, CWnd*, ...)`
- `UploadScopMonData(unsigned char*, unsigned int&, CWnd*, ...)` / `StopUploadScopMonData(CWnd*, ...)`
- `UploadHistoryFaultData(unsigned char**&, unsigned int&, CWnd*, ...)` / `UploadSysRunLog(unsigned char**&, unsigned int&, CWnd*, ...)` / `UploadModuleFaultInfo(int, int, unsigned char*, CWnd*, ...)` / `ClearHistoryFaultData(CWnd*, ...)`
- `ProgramValidate(tagProgramCheck*, EnumCommunicaterCmd, ...)` / `CompleteDownloadCmd(EnumCommunicaterCmd, CWnd*, ...)`
- `PrepareFPGA(const wchar_t*, ...)` / `SwitchUpgradeMode(unsigned char*, ...)`

命令/口令/杂项：

- `SettingPassword(EnumCommunicaterCmd, CStringT<wchar> const&, CStringT<wchar> const&, ...)` / `CheckPassword(EnumCommunicaterCmd, CStringT<wchar> const&, ...)`
- `SimpleCommand(CStringT<wchar> const&, ...)` / `SingleCommand(CStringT<wchar> const&, unsigned short, ...)`
- `SimpleCommonCmd(EnumSimpleCommonCmdType, ...)` / `SingleSimpleCommand(EnumSimpleCmdType, ...)`
- `EnableUpload(int, ...)` / `Enable(EnumFunCode, ...)` / `ModemDial(char*, int, int, unsigned long, unsigned long)` / `SendATCmd(char*, int, int)` / `QueryCanLinkStationInfo(unsigned char*, ...)` / `QueryCanLinkStationState(unsigned char*, unsigned short, ...)`
- `Close(void)` / `Destroy(void)`
- 单例与内部件：`static CCommunicateEth* CCommunicateEth::Instance(void)`；`GetBuffer(void) const`→`CBuffer*`、`GetCommThread(void) const`→`CCommThread*`、`GetTaskPool(void) const`→`CTaskPool*`、`private RunCommThread(CCommunicateEth*, CCommandBase*, EnumExecuteMode)`
- 静态数据导出：`int CCommunicateEth::m_bPlcIdentifier`、`CCommunicateEth* CCommunicateEth::m_communicate`

依赖类型与枚举（从 mangled name 还原）【符号事实】：MFC/ATL `CStringT<wchar>`（路径与命令串，宽字符）、`CSoftAddrMap`（软元件地址映射，读写入口的核心参数）、`CWordArray`、`CMonitElemBuffer`（监控缓冲）、`CWnd*`（UI 消息回传窗口）、`CBuffer/CCommThread/CTaskPool/CCommandBase`；结构 `tagGCMTime`、`tagGCMInfo`、`_PLC_LABLE`、`_CONFIG_INFO`、`tagProgramCheck`、`tagEthernetInfo`、`tagPLCVERSION`、`_PROG_INFO`、`SoftElemAddrs`、`OSCMonitorElem`、`ScilloScopeCfg`；枚举 `EnumExecuteMode`、`EnumCommandLevel`、`EnumCommunicaterCmd`、`EnumCommunicaterType`、`EnumSimpleCmdType`、`EnumSimpleCommonCmdType`、`EnumPlcRunModel`、`EnumGCMState`、`EnumConfigTypeTag`、`EnumFunCode`、`EnumProgType`（另见 InterfaceMgr 中 `EnumGCMDataType`）。

观察（需反汇编确认，非结论）：dumpbin 显示多个导出共享同一 RVA——`ModemDial/QueryCanLinkStationInfo/QueryCanLinkStationState/SendATCmd/isDialConnect` 同在 0x00003E90，`Unforce/WriteElemValue/ReadFouceElemTable/ProgramValidate` 同在 0x000037A0，`ReadElemValue` 在 0x000037B0。【符号事实（RVA 相同）】+【推断：符合增量链接（/INCREMENTAL）thunk 特征，不代表语义相同；调用前必须逐一反汇编】。

### 3.2 InterfaceMgr 与通信/编译工厂类

`D:\Inovance Control\AutoShop\InterfaceMgr.dll`，x86，2026-07-23 15:33:36 链接，**4534 个导出**（dumpbin `//exports`）。【符号事实】本次按任务书关注类固定字符串检索，确认存在（含虚表 `??_7Xxx@@6B@`，说明是多态类）：

| 类 | 证据（mangled 摘录） |
|---|---|
| `CAIComMgr` | `??0CAIComMgr@@QAE@XZ`、`??1CAIComMgr@@UAE@XZ`（virtual dtor）、`??_7CAIComMgr@@6B@`；`CInterfaceMgr::GetAIComMgr/SetAIComMgr(CAIComMgr*)` |
| `CCommLib` / `CCommLibData` | 构造/虚析构/赋值全套 |
| `CCommunicater` | 构造/虚析构/`??_7CCommunicater@@6B@`；virtual `CheckPassword(EnumCommunicaterCmd, CStringT<wchar> const&, EnumExecuteMode, EnumCommandLevel)`、`ClearHistoryFaultData(CWnd*, ...)`、`ClearWatchCommand(void)`——与 `CCommunicateEth` 静态方法同签名族 |
| `CDataManageCenter` | 构造/虚析构/虚表；被 `CSTCompiler::SetPreDataCenterAndQuery`、`CPrjCompare::CompareTwoProj` 引用 |
| `CH530CompileFactory` | 构造/虚表；virtual `CreateContentParser(void)→CILContentParser*`、`CreateH530CompilerCreater(void)→IH530CompilerCreater*`、`CreateSFCMapHelper(CSFCElemMap*)→CSFCMapHelper*` |
| `CEOECommunicateFactory` | `IEOECommunicate::SetFactory(CEOECommunicateFactory*)`、静态成员 `m_pEOECommunicateFactory` |
| `CH530SysResMgrFactory` | `CInterfaceMgr::GetSysResMgrFactory/SetSysResMgrFactory` |
| `CH530CommunicateFactory` | `IH530Communicate::SetFactory(CH530CommunicateFactory*)`、静态成员 `m_pH530CommunicateFactory` |

同目录还可观测到 `CInterfaceMgr`（门面）、`CArrayParser`、`CCNCParser`、`CGvtGridItem`、`CH530ElemMonItemBase`、`CExpTempVar_Interface`、`CGCMFile_Interface`、`CCrossRefInfoBase`、`CEspecillyItemCfgBase`、`CSTCompiler`、`CPrjCompare`、`CErrorCenter(Mgr)`、`CDataType(EnumGCMDataType, CStringT<wchar>)` 等符号。【符号事实，dumpbin 导出表摘录；仅列举，未逐一展开】

### 3.3 通信栈其他 DLL

| 文件 | 位数/时间戳 | 导出 | 说明 |
|---|---|---|---|
| `Communicater.dll` | x86 / 2026-07-23 15:35:58 | **0** | 与任务书"无导出"一致【符号事实】；实现在 InterfaceMgr/CommunicaterModule |
| `CommunicaterModule.dll` | x86 / 2026-07-23 15:34:45 | 95 | `CCommunicaterLocal` 等，签名沿用同一 `EnumCommunicaterCmd/EnumExecuteMode/EnumCommandLevel` 族【符号事实】 |
| `H530Communicate.dll` | x86 / 2026-07-23 15:36:00 | 0 | 未展开【未复核】 |

【推断】通信栈分三层：`InterfaceMgr`（接口/工厂/数据管理，4534 符号的主库）→ `CommunicateEthernet`（对外静态门面 `CCommunicateEth`）→ `CommunicaterModule/Communicater/H530Communicate`（具体通道实现）。H5U/H3U 走 H530 系列工厂。

---

## 4. 共享变量服务与 ITP

### 4.1 服务与监听

- `InoVarSharedService.exe`：x86，2026-01-20 19:49:07 链接，Windows 服务（自动启动，LocalSystem），运行中 PID 22488，**TCP 127.0.0.1:13939 LISTENING**。【符号事实：dumpbin + Get-CimInstance + netstat】
- 静态导入表仅 `KERNEL32.dll` 与 `ADVAPI32.dll`（无 WS2_32）。【符号事实，dumpbin `//imports`】+【推断：Winsock 动态加载，与 4.3 日志中 `CCommunicateBase::InitWSA` 自行 `WSAStartup` 互证】

### 4.2 ITP 符号（任务书情报，部分运行时互证）

以下符号来自任务书情报（PDB 级），本次未能用 PDB 解析器独立复核——`InoVarSharedService.pdb`（707 KB，随 exe 发布）的符号流为压缩格式，`grep -a` 无法提取明文字符串（已尝试，0 命中）：

- `CPacketHandler::HandlerRegister / HandlerRegAnswer / DelRegisterInfo / IsValidCompletePacket / GetITPConnectVec / RecycleConnectOjbect`（注意 `RecycleConnectOjbect` 拼写）
- `CVarDataMgr::ClearHandle / ClearSocket`
- `CCommunicateBase::InitListen / DoJob`
- 端口机制 `GetPort → GetAvaliablePort → SaveServicePort`（注册表持久化）

**运行时互证【符号事实】**：活动日志 `AutoVarShared\SharedVaribleLog\2026-10-23_1.log` 中直接出现 `CCommunicateBase::InitWSA;180`、`CCommunicateBase::InitListen;120/129/136`、`CLogPlugin::InitLogger;42` 的 `Class::Func;Line` 记录——即 `CCommunicateBase` 类名与方法名不依赖 PDB 也可确认。

注册表核查【符号事实（阴性结果）】：本机 `HKLM\SOFTWARE` 与 `HKLM\SOFTWARE\WOW6432Node` 下未找到名称含 `Ino` 的键（`reg query /f *Ino* /k` 无命中），服务键下无 `Parameters` 子键。因此"端口写注册表"的**键位置未落定**，当前唯一端口证据是 netstat 的 13939 LISTENING。建议代理 B 用 Process Monitor 观察服务启动时的注册表写入，或改动注册表/配置后观察端口变化。

### 4.3 日志格式（实测样例）

`AutoVarShared\SharedVaribleLog\YYYY-MM-DD_N.log`，单行格式：

```
ThreadID=(19016);2026:10:23-21:16:19;(INFO):InitLogger Success;CLogPlugin::InitLogger;42
ThreadID=(19016);2026:10:23-21:16:19;(INFO):WSAStartup Success;CCommunicateBase::InitWSA;180
ThreadID=(19016);2026:10:23-21:16:19;(INFO):CreateIoCompletionPort Success;CCommunicateBase::InitListen;120
ThreadID=(19016);2026:10:23-21:16:19;(INFO):WSASocket Create Success;CCommunicateBase::InitListen;129
```

即 `ThreadID=(TID);YYYY:MM:DD-HH:MM:SS;(LEVEL):Msg;Class::Func;Line`。【符号事实，文件原文摘录】日志中的 `CreateIoCompletionPort` 是 IOCP 服务器模型的直接运行时证据；`InitWSA` 说明 Winsock 自初始化（与 4.1 导入表一致）。

### 4.4 官方测试客户端 TestITPClient.exe

`AutoVarShared\TestITPClient.exe`（260,608 字节）。【符号事实，strings 检索】：

- JSON 字段字符串：`varModify`、`VarSize`、`defaultValue`（各 1–2 次出现）
- jsoncpp  writer 类名：`Json::FastWriter`、`Json::StyledWriter`（"FastWriter"/"StyledWriter" 各 1 次）——佐证 JSON 报文用 jsoncpp 序列化
- 导入表：`KERNEL32.dll`、`MSVCR90D.dll`、`MSVCP90D.dll`、`WS2_32.dll`【符号事实】——Debug 版 VC9（VS2008）x86 运行时，与 Plugin.pdb 中 VS2008 链接器线索互证【推断】

【任务书情报】"JSON 报文（jsoncpp）+ IOCP"中的报文字段语义、握手时序留给代理 B 实证（`docs/inovance-itp-protocol.md`），本文不展开。

---

## 5. AI 助手通道（不作为 MCP 集成路径）

| 资产 | 证据 |
|---|---|
| `ACmd.dll` / `APipe.dll` | 7 导出插件契约实例（第 2.3 节）【符号事实】 |
| `AICom.dll` | 无导出（0 exports）【符号事实】；实现经 InterfaceMgr 符号暴露 |
| `InterfaceMgr::CAIComMgr` | 构造/虚析构/虚表 + `CInterfaceMgr::GetAIComMgr/SetAIComMgr`【符号事实，第 3.2 节】 |
| UDP 65241 | AutoShop.exe（PID 24492）监听 `0.0.0.0:65241`【符号事实，netstat】 |
| `AIAssistantELinkCmd.xml` | 419 行，根元素 `<AIATELINKCMD>`，条目为 `<CMDINFO>` 纯指令名列表：`$ADD`、`$MOV`、`ABS`、`ACOS`、`ADD`、`AND`、`ARY*` 系列、`ASCII码转换` 等【符号事实，文件原文摘录】 |
| `AISkin\*.png` | 聊天皮肤图片资源（AIAnser/AIAnswer/AICode/AIHistoryStatu1 等）【符号事实，目录列举】 |

【推断】XML 是 ST 指令索引（供 AI 聊天补全/检索），UDP 65241 疑似本地 AI 进程间通道；具体报文格式未取证。任务书结论维持：**疑似对接云端大模型、随版本漂移，不作为 MCP 集成路径**。

---

## 6. 风险与限制

1. **无头文件**：所有类型信息只能来自 mangled name 与 PDB；`PluginInfo`/`PluginEnum`/`CPluginBase` 布局、`tagGCMTime`/`_PLC_LABLE`/`_CONFIG_INFO` 等结构大小与成员偏移、各 `Enum*` 的枚举值均未知。【符号事实（缺位）+ 推断（影响）】直接按名调用有错位风险。
2. **版本强绑定**：通信栈 2026-07-23 构建、插件框架 2026-01-20 构建、部分插件 2010 年构建；AutoShop 升级极可能改变 `CCommunicateEth` 静态方法集与 Plugin ABI。【推断】任何垫片/插件必须做版本闸门（PE 时间戳 + 导出集合比对），不匹配即拒绝加载。
3. **x86 进程内插件**：Plugin 框架是进程内 DLL（AutoShop 为 x86/WOW64 进程），第三方 DLL 加载进 IDE 进程，崩溃/句柄泄漏直接拖垮 AutoShop；且调试需 x86 工具链。【推断】
4. **无厂商背书**：以上全部为逆向情报，接口语义（尤其 `EnumExecuteMode`/`EnumCommandLevel` 的取值含义、`CWnd*` 回传的消息机制）未经验证；不得写入对外能力声明。【任务书纪律】与 `docs/contracts.md` 一致：未真机验证一律 Experimental，未实现一律 Unsupported。
5. **写入口敏感**：`Force/Unforce/WriteElemValue/Download/SettingPassword` 等导出存在但**本版不接入**；物理写入口仍禁用（任务书第二节）。
6. **注册表端口机制未落定**：13939 仅为本次观测值，端口可能随运行变化（4.2 节阴性结果）。【符号事实（阴性）】
7. **RVA 别名**：3.1 节所述多导出共享 RVA 现象未反汇编确认，存在"同名不同址/同址不同义"风险。【推断】

---

## 7. 后续验证路线建议

按优先级（均不违反目录所有权；实现归代理 B，本文仅建议）：

1. **PDB 类型重建**：用 DIA（`dia2dump`）或 llvm-pdbutil 解析 `Plugin.pdb` / `InoVarSharedService.pdb`，导出 `PluginInfo`/`PluginEnum`/`CPluginBase`/`tagGCMTime`/`_PLC_LABLE` 等 UDT 布局，生成可编译的还原头文件；这是所有调用路线的前置。Plugin.pdb 为明文字符串可读格式，优先级最高。
2. **x86 垫片 DLL**：以 `Plugin.lib`（随产品发布，第 2.2 节）链接原版 ABI，构建 x86 代理 DLL，仅暴露只读面子集（`ReadElemValue`/`ReadWatchElemValue`/`UploadSysRunLog`/`GetExecMode`/`GetGCMState`/`GetVersionInfo`），写/下载/口令一律不包装；加载前校验 PE 时间戳与导出集合哈希。
3. **ITP 实证优先于垫片**：127.0.0.1:13939 为本地回环 JSON 协议（第 4 节），先按代理 B 的只读探测路线逆向握手与读变量报文；相比进程内 DLL，回环 TCP 的爆炸半径最小。
4. **UIA 备选**：若需工程导出类能力而拒绝进程内注入，用 UI Automation 驱动 AutoShop 窗口（DuiLib 界面），作为不依赖私有 DLL 的退化路线。
5. **端口机制确认**：Process Monitor 过滤 `InoVarSharedService.exe` 的 `RegSetValue`，或对比不同时刻 netstat，落定 `SaveServicePort` 的键路径，供 doctor 检测与端口注入（任务书要求端口可注入）。
6. **反汇编补齐**：对 `CCommunicateEth` 的 RVA 别名组（3.1 节）与 `Enum*` 常量做逐一反汇编；对 `TestITPClient.exe` 做完整字符串/交叉引用还原报文模板。

---

## 8. 证据来源与"事实/推断"标注约定

本文三类标注：

- **【符号事实】**：本次会话亲自执行并复核的一手证据。工具与命令：
  - `dumpbin //headers` / `//exports` / `//imports`（路径见 1.4）——PE 位数、时间戳、导出/导入清单、mangled name；
  - `tasklist /FI`、`netstat -ano`——进程 PID 与端口监听；
  - `Get-CimInstance Win32_Service`、`Get-ItemProperty HKLM\...\Services\InoVarSharedService`、`reg query`——服务注册与注册表（含阴性结果）；
  - PowerShell `VersionInfo`——文件版本与厂商；
  - 目录列举与文件原文摘录（日志、`AIAssistantELinkCmd.xml`、`LDCmdVersion.xml`、`Plugin.pdb` 字符串、`TestITPClient.exe` 字符串）。
- **【任务书情报】**：来自 `docs/inovance-integration-plan.md` 或派发说明，本次未独立复核或无法复核（如压缩 PDB 内的 ITP 符号、dnSpy 路径、`V4.12.0.2` 补丁号）。引用时保留原表述并说明复核障碍。
- **【推断】**：基于上述事实的推理（如 WOW64 归属、通信栈分层、增量链接 thunk、版本闸门必要性）。推断不得当作事实用于能力声明。

**与任务书第一节的一致性**：任务书表格中的每个接口名（`PluginManager::*`、`EventCenter::*`、7 导出契约、`CCommunicateEth` 的 50+ 方法、ITP 类符号、`CAIComMgr`、UDP 65241）均可在本文找到对应证据；其中任务书未展开的细节（完整 33/84 导出清单、`PluginInfo` 构造签名、`.def`/`.lib`/`.exp` 线索、RVA 别名、日志实测行、jsoncpp writer 字符串、注册表阴性结果）为本次新增。未发现与任务书矛盾之处；唯一修正是 2.1 节关于 `ListenEvent/PublishEvent` 访问级别的精确化（private 实现 + public 转发）。

**复核覆盖度**：本文所有 dumpbin 结论（Plugin 33 导出、ACmd/APipe 各 7 导出、CommunicateEthernet 84 导出、InterfaceMgr 4534 导出中 8 个关注类、各文件位数/时间戳、Communicater.dll 零导出）均为本次亲自跑出；未能复核项均已在正文显式标注。
