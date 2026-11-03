# PlcMcp 基恩士(KEYENCE)集成 — 阶段交接文档 (2026-11-03)

## 本阶段完成并验证的内容

### 1. MCP 工具（已注册进 McpToolRouter，stdio 验收通过）
- plc_keyence_generate_program：ST 源码 → KV STUDIO MNM 模块文件（UTF-16LE+BOM，DEVICE:/;MODULE:/;MODULE_TYPE:/AREA_ST/END/ENDH 容器），写盘到 data 根目录 keyence-generated\<module>.mnm；离线红线预检（声明包装/容器注入/控制字符/保留字/FBEXEC 拒绝）
- plc_keyence_project_snapshot：.kpr 工程目录只读快照（40 字节 PROJECT 头解析、成员清单、XML 摘要；payloadLen+40==size 规则）
- plc_keyence_doctor：安装/组件/COM/注册表/KvsMon.ini 诊断
- 文件：src\PlcMcp.Server\Mcp\McpToolRouter.cs；src\PlcMcp.Engineering\Workers\Keyence\（KeyenceMnmGenerator.cs、KeyenceProjectSnapshot.cs、KeyenceInstallationDoctor.cs）

### 2. 通讯/协议层（loopback 已验证，未上真机）
- KeyenceAclTcpAdapter：TCP:8500 ACL 读帧（10 字节大端头+checksum），DM 地址按连续区段批量合并，单帧 ≤125 字；ProbeAsync=0x03；loopback 测试 30/30
- KeyenceProgramTransferFrames：静态帧构造（0x39 加载 / 0x38 保存 / 0x50-51 差分 / 0x46+0x22-23 commit / 0x2D 认证 / 0x63 PxWrite）；带 [U] 标记字节为未校准猜测（零填充），严禁直接对真机发送
- 文件：src\PlcMcp.Adapters\Protocols\KeyenceAclTcpAdapter.cs、KeyenceProgramTransferFrames.cs；对应 tests

### 3. KV STUDIO UI 自动化证据链（本机 KV STUDIO 12.42 中文版实测）
- kv-uia 工具（%TEMP%\kvuia，UIA 扫描+Invoke+ValuePattern 写+SendKeys；已修 --pid 遍历 bug）
- 打通：打开工程 → 文件(F)→助记符列表(R)→读取(R) 导入 MNM → 覆盖/插入 → Ctrl+F9 转换 → WM_CONTEXTMENU+复制(C) 抓取完整错误树
- 实测错误码语义（权威结论，勿推翻）：
  - [错误 1232] = ST 标识符未在变量表登记（不是词法错误）；裸写软元件（DM100）同样报此错
  - [错误 1203] = FBEXEC/FBEAND 是梯形图指令、非 ST 语句 → 生成器已在 ST 模块 REJECT
  - [错误 735] = 导入时选择程序种类选成 FB/功能块类；必须选"每次扫描执行型模块"
  - [警告 33] = ENDH 可省略，无害
- 证据目录：%TEMP%\kv-evidence\（compile-errors.txt、phase1/2-errors.txt、final-green-*.txt、grid-readback-*.tsv、restore-before-import\ 原版工程备份）

### 4. 测试状态
- Keyence 相关 31/31 绿（MnmGenerator 14 + Snapshot 6 + doctor 等）
- 全量：PlcMcp.Tests 433/434 绿（1 env-only skip）；Engineering 475/483（3 个失败均为已知环境类：MicroWin 断言 x2 + Python 管道，与 Keyence 无关）
- Release build 0 警告 0 错误

## 遗留问题与待完善（按优先级）

### P0 — "生成→导入→编译 0 错误"闭环未完全达成
- 卡点：KV STUDIO 局部变量表是 owner-drawn 自定义网格，无单元格级 UIA；4 次自动化粘贴均未通过读回验证（读回仍是 LocalVariable1/2，b/out 落错列；证据 grid-readback-v3.tsv）
- 正确路线（上游 KeyenceAgent 验证过）：Alt+L 聚焦筛选框 → Shift+Tab（≤3 次）聚焦网格（焦点门：AutomationId 为数字的 Pane 且宽>300 高>100，或祖先含 _grid）→ Ctrl+Home 锚定左上角名称格 → 2 列 TSV name<TAB>data_type 粘贴 → 覆盖/全部覆盖 弹窗 → Ctrl+A/Ctrl+C 读回必须首两列为目标变量 → 保存编译
- 最捷路径：本机 KV STUDIO 手工在"变量编辑"给 Main 登记 b/out 两个 BOOL 局部变量（5 秒），再 Ctrl+F9 编译即得 0 错误证据
- 可选：把上游 22 个守卫脚本全套（%TEMP%\kv-research\live\）拉齐跑 set_kv_variables.ps1（需过静态门禁，成本高）

### P1 — 生成器增强
- 变量名保留字/软元件形名校验：b 与链接继电器 B 保留字冲突（KV 大小写不敏感）→ 加 KV 保留字表（B/C/T/V 等）与软元件形名（DM100 等）拒绝或改警告
- KV-ST 设备访问只能经变量表 device 绑定（全局 9 列含 device 列），ST 正文禁止裸软元件名

### P1 — 模拟器协议校准（未做）
- KV STUDIO 模拟器（Ctrl+F2）启动后 ACCommServer2 监听 8500/8501；ACL 8500 方言与 8501 ASCII 完整校准未完成

### P2 — 程序传输帧上机验证
- [U] 字节校准 + 真机/模拟器端到端验证；建议先走模拟器链路再碰真机

### P2 — 工具整理
- kv-uia 等临时工具在 %TEMP%，可考虑并入仓库 tools/

## 环境备忘
- KV STUDIO 12.42：C:\Program Files (x86)\KEYENCE\KVS12G\KVS12\KVS\Kvs.exe；AcCommServer2：C:\Program Files (x86)\Common Files\KEYENCE\AC\KvCommunication\ACCommServer2.exe
- 测试工程副本：%TEMP%\kv-work\qa2\（干净空白 KV-X520 工程，DEVICE:60）
- 上游参考：%TEMP%\KeyenceAgentRef\ ；%TEMP%\kv-import-playbook.md（389 行 UI 操作手册）
- 工作区 .mcp.json 已配置 windows-mcp（uvx）与 github MCP（github copilot 网关），后续子代理可优先用它们驱动 KV STUDIO / 提交
