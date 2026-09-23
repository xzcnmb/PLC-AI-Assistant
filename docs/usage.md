# PLC-AI-Assistant 使用说明

QQ 交流群：**462720530**。讨论内容包括 PLC 协议适配、AI/MCP 集成、工程自动化及使用反馈。

## 1. 准备环境

- 推荐 Windows 10/11，安装 .NET SDK 10 和 .NET 8 Runtime。项目运行目标为 net8.0；SDK 10 用来读取仓库的 `.slnx` 解决方案。
- SDK 8 用户可直接构建 `src/PlcMcp.Server/PlcMcp.Server.csproj`、测试 `tests/PlcMcp.Tests/PlcMcp.Tests.csproj`。
- 首次 restore 需要访问 NuGet。运行时直接依赖 S7netplus 0.20.0。
- 模拟模式无需 PLC、厂商 IDE、管理员权限或任何现场网连接。

## 2. 获取、构建与测试

```cmd
git clone https://github.com/xzcnmb/PLC-AI-Assistant.git
cd PLC-AI-Assistant
dotnet restore PlcMcp.slnx
dotnet build PlcMcp.slnx -c Release --no-restore
dotnet test PlcMcp.slnx -c Release --no-build
```

产物位置：`src/PlcMcp.Server/bin/Release/net8.0/PlcMcp.Server.dll`。不要单独移动 DLL：它依赖同目录的其他程序集、deps.json 和 runtimeconfig.json。

## 3. 启动模拟模式

```cmd
dotnet src/PlcMcp.Server/bin/Release/net8.0/PlcMcp.Server.dll
```

程序等待 MCP stdin 消息属于正常行为，不会打开窗口或网页。启动说明写 stderr，stdout 只写 JSON-RPC。默认目标：sim-siemens、sim-omron、sim-mitsubishi、sim-inovance。

它们是内存标签模拟器，不是 IEC 61131-3 程序或 PLC 扫描周期模拟器。PressureActual 不会因修改 PressureSetpoint 自动模拟物理过程。

## 4. 接入 AI 客户端

在支持 stdio MCP 的客户端中新增服务，把路径换成你的绝对路径：

```json
{
  "mcpServers": {
    "plc-ai-assistant": {
      "command": "dotnet",
      "args": [
        "C:\\Projects\\PLC-AI-Assistant\\src\\PlcMcp.Server\\bin\\Release\\net8.0\\PlcMcp.Server.dll"
      ]
    }
  }
}
```

无需配置模型 API Key；MCP 服务本身不调用大模型。推理由客户端提供。不同客户端的配置文件位置不同，使用其添加 MCP 服务界面即可。

可对 AI 说：

- “列出 PLC 目标和各自支持的能力。”
- “读取 sim-siemens 的目标压力和实际压力，注明质量码。”
- “为 sim-siemens 的目标压力 4.5 bar 生成计划，再应用到模拟目标，最后回读。”
- “尝试超量程值 999999，确认服务拒绝。”

## 5. 配置物理只读目标

复制 `profiles/readonly.example.json` 为 `profiles/local.readonly.json`。样例使用文档保留地址；核对目标 IP、CPU 家族、rack/slot/unit、地址映射、单位和字节序后再使用。`profiles/local*.json` 已被 Git 忽略。

```cmd
dotnet src/PlcMcp.Server/bin/Release/net8.0/PlcMcp.Server.dll --config profiles/local.readonly.json
```

也可在 MCP 客户端 args 后追加 `--config` 和配置文件绝对路径。配置模式不会同时加载四个默认模拟目标。

加载配置和列出能力不会自动连接 PLC。调用 `plc_probe_target` 或 `plc_read_tags` 时才连接指定目标。当前没有扫描网段工具。

- 西门子：family 必须是支持的精确系列；S7-200 SMART 的 VW100/VD100 映射 DB1。S7comm-plus 和优化 DB 不支持。
- 欧姆龙：FINS/TCP，本地网络、DM/CIO/W/H/A；CIO0.00 为位地址。32/64 位需指定 byteOrder。
- 三菱：仅 SLMP binary 3E，端口按 GX 工程设置；X/Y/B/W 编号按十六进制，D/R/M 按十进制。
- 汇川/通用 Modbus：HR100 为零基保持寄存器 100；IR/C/DI 分别为输入寄存器/线圈/离散输入。厂商 D/MW 到 Modbus 的映射必须从目标工程确认。
- 所有物理标签 canWrite=false；配置模式不注册 plan/apply 写工具。

`experimental` 表示代码和 localhost 协议夹具通过，仍待型号/固件台架验证。不要把 TCP_CONNECTED 解读成 PLC RUN；不同协议的 probe 信息层级不同。

## 6. 返回与错误处理

正常结果见 `result.content[0].text` 的 JSON，工具错误见 `result.isError=true`。v0.1 还保留根级领域字段。值带 dataType、unit、quality、timestamp。多个标签逐次采样，不保证同一扫描周期。

常见情况：

| 现象 | 检查方式 |
|---|---|
| 启动后无窗口 | 正常，stdio 服务由 MCP 客户端驱动 |
| stdout 出现编译日志 | 先 build，再启动 DLL；不要让客户端用未编译的 dotnet run |
| 配置被拒绝 | 查看 stderr 中具体字段、未知属性、地址/类型、rack/slot/unit 和 canWrite |
| OPC UA/CIP 不支持 | 当前未安装这些后端，选择已实现协议；不能靠修改 capability 字段解锁 |
| 超时 | timeoutMs 是连接加整批读取总预算；减小标签批次或调整到 100～30000 ms |
| 中文别名重复 | 同一 changes 中规范名与别名指向同一标签会被拒绝 |
| 计划无法再次应用 | token 错误、过期、取消、状态变化或成功后的计划均不可复用，重新规划 |

## 7. 本机工程分析与 SMART 可选桥

无需厂商软件也可用 `plc_lint_program` 预检 ST/SCL 代码块、`plc_compare_projects` 比对 PLCopen XML、`plc_doctor` 查看本机软件检测。静态预检不等于编译；PLCopen XML 解析禁止 DTD/外部实体。

本机若已安装 STEP 7-MicroWIN SMART 和独立的 `smart200_mcp` Python 包，可在 MCP 客户端 args 中加 `--smart-project-root` 与一个**绝对目录**，只授权该目录中的 SMART 工程只读/离线分析：

```json
{
  "mcpServers": {
    "plc-ai-assistant-smart": {
      "command": "dotnet",
      "args": [
        "C:\\Projects\\PLC-AI-Assistant\\src\\PlcMcp.Server\\bin\\Release\\net8.0\\PlcMcp.Server.dll",
        "--smart-project-root",
        "D:\\PLCProjects\\Review"
      ]
    }
  }
}
```

启用后 `plc_smart_inspect` 读取 V2 项目概览，V3 加密段会报告离线无法解析；`plc_smart_validate` 对指定 POU 做网络有效性验证。它们在独立工作副本上运行，validate 会拉起本机 MicroWIN 独立实例，可能需要交互式桌面会话；不会下载到 PLC。未设置根目录或依赖不可用时工具不会注册。不要把客户生产工程复制到 Git 仓库，样例的 `--config` 只读 PLC 模式与 `--smart-project-root` 可同时使用。

持久治理目前支持 `plc_get_job` / `plc_get_audit` 查询；审计哈希链坏损时显式拒绝读/追加，残缺尾行只能手工显式修复。模拟写入仍使用内存审计；外部企业身份提供方、持久审批消费和物理执行路径没有接线。

## 8. 有限只读监控与离线 HMI 组态

调用 `plc_monitor_window` 并传入 `targetId`、`tags`，可选 `intervalMs` (50～60000，真实目标最少 200)、`durationSeconds` (当前 stdio 单次最多 15 秒)、`maxSamples` (最多 20)。长采样可能阻塞同一 stdio 服务的其他请求，因此该工具仅用于短窗口检查；运行时底层的 10 分钟会话 API 尚未作为 MCP 后台订阅开放。返回每次采样的质量码、时间、变化标记与摘要。服务绝不保证多个点位处于同一个 PLC 扫描周期，因此 `snapshotGuarantee` 始终为 `none`；这不是持续 SCADA 采集服务。

HMI 组态输入是厂商中立 JSON manifest，文件必须放在已配置的 `--smart-project-root` 工程目录里（该选项在此也作为离线工程根目录）。`plc_hmi_validate` 将它与 MCP 目标的标签表比对；`plc_hmi_generate` 校验通过后，向 Server 的 `data/workspaces/hmi/<id>` 新目录输出 JSON/CSV 及 SHA-256。目标厂商必须与 HMI manifest 一致，不能用空 PLC 标签集合跳过绑定检查。它不是 WinCC/GOT/NA 原生工程，也不会发布至触摸屏。

代码库中已有外部 JSON-RPC worker 的 handshake/doctor/submit/status/artifacts/cancel 进程协议和进程超时/输出限制，但**当前 Server 尚无外部 worker 配置入口，也未包含 TIA/CODESYS/GX/Sysmac/InoProShop 的真实工程 worker**。此代码仅供集成开发和 mock 测试，不能将 worker 自报完成视作厂商编译证据。更多实现边界见 [架构设计](research-and-architecture.md)。

## 9. 当前不能做的操作

跨品牌程序自动编译下载、厂商原生硬件/HMI 组态、在线编辑、真实写参数、CPU RUN/STOP、强制 IO、OPC UA 安全会话和持续订阅均未完成。四协议参数写帧只在 localhost 回环测试，`canWrite` 对物理目标仍须 false。SMART 工程离线分析不代表现场程序下载能力。

使用问题可在仓库提交 Issue，附软件版本、已脱敏配置、错误信息、复现步骤和期望行为。也欢迎加入 QQ 群 **462720530** 交流。
