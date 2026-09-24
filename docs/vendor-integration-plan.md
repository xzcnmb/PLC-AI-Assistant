# 厂商工程软件接入方案（CODESYS / 欧姆龙 / 三菱）

本文件记录**基于本机真实安装证据**的接入方案，而不是依据网上摘要推测 API。
每条结论都区分三档：`Verified`（本机命令/输出/工件证实）、`Experimental`（接口存在但未闭环验收）、`Unsupported`（未安装或证据不足）。

安全边界适用于全部品牌：

- 只在用户授权的本机安装软件和**一次性工作副本**上操作，原工程前后 SHA-256 必须一致。
- 不连接未配置的真实 PLC，不下载、不 RUN/STOP、不 Force、不 Publish HMI。
- 不复制、打包或上传厂商 DLL、注入器、商业软件和客户工程文件。
- “反编译”仅指阅读本机官方脚本存根（`.pyi`）、Profile XML、版本资源、程序集元数据与公开帮助文档；不做受保护二进制逆向或授权绕过。
- walk-forward 规则：方法名存在 ≠ 接口可用。只有真实 stdout/stderr、退出码、工件 hash、工作副本前后 hash 同时成立，才能从 `Unsupported` 提升到 `Experimental`。

---

## 一、CODESYS（主线，本机 3.5.22.30 / SP22 Patch 3 / x64）

### 已核实的本机事实

| 项 | 证据 |
|---|---|
| 主程序 | `C:\Program Files\CODESYS 3.5.22.30\CODESYS\Common\CODESYS.exe` |
| Profile | `CODESYS V3.5 SP22 Patch 3`（`CODESYS\Profiles\*.profile.xml`） |
| 脚本引擎 | `ScriptEngine.plugin.dll` + `ScriptLib\Stubs\scriptengine\*.pyi`，IronPython 2.7，Scripting 4.2.0.0 |
| 工程 API | `projects.open/create/save_as/export_xml/import_xml`、Application `build/clean/rebuild/generate_code`、`system.get_messages/get_message_objects/write_message/exit` |
| 在线 API | `ScriptOnline`：`create_online_application/device`、`login/logout/start/stop`、`read_value(s)`、`set_prepared_value`、`force_prepared_values`、`reset`、`download/upload_file` |
| 官方 MCP Bridge | **未安装**（`CodesysMCPBridge.exe` 不存在） |
| 启动参数 | `--noUI --profile --runscript --scriptargs --noConsole --skipProjectRecovery --skipUnlicensedPlugins` |

### 分层设计

1. **本机证据层**：版本、Profile、位数、插件、脚本 hash、真实命令行语义与退出码。
2. **离线工程 Worker**：只处理隔离工作副本——打开、查 Application、导出 PLCopen XML/ST、`clean/build/rebuild/generate_code`、读编译诊断、校验产物 SHA-256。**不访问 PLC。**
3. **在线设备 Worker**（本轮硬拒绝，未来单列）：login、读变量、RUN/STOP、写入、Force、下载。必须另建 DeploymentPlan + 外部审批绑定 + 目标租约 + CPU/项目 hash + 备份 + 回读 + quarantine。

### 实现进度

- `CodesysWorkerConfig`：pinned exe / profile / script / script-SHA256 / project root，`BuildRawArguments()` 与 `ToExternalWorkerConfig()`。
- `ExternalEngineeringWorker`：新增 `WorkingCopyReadOnly`，默认只读；显式配置的离线构建 worker 才允许在**一次性副本**内写入，原工程仍做完整性校验。
- 复用 `ExternalWorkerProtocol / Client / SecurityPolicy`：JSON-RPC 行协议、身份与协议版本固定、可执行体白名单、输出配额、超时杀进程树、危险操作硬拒绝、`Experimental` 信任上限。
- 驱动脚本 `scripts/codesys_worker.py`：IronPython 2.7 语法，line-JSON `handshake / doctor / submit / status / artifacts / cancel`；仅实现离线操作。

---

## 二、欧姆龙（本机 P0 已落地）

### 已核实事实

| 组件 | 状态 | 路径 / 证据 |
|---|---|---|
| Sysmac Studio | 已安装 1.60.0.64010 | `C:\Program Files\OMRON\Sysmac Studio\SysmacStudio.exe` |
| ACE 脚本宿主 | 存在 | `...\Sysmac Studio\Ace.ScriptHost.exe`；帮助 `...\Help\en-US\ACE API Reference Manual.chm` |
| 交叉编译核心 | 存在（私有） | `...\Sysmac Studio\builder2\nexcc.exe` |
| 工程比对 | 存在 | `...\Sysmac Studio\SysmacDiff.exe` |
| IEC 61131-10 XML | 有 Schema + 样例 | `...\Sample\IEC 61131-10 XML\Controller\IEC61131_10_Ed1_0_SmcExt1_0_Spc1_0.xsd` |
| AutomationML | 有样例 | `...\Sample\IEC 62714 AutomationML\SampleProject.aml` |
| 仿真器 | 已安装 | `Modules\Nex\NexSimulator\RuntimeSimulator\*` |
| CX-Server | 已安装；当前注册表/文件版本为 `5.1.1.4` / `5,1,1,4` | `C:\Program Files (x86)\OMRON\CX-Server\cdmsvr20.exe`，COM `CXServer.Communications`；版本记录以本机审计为准 |
| CX-Programmer / CX-Compolet / Sysmac Gateway | **未安装** | 注册表与文件系统均无 |

### 方案与实现

1. **离线工程侧（首选，已实现）**：`OmronExchangeService` 走 IEC 61131-10 XML / AML / PLCopen XML 只读解析与差异比较——4 MiB 限额、禁止 DTD/外部实体、深度/节点保护、稳定排序与 raw-extension hash。
2. **诊断层（已实现）**：`OmronInstallationDoctor` 动态检测 Sysmac Studio、CX-Server、组件 SHA-256、真实 PE 位数与 13 个 ProgID 静态注册；不激活 COM，不启动 IDE。
3. **禁止**：把 Compolet/FINS 当工程编译器；在线下载/模式切换/Force 继续硬拒绝。

---

## 三、三菱（本机 P0 已落地，工程 API 仍未验证）

### 已验证落地

- 独立 `.NET Framework 4.8 x86` metadata worker 位于 `src/PlcMcp.GxWorks3.Worker`，只读取 GXW3.exe、Service.config 与有限程序集元数据；不执行 GXW3、不加载 ServiceBus、不连接 PLC。
- 真实本机 `GXW3.exe` 版本 `1.128.0.1`、PE `0x014c/x86`；真实 handshake/doctor/shutdown 退出码 0。
- Server 通过 `--gxworks3-probe`、`--gxworks3-probe-sha256`、`--gxworks3-exe`、`--gxworks3-version` 成组参数注册唯一的 `plc_gxworks3_doctor`。
- `engineeringApiVerified=false` 始终保留；`plc_gxworks3_inspect/export/compile` 不注册。

### 尚未验证与禁止

1. `Service.config` 中 PLCFormat/Checker/Label 键存在，但没有证明独立进程可安全初始化 ServiceBus；禁止反射 internal broker 或直接加载厂商 DLL。
2. 工程离线读取、PLCopen/XML 导出、标签提取、比较和编译仍 Unsupported；目录工程快照/重解析点完整性门尚未开放。
3. MX Component 未确认安装，不开放 COM 通信；在线下载/模式/Force/写入/内存操作继续硬拒绝。

---

## 四、统一验收门槛（每后端）

- 精确软件版本、Profile、位数、脚本/可执行 hash。
- 本机 doctor + worker handshake 证据。
- 离线工作副本原文件前后 hash 不变。
- 编译诊断、导出产物、产物 SHA-256、回读/重开证据。
- fake worker 的超时、取消、输出配额、身份篡改、工件路径逃逸、hash 不匹配测试全绿。
- 无 IDE/授权/设备时**明确返回 `Unsupported`**，绝不把方法名当成已完成能力。
