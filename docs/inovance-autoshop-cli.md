# 汇川 AutoShop 命令行开关语义逆向（CLI-RE）实证文档

> 本文档固化 CLI-RE 对 AutoShop V4.12.0.2（x86，`D:\Inovance Control\AutoShop\AutoShop.exe`）
> 命令行开关的全部静态逆向与零 PLC 实验 结论。这是"打开工程/编译"链路的最后一块：
> 回答 **"CLI 能否成为打开/编译的可执行自动化通道"**。
>
> 标注约定（同 `docs/inovance-autoshop-native-bridge.md`）：
> 【实证】= 本次亲自执行并复核（capstone 反汇编 / 实验窗口取证 / 进程与注册表快照）；
> 【推断】= 基于实证的推理；【未验证】= 无直接证据、禁止在实现中当作事实。
>
> **全部结论均为逆向所得，无汇川厂商背书；版本锁定 4.12.0.2 / x86。**
>
> 证据链落盘：`%TEMP%\plcmcp-handoff\cli-re\`（脚本 + `EVIDENCE.md` + `ev_*.txt` 反汇编摘录 +
> `snap/` 注册表与日志快照，不入仓库）。

---

## 一、结论摘要（先读这一节）

| 项 | 结论 |
|---|---|
| 开关全集 | AutoShop.exe 4.12.0.2 **只有三个**真实 CLI 开关：`-downprj`、`-offlinedebug`、`-downfile`【实证】。`-open` 与 `-Debug` **不存在**（早前任务书把它们列为开关是误读，见 2.3/2.4） |
| 唯一解析点 | 命令行解析只存在于一个函数（RVA **0x118B200**，InitInstance 内唯一调用点 0x1197CD8）；`__argc` 引用 1 处、`__wargv` 引用 5 处，全部在该函数内【实证】。AutoShop.exe 不导入 GetCommandLineW【实证】 |
| 语法 | `AutoShop.exe <开关> <路径> <0\|1>`——`__argc == 4` 才进入开关解析（否则立即返回 0 走正常启动）【实证】 |
| 文件参数打开 | 非开关参数（不带 `-`/`/` 前缀）被当作**要打开的工程文件**（MFC 标准解析 + 应用自带存在性检查）——CLI 文件参数打开路径真实可达【实证】；但对本机全部可用样本（14 个模板 + 1 个 V5.x 真实工程）都在"加载工程属性信息"一步失败【实证】 |
| 批处理路径 | **未发现可用的"CLI 打开+自动编译"批处理路径（阴性结论）**；`-downprj/-downfile` 是 PLC 下载语义（静态判定，实参实验被红线禁止）【实证 + 诚实记录】 |
| 自动化通道判定 | CLI **不能**成为编译/打开的可执行自动化通道：打开路径加载失败、下载开关需要 PLC 且协议未逆向。12939 下载协议逆向仍应优先 |

---

## 二、T1 开关语义表（静态逆向）

### 2.1 证据方法

- 字符串定位（`find_strs.py`）：UTF-16 字符串 `-downfile`(VA 0x1FFCE6C)、`-offlinedebug`(0x1FFCE80)、
  `-downprj`(0x1FFCE9C) 位于 .rdata；对每个字符串做全文件 imm32 交叉引用（`xref.py`），
  三者的 `push imm32` 全部落在 **0x118B200**（`ev_parser_0x118B200.txt`）。
- `__argc`(MSVCR90)/`__wargv`(MSVCR90) 引用点枚举（`wargv_refs.py`）：
  `__argc` 仅 1 处（0x118B229）、`__wargv` 5 处（0x118B248/0x118B2E6/0x118B333/0x118B3D2/0x118B41F）——
  **全部在 0x118B200 内**。即 AutoShop.exe 的命令行解析只有这一个 site；不导入 GetCommandLineW。
- 调用者（`find_calls.py`）：0x118B200 的唯一直接调用者 = InitInstance 区域 `call 0x1197CD8`
  （`ev_caller_0x1197CD8.txt`）。
- IAT/重定位解析（`iat_slots.py`/`reloc_resolve.py`）：比较函数 = mfc90u ord1599（`CString::operator==(LPCWSTR)`，
  MFC 导出仅按序号，名称不可得）；路径成员写入 = ord811（operator=）；构造 = ord286。
  注意 AutoShop.exe 是 **RELOCS_STRIPPED**，全文件 imm32 扫描即代码引用（无重定位噪声）。

### 2.2 开关语义表

| 开关 | 用法（还原） | 反汇编证据 | 行为 | 置信度 |
|---|---|---|---|---|
| `-downprj` | `AutoShop.exe -downprj <路径> <0\|1>` | 0x118B265 `push 0x1ffce9c`；0x118B22F `cmp [argc],4` | 路径→app+0x178；经单例链（0x1581a70→0x1581b60→0x162b8e0，非空才继续）调 0x1604320(布尔，B) 执行**工程下载**流；完成后 SendMessageW(hwnd,0x7EB) 继续启动 | 语义=下载流：高【实证】；0x1604320 内部细节：中（增量链接 thunk 未完全展开） |
| `-offlinedebug` | `AutoShop.exe -offlinedebug <路径> <0\|1>` | 0x118B281/0x118B2B6 两次 `push 0x1ffce80` | 与 -downprj 同一公共处理块（0x118B2A1），**额外把出参 B 置 1**（0x118B2D2 `[eax]=1`）——离线调试标志 | 高【实证】（分支结构）；B 的下游语义【未验证】 |
| `-downfile` | `AutoShop.exe -downfile <路径> <0\|1>` | 0x118B39A `push 0x1ffce6c` | 置 app+0x174=1（0x118B3BD）；调用者用路径副本+布尔调 0xa8d0b0（内部按 `[ebp+0x10]==3/4` 分派，疑为下载项类型）；**不走** 0x162b8e0 门 | 语义=文件下载流：高【实证】；分派参数语义：中【推断】 |
| （前置等待） | — | 0x1197CEF..0x1197D9F | 开关命中后先等 "load xml" 完成循环：10ms 步进（KERNEL32!Sleep），>2000 次打印 `outtime load xml 20s!`；随后 `cmp [app+0x174],0` 分派 | 高【实证】 |
| （就绪门） | — | 0x1197E30..0x1197E61 | 单例 vtable+0x64 检查；失败→`app+0x170 = 6` 并打印 `ProcessShellCommand error TestComplileDownload `（500ms 后经 `InterfaceMgr!g_SendLogStringToServer` 输出） | 高【实证】（调用图）；门语义【推断：启动/工程就绪检查】 |
| `-open` | **不存在** | 全文件唯一命中 = 代码页别名表 `"0628596arabic0500936cn-gb0900932sjis-open0712001cp12001"`（RVA 0x1BCA4D8，ISO/Windows 代码页别名）；315 个已装 PE 扫描无第二处 | 无 CLI 开关语义 | 高【实证，阴性】 |
| `-Debug` | **不存在（CLI）** | 3 处 xref（0x10F33A5/0x10F5092/0x10F5360）均为内部函数构造配置键名：`<名>-Config` + 布尔参数非 0 时追加 `-Debug`（`ev_debug_site1.txt`）；另有日志格式 `'%s(%s)-Debug'`（RVA 0x1B18130） | 非命令行开关，是**内部配置键后缀**（如 `X-Config-Debug`） | 高【实证，阴性】 |
| （无参数） | `AutoShop.exe` | 解析器 argc!=4 直接返回 0 | 正常启动（打开最近工程或新建向导，见 caller 0x119831D `OPEN_LAST project` / 0x119815F `DefaultOpenType...` 配置查询） | 高【实证】+ 实验 E2c |
| （裸文件参数） | `AutoShop.exe <工程文件路径>` | MFC 标准 CCommandLineInfo：非标志参数→FileOpen；应用存在性检查报 `工程 "%s" 已不存在或文件路径超出系统限制！`（.rsrc，RVA 0x3534066） | 尝试打开工程（实验 E2a/E2b 证实） | 高【实证】；打开成功条件见第四节【未验证】 |

### 2.3 `-open` 误读的来龙去脉【实证，阴性】

此前任务书（`docs/inovance-integration-plan.md`/P1 第九节）把 `-open` 列为待逆向开关，依据是 AutoShop.exe
UTF-16 串扫描命中。本轮复核：该命中在**字符集别名表**内部——

```
'0628596arabic0500936cn-gb0900932sjis-open0712001cp12001'   (RVA 0x1BCA4D8)
```

这是 ISO/Windows 代码页别名清单（`sjis-open`、`0712001`、`cp12001` 等是 cp932/windows-31j 的别名），
与命令行无关。全安装目录 315 个 PE 复扫（`scan_install.py`），除 Qt 自身字符串外无任何厂商二进制含
`-open` 开关。**结论：4.12.0.2 无 `-open` 开关。**

### 2.4 `-Debug` 同理【实证，阴性】

三处引用（0x10F33A5/0x10F5092/0x10F5360）都在形如 `f(this, 名字, 标志)` 的内部函数里：

```
push 0x1ff4600 ; u16 '-Config'   → 名字 + "-Config"
[标志 != 0] push 0x1ff45f0 ; u16 '-Debug' → 再追加 "-Debug"
随后在 this+0x180 的 map 里查找该键
```

即 `-Debug` 是**配置键名后缀**（模块调试配置项），与命令行无关。`'%s(%s)-Debug'`（RVA 0x1B18130）
是调试日志格式串。**结论：4.12.0.2 无 `-Debug` 命令行开关。**

### 2.5 shell 动词注册（旁证）

`'%s\\shell\\open\\%s'`（RVA 0x1BFCE48）、`'%s\\DefaultIcon'`、`'%s\\ShellNew'`、`' "%1"'`、`'command'`
（RVA 0x1BFCDxx-0x1BFCE1x）在 MFC RegisterShellFileTypes 风格的注册函数（0x1189010 起）中被拷贝使用，
与 `.hcp` 的 shell 关联（`AutoShop.Document`）一致；三个下载开关字符串**不在**注册函数中被引用
（其 xref 只有解析函数一处），即它们不是由安装器写进 shell 命令行的固定参数，而是独立的手工调用语法。

---

## 三、T2 实验证据（零 PLC 动作，全程只读观察）

实验框架：`run_exp.py`（自启进程 → 8/16/26/36s 四拍窗口快照 → 模态对话框子控件文本取证 →
`taskkill /F /T /PID` 清理 → tasklist 复核 → 日志差分）；`win_enum.py`/`win_child.py`（EnumWindows/
EnumChildWindows + GetWindowText，只读）。工作目录=`%TEMP%\plcmcp-handoff\cli-re\run1`（AutoShop 未写入）。

### 3.1 E2a：`-open <不存在路径>`（两次：PID 46112、376）

| 时点 | 取证 |
|---|---|
| 8s | 主窗口 `Afx:00400000:8:...` title=`AutoShop V4.12.0.2` 可见 en=1；42 窗口（tooltips×29、ComboLBox×4、GDI+ Hook Window、隐藏 #32770 等） |
| ~16s | 弹出模态 #32770，title=`AutoShop`，**主窗口 en=0**（模态阻塞），进程驻留 |
| 对话框文本 | **`工程 "C:\nonexistent\definitely_missing.hcp" 已不存在或文件路径超出系统限制！`** + [确定] 按钮（子控件 Static 文本，GetWindowText 只读取得） |
| 36s+ | 仍驻留（等待对话框关闭）；`taskkill /F /T` rc=0，复核无残留 |

结论：**非开关命令行参数被当作"要打开的工程"处理，CLI 打开路径真实可达**（无需任何 UI 交互即可进入）；
未知开关（`-open`）被忽略；缺失文件触发应用自带错误对话框而非崩溃/退出。

### 3.2 E2b：文件参数打开真实容器（三次）

| 实参 | PID | 结果 |
|---|---|---|
| `...\Template A1\Template A1.hcpp`（V4.12 模板，15,521 B） | 50748 | 模态 "AutoShop" 对话框：**`加载Hcp文件中的工程属性信息错误，工程打开失败！请尝试重新打开工程目录下Hcpp文件！`**；主窗口 title 不变（无工程名）；进程驻留；清理成功 |
| 同文件改名副本 `proj\Template A1.hcp`（temp 内） | 43264 | 同上，逐字一致 |
| `...\连续式立包机\连续式立包机.hcpp`（V5.x 真实工程，304,619 B） | 47876 | 同上，逐字一致 |

结论：
1. CLI 打开路径**确实解析 PPCH 容器**（扩展名 .hcpp/.hcp 均被识别——否则会走"文件不存在"分支）。
2. 失败点固定为"**加载工程属性信息**"——与 P1 的"Main blob 编码未恢复/AES 加密"一致：工程属性
   在加密 blob 内，直接打开路径未能完成解密/解析【推断：失败点在属性解密或容器成员缺失，未深入】。
3. V4.12 模板与 V5.x 真实工程同样失败——**不是版本门**；错误提示"请尝试重新打开工程目录下Hcpp文件"
   暗示正常打开路径可能还依赖工程目录上下文【未验证】。
4. 错误串在 .rsrc（RVA 0x355B6E2 / 0x3534066，LoadString 引用，无代码 xref）。

### 3.3 E2c 对照组：无参数启动（PID 31992）

主窗口 "AutoShop V4.12.0.2" 可见 en=1，四拍均**无任何模态对话框**——E2a/E2b 的对话框由文件参数引发，
不是启动基线行为。无参数启动正常驻留（后台可保持运行）。

### 3.4 副作用与回滚（全部可回滚）

- AutoShop 自行在安装目录写 `log\2026-10-28.log`（应用自身日志，内容 "support solve export data load finished!"，benign；这是应用启动行为，非本代理写入）。
- 注册表新增**空键** `Recent File List`（无值）→ 实验后 `reg delete` 回滚 → after2.reg 与 before.reg 字节一致【实证】。
- 安装目录无 PlcMcp 写入；工程模板原文件未动（只读引用）；temp 副本 `proj\` 已隔离。
- 清理方式：`taskkill /F /T /PID <自启 PID>` 全部 rc=0。此前任务书记载的 "taskkill AutoShop.exe → 拒绝访问"
  不适用于自启实例（当时是对运行中主实例的尝试）。

### 3.5 T2c：-downprj / -downfile 不做实参实验（红线判定）

静态语义（2.2）表明两个开关都是**自动下载流**：连接目标来自工程内 `NetConfig.cfg` 或无工程默认
`192.168.1.1:12939`（`docs/inovance-autoshop-native-bridge.md` 第十节）——**非回环地址**。触碰红线
"零 PLC 动作 / 不连接 127.0.0.1 以外地址"，**实参实验禁止**。仅归档静态结论与"需要 PLC / 需要何种参数"：
`AutoShop.exe -downprj|-downfile <路径> <0|1>`，路径疑为工程/下载文件，布尔疑为
m_bIsTestComplileDownload 类"测试编译下载"标志（m_bIsTestComplileDownload 字符串 ref 0x12C18A3；
Temp Project.hcp/临时工程.hcp ref 0x12C0ACD——详见 T3）。

---

## 四、T3："打开+自动编译"批处理路径（阴性结论）

**未发现可用的批处理路径。** 三条候选全部被实证/红线封死：

1. **`-open`/`-Debug` 开关**：不存在（2.3/2.4）。
2. **裸文件参数打开**：路径真实可达，但对本机全部 15 个样本在"加载工程属性信息"失败（3.2）——
   与 P3 的"源 blob AES 加密"一致；在属性加载未打通前，打开即失败，无从触发编译。
3. **`-downprj`/`-downfile`**：PLC 下载语义（3.5），实参实验被红线禁止；即便可行也需要工程内
   NetConfig.cfg 与下载协议配合。

**疑似存在但不可达的内部批处理路径**（静态线索，供后续）：
`m_bIsTestComplileDownload`（`err!` 日志，ref 0x12C18A3）、`Temp Project.hcp`/`临时工程.hcp`/`臨時工程.hcp`
（按语言三选一，ref 0x12C0ACD，配合 `.hcp` 扩展名构造，`ev_tempproject_site.txt`）、
`H530_OpenProject`（6 处 ref：0x12C2F0C/0x12C509B/0x12C4ECC/0x12C4ADB/0x12C488E/0x12C56CA）——
这批符号聚集在 0x12C0000-0x12C6000，疑似厂商自动测试工具（TestHost 风格）使用的
"测试编译下载"流水线：临时工程文件 → 编译（进程内 STCompiler.dll）→ 下载。其入口在二进制内但
没有已知的 CLI/UI 触发参数；驱动它需要 IDA 级深度逆向或厂商测试工具本体（未随产品安装，
安装目录无独立测试 exe）【推断 + 未验证】。

复现/取证脚本全部在 `%TEMP%\plcmcp-handoff\cli-re\`（`EVIDENCE.md` 有逐条命令），不进仓库。

---

## 五、未验证项（禁止当作事实）

1. `-downprj`/`-downfile` 实参行为（路径类型：工程包？目录？.down 文件？；布尔语义）——红线禁止实验。
2. `0x1604320`/`0xa8d0b0` 下载流内部（增量链接 thunk 未展开；连接目标来自工程/默认 192.168.1.1:12939）。
3. "工程属性信息"加载失败的确切原因（属性解密失败？容器成员缺失？工程目录上下文要求？）。
4. 裸文件参数打开**成功**所需的工程形态（本机无合法可打开样本；`HcppWriter` 生成的容器未验证）。
5. `H530_OpenProject` 批处理流水线的触发方式。
6. MFC 层对多文件参数/引号路径的行为细节（仅验证了单文件参数）。
7. AutoShop 升级后解析函数偏移/开关集的稳定性。

## 六、给集成阶段的接口建议

1. **CLI 不能成为编译/打开的可执行自动化通道**（本任务核心结论）：
   - 打开路径：CLI 文件参数真实可达，但工程属性加载对全部样本失败——在 P3 的"blob 解密"（EnDecrypt.dll
     `CAESAlg` 密钥导出）打通之前，任何"CLI 打开工程"的自动化都会止步于同一个模态对话框。
   - 编译路径：CLI 没有编译开关；`-downprj`/`-downfile` 是下载语义且需要 PLC。
2. **优先级**：**12939 下载协议逆向（mp_core.dll `download_file_handler` 帧格式）优先于 CLI 任何后续工作**——
   它是"编译产物 → daemon 装载"的必经之路（P3 第六节），而 CLI 这条线已到阴性终点；
   其次是 EnDecrypt.dll 密钥导出（打通 .hcp 源合成 → 属性加载 → CLI 打开才可能成功）。
3. 若后续必须走 CLI：`-downprj/-downfile` 需要审批门控（PLC 动作）+ 工程内 NetConfig.cfg 的真实目标；
   文件参数打开可作为"探测工程合法性"的信号源——模态对话框的 Static 文本（`win_child.py` 只读取得）
   能区分"文件不存在 / 属性加载失败 / 打开成功（无对话框 + title 含工程名）"三种状态，可作为
   doctor 类检查的本机取证手段（仅限本机诊断，不进 CI）。
4. 能力声明：`cli_automation` = **Unsupported**（打开失败 + 下载未验证）；文档不得宣称 CLI 可驱动编译。
