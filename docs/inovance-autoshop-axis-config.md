# 汇川 AutoShop 轴组态（xml\axis.xml）模型与编辑管线（F2）实证文档

> 本文档固化 F2（阶段三追加任务：轴组态 XML 解析与编辑）的全部实证结论与交付。
> **所有接口均为逆向所得，无汇川厂商背书；版本锁定 AutoShop 4.12.0.2 / x86。**
> 标注约定沿用系列文档：【实证】= 亲自执行并复核；【推断】= 基于实证的推理；
> 【未验证】= 无直接证据、禁止当作事实。能力声明纪律沿用 docs/contracts.md。
>
> 配套阅读：`inovance-autoshop-project-format.md`（P1 容器）、
> `inovance-autoshop-project-encryption.md`（D3 UCodeCrypt）、
> `inovance-autoshop-open-and-compile.md`（E3 打开路径）、
> `inovance-autoshop-native-bridge.md` 第十五节（F2 真实读取路径）。

对应实现（全部离线可测，不依赖安装目录）：

| 层 | 位置 |
|---|---|
| 轴模型 / 解析器 / 模板合成 | `src/PlcMcp.Engineering/Workers/AutoShop/ProjectFormat/AxisConfig.cs` |
| 读→改→回写→重加密→重打包管线 | `src/PlcMcp.Engineering/Workers/AutoShop/ProjectFormat/AxisPackageEditor.cs` |
| 离线测试（9 个） | `tests/PlcMcp.Engineering.Tests/AutoShopAxisTests.cs`（新建） |
| 真机打开验证 harness（不入仓库） | `%TEMP%\plcmcp-handoff\f2-opencheck\` |

---

## 一、结论摘要（先读这一节）

| 项 | 结论 |
|---|---|
| **axis.xml 真实样本** | **本机不存在**【实证，阴性】。全部 15 个模板 .hcpp（Template A1–E / 模板 B–E / 範本 A1–E）、连续式立包机 V5.x 工程（385 条记录逐一检查）、模拟器下载包（`project.hcpp` = "Empty"）、E3 解包的 H3U 工程（`Axis.dat` = 2 字节 `00 00`）均无 axis.xml 文件记录；两份解密 Main XML（A1/立包机）的文件列表与顶层标签也无任何 axis 相关元素。S3 阶段因输入过滤从未新建过 H5U 工程（handoff `samples.status=not-created`），故本机从未产生过含轴组态的工程。 |
| **axis.xml 的定位** | AutoShop.exe 内唯一引用是 UTF-16 串 `\xml\axis.xml`（文件偏移 0x1bfbf1c，InitInstance 区段，VA 0x1FFCF26/0x1FFD0D4 两处 "axis.xml"）【实证】——它是**解包后工程目录内** `xml\axis.xml` 的逐工程文件（启动播种路径），与 .hcpp 容器成员名（MAIN.LD 等）不同层。 |
| **轴字段 schema（RE 提取）** | 从 AutoShop.exe 轴属性代码区（文件偏移 0x1177000–0x117b600，RTTI 类 CAxisMgr / CAxisBaseProcess / CAIWebAPICreateAxis 等）提取的元素名表【实证】：容器 `AxesGroupName` / `XAxis` / `YAxis` / `ZAxis` / `AssitAxis`；字段 `AxisName`、`AxisType`、`Mode`(int)、`EnableLimitSet`(bool)、`ForwardLimitValue`(double)、`NegativeLimitValue`(double)、`RotationPeriod`(double)、`IsVirtual`(bool)、`IsContour`(bool)、`OutputDev`、`InputDev`、`slaveName`、`Row`。类型判定依据消费代码的取值方式（字符串拷贝 / `movzx byte` / `fld qword`）【实证】。 |
| **schema 可信度** | 元素名 = 实证（二进制内字符串+消费代码）；**嵌套结构 = 推断**（无真实样本可对照）。因此解析器做成**结构保持**（parse 保留全树，写回逐字保留未知内容）——真实样本到手后只需补名字表，不改模型。 |
| **编辑管线（核心交付）** | 读 .hcpp（HcppReader）→ 定位 axis.xml 成员 → 解析 → 编辑 → 写回 → **HcppWriter 重打包**（主记录字节透传）；主 XML 路径走 **DecryptBlob→改→EncryptBlob**（D3 循环）。离线往返 9/9 测试绿【实证】。 |
| **真机打开验证（重大）** | **AutoShop V4.12.0.2 成功打开 F2 重打包的工程**：用 E4 的 `AutoShopInjectTools.OpenProjectAsync` 做对照实验——基线（字节忠实解包）`Outcome=Opened`，**F2 重加密变体（ProjectDesc 解密→修改→重加密→HcppWriter 重打包）同样 `Outcome=Opened`**（窗口标题 MAIN，无错误对话框）【实证，报告 `%TEMP%\plcmcp-handoff\f2-opencheck\f2-opencheck-report.jsonl`】。即 **DecryptBlob→编辑→EncryptBlob→HcppWriter 循环产出的工程被厂商软件接受**。 |
| **轴成员打开验证** | 未做【如实标注】：H3U 模板无轴模型，无法给"轴组态是否被正确消费"提供有效信号；axis.xml 的厂商打开行为待 H5U 样本工程出现后再验。 |

---

## 二、侦察记录（为什么没有真实样本）

1. **模板容器逐一解析**（python zlib+struct，与 P1 解析器同逻辑）：15 个 .hcpp 的记录流全量列出，
   无任何 `axis.xml`/`Axis` 文件名记录；连续式立包机 385 条记录中 axis 命中仅为库文件名
   （`HC_ReadAxisError.FB`、`PackMachine-FB_AxisSwitch.libtemp` 等）【实证】。
2. **Main blob XML**：A1（9,472 字符）与立包机（131,806 字符）的 `<file>` 列表与顶层标签中
   无 `AxisList`/`CanAddAxis`/`axis.xml`（InterfaceMgr.dll 0xB2430 的名字表虽有 `CanAddAxis`/
   `AxisList`/`AxisGroupList`，但那是**另一代工程 XML schema** 的元素名，两个真实样本均未出现）【实证】。
3. **解包工程**：E3 打开模板 A 产生的工程目录（proj5/proj6）内 `Axis.dat` 仅 2 字节 `00 00`
   （H3U 无轴，占位文件）；无 `xml\` 子目录【实证】。
4. **模拟器**：`Simulator\app\project\project.hcpp` = 7 字节 `Empty\r\n`（默认空应用，D1 已记录）【实证】。
5. **UI 路径封死**：S3 实证 AutoShop 输入过滤（uia-map 文档），无法用 UI 新建 H5U 工程生成
   axis.xml【继承结论】。

## 三、AutoShop.exe 轴 schema 证据（RE）

### 3.1 元素名表（Wide 字符串，AutoShop.exe）

| 证据点 | 文件偏移 | 内容 |
|---|---|---|
| 轴名字符串组 | 0x1bfb050 区 | `AssitAxis` / `ZAxis` / `YAxis` / `XAxis` / `RotationPeriod` / `ForwardLimitValue` / `NegativeLimitValue` / `EnableLimitSet` / `AxisName` / `IsContour` / `IsVirtual` / `OutputDev` / `InputDev` / `AxisType` / `slaveName` / `Row` |
| 消费代码 | 0x117755e–0x117766d | push `AxisName`→CString 拷贝、`Mode`→int、`EnableLimitSet`→byte 布尔、`NegativeLimitValue`/`ForwardLimitValue`/`RotationPeriod`→`fld qword`（double） |
| 第二组 | 0x11779c7–0x1177aa6 | `AxisType` / `InputDev` / `OutputDev` / `IsVirtual` / `IsContour` |
| 容器组 | 0x117710a–0x11771f6 | `AxesGroupName` / `XAxis` / `YAxis` / `ZAxis` / `AssitAxis`（同一属性对象按键取值） |
| RTTI | `.?AVCAxisMgr@@` 等 | CAxisMgr / CAxisBaseProcess / CAxisBaseProcessMgr / CAxisCount / CAxisNormal / CAxisUnitProcess / CAxisZeroProcess / CAxisHceChannelConfig / CAxisGroupObject / CAIWebAPICreateAxis / CAIWebAPIModifyAxisName / CAIWebAPIDelAxis |

`Axis_%d`（0x1c02054）与 `AddAxis/DelAxis/ModifyAxisName/ConfigAxisMode/GetAxisDeviceInfo`
（AI Web API 方法名表）同区，佐证"轴以序号+名称管理"的模型。

### 3.2 与 InterfaceMgr 名字表的关系

InterfaceMgr.dll 0xB2430 UTF-16 表含工程级元素名对（`CanAddStruct`/`StructList`、
`CanAddAxis`/`AxisList`、`CanAddAxisGroup`/`AxisGroupList`、`CanAddEtherCatSlave`/
`EtherCATSlaveList`…）——是**工程 XML 的能力标志/列表名**，本机两个真实工程 XML 均未出现，
属【未验证】的 schema 变体，不纳入本交付的解析目标。

## 四、C# 交付 API

```csharp
using PlcMcp.Engineering.Workers.AutoShop.ProjectFormat;

// 1) 解析（结构保持，不抛异常版）：
bool ok = AxisConfigXml.TryParse(string axisXml, out AxisConfigDocument? doc, out string? error);
// doc.Axes              — List<AxisDefinition>（X/Y/Z/AssitAxis 容器下的轴定义）
// doc.AxesGroupName     — 组名（RE 字段）
// doc.Document          — 活的 XDocument（未知内容原样保留）

// 2) 类型化字段（活视图，setter 直接改树）：
var x = doc.Axes.Single(a => a.Kind == AxisKind.X);
x.EnableLimitSet = false;          // EnableLimitSet / IsVirtual / IsContour (bool?)
x.ForwardLimitValue = 4321.5;      // Forward/NegativeLimitValue / RotationPeriod (double?)
x.Mode = 0;                        // (int?)   x.Name/x.AxisType/x.OutputDev/x.InputDev/x.SlaveName (string?)
x.Row = 3;                         // (int?)

// 3) 写回（保留 XML 声明与未知内容）：
string newXml = AxisConfigXml.Write(doc);

// 4) 合成模板（fixture/文档用，schema 为 RE 推断，已如实标注）：
string sample = AxisConfigXml.BuildTemplate("G1", new[] { (AxisKind.X, "X1", "Pulse", true, 1000.0, -1000.0, 5.0), ... });

// 5) 包级管线（.hcpp）：
ok = AxisPackageEditor.TryLoad(hcpp, out doc, out bool hasAxisFile, out error);
ok = AxisPackageEditor.TryEditAxisAndRepackage(hcpp, doc => {...}, out byte[] repacked, out error);
ok = AxisPackageEditor.TryEditMainXmlAndRepackage(hcpp, xml => xml.Replace(...), out repacked, out error); // DecryptBlob→改→EncryptBlob
ok = AxisPackageEditor.TryVerify(repacked, out string? axisXml, out string? mainXml, out error);          // 读回+解密断言
```

实现纪律：

1. **结构保持**：模型字段是 XElement 的活视图；未识别的元素/属性原样透传（测试
   `RoundTrip_PreservesUnknownContent_ByteForByte` 验证含中文与属性的未知子树逐字节保留）。
2. **axis.xml 成员无需解密**：.hcpp 成员文件为明文（D3：UCodeCrypt 只保护 Main 记录）；
   主 XML 路径才走 DecryptBlob/EncryptBlob。
3. **BOM 修复**：主 XML 解码剥离 U+FEFF 后再编辑，重加密前统一补 FF FE BOM（避免双 BOM）。
4. schema 为 RE 推断项在代码注释、XML 注释与本文档三处同步标注，禁止当作已验证。

## 五、往返验证（离线 + 真机）

### 5.1 离线测试（9/9 绿，`AutoShopAxisTests.cs`）

| 测试 | 验证点 |
|---|---|
| Parse_SynthesizedTemplate_FindsAxesAndTypedFields | 3 轴解析、typed 字段全对 |
| Parse_RejectsGarbage_WithReason | 坏输入给原因不抛异常 |
| RoundTrip_PreservesUnknownContent_ByteForByte | 未知子树（含属性+中文）逐字节保留 |
| EditAxis_Repackage_VerifyShowsEditAndPreservesMain | 轴编辑落盘 + 主 blob 字节透传 |
| EditMainXml_Reencrypt_RoundTripsThroughDecrypt | DecryptBlob→改→EncryptBlob 循环闭合 |
| ContainerWithoutAxisMember_ReportsAbsentWithoutError | 无轴容器正常报告缺席 |
| TryLoad_RoundTripsSyntheticPackage | 包级读回 |
| Crypt_SelfTest_Gate | D3 已知答案锚点（版本闸门） |
| RealTemplate_NoAxisMember_Evidence | env-gated：真实模板无 axis 成员（阴性证据） |

### 5.2 真机打开验证【实证，2026-10-28】

harness：`%TEMP%\plcmcp-handoff\f2-opencheck\`（独立 console，引用仓库 PlcMcp.Server/
PlcMcp.Engineering Release DLL；AutoShop 实例由注入器自启、归属日志、实验后停止）。

| 步骤 | 结果 |
|---|---|
| 基线（字节忠实解包 Template A1）`OpenProjectAsync` | `Success=true, Outcome=Opened, WindowTitle=MAIN`（对照有效） |
| F2 变体：主 XML 解密→`<ProjectDesc>模板A工程描述→F2重加密往返验证`→EncryptBlob→HcppWriter 重打包 | `Success=true, Outcome=Opened, WindowTitle=MAIN`，自校验 `containsEdit=true` |
| 结论 | **解密→编辑→重加密→重打包的工程被真实 AutoShop 打开**（厂商加密路径兼容性首次得到打开级验证） |

### 5.3 未验证项（如实标注）

1. **axis.xml 成员的厂商消费行为**：无 H5U 工程（无轴模型）可作对照，往 H3U 模板塞
   axis.xml 不能产生有效信号——未做，待真实 H5U 样本。
2. **轴编辑后经 AutoShop 打开的 UI 呈现**：同上。
3. **轴字段完整清单**：RE 名字表来自 AI Web API 与属性代码区，H5U 轴组态对话框的全部
   参数（电子凸轮引用、齿轮比等）可能在其他字段表——真实样本到手后补齐。
4. xml\axis.xml 的**文本编码**（UTF-8/UTF-16）无样本可证；解析器两种都接受。

## 六、给集成代理（F3/E4）的接口说明

- 轴组态读取工具：`AxisPackageEditor.TryLoad`（`hasAxisFile=false` = 无轴工程，如实上报）。
- 轴组态编辑工具：`TryEditAxisAndRepackage` + `OpenProjectAsync`（E4）可组成"改轴参数→重打包→打开验证"受治理工具；**写工程文件属于工程文件写路径，不触 PLC，无需 PLC 审批门控**（对齐红线：零 PLC 动作）。
- 监视真值工具接线见 `inovance-autoshop-native-bridge.md` 第十五节。
