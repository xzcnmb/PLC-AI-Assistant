# 汇川 AutoShop 通信栈"写面"签名与实证（F1，2026-10-28）

> 本文档固化阶段三 F1（写面签名还原与实证）的全部结论：`CCommunicateEth`
> 写面 11 个导出的完整签名（调用约定/参数/返回值）、枚举取值表（从
> AutoShop.exe 真实调用点立即数反汇编）、写场景 `CSoftAddrMap` 构造序列、
> 对 127.0.0.1 模拟器的实证记录（含阴性记录），以及给 E4 的**门禁放行条件清单**。
>
> 对象：AutoShop V4.12.0.2（x86，`D:\Inovance Control\AutoShop`）。
> 与 `docs/inovance-autoshop-native-bridge.md`（S2 只读垫片）、
> `docs/inovance-autoshop-inject-bridge.md`（E2 注入桥）、
> `docs/inovance-autoshop-open-and-compile.md`（E3 编译链）配合阅读。
>
> **全部结论均为逆向所得，无汇川厂商背书；版本锁定 4.12.0.2 / x86。**
> 标注约定：【实证】= 亲自执行并复核（dumpbin / capstone 反汇编 / harness 实调 / Modbus 回读）；
> 【推断】= 基于实证的推理；【未验证】= 无直接证据，禁止当作事实。
> 证据分级：**A** = 反汇编+运行实调双证；**B** = 反汇编单证；**C** = 推断。

---

## 〇、结论摘要（先读这一节）

| 项 | 结论 |
|---|---|
| **调用链全貌（新发现）** | UI → `IH530Communicate`（InterfaceMgr.dll 静态门面）→ **活动通信器**（工厂单例 `CInterfaceMgr::GetPublicMgr()` 等）vtable 转发 → `Communicater.dll`（通道分发器，按 `[0x1000A514]==4` 选通道）→ **以太网通道** `CCommunicateEth`（CommunicateEthernet.dll）或 **本地通道** `CCommunicaterLocal`（CommunicaterModule.dll）。**AutoShop.exe 从不直接导入 CommunicateEthernet.dll**（唯一静态导入者是 Communicater.dll）【实证】 |
| **写面导出真相（改写 E2 结论）** | `WriteElemValue`/`Unforce`/`ProgramValidate` 三个导出在 4.12.0.2 全部指向同一 6 字节桩 **`mov eax,0xF390; ret`（RVA 0x37A0）**：0xF390 = **62352 = 通信栈自身的"失败/不可用"错误码**（`ReadElemValue`/`CCommunicaterLocal::WriteElemValue` 的失败路径同样 `mov eax,0xF390`）。即**以太网通道的元件写导出在本版是桩**——不是"尾参未还原"，而是**根本没有函数体**【实证：反汇编 + harness 运行时实调返回 62352 + Modbus 前后回读零变化 + daemon 日志无信号】 |
| **真实写实现所在（新发现）** | **`CCommunicaterLocal::WriteElemValue`（CommunicaterModule.dll RVA 0x8DC0）是真实函数体**：单例 `Instance()` → 分配 0x3C 字节命令对象 → 命令对象装载 (mode, level) 与 (map, &hWnd, map2) → `RunCommThread(this, int, CCommandBase*, EnumExecuteMode)` 派发。但本地通道 = **串口/USB 通道**（`SetCommunicatConfig(tagSerialInfo*)`/`IsComOnLine`/`IsUsbOnLine`），**不经以太网到 12939**【实证：反汇编；对模拟器无效】 |
| **枚举取值（关键交付）** | `EnumExecuteMode`=1（UI 全部写/读/状态调用点一致 push 1）、`EnumCommandLevel`=0（全部调用点一致 push 0）、`EnumGCMState`: 1=RUN / 0=STOP（STOP 处理器分支 `state==1→set 0` 实证）、`EnumCommunicaterType`: 4=以太网（两处独立二进制交叉验证）、`EnumCommunicaterCmd`: 0x1005=设密码 / 0x1006=校密码（+ 0x1007/0x1009 出现在密码族调用点）、`EnumH530SimpleCmdType`: 0 与 0x7B(123)（同一函数先发 0 后轮询 0x7B ×200）【实证】 |
| **Force 实弹阴性（重要）** | `Force`（RVA 0x3200）是**真实函数体**（0x34 字节命令对象），但用基类 `CSoftAddrMap`+一个 `tagSoftAddr` 条目按厂商转发函数确认的参数序调用时，**进程以用户 SEH 无法捕获的方式终止**（厂商栈 cookie fast-fail 形态）——E2 的"猜测签名调用会危及宿主"警示以运行时实验成立【实证 ×2 可复现】 |
| **门禁放行** | 见第九节清单：写/Force/下载三层门全部保持关闭；`element_write` 的"签名验证闸门"现在有**确定性依据**（以太网写导出 = 桩），不是保守猜测 |

---

## 一、对象模型与调用链（谁调用谁）

### 1.1 模块分层【实证：dumpbin /imports 全量扫描】

| 层 | 模块 | 角色 |
|---|---|---|
| UI | AutoShop.exe（55 MB） | 调 `IH530Communicate` 静态门面（InterfaceMgr.dll）；**不导入** CommunicateEthernet.dll / Communicater.dll |
| 门面 | InterfaceMgr.dll（4534 导出） | `IH530Communicate`（SAJ 静态门面）+ `CCommunicater`（UAEJ 虚基类，**全部虚实现 = `xor eax,eax; ret N` 桩**）+ `CCommunicaterLocal`/`IH530Communicate_Interface`（同桩）+ 类型/映射原语（`CSoftAddrMap`/`tagSoftAddr`/`CMap<...>` 全部导出且为真实实现） |
| 通道分发 | Communicater.dll（14 KB text，0 导出，无人导入） | 实现 `CCommunicater` 派生类：每个方法先 `cmp [0x1000A514], 4`（当前通道）→ 以太网路径调 `CCommunicateEth` 静态导入 / 本地路径调 `CCommunicaterLocal` 静态导入（两套 IAT 同签名同参数序） |
| 以太网通道 | CommunicateEthernet.dll（84 导出） | `CCommunicateEth` 静态门面（S2 垫片的对象） |
| 本地通道 | CommunicaterModule.dll（95 导出） | `CCommunicaterLocal` 静态门面（串口/USB），单例 `Instance()`（RVA 0x7970，`m_communicate` 静态 @0x78DA8） |

- 通信对象内部（S2 第十节结论不变）：`+0x5C` 持连接配置对象、`+0x48` 持 HWND。
- `Communicater.dll` 的通道分发静态 `[0x1000A514]`：**值 4 = 以太网**（与 `EnumCommunicaterType` 取值交叉验证，见第三节）。

### 1.2 UI 实际调用点【实证：AutoShop.exe IAT 扫描（IAT VA = 0x1EB081C 起，1033 项 InterfaceMgr 导入）】

AutoShop.exe 导入的写面相关 `IH530Communicate` 静态门面（IAT VA / 调用点数）：

| 导出（签名见第二节） | IAT VA | 调用点数 |
|---|---|---|
| `?WriteMemory@IH530Communicate@@SAJPAVCSoftAddrMap@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x1EB16E4 | **30** |
| `?ReadMemory@IH530Communicate@@SAJPAVCMonitElemBuffer@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x1EB1728 | 25 |
| `?SetGCMState@IH530Communicate@@SAJPAW4EnumGCMState@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x1EB1730 | 11 |
| `?GetGCMState@IH530Communicate@@SAJPAW4EnumGCMState@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x1EB174C | 35 |
| `?SetPassword@IH530Communicate@@SAJW4EnumCommunicaterCmd@@ABVHString@@1W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x1EB11E4 | 22 |
| `?CheckPassword@IH530Communicate@@SAJW4EnumCommunicaterCmd@@ABV?$CStringT@...@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x1EB0ACC | 1 |
| `?SimpleCommand@IH530Communicate@@SAJPAKW4EnumH530SimpleCmdType@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x1EB0AC4 | 2 |
| `?SetCommunicatType@IH530Communicate@@SAXW4EnumCommunicaterType@@PAUtagEthernetInfo@@@Z` | 0x1EB09D0 | 27 |

**注意**：UI 的元件写走 **`WriteMemory`**（单 map、4 参），不是 `CCommunicateEth::WriteElemValue`（5 参双 map）。`WriteElemValue` 是通信栈门面层的函数，UI 不直接到达。

### 1.3 IH530Communicate 转发器形态【实证：反汇编 0x68E20（WriteMemory）/0x68A10（SetGCMState）】

```
IH530Communicate::WriteMemory(map, wnd, mode, level):    ; InterfaceMgr RVA 0x68E20
    ecx = [0x101366E0]        ; 静态：工厂/聚合器单例指针
    eax = [ecx]; edx = [eax+4]; call edx   ; vtable 槽 1 → 返回活动通信器（eax）
    push level; push mode; push wnd; push map
    mov ecx, eax; call [eax_vtbl + 0xEC]   ; 活动通信器 vtable 槽 +0xEC
    ret                        ; 4 参由虚实现 callee 清理（ret 0x10）
```
即：UI → 门面 → 工厂单例 vtable 槽 1 取活动通信器 → 活动通信器 vtable 虚调用。各门面对应的虚槽（活动通信器 vtable，class 布局见 1.1 分发行）：
`WriteMemory`→+0xEC、`SetGCMState`→+0xA8、`ReadMemory`→+0x14、`WriteElemValue`→+0x44（Communicater.dll 分发器类 vtable @0x100056C0；基类 `CCommunicater` vtable @InterfaceMgr 0xB3D24 共 146 槽）。

---

## 二、写面完整签名表（T1）

全部 11 个导出：**调用约定 = `SA`（static，x86 cdecl，caller 清栈）；返回 = `long`（eax）**。
参数序由 Communicater.dll 的转发函数逐一确认（转发器 `push` 序 = 倒序展开 + `add esp, N` = 参数字节数）；`ret N` 桩（InterfaceMgr 基类）独立佐证参数字节量。

| # | 导出（mangled 节选） | RVA | 还原签名（x86 cdecl） | 参数含义 | 证据 |
|---|---|---|---|---|---|
| 1 | `?WriteElemValue@CCommunicateEth@@SAJPAVCSoftAddrMap@@PAVCWnd@@0W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | **0x37A0（桩）** | `long WriteElemValue(CSoftAddrMap* elements, CWnd* wnd, CSoftAddrMap* elements2, EnumExecuteMode, EnumCommandLevel)` | p1=元件 map（主），p2=UI 回调窗口（可 NULL，取 `wnd+0x20`=m_hWnd），p3=第二 map（同类型；读回/结果面），p4/p5=枚举（值见第三节） | 【A】转发器 0x10001390 倒序展开 5 参 + `add esp,0x14`；运行时实调返回 62352（桩） |
| 2 | `?Force@CCommunicateEth@@SAJPAVCSoftAddrMap@@PAVCWnd@@0W4EnumExecuteMode@@W4EnumCommandLevel@@H@Z` | **0x3200（真实）** | `long Force(CSoftAddrMap* elements, CWnd* wnd, CSoftAddrMap* elements2, EnumExecuteMode, EnumCommandLevel, int flag)` | p1/p3 同上；p4=mode、p5=level、**p6=int flag（语义未解，UI 路径未见取值）**；内部：分配 0x34 字节命令对象 → `map.GetCount()`==1 → 命令码 0x7D(125)/计数 5 | 【A】转发器 0x10001770 倒序展开 6 参 + `add esp,0x18`；实调崩溃（阴性，见第六节） |
| 3 | `?Unforce@CCommunicateEth@@SAJPAVCSoftAddrMap@@PAVCWnd@@0W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | **0x37A0（桩）** | `long Unforce(CSoftAddrMap*, CWnd*, CSoftAddrMap*, EnumExecuteMode, EnumCommandLevel)` | 同 WriteElemValue | 【A】同 1；实调返回 62352 |
| 4 | `?Download@CCommunicateEth@@SAJPB_WW4EnumCommunicaterCmd@@PAVCWnd@@HW4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x3040（真实） | `long Download(const wchar_t* path, EnumCommunicaterCmd cmd, CWnd* wnd, int index, EnumExecuteMode, EnumCommandLevel)` | p1=载荷路径（宽字符），p2=下载命令枚举，p3=窗口，p4=int index（疑通道/文件序号，未解），p5/p6=枚举 | 【B】mangled 名 + InterfaceMgr 基类 `ret 0x18`（6 参）+ Communicater.dll IAT 引用；**未实调（红线）** |
| 5 | `?EnableUpload@CCommunicateEth@@SAJHW4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x46C0（真实） | `long EnableUpload(int index, EnumExecuteMode, EnumCommandLevel)` | p1=int（疑通道 index，与 Download p4 同族） | 【B】mangled + `ret 0xC`（3 参）；未实调 |
| 6 | `?CompleteDownloadCmd@CCommunicateEth@@SAJW4EnumCommunicaterCmd@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x47E0（真实） | `long CompleteDownloadCmd(EnumCommunicaterCmd cmd, CWnd* wnd, EnumExecuteMode, EnumCommandLevel)` | 下载完成确认命令 | 【B】mangled + `ret 0x10`（4 参）；未实调 |
| 7 | `?SimpleCommand@CCommunicateEth@@SAJABV?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x3400（真实） | `long SimpleCommand(const CString& command, EnumExecuteMode, EnumCommandLevel)` | p1=命令文本（厂商 `CStringT<wchar_t, StrTraitMFC_DLL>` 常引用） | 【B】mangled + `ret 0xC`；UI 不直接调用（UI 用 `IH530Communicate::SimpleCommand(long* result, EnumH530SimpleCmdType, ...)` 变体，IAT 0x1EB0AC4） |
| 8 | `?SingleCommand@CCommunicateEth@@SAJABV?$CStringT@...@@GW4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x3350（真实） | `long SingleCommand(const CString& command, unsigned short count, EnumExecuteMode, EnumCommandLevel)` | p2=unsigned short（`G`；疑单命令计数/重复数，未解） | 【B】mangled + `ret 0x10`（4 参） |
| 9 | `?SimpleCommonCmd@CCommunicateEth@@SAJW4EnumSimpleCommonCmdType@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x34B0（真实） | `long SimpleCommonCmd(EnumSimpleCommonCmdType cmd, EnumExecuteMode, EnumCommandLevel)` | 取值表未还原（UI 无调用点） | 【B】mangled + `ret 0xC` |
| 10 | `?SingleSimpleCommand@CCommunicateEth@@SAJW4EnumSimpleCmdType@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x43E0（真实） | `long SingleSimpleCommand(EnumSimpleCmdType cmd, EnumExecuteMode, EnumCommandLevel)` | 取值表未还原（UI 无调用点） | 【B】mangled + `ret 0xC` |
| 11 | `?SettingPassword@CCommunicateEth@@SAJW4EnumCommunicaterCmd@@ABV?$CStringT@...@@1W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x3290（真实） | `long SettingPassword(EnumCommunicaterCmd cmd, const CString& pwd1, const CString& pwd2, EnumExecuteMode, EnumCommandLevel)` | p1=命令枚举（0x1005=设密码），p2/p3=新/旧密码（`1`=回引用第二个 CString）；UI 变体用 `HString`（`?SetPassword@IH530Communicate@@SAJW4EnumCommunicaterCmd@@ABVHString@@1...@Z`） | 【A】UI 调用点 0x00E93B9B 完整反汇编（push 序 + `add esp,0x14` = 5 参）+ 取值 0x1005 |
| 12* | `?CheckPassword@CCommunicateEth@@SAJW4EnumCommunicaterCmd@@ABV?$CStringT@...@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z` | 0x3C90（真实） | `long CheckPassword(EnumCommunicaterCmd cmd, const CString& pwd, EnumExecuteMode, EnumCommandLevel)` | p1=0x1006（校密码） | 【A】UI 调用点 0x00E93A91 反汇编（4 参 + 0x1006） |

`ret N` 独立佐证【实证】：InterfaceMgr.dll 基类桩 `WriteElemValue/Unforce/SettingPassword→ret 0x14`（20 B=5 参）、`Force/Download→ret 0x18`（24 B=6 参）、`CheckPassword/CompleteDownloadCmd/ReadElemValue/GetExecMode/SingleCommand→ret 0x10`（4 参）、`EnableUpload/SimpleCommonCmd/SingleSimpleCommand→ret 0xC`（3 参）。

### 2.1 返回值语义【实证 + 推断】

| 返回值 | 含义 | 证据 |
|---|---|---|
| 0 | 命令被受理（异步分发提交；**不代表执行完成**） | 【A】S2 TestHost (0,0) rc=0 + 本 harness `GetExecMode(0,0)` rc=0 + ReadElemValue 成功尾 `jmp 0x1000C290` |
| 1 | 部分函数的成功判据：`SetGCMState`/`SimpleCommand` 的 UI 调用点用 `cmp rc,1` 判成功 | 【A】SetGCMState 包装器 0x00E63B20（`cmp [ebp-4],1; jne fail; mov eax,1`）+ SimpleCommand 调用点 0x00E7331D（`cmp [ebp-0x18],1`） |
| **0xF390 = 62352** | **通用失败/不可用错误码**：写导出桩的返回值；`CCommunicaterLocal::WriteElemValue`/`ReadElemValue` 失败路径同码 | 【A】导出桩 `mov eax,0xF390; ret` + 失败路径反汇编 + harness 实调 |
| 17 = 0x11 | （观察）`GetExecMode/GetGCMState` 以 (mode=1, level=0) 调用时的拒绝码；同函数 (0,0) → rc=0 | 【B】harness 实调（回环在线状态）；语义未解 |

**rc=0/1 的两义性是 S2 文档第五节"rc=0=命令受理"结论的精化**：判据依函数而异，不可通用化。

---

## 三、枚举取值表（T1c，关键交付）

方法：对 AutoShop.exe 全部 `IH530Communicate` IAT 调用点做 `ff 15 <IAT>` 扫描 + 前置 `push imm` 反汇编（capstone；AutoShop.exe RELOCS_STRIPPED、ImageBase 0x400000，VA=文件内立即数）。合计扫描 11 个函数 152 个调用点。

### 3.1 EnumExecuteMode【实证 A：111 个调用点】

| 取值 | 含义 | 证据 |
|---|---|---|
| **1** | **正常执行/在线执行** | WriteMemory 20 处、SetGCMState 8 处、SetPassword 17 处、CheckPassword 1、SimpleCommand 1、WriteMemoryEx 2、GetPLCRunMode 3、ReadPLCNetMode 2、WritePLCNetMode 1——**全部一致 push 1** |
| 0 | （GetExecMode/GetGCMState/ReadMemory 少数点出现 (0,0) 组合；harness 佐证 (0,0) → rc=0 被受理） | 【B】调用点扫描 + harness |

注：UI 的读/写/状态调用**从未出现 mode≠1**（除上述 (0,0) 观察点）；2/3 等其余取值无调用点证据。

### 3.2 EnumCommandLevel【实证 A：全部调用点一致】

| 取值 | 含义 | 证据 |
|---|---|---|
| **0** | 默认级别 | 全部 152 个调用点无一例外 push 0 |

### 3.3 EnumGCMState【实证 A：运行/停止语义双向验证】

| 取值 | 含义 | 证据 |
|---|---|---|
| **1** | **RUN（运行）** | 停止处理器 0x009301xx：`GetGCMState(&s,NULL,1,0)` → `cmp s,1; jne else` →（运行中才）`report + SetGCMState(&0,...)`——**当前值==1 触发"停止"动作** ⇒ 1=RUN |
| **0** | **STOP（停止）** | 同上（置 0 = 停止）；RUN 方向：下载流函数 0x015F1BDB `mov [ebp-0x18],1` → `SetGCMState(&1,...)`（下载前使能） |
| 2 | 附加状态（语义未解；相邻包装器 0x00E63B20 置 2，调用序 = 置 2 → 在线检查 → 置 3） | 【B】包装器反汇编 |
| 3 | 附加状态（语义未解；0x00E63B90 置 3） | 【B】同上 |

### 3.4 EnumCommunicaterType【实证 A：两个独立二进制交叉验证】

| 取值 | 含义 | 证据 |
|---|---|---|
| **4** | **以太网** | ① AutoShop.exe `SetCommunicatType` 调用点 6 处 push 4（含 `mov [ebp-0x28],0x328B`（12939）后 push &info、push 4）；② Communicater.dll 通道分发 `cmp [0x1000A514],4 → 以太网分支` |
| 0 | 空通道/未设置（4 处 push 0） | 【B】调用点扫描 |
| 其余 | 串口/USB（`CCommunicaterLocal` 侧；`GetCurPLCCommunicateTypeWithCom` 族），未还原 | 【未验证】 |

### 3.5 EnumCommunicaterCmd【实证 A：密码族；Download 族未还原】

| 取值 | 含义 | 证据 |
|---|---|---|
| **0x1005** | 设置密码 | SetPassword UI 调用点 0x00E93B9B（push 0x1005）+ 0x0092CD8E 等多址 |
| **0x1006** | 校验密码 | CheckPassword UI 调用点 0x00E93A91（push 0x1006）+ SetPassword 邻址 |
| 0x1007 / 0x1009 | 密码族附加命令（疑清除/解锁，未解） | 【B】SetPassword 调用点 0x00E94009（0x1007）/ 0x00F6BE25（0x1009） |
| Download/Upload 族 | `Download(PB_W, cmd, ...)` 的 cmd 取值：UI 不导入 `Download@IH530Communicate`（下载走 `DownloadFile`/`Net_Download` 层），**无调用点证据** | 【未验证】禁止当作事实 |

### 3.6 EnumH530SimpleCmdType【实证 A：2 个调用点】

| 取值 | 含义 | 证据 |
|---|---|---|
| 0 | 初始/查询（先发） | SimpleCommand UI 调用点 0x00E7324D（push 0） |
| **0x7B = 123** | 轮询命令（随后 ×200 次循环重发，`cmp [ebp-0x20],0xC8`；rc 与 1 比较） | SimpleCommand UI 调用点 0x00E7331D（push 0x7B + 200 次循环）——疑"PLC 就绪等待"序列 |

### 3.7 EnumSimpleCmdType / EnumSimpleCommonCmdType

`SingleSimpleCommand`/`SimpleCommonCmd` 为 `CCommunicater` 层函数，UI 不导入、无调用点——**取值表未还原**【未验证】。

---

## 四、写场景 CSoftAddrMap 构造序列（T1d）

### 4.1 AutoShop 写对话框的真实序列【实证：AutoShop.exe 0x00A9249C–0x00A9267D 反汇编】

```
1.  ecx=[ebp-0x10]（上下文对象）→ vtable 槽 +0x4C → 写目标对象（[ebp-0x14]）
2.  call [0x1EB171C] = CInterfaceMgr::GetPublicMgr()          ; PublicMgr 单例
3.  eax = publicMgr → vtable 槽 +0x80 → **活动 CSoftAddrMap***（[ebp-0x24]）
    ⇒ map 不是对话框构造的，是通信器持有的活动 map
4.  push 0x48; call malloc → 新 tagSoftAddr（72 字节，确认 S2 的 72B 推断）
5.  [tag] = 0x1EF0790（AutoShop 自有 tagSoftAddr 子类 vtable，.data）
6.  写目标对象 → vtable 槽 +0x48 → tag+0x10 = 返回值
7.  CString 构造进 tag+0x0C（源自 .data 常量 0x23FF9A4/0x23FF9A8）
8.  map.SetAt(key=地址串缓冲(wchar_t*), value=tag)（CMap<CStringT,wchar_t const*,tagSoftAddr*,tagSoftAddr&>::SetAt，IAT 0x1EB16DC）
9.  call [0x1EB16E4] = IH530Communicate::WriteMemory(map, wnd, mode=1, level=0)
    （push 序：0(level), 1(mode), wnd, map；add esp,0x10）
```

### 4.2 映射原语导出（InterfaceMgr.dll，全部真实实现）【实证】

| 原语 | RVA | 说明 |
|---|---|---|
| `??0CSoftAddrMap@@QAE@XZ` | 0x53100 | 基类 ctor（vtable + 空 CMap 头：+0x04 hash=0、+0x08 size=17、+0x0C count=0） |
| `??0tagSoftAddr@@QAE@XZ` | 0x1AB0 | 默认 ctor：vtable=0x100B3484、CString +0x08/+0x0C/+0x14、int +0x10、**double +0x20（元件值）**、int +0x18/+0x1C ⇒ **72 字节布局实证** |
| `??0tagSoftAddr@@QAE@PB_WW4EnumForceQueryResult@@0HH@Z` | 0x1C10 | 5 参 ctor：(addr→+0x08, EnumForceQueryResult→+0x1C, addr2→+0x0C, int→+0x10, int→+0x18) |
| `?SetAt@?$CMap@...tagSoftAddr...@@QAEXPB_WAAPAUtagSoftAddr@@@Z` | 0x12AB0 | 真实插入（Lookup+存储，`ret 8`） |
| `?GetCount@?$CMap@...@@QBEHXZ` | 0x26130 | 真实（Force 内部亦用它判元素数：==1 → 命令码 0x7D） |
| `?RemoveAll@?$CMap@...@@QAEXXZ` | 0x12B60 | 真实 |
| `??A?$CMap@...@@QAEAAPAUtagSoftAddr@@PB_W@Z` | 0x12A40 | `operator[](const wchar_t*)` → `tagSoftAddr&`（缺失键置 NULL 槽） |

### 4.3 harness 的可复现构造（Base-class 版）【实证】

harness（第六节）以基类原语复现 4.1 的形状：
`CSoftAddrMap ctor(256B 零缓冲) → tagSoftAddr ctor5(L"D200",0,L"",0,0) → tag+0x20=4660.0 → SetAt(map,L"D200",tag) → GetCount()==1 确认插入`。
**生命周期红线（实测踩坑）**：tag 缓冲必须长于 map 的生命周期——厂商真实函数体（Force）会迭代 map 条目；栈局部 tag 导致崩溃（第六节阴性 #2）。

---

## 五、编译入口签名证据（E3 链的完整签名文，T1-compile）

E3 已实调成功（×3，`docs/inovance-autoshop-open-and-compile.md` 第六节）；本节补全"调用约定+参数+返回+异步时序+前置条件"的签名证据（反汇编锚点）：

```
; AutoShop.exe 4.12.0.2 x86, ImageBase 0x400000, RELOCS_STRIPPED（VA 固定）
PFN_GETROOT   = 0x001581A70  ; void* __cdecl GetRootManager(void)
PFN_GETMEMBER = 0x001581CA0  ; void* __thiscall GetCompileAllMgr(void* root)  = *(root+0x34)
PFN_COMPILEALL= 0x0015A2EC0  ; void  __thiscall CompileAll(void* cmpMgr)      ; 无参

void* root   = ((PFN_GETROOT)0x001581A70)();        ; 实测返回 0x07093FC0/0x07103FC0（476B 根管理器）
void* cmpMgr = ((PFN_GETMEMBER)0x001581CA0)(root);  ; ecx=root；实测 0x0DD50BC8
((PFN_COMPILEALL)0x0015A2EC0)(cmpMgr);              ; __thiscall：ecx=cmpMgr，栈 0 参
```

| 项 | 签名证据 | 等级 |
|---|---|---|
| 调用约定 | `0x1581A70` 无 this/无栈参（单例 getter，`mov eax,[0x2438E3C]` 形态）；`0x1581CA0` = `mov eax,[ecx+0x34]`（thiscall 访问器）；`0x15A2EC0` 入口无 `[ebp+8]` 读取（无参），`ecx` 由调用方（E3 6.2 代码）置为 cmpMgr——**__thiscall 修正版（E3 第 3 次实验）** | A |
| 参数/返回 | 无栈参、无返回值消费（处理器 0xA847A0 调用后不读 eax；编译产物以文件出现为准） | A |
| 异步时序 | 入口内部：快照（0x43DDD0 ctor + 0x1592B60 文件列表收集）→ 逐文件 `new 0x4C` 编译项（+0x14=文件对象, +0x18=1, +0x1C=2）→ **`0x4179A0` 异步提交后立即返回**；产物数秒内落盘（实测 25–40s 收取） | A |
| 前置条件 | **门 0x161B620（当前工程检查）非 0**——必须已打开工程（CLI 裸路径 `.hcp`，E3 第四节构造法）；下载门 `0x1581B60(root+0x04)→0x162B2E0` 非 0 = 占用中即退出；工程打开检查 `0x15929D0`；快照 0 文件即退出 | A |
| 完整管线 | 编译+生成下载文件 = `0x15A3780(this, int mode)`（命令 0x8257 处理器 0xA81B00；LoadString 899/900/901 = 完成程序编译/完成编译/生成下载文件成功） | B（串证据） |
| 产物 | `<工程目录>\Compile\<名>.compile`（UCode 加密，D3 算法可解）+ `<名>.foid` + 重打包 `<名>.hcpp`；`\Temp\stprog.o/symbol.sym/varsinfo.foid/userprog.foid` = 下载载荷临时件（0x8257 管线后半段，0x80E1 未触达） | A（E3 ×3） |

**注入桥接线锚点**（E2 `HandleCompile` 的"地址已配置 + 签名已验证"两条件均已满足）：
`compileEntryRva` = `0x15A2EC0`（或完整三行链 0x1581A70/0x1581CA0/0x15A2EC0）；签名验证 = 本节约定/参数/返回/前置条件四项 A 级证据。

---

## 六、实证记录（T2，全部对 127.0.0.1 模拟器，零物理风险）

### 6.1 实验设置【实证】

- 模拟器：`D:\Inovance Control\AutoShop\Simulator\h5u_daemon.exe`（以 Simulator 目录为工作目录，PID 61624；TCP 502 Modbus + TCP 127.0.0.1:12939 原生协议；程序默认 STOP、寄存器全 0）。启动前清掉了两个残留实例（46676/61508），实验后 `taskkill /F` 清理并复核端口释放（仅余 TIME_WAIT 自熄）。
- harness：`tools/AutoShopNativeBridge/src/f1/WriteProbe.cpp`（x86 v143 /MT；静态导入写面+只读基线导出，运行时 GetProcAddress 解析 InterfaceMgr 映射原语；**目标硬校验 127.0.0.0/8**，非回环在 SetCommunicatConfig 之前即拒绝）。构建：`build_f1.cmd` → `%LOCALAPPDATA%\AutoShopNativeBridge-f1-run\`（不入仓库）。标 **LocalSimulator，不进 CI**。

### 6.2 只读闭环（正性）【实证】

```
[A] InterfaceMgr.dll 映射原语全部解析（ctor/[]/SetAt/GetCount/tagSoftAddr ctor，6/6）
[B] SetCommunicatConfig(tagEthernetInfo{127.0.0.1:12939}, HWND=NULL) → 完成
[B] IsEthOnLine() → 1 = ONLINE（回环模拟器在环）
[B] GetExecMode(mode=1, level=0) → rc=17, mode 未写回（-1）
[B] GetGCMState (mode=1, level=0) → rc=17, state 未写回（-1）
[B] GetExecMode(mode=0, level=0) → rc=0（S2 基线复现；受理）
[modbus] FC3 基线（协议地址 199..201）→ OK regs=[0 0 0]
```

### 6.3 最小写闭环（**阴性，结论性**）【实证】

```
[C] map 构造成功：CSoftAddrMap(基类 ctor) + tagSoftAddr(ctor5, L"D200", 值 4660.0@+0x20)
    + SetAt → GetCount()==1（插入实证）
[C] WriteElemValue(map, NULL, map, mode=1, level=0) → **rc=62352（0xF390）**
    ——运行时复现静态结论：导出@0x37A0 = `mov eax,0xF390; ret` 桩
[modbus] FC3 写后回读（协议地址 199..201）→ regs=[0 0 0]，前后零变化
[daemon 侧信号] stdout 仅启动行；app\log\h5u_log_0.csv 仅 PowerOn/Update 轮询，
    **无任何 12939 命令链日志** ——桩在触网之前即返回，符合静态分析
[D] Unforce(同参) → rc=62352（同一桩）
[E] Force(map, NULL, map, mode=1, level=0, flag=1) → **进程终止（阴性）**，见 6.4
[F] Download 未调用（红线：模拟器装载为桩；D1 文件传输层持有下载工作流）
```

**写闭环裁决**：通信栈（以太网通道）层级的元件写在 4.12.0.2 **不可达**——导出即桩（返回 62352、零行为、零网络流量）。这不是"写入被 daemon 忽略"，而是**厂商 DLL 自身不提供以太网元件写函数体**。对模拟器的唯一实证写通道仍是 **Modbus TCP 502（FC5/6，P2 实证）**。真实写实现位于 `CCommunicaterLocal::WriteElemValue`（串口/USB 通道）与 AutoShop UI 的 `IH530Communicate::WriteMemory` 路径——对 12939 以太网模拟器均无效。

### 6.4 Force 实弹阴性（可复现 ×2）【实证】

`Force` 导出（RVA 0x3200）是真实函数体；按厂商转发函数确认的参数序、以基类 map + 一个 tagSoftAddr 条目调用时，**进程以用户 SEH 无法捕获的方式立即终止**（报告终止于调用前 marker 行；__try/__except 未收到控制权——厂商栈 cookie fast-fail 形态【推断】）。第二次运行复现一致。
⇒ **E2 的"猜测签名调用会危及宿主"警示以运行时实验成立**；SEH 包裹（-32004）不足以兜底 Force 路径，门禁必须保持关闭直到完整元素模型（派生 map + tagSoftAddr 全字段语义）还原。

### 6.5 报告落盘

`%TEMP%\plcmcp-handoff\f1-write-probe\report.txt`（完整全绿运行）、`report-run1-skip-force.txt`、`report-run2-force-crash.txt`（Force 阴性，止于 marker 行）。

---

## 七、给 E4 的门禁放行条件清单（T3 交付）

### 7.1 三层门现状与放行所需

| 门 | 层 | 现状 | 放行条件（全部满足才可打开） |
|---|---|---|---|
| MCP 审批 | E4/S4 | 默认关 | 下载/强制/写参数按 DeploymentPlan+审批（contracts.md 56–58）；默认仅回环/模拟器自动放行 |
| C# 门 `InjectBridgeGateOptions` | E2 | AllowElementWrite/AllowForce/AllowDownload 默认全关；RequireLoopbackTarget 不可关 | 操作员显式开 + 桥侧 pending 配置对应 allow 开 |
| 桥 DLL 门 | E2 | allowForce/allowDownload 默认 false | 注入器 `--allow-force 1` / `--allow-download 1` |
| 回环硬校验 | E2 | 已实现（set_target 仅 127.0.0.0/8；写请求 target 非回环即拒） | （常开，不可放行） |
| **签名验证闸门** | E2 | 写导出"解析不调用" | **本交付后应改为"确定性关闭"**（见 7.2），Force 保持关闭（6.4） |

### 7.2 `element_write` 能力的最终裁决（替代原"签名还原后可开"的预留条件）

1. **通信栈以太网写导出 = 桩（0xF390，无函数体）**——`WriteElemValue`/`Unforce` 在 4.12.0.2 以太网通道**确定性不可用**。桥的写应答应从 `resolved:true, invoked:false + "签名未还原"` 更新为 `resolved:true, invoked:false + reason="vendor export is a stub (returns 0xF390=62352; no function body in the eth channel build)"`（-32001 语义族：导出不可用）。
2. **UI 写路径的 ABI（`IH530Communicate::WriteMemory(map, wnd, mode=1, level=0)`，IAT 0x1EB16E4）已完整还原**（A 级）——但其在**活动通信器**内的下游（工厂单例 vtable 槽 1 的返回对象、槽 +0xEC 的真实实现）依赖 AutoShop 进程内的对象状态；进程内调用必须**先有已打开工程 + 已建立连接**（`IsEthOnLine` 之外的会话建立未还原）。
3. **对模拟器的写闭环 = Modbus TCP 502（FC5/6）**——已实证（P2），与通信栈无关。MCP 的模拟器写能力应走 Modbus 适配器，不走注入桥写面。
4. 放行 `element_write`（真机/进程内）需要的新证据（当前均【未验证】）：
   - 活动通信器解析：工厂单例（InterfaceMgr `.data` 0x101366E0 指向的对象）vtable 槽 1 返回对象的类与 vtable 全槽映射；
   - 会话建立：`SetCommunicatConfig` + 连接建立（`IsEthOnLine`=1 之外的状态机）；
   - 写内存路径实测：`WriteMemory` 进程内调用 + tagSoftAddr **派生类**（vtable 0x1EF0790，AutoShop 自有子类）的全字段语义；
   - 值语义：tag+0x20 double 对 D 寄存器的编码（整数/浮点分支）。

### 7.3 `force` 能力

保持 **Unsupported/门关**。6.4 实验证明：即便参数序正确、SEH 兜底，Force 真实函数体仍以不可捕获方式终止宿主。放行需要：完整元素模型（派生 map + `CForceTable` + tagSoftAddr 全字段）+ RUN 态 PLC 语义 + 独立验证，**当前不具备任何一项**。

### 7.4 `compile` 能力

E3 地址 + 本文档第五节签名证据 = **两条件已齐**。E4/集成代理可接线 `compileEntryRva=0x15A2EC0`（或三行链），桥的 `HandleCompile` 从诚实 Unsupported 切换为受门控调用；前置条件（已打开工程）由 open 流程保证。

### 7.5 `download` 能力

保持 **Unsupported/审批门控**。D1 12939 文件传输层持有载荷工作流；`Download@CCommunicateEth` 签名已还原（第二节）但语义/载荷未接线，且 UI 下载走 `DownloadFile`/`Net_Download` 层（无 `EnumCommunicaterCmd` 取值证据）。

---

## 八、已知限制与未验证项（禁止当作事实）

1. **版本锁定 4.12.0.2 / x86**：全部 RVA/立即数随版本漂移（AutoShop.exe RELOCS_STRIPPED 无 ASLR，但升级即失效）。
2. `Download` p4（int index）、`EnableUpload` p1、`Force` p6（int flag）、`SingleCommand` p2（unsigned short）语义未解。
3. `EnumCommunicaterCmd` 的 Download/Upload 族取值、`EnumSimpleCmdType`/`EnumSimpleCommonCmdType` 取值表未还原（UI 无调用点）。
4. `EnumGCMState` 2/3 语义未解；`GetExecMode` rc=17（(1,0) 拒绝）语义未解。
5. 活动通信器的类与 vtable 全槽映射未还原（1.2 的槽位是 Communicater.dll 分发器类 vtable @0x100056C0 的对应关系，工厂返回对象是否即该类【推断】）。
6. tagSoftAddr 派生类（vtable 0x1EF0790）字段语义（AutoShop 写对话框的填充值）未全还原；+0x14/+0x34/+0x38/+0x3C/+0x40 五个 CString 的语义仍为【推断】。
7. Force 崩溃的精确根因（fast-fail 假设）未调试器级确认。
8. 真机行为零验证：本文全部结论仅覆盖 127.0.0.1 模拟器与静态分析。

---

## 九、红线合规声明（F1 实验全程）

- **零 PLC 动作**：全部实验仅触达 127.0.0.1 模拟器（h5u_daemon）与本地文件分析。
- **回环硬校验**：WriteProbe 的目标解析在 `SetCommunicatConfig` 之前拒绝一切非 127.0.0.0/8 目标（harness 代码即红线）。
- **安装目录零写入**：CommunicateEthernet.dll/InterfaceMgr.dll 仅**复制**到 run 目录；模拟器目录零写入（daemon 自身日志除外）。
- **注册表零写入**；实验实例/daemon 已清理（taskkill 复核端口释放）。
- 写命令从未发往任何非回环目标。
- 构建产物不入仓库（`%LOCALAPPDATA%\AutoShopNativeBridge-f1-run\`）。
