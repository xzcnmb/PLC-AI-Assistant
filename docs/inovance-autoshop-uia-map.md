# 汇川 AutoShop UIA 界面自动化测绘（阶段二 / S3）

> 测绘对象：本机 AutoShop V4.12.0.2（`D:\Inovance Control\AutoShop`，PID 观测 24492，x86/WOW64）。
> 测绘日期：2026-10-24。性质：本文件记录**界面自动化（UIA）路线**的实测事实与结论，供 MCP 工具集成（S4）与工程格式逆向（S1）参考。所有能力结论均基于本次实测，未实测的一律标注 UNVERIFIED。
>
> 对应交付代码：`src/PlcMcp.Engineering/Workers/AutoShop/Uia/**`（可复用基础库）与 `tests/PlcMcp.Engineering.Tests/AutoShopUiaTests.cs`（54 项离线单测，全绿）。

---

## 0. 结论速览（TL;DR）

| 议题 | 结论 |
|---|---|
| UI 框架 | **标准 MFC MDI 应用**，非纯 DuiLib 外壳。主窗口类 `Afx:00400000:8:...`，含 `MDIClient`、多个 `Afx:ToolBar`/`Afx:ControlBar`/`Afx:StatusBar`，以及 `SysTreeView32`/`ListBox`/`Button`/`Static` 等真实子窗口。（DuiLib.dll 存在，但用于部分皮肤/对话框，非主框架。） |
| UIA/Win32 可用性 | **部分可用**：工程树、信息输出列表、状态栏、对话框控件均有真实 HWND，`WM_GETTEXT`/`LB_GETTEXT`/`SB_GETTEXT` 可读。**菜单栏与工具栏是 CMFC 自绘控件**：无 HMENU（`GetMenu==0`）、工具栏 `TB_BUTTONCOUNT==0`、无逐项 HWND，只能靠坐标点击 + OCR。 |
| DPI 缩放（关键坑） | 桌面 **150% 缩放**（物理 2560×1600 vs 虚拟化 1707×1067）。测量进程**必须**先 `SetProcessDpiAwarenessContext(-3)`（Per-Monitor V2），否则 `GetWindowRect` 返回虚拟化坐标（÷1.5），与合成输入物理坐标错位 ~1.5×，点击全部落空、截图发虚。 |
| OCR（读自绘区的“眼睛”） | WinRT `Windows.Media.Ocr`（本机仅 `zh-Hans-CN`）。**必须先设 DPI 感知**再 `CopyFromScreen` 才能读到文字；对白底灰字菜单需阈值/放大预处理。已验证可读标题栏、菜单栏标签。 |
| **输入防护（最重要）** | **AutoShop 忽略合成鼠标输入**：`SendInput` 鼠标、window-mcp `Click`、向菜单栏 `PostMessage(WM_LBUTTONDOWN/UP)` 三者对菜单栏/工具栏**均零效果**（按下不放、两次点击、反色检测均无变化）。键盘加速键 `Ctrl+N` **曾一次到达** AutoShop（触发“正在准备首次使用模块”一次性准备），但 `Alt+F`/`F10`/方向键**未能打开菜单**。本机为**本地会话**（`SM_REMOTESESSION==0`），AutoShop 清单 `level="asInvoker"`（未提权，与驱动进程同 Medium），故非 UIPI/远程会话所致。 |
| 对 M2/M3/M4 的影响 | 因鼠标被拦、菜单键盘导航不可用，**无法经 UI 可靠驱动**新建工程向导/编译/模拟器。M2 样本工程、M3 编译实证、M4 模拟器实证**未完成**（如实记录，未伪报）。已把完整 UI 流程、坐标、抓取策略固化为代码与文档，供人工或修复输入通道后执行。 |

---

## 1. 环境证据

| 项 | 值 | 证据 |
|---|---|---|
| 进程 | `AutoShop.exe` PID 24492（观测值），主窗口标题 `AutoShop V4.12.0.2` | `Get-Process`、`GetWindowText` |
| 安装 | `D:\Inovance Control\AutoShop` | 目录列举（同 re-notes） |
| 主窗口类 | `Afx:00400000:8:00010003:00000000:00010AC3`（MFC 框架窗） | `GetClassName` |
| 完整性 | `asInvoker`（Medium，未提权） | exe 嵌入清单 `requestedExecutionLevel level="asInvoker"` |
| 会话 | 本地控制台（非远程） | `GetSystemMetrics(SM_REMOTESESSION)==0` |
| 桌面 | 物理 2560×1600，DPI 150% | `GetSystemMetrics`（设感知前后对比） |
| InoVarSharedService | 运行中，TCP 127.0.0.1:13939（属代理 B/ITP，非 PLC 模拟器） | re-notes + 本机状态栏“变量共享: 未连接” |

---

## 2. 主窗口层级（物理像素，测量进程已设 Per-Monitor DPI 感知）

主窗口 `(39,39)-(1959,1142)`（1920×1103）。自顶向下：

```
MainFrame 0x10A80  Afx:00400000:8      (39,39,1959,1142)  "AutoShop V4.12.0.2"
├─ DockPane 0x10B9C                     (51,86,1947,218)   顶部停靠区
│  ├─ MenuBar 0x10A96  Afx:ToolBar      (51,86,1947,128)   "菜单"      ← 自绘，无 HMENU
│  ├─ StandardToolbar 0x10A9A           (51,128,731,173)   "标准工具栏"
│  ├─ CompileToolbar 0x10AA2            (731,128,827,173)  "编译工具栏" ← 编译入口
│  ├─ PlcToolbar 0x10AA6                (827,128,1718,173) "PLC工具栏"
│  ├─ VariableShareToolbar 0x10B98      (1718,128,1948,173) 含"变量共享""未连接"按钮
│  ├─ SfcToolbar 0x10AAE                (51,173,639,218)   "顺序功能图"
│  ├─ LadderToolbar 0x10AAA             (639,173,1446,218) "梯形图工具栏"
│  └─ CommToolbar 0x20AF0               (1446,173,1601,218) 含"本地""USB"通道按钮
├─ ProjectPane 0x10ABE Afx:ControlBar   (51,218,351,864)   "工程管理"
│  └─ ProjectTree 0x10AC2 SysTreeView32 (53,260,350,863)   ← 真实 HWND，可读
├─ MdiClient 0x10A82 MDIClient          (357,218,1947,864) 编辑区
├─ OutputPane 0x10AC6 Afx:ControlBar    (51,870,1947,1095) "信息输出窗口"
│  ├─ OutputTab 0x10ACA Afx:TabWnd      (51,907,1947,1095) 页签(编译/输出/查找…)
│  └─ OutputList 0x10ADA ListBox        (56,915,1943,1065) ← 编译结果列表，真实 HWND，LB_GETTEXT 可读
└─ StatusBar 0x10ABA Afx:StatusBar      (51,1095,1947,1130) "就绪"；含"本地/USB/变量共享:未连接"按钮
```

以上坐标已固化到 `AutoShopUiaMap.Controls`（`src/.../Uia/AutoShopUiaMap.cs`），S4 可直接引用；换缩放比的桌面需用 `DpiContext` 重算。

---

## 3. 菜单结构（CMFCMenuBar，自绘；OCR 采集）

顶层菜单（左→右，含加速助记字母）：

| 顶层 | 助记 | 备注 |
|---|---|---|
| 文件 | (F) | 新建/打开/保存/另存为/关闭/工程管理/最近工程/退出 |
| 编辑 | (E) | |
| 视图 | (V) | |
| PLC | (P) | 通信/下载/上传/在线相关多在此 |
| 工具 | (T) | |
| 帮助 | (H) | |

- OCR 采集到的标签（含位置）：`文件`≈x74、`视图(V)`≈x151-188、`PLC(P)`≈x232、`工具(T)`≈x304、`帮助(H)`≈x382（y≈97）。中文 OCR 对个别字有误差，但结构与助记字母清晰。
- 因菜单为自绘且输入被拦，**下拉子项未能可靠逐条 OCR**（下拉出现即被焦点变化取消，见 §5）。`文件 > 新建工程`、`文件 > 打开工程`、`编译 > 全部编译` 等路径依 AutoShop 常规布局推定，标注 UNVERIFIED。

---

## 4. 工具栏与关键入口

| 名称 | 物理矩形 | 说明 |
|---|---|---|
| 编译工具栏 | (731,128,827,173) | 编译/全部编译入口（约 1-2 个图标按钮）。编译快捷键常规为 **F7**（UNVERIFIED，输入被拦无法触发表证）。 |
| PLC工具栏 | (827,128,1718,173) | 下载/上传/在线监视/通信设置等多按钮。**只记录，不执行**（红线：禁对实体 PLC 下载/Force）。 |
| 变量共享工具栏 | (1718,128,1948,173) | 含“变量共享”“未连接”按钮——**这是 ITP 共享变量发布开关**（供 ITP 服务推送数据，属代理 B 域），非 PLC 模拟器。 |
| 通信通道 | CommToolbar (1446,173,1601,218) | “本地”“USB”通道切换。 |

**下载/上传入口**：PLC工具栏（仅记录位置，未执行）。**在线监视入口**：PLC工具栏/PLC 菜单（UNVERIFIED）。**轴组态入口**：工程树/PLC 菜单（UNVERIFIED）。

---

## 5. 输入防护实测（本阶段最重要发现）

对 AutoShop 的合成输入做了系统测试，结论：**鼠标注入被忽略，菜单键盘导航不可用，仅加速键偶达**。

| 方法 | 目标 | 结果 |
|---|---|---|
| `SendInput` 鼠标（绝对坐标，返回 3=注入成功） | 菜单栏“文件”、标准工具栏 | 界面**零变化**（前后全屏 diff=0） |
| `SendInput` 按住不放 700ms 再捕获 | 菜单栏“文件” | 无下拉 |
| window-mcp `Click`（工具自身注入） | 菜单栏“文件”、工具栏 | 无下拉/无对话框（artifact OCR 仅见背景） |
| `PostMessage(WM_LBUTTONDOWN/UP)` 到菜单栏 HWND | 菜单栏“文件” | 无下拉（CMFCMenuBar 需完整鼠标捕获序列，单投消息不够） |
| `SendInput`/window-mcp `Alt+F`、`F10`、`Enter` | 菜单栏 | 未打开菜单/向导 |
| window-mcp `Ctrl+N` | 全局 | **触发了一次性“正在准备首次使用模块”准备框**（证明键盘加速键可到达）；准备完成后状态回“就绪”，再按无向导 |
| 右键（`SendInput` + window-mcp） | 工程树 | 无上下文菜单 |

**根因分析（事实 + 推断）**：
- 【事实】本地会话、AutoShop 未提权（asInvoker/Medium）、与驱动进程同级 → 排除 UIPI 提权拦截与远程会话注入限制。
- 【事实】window-mcp 对任务栏的点击有效（可切前台），证明注入链路本身工作；仅 AutoShop 自身控件不响应。
- 【推断】AutoShop 很可能装有低层鼠标钩子（WH_MOUSE_LL）并丢弃带 `LLMHF_INJECTED` 标记的注入事件（常见反自动化手段）；键盘未经同等拦截，故 `Ctrl+N` 加速键可达，但菜单栏的 Alt/方向键路径需要窗口处于“菜单激活”态，未能打通。
- 【事实】测量进程若不设 DPI 感知，`GetForegroundWindow` 出现“返回 AutoShop HWND 但属主 PID=测量进程”的错位，进程内 `SendInput` 会路由到测量进程自身——这也是早期“进程内点击无效”的部分原因。

**给 S4 的对策（按推荐排序）**：
1. **键盘加速键优先**：能用 `Ctrl+N/Ctrl+O/Ctrl+S/F7` 等加速键完成的操作走键盘（window-mcp `Shortcut` 或独立注入进程可达）。对话框内的标准控件（Button/Edit）可 Tab/方向键/Enter 驱动并 `WM_GETTEXT` 校验。
2. **用独立、非测量进程注入**：注入进程不要与截图/读取进程混一（避免前台线程错位）；注入前 `SetForegroundWindow(AutoShop)` 确认真激活。
3. **真机鼠标/硬件级输入**：若必须点自绘菜单/工具栏，需真实硬件输入或 UIAccess 签名提权注入器（超出本阶段范围）。
4. **结果抓取不依赖点击**：编译结果从 `OutputList`（ListBox，`LB_GETTEXT`）与状态栏（`SB_GETTEXT`）读取——这两个是真实 HWND，键盘触发编译后即可读，无需鼠标。
5. **OCR 兜底**：读自绘区用 WinRT OCR，务必先设 DPI 感知 + 适当放大/阈值（见 `tools/` 说明与本文 §6）。

---

## 6. OCR / 文本抓取策略（已验证）

- **引擎**：WinRT `Windows.Media.Ocr.OcrEngine`，本机语言仅 `zh-Hans-CN`（对拉丁/数字有误差，中文菜单标签可读）。
- **前置**：测量进程先 `SetProcessDpiAwarenessContext(-3)`；用 `CopyFromScreen`（物理像素）而非 window-mcp 下采样 artifact。
- **文本源优先级**（`TextCaptureSelector` 已编码）：标准控件 `WM_GETTEXT` > ListBox `LB_GETTEXT` > 状态栏 `SB_GETTEXT` > 自绘区 OCR。
- **踩坑记录**：① WinRT 静态方法 `SoftwareBitmap.CreateCopyFromBuffer` 在 WinPS 5.1 无法绑定——改用“24bppRgb 存 PNG → BitmapDecoder → Recognize”可靠路径；② 深底浅字（如标题栏）需反色，白底灰字菜单需阈值化；③ 每个 PowerShell 调用是全新进程，DPI 感知与 WinRT 类型投影每次都要重设。
- 参考实现脚本位于本机临时工具目录 `%TEMP%\s3-uia-tools\Ocr.ps1`（测绘用，非交付物；交付物为 C# 侧的抓取策略选择逻辑）。

---

## 7. M2 样本工程（未完成 —— 输入防护阻断）

**目标流程（已编码到 `SampleProjectFactory.NewProjectSteps`，供人工/修复输入后执行）**：
1. 菜单 `文件 > 新建工程`（或 `Ctrl+N`，若其在完全初始化后映射到新建工程——UNVERIFIED）。
2. 向导第 1 步：选 PLC 系列，选型号 **H5U**（另建 **H3U**）。
3. 向导第 2 步：设工程名与保存位置 `C:\Users\18735\AppData\Local\Temp\plcmcp-handoff\autoshop-samples\`。
4. 完成创建空工程；`文件 > 保存`(Ctrl+S) 落盘。
5. （可选）加 M0 线圈 + 一条 ST 赋值 + 一个 D 变量后另存“带内容”样本。

**现状**：新建工程向导为鼠标/菜单驱动，本机鼠标被拦、菜单键盘不可用 → **未能创建任何样本工程**。`autoshop-samples\` 目录为空。已如实记入 `handoff.json` 的 `samples` 键。S1 需在输入通道修复后由本流程产出样本，或改用其它途径获取工程文件。

---

## 8. M3 编译触发与结果抓取（未实证 —— 依赖 M2）

- **入口**：编译工具栏 `(731,128,827,173)`；快捷键常规 **F7**（UNVERIFIED）。触发走键盘加速键（`AutoShopUiaOptions.CompileAcceleratorKeys=["f7"]`）。
- **结果抓取**：编译结束后读 `OutputList`（`LB_GETTEXT`，逐行）+ 状态栏（`SB_GETTEXT`）。这两者为真实 HWND，**不依赖鼠标**。
- **结果判定**：`CompileOutputParser`（纯函数，已单测）识别中文/英文“编译通过/编译失败/错误/警告”，并区分“0 错误 0 警告”这类**通过摘要**与真实错误行，提取 `(行号)`/`[块]` 位置。空输出→判为“未确认”，**绝不伪装成功**。
- **现状**：无工程可编译（M2 阻断），故未做“空工程编译通过”与“语法错误抓行”实证。抓取与判定逻辑已就绪并经单测覆盖。

---

## 9. M4 模拟器探查（未发现 —— 输入防护阻断菜单探查）

- **在线/模拟菜单无法打开**（输入防护），故 H5U 在线模拟器**是否存在、如何启动、虚拟 PLC 端点均未验证**。
- **静态侦察**：通信通道栏仅见“本地”“USB”切换，未见模拟器端点；状态“变量共享: 未连接”。
- **共享变量发布开关（已定位）**：变量共享工具栏 `(1718,128,1948,173)` 的“变量共享”按钮 + 状态按钮“未连接”——这是 ITP 共享变量服务（InoVarSharedService 127.0.0.1:13939，代理 B 域）的界面开关，**不是 PLC 模拟器**。
- 结论写入 `handoff.json` 的 `simulator` 键（`exists=false / verified=false`，附阻断原因与静态证据）。

---

## 10. C# 交付与接口（供 S4 复用）

`src/PlcMcp.Engineering/Workers/AutoShop/Uia/`：
- `AutoShopUiaModels.cs`：`ScreenRect`/`DpiContext`（DPI 变换）、`UiaWindow`/`UiaControl`/`MenuPath`/`WindowQuery`、`InputMethod`/`InputCapabilities`/`InputPlanner`（输入策略选择，编码“鼠标被拦→键盘优先”）、`TextCaptureStrategy`/`TextCaptureSelector`、`CompileDiagnostic`/`CompileTriggerResult`/`CompileOutputParser`、`SimulatorReport`。
- `AutoShopUiaMap.cs`：实测控件坐标/菜单/编译入口的静态地图 + `CapturedDpi`。
- `AutoShopUiaInterop.cs`：接口 `IWindowLocator`/`IInputSender`/`ITextReader`/`IDialogWaiter` + 真实 Win32 实现（`Win32WindowLocator`/`Win32TextReader`/`Win32InputSender`/`DialogWaiter`/`Win32Dpi`/`VirtualKeys`）。真实实现标注“依赖本机 AutoShop，不进 CI”。
- `AutoShopUiaAutomation.cs`：门面 `CompileTrigger`/`ProjectOpener`/`SampleProjectFactory`/`SimulatorProbe` + `AutoShopUiaOptions`/`ProjectOpResult`。全部经接口注入 + 可注入延迟，超时完整，**失败不伪装成功**。

测试：`tests/PlcMcp.Engineering.Tests/AutoShopUiaTests.cs`（54 项，覆盖 DPI 变换、窗口匹配、输入规划、抓取策略、编译输出解析、对话框等待超时、门面阻断路径；全用 fake，不触真机）。

**S4 集成提示**：把真实实现（`Win32WindowLocator` 等）装配进门面时，先用一次轻量“输入能力探测”得到 `InputCapabilities`（本机观测值 = `InputCapabilities.ObservedAutoShop`：鼠标 false、加速键 true、菜单导航 false），据此门面会自动降级并返回诚实的 Unsupported/Blocked 结果。

---

## 11. 事实/推断标注与红线遵守

- 【事实】窗口层级/类名/矩形、菜单标签与助记、工具栏清单、DPI 前后坐标、输入各方法零效果、`Ctrl+N` 一次响应、`SM_REMOTESESSION==0`、exe 清单 asInvoker、OCR 语言集合。
- 【推断】WH_MOUSE_LL 注入过滤为输入无效的可能根因；文件/编译等具体子菜单路径依常规布局推定（UNVERIFIED）。
- **红线遵守**：全程未对任何 PLC 下载/Force/写入；未写入仓库或安装目录（脚本仅在 `%TEMP%`，样本目录为空）；未删除/改动用户文件；AutoShop 无未保存工程，未重启。所有“新建/编译/模拟器”动作因输入防护均未真正执行，无确认框风险。
