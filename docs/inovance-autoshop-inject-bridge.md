# 汇川 AutoShop 进程内注入桥（E2）实证与交付文档

> 本文档固化阶段三 E2（进程内注入桥）的架构、操作面、门控规则与安全红线。
> 注入桥把"官方软件能力"变成 MCP 功能的执行通道：向一个**由注入器自己启动的**
> AutoShop 实例注入自有 DLL，DLL 在 AutoShop 进程内经命名管道应答操作请求，
> 直接调用进程内已加载的通信栈（CommunicateEthernet.dll / InterfaceMgr.dll）——
> 完全绕开本机被过滤的 UI 输入（阶段二实证：SendInput/WM_COMMAND 全部无效）。
>
> **所有接口均为逆向所得，无汇川厂商背书；版本锁定 AutoShop 4.12.0.2 / x86。**
> 标注约定沿用 S2/D3 文档：【实证】= 亲自执行并复核；【推断】= 基于实证的推理；
> 【未验证】= 无直接证据，禁止当作事实。能力声明纪律沿用 docs/contracts.md。

对应实现：

| 层 | 位置 |
|---|---|
| 注入器（x86） | `tools/AutoShopInjectBridge/src/injector/AutoShopInjectBridgeInjector.cpp` |
| 桥 DLL（x86） | `tools/AutoShopInjectBridge/src/bridge/`（DllMain + 5 导出 + 管道服务 + 厂商解析） |
| 验证 harness | `tools/AutoShopInjectBridge/src/host/BridgeTestHost.cpp`（构建产物 `%LOCALAPPDATA%\AutoShopInjectBridge-run\`，不入仓库） |
| 构建脚本 | `tools/AutoShopInjectBridge/build.cmd`（VS2022 v143 x86，已实证复现） |
| C# 客户端/服务/Worker | `src/PlcMcp.Engineering/Workers/AutoShop/InjectBridge/` |
| 离线测试 | `tests/PlcMcp.Engineering.Tests/InjectBridgeTests.cs`（43 个，含 1 个真实注入 env-gated 跳过项） |

---

## 一、结论摘要（先读这一节）

| 项 | 结论 |
|---|---|
| 注入链路 | **CreateProcessW(CREATE_SUSPENDED) → 写 pending-\<pid\>.json → CreateRemoteThread(LoadLibraryA) → CreateRemoteThread(Bridge_Init) → ResumeThread**；注入只发生在注入器自己创建的进程上，**不存在"注入任意 PID"模式**【实证：注入器动词面仅 launch/stop/eject/status】 |
| 防注入错误进程 | 注入前对 exe 与 DLL 做 PE 校验（MZ/PE 头、IMAGE_FILE_MACHINE_I386、基础名必须为 AutoShop.exe）；C# 侧 Options 再锁一次文件名【实证：测试 Launcher_Options_Validate】 |
| PID 归属 | 启动记录（PID + exe 路径 + 进程创建时间 + 记录时间）落盘 JSONL 日志；stop/eject 对每个 PID **重新核验活体身份**（镜像路径 + 基础名 + 创建时间，容差 10s），任何不匹配即拒绝且**永不 kill**【实证：Launcher_Stop_KillsOnlyOwnershipVerifiedInstances】 |
| 通信 | 命名管道 `\\.\pipe\PlcMcpAutoShopInjectBridge`，字节模式、单客户端、一行一 JSON（UTF-8/LF）；**PIPE_REJECT_REMOTE_CLIENTS**（只允许本机连接）+ 默认 DACL（宿主用户）【实证：BridgeTestHost 全链路】 |
| 回环纪律 | 桥在任何厂商通信栈调用前**必须**先 `set_target` 存入 127.0.0.0/8 目标；`set_target` 对非回环地址**先拒绝后存储**。厂商默认目标 192.168.1.1:12939 因此**不可能**被本桥触达【实证：harness set_target 8.8.8.8 → -32003】 |
| 写面（预留） | write_elements/force/download 的完整管线（C# 门 → 桥门 → 回环校验 → 导出解析 → SEH）已实现；**厂商导出只解析、不调用**（应答 `resolved:true, invoked:false` + 明确理由）——S2 实证 WriteElemValue/Unforce/ProgramValidate 共享增量链接 thunk RVA 0x37A0，尾参语义未还原，猜测签名调用会危及宿主【实证：export 表扫描；诚实应答】 |
| force/download 默认 | 桥侧门默认关闭（allowForce=false / allowDownload=false）→ **直接应答 Unsupported（-32005）**，不进入解析步骤【实证：harness capabilities/force/download】 |
| 编译（E3 槽） | 桥配置 `compileEntryRva` 槽位已预留；默认未配置 → **Unsupported（-32006）**并注明"E3 交付后接线"【实证：harness compile】 |
| 崩溃防护 | 每个请求处理器经 SEH 包裹（`HandleLineSafe`）：厂商侧异常 → 结构化错误 -32004，宿主进程存活【实证：实现 + 纪律说明；注入宿主内的实弹验证属真实注入路径】 |

---

## 二、注入时序（架构图）

```
 C# InjectLauncher                    AutoShopInjectBridgeInjector.exe (x86)          AutoShop.exe (x86)
 ================                     ======================================          ==================
 StartAsync()
   |-- Discover()                 验证 injector/dll/exe 存在
   |-- InjectorRunner("launch …")
   |        |
   |        +------------------> 1. ValidateImage(exe, "AutoShop.exe")  PE: MZ/PE/MACHINE_I386
   |                             2. ValidateImage(dll)                  PE: MZ/PE/MACHINE_I386
   |                             3. CreateProcessW(exe, CREATE_SUSPENDED)  -->  [主线程挂起]
   |                             4. 写 %TEMP%\plcmcp-handoff\inject-bridge\
   |                                pending-<pid>.json（管道名 + 桥侧门控默认值）
   |                             5. VirtualAllocEx + WriteProcessMemory(dll 路径)
   |                                CreateRemoteThread(kernel32!LoadLibraryA)
   |                                                                    -->  [桥 DLL 映射，DllMain 仅记录]
   |                             6. 本地解析 DLL 导出表取 Bridge_Init RVA
   |                                CreateRemoteThread(remoteBase+RVA)
   |                                                                    -->  Bridge_Init():
   |                                                                          读 pending-<pid>.json（消费并删除）
   |                                                                          启动管道服务线程
   |                             7. ResumeThread(主线程)              -->  [AutoShop 正常启动，桥已就绪]
   |                             8. WaitNamedPipeW 轮询管道就绪
   |                             9. 追加归属记录到 owned-instances.jsonl
   |                             10. stdout 输出 {"event":"launched","pid":…}
   |-- 解析 JSON 事件
   |-- InjectBridgeClient → 命名管道 handshake（身份核验）
   |-- 成功：记入内存 owned 列表；失败：StopAsync() 清理自己启动的实例
```

失败回滚：步骤 4–7 任何一步失败，注入器 **TerminateProcess 自己刚创建的子进程**、
删除 pending 配置并输出 `{"event":"failed",…,"rolledBack":true}`；C# 侧在注入器
已报告成功但握手失败时，也用归属核验后的 tree-kill 清理该实例。

## 三、操作面表（管道方法白名单）

| 方法 | 参数 | 前置门控 | 应答 |
|---|---|---|---|
| `handshake` | `{client}` | 无（不触厂商） | workerName/workerVersion/protocolVersion/vendor/bitness/runtimeEnvironment/**pid**/capabilities/disclaimer |
| `capabilities` | — | 无 | 能力矩阵（桥侧门状态逐项声明，见第五节） |
| `set_target` | `{ip, port}` | **仅 127.0.0.0/8**，先拒绝后存储 | `{stored, ip, port, loopbackOnly}`；非回环 → `-32003` |
| `read_status` | — | 厂商模块已加载 + 回环目标已设 | execMode/gcmState **调用回执**（-32001/-32003 前置缺失） |
| `read_elements` | `{addresses}` | 同上 | count + 调用回执；空 map 调用（AddElem 基类桩，无法填元件），**不解码任何值** |
| `write_elements` | `{addresses, values?, target?}` | C# 门 + 桥门 allowWrite + 回环 | **resolved:true / invoked:false** + 导出名/RVA + 理由；target 非回环 → `-32003` |
| `force` | `{addresses, …}` | 桥门 allowForce（默认关）→ `-32005` | 开门后同 write_elements 的解析不调用语义 |
| `download` | — | 桥门 allowDownload（默认关）→ `-32005` | 开门后如实回答"下载载荷工作流未接线（D1 层）" `-32006` |
| `compile` | — | E3 槽 `compileEntryRva`（默认空）→ `-32006` | 配置了地址但签名未验证 → 仍 `-32006` |
| `shutdown` | — | 无 | `{stopping:true}` 并停止服务 |

未知方法 → `-32601`；缺 method → `-32600`。**白名单之外的一律不尝试**。

结构化错误码（两侧镜像）：

| 码 | 含义 |
|---|---|
| -32600 / -32601 / -32602 | 缺 method / 未知方法 / 参数缺失 |
| -32001 | 厂商模块/导出不可用（不在 AutoShop 进程内或导出缺失） |
| -32002 | 厂商调用返回错误 |
| -32003 | 非回环目标拒绝 / 未设置回环目标 |
| -32004 | 桥内 SEH 异常（宿主存活） |
| -32005 | 桥侧门关闭（force/download 默认） |
| -32006 | 诚实 Unsupported（compile 槽 / 下载工作流） |

## 四、原生 API 与 C# API

### 4.1 桥 DLL 平面 C 导出（5 个，x86 cdecl，未修饰名）

| 导出 | 用途 |
|---|---|
| `DWORD Bridge_Init(void*)` | 远线程入口：读 pending-\<pid\>.json（或默认值）并启动管道服务线程；0=成功 |
| `DWORD Bridge_SetPipeName(const char*)` | 仅手动（harness）模式：服务启动前覆盖管道名 |
| `DWORD Bridge_Pump(int timeoutMs)` | 仅手动模式：一次 accept/handle 周期（1=处理，0=空闲，-2=线程模式运行中） |
| `void Bridge_Shutdown(void)` | 停服务线程、释放缓存句柄（幂等） |
| `const char* Bridge_Version(void)` | 版本串 |

`DllMain` 只记录实例句柄 + DisableThreadLibraryCalls——全部服务启动都发生在
`Bridge_Init`（注入器远调用）或手动 Pump 中，规避 loader-lock 风险。

### 4.2 注入器动词面

```
launch --exe <AutoShop.exe> --dll <bridge.dll> [--pidfile <path>] [--pipe <name>]
       [--args "<AutoShop 命令行>"] [--allow-write 1] [--allow-force 0] [--allow-download 0]
       [--compile-rva <hex|none>] [--wait-pipe-ms 15000] [--inject-timeout-ms 15000]
stop   --pidfile <path> [--tree] [--pid <pid>]     # 只杀归属核验通过的 PID
eject  --pidfile <path> --dll-name <name.dll> [--pid <pid>]
status --pidfile <path>
```

stdout 仅一行 JSON 事件（C# 解析权威）；进度日志走 stderr。退出码：
0 成功 / 1 用法 / 2 校验 / 3 创建进程 / 4 注入 / 5 初始化 / 6 管道等待 / 7 归属拒绝。

### 4.3 C# 侧（`Workers/AutoShop/InjectBridge/`）

| 文件 | 职责 |
|---|---|
| `InjectBridgeProtocol.cs` | 协议常量、方法白名单（6 个非门控 + 4 个门控）、请求/响应模型、错误码 |
| `InjectBridgeGateOptions.cs` | **C# 主门**：AllowElementWrite/AllowForce/AllowDownload 默认全关；CompileEntryHint（E3 槽）；RequireLoopbackTarget 不可关（Validate 拒绝）；`IsLoopbackAddress`（127.0.0.0/8 + localhost） |
| `InjectBridgeClient.cs` | 管道客户端：非门控方法走白名单；门控方法按门放行；**非回环 target/ip 本地拒绝（不碰管道）**；超时/断链/坏帧一律失败；裸字节读写 + 手工分行（S2 已证 StreamReader 跨管道挂死） |
| `InjectBridgeServices.cs` | 门面：Handshake/Capabilities/SetTarget/ReadStatus/ReadElements/WriteReservation/Compile；门关闭 → 本地 HonestUnsupported 报告（不触管道）；桥的 -32005/-32006 映射为 HonestUnsupported |
| `InjectLauncher.cs` | 启动/注入/握手核验/停止；发现检查；注入器 runner、活体身份查询、树杀、握手探针全部可注入（离线可测）；归属日志读写 |
| `InjectBridgeWorker.cs` | `IEngineeringWorker`：ExternalWorkerSecurityPolicy 硬拒绝（Download/RunStop/Force/HmiPublish 及关键词扫描）先于一切管道流量；只接受 InspectProject；结果最高 Experimental |

## 五、门控规则与能力矩阵

**三层防线**（写入面）：

```
MCP 审批（E4/S4，默认关） → C# 门 InjectBridgeGateOptions（默认全关）
  → 桥 DLL 门 pending 配置（allowForce/allowDownload 默认 false）
    → 回环硬校验（set_target 仅 127.0.0.0/8；写请求 target 参数非回环即拒）
      → 厂商导出解析（存在性证明）
        → 【当前终态】签名验证闸门：尾参语义未还原 → 永不调用
```

能力矩阵（桥 `capabilities` 应答，`Harness 实证`原文）：

| 能力 | 默认声明 | 打开条件 |
|---|---|---|
| `comm_stack_read_only` | Experimental：进程内直调 CommunicateEthernet.dll（S2 签名）；回环优先纪律；无 PLC 实证 | set_target(127.0.0.1) + 模拟器在环 |
| `variable_read` | Unsupported：无已验证的 publisher/连接 | 工程通信配置解析落地后 |
| `element_write` | 默认门关 → Unsupported；C# 门开 → Experimental（预留）：C# 门 + 回环 + 导出解析已实现，厂商调用被签名验证闸门阻断 | 操作员开 C# 门 + 桥门 + 回环目标 + **E 写面签名还原与验证**（后续代理） |
| `force` | Unsupported：桥门默认关（allowForce=false） | 操作员决定 + S4 审批门控 + 签名验证 + 回环 |
| `download` | Unsupported：桥门默认关（allowDownload=false） | 操作员决定 + S4 审批门控 + 下载载荷工作流（D1 12939 层） |
| `compile` | Unsupported：E3 入口槽预留（无已验证地址） | E3 交付编译入口地址 + 签名证据 → 由集成代理接线 compileEntryRva |
| `run_stop` | Unsupported | 未在任务范围 |

诚实纪律：所有"预留/门关"应答都**原样**声明 Unsupported 或 resolved-not-invoked，
C# 侧映射为 `HonestUnsupported=true` 的报告，任何一层都不会把预留说成已执行。

## 六、安全红线（本桥的实现即红线）

1. **只注入自己启动的实例**：注入器没有"注入已有 PID"动词；注入目标 = 注入器自己
   CreateProcess 出来的进程。用户交互中的 AutoShop 实例在架构上不可达。
2. **归属保护 + 干净终止**：启动记录（PID/exe/创建时间）落盘
   `%TEMP%\plcmcp-handoff\inject-bridge\owned-instances.jsonl`；stop/eject/C# StopAsync
   对每个 PID 重验活体身份（镜像路径 + 基础名 + 创建时间 ±10s），不匹配即拒绝不杀；
   C# 停止用整树 kill（对齐 taskkill /T 语义），树扩展仅限镜像位于记录安装目录下的子进程。
3. **回环硬校验双保险**：C# 客户端本地拒绝非回环 target/ip（不碰管道）；桥 DLL 在
   `set_target` 存储前再拒一次。厂商默认目标 192.168.1.1:12939 不可达。
4. **网络仅本机命名管道**：PIPE_REJECT_REMOTE_CLIENTS + 默认 DACL；桥内无任何
   主动外连（厂商通信栈流量仅发往已设置的回环目标）。
5. **不修改安装目录 / 不写注册表**：桥与注入器唯一的文件写入是
   `%TEMP%\plcmcp-handoff\inject-bridge\`（pending 配置 + 归属日志 + harness 报告）。
6. **崩溃防护**：每个操作 SEH 包裹，异常 → 结构化错误，宿主不崩。
7. **对 PLC 零动作（默认）**：写/强制/下载三层门默认全关；即便全部打开，交付态的
   签名验证闸门仍保证厂商写导出**不被调用**。

## 七、构建与验证（实证记录）

```bat
tools\AutoShopInjectBridge\build.cmd        :: VS2022 v143 x86
cd %LOCALAPPDATA%\AutoShopInjectBridge-run
BridgeTestHost.exe                          :: 14 项检查全 PASS（0 failures）
```

- 【实证】2026-10-28 本机构建复现：AutoShopInjectBridge.dll / AutoShopInjectBridgeInjector.exe /
  BridgeTestHost.exe 全部产出，0 编译错误。
- 【实证】BridgeTestHost：5 导出解析、handshake、capabilities（compile/force/download =
  Unsupported）、set_target 8.8.8.8 → -32003、厂商缺位 → -32001、门关 → -32005、
  compile → -32006、未知方法 → -32601、shutdown → stopping:true，全部 PASS。
- 【实证】注入器 status/stop 对空日志的行为（不崩溃、报告空记录）。
- 【实证】C# 测试 43 项（协议 4、门控 3、客户端 9、服务 13、Worker 5、Launcher 8 +
  真实注入 env-gated 跳过 1），全部离线。
- 【未验证（如实声明）】真实 AutoShop 实例内的注入闭环（需要本机 AutoShop 可执行 +
  `PLCMCP_LOCAL_SIMULATOR=1` 环境变量；测试 `RealInjection_AgainstLocalAutoShop_*` 已就位）。
  桥内厂商调用在宿主内的实弹行为（SEH 捕获、异步分发回执）待该路径首次运行。

## 八、与 E3 / E4 的接口约定

### 8.1 给 E3（编译管线逆向）的槽位

- 桥配置 `compileEntryRva`（pending-\<pid\>.json / 注入器 `--compile-rva <hex>`）已预留。
- E3 交付已落地：handoff.json **`openCompile`** 键 + `docs/inovance-autoshop-open-and-compile.md`
  ——编译链 = WM_COMMAND 0x80E1 → handler 0xA847A0 → 单例 0x1581A70 → 0x1581CA0 →
  0x15A2EC0（无参 thiscall，经 0x4179A0 异步提交）；另给出"最小可打开工程"构造法
  （UCode 加密 .hcp + 同目录声明文件）。集成代理接线时：
  1. 把编译入口地址/RVA 写入注入器调用参数（或桥默认配置）；
  2. **必须同时交付签名已验证的证据**（参数语义 + 反汇编锚点）——桥的
     `HandleCompile` 只有在"地址已配置 + 签名已验证"两个条件都成立时才允许从
     诚实 Unsupported 切换为受门控调用；当前代码在 `compile` 处理器留有明确注释位；
  3. 注意 E3 的红线：.hcp 打开时**就地解密**（文件被消耗），任何 open 实验必须用副本。

### 8.2 给 E4（MCP 集成）的接线说明

```
InjectLauncher.StartAsync()        → 启动+注入+握手核验（返回 pid/pipeReady）
InjectBridgeServices.HandshakeAsync() / CapabilitiesAsync()
                                   → 环境与能力证据（诚实矩阵）
InjectBridgeServices.SetTargetAsync("127.0.0.1", 12939)
                                   → 回环目标（模拟器闭环前置）
InjectBridgeServices.ReadStatusAsync() / ReadElementsAsync("D100,M0")
                                   → 只读监视（调用回执级，Experimental）
InjectBridgeServices.WriteReservationAsync(method, addresses, target)
                                   → 写面预留：门关 → HonestUnsupported（报告门条件）
InjectLauncher.StopAsync()         → 实验实例干净终止（归属核验 + 整树 kill）
InjectLauncher.StopFromJournalAsync() → 崩溃恢复清理路径
InjectBridgeWorker                 → 已按 ExternalWorkerProtocol 姿态就绪（只接受
                                     InspectProject，硬拒绝危险操作，最高 Experimental）
```

各能力的门控打开条件（E4 注册工具时如实呈现）：

| 工具 | 默认 | 打开条件 |
|---|---|---|
| 注入启动/停止（实验实例） | 需显式开关 | 原生产物存在 + 归属日志路径可写；**无需**写面门 |
| 只读监视（read_status/read_elements） | Experimental | 显式开关 + set_target 回环 |
| 写元件 | Unsupported（门关） | `InjectBridgeGateOptions.AllowElementWrite=true` + 桥侧 allowWrite + 回环目标 + S4 审批；交付态仍受签名闸门阻断 |
| 强制 | Unsupported（门关） | `AllowForce=true` + 注入器 `--allow-force 1` + S4 审批 + 签名验证 |
| 下载 | Unsupported（门关） | `AllowDownload=true` + `--allow-download 1` + S4 审批 + D1 载荷工作流 |
| 编译 | Unsupported（E3 槽空） | E3 交付地址与签名证据 + 集成接线 |

建议能力声明：`autoshop_inject_bridge_read_only` = Experimental（调用回执级）；
写/强制/下载/编译 = Unsupported（门控未开 / 预留），**禁止**宣称"支持写入/下载/编译"。

### 8.3 与其他桥的关系

- **S2 原生插件桥**（`tools/AutoShopNativeBridge`）：只读垫片（静态导入）+ 插件契约；
  本桥**复用其签名与回执语义**，但写面/注入路径完全独立（S2 垫片从链接层就不可能调用写导出）。
- **D1 12939 下载协议 / D3 工程解密**：下载载荷工作流归属 D1 层；本桥 download 面
  在门打开时也只如实回答"未接线"。
- **P2 模拟器**：`set_target(127.0.0.1, 12939)` 与 h5u_daemon 闭环兼容——桥内读链路
  的预期验证路径即"自启实例 + 本桥 + 回环模拟器"。

## 九、handoff 与证据链

- `%TEMP%\plcmcp-handoff\handoff.json` 新增 **`injectBridge`** 键（本文档结论的机器可读摘要）。
- `%TEMP%\plcmcp-handoff\inject-bridge\`：归属日志 `owned-instances.jsonl`、
  pending 配置（短暂存在，桥消费即删）、`harness-report.txt`（BridgeTestHost 输出）。
- 原生产物不入仓库（对齐 S2 纪律）；仓库只含源码 + 构建脚本 + 本文档。

## 十、已知限制（禁止当作事实）

1. **版本锁定 4.12.0.2 / x86**：厂商导出签名/地址随版本漂移；`ResolveAll` 按 S2 已证
   修饰名解析，升级后解析失败会如实回答 -32001（不会猜）。
2. **写面未实弹**：签名验证闸门是有意为之（re-notes RVA 共享证据）；E 写面签名还原后
   才能打开"真调用"，当前所有写应答均为解析证据 + 明确 not-invoked。
3. **读链路数值**：`read_elements` 在基类 AddElem 桩限制下无法填元件——回执不代表
   读到值；与 S2 文档第五节结论一致。
4. **单实验实例**：默认管道名固定，注入器与 C# 启动器以串行化（信号量）保证同时只有
   一个实验实例占用管道；多实例需经 `--pipe`/`Bridge_SetPipeName` 自定义名（已支持）。
5. **真实注入闭环**：测试与环境变量门就位，待本机首次实跑（见第七节）。
