# kv-uia

KEYENCE KV STUDIO 的 UIA(Windows UI Automation)树扫描 + 单发操作工具。
原为 2026-11-03~04 Keyence 集成调试期间放在 `%TEMP%\kvuia` 的临时工具,现入库保存。

## 用途

- **扫描**:遍历桌面 UIA 元素树,按正则打印匹配的控件(`pid / name / ControlType / hwnd / offscreen`),
  用于摸清 KV STUDIO 对话框、菜单、网格的 UIA 结构。
- **单发 Invoke/Expand**:对第一个命中且未离屏的控件执行 InvokePattern(或 ExpandCollapsePattern),
  等价于一次点击,可驱动"打开工程 / 文件(F)→助记符列表(R)→读取(R) / 覆盖"等菜单链路。
- **单发 SetValue**:对命中的控件执行 ValuePattern.SetValue(写文本格),打印 `SET` /
  `SET-FAILED (reason=NoValuePattern|ElementOffscreen|NoMatch)`。
- **单发 SendKeys**:`--keys` 不经 UIA 遍历,直接向**前台窗口**发送按键串
  (如 `{ENTER}`、`{TAB}`、`^%{F4}`),经 `WScript.Shell` COM 的 SendKeys 实现。

注意:`--pid` 只过滤**打印与操作**的目标;遍历本身仍跨进程下钻(RootElement 自身 pid 为 0)。

## 用法

```
kv-uia <regex> [--invoke] [--set <value>] [--keys <keys>] [--pid <pid>] [--max <nodes>]
```

- `<regex>`:控件 Name 的匹配正则(不区分大小写);省略时默认 `模拟`。
- `--invoke`:Invoke/Expand 第一个命中的可见控件。
- `--set <value>`:同 `--invoke` 的选择逻辑,改为 SetValue。
- `--keys <keys>`:跳过遍历,向前台窗口发送 SendKeys 串。
- `--pid <pid>`:只对该进程的控件打印/操作(遍历不受限)。
- `--max <nodes>`:遍历节点上限,默认 400000。

示例:

```bat
kv-uia "转换" --pid 1234 --invoke          REM 在 KV STUDIO 进程内点击"转换"
kv-uia "模块名" --set sw                    REM 向匹配文本格写入 sw
kv-uia --keys "{ENTER}"                     REM 前台窗口回车确认弹窗
```

## 构建

目标框架为 **.NET Framework 4.8**(`net48`),用 dotnet CLI 或 VS2022 构建均可:

```bat
cd tools\kv-uia
dotnet build -c Release
```

UIAutomationClient / UIAutomationTypes 两个引用解析自 GAC
(`%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\...`),因此只能在装了 .NET Framework 4.8
的 Windows 桌面上构建/运行,产物为 `kv-uia.exe`。二进制产物不入库。

## 应用场景

- 2026-11-03 Keyence UI 自动化证据链:打开工程 → 助记符列表导入 MNM → Ctrl+F9 转换 →
  抓取编译错误树(见 `docs/HANDOFF-2026-11-03-keyence.md` 第 3 节)。
- 2026-11-04 P0 闭环(qa2 工程生成→导入→变量登记→编译 OK)与模拟器协议校准期间的
  弹窗驱动,见 `docs/keyence-simulator-protocol-calibration-2026-11-04.md`。
- 后续 KV STUDIO 相关自动化(变量表网格、模拟器启停等)可继续复用本工具做只读扫描与
  单发操作;KV STUDIO 局部变量表为 owner-drawn 网格、无单元格级 UIA,写格操作受限,
  详见交接文档 P0/P1 段。

## 源码

- `Program.cs` — 单文件实现(Main 约 130 行)。
- `kv-uia.csproj` — net48 Exe,LangVersion 9.0,引用 GAC UIAutomation 程序集。
