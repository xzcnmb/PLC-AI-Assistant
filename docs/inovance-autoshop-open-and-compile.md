# 汇川 AutoShop 工程打开失败根因与编译管线（E3）实证文档

> 本文档固化 E3（打开失败根因 + 编译管线逆向）的全部实测结论：.hcpp 打开失败的根因、
> 通过打开校验的**最小工程目录构造法**（已在真机 AutoShop 上实证打开成功）、全部编译(0x80E1)
> 的完整调用链、编译产物落盘路径，以及**进程内注入触发编译的实证**。
> 对象：AutoShop V4.12.0.2（x86，`D:\Inovance Control\AutoShop\AutoShop.exe`，
> 55,592,960 B，ImageBase 0x400000，**RELOCS_STRIPPED**——文件内 imm32 即代码引用）。
>
> 与 `docs/inovance-autoshop-cli.md`（CLI-RE）、`docs/inovance-autoshop-project-encryption.md`
> （D3：CUCodeCrypt 解密）、`docs/inovance-autoshop-project-format.md`（P1：PPCH 容器）、
> `docs/inovance-autoshop-native-bridge.md`（S2 桥方法学）配合阅读。
>
> 标注约定：【实证】= 本次亲自执行并复核（capstone 反汇编 / 自建 harness 实调 / 自启 AutoShop
> 实例窗口取证）；【推断】= 基于实证的推理；【未解】= 无可信证据，禁止当作事实。
> **全部结论均为逆向所得，无汇川厂商背书；版本锁定 4.12.0.2 / x86。**
>
> 证据链落盘：`%TEMP%\plcmcp-handoff\e3-open-compile\`（脚本 + 变体工程 + 桥源码/日志，不入仓库）。

---

## 一、结论摘要（先读这一节）

| 项 | 结论 |
|---|---|
| **7596 根因** | **`.hcpp`（PPCH 容器）根本不是"可打开工程"，是打包/下载格式**。CLI 裸路径把 .hcpp 直传给核心加载器的 `UCodeDecrypt(文件,文件)` **原地带内解密门**；容器二进制（NUL/非文本）无法作为文本文件读取并往返 → UCodeDecrypt 返回 0 → **7596**【实证：E1 打开 .hcpp 后文件字节未变（解密未发生）+ 7596 对话框】 |
| **可打开工程形态** | **目录形态**：`<名>.hcp`（= UCode 加密 UTF-16LE XML，即 .hcpp Main 记录的 blob）+ XML `<file>` 声明的文件放**同一目录**，内容 = .hcpp 文件记录的**完整内容（含 31 字节 "AutoShop" 头）**【实证 proj3/proj5：打开成功 + 11 文件全字节一致】 |
| **P1 证伪** | P1 "磁盘文件 = 31 字节头**之后**的负载"被证伪：磁盘文件**含头**（app 打开后重写/回存均带头；丢头 → "打开文件：VarList.gdt 时出错"）【实证 proj1 阴性对照】 |
| **7597 触发** | `atol(//project/Version) > 1` → 弹 62203 "所选工程文件版本高于当前程序，请先升级程序" + 7597【实证 Version=2 变体】 |
| **62220 触发** | `//project/file` 元素为空（count=0）→ "打开工程失败"（错误码 4）【实证无-file 变体】 |
| **全部编译链** | `WM_COMMAND 0x80E1` → 消息映射 `.data 0x22DD288` → 处理器 `0xA847A0` → 单例链 `0x1581A70`（根管理器，缓存于 `.data 0x2438E3C`）→ `0x1581CA0`（= root+0x34）→ **`0x15A2EC0`（全部编译入口，无参 thiscall）**【实证】 |
| **编译产物** | `<工程目录>\Compile\<名>.compile`（UCode 加密编译产物）+ `<工程目录>\<名>.foid` + 重打包 `<名>.hcpp`（=下载文件）【实证：0x80E1 经注入桥触发 3 次】 |
| **进程内触发** | **已实证**：注入 DLL 内 `root=0x1581A70(); cmp=0x1581CA0(root); 0x15A2EC0(cmp)` —— 零 UI 消息、零对话框、无异常，编译产物落盘【实证 ×3】 |

---

## 二、T1a：错误报告函数与错误码表【实证】

### 2.1 错误报告函数 0x16ADD F0

三个错误串的 `push imm32` 全部落在同一函数（VA 0x16ADD F0，`ret 8`，两栈参）：

```
report(sink=[esp+8] /*CString*，调用方传 单例+0x90*/, code=[esp+0xc]):
    code -= 1;  switch (code) via 跳转表 [0x16ADEC8 + code*4]:
        0 → LoadString(0x1DAC = 7596)   ; 属性信息错误
        1 → LoadString(0x1DAD = 7597)   ; 版本不兼容
        2 → LoadString(0x1DAE = 7598)   ; 文件列表错误
        3 → LoadString(0xF2FC = 62220)  ; "打开工程失败"（默认码 4）
    → 交 [0x1EB35E8]（CString 赋值 sink）
```

即**调用方传入的 code ∈ {1,2,3,4}**；三串的 LoadString push 实测位置：7596@0x16ADE53、
7597@0x16ADE63、7598@0x16ADE73（文件偏移 0x12AD253/63/73）。

### 2.2 错误码表【实证 + 实验】

| code | 资源 ID | 文案 | 触发条件（见第四节） | 实验证据 |
|---|---|---|---|---|
| 1 | 7596 | 加载Hcp文件中的工程属性信息错误，工程打开失败！请尝试重新打开工程目录下Hcpp文件！ | UCodeDecrypt 文件解密门失败 / XML 解析失败 / `//project/Version` 查询失败 | E1（.hcpp）+ E2（enc.hcp 首门） |
| 2 | 7597 | Hcp文件版本不兼容，工程打开失败！请升级Autoshop！ | `atol(//project/Version) > 1`（先弹 62203） | proj_v2（Version=2）复现 |
| 3 | 7598 | 加载Hcp文件的文件列表信息错误，工程打开失败！请尝试重新打开工程目录下Hcpp文件！ | `//project/file` XPath 查询返回 0 | 【静态】未单测复现 |
| 4 | 62220 | 打开工程失败 | `<file>` 元素为空（count=0） | proj_nofile（删全部 `<file>`）复现 |
| — | （对话框） | 打开文件：`%s` 时出错（62506/62507 拼接） | 声明文件在工程目录打开/解析失败 | proj1（VarList.gdt 丢头） |
| — | （对话框） | 工程 "%s" 已不存在或文件路径超出系统限制！（RVA 0x3534066） | CLI 文件参数不存在 | CLI-RE E2a（既有） |

### 2.3 五个加载函数 = 同一核心加载器的五个包装【实证】

D3 记录的 5 个 `.hcpp` 加载函数（VA 0x16A8EB0 / 0x16AF050 / 0x16AF660 / 0x16C0850 / 0x16C6360）
**全部收敛到核心加载器 `0x41D600`**（5 个 call：0x16A94FC / 0x16AF120 / 0x16AF7C8 / 0x16C1840 / 0x16C67CA）。
各自角色（按调用者）：

| 函数 | 角色（推断+串证据） | 直接调用者 |
|---|---|---|
| 0x16A8EB0 | 打开（含 "临时工程.hcp" 特判：文件名 == LoadString(126) "临时工程"+".hcp" → 取 exe 目录分支） | 0x158D050（← 0xA8EBFE，-downfile 流）、0x15909D0、0x1590DF0 |
| 0x16AF050 | 打开（同构包装，`this=[对象+0x14]`） | 0x16AEEE0（7 处） |
| 0x16AF660 | 打开（同构包装） | 无直接调用（虚表/间接） |
| **0x16C0850** | **主 .hcp 打开装载器**：内含 `m_bIsTestComplileDownload` 串引用（0x16C18A3）与 `H530_OpenProject begin`（0x16C2F0C） | 11 处（0x460746/0x630024/0x129DC49/0x158860C/0x15F843D/0x15F9349/0x160DADE/0x160E8C3/0x16C0433/0x16C86E8/0x16CB98D） |
| 0x16C6360 | 打开（同构包装，经 0x16AB150 取上下文） | 0x16B65E0（3 处） |

`H530_OpenProject 1..5` 日志串全部在 **0x16C3CD0**（H5U 系工程打开步骤函数），由 loader4 的
"begin" 引出——即 **0x16C0850 → 0x16C3CD0 = H5U 系（H530）工程打开流水线**，含
`m_bIsTestComplileDownload` 测试编译下载标志（CLI-RE 第四节的"疑似厂商自动测试流水线"即此）。

---

## 三、T1b：核心加载器 0x41D600 完整逆向【实证】

签名（从 5 个调用点反汇编还原）：

```
int __thiscall LoadHcp(ctx /*ecx*/, const wchar_t* path, int* outA, int* outB, int* outC)
// 返回 eax != 0 = 成功；失败时 *outC = 错误码 {1,2,3,4}（→ 7596/7597/7598/62220）
// ctx = 加载器上下文对象（196 字节，vtable 0x1EECFB4，构造于 loader1 的 0x16A93E4 处）的成员
//       [ctx+0x34] = CString 原文（未解密文本），[ctx+0x3C] = int 解密轮数计数
```

### 3.1 流程（关键分支全部反汇编核对）

1. `GetFileAttributesW(path)`（IAT 0x1EB1848）——**参数是文件路径**；若 READONLY 位则
   `SetFileAttributesW` 清除（0x1EB1960）。
2. **`CUCodeCrypt` ctor（0x1EB17F8）+ `UCodeDecrypt(PB_W0)(path, path)`（IAT 0x1EB17E4）**——
   **文件→文件原地带内解密**（src=dst=path）。
   - 【实证（自建 harness UcodeProbe/dbg2 实调）】`UCodeDecrypt(L"...enc_main.bin", L"...out.bin")`
     → rc=1，out.bin = 18,944 B 明文 XML（FF FE + `<?xml version="1.0"...`）——与 D3 的
     DecryptArithmetic 字节级一致（该导出内部经宽→窄转换后调 DecryptArithmetic，
     再窄→宽写回；读文件用 MFC CFile 语义 0x20=shareDenyWrite，写回 0x1001=modeCreate|modeWrite）。
   - **返回 0（读取/往返校验失败）→ `*outC = 1` → 7596，立即返回**（0x41D695/0x41D698）。
3. **解密轮自动探测循环**（0x41D6C3–0x41DA11）：`IQueryerCreater::Instance()`（IAT 0x1EB17F4，
   XML 查询器单例）`vtbl[3](str)` = 解析；返回 0 → `UCodeDecrypt(path,path)`/`UCodeFileEncrypt(PB_W)`
   再解密 1–2 轮重试（`[ctx+0x3C]` 计数）；全部失败 → `*outC = 1`（7596）。
4. 解析成功后（0x41DA3F）：
   - `vtbl[5](&str, "//project/Version")`（XPath 0x1EED798）→ 查询失败或值缺失 → `*outC = 1`（7596）；
   - `g_atol(value)`（IAT 0x1EB17E8）`> 1` → `MessageBox(62203 "所选工程文件版本高于当前程序，请先升级程序")`
     → `*outC = 2`（**7597**，0x41DB5D/0x41DB60）；
   - `0x423610(ctx, &queryer, outB)` = 文件列表装载 → 失败路径 `*outC = 1`（7596，0x41DBF0…0x41E1BF 共 10 处）；
   - `vtbl[0](&list, "//project/file")`（XPath 0x1EED3A4）返回 0 → `*outC = 3`（**7598**，0x41E602/0x41E605）；
   - `vtbl[?](&count, "//project/HCPFileVer")`（XPath 0x1EED378）+ `0x422E70/0x422FE0/0x422040`
     三个装载步骤 → 失败 `*outA/*outB = 1`（7596，0x41E69A/0x41E6B2/0x41E6CA）；
   - `<file>` 元素为空（count=0）→ `*outC = 4`（62220，0x41ED38/0x41ED3B）【实证 proj_nofile】。
5. `//project/ProjEncodingFormat`（XPath 0x1EED3C4）缺失时默认写 "GB2312"（0x1EECF9C）。

### 3.2 XML 查询器使用的 XPath 全集【实证（串提取）】

`//project/Version`、`//project/file`、`//project/HCPFileVer`、`//project/ProjEncodingFormat`、
`//project/EcamSetting`——工程属性 XML 的根为 `<project>`（与 D3 解密明文一致）。

---

## 四、T1c：变体实验（打开成功的最小构造规则）【实证，核心交付】

实验框架：`run_exp.py`（自启 AutoShop 实例 → 8/16/26/36s 四拍窗口快照 → 模态对话框子控件文本取证
→ `taskkill /F /T` 清理 → 文件 SHA256 前后比对）。变体全部写在
`%TEMP%\plcmcp-handoff\e3-open-compile\`（未写仓库、未写安装目录）。

### 4.1 实验矩阵与结果

| # | 变体 | 形态 | 结果 |
|---|---|---|---|
| E1 | `.hcpp` 容器副本（Template A1.hcpp 15,521 B 原样） | PPCH 容器 | **7596 对话框**；文件字节**未变**（SHA 前后一致）——UCodeDecrypt 门失败，容器未解密 |
| E2 | `enc.hcp`（= .hcpp Main 记录 blob 18,944 B，即 UCode 加密 XML） | 单文件 | **属性装载通过**：文件被**原地带内解密**（SHA 变了）；错误变为 "打开文件：…VarList.gdt 时出错"（找声明文件） |
| proj1 | 同 E2 + 容器提取的文件放同目录，但**丢 31 字节头**（P1 解释） | 目录 | 仍 "打开文件：…VarList.gdt 时出错" |
| **proj2** | 同 proj1 但 VarList.gdt 用**完整内容（含 31 字节头）** | 目录 | **打开成功**：主窗口 title = `AutoShop V4.12.0.2 …\proj2 - [MAIN]`，零错误对话框 |
| **proj3** | **全部 11 个声明文件均含 31 字节头** | 目录 | **打开成功 + 完美往返**：11 文件打开后**全字节一致**（app 未重写任何一个）；副作用仅 BackUP/、Temp/、config.ini、ModbusConfig.cfg（app 自动建）、.hcp 重加密 |
| proj_v2 | 同 proj3 但 XML `<Version>2</Version>` | 目录 | **7597**：先弹 "所选工程文件版本高于当前程序，请先升级程序" |
| proj_nofile | 同 proj3 但删除全部 `<file>` 元素 | 目录 | **62220**："打开工程失败"（错误码 4） |

### 4.2 最小构造规则（能让 AutoShop 成功打开的工程）

1. **目录**（任意位置；AutoShop 对其有完全写权限）内放：
   - `<名>.hcp`：**UCode 加密的 UTF-16LE XML**（算法/密钥见 D3：11 字节表 "SclSoftware"
     ADD/XOR 流密码）。XML 根 `<project>`，含 `<Version>1</Version>`（**必须 ≤1**）、
     `<GCMModal>H3U</GCMModal>`、`<HardwareFile>…</HardwareFile>`、**`<file id="N">` 元素列表**等。
   - XML `<file>` 元素里 `<FileName>` 声明的**每个文件放同一目录**，内容 = .hcpp 容器对应文件记录的
     **完整字节（含 31 字节 "AutoShop" 头）**。缺声明的文件被容忍（app 自动建空件，如
     ModbusConfig.cfg）；多余文件（如 LoginPassowrd.dat，不在 XML 声明里）无害。
2. 打开方式：`AutoShop.exe <绝对路径>\<名>.hcp`（CLI 裸路径参数，MFC FileOpen 路径）。
3. **成功判据**：无模态对话框 + 主窗口 title = `AutoShop V4.12.0.2 <目录> - [<编辑文件名>]`
   （实测 "[MAIN]"）。取证用 EnumWindows(pid+class `Afx:00400000:8`)，只读。
4. **.hcp 会被原地带内解密（消费）**——每次实验/每次打开都必须用全新副本；失败/关闭路径 app 会把它
   **重加密回写**（history 追加一条 + `ProjVersion` 升级为当前 IDE 版本，实测 19,306 B）。
5. 打开副作用（均良性、可回滚）：`BackUP\<名>_bk_<ProjVersion>_<时间戳>.hcpp`（app 自己的重打包）、
   `Temp\` 目录、`config.ini`、空 `ModbusConfig.cfg`；注册表 `Recent File List` 追加
   （实验后 `winreg.DeleteKey` 回滚，已验证与实验前一致）。

### 4.3 对 P1 文档的修正

P1 第十节"`Files` 中内容是 AutoShop 头之后的负载（与磁盘上的目标文件内容同构）"**被本实验证伪**：
磁盘上的目标文件 = **含 31 字节头的完整内容**。证据：proj2/proj3 含头文件被原样接受且
proj3 全部 11 文件打开后字节一致；proj1 丢头的 VarList.gdt 打开报错；app 打开后重写/回存的
MAIN.LD 亦带头（328 B，头 "AutoShop"+`2.6`/`6.0`）。
`.hcpp` 的 PPCH 容器 = `Main 记录（加密 .hcp blob）+ 文件记录（磁盘文件原样）`。

---

## 五、T2：全部编译（0x80E1）调用链与产物【实证】

### 5.1 命令分发 → 编译入口

```
WM_COMMAND(wParam=0x80E1)（菜单/工具栏"全部编译"，命令 ID 资源段映射既有）
  → MFC 消息映射表：.data 0x22DD288 = AFX_MSGMAP_ENTRY
      { msg=0x111(WM_COMMAND), code=0, id=0x80E1, last=0x80E1, sig=0x39(void(void)), pfn=0xA847A0 }
  → 0xA847A0（thiscall，主框架/应用成员，仅 3 行）:
        root  = 0x1581A70();          // 根管理器单例（476 B，缓存于 .data 0x2438E3C）
        cmpMgr = 0x1581CA0(root);     // = *(root+0x34)：全部编译管理器
        0x15A2EC0(cmpMgr);            // 全部编译入口（无参 thiscall）
```

根管理器成员表（单例 getter 全部为 `[root+offset]` 访问器）【实证】：
`+0x04`=下载门管理器(0x1581B60)、`+0x0C`=0x1581B40、`+0x20`=0x1581C00、`+0x24`=报错器
(0x1581C20，其 +0x90 = 错误 CString sink)、`+0x2C`=0x1581C60、**`+0x34`=全部编译管理器(0x1581CA0)**、
`+0x48`=0x1581D40。

### 5.2 全部编译入口 0x15A2EC0 内部门【实证（反汇编）】

| 门 | 调用 | 失败行为 |
|---|---|---|
| 1 | `0x1581B40(root+0x0C)→0x163C760→vtbl[65]` | — |
| 2（下载门） | `0x1581B60(root+0x04)→0x162B2E0` 非 0 | 退出（下载/调试占用中） |
| 3 | `0x1581C00(root+0x20)→0x1605860` 为 0 | 退出 |
| 工程打开检查 | `0x15929D0` 为 0 | msgbox(64099 "请保证添加的模块连续") 退出 |
| **当前工程检查** | `0x161B620` 为 0 | **msgbox(62220 "打开工程失败") 退出——编译必须先有已打开工程** |
| 快照 | `0x43DDD0` ctor + `0x1592B60` 收集文件列表 | 0 个文件 → 退出 |
| 逐文件提交 | 每文件 new 0x4C B 编译项（`+0x14`=文件对象, `+0x18`=1, `+0x1C`=2）→ `0x4179A0` 提交 | 异步 worker 执行 |

### 5.3 完整管线（编译 + 生成下载文件）= 0x15A3780【实证（反汇编 + 串证据）】

- 签名：`void __thiscall CompilePipeline(this, int mode)`——`[ebp+8]` 与 2/4 比较（编译模式分支）。
- **调用者 = 命令 0x8257 的处理器 0xA81B00**（消息映射 .data 0x22DD0A8，同表；0x8257 为另一命令——
  按资源段映射待查，处理链读配置键 "bShowSymbol" 后进入本管线）。
- 管线内状态串（LoadString）：**899=完成程序编译 / 900=完成编译 / 901=生成下载文件成功！**
  （0x15A3A46/0x15A3A96/0x15A6F26/0x15A709C）——编译与生成下载文件在同一函数。
- 逐文件编译经文件对象 `vtbl[83]` + `__RTDynamicCast`（0x1800606）取编译接口后调用。
- **STCompiler.dll 在 AutoShop 启动时即装载**（CRT 动态初始化器 0x1AC1B60：`mov ecx, 0x23ED078;
  call 0x45ED30`——固定全局编译管理器 0x23ED078；装载函数 0x1584B50 内
  `LoadLibraryW("STCompiler.dll")` 失败 → "STCompiler.dll load fail!"（0x1FFCB40））。
- `H530_OpenProject`（0x16C3CD0，步骤 1-5 日志）与 `m_bIsTestComplileDownload`（0x2002864 串，
  引用 0x16C18A3 在 loader4 内）= **H5U 系打开+测试编译下载流水线**——厂商自动测试路径的入口
  在二进制内（loader4 的 11 个调用者），但无已知 CLI/UI 参数触发；驱动它需注入调用
  （与 T3 同法，入口待后续按调用者逐一核对）【推断 + 部分【实证】】。

### 5.4 编译产物落盘（0x80E1 经注入桥触发 3 次实测）【实证】

| 产物 | 路径 | 形态 |
|---|---|---|
| **编译产物** | `<工程目录>\Compile\<名>.compile` | UCode 加密二进制（实测 SBR_001.compile 480 B；D3 算法解密后 = `53 41 03 00 02 00 01 50 …` 结构化数据）；**注意只出现 SBR_001 一个**（MAIN/INT 未落 .compile，【未解】：疑为按 POUID/类型选择或异步未完即被 taskkill） |
| **foid 表** | `<工程目录>\<名>.foid` | 实测 ecttrans.foid 9 B（`00 00 00 30 01 00 …`） |
| **重打包下载文件** | `<工程目录>\<名>.hcpp` | app 自己的 PPCH 重打包（实测 15,742 B）——即"生成下载文件"的产物 |
| 空目录 | `<工程目录>\Temp\` | `\Temp\stprog.o / symbol.sym / varsinfo.foid / userprog.foid`（串 0x1F59100/0x1FFE6B8/0x1F590BC/0x1F59210，被 0xA63xxx–0xA6Axxx vtable 类引用=下载文件生成器）为 **PLC 下载载荷环节**的临时产物；0x80E1 触发未到该环节（实测 Temp 为空）【实证 + 静态】 |

**产物可用 `tools/AutoShopNativeBridge/src/crypto/AutoShopProjectCrypt.cs`（D3 交付）解密读取。**

### 5.5 "能否在打开工程后独立触发"——**能**（T3 实证）

编译链不消费任何 UI 消息状态：入口 `0x15A2EC0` 只依赖 (a) 根管理器单例（自取）与 (b) 当前工程
（`0x161B620` 门——**必须已打开工程**）。UI 通道（SendInput/PostMessage WM_COMMAND）被输入过滤
封死（阶段二既有结论），**进程内直接调用是唯一可靠触发路径**（下节）。

---

## 六、T3：进程内命令分发与最短触发路径【实证（注入桥 3 次成功）】

### 6.1 命令分发机制

- 命令分发 = MFC 标准 `CWnd::OnCmdMsg` 链：`WM_COMMAND → AFX_MSGMAP_ENTRY 表线性匹配 → pfn`。
  消息映射表为静态数据（本类在 `.data 0x22DD0A8–0x22DD2A0` 区，全量可枚举）。
- 处理器签名：sig 0x39 = `void __thiscall f(void)`（主框架成员，this = 主框架指针）。
- **主框架 HWND 获取**：`EnumWindows` 按 pid + 类名前缀 `Afx:00400000` 过滤（title 形如
  `AutoShop V4.12.0.2 <工程目录> - [MAIN]`）；MDI 框架，菜单无 HMENU（owner-drawn）。

### 6.2 最短触发路径（注入 DLL，零 UI 消息）

```c
// AutoShop.exe 4.12.0.2 x86，ImageBase 0x400000，RELOCS_STRIPPED => VA 固定
typedef void* (*PFN_GETROOT)(void);                 // 0x1581A70：根管理器单例
typedef void* (__thiscall *PFN_GETMEMBER)(void*);   // 0x1581CA0：ecx=root，返回 *(root+0x34)
typedef void  (__thiscall *PFN_COMPILEALL)(void*);  // 0x15A2EC0：全部编译入口

void* root    = ((PFN_GETROOT)0x001581A70)();       // 实测 0x07093FC0 / 0x07103FC0
void* cmpMgr  = ((PFN_GETMEMBER)0x001581CA0)(root); // 实测 0x0DD50BC8
((PFN_COMPILEALL)0x0015A2EC0)(cmpMgr);              // 全部编译（异步提交后立即返回）
```

前置条件：**工程已打开**（`0x161B620` 当前工程门非空）——用 CLI 裸路径 `.hcp` 打开（第四节构造法）。
编译为**异步提交**（0x4179A0），调用立即返回；产物在数秒内落盘（实验等 25–40 s 后收取）。

**同法可触发的相邻命令**（同一根管理器链，处理器均在消息映射表内）：
`0x8072`=下载（处理器 0xA81D00——**PLC 动作，默认禁止，真机必须审批门控**）、
`0x8063`=更新 UI、`0x804B`/`0x81AE`/`0x83D1`（待逐一核对语义）、`0x8257`→编译+生成下载管线 0x15A3780。
保存/生成下载文件的处理器入口同表可枚举（`AFX_MSGMAP_ENTRY` 全量扫描即可）。

### 6.3 实证记录（E3CompileBridge.dll / E3Injector2.cpp，3 次运行）

| 次序 | 工程 | 触发 | 结果 |
|---|---|---|---|
| 1 | proj4（CLI 打开成功后注入） | 事件 `E3CompileGo` → 上述三行调用 | `compileAll returned`（无异常）；`Compile\SBR_001.compile`(480B) + `ecttrans.foid`(9B) 落盘 |
| 2 | proj5（同上） | 同 | 同（可复现） |
| 3 | proj6（**__thiscall 修正版**） | 同 | 同（调用约定按 6.2 后仍成功，bridge 日志 bridge_log.txt） |

注入器（E3Injector2.cpp）：`CreateProcessW(AutoShop.exe <proj.hcp>)`（**只注入本工具自己启动的实例**，
PID 归属保护）→ 20 s 等 title 含工程目录 → `CreateRemoteThread+LoadLibraryW` 注入 → 命名事件触发 →
窗口快照取证 → `taskkill /F /T` 清理。全程零点击、零 PLC、零安装目录写入；
注册表 `Recent File List` 已回滚（`winreg.DeleteKey`，复核键已消失）。

---

## 七、给 E2（注入桥）与 E4（MCP 集成）的接口

### 7.1 打开工程（前置）

1. 源 = `.hcpp`（打包格式）：解包 = P1 `HcppReader`（`src/PlcMcp.Engineering/Workers/AutoShop/ProjectFormat/`）
   → `MainRecord.MainBlob` 原样写 `<名>.hcp` + `Records` 中文件记录**原样**（**不要剥 31 字节头**——
   P1 语义修正）写同目录。
2. 启动：`AutoShop.exe <绝对路径>\<名>.hcp`（**自启实例**，`CREATE_NEW_PROCESS_GROUP`，工作目录=临时目录）。
3. 成功判据：≤20 s 内 EnumWindows(pid, `Afx:00400000`) title 含 `<目录名>`；模态 `#32770` 对话框
   出现即失败（子控件 Static 文本可区分 7596/7597/62220/打开文件错误四种状态——doctor 可复用）。
4. **`.hcp` 被原地解密消费**：每次打开必须全新副本；实验后 `BackUP/`、`Temp/`、`config.ini` 为
   app 自建（良性）；注册表 `Recent File List` 需回滚。

### 7.2 编译（打开成功后，从注入 DLL）

1. 注入桥 DLL（`CreateRemoteThread+LoadLibraryW`，仅限本代理/工具自启实例）。
2. 触发：`root=0x1581A70(); cmp=0x1581CA0(root); 0x15A2EC0(cmp)`（第六节代码，异步）。
3. 收取产物（25–40 s 后）：`<工程目录>\Compile\*.compile` + `*.foid` + 重打包 `*.hcpp`；
   解密用 `AutoShopProjectCrypt.DecryptBlob`（D3）。
4. 失败可见性：编译错误弹窗/输出窗为 UI 态——桥侧以"产物文件是否出现 + bridge 日志"为准。
5. **能力声明建议**：`autoshop_open`（构造目录+CLI 打开）= Experimental（本机实证 proj2/3/5/6）；
   `autoshop_compile`（注入触发）= Experimental（本机实证 ×3，真机/多文件场景未全验——见 5.4 未解项）；
   `autoshop_download` = **Unsupported/审批门控**（0x8072 处理器已知但禁止调用）。

### 7.3 未解项（禁止当作事实）

1. 为何 0x80E1 只落盘 `SBR_001.compile`（MAIN/INT 未落 .compile）——疑按文件类型/POUID 选择，
   或异步未完即被 taskkill；待更长等待复验。
2. 7598（`//project/file` 查询返回 0）未单测复现（实验中删 `<file>` 得 62220 而非 7598）。
3. `\Temp\stprog.o/symbol.sym` 等下载载荷产物未被 0x80E1 触发到（属 0x8257/下载管线后半段）。
4. 0x8257 命令的菜单名/语义（资源段菜单 ID 映射待补）；0x804B/0x81AE/0x83D1 处理器语义。
5. 根管理器各成员（+0x0C/+0x20/+0x2C/+0x48）对应的管理器类名与完整能力面。
6. 版本漂移：全部 VA 针对 4.12.0.2（RELOCS_STRIPPED，无 ASLR 基址漂移风险，但升级即失效）。

---

## 八、实验红线合规声明

- **零 PLC 动作**：全部实验无任何 PLC 连接/写操作（编译为离线行为；未触发 0x8072 下载）。
- **自启实例**：所有 AutoShop 实例由本代理脚本启动（E3Injector/CreateProcessW），窗口取证只读，
  实验后 `taskkill /F /T` 清理并复核无残留；未触碰用户交互中的实例（实验时无他实例运行）。
- **安装目录零写入**：InterfaceMgr.dll 仅**复制**到交接目录（UcodeProbe 实调用）；模板样本只读。
- **注册表回滚**：`Recent File List` 键实验后已删除（与实验前状态一致，已复核）。
- 容器/工程变体只写交接目录 `%TEMP%\plcmcp-handoff\e3-open-compile\`（不入仓库）。
- **全部结论均为逆向所得，无汇川厂商背书；版本锁定 4.12.0.2 / x86。**

---

## 九、追加（F1，2026-10-28）：编译入口的完整签名证据文

T3 实证链（第六节三行调用）的"调用约定+参数+返回+异步时序+前置条件"签名证据已整理成文，
并入 `docs/inovance-autoshop-write-signatures.md` 第五节（F1 写面文档）。

| 项 | 结论（全文见写面文档第五节） |
|---|---|
| 调用约定 | `0x1581A70` 无参单例 getter（__cdecl）；`0x1581CA0` = `mov eax,[ecx+0x34]`（__thiscall 访问器）；`0x15A2EC0` = **无参 __thiscall**（ecx=cmpMgr；E3 第 3 次实验的 __thiscall 修正版，×3 实调成功） |
| 参数/返回 | 无栈参、无返回值消费（处理器 0xA847A0 不读 eax）；成功以产物文件出现为准 |
| 异步时序 | 入口内快照（0x43DDD0+0x1592B60）→ 逐文件 `new 0x4C` 编译项（+0x14=文件对象,+0x18=1,+0x1C=2）→ `0x4179A0` 异步提交后立即返回；产物 25–40s 落盘 |
| 前置条件 | 门 `0x161B620`（当前工程）非 0——必须已打开工程；下载门 `0x162B2E0` 非 0 = 占用中退出 |
| 注入接线 | `compileEntryRva=0x15A2EC0` + 第五节签名证据 = E2 `HandleCompile` 的"地址已配置+签名已验证"两条件均已齐（F1 交付） |
