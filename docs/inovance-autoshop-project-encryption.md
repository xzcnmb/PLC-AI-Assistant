# 汇川 AutoShop 工程源内容加密器定位与解密实证（D3）

> 本文档固化 D3（AutoShop 主程序 blob 真实加密器定位）的全部实测结论。
> 证据来源：AutoShop.exe 与全部自装 PE 的只读静态分析（capstone 5.0.7 反汇编 / pefile 2024.8.26 /
> 密码常量特征扫描）、InterfaceMgr.dll 导出表与函数级反汇编、以及**纯本机 Python 实验**的解密实证。
> **未修改安装目录任何文件、未写注册表、未连接任何网络、未向任何 PLC 发送数据。**
> 与 `docs/inovance-autoshop-program-blob.md`（P3：Main blob 结构）、
> `docs/inovance-autoshop-encryption.md`（D2：EnDecrypt.dll 证伪）、
> `docs/inovance-autoshop-cli.md`（CLI-RE：打开失败路径）、
> `docs/inovance-autoshop-native-bridge.md`（十四节：C# 交付）配合阅读。
>
> 标注约定：【实证】= 本次亲自执行并复核；【推断】= 基于实证的推理；
> 【未解】= 无可信证据，实现中禁止当作事实。**全部结论均为逆向所得，无汇川厂商背书。**
>
> 证据链落盘：`%TEMP%\plcmcp-handoff\d3-encrypt-recon\`（脚本 + 解密样本 + C# 自测工程，不入仓库）。

---

## 一、结论摘要（先读这一节）

| 项 | 结论 |
|---|---|
| **真加密器** | **`CUCodeCrypt`（InterfaceMgr.dll）**：导出 `EncryptArithmetic/DecryptArithmetic`（private 但已导出，RVA 0x545F0/0x54640）与 `UCodeFileEncrypt/UCodeDecrypt` 家族（14 个修饰名被 AutoShop.exe 直接静态导入）【实证】 |
| **算法** | **逐字节 ADD/XOR 流密码（不是 AES、不是 Blowfish）**：`pt[i] = ((ct[i] ^ (i&0xFF 当 i 偶)) − key[(i+1) % 11]) & 0xFF`；厂商循环 esi 每字节 +1 到 0xB 归零【实证（反汇编 + 三路解密全对）】 |
| **密钥** | 11 字节 ASCII 表 **`"SclSoftware"`**，InterfaceMgr.dll .rdata RVA 0xB6620（VA 0x100B6620）【实证（直接读出 + 算法拟合 8/8）】 |
| **明文** | Main blob 解密后 = **FF FE（UTF-16LE BOM）+ UTF-16LE XML 工程源内容**（`<?xml version="1.0" encoding="UTF-16"?><project>...`）；A1 18,944 B blob → 9,472 字符 XML **逐字节完美**（中文"模板A工程描述"等全对）；立包机 263,612 B → 131,806 字符 XML【实证】 |
| **P3 "交错层"证伪** | "SlotaecSfwr" 奇数位循环**不是**独立混淆层——它是 UTF-16LE ASCII 文本高字节 0x00 经加密后的形态（enc(0)=key[(i+1)%11]^(i if even)，周期 22 吻合）；"循环挂起区" = CJK 汉字（非零高字节）；"bj" prologue = enc(FF FE BOM)；180 字节公共前缀 = 项目无关 XML 头（90 个 UTF-16 字符）【实证，全部数字拟合】 |
| D2 阴性裁决复核 | 成立：EnDecrypt.dll 的 8 个 AES-128 密钥与 blob 无关（AutoShop 零引用 + 解密全阴性）；**但 D2 的 AES 论断本身也是误判**——blob 根本不是 AES【实证】 |
| **对集成的裁决** | **.hcp 工程源内容可离线读取**：Main blob（工程属性/文件列表 XML）+ .hcpp 全部字段可解 → **变量导出/符号浏览/工程读取解锁**；程序合成（写回 .hcp）同样解锁（加密方向已还原并验证）【实证】 |

---

## 二、T1：密码常量扫描（AES 特征全阴性 → 另寻他径）

### 2.1 扫描方法与范围

对 `D:\Inovance Control\AutoShop` 递归全部 **321 个 PE**（.dll/.exe/.ocx，含子目录）做字节签名扫描：
标准 AES S-box/逆 S-box 前 16 字节、Te0..Te3/Td0 T-table（LE dword 形态）、AES Rcon（LE dword 40B 与 BE 10B）、
SM4 S-box/FK/CK、SM3 IV、SHA-256 IV、Blowfish P-array、TEA/XTEA delta、ChaCha/Salsa sigma
（脚本 `d3_scan_crypto.py`，结果 `d3_crypto_scan.json`）。

### 2.2 命中表【实证】

| 文件 | 命中 | 判读 |
|---|---|---|
| **AutoShop.exe** | 仅 `BLOWFISH_P_LE ×1`（文件偏移 0x1AF0E40，.rdata VA 0x1EF1E40） | **完整 Blowfish 常量表**（P-array 18 dword + 4×256 S-box，4168 B，其后即 UTF-16 串）；**红鲱鱼**（见 2.4） |
| libcrypto-3.dll | AES S-box ×4 / 逆 S-box ×4 / Rcon ×1 / SM4 FK ×1 / SHA-256 IV ×1 / ChaCha sigma ×1 | OpenSSL 3 自带表（D2 已证 AutoShop 仅用其 RSA_public_encrypt，无 EVP_aes/SM4 导入） |
| InoLoaderPro kerneldlls（CANFDNET_TCP/UDP、USBCANFD） | AES S-box/逆 S-box/Rcon 各 1 | **独立产品**（Qt5 烧录工具）的内核 DLL，与 AutoShop IDE 无关 |
| 其余（global.dll、Converter.dll、ElementEntity.dll、H530Converter.dll、H530SysResMgr.dll、CommunicaterModule.dll、InterfaceMgr.dll、ChsRes/ChtRes/EnuRes、Communicate*.dll 等 311 个） | **零命中** | **AutoShop IDE 全家无标准 AES 表常量**【实证，阴性】 |

**中间结论**：AutoShop IDE 二进制内**不存在**任何标准 AES T-table/S-box/Rcon 常量——P3/D2 的
"AES 加密"假设的静态基础不成立（唯一 AES 库 = EnDecrypt.dll，已证零引用；OpenSSL 无 AES 导入）。

### 2.3 AutoShop.exe 的 Blowfish（红鲱鱼）【实证，排除】

完整还原了 AutoShop.exe 内嵌的 Blowfish 实现（标准算法、无交换展开）：

| 件 | 位置 | 证据 |
|---|---|---|
| 标准常量表 | .rdata VA 0x1EF1E40（P-array 0x48B + S-boxes 0x1000B，共 4168 B），表后紧跟 UTF-16 串 | pefile 定位 + 全表字节比对 |
| F 函数 | VA 0x46D0F0：`F(x)=((S0[a]+S1[b])^S2[c])+S3[d]`，S-box 在 ctx+0x48 | capstone 反汇编 |
| 单块加密 | VA 0x46D040：`for(i=0;i<16;i+=2){xl^=P[i];xr^=F(xl);xr^=P[i+1];xl^=F(xr)} xl^=P[16];xr^=P[17]` + 输出交换 | 同上 |
| 单块解密 | VA 0x46D150：P 倒序轮 + 末轮 P[1]/P[0] | 同上 |
| 初始化 | VA 0x46D200：拷贝标准表（S-boxes 0x1000B @ 0x1EF1E88 → ctx+0x48；P-array 0x48B @ 0x1EF1E40 → ctx+0）+ 调 0x46D290 密钥调度 | 同上 |
| 密钥 | **硬编码字符串 `"D1w0@t0sHtryF?5t"`（16 B）** @ .rdata VA 0x1F070F0（strlen=16 经导入 strlen 传入 `BF_init(ctx, key, 16)`，调用点 VA 0x64065F）；相邻 0x1F070E8 处还有 `"user0"`（5 B）密钥串（另一路 32B 密钥材料：0xA7 填充） | 反汇编 + 文件字节 |

调用图：`BF_init` 唯一调用者 0x640610；0x640610 由 0x63FCC0 / 0x640710 调用；加密侧 0x46CDE0→0x46CF10。
**Python 标准实现（表从 exe 提取，FIPS 测试向量 4EF997456198DD78 自检通过）对 A1/立包机 payload 的
ECB 解密/加密 × 偏移 0..23 × 4 密钥全阴性**（可打印率 ≈0.37 = 随机基线）；
且 5 个 .hcpp loader 函数的 8 层调用闭包（478–14,923 函数）**零路径**到该 Blowfish 链【实证，阴性】
→ Blowfish 与 Main blob 无关，判为红鲱鱼（疑为烧录/授权用途）。

### 2.4 其他密码常量【实证，阴性】

SM4 S-box/FK/CK、SM3 IV、TEA/XTEA delta、ChaCha/Salsa sigma 在 AutoShop IDE 全家（除 libcrypto-3.dll
的 OpenSSL 数据段）零命中。libcrypto-3.dll 的 AES_SBOX×4 = OpenSSL 内部表，AutoShop 对其的
79 个导入仅 `RSA_public_encrypt` 一个密码学导出【实证】。

---

## 三、T2：加密器定位（InterfaceMgr.dll `CUCodeCrypt`）【实证】

### 3.1 定位路径

1. 容器字符串全目录检索（`d3_find_hcpp_modules.py`）：AutoShop.exe 内有 "PPCH" ASCII @ .text 0x54D88C →
   反汇编 = **.hcpp 容器头写入函数**（VA 0x94E480：写 magic 0x48435050 + Field4=0x300 + memset 16B）——
   AutoShop 自身打包 .hcpp；InterfaceMgr.dll 有独立的 ASCII 串
   `?PackHcppFileFunc@CPrjCompare@@UAEXXZ` / `?SetPackHcppFileFunc@CPrjCompare@@UAEXP6AXXZ@Z`。
2. InterfaceMgr.dll 导出表（4,534 符号）中发现 **`CUCodeCrypt` 类**：
   `EncryptArithmetic`（RVA 0x545F0，private 但导出）、`DecryptArithmetic`（RVA 0x54640，同）、
   `UCodeFileEncrypt` ×4 重载、`UCodeDecrypt` ×4 重载、`ResetData`。
3. **AutoShop.exe 静态导入确认**：InterfaceMgr.dll 的 1,033 个导入中含 14 个 UCode/Crypt 符号：
   `??0CUCodeCrypt@@QAE@XZ`（ctor）、`??1`（dtor）、`?UCodeDecrypt@CUCodeCrypt@@QAEHPB_W@Z`、
   `?UCodeDecrypt@@QAEHPAEH@Z`、`?UCodeFileEncrypt@@QAEHPB_W0@Z / PAEH@Z / PB_W@Z`、
   `?Encrypt@CBaseData@@...`、`?IsEncrypted@CBaseData@@QAEHXZ`、`?m_pGetEncrypted@CBaseData@@...`、
   `?ResetData@CUCodeCrypt@@QAEXXZ`、`?g_Encrypt@@YAXPAEI@Z`、`?g_PlcIdentifyEncrypt@@...`【实证】。

### 3.2 加密器反汇编还原【实证】

`EncryptArithmetic`（RVA 0x545F0）核心循环（this+4=数据指针，this+8=长度）：

```
esi = 0; ecx = 0（字节计数 i）
loop:
    inc esi; cmp esi, 0xB; jne +2; xor esi, esi   ; 密钥索引 = (i+1) % 11
    mov bl, [esi + 0x100B6620]                    ; key = "SclSoftware"[esi]
    mov al, [edx]; add al, bl                     ; data[i] += key
    mov [edx], al
    test cl, 1; jne skip; xor al, cl; mov [edx], al  ; 当 i 为偶数：data[i] ^= (i & 0xFF)
    inc ecx; inc edx; cmp ecx, len; jb loop
```

`DecryptArithmetic`（RVA 0x54640）= 逆序：先（当 i 偶数）`xor [edx], cl`，再 `sub al, bl`【实证】。

### 3.3 密钥表【实证】

`mov bl, byte ptr [esi + 0x100B6620]` → InterfaceMgr.dll RVA 0xB6620（.rdata，ImageBase 0x10000000，
文件偏移 0xB5820）实际字节：**`53 63 6C 53 6F 66 74 77 61 72 65` = "SclSoftware"**（11 字节，含两个 'S'）。
与 P3 观测的 blob 奇数位循环字符多重集（"Software"+"c"+"l"+额外 'S'）**完全一致**【实证】。
"SclSoftware" 全安装目录 321 个 PE 中仅此一处【实证】。

### 3.4 .hcpp 打开路径与错误分支【实证】

- "加载Hcp文件中的工程属性信息错误，工程打开失败！请尝试重新打开工程目录下Hcpp文件！" = 资源串 ID **7596**
  （block 475 idx 12；CLI-RE 已定位 RVA 0x355B6E2，本次补出 ID）；同族 ID 7597（版本不兼容）、
  7598（文件列表错误）。
- 三串的 `push imm32` 全部落在**同一个错误报告函数**（VA 0x16ADD F0）：`report(this+0x90, code 0..3, &msg)`，
  跳转表 @ 0x16ADEC8，按 code 选 LoadString 后交 [0x1EB35E8]。
- 该报告函数的 5 个调用者 = 5 个 .hcpp 加载函数：0x16A8EB0 / 0x16AF050 / 0x16AF660 / 0x16C0850 / 0x16C6360，
  全部经单例链 `0x1581A70 → 0x1581C20` 取 this+0x90 报错【实证】。
- 解密调用图：CUCodeCrypt 的 14 个导入在 AutoShop.exe 内有 200+ 间接调用点
  （`UCodeDecrypt(PB_W0)` 93 处、`UCodeFileEncrypt(PB_W)` 100 处等）；与 .hcpp 打开路径最近的：
  0x161E9A0（UCodeDecrypt(PAEH)，解 0x8C=140B 缓冲）、0x16AC020（UCodeFileEncrypt）——
  5 个 loader 无**直接** IAT 调用（解密经中间层函数到达，闭包深度限制内未逐一展开）【实证 + 如实记录】。

---

## 四、T3：解密实证（三路独立验证，全部通过）【实证】

### 4.1 算法定式

```
加密：ct[i] = ((pt[i] + key[(i+1) % 11]) & 0xFF) ^ (i & 0xFF 当 i 为偶数)
解密：pt[i] = ((ct[i] ^ (i & 0xFF 当 i 为偶数)) - key[(i+1) % 11]) & 0xFF
key = "SclSoftware"（11 字节）
```

### 4.2 验证矩阵

| # | 验证 | 结果 |
|---|---|---|
| 1 | **Main blob → UTF-16LE XML**：A1 blob 18,944 B 全量解密 = FF FE BOM + 9,472 字符 XML，`<ProjectDesc>模板A工程描述</ProjectDesc>`、`<GCMModal>H3U</GCMModal>`、`<HardwareFile>H3U.dll</HardwareFile>`、历史版本记录（`模板A` / `AutoShop V4.11.0.0.LXH_01B11` / `2025-10-16 15:18:44`）全部逐字节可读 | **完美**【实证】 |
| 2 | **立包机 blob**：263,612 B → 131,806 字符 XML，`<GCMModal>H6UA8</GCMModal>`、`<projnameex>小型PLC批次模板v1</projnameex>`、128 条文件记录（`PID_ATC-PID_ATC.libtemp` 等）全可读 | **完美**【实证】 |
| 3 | **LoginPassowrd.dat 已知答案**：17 B 密文 `B0DBB5D4A7A487A6D7D5CDDC7AD79B31`+`02` → `"Mode=0\nEmpty\n"` + 3 字节校验 + 0x02 尾字节 | **完美**【实证】 |
| 4 | **"bj" prologue**：enc(FF FE BOM) = `62 6A`（(0xFF+0x63)&0xFF=0x62 'b'；(0xFE+0x6C)&0xFF=0x6A 'j'） | **精确**【实证】 |
| 5 | **全零明文形态**：enc(8×00) = `63 6C 51 6F 62 74 71 61`（"clQobtqa"）= 模板 .hcpp 内全部 5 个文件内容记录的头 8 字节（.LD/.mon/.dev/.cfg 为**明文**二进制格式，其头 8 字节恰为该常量） | **精确**【实证】 |
| 6 | **P3 "SlotaecSfwr" 相位**：奇数位 blob[2k+1] = key[(2k+2) % 11]（enc(0) 于 UTF-16LE 高字节 0x00 处），相位锁定 blob[21]='S' 与首槽 'j' 例外 | **精确**【实证】 |
| 7 | **C# 交付自测**：`AutoShopProjectCrypt.SelfTest()` = PASS（已知答案 + 双向随机往返 8 组 + "bj"/"clQobtqa" 形态）；`TryDecryptProjectXml(A1 blob)` = True，ProjectDesc = "模板A工程描述" | **全过**【实证】 |

### 4.3 P3 未解之谜全解【实证，全部闭合】

| P3 记录 | 本轮解释 |
|---|---|
| 奇数位 "SlotaecSfwr" 循环（周期 22，相位锁定） | **不是交错层**：明文 = UTF-16LE，ASCII 字符高字节 = 0x00；enc(0x00)（奇数位无 XOR）= key[(i+1)%11] = 固定循环。"SlotaecSfwr" 与密钥表 "SclSoftware" 同多重集 |
| 首槽 blob[1]='j' 例外 | BOM 第二字节 0xFE：enc = (0xFE + key[2]=0x6C) & 0xFF = 0x6A 'j' |
| "循环挂起区"（17/56 处，1–13 B 连续双奇偶 payload） | **CJK 汉字**（UTF-16 高字节非零）处的表循环中断；A1 首个挂起区 blob[181..193) = `模板A工程描述` 的 UTF-16 字节 |
| blob[0..2) "bj" prologue | enc(FF FE) = UTF-16LE BOM 的密文 |
| 两工程公共前缀恰 180 字节 | 项目无关 XML 头 `<?xml ... ?>\r\n<project>\r\n\t<Version>1</Version>\r\n\t<ProjectDesc>`（90 个 UTF-16 字符 = 180 B），ProjectDesc 自 char 90 起 |
| payload 熵 7.981/7.999 | UTF-16LE XML：每字符 2 字节、ASCII 字符低字节高重复 + 高字节固定 0x00 → 解密后熵极低；密文熵高只因 ADD/XOR 扩散 |
| "AES 加密"论断 | **证伪**：密文无 AES 表常量支撑；真实算法 = 11 字节表 ADD/XOR 流密码 |

### 4.4 阴性尝试矩阵（诚实记录）

以下全部阴性（可打印率 ≈ 随机基线 0.37–0.48，已知标记 0 命中）：

1. D2 的 8 个 EnDecrypt.dll AES-128 密钥 × {厂商 InvCipher, Python} × {ECB/CBC/CFB/OFB/CTR} × 偏移 0..31（D2 已做，本轮未重复）。
2. 本轮：Blowfish（标准表自 exe 提取）× 4 密钥（"D1w0@t0sHtryF?5t" 16B/UTF-16 形态、"user0" 5B、"user0"+A7×27 32B）× {ECB 解密, ECB 加密} × de-interleave/原始 blob × 偏移 0..23 —— 168 组合全阴性。
3. UCodeCrypt 参数扫描中的错误参数（keyidx = i / 2i+2 / 2i / i+2、xor = odd/all/none、方向反）——对 LoginPassowrd.dat 与 payload 全部阴性（仅正确参数组合命中）。

---

## 五、T4 交付

| 层 | 位置 | 说明 |
|---|---|---|
| **C# 解密器** | `tools/AutoShopNativeBridge/src/crypto/AutoShopProjectCrypt.cs`（新增） | `DecryptBlob/EncryptBlob/TryDecryptProjectXml/SelfTest`；纯内存只读，无文件写/注册表/网络/PLC，不加载任何厂商 DLL |
| 自测 | 同文件 `SelfTest()` + `%TEMP%\plcmcp-handoff\d3-encrypt-recon\cs-selftest\`（独立 net8.0 控制台工程，链接该文件，不入仓库） | 实测：SelfTest PASS；A1 blob 18,944 B → True，9,471 字符 XML，ProjectDesc="模板A工程描述" |
| 解密样本落盘 | `%TEMP%\plcmcp-handoff\d3-encrypt-recon\{A1,libao}_decrypted_full.bin` | A1 18,944 B / 立包机 263,612 B 的 UTF-16LE XML 明文 |
| 逆向脚本 | `%TEMP%\plcmcp-handoff\d3-encrypt-recon\*.py`（`d3_scan_crypto.py`、`d3_find_callers*.py`、`d3_closure2.py`、`d3_t3_*.py`、`d3_dis.py`、`d3_find_hcpp_modules.py` 等，不入仓库） | 全部证据链可复现 |
| 文档 | `docs/inovance-autoshop-project-encryption.md`（本文件）；`docs/inovance-autoshop-native-bridge.md` 第十四节 | 交付与集成结论 |
| handoff | `%TEMP%\plcmcp-handoff\handoff.json` 新增 `projectEncryption` 键 | 跨代理事实同步 |

C# 最小用法（不抛异常版）：

```csharp
using PlcMcp.AutoShopNativeBridge.Crypto;

// 解密 Main blob（P3 MainBlobScanner.ExtractPayload 的输出或 .hcpp Main 记录 blob）：
bool ok = AutoShopProjectCrypt.TryDecryptProjectXml(blob, out string? xml, out string? error);
// ok=true 时 xml = UTF-16 解码后的工程 XML（以 "<?xml " 起，含 <project> 工程属性/文件列表）。

// 仅解密字节（不校验结构）：
byte[] plain = AutoShopProjectCrypt.DecryptBlob(blob);

// 自检（离线已知答案 + 往返）：
bool pass = AutoShopProjectCrypt.SelfTest();
```

实现纪律：

1. **纯只读**：不加载厂商 DLL、不写文件/注册表、无网络、无 PLC；解密结果仅用于离线工程读取/诊断。
2. 自测含**已知答案锚点**（LoginPassowrd.dat 真实样本密文→明文）——密钥表或算法随版本漂移时自检即失败，
   充当版本闸门。
3. `TryDecryptProjectXml` 校验 BOM + `<?xml ` 前缀，失败即返回 error（不猜测、不吞错）。
4. 该文件**不参与** PlcMcp.slnx 构建（tools/ 为原生工程）；集成时按需并入工程或原样复制（无外部依赖，net8.0 可编译）。

---

## 六、给集成阶段的结论【实证】

1. **工程读取解锁（核心结论）**：.hcp 工程源内容 = **UTF-16LE XML**，加密算法与密钥已完全还原并三路实证
   → **Main blob 可离线解密读取**：工程属性（PLC 型号 `<GCMModal>H3U/H6UA8</GCMModal>`、CPU 版本、
   硬件 DLL、能力值）、文件列表（128 条文件记录：`*.LD/*.mon/*.dev/*.cfg/*.flib`）、历史版本全可读。
   **变量导出/符号浏览/工程读取**随之解锁（读取路径 = .hcpp 容器解析（P1 已有 C# reader）→ Main blob 解密（本交付）→ XML 解析）。
2. **程序合成同步解锁（加密方向）**：`EncryptBlob` 为 DecryptBlob 的精确逆（自检验证）——
   构造 UTF-16LE 工程属性 XML → 加密 → 填入 .hcpp Main 记录（P1 `HcppWriter` 已能产容器）
   → 可合成 AutoShop 可识别的工程属性。**注意**：合成工程的"AutoShop 打开→编译通过"往返仍未验证
   （CLI 打开对全部样本仍失败于"加载工程属性信息"——该失败原因本任务未解，见 4.5；合成工程能否打开需真机验证）。
3. **与 D1 下载协议 + IDE 编译的衔接**：
   - 下载链路不受影响：12939 原生协议（mp_core.dll `download_file_handler`）仍是编译产物装载必经之路（P3 第六节不变）。
   - 模拟器装载（h5u_daemon）启动时不读 project.hcpp/stprog.o（P3 实证）——解密打通不改变该结论。
   - 编译入口仍是 IDE 进程内 STCompiler.dll（无导出）；本交付使**工程内容的读/写两侧**可离线复现，
     为后续"生成工程 → 厂商打开"验证与变量/符号工具提供了缺失的一环。
4. **.LD 文件内容为明文**（本任务顺带实证）：模板 .hcpp 内 `*.LD/*.mon/*.dev/*.cfg` 文件记录为未加密的
   二进制格式（头 8 字节 `63 6C 51 6F 62 74 71 61` + "CLVTItem" 结构 + UTF-8 注释"网络注释" + LE 整数），
   **梯形图程序内容可直接离线读取**（格式细节属 P1/S1 工程格式域）；`LoginPassowrd.dat` 为同一 UCodeCrypt
   加密（已知答案锚点即来自它）。
5. **能力声明建议**：`autoshop_project_read`（解密读取）= 可标 Experimental（离线可验、有已知答案锚点）；
   任何"写回 .hcp/合成工程"能力 = 需打开往返验证后才可升级；下载/编译能力不变（Unsupported/审批门控）。

### 4.5 未解项（禁止当作事实）

1. CLI 打开 .hcpp 在"加载工程属性信息"失败的确切原因——解密本身已通（本任务），失败点在容器打开路径的
   其他环节（工程目录上下文？其他成员缺失？），未深入。
2. CUCodeDecrypt 家族其余重载（PB_W 宽字符串版）的确切使用语境（200+ 调用点未逐一归档）。
3. .LD/.mon/.dev 文件内部二进制格式（"CLVTItem" 结构、LE 字段语义）——属工程格式域，本任务未逆向。
4. `LoginPassowrd.dat` 明文尾部 3 字节（`\x84&\xcb`）语义（疑为内容校验）+ 0x02 尾字节语义【未解】。
5. 版本漂移：密钥表/算法针对 AutoShop 4.12.0.2 的 InterfaceMgr.dll；其他版本未验证（自检锚点可检测）。

---

## 七、限制与无厂商背书声明

- 全部结论针对本机 `D:\Inovance Control\AutoShop` 的 AutoShop.exe（55,592,960 B，RELOCS_STRIPPED）
  与 InterfaceMgr.dll（1,372,672 B，ImageBase 0x10000000），AutoShop V4.12.0.2 / x86；
  **无汇川厂商背书**，密钥与算法随版本漂移。
- 解密能力仅用于**只读**工程互操作（变量导出/符号浏览/诊断）；本仓库不提供任何面向 PLC 的写路径。
- 证据分级：算法/密钥/明文 = **实证**（三路独立验证 + C# 已知答案自检）；挂起区=CJK 汉字 = **实证**（字节级拟合）；
  "程序合成可打开" = **未验证**（需真机打开实验）。
- 实验红线合规：安装目录零写入（仅读）、无注册表写、无网络、无 PLC 数据、无驻留进程（全部分析为离线文件读取 + 短命 Python 进程）。
