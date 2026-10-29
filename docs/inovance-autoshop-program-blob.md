# 汇川 AutoShop 主程序 blob 逆向 + 模拟器闭环实验（P3）

> 本文档固化 P3（主程序 blob 侦察 + "生成程序→模拟器运行"闭环实验）的全部实测结论。
> 证据来源：真实模板样本的只读分析、h5u_daemon.exe 工作副本上的受控实验
> （**未修改安装目录任何文件**；实验全程在
> `C:\Users\18735\AppData\Local\Temp\plcmcp-handoff\p3-sim-workspace\` 的工作副本上进行）。
> 与 `docs/inovance-autoshop-project-format.md`（P1 容器格式）、
> `docs/inovance-autoshop-simulator.md`（P2 模拟器）配合阅读。
>
> 标注约定：【实证】= 真实样本/运行实例上执行并复核；【推断】= 基于实证的推理；
> 【未解】= 无可信证据，实现中禁止当作事实。能力声明纪律沿用 `docs/contracts.md`。
>
> **全部结构均为逆向所得，无汇川厂商背书。**

---

## 一、结论摘要（先读这一节）

| 项 | 结论 |
|---|---|
| Main blob 结构 | **[payload][cycle] 逐字节交错**：奇数位是固定 11 字符混淆循环 `"SlotaecSfwr"`（blob 空间周期 22），其余字节承载 payload【实证】 |
| payload 内容 | 高熵（7.981/7.999 bits/byte，与随机不可区分），**AES 加密**（AutoShop 自带 `EnDecrypt.dll` 导出 `CAESAlg::Cipher/InvCipher`）；**内容不可恢复**【实证形态 + 加密推断，未解密】 |
| blob ↔ stprog.o | **无同构段**（8 字节公共串数为 0）：blob = .hcp 源工程序列化；stprog.o = ARM ELF 编译产物【实证】 |
| daemon 复制目的地 | `<Simulator>\app\`（daemon chdir 进 `app\`，即模拟的 `/am600-nand/app`）；`public.dll` 内命令字符串 `XCOPY /E/Y .\default\app\* .\`【实证】 |
| daemon 是否重新编译 | **否**——启动只复制文件；stprog.o/symbol.sym 三个启动周期字节级不变（mtime 保持 Jul 21）；编译步骤 = AutoShop IDE（进程内 `STCompiler.dll`，无导出）【实证】 |
| daemon 是否读 project.hcpp/stprog.o | **启动时不读不校验**：garbage project.hcpp + 破坏 ELF magic 的 stprog.o 均无任何反应【实证】 |
| "生成程序→模拟器运行"闭环 | **未打通**（T3 条件不成立）：源 blob 加密不可合成、编译器在 IDE 进程内不可驱动、daemon 只经 12939 下载协议装载编译产物【实证 + 诚实记录】 |

---

## 二、T1：Main blob 结构侦察（区段图）

### 2.1 方法

用 python zlib/struct 独立解出两个模板样本（与 P1 的 C# 解析器交叉印证，结果一致）的
Main 记录 blob，落盘到 `%TEMP%\plcmcp-handoff\p3-blob-recon\{a1,libao}_mainblob.bin`，
然后做字节直方图、类别序列自相关（A=可打印 / H=高位 / C=控制字符）、逐字节奇偶拆分、
已知签名搜索（ELF/zlib/gzip/PK/PPCH）、已知指令名搜索（MC_MoveAbsolute/MOV/LD/SET/
Software/Slot/Project/Program/Main）、以及 stprog.o/symbol.sym 的公共串交叉印证。

### 2.2 区段图（Template A1：blob 18,944 B；连续式立包机：blob 263,612 B）

| 区段 | 形态 | 含义 | 置信度 |
|---|---|---|---|
| `blob[0..2)` | `62 6a`（"bj"），两工程相同 | payload 序列 prologue（密文前 2 字节） | 高【实证】 |
| 奇数位 `blob[1::2]` | 固定 11 字符循环 **`"SlotaecSfwr"`**（blob 空间周期 22；相位锁定：blob[21]='S'=循环位 0，blob[3]='o'=循环位 2）；首槽 blob[1]='j' 为例外（两工程相同） | 混淆/校验填充层；A1 纯度 99.27%（69 偏差字节）、立包机 99.84%（210 偏差字节） | 高【实证】 |
| 偶数位 `blob[0::2]` + 偏差字节 | 高熵 payload：A1 9,541 B / **7.981 bits/byte**、可打印 37.0%（=随机分布 95/256）；立包机 132,016 B / **7.999 bits/byte** | = `.hcp` 源工程内容，**AES 加密**（见 2.4）；字节直方图 256 种全出现 | 形态高【实证】/ 内容【未解】 |
| 17 处（A1）/ 56 处（立包机）循环挂起区 | 1-13 字节连续双奇偶 payload（循环被挂起后按相位恢复，全程无相位漂移） | 数据侵入点；A1 首个挂起区 = blob[181..193]，**恰为两工程的分歧点**（公共前缀恰 180 字节） | 形态高【实证】/ 语义【推断：疑为加密器帧边界】 |
| 两工程公共前缀 | 前 **180 字节**字节级相同（两个不同工程） | 工程无关样板（固定密钥/固定头密文） | 高【实证】 |

### 2.3 关键实测数字

- 类别序列自相关：周期 2 匹配率 **0.889**（第一周期内偶数位 A/H 混合、奇数位近全 A）；
  无其他显著周期（周期 1..32 全扫）。
- 奇数流字节直方图：`S`(0x53) 出现 1708 次（A1）= 每循环 2 次，其余 9 字符各 ~855 次
  = 每循环 1 次；**99.3% 的奇数位字节只落在 10 个字符 {S,l,o,t,a,e,c,f,w,r} 上**。
- 循环字符集 = "Software" + "c" + "l" + 额外 "S" 的字母重排（10 个互异字符）；
  语义未解，按固定混淆常量对待【实证形态，语义未解】。
- payload 解压尝试：raw deflate（wbits=-15）、zlib、gzip、bzip2、lzma magic 全部未命中；
  XOR keystream（11 字节循环全 11 相位 + 22 字节 key 全 22 相位）、加减法、位反转、
  半字节交换、ROL/ROR 1-7、bit 交织抽取全部不产生可识别文本/签名【实证】。
- payload 熵 7.981/7.999 bits/byte（8.0 = 理论随机）。

### 2.4 payload 加密的厂商证据

- `D:\Inovance Control\AutoShop\EnDecrypt.dll`（x86）导出：
  `CAESAlg::Cipher/InvCipher/CipherN/InvCipherN/GetPassword`（带 `PASSWORDTYPE` 枚举）、
  `CEncryptAlg::Encrypt/EncryptBlock/EncryptFrame/CreateNewKey/GetKey/Insert`、
  `CEnDecryptAlg`——AES 族加密器，密钥由私有 `CreateNewKey/GetKey` 管理【实证（导出符号）】。
- `EncryptionToolDll.dll` 导出 `CEncryptAlg::*` + `DestFile/DestFile1`。
- payload 无任何压缩/编码签名且熵 ≈8.0 → **AES 加密**为最强解释【推断，未解密】；
  密钥恢复属深度逆向（IDA/动态跟踪），本任务不尝试。

### 2.5 blob ↔ 模拟器文件交叉印证

- blob 与 `Simulator\default\app\stprog.o` / `symbol.sym` 的 8 字节公共串数 = **0**【实证】
  → blob（源工程序列化）与 stprog.o（编译产物 ARM ELF）**无同构段**，
  "模板=源工程序列化、模拟器工程=编译产物"成立。
- `stprog.o`：ARM（e_machine=0x28）、32 位小端、REL（可重定位）、节
  `.text/.rel.ARM.exidx/.ARM.attributes/.symtab/.strtab`、符号 `_HC_EMPTYFUC`
  （空函数表）= 出厂"空程序"【实证】。
- `symbol.sym`（84 B）：`62 6d 79 73 40 00...`（"bmys@"，"symB" 字节反序）+ 符号偏移表
  0x40/0x44/0x48/0x4C + 全零 = 空符号表【实证】。

---

## 三、T2：daemon 工作副本实验（复制目的地 / 是否重新编译 / 是否读工程文件）

### 3.1 复制目的地（已定位）

- **目的地 = `<Simulator>\app\`**（daemon 启动后 chdir 进该目录 = 模拟控制器
  `/am600-nand/app` 文件系统；`mp_core.dll` 内字符串 `/am600-nand/app`）【实证】。
- 机制字符串（`public.dll`）：`DEL /R/F/Q .\*.foid`、`DEL /R/F/Q .\project\*`、
  `RD /S/Q .\project`、`.\default\app`、**`XCOPY /E/Y .\default\app\* .\`**、
  `Load default application.`、`.\userprog.foid`、`.\project`【实证】。
- "复制了 14 个文件" = `default\app` 树恰好 14 个文件（autosys.cfg + 7 个 .foid +
  stprog.o + symbol.sym + project\project.hcpp 等）【实证，逐文件清点】。
- daemon 同时在 `app\default\` 维护 `default\` 的嵌套镜像（递归复制，
  mtime 保留），后续启动的 XCOPY 从该嵌套镜像取源【实证，实验 1 观测】。

### 3.2 实验设计（全程工作副本，安装目录零写入）

1. `robocopy Simulator → %TEMP%\plcmcp-handoff\p3-sim-workspace\Simulator`（19 MB 全量副本）。
2. 对照组：原始 default（project.hcpp = 7 字节 ASCII `"Empty\r\n"`）→ 启动 → 观测。
   结果与协调人实例一致：app\ 重建 14 文件、日志 `[PowerOn]`/`[FirstBurn]:nonvolatile.foid
   file is not exit.`/Update 循环、Modbus D0-9/D100-109/R32768+/线圈全 0、
   0.0.0.0:502 + 127.0.0.1:12939 监听【实证】。
   （注：协调人 daemon PID 36636 在本实验开始前已自行退出，netstat 502/12939 无监听者，
   故端口无冲突；全程未杀任何他人实例。）
3. 实验 1：改 default\app\project\project.hcpp = Template A1.hcpp（15,521 B PPCH），
   app\ 已存在时启动 → **app\project 未被更新**（仍是 "Empty"）；
   嵌套 `app\default\app\project\` 被更新【实证 → 首烧复制只在文件缺失时发生】。
4. 实验 2：删除 app\（强制首烧路径）+ Template A1 → **被复制到
   `app\project\project.hcpp`**（15,521 B，PPCH magic，mtime 保留）；daemon 正常运行，
   寄存器全 0、日志无编译/装载痕迹、stprog.o/symbol.sym md5 与原文件相同【实证】。
5. 实验 3：破坏 stprog.o ELF magic（7f→00）→ 复制后 daemon 正常启动、无错误日志、
   寄存器正常【实证 → 启动不加载/校验 stprog.o】。
6. 实验 4：project.hcpp = 1024 B 随机字节 → 复制后 daemon 正常启动、无错误【实证 →
   启动不解析 project.hcpp】。

### 3.3 结论

1. **daemon 不在启动时读 project.hcpp，也不读/校验 stprog.o**——启动只做
   文件复制 + 运行时初始化（空程序态：寄存器全 0、STOP）【实证】。
2. **daemon 不做编译**：三个启动周期 stprog.o/symbol.sym 字节级不变（mtime 保持
  Jul 21 出厂值）；"哪一步负责编译" = **AutoShop IDE**：`AutoShop.exe` 内
  `'STCompiler.dll load fail!'`（进程内加载）、`'m_bIsTestComplileDownload err!'`、
  `Compile\.hcp` 等字符串；`STCompiler.dll` 无导出目录（COM/进程内组件，
  不可直接驱动）【实证（字符串 + 导出表）】。
3. 编译产物的装载路径 = **12939 原生协议的下载流**：`mp_core.dll` 内
  `project\project.hcpp`、`project\project_backup.hcpp`、`project\.incomplete`、
  `project/PLCProgram.down`、`project/PLCProgram.updown`、`download_file_handler`、
  `download_prg_update_handle`、`mp_index_write_load_default_app`；
  `CompileSystem.dll` 导出 `elf_load_module/elf_load_file_header/elf_apply_relocate_arm/`
  `elf_apply_relocate_x86/builtin_*`（运行时 ELF 装载器 + 内置函数库）【实证（字符串/导出）】。
   即：AutoShop 编译 → 12939 下载（写 project.hcpp + .incomplete + 编译产物）→
   daemon 装载 ARM ELF。本仓库 `tools/AutoShopNativeBridge` TestHost 仅暴露
   `--comm-only/--plugin-only/--target=`（只读探测），无下载能力。

### 3.4 T3 结论：程序可生成性（诚实记录）

**"生成程序→模拟器运行"闭环未打通，且在本任务边界内不可打通。** 三条路全部被实证封死：

1. **源 blob 不可合成**：payload AES 加密（2.4），无密钥不可构造合法 .hcp 内容；
   即便构造，daemon 启动也不读它（3.2-实验 2/4）。
2. **编译器不可驱动**：`STCompiler.dll` 无导出、在 IDE 进程内；UI 输入通道全被过滤
   （`docs/inovance-autoshop-uia-map.md`）；无 CLI 编译器（安装目录清点无独立编译 exe）。
3. **装载路径不可注入**：编译产物经 12939 厂商协议下载（download_file_handler 等），
   协议未逆向且 payload 为厂商私有帧；TestHost 无下载能力。

**已实证可用的寄存器变化通道**（非程序生成，属 P2 既有结论的复验）：
Modbus FC5/FC6 写后 FC1/FC3 读回一致（协调人实测 D200=1234、M0=ON）；
本实验对照组/实验组 Modbus FC1/FC3 读全部正常。运行时寄存器变化 ≠ 程序生成。

---

## 四、T4 交付（C#）

| 层 | 位置 | 说明 |
|---|---|---|
| 区段扫描器 | `src/PlcMcp.Engineering/Workers/AutoShop/ProjectFormat/MainBlobScanner.cs`（新增） | `MainBlobScanner.TryScan/Scan/ExtractPayload` |
| 单测 | `tests/PlcMcp.Engineering.Tests/AutoShopProjectFormatTests.cs`（追加 8 个：7 离线 + 1 env-gated） | 离线合成 blob 全覆盖 + 真实样本 env-gated |
| 侦察脚本 | `%TEMP%\plcmcp-handoff\p3-blob-recon\*.py`（不入仓库） | python 独立分析（extract/analyze1-13/pe_exports 等） |
| 文档 | `docs/inovance-autoshop-program-blob.md`（本文件） | 全部证据 |

```csharp
// 区段扫描（不抛异常版）：
bool ok = MainBlobScanner.TryScan(ReadOnlySpan<byte> blob,
                                  out MainBlobScanReport? report, out string? error);
// report.CycleChars        — 派生的混淆循环（已知样本 "SlotaecSfwr"，11 字符）
// report.CyclePeriod/Stride/CycleStartOffset/CyclePurity
// report.DeviationCount / SuspendedRunCount
// report.Segments          — 区段列表（Prologue / Interleaved / CycleSuspendedPayload，
//                             Offset+Length 指向 blob 内偏移）
// report.PayloadLength / PayloadEntropy / PayloadPrintableFraction
// report.PayloadLooksEncrypted — 熵>=7.5 且可打印<=0.5 的实测统计画像（非解密声明）
// report.PayloadMagic      — PPCH/ELF/zlib/gzip 签名检测（实测样本：null）
// report.MatchesKnownStructure — 与已知 AutoShop 混淆结构匹配（含旋转归一）
// report.Findings          — 人类可读事实清单

// payload 抽取（诊断用；结果为加密字节，不可解释）：
byte[] payload = MainBlobScanner.ExtractPayload(ReadOnlySpan<byte> blob);
```

实现纪律：

1. 扫描器**纯只读**：不修改 blob、不解密、不合成程序；`MatchesKnownStructure=false`
   时调用方必须按 Unsupported 处理。
2. 循环由数据派生（多数投票 + 旋转归一），不硬编码假设——未来 AutoShop 版本换循环
   常量时扫描器仍能如实报告。
3. 已知结构判定：周期 11 + 步长 2 + 纯度 ≥ 0.95 + 字符 = "SlotaecSfwr"。
4. 测试：7 个离线测试（合成 blob 构造/往返/小输入拒绝/随机不匹配/挂起区段/熵画像/
   签名检测/非法步长）+ 1 个 env-gated 真实样本测试
   （`PLCMCP_REAL_AUTOSHOP_SAMPLES=1`，验证 Template A1 69 偏差/17 挂起区/9,541 B
   payload 与立包机 210/56/132,016 全部精确复现）；文件共 36 个测试。

---

## 五、限制与无厂商背书声明

- 全部结论基于 2 个模板样本（14 个字节相同的小模板 + 1 个 V5.x 工程）与
  h5u_daemon.exe 工作副本实验；**无汇川厂商背书**，格式随版本漂移。
- payload 加密密钥未恢复【未解】；AES 论断基于导出符号 + 熵画像【推断】。
- 挂起区语义（加密器帧边界？）、循环字符 "SlotaecSfwr" 的来源、blob[0..2) "bj"
  prologue 语义【未解/推断】。
- 12939 下载协议未逆向（S2/后续域）；TestHost 无下载能力。
- 厂商兼容性：AutoShop 能否打开生成容器仍未验证（P1 结论不变）。

## 六、给后续（S2 CLI/编译链）的建议

1. **编译链**：AutoShop IDE 进程内的 `STCompiler.dll` 是唯一编译入口；可探索
   x86 进程内加载（`LoadLibrary` + 内部符号，需 IDA 定位其入口/COM 注册表）或
   AutoShop.exe 命令行开关 `-open/-downprj`（语义未逆向，见 P1 文档第九节）。
2. **下载链**：逆向 12939 的 `download_file_handler` 帧（mp_core.dll）——
   这是"编译产物 → daemon 装载"的必经之路；`net.down/net.tmp/net.up` 是工程包内
   文件名，不是传输帧。
3. **密钥**：`EnDecrypt.dll` 的 `CAESAlg` 密钥管理（`CreateNewKey/GetKey`）可用
   x86 垫片直调（S2 已有 CommStackShim 先例）——若密钥可从进程内导出，blob payload
   即可解密，程序合成随之打通。
4. **模拟器装载验证**：一旦能生成/获得编译产物（stprog.o + symbol.sym + *.foid），
   放入 `app\`（首烧复制会覆盖，需在 daemon 启动后写入或经下载链），
   用 `elf_load_module` 装载路径 + Modbus 读回做闭环验收。
