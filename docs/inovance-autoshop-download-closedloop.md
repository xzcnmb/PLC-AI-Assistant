# 汇川 AutoShop 下载闭环实证（编译产物 → 下载 → 装载 → 寄存器变活）（G2）

> 本文档固化 G2（下载闭环大块）的全部静态结论与对 127.0.0.1 模拟器的实证记录：
> AutoShop 下载对话框到通信栈的调用语义、"编译产物→可下载载荷"的最小转换、
> 12939 文件传输落盘、模拟器装载桩的帧级确认、**RUN 控制字让寄存器真正变活**
> 的首次实证，以及给集成的最小"下载+运行"接口。
>
> 对象：AutoShop V4.12.0.2（x86，`D:\Inovance Control\AutoShop`）+ 其自带
> H5U 软件模拟器 `Simulator\h5u_daemon.exe`（TCP 502 Modbus + 127.0.0.1:12939 原生协议）。
>
> 与以下文档配合阅读，本文只补"下载/装载/运行"这一段：
> - 写面签名与 Download/CompleteDownloadCmd/EnableUpload 签名 =
>   `docs/inovance-autoshop-write-signatures.md`（F1 第 2、3、6 节）
> - 12939 帧格式 + 文件传输链（serverId 4，type 2/5/0F）+ 登录 =
>   `docs/inovance-autoshop-download-protocol.md`（D1）
> - 编译产物落盘（Compile\*.compile + *.foid + 重打包 *.hcpp）=
>   `docs/inovance-autoshop-open-and-compile.md`（E3 第 5.4 节）
> - 通信栈进程内直调 + 回环硬校验 = `docs/inovance-autoshop-native-bridge.md`（S2/F2）
>
> 标注约定：【实证】= 本次亲自执行并复核（capstone 反汇编 / 自建 harness 实调 /
> 帧收发落盘 / Modbus 读回）；【推断】= 基于实证的推理；【未验证】= 无直接证据、
> 禁止当作事实。**全部结论均为逆向所得，无汇川厂商背书；版本锁定 4.12.0.2 / x86。**
> 能力声明纪律沿用 `docs/contracts.md`。
>
> 红线：全部实验仅对 127.0.0.1 模拟器；零物理 PLC；安装目录零写入（复核见第八节）；
> 实验 daemon / 抓包代理 / 注入实例全部干净退出。

---

## 〇、结论摘要（先读这一节）

| 项 | 结论 | 等级 |
|---|---|---|
| **下载载荷转换（最小）** | **重打包的 `project.hcpp`（PPCH 容器）本身就是一个自足的可下载载荷**——编译管线（E3）已经把它当"生成下载文件"产物落盘；下载对话框对它及 Compile 成员件按文件逐个传输，不做二次封装。转换函数只需守 PPCH magic | 【实证】+【静态】 |
| **UI 下载调用面** | AutoShop.exe **从不调用** `CCommunicateEth::Download/CompleteDownloadCmd` 这两个导出（它连 CommunicateEthernet.dll 都不静态导入，F1 第 1.1 节）；UI 下载走 **`IH530Communicate::DownloadFile`（IAT 0x1EB11D8，31 个调用点）**，逐个把 `project.hcpp / userprog.foid / stprog.o / stuserprog.o / symbol.sym / varsinfo.foid / EIPIns.foid` 当文件下载 | 【实证 A】 |
| **"下载族" EnumCommunicaterCmd** | **在 4.12.0.2 无法从调用点立即数还原**——因为根本没有人按 `Download(path,cmd,...)` 签名调用它取命令值；UI 用无 cmd 参数的 `DownloadFile`。协议层的"下载命令"实际是 **serverId 4 文件传输（type 2/5/0F，D1）+ serverId 1 寄存器链的 0xFF000010（下载状态/装载）与 0xFF000020（RUN/STOP）**，不是 MFC 层的 EnumCommunicaterCmd 立即数 | 【实证 A】（阴性=无调用点） |
| **Download 导出实调（独立 harness）** | `CCommunicateEth::Download(path,cmd,NULL,0,1,0)` 在 mode=1/level=0 返回 **rc=17（拒绝）**；换 mode=0/level=0 **rc=0 受理**（+ CompleteDownloadCmd rc=0、EnableUpload rc=0），**但对 daemon 零文件传输帧、哨兵文件不变**——异步通信 worker 需 AutoShop UI 会话前置（F2 §15.3 同象），**独立进程无法驱动它落地下载** | 【实证】 |
| **下载→落盘（D1 协议路径）** | 经 12939 文件传输链把 project.hcpp（15,745 B，重打包下载文件）下载进 daemon：`login→type2 会话→type5×N→0x0F`，**daemon 侧 md5 与源字节完全一致（f463cbaa…）**，`.hcpp` 旧件先改名 project_backup.hcpp | 【实证 A】（复用 D1 已验协议） |
| **装载（0xFF000010）** | daemon 对 `0xFF000010←1` 应答 **status 0x82**，与 D1 结论一致：模拟器 `load_down_file_start` 是桩（ret 1→0x82 映射表），**下载的文件不激活为运行程序** | 【实证】+【D1】 |
| **RUN（0xFF000020）——寄存器真正变活** | **首次实证**：向模拟器寄存器链写 `0xFF000020←1`（控制字），daemon 回 `[06]` ACK，随后 **Modbus 读回 D100 由 0 → 4660、R100 由 0 → 4660**（出厂默认 app 进 RUN 态执行）。写 `0xFF000020←0`（STOP）同样回 `[06]`；已写入值不被 STOP 清零（寄存器保持末值） | 【实证 A】 |
| **闭环判定** | **"下载+运行"最小序列在协议层闭环通**（下载落盘字节一致 + RUN 让寄存器变活）；**"编译产物→该产物作为运行程序"这一段因装载桩不可达**（模拟器限制，非下载传输限制）。真机需 `load_down_file_start` 非桩 + 完整下载状态机 | 混合（通/阴性分明） |

---

## 一、T1 静态：下载对话框 → 调用点 → 载荷 → 命令族

### 1.1 谁调用什么（模块边界）【实证 A，capstone IAT 扫描】

- AutoShop.exe 静态导入 `InterfaceMgr.dll` 的 `IH530Communicate`（1033 项，与 F1 §1.2 一致），
  **但不导入 CommunicateEthernet.dll / Communicater.dll**（F1 §1.1：Communicater.dll 是 CommunicateEthernet
  的唯一静态导入者）。因此 **UI 触不到 `CCommunicateEth::Download` 这个以太网通道的静态导出**。
- AutoShop.exe 的下载 IAT（`InterfaceMgr!IH530Communicate` / `CNetInterFace`）：

| 导出（IAT VA） | 调用点数 | 语义 |
|---|---|---|
| `?DownloadFile@IH530Communicate@@SAJCS..CS..CWnd*CS..mode,level@Z`（0x1EB11D8） | **31** | **UI 下载的真实入口**（按文件） |
| `?Net_Download@@YAHPAUHWND__@@@Z`（0x1EB08B0） | 1（0xbca076） | 网络工程整体下载流（`CNetInterFace::Net_Download` vtbl+0x40） |
| `?PROXY_Download@@YAHPAUHWND__@@@Z`（0x1EB08AC） | 1（0xbca08b） | 代理下载分支 |
| `?Net_StopDownload@@YAHXZ`（0x1EB08A8） | 1 | 中止下载 |
| `?UploadFile@IH530Communicate@@...@Z`（0x1EB1738 / 0x1EB13E4） | 39 / 2 | 上传面 |
| `?CreatePatchDownloadCommunicate@...@Z`（0x1EB0820） | 3 | 补丁下载工厂 |

- 转发链（与 F1 §1.3 的 `IH530Communicate` 门面形态同构）【实证】：
  `DownloadFile@IH530Communicate`（InterfaceMgr RVA 0x68EF0）取工厂单例
  `[0x101366E0]` → vtable 槽 1 得活动通信器 → **vtable 槽 +0xFC** 虚调用（本函数按
  栈位复制 3 个 CString 参数后 `call [vtbl+0xFC]`）；Communicater.dll 的下载分发器在
  基类桩位（`?Download@CCommunicater@@...@Z` RVA 0x6A040、CompleteDownloadCmd 0x6A050 是
  `ret` 桩），以太网真实体 = Communicater.dll 转发到 `CCommunicateEth::Download@0x3040` /
  `CompleteDownloadCmd@0x47E0`（F1 签名，仅 Communicater.dll 静态导入）。

### 1.2 下载对话框逐个传输哪些文件【实证 A，0xE63F00–0xE7B400 函数族】

`0x8072`（下载）命令处理进入上述函数族后，对每个编译产物文件调用一次 DownloadFile。
反汇编证据：wide 字符串 `push imm32` 的 .text 引用与紧随其后的 `call [0x1EB11D8]` 配对：

| remote 文件名（wide 字符串 VA） | push 引用点 | DownloadFile 调用点 |
|---|---|---|
| `project.hcpp`（0x1f071ec） | 0xe63f71 | **0xe64016** |
| `userprog.foid`（0x1f205d8） | 0xe64245 | **0xe64293** |
| `stprog.o`（0x1f205c4） | 0xe64437 | **0xe64485** |
| `stuserprog.o` | 0xe64666 | **0xe646b4** |
| `symbol.sym`（0x1f20590） | 0xe648f3 | **0xe64941** |
| `varsinfo.foid`（0x1f20574） | 0xe64bb2 | **0xe64c00** |
| `EIPIns.foid` | 0xe74... | 0xe74328 |

（同族文件在 0xe6b–0xe74 多个下载向导变体里重复出现，顺序一致。）

**关键判读**：
- **调用约定 = cdecl（InterfaceMgr `SA` 静态门面），不是 __thiscall**——F1 第五节的 __thiscall
  修正是针对编译入口 `0x15A2EC0`（AutoShop.exe 内部 thiscall），与这里的跨 DLL 静态门面
  `DownloadFile@IH530Communicate` 无关；后者由调用方 `add esp,0x18` 清栈（0xe6401c）。
- `EnumExecuteMode` / `EnumCommandLevel` 在这些调用点仍取 UI 一致值（mode=1/level=0，
  F1 第 3.1/3.2 节的 152 调用点结论），DownloadFile 内部再向下传。

### 1.3 "编译产物 → 可下载载荷"的最小转换【实证 + 静态】

编译管线（E3 §5.4）落盘：`Compile\<名>.compile`（UCode 加密）+ `<名>.foid` +
**重打包 `<名>.hcpp`（PPCH 容器，= app 自己的"生成下载文件"）**；`Temp\stprog.o/
symbol.sym/varsinfo.foid/userprog.foid` 是下载载荷临时件（0x8257 管线后半段）。

下载对话框把上述件**当文件逐个传**（第 1.2 节）。所以：

> **最小转换 = 直接把重打包的 `project.hcpp`（PPCH 容器）作为下载载荷**，
> 无需任何额外封装；转换函数唯一职责是守住 **PPCH magic**（拒绝把裸 `.compile/.foid`
> 当整包载荷）。工程包成员名 `net.down / net.tmp / net.up`（0x1f316e0/0x1f31724/0x1f31740）
> 是**配置下载**（"以太网配置信息下载"，S2 §10.3）用的工程包成员，不是程序下载传输帧。

对应实现：`DownloadPayloadPlan`（本文 §5）。

### 1.4 "下载族" EnumCommunicaterCmd 取值——诚实阴性【实证 A：无调用点】

- F1 §3.5 已标 `Download/Upload` 族取值【未验证】。G2 复核：AutoShop.exe 无任何按
  `Download(PB_W, EnumCommunicaterCmd, ...)` 签名取命令值的调用点（它用无 cmd 参的 DownloadFile），
  Communicater.dll 的转发器只把上游参原样透传。**故本版无 UI 证据可还原"下载族"立即数。**
- CommunicateEthernet.dll 内出现的 `0x1000/0x1001/0x1002/0x1005/0x1006` 立即数经反汇编排除：
  0x1005/0x1006 位于 `WS2_32!send`（导入 #21）调用前的固定参（0x100085A9/0x100085DB 的
  `push 0xffff; push 0x1005/0x1006`），属登录/保活 socket 语义（F1 已把 0x1005/0x1006 定为密码族，
  UI 调用点一致）；0x1000 是 `cmp` 阈值（0x1001216E `mov edx,0x1000; cmp ax,dx`），0x1001/0x1002
  是构造 CString 参时的类型/长度标记（0x10014324/0x10014722 `push 0x1001/0x1002; push <obj>;
  call <CString-ctor IAT>`）——**均非 EnumCommunicaterCmd**。
- **协议层的"下载命令族"真实身份 = serverId 4 文件传输（type 2 会话 / type 5 数据 / 0x0F 完成，
  D1）+ serverId 1 寄存器链 0xFF000010（下载/装载状态，para_len=4）+ 0xFF000020（RUN/STOP 控制字，
  para_len=4）**。这两个命令字在 h5u_daemon 的 mp_core 注册表实证（D1 §3.2），本次 §3 帧级复核。

---

## 二、实现落点

| 层 | 位置 | 职责 |
|---|---|---|
| 独立下载 harness（x86，实调通信栈） | `tools/AutoShopNativeBridge/src/g2/DownloadProbe.cpp` + `g2build.cmd` | set_target 回环 + Download/CompleteDownloadCmd/EnableUpload 受理实证（**不进 CI**，产物 `%LOCALAPPDATA%\AutoShopNativeBridge-g2-run\`） |
| 抓包代理 / D1 下载 / Modbus 观测（本地取证） | `tools/AutoShopNativeBridge/src/g2/frame_proxy.py` / `g2_download.py` / `modbus_watch.py` / `pe_tool.py` | 12939 帧捕获、字节级文件下载、寄存器活体对比（本地工具，非 CI） |
| C# 帧/载荷模型 | `src/PlcMcp.Engineering/Workers/AutoShop/InjectBridge/SimulatorControlFrames.cs` | RUN/STOP/装载触发/轮询寄存器链帧的**纯字节构造 + 应答解析**（含 0x82 桩态） |
| C# 载荷转换 | `.../InjectBridge/DownloadPayloadPlan.cs` | PPCH magic 校验 + 按厂商文件序的下载计划 + type2 会话帧构造（离线单测） |
| C# 闭环运行器 | `.../InjectBridge/AutoShopDownloadClosedLoop.cs` + `SimulatorRegisterClient.cs` | download→load→RUN→STOP 编排；可注入 channel 离线重放；生产适配器硬回环 |
| 桥 download 面（诚实） | `tools/AutoShopInjectBridge/src/bridge/BridgePipeServer.cpp`（HandleDownload）+ C# `InjectBridgeServices` | 门默认关→-32005；门开→**如实答 G2 裁决（导出受理但不传输，闭环归 D1 协议层）**，厂商导出永不实调 |
| 离线测试 | `tests/PlcMcp.Engineering.Tests/InjectBridgeTests.cs`（G2 区，12 个） | 帧字节锚定 / 载荷转换 / 回环拒绝 / 门控收据 |

---

## 三、实证记录（全对 127.0.0.1 模拟器，零物理风险）

### 3.1 实验设置【实证】

- daemon：`d1-download-workspace\Simulator\h5u_daemon.exe`（D1 的完整工作副本，安装目录零写入），
  监听 502 + 127.0.0.1:12939；工作副本 `app\project\project.hcpp` 基线 md5 = 647096e8…（"Empty\r\n"）。
- 抓包：`frame_proxy.py` 127.0.0.1:12940 → 127.0.0.1:12939，逐帧 hex 落盘 `wire-capture.log`。
- 载荷：`g2-download-closure\payload\project.hcpp` = E3 proj6 重打包下载文件，15,745 B，
  md5 = **f463cbaa9613685942ae245e525a6c9e**（PPCH 头）。
- Modbus 观测：`modbus_watch.py` 读 D100-109 / D0-9 / coils / R100。

### 3.2 (a) 独立 harness 实调 Download（阴性结论）【实证】

DownloadProbe（运行时 GetProcAddress 解析以太网通道写面，目标硬校验 127.0.0.0/8）：

```
[B] SetCommunicatConfig(127.0.0.1:12940); IsEthOnLine=1
[B] GetGCMState(0,0) rc=0 state=-1
[C] Download(..., cmd=0x1000/0x1002, mode=1, level=0) -> rc=17（0x11 拒绝；F1 观察码）
[C] Download(..., cmd=0x1002, mode=0, level=0)          -> rc=0（受理）
[C] CompleteDownloadCmd(cmd, NULL, 0, 0)                -> rc=0
[D] EnableUpload(index=0, 0, 0)                         -> rc=0
[H] 保持 8–25 s 等异步 worker 触网
哨兵实验：先经 D1 协议把 project.hcpp 改成 25 B "SENTINEL-G2-DO-NOT-REMOVE"（md5 372753ac…），
再跑上述"受理"的 Download（载荷=真 15745 B project.hcpp）25 s。
结果：daemon 侧 project.hcpp md5 仍 372753ac…（哨兵纹丝不动），wire-capture 期间
      仅一条 c1 连接 open→0 字节 close（即 IsEthOnLine 探活），无任何 type2/type5/0F 文件帧。
```

**裁决【实证】**：`CCommunicateEth::Download` 在独立进程**被受理但不传输**——异步通信 worker
缺 AutoShop UI 会话前置（同 F2 §15.3 "缺监视启动前置"象）。`DownloadPlcSoft` 独立实调直接**段错误**
（另一次运行 [E] 后进程崩溃），佐证 F1 §6.4 "猜测签名调用危及宿主"。**故进程内 vendor Download 导出
路径不可作为自动化闭环通道；闭环走 D1 协议层。**

### 3.3 (b) D1 协议下载 project.hcpp → 字节级落盘【实证 A】

`g2_download.py download "project\project.hcpp" payload/project.hcpp 256`（先登录）实测帧：

```
login  tx 0100 706c636d6370(32B 'plcmcp') 30...(32B '0')        rx 0900 0280 ... 06      [06]=OK
cmd2   tx 0200 00000000 00000000 | 00010000 | 813d0000 | "project\project.hcpp\0"
       rx 0e00 0204 00000000 06 00 | 813d0000                                          total 0x3d81=15745 回显
data0..62 tx 0500 idx len "PPCH..."                                                    rx 06 idx ACK ×N
finish tx 0f00 00000000                                                                 rx (无)
```

daemon 侧复核：
- `app\project\project.hcpp` md5 = **f463cbaa…**（与源字节完全一致，15,745 B）。
- `.hcpp` 备份舞蹈：旧件改名 `project_backup.hcpp`（D1 §四）。
- daemon 日志 `app\log\h5u_log_0.csv`：`Download project\project.hcpp start.` / `... finished.`

**结论**：编译产物（重打包 .hcpp）经协议**真实、字节一致地落进模拟器工作区**。装载轮询见 §3.4。

### 3.4 (c) RUN：寄存器真正变活（本次核心新证）【实证 A】

对 12939 寄存器链（serverId=1，payload `[sub][0][cmd@2 LE][paraLen@6=4][para@10 LE]`）实测：

```
写 0xFF000020 ← 1  (RUN)  tx 0200 200000ff 04000000 01000000   rx 0900 0201 ... 06     [06] ACK
写 0xFF000020 ← 0  (STOP) tx 0200 200000ff 04000000 00000000   rx 0900 0201 ... 06     [06] ACK
读 0xFF000010        tx 0100 100000ff 04000000 00000000        rx 0e00 0201 ... 05 00000000   status=0
写 0xFF000010 ← 1 (装载触发) tx 0200 100000ff 04000000 01000000  rx 0e00 0201 ... 0f 00 82000000   status=0x82
```

Modbus 502 读回（**RUN 前后因果**）：
- 基线（daemon 启动态，STOP）：D100-109 全 0、R100 全 0、coils 0 → 07:39:19 `ALL-ZERO`。
- 写 `0xFF000020←1` 后：D100-109[0]=**4660**、R100-101[0]=**4660** → 07:54:21 `ALIVE`。
- 写 `0xFF000020←0`（STOP）：daemon 回 ACK，D100 **保持 4660**（STOP 不清值，如实记录）。
- 新连接不登录直接写 `0xFF000020←1/0` 也回 `[06]`——RUN/STOP 命令字**不受授权门约束**
  （授权门 659076A4 只作用于文件链 serverId 4，D1 §2.5）。

**结论**：
1. **RUN 通道实证通**——`0xFF000020` 控制字是模拟器真实 RUN/STOP 入口，寄存器随之**从全 0 变活**。
   这把 D1 §七"0xFF000020 故意未实现、未实测"的空白填上（现为【实证】）。
2. **装载仍是桩**——`0xFF000010←1` 回 status **0x82**，与 D1 "load_down_file_start 恒返回 1→映射 0x82" 完全吻合；
   **下载进工作区的 project.hcpp 不被激活为运行程序**，本次变活的是**出厂默认 app** 的程序。
3. ⇒ **"编译产物→该产物作为运行程序"闭环在本模拟器不可达**（装载桩）；但**"下载落盘 + RUN 使寄存器变活"**
   这两个环节各自实证通。真机需 load_down_file_start 非桩。

---

## 四、T2 实证矩阵（汇总）

| # | 步骤 | 目标 | 结果 | 证据 |
|---|---|---|---|---|
| a1 | 独立 harness Download(mode=1) | 12940→12939 | rc=17 拒绝 | report-sweepC |
| a2 | 独立 harness Download(mode=0) | 12940→12939 | rc=0 受理，**零文件帧** | report-sweepE + wire-capture |
| a3 | 哨兵不变性 | daemon 工作区 | project.hcpp 仍 372753ac（未被 Download 覆写） | md5 前后 |
| a4 | DownloadPlcSoft 独立实调 | — | 段错误 | report-sweepD 止于 [E] |
| b1 | D1 下载 project.hcpp（15745B） | 12939 | **字节一致落盘**（md5 f463cbaa） | wire-capture + md5 + daemon 日志 |
| b2 | .hcpp 备份舞蹈 | 工作区 | 旧件→project_backup.hcpp | ls + D1 §四 |
| c1 | RUN 写 0xFF000020←1 | 12939 | [06] ACK，**D100/R100 0→4660** | frame + modbus_watch |
| c2 | STOP 写 0xFF000020←0 | 12939 | [06] ACK，值保持 4660 | frame + modbus_watch |
| c3 | 装载触发 0xFF000010←1 | 12939 | **status 0x82（桩）** | frame（D1 §3.2 复核） |
| c4 | 装载轮询 0xFF000010 读 | 12939 | status 0（未装载） | frame |

---

## 五、给集成的最小接口（下载 + 运行）

### 5.1 载荷转换（DownloadPayloadPlan）

```
ForRepackedProject(byte[] hcpp) -> [{ RemoteName="project\\project.hcpp", Bytes=hcpp 原样 }]
    — 守 PPCH magic；非容器 blob（裸 .compile/.foid）抛 ArgumentException（拒绝当整包载荷）
ForFullProject(hcpp, {symbol.sym/stprog.o/...}) -> 按厂商序 project.hcpp→userprog.foid→stprog.o→
    stuserprog.o→symbol.sym→varsinfo.foid，成员缺失可容忍（LD-only 工程无 userprog.foid）
BuildSessionFrame(file, chunk) -> D1 type2 会话帧（chunkSize@0x0A / total@0x0E / filename@0x12）
```

### 5.2 "下载 + 运行"最小调用序列（协议层，闭环通的两步）

```csharp
// 前置：目标硬回环（DownloadProtocolClient.Create / SimulatorRegisterClient.Create 构造即拒非回环）
var dl = DownloadProtocolClient.Create("127.0.0.1", 12939);
await dl.ConnectAsync();
await dl.LoginAsync("plcmcp", "0");                        // .hcpp 受保护，须同连接登录（D1 §2.5）
var plan = DownloadPayloadPlan.ForRepackedProject(hcppBytes);
var res  = await dl.DownloadFileAsync(plan[0].RemoteName, plan[0].Bytes, 256);  // 字节一致落盘
// 用同一连接/新回环连接发 RUN 控制字（0xFF000020←1）——寄存器变活：
using var ctl = SimulatorRegisterClient.Create("127.0.0.1", 12939);
var run = await ctl.RunAsync();                            // 应答 [06] = accepted
```

`AutoShopDownloadClosedLoop.RunAgainstDaemonAsync(dl, ctl, plan)` 封装上述
（download→触发装载→RUN→可选 STOP），逐步回执，**装载步如实回 0x82 桩态、下载/运行步如实回**，
任何一层都不把桩说成成功。

### 5.3 门控建议

- **下载/运行是 PLC 动作**，按 contracts.md 第 56–58 行走 **DeploymentPlan + 审批门控**；
  默认仅回环/模拟器可自动放行，真机目标必须显式审批配置。
- 进程内 vendor `CCommunicateEth::Download` 导出层保持 **Unsupported / 永不实调**（§3.2 死路 +
  DownloadPlcSoft 崩溃实证）；闭环通道**只走 D1 12939 协议层**（DownloadProtocolClient +
  SimulatorRegisterClient，二者均硬回环）。
- 桥 `HandleDownload`：门关→-32005；门开→如实 G2 裁决文本（§3.2），C# 侧映射 `HonestUnsupported=true`
  （测试 `Services_WriteReservation_Download_GateOpen_BridgeG2Receipt_...` 锚定）。

### 5.4 能力声明（诚实）

| 能力 | 声明 | 依据 |
|---|---|---|
| `autoshop_download_transfer` | **Experimental**：回环模拟器字节级落盘实证（project.hcpp md5 一致）；真机未验 | §3.3 |
| `autoshop_run_stop` | **Experimental**：模拟器 RUN/STOP 控制字实证通，寄存器 0→4660 变活；真机未验 | §3.4 |
| `autoshop_program_load` | **Unsupported（模拟器）**：load_down_file_start 桩，status 0x82，下载件不激活 | §3.4，D1 |
| `autoshop_download_via_vendor_export`（进程内） | **Unsupported**：导出受理但不传输（独立），DownloadPlcSoft 危及宿主 | §3.2 |
| `EnumCommunicaterCmd 下载族取值` | **未还原**：UI 不按该签名调用（走 DownloadFile），无调用点立即数 | §1.4 |

**禁止**：宣称"编译产物已作为运行程序装载"（装载桩）；把 RUN 的 4660 当作下载件运行值（那是默认 app）；
把进程内 Download 回执当下载成功。

---

## 六、真机路径差距（若闭环在本模拟器不通的部分）

- **装载**：真机 `load_down_file_start`（本项目为桩 ret 1）应真实 mmap 下载件；下载→0xFF000010 触发
  →0xFF000010 轮询到 done→寄存器按**下载的程序**变活。当前模拟器只能证明"下载传输"与"RUN 控制字"两段。
- **EnumCommunicaterCmd 下载族**：真机若有按 `Download(path,cmd,...)` 的调用面，可据调用点立即数补 F1 §3.5 空白；
  本版无 UI 证据。
- **CRC / 授权**：D1 实证下载帧无 CRC、授权仅文件链；真机 check_userlogin_default/哈希可能不同。
- **版本漂移**：全部 RVA/IAT/立即数锁 4.12.0.2（RELOCS_STRIPPED 无 ASLR，升级即失效）。

---

## 七、T4 离线测试（全绿）

`InjectBridgeTests.cs` 新增 G2 区 **12 个**（全离线，无 AutoShop/无真 daemon）：
- 帧字节锚定：RunFrame / Stop+LoadFrames / SessionFrame / RegisterSubType 校验（对齐 §3.3/§3.4 实测 hex）
- 应答解析：Ack / 0x82 桩态 / Value / Unknown（ParseReply）
- 载荷转换：ForRepackedProject 字节一致 + 拒非容器 + ForFullProject 厂商序 + 会话帧边界
- 闭环运行器：ScriptedControlChannel 重放（装载 0x82 / RUN/STOP ACK，3 帧）+ 下载失败不发 RUN + 空计划拒绝
- 回环硬拒：`SimulatorRegisterClient.Create("8.8.8.8")` 抛（RUN 帧绝不发非回环）
- 门控收据：桥 download 门开 → -32006 G2 裁决 → C# `HonestUnsupported`

原生桥重建后 `BridgeTestHost` 仍 **14/14 PASS**（含 download 门关 -32005）。
`dotnet build PlcMcp.slnx -c Release` 0 错误；`dotnet test` Engineering 365 通过 / Server 383 通过 / 0 失败。

---

## 八、红线合规声明

- **零物理 PLC**：全部实验仅触达 127.0.0.1 模拟器（502/12939）与本地文件分析。
- **回环硬校验**：DownloadProbe 在 SetCommunicatConfig 前拒非 127.0.0.0/8；frame_proxy/g2_download/
  modbus_watch/SimulatorRegisterClient/DownloadProtocolClient 全部仅 127.0.0.1。下载帧从未发往非回环。
- **安装目录零写入（复核）**：实验前杀掉了兄弟代理遗留的**安装目录** daemon（与本次工作区 daemon 双绑同端口，
  为确定性实验所必须）；实验后复核 `D:\Inovance Control\AutoShop\Simulator\app\stprog.o`（b0616e43…）、
  `app\project\project.hcpp`（647096e8…）md5 与 mtime（2026-07-21）均**未变**——本次下载帧只落进工作区副本。
- **工作区回滚**：实验结束把工作区 `app\project\project.hcpp` 恢复为基线 "Empty\r\n"（md5 647096e8…）。
- **进程清理**：自启 daemon（PID 58520）taskkill，frame_proxy（PID 19264）停止；复核 502/12939/12940
  无监听、无 h5u_daemon/AutoShop 进程残留。
- **构建产物不入仓库**：DownloadProbe.exe / vendor DLL 副本 / 日志均在 `%LOCALAPPDATA%`、`%TEMP%\plcmcp-handoff\`。
- **全部结论均为逆向所得，无汇川厂商背书；版本锁定 4.12.0.2 / x86。**
