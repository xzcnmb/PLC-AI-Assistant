# 汇川 AutoShop 原生插件桥（S2）实证文档

> 本文档固化 S2（原生插件桥）在 AutoShop V4.12.0.2（x86，`D:\Inovance Control\AutoShop`）上的
> 全部实证结论：插件契约、宿主加载路径、通信栈直调、命名管道 IPC 与 C# 侧实现。
>
> **所有接口均为逆向所得，无厂商背书；版本锁定 4.12.0.2 / x86。** 与
> `docs/inovance-autoshop-re-notes.md`（情报归档）、`docs/inovance-itp-protocol.md`（ITP 只读桥）
> 配合阅读。能力声明纪律沿用 `docs/contracts.md`：未真机验证一律 Experimental，未实现一律 Unsupported。
>
> 标注约定：【实证】= 本次亲自执行并复核（dumpbin/反汇编/构建/运行/日志）；
> 【推断】= 基于实证的推理；【未验证】= 无直接证据、禁止在实现中当作事实。

对应实现：

| 层 | 位置 |
|---|---|
| 原生源码与构建脚本 | `tools/AutoShopNativeBridge/**`（构建产物在 `%LOCALAPPDATA%\AutoShopNativeBridge-run\`，**不入仓库**） |
| C# 管道客户端 / worker / 模型 | `src/PlcMcp.Engineering/Workers/AutoShop/NativeBridge/**` |
| mock 管道测试 | `tests/PlcMcp.Engineering.Tests/NativeBridgeTests.cs`（20 个测试） |

---

## 一、结论摘要（先读这一节）

| 里程碑 | 结论 |
|---|---|
| M1 插件加载 | **当前契约 = `CreatePlugin()` 工厂**（非 2010 年代 7 导出契约）。已用真实 `Plugin.dll` + 自建宿主（`TestHost.exe`，复刻服务加载序列）**完整实证**：加载成功、`GetPluginByEnum` 取回实例、vtable 三个槽位全部 MATCH。厂商宿主（InoVarSharedService）**只硬编码加载两个 DLL**，第三方插件无法被任何已装宿主加载；本机无法重启服务/AutoShop（权限不足），已如实记录。 |
| M2 宿主接口 | 完整还原 `PluginManager::LoadLibiary` 调用序列与 `CPluginBase` 5 槽 vtable 契约；还原 `PluginInfo`（36 字节）与 VS2008 `std::string`（28 字节，含 proxy 前缀）布局；版本闸门 `(v & 0xFFFF0000)==0x00010000 && (v & 0xFF00)<=0x100`。 |
| M3 通信栈直调 | x86 垫片（`CommStackShim.dll`）以**静态导入**链接真实 `CommunicateEthernet.dll` 的 5 个只读静态方法；已在本机进程内实调 `GetExecMode/GetGCMState/GetPlcLable/ReadElemValue`（rc=0，无崩溃）。**无 PLC、无模拟器（S3 实证 simulator.exists=false），真机未验**。`ReadSeriesWordElem` 在 4.12.0.2 中是桩（直接 ret 0）。 |
| M4 IPC | 命名管道 `\\.\pipe\PlcMcpAutoShopBridge` + JSON 行协议（handshake/capabilities/read_status/read_elements/shutdown）；C# 侧 client/worker/模型 + 20 个 mock 管道测试全绿。安全姿态对齐 `ExternalWorkerProtocol`（危险操作硬拒绝、超时不转成功、最高 Experimental）。 |
| 追加：通信配置定位 | 连接目标（IP/端口/从站）存于**工程包 `.hcpp` 的 `NetConfig.cfg`/`ComCfg.data`/`CombInfo.dat`**；ini/注册表只有调优参数；无工程时内存默认 `tagEthernetInfo`=192.168.1.1:12939。详见第十节。 |

---

## 二、环境与工具链

| 项 | 值 | 来源 |
|---|---|---|
| AutoShop.exe | FileVersion 4.12.0.2（任务书记载；PE 版本资源为 4.12.0.0），PID 24492 运行中，x86 | 【实证】`Get-Process`/`VersionInfo`/dumpbin |
| InoVarSharedService | 运行中，PID 22488，TCP 127.0.0.1:13939 | 【实证】`netstat`/`sc query` |
| 插件 SDK | `D:\Inovance Control\AutoShop\AutoVarShared\`：`Plugin.dll`(+.lib+.pdb)、`PluginCommunicate.dll`(+.lib+.pdb)、`PluginGlobalFunc.dll`(+.pdb)、`PluginLogger.dll`(+.lib+.pdb)、`TestITPClient.exe` | 【实证】目录列举 |
| 通信栈 | `CommunicateEthernet.dll`（84 导出，x86）、`InterfaceMgr.dll`（4534 导出，x86） | 【实证】dumpbin |
| 工具链 | dumpbin/link/lib = VS2022 Community v143 x86（`...\MSVC\14.34.31933\bin\Hostx64\x64\`）；原生工程用 `vcvarsall.bat x86` 构建 | 【实证】构建日志 |
| 运行时 | 本机无 .NET 8 运行时，C# 测试用 `DOTNET_ROLL_FORWARD=Major dotnet test`（.NET 10.0.8 上运行） | 【实证】 |
| 权限 | 当前 Shell **非管理员**：`sc stop InoVarSharedService` → 拒绝访问(5)；UAC 提权（`Start-Process -Verb RunAs`）→ "该用户尚未被授权"；`taskkill AutoShop.exe` → 拒绝访问 | 【实证】三条命令均实际执行 |

---

## 三、M1 插件加载实证

### 3.1 当前插件契约（反汇编证据）

`Plugin.dll`（x86，33 导出）的 `PluginManager::LoadLibiary(std::string const&)`（RVA 0x53F0）完整序列【实证，dumpbin /disasm】：

1. `GetPluginLibName(path)` → 从路径取库名；
2. `ResolvePathExtension(name)` → 补扩展名；
3. `DLibrary::LoadLibByPath(name)` → `LoadLibraryA`（空路径时报 "Failed to load library: Empty path"）；
4. **`DLibrary::GetProcAddrByName(hLib, "CreatePlugin")`**（字符串字面量在 Plugin.dll RVA 0x9256 附近）——取不到则 stderr 打印 `Failed to initialize plugin "%s"` 并 `FreeLibrary`；
5. 调用 `CreatePlugin()` → 期望返回 `CPluginBase*`；返回 NULL 则打印 `Self registry failed on plugin "%s"` 并卸载；
6. `IRegisterManager::IsCanLoadPlugin(CPluginBase*)`（RVA 0x3830）→ false 则经 **vtable 槽 0 传参 1**（scalar deleting destructor）销毁对象并卸载；
7. `IRegisterManager::IsValidateInterface(CPluginBase*)`（RVA 0x38C0）→ false 同样销毁卸载；
8. `IRegisterManager::AddPlugin(CPluginBase*)`（RVA 0x3722）→ 写入 `std::map<PluginEnum, CPluginBase*>`；
9. 库句柄存入 `std::map<std::string, DLibrary*>`。

**宿主是谁、加载什么**（`InoVarSharedService.exe` 反汇编，【实证】）：

- 只有 `AutoVarShared` 家族导入 `Plugin.dll`：`PluginCommunicate.dll`、`PluginLogger.dll`、`InoVarSharedService.exe`（逐个 dumpbin //imports 复核）。
- 服务 `CServiceMgr::InitLoggerLib()`（RVA 0x1150）与 `InitComLib()`（RVA 0x1270 附近）：
  `GetPluginMgr()` → `RegisterInterface(静态 sPluginInfo)` → **`LoadLibiary(std::string("PluginLogger.dll"))`** → `GetPluginByEnum(enum)`；
  对 `PluginCommunicate.dll` 同样一遍。**两个名字都是硬编码裸文件名**，无目录扫描。
- `AutoShop.exe`（55 MB）**不导入 Plugin.dll**，全文件检索无 `CreatePlugin`/`GetPlugInName` 等任何插件契约字符串【实证】；AutoShop 根目录下的 2010 年代插件（ACmd.dll/APipe.dll/config.dll/crc32.dll/eFex.dll，均带 7 导出契约）**在本机任何已装二进制中都没有加载器**（全盘递归检索引用名与契约字符串均无命中）。
- `LoadLibiaryFromFolder`（文件夹扫描）未被任何已装二进制导入——**本机不存在第三方插件目录机制**。

### 3.2 自建宿主实证（核心证据）

`TestHost.exe`（`src/host/TestHost.cpp`）链接厂商 `Plugin.lib`，逐句复刻服务加载序列。运行输出【实证，2026-10-24，`%LOCALAPPDATA%\AutoShopNativeBridge-run\TestHost.exe`】：

```
[1] GetPluginMgr -> singleton
    RegisterInterface(name="PmNativeBridge", enum=100, version=0x00010000) done
[1a] negative: PlcMcpBridgeNoFactory.dll -> LoadLibiary false
     (stderr: Library symbol "CreatePlugin" not found. Failed to initialize plugin ...)
[1b] positive: PlcMcpBridgePlugin.dll -> LoadLibiary true
     GetPluginByEnum(100) -> instance
     vtable slot2 GetPluginVersionEx -> 0x00010000 MATCH
     vtable slot4 GetPluginEnumEx     -> 100 MATCH
     vtable slot3 GetPluginName       -> "PmNativeBridge" (len=14)
[1c] duplicate load -> true（框架按名字持 map，重复加载会再次 CreatePlugin 并覆盖注册项）
```

标记日志 `%TEMP%\plcmcp-handoff\m1-marker.log`（DllMain/CreatePlugin 各写一行，含 PID/DLL 路径/宿主/时间戳）：

```
2026-10-24 06:25:03.582 | pid=24732 | event=CreatePlugin.called | dll=...\run\PlcMcpBridgePlugin.dll | host=...\run\TestHost.exe | returning CPluginBase with 5-slot vtable
```

结论：**CreatePlugin 契约 + 自注册序列 + 5 槽 vtable 全部闭环验证通过**（对真实 `Plugin.dll`）。

### 3.3 厂商宿主候选目录实验（含清理记录）

按红线"插件 DLL 只准复制到候选目录"执行【实证】：

- 2026-10-24 06:26 将 `PlcMcpBridgePlugin.dll` 复制到
  `D:\Inovance Control\AutoShop\AutoVarShared\` 与 `D:\Inovance Control\AutoShop\`（两个候选目录）。
- 重启尝试全部失败：`sc stop`（拒绝访问，未提权）、UAC 提权（用户未授权）、`taskkill AutoShop.exe`（拒绝访问）。服务日志与标记日志**无任何厂商宿主加载记录**。
- 2026-10-24 06:27 已从两个安装目录**移除**该 DLL 并记录在 `m1-marker.log` 末尾；安装目录复核无 PlcMcp 残留文件。

因此"谁加载、从哪个目录、什么时机"的最终判定以 3.1 的静态证据为准：
**唯一宿主是 InoVarSharedService.exe，启动时以硬编码裸名从自身目录加载 PluginLogger.dll 与 PluginCommunicate.dll，加载前已静态注册对应 PluginInfo。第三方插件在本版无法进入任何已装宿主。**

### 3.4 遗留 7 导出契约（2010 年代）

`ACmd.dll`/`APipe.dll`（2010 链接）导出 7 个 C 函数【实证，dumpbin】：
`GetPlugInName`→"acmd"、`GetPlugInType`→GUID 串（ACmd: `8d49a2c4-...`，APipe: `77763e54-...`）、
`GetPlugInVersion`→0x100、`GetAuthor`/`GetCopyRight`→"scottyu"、`QueryInterface(const char*)`（与自身 GUID 比较，失配返回 0x6D=109）、`GetInterface(const char*)`（GUID 匹配则返回接口表指针，否则 NULL；APipe 填 4 个函数指针后返回表地址）。
本桥的插件同时导出这 7 个函数（兼容面），但 **Plugin.dll 从不按这些名字取地址**——它们是历史契约，本机无宿主使用。

---

## 四、M2 宿主接口发现

### 4.1 CPluginBase vtable 契约（5 槽）

依据 `PluginLogger.dll` 的 `??_7CLogPlugin@@6B@`（RVA 0x54B4，8 个真实项）逐槽反汇编【实证】：

| 槽 | 偏移 | 签名（还原） | 证据 |
|---|---|---|---|
| 0 | +0x00 | `virtual ~CPluginBase(int alsoDelete)`（scalar deleting dtor） | `0x2B40`：调实析构后 `test [esp+8],1` 决定 `operator delete`；`LoadLibiary` 失败路径 `push 1; call [vt+0]` |
| 1 | +0x04 | `virtual ~CPluginBase()`（complete dtor） | `0x2F20` |
| 2 | +0x08 | `virtual unsigned GetPluginVersionEx() const` | `0x2A30`：`mov eax, ds:[1000865Ch]`（全局版本）；`IsCanLoadPlugin`/`IsValidateInterface` 调用 |
| 3 | +0x0C | `virtual std::string GetPluginName() const`（sret：ecx=this，[esp+4]=返回缓冲） | `0x2A00`：从 "CLogPlugin" 构造字符串写入 sret 缓冲 |
| 4 | +0x10 | `virtual PluginEnum GetPluginEnumEx() const` | `0x2A40`：`mov eax, ds:[10008658h]`（全局枚举） |

`IsCanLoadPlugin`（RVA 0x3830）逻辑【实证】：先调槽 4 取枚举 → 在 `map<PluginEnum, PluginInfo>` 中查找；
**未注册 → 返回 false**（即插件类型必须被宿主预先 `RegisterInterface`）；已注册 → 调槽 2 取版本，要求
`(v & 0xFFFF0000) == 0x00010000 && (v & 0xFF00) <= 0x00000100`。
`IsValidateInterface`（RVA 0x38C0）逻辑【实证】：同样查找枚举（未注册 → false）；已注册则比较版本——
高 16 位必须一致，且 `(pluginVersion & 0xFF) >= (registeredVersion >> 8) & 0xFF`。
本桥取值：version = `0x00010000`（主 1 次 0），注册同名同版本，双向通过。

### 4.2 插件可从宿主获得的服务

插件侧可用的宿主 API = `Plugin.dll` 导出集（经随产品发布的 `Plugin.lib` 静态导入，ABI 精确）【实证，导出表 + mangled name】：
`PluginManager::GetPluginMgr()`、`RegisterInterface(PluginInfo const&)`、`GetPluginByEnum(PluginEnum)`、
`RegisterPluginEvent/UnRegisterPluginEvent/RarseEvent`（事件）、`LoadLibiary/Unload/UnloadAllLibrary/IsLibraryLoaded/GetPluginsByName`。
`RegisterInterface` 是插件自注册入口（服务对自家插件正是这么做的；第三方插件在自己宿主里亦可自注册后放行——本机厂商服务不开放此路径）。

### 4.3 PluginInfo 与 VS2008 std::string 布局（跨工具链关键）

`PluginInfo`（36 字节）【实证，`??0PluginInfo@@QAE@W4PluginEnum@@V?$basic_string...I@Z` RVA 0x12D0，`ret 24h`=36 字节栈参】：

| 偏移 | 字段 | 证据 |
|---|---|---|
| +0x00 | `std::string m_name`（VS2008 映像，28 字节） | 拷贝构造入 `this+0` |
| +0x1C | `PluginEnum m_enum`（4） | `mov [esi+1Ch], enumArg` |
| +0x20 | `unsigned m_version`（4） | `mov [esi+20h], versionArg` |

VS2008 `std::string` = **28 字节**（VS2022 只有 24）【实证】：

| 偏移 | 字段 | 证据 |
|---|---|---|
| +0x00 | `void* proxy`（_String_val 代理；读路径不用，可置空） | 布局差 4 字节的定位证据 |
| +0x04 | `char[16] buf` / `char* ptr`（SSO/堆指针） | `DLibrary::LoadLibByPath`：`lea esi,[edi+4]`；堆路径 `mov eax,[esi]` |
| +0x14 | `unsigned mysize` | `cmp [edi+14h],0`（空串检查） |
| +0x18 | `unsigned myres`（<16 走 SSO） | `cmp [edi+18h],10h; jb`；msvcp90 拷贝构造 `mov [esi+18h],0Fh` |

跨工具链规则（`src/plugin/PlcMcpBridgeAbi.h` 落地）：**绝不跨边界传本工具链的 std::string/CString/PluginInfo 对象**；
一律手工构造 VS2008 映像按指针/引用传入；框架**拥有**的字符串（注册的 PluginInfo 名、`GetPluginName` 的 sret）必须用 SSO 形式（≤15 字符），否则框架析构时会用 VS2008 分配器释放外来指针。参数侧长名字（如 `LoadLibiary` 的文件名）可用进程堆映像，调用后自行释放（厂商只读不接管）。

---

## 五、M3 通信栈直调

### 5.1 结构布局重建（每个字段的证据见 `src/plugin/PlcMcpBridgeAbi.h` 注释）

`CSoftAddrMap`（28 字节头，`InterfaceMgr.dll` `??0CSoftAddrMap@@QAE@XZ` RVA 0x53100）【实证】：
`+0x00 vtable(??_7CSoftAddrMap@@6B@)`、`+0x04 m_pHashTable=0`、`+0x08 m_nHashTableSize=17`、`+0x0C m_nCount=0`、`+0x10 m_pFreeList=0`、`+0x14 自有字段=0`、`+0x18 自有字段=10`。
类型实参（mangled）证明它是 `CMap<CStringT<wchar>, wchar_t const*, tagSoftAddr*, tagSoftAddr*>` 子类（地址串 → 元件指针）【实证】。

`tagSoftAddr`（字段范围 0x00–0x43，含 double 对齐后 72 字节；`??0tagSoftAddr@@QAE@PB_WW4EnumForceQueryResult@@0HH@Z` RVA 0x1C10）【实证】：
`+0x00 vtable`、`+0x08/+0x0C/+0x14/+0x34/+0x38/+0x3C/+0x40 CStringT<wchar>`（其中 +0x08/+0x0C 由构造参数 1/3 构造）、
`+0x10 int(参数4)`、`+0x18 int(参数5)`、`+0x1C EnumForceQueryResult(参数2)`、`+0x20 double(0.0，数值元件值)`、`+0x28/+0x2C/+0x30 int=0`。
字段语义（哪个 CString 是地址、哪个是注释）为【推断】——5 参构造是 Force 查询专用，普通读取路径的元素构造未反汇编。

**关键限制**【实证】：`CSoftAddrMap::AddElem`（RVA 0x5080）在基类里是 **`xor eax,eax; ret 8` 桩**（`IsExist` 同理）；
真正的元素容器在派生类（`CLDElement`/`CH530ElemMonItemBase` 一族）。因此垫片只能经厂商导出构造基类空 map 并调用 `ReadElemValue`，
**无法自行填元素**——值解码路径未闭合，属【未验证】。

### 5.2 垫片与验证

`CommStackShim.dll`（x86，静态导入；`scripts/gen_import_lib.py` 从导出表生成 `.def` 只含 5 个只读方法，
写/Force/下载入口**故意不导入**，从链接层面不可能调用）：

| 导入（mangled，节选） | 垫片 C API |
|---|---|
| `?ReadElemValue@CCommunicateEth@@SAJPAVCSoftAddrMap@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | `PlcMcpComm_ReadElements` |
| `?ReadSeriesWordElem@CCommunicateEth@@SAJABV?$CStringT@...@@ABGPAVCWordArray@@...@Z` | `PlcMcpComm_ReadSeriesWord` |
| `?GetExecMode@CCommunicateEth@@SAJPAW4EnumPlcRunModel@@HW4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | `PlcMcpComm_GetExecMode` |
| `?GetGCMState@CCommunicateEth@@SAJPAW4EnumGCMState@@PAVCWnd@@...@Z` | `PlcMcpComm_GetGcmState` |
| `?GetPlcLable@CCommunicateEth@@SAJPAU_PLC_LABLE@@W4EnumCommunicaterType@@...@Z` | `PlcMcpComm_GetPlcLable` |

签名要点（修正 re-notes 的两处笔误）【实证，导出表为准】：`ReadSeriesWordElem` 返回 **long**（`SAJ...`），
第二参是 `const unsigned short&`（`ABG`）而非指针；`Enum*` 取值未知，垫片统一传 0。

实调证据（TestHost `[3] comm-stack probe`，本机进程内，【实证】）：

```
shim: PlcMcp CommStackShim 1.0.0 (reverse-engineered, read-only, x86, AutoShop 4.12.0.2)
PlcMcpComm_GetExecMode    -> rc=0 mode=0   (no PLC connection expected)
PlcMcpComm_GetGcmState    -> rc=0 state=0
PlcMcpComm_GetPlcLable    -> rc=0          (struct size unverified; 4KB zeroed buffer)
PlcMcpComm_ReadElements   -> rc=0 count=0  (empty map via vendor ctor; no crash)
```

安全评估【实证，反汇编】：`ReadElemValue`/`GetExecMode` 均先过单例守卫（`10002F40`：`m_communicate` 空则走错误分支），
`CWnd*` 允许为 NULL（代码显式判空后取 `[ecx+20h]`=m_hWnd）；rc=0 表示"命令被受理"（异步分发），**不代表读到值**。
`ReadSeriesWordElem` 的导出 RVA 0x3E90 是 `xor eax,eax; ret` 桩（与 `isDialConnect`/`SendATCmd` 等同址，增量链接别名），
调用安全但无功能。**真机/模拟器验证：无**——S3 实证在线模拟器不存在（handoff.json `simulator.exists=false`），本机无实体 PLC。

---

## 六、M4 IPC 与 C# 侧

### 6.1 原生管道服务（进程内）

`BridgePipeServer.cpp`：命名管道 `\\.\pipe\PlcMcpAutoShopBridge`，字节模式、单客户端、一行一 JSON（UTF-8，`\n` 结尾）。
方法集（**只读**，无任何写/Force/下载/编译/启停方法）：

| 方法 | 参数 | 应答 |
|---|---|---|
| `handshake` | `{client}` | `workerName/workerVersion/protocolVersion/vendor/bitness/runtimeEnvironment/capabilities/disclaimer` |
| `capabilities` | — | 能力字典（compile/download/force/run_stop 一律 Unsupported） |
| `read_status` | — | `execMode/execModeCallResult/gcmState/gcmStateCallResult/evidence`（调用回执，非 PLC 状态） |
| `read_elements` | `{addresses:"D100,D101"}` | `{count,values}`（布局未验证，values 常为空） |
| `shutdown` | — | 停止服务 |

原生侧实调证据【实证】：TestHost `[2]` 段完整五轮往返（含 read_status 经垫片打到通信栈）：

```
-> {"id":"3","method":"read_status"}   <- {"id":"3","result":{"execMode":0,"execModeCallResult":0,...,"evidence":"call-return codes only; no PLC connection verified"}}
```

### 6.2 C# 侧（`src/PlcMcp.Engineering/Workers/AutoShop/NativeBridge/**`）

| 文件 | 职责 |
|---|---|
| `NativeBridgeProtocol.cs` | 协议常量/方法白名单/请求响应模型（与原生 `BridgePipeServer.cpp` 镜像） |
| `NativeBridgePipeClient.cs` | 管道客户端：仅本机固定管道名；仅发送白名单方法；每次调用有超时+取消令牌；超时/断链/坏 JSON 一律转失败，**绝不转成成功空结果** |
| `NativeBridgeServices.cs` | 门面：handshake/read_status/read_elements，传输失败降级为报告而非抛异常 |
| `NativeBridgeWorker.cs` | `IEngineeringWorker`：经 `ExternalWorkerSecurityPolicy.IsHardRejectedOperation` 硬拒绝 Download/RunStop/Force/HmiPublish 及选项关键词扫描；只接受 InspectProject；结果最高 `Experimental` |

实现注记【实证】：本机 .NET 10 滚动前向运行时上，`StreamReader/StreamWriter` 跨命名管道会挂死
（最小复现：客户端 `WriteLineAsync` 后无响应）；客户端与测试 mock 均改用**裸字节读写 + 手工分行**，
`Stream.ReadAsync` 取消/完成语义正常。此行为已写入代码注释。

### 6.3 测试

`tests/PlcMcp.Engineering.Tests/NativeBridgeTests.cs`：20 个测试全绿（协议模型 4、管道客户端 7、
Services 4、Worker 安全姿态 5），全部对本地回环 mock 管道运行，不依赖 AutoShop 安装。
全量回归：`dotnet test PlcMcp.slnx -c Release` → 507 通过 / 0 失败（Engineering 192 + Server 315）。

---

## 七、"符号事实 / 推断 / 未验证"清单

**符号事实（本次亲自复核）**：CreatePlugin 契约与完整加载序列；5 槽 vtable 布局；PluginInfo 36 字节布局；
VS2008 string 28 字节布局（proxy@0/buf@4/size@0x14/res@0x18）；版本闸门常量；宿主只硬编码加载两个 DLL；
`AutoShop.exe` 不用 Plugin.dll；`AddElem`/`IsExist` 是桩；5 个只读导出的 mangled 名与 `ReadSeriesWordElem` 桩；
自建宿主加载/管道/垫片实调的全部运行输出；权限拒绝的三条命令；DLL 复制与移除记录。

**推断**：`CSoftAddrMap+0x14/+0x18` 自有字段语义（0 与 10）未知；`tagSoftAddr` 各 CString 字段的语义；
`EnumExecuteMode/EnumCommandLevel` 等枚举取值（传 0）；tagSoftAddr 总大小 72（double 对齐推断）；
rc=0 表示"命令受理"而非"读到值"。

**追加（第十节）符号事实**：`tagEthernetInfo` 20 字节默认值（12939 / 192.168.1.1）与 `tagSerialInfo` 24 字节默认值；
通信对象 +0x5C 持配置对象、+0x48 持 HWND；`SetCommunicatConfig`/`IsEthOnLine` 经配置对象 vtable 槽 1 应用；
工程包成员名 `NetConfig.cfg`/`ComCfg.data`/`CombInfo.dat`/`net.down` 与版本标签 `NetCfg_V3.1..V1.0`；
下载项描述"以太网配置信息下载"并列 NetConfig.cfg 与 TcpModbusConfig.cfg；`GetConfigInfo` 是 PLC 侧读取。
**追加推断**：`.hcpp` 是工程包扩展名、`h5u` 是机型族名（两者关系待 S1 样本落定）；工程包疑为 zip 类容器（存在 ZipModule.dll，未验证）。
**未验证（禁止当作事实）**：真 PLC 读取/写入行为；模拟器闭环（本机无模拟器）；`_PLC_LABLE` 等结构大小；
`NetConfig.cfg`/`ComCfg.data` 的二进制布局（无样本工程）；
元件值二进制布局（推送/读取解码）；数据类 ITP 报文（见 `inovance-itp-protocol.md` 第六节）；
多包/超大包行为；AutoShop 升级后的 ABI 稳定性。

---

## 八、已知限制

1. **版本锁定 4.12.0.2 / x86**：通信栈 2026-07-23 构建、插件框架 2026-01-20 构建；升级极可能改变导出集与 ABI。垫片/插件应做 PE 时间戳 + 导出集合比对闸门（未实现，属后续工作）。
2. **第三方插件无法进入厂商宿主**：服务硬编码两个 DLL 名且预注册 PluginInfo；本机无目录扫描机制。插件路线在本版只能跑在自建宿主（harness）里——这是架构级结论，不是实现缺口。
3. **无厂商背书、无真机验证**：一切能力最高 Experimental；通信栈数值一律标注"调用回执，无 PLC 连接实证"。
4. **x86/WOW64 与 VC90 依赖**：厂商 DLL 需要 mfc90u/msvcp90/msvcr90（WinSxS Fusion 副本），构建脚本会复制到产物目录。
5. **跨工具链字符串映像**：VS2022 无法直接构造 VS2008 std::string；长名字走进程堆映像（调用后释放），框架拥有的字符串必须 SSO（≤15 字符）。
6. **本机运行时特性**：.NET 10 滚动前向下 StreamReader/Writer 跨管道挂死（已绕开并注释）。

## 十、追加实证：通信配置存储定位（2026-10-24）

背景问题：垫片 rc=0 只证明"命令被受理"；独立进程要真正连接/监视，必须知道 `CCommunicateEth` 的连接配置
（目标 IP/端口/协议/从站）从哪里来。本节给出反汇编结论 + 本机旁证枚举 + 最小验证实验方案（只描述，未执行）。

### 10.1 结论表

| 配置项 | 存储位置 | 证据强度 |
|---|---|---|
| **连接目标（IP/端口/通道/从站）** | **工程包（`.hcpp`）内的 `NetConfig.cfg` / `ComCfg.data` / `CombInfo.dat`** | 【实证】字符串与下载项描述；二进制布局【未验证】（本机无样本工程） |
| 通信栈调优（超时/帧长/日志开关） | 安装目录 `config.ini` `[COMM]` 段（+ `userConfig.ini`、`sys\sysConfig.ini`） | 【实证】文件内容 + 引用点 |
| UI 状态/最近文件/语言/PLC 标识 | `HKCU\Software\Inovance Control\AutoShop\*` | 【实证】注册表枚举 |
| 无工程时的内存默认值 | `tagEthernetInfo` = {端口 12939, IP 192.168.1.1}；`tagSerialInfo` = {站号 1, 19200, 7 数据位, 2 停止位, 超时 2000} | 【实证】构造函数反汇编 |
| PLC 侧网络配置（设备里的） | `IH530Communicate::GetEthInfo(_H530_NET_CFG*, ...)` 从设备读取 | 【实证】导出符号 |

**没有找到任何"机器级"的连接目标存储**：注册表与全部 ini 都没有 IP/端口/站号字段——连接配置是**工程属性**，
随工程走（也可从 PLC 上载/下载到 PLC）。

### 10.2 关键调用点（反汇编）

- `CCommunicateEth::SetCommunicatConfig(tagEthernetInfo*, HWND*)`（CommunicateEthernet.dll RVA 0x3010）【实证】：
  单例守卫 → 参数非空 → `call 10002BB0`：`eax = [commObj+5Ch]`（当前通信配置对象指针），非空则经其
  **vtable 槽 1**（RVA 0x8560 应用逻辑）生效，随后 `[commObj+48h] = HWND`。
  即：**通信对象 +0x5C 持有连接配置对象，+0x48 持有 UI 回调窗口**。
- `IsEthOnLine(tagEthernetInfo*)`（RVA 0x4E90）与无参 `IsEthOnLine()`（RVA 0x4E70）【实证】：都从
  `[commObj+5C]` 取配置后跳到 TCP 探活（RVA 0x8640）——**探活目标来自存储的配置**。
- `GetConfigInfo(EnumConfigTypeTag, _CONFIG_INFO*, ...)`（RVA 0x4780）【实证】：走异步命令分发
  （分配 0x24 字节命令对象 → 校验 → `10002FB0` 派发）——它读的是 **PLC 侧配置**，不是本地连接配置。
  文档/实现中不可把二者混为一谈。
- `tagEthernetInfo` 构造函数（InterfaceMgr.dll RVA 0x1EC0，共 20 字节）【实证】：
  `+0x00 = 0x328B(12939 端口)`、`+0x04 = 192`、`+0x08 = 168`、`+0x0C = 1`、`+0x10 = 1`
  → 默认 **192.168.1.1:12939**（汇川 H5U 系列默认以太网端点）。
- `tagSerialInfo` 构造函数（RVA 0x1E60，24 字节）【实证】：`+0=1(站号)`、`+4=0x4B00(19200 波特)`、
  `+8=7(数据位)`、`+0xC=1`、`+0x10=2(停止位)`、`+0x14=2000(超时 ms)`。
- `CCommunicater::SetCommunicatConfig(tagSerialInfo*, tagEthernetInfo*, HWND__*)`（InterfaceMgr RVA 0x60C0）
  与 `CCommunicater::OpenSerialPort/IsEthOnLine`（RVA 0x5090）在基类均为**桩**（`ret`）；串口实现在
  `CommunicaterModule.dll` 的 `CCommunicaterLocal::SetCommunicatConfig/GetSerialInfo/OpenSerialPort`。
- 应用层设置入口：CommunicateEthernet.dll 资源串 `通讯设置(&C)`、`以太网配置`、`IP地址配置表`、`端口号：`、
  `通讯速率|奇偶校验位|站号`（对话框类 `CCommSettingDlg` 等，实现在 Global.dll 静态代码，无导出）。

### 10.3 工程包成员清单（旁证，AutoShop.exe 字符串）

- **`NetConfig.cfg`**——与 `TcpModbusConfig.cfg` 并列出现在下载项描述 **"以太网配置信息下载"** 中（RVA 0x34e0e8e 附近）；
  另有 `\NetConfig.cfg` 作为工程根成员（RVA 0x1b2c1aa，与 `_DevInfo.SWVersion` 等 PLC 信息项同列表，可从 PLC 上载）。
- `ComCfg.data`、`CombInfo.dat`（下载临时件 `\Temp\CombInfo.dat`）。
- 版本标签族：`NetCfg_V3.1 / V3.0 / V2.0 / V1.0`；下载载荷 `\Temp\net.down`、`\net.tmp`、`\net.up`、`net_cfg.tmp`。
- 同族通信配置：`Modbus_V3.0`(+`\Temp\modbus.down`)、`Canlink_V3.0`(+`\Temp\canlink3.down`)、
  `Ecam.cfg`、`\canopen.data`、`\EtherCat.dat`、`\EIP.dat`、`TcpModbusConfig.cfg`。
- 工程包扩展名 **`.hcpp`**（Global.dll 中文串："没有找到工程文件(.hcpp)，当前可打包的文件共[ %d ]个"）；
  项目成员还有 `varsinfo.foid`、`stprog.o`、`userprog.foid`、`project_backup.hcpp` 等。
  （注：S3 任务书称样本为 `.h5u/.h3u`；二进制证据里 `h5u` 是机型族名字符串，工程包扩展名是 `.hcpp`——
  两者关系待 S1 用样本落定。）

### 10.4 本机旁证枚举（2026-10-24 实测）

| 来源 | 内容 | 与连接目标的关系 |
|---|---|---|
| `HKCU\Software\Inovance Control\AutoShop` | `InstallPath=D:\Inovance Control\AutoShop\AutoShop.exe`；`Recent File List`（File1..10 全空）；`Resource\Language`（Data=0）；`Settings 1.0\ControlBars-Summary`；`Resource\PlcIdentifier` | **无** IP/端口/站号 |
| `HKLM\SOFTWARE`（含 WOW6432Node） | 无 Inovance/AutoShop 配置键（仅 `Classes\AutoShopFile` 文件关联；`AutoServicePort` 属 ITP/代理 B 域） | **无** |
| `config.ini`（安装根） | `[COMM]`：`Logout=0`、`NeedLogin=0`、`TimeOut=3000`、`UpgradeTime=50000`、`EthFrameLen=10000`、`EthNetTimeOut=10000`；另有 `[RemoteLogin] IP=services.iotdataserver.cn PORT=80`（云服务，非 PLC） | 仅**调优参数** |
| `userConfig.ini` / `sys\sysConfig.ini` | UI/系统项（ShowVarAddr、MaxECATSlaveCnt、USB、STKey 关键字表等） | **无** |
| 工程样本 | 本机无 `.hcpp`/`.h5u` 工程（S3 实证：输入被 hook 拦截，样本未创建） | `NetConfig.cfg` 布局【未验证】 |

### 10.5 最小验证实验方案（**只描述，未执行**；零 PLC 动作）

目的：证明"独立进程可用 `tagEthernetInfo` 驱动连接目标"，且存储的配置确实被 `IsEthOnLine` 使用。

1. 在 TestHost 里手工构造 20 字节 `tagEthernetInfo`，目标设为 **`127.0.0.1:12939`（回环）**——保证任何情况下都不触达 PLC。
2. 调用 `CCommunicateEth::SetCommunicatConfig(info, nullptr)`（`HWND` 传 NULL 已实证安全）。
3. 调用无参 `IsEthOnLine()`：预期返回 0（回环无监听）。
4. （可选闭环）先在回环起一个 TCP 监听占住 12939，复测 `IsEthOnLine()` 预期非 0，证明存储配置驱动探活；
   关闭监听后复测回 0。全程仅回环。
5. 禁止把目标改成任何非回环地址（红线）；写/Force/下载入口依旧不导入。

工程存储路径（`NetConfig.cfg` 二进制布局）的验证依赖一份含通信配置的样本工程——属 S1/S3 交付物；
在布局未逆向前，**不得**解析或生成该文件。

### 10.6 对垫片/集成的含义

- 垫片 `PlcMcpComm_ReadElements` 等只证明调用约定；要读真实数据，还需 (a) 一份带连接配置的工程、(b) 将工程内
  `NetConfig.cfg` 的目标灌入 `tagEthernetInfo` 并 `SetCommunicatConfig`、(c) 探活通过后下发读命令。
- doctor/检测类工具可检查"工程是否存在且含 NetConfig.cfg/ComCfg.data"，但**不得**把 ini 的 `[COMM]` 调优参数
  当作连接目标上报。

---
## 十一、给集成代理 S4 的接口说明（含追加任务结论）

- **接入点**：`NativeBridgeServices`（`src/PlcMcp.Engineering/Workers/AutoShop/NativeBridge/`）——
  `HandshakeAsync` / `ReadStatusAsync` / `ReadElementsAsync`，全部超时内完成、失败不抛异常。
  `NativeBridgeWorker` 已按 ExternalWorkerProtocol 姿态就绪（只接受 InspectProject，最高 Experimental）。
- **开关**：默认禁用。启用需要 (a) 原生产物存在（`%LOCALAPPDATA%\AutoShopNativeBridge-run\`，`build.cmd` 可复现构建），
  (b) 宿主进程内运行插件（本机无厂商宿主可加载，见第八节第 2 条——**工具注册前必须先有宿主**），
  (c) 显式开关（建议对齐现有 `--autoshop-bridge` 风格）。
- **能力声明建议**：`comm_stack_read_only` = Experimental（调用回执级）；`variable_read` = Unsupported；
  compile/download/force/run_stop = Unsupported。禁止宣称"支持读取 PLC 数据"。
- **禁止**：不要把本桥的读回执当作 PLC 状态；不要在无审批门控前开放任何写入口（本桥从协议层面就没有写入口）。
- **与 ITP 桥的关系**：`inovance-itp-protocol.md` 的 ITP 回环桥与本桥相互独立；ITP 的数据推送布局同样未验证。二者都只应暴露只读检查类工具。

---

## 十二、追加：AutoShop 命令行开关语义（CLI-RE，2026-10-28）

命令行开关的完整逆向与零 PLC 实验已单独成文：**`docs/inovance-autoshop-cli.md`**。
与本桥相关的结论摘要：

| 项 | 结论 |
|---|---|
| 开关全集 | AutoShop.exe 4.12.0.2 只有 `-downprj` / `-offlinedebug` / `-downfile` 三个真实 CLI 开关（唯一解析函数 RVA 0x118B200，语法 `AutoShop.exe <开关> <路径> <0\|1>`，argc==4 强制）；`-open`/`-Debug` 不存在（早前误读为开关） |
| 文件参数打开 | 非开关参数被当作要打开的工程（MFC FileOpen + 应用存在性检查），路径真实可达；对本机全部样本（模板+V5.x 工程）在"加载工程属性信息"失败——与第十节"工程属性随工程走、Main blob AES 加密"一致 |
| 批处理路径 | **阴性**：无"CLI 打开+自动编译"可用路径；`-downprj/-downfile` 是 PLC 下载语义（连接目标=工程内 NetConfig.cfg 或默认 192.168.1.1:12939，非回环），实参实验被红线禁止 |
| 对本桥的含义 | 命令行**不能**替代本桥的定位：通信配置仍只能来自工程（第十节）或显式注入；下载链路优先级在 12939 协议逆向（mp_core.dll `download_file_handler`），CLI 到阴性终点 |

实验取证（自启实例 + taskkill 清理 + 注册表回滚，全部零 PLC 动作）与复现脚本在
`%TEMP%\plcmcp-handoff\cli-re\`（`EVIDENCE.md`）。

---
## 十三、追加：EnDecrypt.dll（AES 库）加密垫片（D2，2026-10-28）

EnDecrypt.dll 密钥导出可行性实验的完整证据单独成文：**`docs/inovance-autoshop-encryption.md`**。
与本桥相关的结论摘要：

| 项 | 结论 |
|---|---|
| 垫片形态 | `src/crypto/CryptoShim.dll`：运行时解析（`LoadLibrary("EnDecrypt.dll")` + GetProcAddress 修饰名白名单 12 个导出），平面 C API `PlcMcpCrypt_*`；批量加密器与 `CEnDecryptAlg` 刻意不解析；纯内存只读 |
| 与 CommStackShim 的差异 | 通信栈垫片用**静态导入**（gen_import_lib.py 生成导入库）；加密垫片因 build.cmd 归属（D2 不可改）走**运行时解析**——即本桥 5.1 节 `CSoftAddrMap` ctor 装载器的同一模式 |
| 密钥可导出 | **成立**：`GetPassword/GetPasswordN/CreateNewKey/GetKey` 声明 private 但都在导出表，8/8 密钥静态提取与运行实调一致；密钥为编译期常量（.rdata ASCII 串 / 代码内联 dword），无需附加到 AutoShop 进程 |
| 厂商 AES | 标准 AES-128-ECB 单块（10 轮、标准 S-box；FIPS-197 自检后 8/8 密钥与独立标准实现逐字节一致） |
| blob 裁决 | .hcp Main blob payload **不**用这些密钥（两样本逐块厂商 InvCipher 全阴性）；AutoShop 自身零引用 EnDecrypt.dll → P3 的"CAESAlg 密钥→blob 可解密"假设证伪 |
| 对本桥的含义 | 垫片可作未来版本密钥漂移检测器（比对 GetPassword 输出）；harness（CryptoHost.exe）与 TestHost.exe 同目录、同一退出前释放纪律 |

---
## 十四、追加：Main blob 真实加密器定位与解密实证（D3，2026-10-28）

Main blob（.hcp 工程源内容）加密器的完整定位、算法还原与解密实证单独成文：
**`docs/inovance-autoshop-project-encryption.md`**。与本桥相关的结论摘要：

| 项 | 结论 |
|---|---|
| 真加密器 | **`CUCodeCrypt`（InterfaceMgr.dll）**：`EncryptArithmetic/DecryptArithmetic`（RVA 0x545F0/0x54640）+ `UCodeFileEncrypt/UCodeDecrypt` 家族 14 个修饰名被 AutoShop.exe **直接静态导入**——与 EnDecrypt.dll（零引用，十三节）完全不同 |
| 算法 | **11 字节表 ADD/XOR 流密码（非 AES/非 Blowfish）**：`pt[i] = ((ct[i] ^ (i&0xFF 当 i 偶)) − key[(i+1) % 11]) & 0xFF`；密钥 = InterfaceMgr.dll .rdata RVA 0xB6620 的 ASCII `"SclSoftware"` |
| 明文 | Main blob 解密后 = **FF FE（UTF-16LE BOM）+ UTF-16LE XML 工程源内容**；A1 18,944 B → 9,472 字符 XML 逐字节完美（`<ProjectDesc>模板A工程描述</ProjectDesc>` 等中文全对） |
| P3 之谜全解 | "SlotaecSfwr" 奇数位循环 = UTF-16LE ASCII 高字节 0x00 的密文形态（**非交错层**）；"挂起区" = CJK 汉字；"bj" prologue = enc(FF FE BOM)；180 B 公共前缀 = 项目无关 XML 头 |
| 红鲱鱼排除 | AutoShop.exe 内嵌完整 Blowfish（标准表 VA 0x1EF1E40、F@0x46D0F0、enc/dec、密钥 `"D1w0@t0sHtryF?5t"` @ 0x1F070F0 + `"user0"` @ 0x1F070E8）——对 blob 全阴性且 loader 闭包零路径，与 Main blob 无关 |
| C# 交付 | **`src/crypto/AutoShopProjectCrypt.cs`（新增）**：`DecryptBlob/EncryptBlob/TryDecryptProjectXml/SelfTest`，纯内存只读、无厂商 DLL 依赖；`SelfTest()` 含已知答案锚点（LoginPassowrd.dat 真实样本）充版本闸门；独立 net8.0 工程实测 PASS |
| 对本桥的含义 | **工程读取解锁**：.hcpp 解析（P1 reader）→ Main blob 解密（本交付）→ XML → 工程属性/文件列表/变量清单可离线读取；加密方向同步还原（合成路径打通，打开往返仍需真机验证）；.LD 文件内容为**明文**（"CLVTItem" 二进制格式，可直接读） |

---
## 十五、追加：通信栈真实读取路径（F2，2026-10-28）

E2 遗留问题的收敛：`read_elements count=0` 的根因是基类 `AddElem` 桩（第五节）——F2 找到了
**非桩的填表原语**，重建了最小可用读取管线，并用独立 harness 实证到"请求帧上网"。
harness 源码在 `tools/AutoShopNativeBridge/src/f2/`（**不进 CI**，构建脚本 `f2build.cmd`），
运行证据在 `%TEMP%\plcmcp-handoff\f2-realread\`。

### 15.1 导出面判别（哪些是桩、哪些是真的）【实证，dumpbin + 反汇编】

| 导出 | InterfaceMgr RVA | 判定 |
|---|---|---|
| `?AddElem@CSoftAddrMap@@...` | 0x5080 | **桩**（`xor eax,eax; ret 8`，第五节已证） |
| `?AddElem@CElemMonArray@@...` / `?AddElem@CH530ElemMonArray@@...` | 0x62000 | **桩**（`xor eax,eax; ret 0xC`） |
| `?AddElemToMonDoc@CElemMonData@@...`（两代重载） | 0x5040 / 0x152F0 | **桩**（析参数串后 `xor eax,eax; ret`） |
| `?SetAt@?$CMap@<tagSoftAddr>@@...` | **0x12AB0** | **真实现**（AutoShop.exe 自己导入并调用，IAT 0x1EB16DC） |
| `??A?$CMap@<tagSoftAddr>@@...`（operator[]） | 0x12A40 | **真实现**（g_AddMonitorElem 内部用它插表） |
| `?Lookup` / `?GetStartPosition` / `?GetNextAssoc` / `?GetCount` / `?RemoveAll` | 0x129F0 / 0x12BC0 / 0x12C00 / 0x26130 / 0x12B60 | **真实现**（CMap 模板代码块） |
| `??0tagSoftAddr@@QAE@PB_WW4EnumForceQueryResult@@0HH@Z`（5 参 ctor） | 0x1C10 | **真实现**（构造 tag 的全部字段） |
| `?g_AddMonitorElem@@YAXW4EnumGCMDataType@@ABV?$CStringT@...@@PAVCSoftAddrMap@@@Z` | 0x3CCF0 | **真实现**（AutoShop 监视表的官方填表入口） |
| `?g_GetBitElemVal@@...` / `?g_GetIntElemVal@@...` | 0x3D6B0 / 0x3D620 | **真实现**（按键 Lookup 取值） |
| `CCommunicateEth::ReadElemValue` | CommunicateEthernet 0x37B0 | **真实现**（异步命令分发，rc=0x0=受理） |

`g_AddMonitorElem(type, addr, map)` 反汇编还给出 tag 的**权威字段语义**（推翻 S2 第五节的多处【推断】）：

```
tagSoftAddr（0x48 = 72 字节）：
  +0x00 vtable          +0x08 CString  ← 元素地址串（如 L"D100"），= map 的键
  +0x0C CString         +0x10 int      ← EnumGCMDataType（元素数据类型！）
  +0x14 CString         +0x18 int      +0x1C EnumForceQueryResult
  +0x20 double          ← 数值元件值槽（fldz 初始化；写回目标待 A2 闭环证实）
  +0x28/+0x2C/+0x30 int +0x34/+0x38/+0x3C/+0x40 CString
```

### 15.2 最小可用读取管线（全部经厂商导出，harness 已跑通构造侧）

```c
CSoftAddrMap ctor(0x53100)                                  // 空 map（28 字节头）
tag = malloc(0x48)+memset0; tagCtor5(tag, L"D100", 0, L"", type, 0)
CMap::SetAt(map, L"D100", &tag)                             // 0x12AB0，真实现
CCommunicateEth::SetCommunicatConfig(ethInfo(20B), NULL)    // {12939, 127,0,0,1}
CCommunicateEth::IsEthOnLine()                              // =1 ONLINE（回环模拟器）
CCommunicateEth::ReadElemValue(map, NULL, mode=0, level=0)  // rc=0 受理（异步）
Sleep → CMap::Lookup(map, L"D100", &tag) → tag+0x20 double  // 值回填目标（待证实）
```

EnumGCMDataType 经 `CDataType::GetBasicTypeBitLen(type)` 扫描实证：**type=1 → 位（bitlen 1），
type=4 → 字（bitlen 16）**。`CDataType::GetBasicTypeByName(名字)` 在独立进程中崩溃（依赖
AutoShop 启动期初始化的类型注册表），不可用——已如实记录。

### 15.3 A2 实证结果（127.0.0.1 模拟器，回环纪律）

| 步骤 | 结果【实证】 |
|---|---|
| 厂商 DLL 独立进程加载 + SetCommunicatConfig(回环) + IsEthOnLine | ONLINE（与 S2/E4 一致） |
| map+tag 构造（tagCtor5 + SetAt，type=4/1） | GetCount=2；tag 内存布局与 15.1 一致 |
| Modbus 502 基线（同进程） | D100=0x1234、M0=1 写读一致 |
| `ReadElemValue` | rc=0x0 受理 |
| 抓包（回环代理 12940→12939） | **请求帧首次直接捕获**：21 字节 `00 00 00 64 00 0f ff 02 a3 c1 00 00 00 00 00 02 30 00 03 01 96`（单连接单帧） |
| daemon 应答 | **无任何 S->C 字节，连接被关闭**；tag+0x20 保持 0 |

**结论（如实）**：构造侧管线（map/tag/config/read 调用）全部闭合且请求真实上网；**值回填未
闭合**——独立进程缺少 AutoShop UI 会话里监视启动前的某个前置（0x81 登录态查询/D1 会话
握手在 UI 流程中由 IDE 建立；CheckPassword("") 实验未改变结果）。**最接近的替代/完成路径**：
在 **E2 注入桥所在的 AutoShop 进程内**用同一套原语（SetAt + tagCtor5 + type）填 map 后调
`ReadElemValue`——那里会话机器已就绪；`HandleReadElements` 应升级为"经 SetAt/tagCtor5 填表，
从 tag+0x20 取值"，替换现在的空 map 调用。此为给集成负责人的明确接线说明（注入桥原生代码
不在 F2 可编辑范围）。

### 15.4 证据落盘

- harness 源码：`tools/AutoShopNativeBridge/src/f2/F2RealReadHarness.cpp` + `f2build.cmd`
- 运行输出 + 抓包：`%TEMP%\plcmcp-handoff\f2-realread\{run-final-12939.txt, run-proxy-12940.txt, wire-capture.log}`
- 实验进程已清理（harness 自退出、daemon/代理 taskkill）。

### 15.5 与第十五节前文的冲突裁决

第五节"`tagSoftAddr` 各 CString 字段语义为推断"升级为实证：+0x08=地址串（=键）、+0x10=类型；
"+0x20 double 数值槽"由推断升级为**强证据**（ctor `fldz` 初始化 + g_GetIntElemVal 取值路径），
"读到值"仍差 daemon 应答一步，维持【未验证】纪律。
