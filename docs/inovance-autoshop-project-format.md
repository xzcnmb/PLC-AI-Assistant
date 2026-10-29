# 汇川 AutoShop 工程包格式（.hcpp / PPCH）实证文档（P1）

> 本文档固化 P1（工程格式收尾）对 AutoShop 工程包 `.hcpp`（PPCH 容器）的全部实测结论，
> 证据来源为**真实模板样本**（只读分析，未修改安装目录、未与 PLC 交互）：
>
> | 样本 | 大小 | 说明 |
> |---|---|---|
> | `D:\Inovance Control\AutoShop\Template_Project\English\Library_Template\Template A1\Template A1.hcpp` | 15,521 B | V4.12 系小模板 |
> | `Template_Project\Simplified_Chinese\Library_Template\模板B..E\模板B..E.hcpp`、`Traditional_Chinese\Library_Template\範本*.hcpp` | 各 15,521 B | 与 Template A1 **字节级完全相同**（14 个文件 SHA256 唯一）【实证】 |
> | `Template_Project\Simplified_Chinese\Library_Template\连续式立包机\连续式立包机.hcpp` | 304,619 B | V5.x 系真实工程（内容头版本 "5.02"/"5.03"） |
>
> **所有结构均为逆向所得，无厂商背书。** 容器骨架（magic、zlib、记录流、终止符）、头部
> 两个 size 字段、31 字节内容头与配对规则是实测事实（标注【实证】）；blob 编码、校验槽
> 算法、Field4/marker/trailer 语义等标注【未解】，禁止在实现中当作事实。与
> `docs/inovance-autoshop-native-bridge.md`（S2 桥）、`docs/inovance-integration-plan.md`（种子情报）
> 配合阅读。能力声明纪律沿用 `docs/contracts.md`。
>
> 标注约定：【实证】= 真实样本上执行并复核（解析器逐字节验证 + python zlib/struct 交叉验证）；
> 【推断】= 基于实证的推理（含单样本巧合项）；【未解】= 无可信证据、生成器不得猜测。

对应实现（离线单测，不依赖安装目录）：

| 层 | 位置 |
|---|---|
| 解析器/生成器 | `src/PlcMcp.Engineering/Workers/AutoShop/ProjectFormat/{HcppContainer,HcppReader,HcppWriter}.cs` |
| 单测 | `tests/PlcMcp.Engineering.Tests/AutoShopProjectFormatTests.cs`（27 个测试） |
| 真实样本验证 harness | `%TEMP%\plcmcp-handoff\pfmt-validate\`（**不入仓库**，报告 `pfmt-validation-report.txt`：97 项检查全过） |

---

## 一、结论摘要（先读这一节）

| 项 | 结论 |
|---|---|
| 容器骨架 | 36 字节 PPCH 头 + zlib 流 + `[u32 len][data]` 记录流；0xCDCDCDCD 终止符**可选**（小模板有，V5.x 大工程没有——流尽即止）【实证】 |
| 头部 size 字段 | `[6..10)` = 解压后记录流大小、`[10..14)` = 压缩负载大小（文件长度−36−4），**两样本均精确相等**【实证】；读取器仍按扫描 0x78 定位 zlib、不强制 size 字段（容忍变体） |
| 记录形状 | Main / FileName / FileContent / CombinedFile 四种 + Unknown 分类【实证】 |
| 内容头 | 31 字节 `"AutoShop"` + 12 零 + 版本串@20/@25（变体见第五节）【实证】 |
| 主程序 blob | **编码未恢复**【未解】——生成器只能复用现成 blob（如从模板解析所得），不能合成新程序 |
| 厂商兼容性 | **未验证**——环回测试只证明本 reader/writer 对自洽；AutoShop 能否打开生成容器未知 |

---

## 二、PPCH 容器头（36 字节，小端）

| 偏移 | 类型 | 实测值 | 证据分级 |
|---|---|---|---|
| `[0..4)` | magic | `"PPCH"`（按小端 uint32 读为 `0x48435050`，字节序列 50 50 43 48） | 【实证】两样本一致 |
| `[4..6)` | uint16 | V4.12 小模板 `0x0300`；V5.x 大工程 `0x0200`。语义（版本？标志？）未解 | 【实测值，语义未解】 |
| `[6..10)` | uint32 | 解压后记录流大小。Template A1 = 24,867（= 实测解压长度）；连续式立包机 = 1,575,805（同左）。**两样本精确相等** | 【实证】python struct + zlib 交叉验证 |
| `[10..14)` | uint32 | 压缩负载大小 = 文件长度 − 36（头）− 4（校验槽）。15,521−40 = 15,481 ✓；304,619−40 = 304,579 ✓。**两样本精确相等** | 【实证】同上 |
| `[14..30)` | 16 字节 | 全零（两样本） | 【实证】 |
| `[30..36)` | 6 字节 marker | 小模板 `CD CD 0C 00 00 00`；大工程 `00 00 01 01 00 00`。语义未解 | 【实测值，语义未解】 |
| `[36..)` | zlib 流 | CMF `0x78` + FLG `0x9C`（两样本） | 【实证】 |

读取策略：`HcppReader.LocateZlibStream` 在头部区域（偏移 30 起）扫描满足 RFC 1950 头校验
（`(0x78*256+flg) % 31 == 0` 且未用预设字典）的 zlib 流，不信任固定偏移与 size 字段——
容忍未来变体；`HcppHeader.Parse` 仍如实记录上述字段供诊断（`DecompressedSize`/
`CompressedSize` 属性），但不据此强制校验。

---

## 三、zlib 包装与校验槽

- 偏移 36 起：`78 9C` + 原始 deflate 流 + **4 字节校验槽**【实证】。
- **校验槽未解**：两样本的槽值都**不等于**标准 adler32（大端、小端读法都不等），也不等于
  crc32（对解压数据或压缩数据）【实证】：
  - Template A1：槽值（小端读）`0xE0C4764B`（大端读 `0x4B76C4E0`），adler32(解压流) = `0x0FAB276F`；
  - 连续式立包机：槽值（小端读）`0x342B043F`（大端读 `0x3F042B34`），adler32(解压流) = `0x0F563508`。
  推测为厂商自算校验或对打包前数据计算【未解】。读取器忽略该槽（deflate 数据本身自结束）。
- **生成器写出 RFC 1950 大端 adler32**（理由：厂商值不可复现，该槽无法按厂商字节匹配；
  写标准值可被常规 zlib 工具校验，读取器两端均忽略）。这是 P1 相对上一轮的**实现修改**，
  理由即上述实测——上一轮的小端写法同样不可复现厂商值，但不符合 RFC 1950 网络字节序。

---

## 四、记录流与四种记录形状

解压后为 `[uint32 LE 长度][数据]` 记录流，遇 `0xCDCDCDCD` 终止（小模板）或流尽即止
（大工程，实测其最后一条记录恰好耗尽解压数据，无终止符）【实证】。

### 1. Main（记录 0）：`[u32 namelen][名字][u32 bloblen][blob][u32 groupHint]`

| 样本 | 名字 | bloblen | groupHint |
|---|---|---|---|
| Template A1 | `"模板A.hcp"`（**UTF-8** 11 字节 `e6a8a1e69dbf412e686370`）【实证】 | 18,944 | 1,471 |
| 连续式立包机 | **GBK** 24 字节 `bdf0cca9d2e6c1a2b0fcbbfa...`，GBK 解码 = `"金泰益立包机25.09.03.hcp"`；UTF-8 解码失败（`0xbd` 非法起始字节），按替换字符解出乱码但**不抛异常**【实证】 | 263,612 | 2,794 |

- **名字编码**：V4.12 模板为 UTF-8；V5.x 真实工程为系统 ANSI（中文 Windows 即 GBK）。
  解析器统一按 UTF-8 解码：GBK 名字得到 U+FFFD 替换字符（合法 UTF-8 之外的字节不会
  抛异常，`IsPlausibleName` 放行）——不丢字节、不崩溃，但**显示为乱码**【实证 + 限制】。
- **groupHint 实测约定**：= 首个文件条目的名字记录（4+名字字节数）+ 其内容记录
  （4+31+负载字节数）**+ 4**：
  - Template A1：MAIN.LD 名字记录 11 + 内容记录 1,456 + 4 = **1,471** ✓（精确）；
  - 连续式立包机：11 + 2,779 + 4 = **2,794** ✓（精确）。
  尾部 +4 的含义未解（疑为厂商内部组帧的 4 字节 trailer）【推断，两样本精确拟合】。
  生成器按该约定写 groupHint（P1 修改；上一轮"= 后续文件记录总大小"的说法与本实测矛盾，
  Template A1 后续记录总大小为 5,892 ≠ 1,471）。

### 2. FileName（裸名字记录）：仅名字本身，无嵌套帧【实证】

如 `MAIN.LD` / `INT_001.LD` / `MAIN.mon` / `DeviceTable.dev` / `PortConfig.cfg` /
`LoginPassowrd.dat`（Template A1 全部 6 条，顺序实测如此）。名字编码同上（UTF-8/GBK 混杂，
大工程内 GBK 为主）。

### 3. FileContent（内容记录）：31 字节 "AutoShop" 头 + 负载，**无尾缀**【实证】

紧随裸名字记录出现，与之一一配对（见配对规则）。Template A1 六条内容记录长度
1,421 / 1,263 / 274 / 238 / 186 + LoginPassowrd.dat 加密负载 17 字节。

### 4. CombinedFile（合并记录）：`[u32 namelen][name][u32 contentlen][content][u32 trailer]`【实证】

自带名字、独立成条。Template A1 五条：`SBR_001.LD`（1,294，trailer 0x524）、
`VarList.gdt`（266，0x145）、`Config.sdt`（312，0x128）、`ElemUseInfo.esi`（214，0xF3）、
`MDI.cfg`（**contentlen=0，trailer=0x2E**）。content 内**多数**嵌 31 字节 AutoShop 头
（Template A1 四条带内容的全部嵌）——但大工程 128 条带内容合并记录中仅 103 条嵌头
（`*.csv`、`*.tr`、`*.dat`、`*.flib` 等为裸内容）【实证】。
trailer 值未解（0x2E 在 MDI.cfg/dataLog.data 重复出现；SBR_001.LD 0x524 与自身数据长度
1,316 巧合相等；folder.txt 为 0）【未解】。解析器对"有/无 trailer"两种长度都接受。

### 配对规则与 Unknown 分类

- **配对**：裸名字记录**紧跟**其内容记录——内容记录为 FileContent（带 AutoShop 头）或
  Unknown（无头）【实证】。大工程中 `config.ini`（396 字节明文 ini，`"[Comm_Param]\r\n…"`）、
  `TcpModbusConfig.cfg`（8 字节）、`OpcUAServer.dat`（250 字节）、`LoginPassowrd.dat`
  （17 字节密文）等均为"裸名字 + Unknown"配对。`AssembleFiles` 因此把两者都配对（P1 修改，
  上一轮只配 FileContent，导致 LoginPassowrd.dat 从文件列表丢失；实测其确有内容记录）。
- **Unknown**：不满足上述形状的透明负载。判定依据：以 `"AutoShop"` 开头 → FileContent；
  能按 `[u32 名长][名字][u32 内容长][内容][u32 trailer]` 解析 → CombinedFile；
  是合法名字（1..64 字符、无控制字符的 UTF-8）→ FileName；否则 Unknown。
  **固有歧义**：无 AutoShop 头且全为可打印 ASCII 的负载与裸名字不可区分（实测样本的
  无头负载均含控制字符或非法 UTF-8，故无歧义发生）【限制】。

---

## 五、31 字节内容头布局

```
[0..8)   "AutoShop"（ASCII）                【实证】两样本一致
[8..20)  12 字节全零（保留）                【实证】
[20..25) 版本串 + 0x00 填充（定长 5 字节）   【实证，变体见下】
[25..31) 第二版本串 + 0x00 填充（定长 6 字节）【实证，变体见下】
```

实测变体（识别只看 `"AutoShop"` 前缀，版本串不定长，解析器不校验版本）：

| 样本 | @20 | @25 |
|---|---|---|
| V4.12 小模板（全部 14 个） | `"2.6\0\0"` | `"6.0\0\0\0"` |
| V5.x 连续式立包机 | `"5.02\0"` | `"5.03\0\0"` |
| （P1 任务书转述） | `"2.0"` | `"6.0"`（样本未定位，存疑） |

Template A1 完整十六进制（31 字节）：
`4175746F53686F70000000000000000000000000322E360000362E30000000`；
连续式立包机：`...352E303200352E30330000`【实证】。

---

## 六、大工程（连续式立包机.hcpp）补充事实

- 385 条记录、256 个装配文件；记录流 1,575,805 字节【实证】。
- **无 0xCDCDCDCD 终止符**：最后一条记录（`folder.txt` 合并记录）恰好耗尽解压数据【实证】。
- 名字以 GBK 编码（见第二节）；`Ecam.tr` ×5、`trace.tr` ×13 等文件条目**重复出现**【实证】。
- 头部 marker `00 00 01 01 00 00`、Field4 `0x0200` 与小模板不同（语义未解）【实证】。
- 加密的 `PackMachine.flib` / `cmpHCBatch.flib` / `AA.flib` / `ITR.flib` / `Opmode.flib` /
  `RTI.flib` / `RTR.flib`（240 字节，头部 `A5280E36…`，含控制字符 0x0E）为"裸名字 + Unknown"
  配对【实证】。

---

## 七、未解区段（生成器不得猜测）

| 项 | 状态 | 影响 |
|---|---|---|
| **主程序 blob（记录 0）编码** | 【未解】编码未恢复（Template A1 blob 头 32 字节 `626A8D6FA174E961D765B5638053EB66C977C172CC53DA6CDA6FCE74A8618A65`，疑加密/压缩，未验证） | **生成器只能复用现成 blob**（如从模板解析所得），不能合成新主程序；`HcppWriter` 的 blob 由调用方提供 |
| zlib 校验槽算法 | 【未解】两样本均不匹配标准 adler32/crc32（大小端都不等） | 生成器写 RFC 1950 大端 adler32（可被常规工具校验）；无法复现厂商槽值 |
| 头部 Field4 与 6 字节 marker | 【未解】实测值已记录（0x0300/0x0200；CDCD0C000000 / 000001010000） | 生成器按观测值写（小模板变体） |
| groupHint 尾部 +4 | 【未解】两样本精确拟合"首条目 +4"，+4 含义不明 | 生成器按实测约定写 |
| CombinedFile trailer | 【未解】值规律不明（0x2E/0x524/0/…） | 生成器不产出 CombinedFile；解析器兼容有无两种 |
| LoginPassowrd.dat 加密 | 【未解】两样本末条记录为**同一段** 17 字节密文 `B0DBB5D4A7A487A6D7D5CDDC7AD79B31 02`（末字节 0x02 为控制字符），疑空口令哨兵 | 解析器按 Unknown 透传；配对进文件列表 |
| 裸名字 vs 无头内容 | 固有歧义（全可打印 ASCII 负载与名字不可区分） | 解析器按控制字符/合法 UTF-8 启发式区分，实测样本无歧义 |

---

## 八、限制与无厂商背书声明

- 全部结论仅基于**模板样本**（14 个字节相同的小模板 + 1 个 V5.x 工程）逆向所得，
  **无汇川厂商背书**；格式可能随 AutoShop 版本变化。
- **厂商兼容性未验证**：环回（round-trip）测试只证明本 reader/writer 对自洽；
  AutoShop 能否打开生成的容器**未知**。若需真机验证，属后续任务（需 UI/下载通道）。
- 名字编码为 UTF-8 解码：GBK 名字（V5.x 工程）得到替换字符——不抛异常、不丢记录，
  但字符串显示为乱码；如需正确显示，须引入系统 ANSI 代码页回退（当前未实现，属猜测性
  启发式，见第七节）。
- 裸内容记录（无 AutoShop 头）不参与 31 字节头模板；生成器产出的内容记录一律带头。

## 九、CLI 开关线索（未逆向）

- `docs/inovance-integration-plan.md` 第一节字符串证据：`AutoShop.exe` 存在命令行开关
  `-open` / `-downfile` / `-offlinedebug` / `-downprj` / `-Debug`——**语义待逆向**，
  本文档不据此做任何实现假设。
- 工程源文件扩展名 `.hcp`（Main 记录名字即 `"*.hcp"`）；打包/下载工程为 `.hcpp`。

## 十、给 S4 的接口说明（工程格式层）

```csharp
// 解析（不抛异常版）：
bool ok = HcppReader.TryRead(byte[] file, out HcppContainer? container, out string? error);
// container.Header          — HcppHeader（Field4/DecompressedSize/CompressedSize/Reserved/Marker/PayloadOffset）
// container.InnerStream     — 解压后记录流原始字节
// container.Records         — 全部记录（Kind = Main/FileName/FileContent/CombinedFile/Unknown，
//                               含 Offset/Data/MainProjectName/MainBlob/MainGroupSizeHint/
//                               FileName/FileContent/ContentHeader/Trailer）
// container.MainRecord      — 记录 0（Main），MainBlob 为**不透明字节**（编码未恢复）
// container.Files           — 装配后的 (string Name, byte[] Content) 文件列表
//                               （裸名字+内容配对；内容为 AutoShop 头**之后**的负载；
//                                CombinedFile 直接成条；LoginPassowrd.dat 等无头负载按原样给出）

// 生成（仅自洽；blob 必须由调用方提供，如取自 container.MainRecord.MainBlob）：
byte[] hcpp = HcppWriter.Write(
    new HcppMainRecordSpec(projectName, blob),      // 可为 null
    new[] { new HcppFileSpec("MAIN.LD", payload) }, // 内容自动加 31 字节 AutoShop 头
    header: null);                                  // 缺省用 DefaultHeader（Field4=0x0300，size 字段自动填充）
```

注意事项：

1. `Files` 中内容是 **AutoShop 头之后的负载**（与磁盘上的目标文件内容同构）；CombinedFile
   的 `FileContent` 则为**含头的完整内容**（且可能无头，见第四节）。
2. 生成器**不产出** CombinedFile 与加密记录；如需完整复刻模板结构，建议直接复用模板原文件
   并按字节级补丁（blob 替换等）——该路径尚未实现。
3. 名字长度限制 1..64 字符（与实测一致）；文件名/项目名超限抛 `ArgumentException`。
4. 生成容器头部 size 字段（解压/压缩大小）由 writer 自动填充（实测字段，勿手填）。
