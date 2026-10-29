# AutoShop H5U 模拟器（h5u_daemon）自动化层

状态：**已实证 + 自动化就绪**（2026-10-24，P2）。本文取代 handoff.json 中 S3 旧的
`simulator.exists=false` 结论（见下文「结论修订」）。

## 结论修订

S3 当时因 UI 输入被拦无法打开"在线模拟"菜单，写下 `simulator.exists=false`。
协调人后续实测推翻了该结论：模拟器是一个**独立守护进程**，与 AutoShop 主程序/UI
无关，可直接启动。P2 在此事实之上实现启动器/探活并完成闭环验证（证据均标注日期）。

## 快速启动序列

```powershell
# 1. 启动（无需 AutoShop UI、无需管理员；必须以 Simulator 目录为工作目录）
Start-Process -FilePath "D:\Inovance Control\AutoShop\Simulator\h5u_daemon.exe" `
    -WorkingDirectory "D:\Inovance Control\AutoShop\Simulator"

# 2. 健康检查：两个端口都必须可连
#    127.0.0.1:502   Modbus TCP
#    127.0.0.1:12939 AutoShop 原生协议
# 3. 停止：只停自己启动的实例（见 SimulatorLauncher 的归属保护）
```

启动后 daemon 驻留；stdout 逐行打印 `.\default\app\...` 文件清单并以
`复制了 14 个文件` 结束（P2 于 2026-10-24 用重定向日志复核，样本：
`%LOCALAPPDATA%\Temp\plcmcp-handoff\h5u_daemon.log`）。仓库代码不依赖这段输出，
健康判定只看端口。

## 端口表

| 端口 | 协议 | 绑定 | 用途 | 证据 |
|---|---|---|---|---|
| 502 | Modbus TCP | 0.0.0.0:502 | FC1/2/3/4 读 + FC5/6 写；unit 不敏感（1/255 均应答） | 协调人 2026-10-24 原始 python 实测；P2 2026-10-24 FC3/FC1 复测 |
| 12939 | AutoShop 原生协议 | 127.0.0.1:12939 | 厂商通信栈（CommunicateEthernet.dll / CommStackShim） | TestHost `--target=127.0.0.1:12939` → `IsEthOnLine=1 (ONLINE)`，P2 2026-10-24 复跑 73–112 ms |

寄存器映射（仓库既有 `InovanceSoftAddress` 表，P2 实测复核）：D→HR0、M→coil1024、
X→DI0（FC2）、Y→coil0、R→HR32768。程序未运行时寄存器全 0。

## 交付代码（仓库）

`src/PlcMcp.Engineering/Workers/AutoShop/Simulator/`：

- **SimulatorLauncher**（T1）：发现 / 启动 / 健康检查 / 干净停止。
  - `Discover()` — exe 存在性 + 工作目录报告。
  - `CheckHealthAsync()` — TCP 探活 502+12939（两者皆通才算健康）+ 自有实例存活。
  - `StartAsync()` / `EnsureRunningAsync()` — 健康则复用（外来实例标记 `Owned=false`，
    绝不停止）；不健康才启动，记录 PID/进程名/创建时间，轮询端口直至超时；
    超时则只清理自己刚启动的实例。
  - `StopAsync()` — 只停**本启动器实例记录在案**的进程；停止前对活进程重验
    PID+进程名+创建时间，任一不符（PID 复用/改名）即拒绝并记录 `Refused`，绝不裸杀。
  - 全部探针可注入（`fileExists` / `processStarter` / `tcpProbe`），测试完全离线。
  - 回环红线编码在代码里：非 127.0.0.1/localhost/::1 的主机名在选项校验即抛异常。
- **SimulatorProbe**（T2）：只读 Modbus 探测。
  - `BuildReadRequest` 只接受 FC1/2/3/4，**任何写功能码直接抛异常**（写帧在结构上不可能产生）。
  - `ProbeHoldingRegistersAsync` / `ProbeCoilsAsync` — 报告值 + 往返时延（write→response）+ 质量码。
  - `ProbeNativeAsync` — 封装 `%LOCALAPPDATA%\AutoShopNativeBridge-run\TestHost.exe
    --target=127.0.0.1:12939`（S2 的只读 CommStackShim 探针）；二进制缺失则显式
    `Skipped=true` 并给出原因，绝不静默。
- 测试：`tests/PlcMcp.Engineering.Tests/AutoShopSimulatorTests.cs`（46 个用例：
  43 离线 + 3 个 `LocalSimulator` 实机用例）。

## 闭环验证（T3，实跑记录 2026-10-24 10:12）

脚本：`%LOCALAPPDATA%\Temp\plcmcp-handoff\simulator-closedloop.ps1`
（交接目录，**不进 CI**）。链路：Launcher 健康检查/按需启动 → SimulatorProbe →
`InovanceModbusTcpAdapter`（MCP 服务器同款适配器）→ TestHost 原生探活 → 独立
PowerShell 原始 Modbus 读复核。零写入（FC1/FC3 only）。

实跑结果（daemon=PID 36636，协调人实例，复用未杀）：

| 读点 | 帧 | 值 | 时延 | 质量 |
|---|---|---|---|---|
| D100 (HR100, Int32) | FC3 count2 | 0, 0 | 15.7 ms | good |
| R0 (HR32768) | FC3 count1 | 0 | 6.9 ms | good |
| M0..M2 (coil1024) | FC1 count3 | **1**, 0, 0 | 8.8 ms | good |

.NET 链（`simulator-closedloop-dotnet.json`）：Probe HR0/HR100/HR32768 = 0
（4.9–12.2 ms，Good），coil1024 = 1（11.8 ms，Good）；
适配器读 D100=0 / **M0=True** / X0=False / R100=0，全部 quality=Good。
M0=True 是协调人早前 FC5 写实验遗留的线圈状态——恰好证明"写→读回"状态一致。
原生探针：TestHost exit 0、ONLINE、73–112 ms。

测试门控：`[LocalSimulatorFact]` + `[Trait("LocalSimulator","1")]`，仅当
`PLCMCP_LOCAL_SIMULATOR=1` 时运行，否则跳过（不是失败）。设置
`PLCMCP_SIMULATOR_RESULT_PATH` 可让实机用例把详细证据 JSON 写到指定路径
（闭环脚本用；不设则不写任何文件）。

## 限制（如实）

- 模拟器内工程默认 **STOP** 态：数据寄存器不扫描刷新，读值恒为初始/遗留值。
- **RUN 命令未逆向**：`h5u_daemon.exe` 的运行/停止控制通路（原生协议写命令或
  `AutoShop.exe -Debug` 之类 CLI）均未验证；本层不提供任何写/RUN 能力。
- **真机未验**：全部结论来自 127.0.0.1 模拟器；`InovanceSoftAddress` 映射表
  对真硬件是否成立仍待真机资格认证。
- daemon 对 unit id 不敏感（1/255 均应答），生产 profile 仍建议固定 unit=1。
- 模拟器状态是**有副作用的持久会话**：实验写入会残留（如 M0=1），下次读值
  与全 0 预期不符时先查历史实验。
- TestHost 属 S2 的只读 harness，每次运行会覆写交接目录的 `m1-harness-report.txt`。

## 红线遵循

只运行厂商自带二进制（h5u_daemon.exe、TestHost.exe→厂商 DLL）；未装驱动、未改系统、
未改安装目录；全部实验限 127.0.0.1（回环校验编码在代码里）；仓库交付代码零写入路径
（probe 只有 FC1/2/3/4 帧构造器，写功能码直接抛异常）；实验后进程干净退出
（P2 启动过的实例均已停止；PID 36636 为协调人实例，按约定保留并在报告中注明）。

## handoff.json 同步

`simulator` 键已由 P2 重写为新结论（覆盖 S3 旧 `exists=false`，旧文引述保留于
键内注明）；自动化层事实另立 `p2_simulator_automation` 键。
