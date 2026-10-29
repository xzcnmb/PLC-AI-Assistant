# 汇川 AutoShop 注入桥工程工具示例（generate → open → compile）

这是 `plc_autoshop_generate_project` → `plc_autoshop_open_project` → `plc_autoshop_compile`
的流程示例说明（阶段三 E4 交付）。本文件不是 JSON 配置：工程闭环不加载目标 profile，
全部动作只发生在**本机 AutoShop 实例与本地文件**上，零 PLC 动作。

## 0. 前置

1. `.NET`：`dotnet build PlcMcp.slnx -c Release`（见 usage.md 第 2 节）。
2. 原生注入器/桥 DLL：`tools\AutoShopInjectBridge\build.cmd`（VS2022 v143 x86），
   产物在 `%LOCALAPPDATA%\AutoShopInjectBridge-run\`（AutoShopInjectBridgeInjector.exe +
   AutoShopInjectBridge.dll），缺一工具调用即失败（discovery 证据）。
3. Server 以 `--autoshop-bridge` 启动（工具默认全部不注册；可加
   `--autoshop-compile-rva <hex>` 覆盖注入桥编译入口槽，默认为 E3 实证地址 0x15A2EC0）。

## 1. 工程生成（离线构造 + 往返校验）

```json
{
  "tool": "plc_autoshop_generate_project",
  "arguments": {
    "projectDir": "D:\\PLCProjects\\e4demo",
    "model": "H3U",
    "projectName": "e4demo",
    "sourceHcpp": "C:\\Users\\you\\AppData\\Local\\Temp\\plcmcp-handoff\\Template_A1_copy.hcpp"
  }
}
```

- 不传 `sourceHcpp`：按 Template A1 实测结构生成最小 XML（`Version=1`、`GCMModal`、
  默认声明 MAIN.LD/VarList.gdt/Config.sdt 占位内容），UCodeCrypt 加密为 `<名>.hcp`，
  声明文件（含 31 字节 AutoShop 头）写入同目录，HcppWriter 打包 `.hcpp`；
  能力 `Experimental`——占位内容能否被 AutoShop 接受未验证。
- 传 `sourceHcpp`（推荐，如厂商模板副本）：字节忠实解包真实工程包
  （Main blob → `.hcp`，文件记录原样 → 磁盘文件），往返校验通过即已知可打开。
- 返回：`.hcp`/`.hcpp` 路径、文件清单、XML 摘要与往返校验结论。
  工程目录禁止位于 AutoShop 安装目录内；非 ASCII 工程名自动改用 `project.hcp`（注入器
  ANSI 转换限制）。

## 2. 打开（自启实例 + 注入桥 + 窗口取证）

```json
{
  "tool": "plc_autoshop_open_project",
  "arguments": { "projectDir": "D:\\PLCProjects\\e4demo" }
}
```

- 每次调用重新解包**全新 `.hcp`**（AutoShop 打开时原地解密消费 `.hcp`）→
  `AutoShop.exe <绝对路径>.hcp` 启动自启、记账、握手核验的实验实例 →
  有界轮询（默认 30 s）主窗口标题/模态对话框。
- 成功：主窗口 title 含工程目录（`AutoShop V4.12.0.2 <目录> - [MAIN]`），
  返回 pid 与窗口标题；实例保留供 compile/monitor_read 使用。
- 失败：按逆向错误串映射六态（属性 7596 / 版本 7597 / 文件列表 7598 /
  打开工程失败 62220 / 文件不存在 / 打开文件出错 / 未知对话框），实例被停止，
  返回对话框证据；"正在加载中"等良性过场对话框继续轮询，不判失败。

## 3. 编译（当前工程门）

```json
{ "tool": "plc_autoshop_compile", "arguments": {} }
```

- 前置：open_project 成功（会话内当前工程门；否则诚实 Unsupported，不发桥请求）。
- 流程：注入桥 compile 请求（E3 槽 0x15A2EC0 已接线，桥确认 `compile entry RVA
  0x015a2ec0 is configured`）→ 轮询 `<工程目录>\Compile\*.compile` 与 `<名>.foid` →
  UCodeCrypt 解密每个 `.compile` 并给出解密摘要（头 16 字节 hex）。
- **当前交付态桥 DLL 对 compile 一律诚实回答 Unsupported**（调用约定与签名验证证据未
  落地，见 docs/inovance-autoshop-open-and-compile.md §8.1）：工具如实转述原因，
  不伪装成功；桥答 Unsupported 时轮询缩短为 3 s（仅捕获并行/既有产物）。

## 4. 只读监视（可选）

```json
{ "tool": "plc_autoshop_monitor_read", "arguments": { "addresses": "D100,M0" } }
```

- 前置：open_project 成功（注入实例在环）。回环 set_target（127.0.0.1，工具无目标参数）
  → 转述 read_status/read_elements 的**调用回执**。
- 这是调用回执级证据，**不是 PLC 数据**：交付态桥无法填元件值（AddElem 基类桩）。

## 5. 清理与产物位置

- 实例清理：下一次 open、`StopOwnedAsync`（Server 关闭时自动）或崩溃恢复
  （`%TEMP%\plcmcp-handoff\inject-bridge\owned-instances.jsonl` 记账 + PID 归属核验 +
  整树终止）；外部/用户实例永不被触碰。
- 产物：`.hcp`/`.hcpp`/声明文件在 `projectDir`；编译产物在
  `<工程目录>\Compile\<名>.compile`（UCode 加密）与 `<工程目录>\<名>.foid`；
  工具日志在 Server 数据目录 `data/autoshop-inject/autoshop-inject.log`（JSONL）。
- 良性副作用（AutoShop app 自建）：`BackUP/`、`Temp/`、`config.ini`、
  `HKCU\...\Recent File List` 追加（本工具不做注册表回滚）。

## 6. 能力矩阵（诚实声明）

| 能力 | 状态 | 原因 |
|---|---|---|
| generate_project | `Experimental` | 离线构造 + 往返校验通过；内容能否被 AutoShop 接受未验证（模板副本输入已实证可打开） |
| open_project | `Experimental` | E3 构造法本机实证（模板类工程）；自启/记账/清理，非 PLC 动作 |
| compile | `Unsupported` | 桥诚实答 Unsupported：E3 槽地址已配置但调用约定/签名验证证据未落地 |
| monitor_read | `Experimental` | 调用回执级证据，不是 PLC 数据；桥无法填元件值 |
| 写元件 / 强制 / 下载 / RUN-STOP | `Unsupported` | 本版不注册工具；三层门默认关 + 厂商写导出"只解析不调用" |

全部接口为逆向所得，无汇川厂商背书，版本锁定 AutoShop 4.12.0.2（x86）。
