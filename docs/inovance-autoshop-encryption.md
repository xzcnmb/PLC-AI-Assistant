# 汇川 AutoShop EnDecrypt.dll（AES 加密库）密钥机制与导出可行性实验（D2）

> 本文档固化 D2（EnDecrypt.dll 密钥导出可行性实验）的全部实测结论。
> 证据来源：`D:\Inovance Control\AutoShop\EnDecrypt.dll` 的只读静态分析
> （dumpbin /exports /disasm /imports /headers）与 x86 垫片（CryptoShim.dll）+
> 验证 harness（CryptoHost.exe）在本机进程内的实调实验。
> **未修改安装目录任何文件、未写注册表、未连接任何网络、未向任何 PLC 发送数据。**
> 与 `docs/inovance-autoshop-program-blob.md`（P3：Main blob 结构）、
> `docs/inovance-autoshop-native-bridge.md`（S2 垫片方法学）配合阅读。
>
> 标注约定：【实证】= 本次亲自执行并复核；【推断】= 基于实证的推理；
> 【未解】= 无可信证据，实现中禁止当作事实。**全部接口均为逆向所得，无汇川厂商背书。**

---

## 一、结论摘要（先读这一节）

| 项 | 结论 |
|---|---|
| EnDecrypt.dll 是什么 | VC10（MSVCP100/MSVCR100）时代的 x86 AES 工具库，PE 时间戳 2016-01-14，PDB 源工程 `E:\MD\code\pcVS\...\EncryptionTools\tmpr\EnDecrypt\EnDecrypt.pdb`；29 个命名导出【实证】 |
| 密钥来源 | **硬编码**：8 个 16 字节 ASCII 密钥直接存在 .rdata（`HC.0793.*` / `Hc.1012.*` 两族），由导出函数 `GetPassword/GetPasswordN` 返回；`CEncryptAlg::GetKey` 的 8 个 32 位常量字（32 B）直接内联在代码里【实证（反汇编 + 运行导出一致）】 |
| AES 参数 | **标准 AES-128-ECB 单块**：10 轮（核心循环 `cmp eax,0Ah`）、标准 S-box/逆 S-box（以小端 dword 立即数写入 752 B 工作区）、单次调用只处理 16 字节；**8/8 密钥与标准 AES-128-ECB 密文逐字节一致**（FIPS-197 自检通过后对照）【实证】 |
| 密钥是否可导出 | **可导出，且零门槛**：`GetPassword/GetPasswordN/CreateNewKey/GetKey` 虽声明 private 但**都在导出表里**，`GetProcAddress` 直接可调；静态提取与运行实调 8/8 一致【实证】 |
| Main blob 是否用这些密钥 | **否**：真实模板 payload（A1 9,541 B / 立包机 132,016 B）用全部 8 个密钥经**厂商 InvCipher**（已证与标准 ECB 等价）逐块解密，可打印率 ≈0.38、熵 ≈7.98、已知明文标记 0 命中；Python 横扫（8 密钥 × 加/解密 × ECB/CBC/CFB/OFB/CTR × 偏移 0..31）同样全阴性【实证】 |
| AutoShop 是否使用 EnDecrypt.dll | **完全不引用**：AutoShop.exe 与全部自装 DLL 无静态导入、无 "EnDecrypt" 字符串（ASCII/UTF-16）；仅 `EncryptionToolDll.dll` 静态导入（ctor/dtor/CipherN）与独立的 InoLoaderPro 产品（MainControl.dll 动态解析 5 个导出）使用它【实证】 |
| 对 P3 假设的裁决 | **证伪**：P3 "若 CAESAlg 密钥可导出，blob payload 即可解密" 的前提（密钥可导出）成立，但结论不成立——这些密钥**不是** Main blob 的密钥；blob 加密机制另有其处、仍然未解【实证（阴性）】 |

---

## 二、T1：EnDecrypt.dll 静态分析

### 2.1 全量导出表（dumpbin /exports，29 个命名导出）【实证】

**CAESAlg**（AES 单块算法类，vtable `??_7CAESAlg@@6B@` RVA 0x51CC）：

| 导出（mangled） | RVA | 还原签名 |
|---|---|---|
| `??0CAESAlg@@QAE@XZ` | 0x2130 | 默认构造：malloc **0x2F0 = 752 B** 工作区，调 0x15A0（写 S-box）/0x1810（写逆 S-box） |
| `??0CAESAlg@@QAE@ABV0@@Z` / `??4` | 0x2080/0x20A0 | 拷贝构造 / operator= |
| `??1CAESAlg@@UAE@XZ` | 0x21C0 | 虚析构：free 工作区（**不** free 对象本身） |
| `?Cipher@CAESAlg@@QAEPBEPBEW4PASSWORDTYPE@@@Z` | 0x23B0 | `const u8* Cipher(const u8* in, PASSWORDTYPE)`——**非虚**，先调 GetPassword 取密钥 |
| `?CipherN@CAESAlg@@QAEPBEPBEW4PASSWORDTYPE@@@Z` | 0x2400 | 同上，用 GetPasswordN |
| `?InvCipher@CAESAlg@@QAEPBEPBEW4PASSWORDTYPE@@@Z` | 0x2450 | 解密方向（非虚），用 GetPassword |
| `?InvCipherN@CAESAlg@@QAEPBEPBEW4PASSWORDTYPE@@@Z` | 0x24A0 | 解密方向（非虚），用 GetPasswordN |
| `?Cipher@CAESAlg@@UAEPBEPBE0H@Z` | 0x21E0 | **虚**：`const u8* Cipher(const u8* in, const u8* key, int keyLen)`——调用方自带密钥 |
| `?InvCipher@CAESAlg@@UAEPBEPBE0H@Z` | 0x2200 | 虚解密（调用方密钥） |
| `?GetPassword@CAESAlg@@AAE_NPAEW4PASSWORDTYPE@@@Z` | 0x2220 | **private 但已导出**：`bool GetPassword(u8* out, PASSWORDTYPE)` |
| `?GetPasswordN@CAESAlg@@AAE_NPAEW4PASSWORDTYPE@@@Z` | 0x2300 | private 但已导出，N 族 |

**CEncryptAlg**（向量封装层，vtable RVA 0x5220）：ctor（RVA 0x2730，签名
`(vector<unsigned>& key, vector<unsigned char>& data, unsigned n)`）、拷贝构造、
operator=、dtor、以及 **private 但已导出**的
`CreateNewKey`（RVA 0x2C40）、`GetKey`（RVA 0x2920）、`Insert`（RVA 0x2C10）、
`EncryptBlock`（RVA 0x2E10）、`EncryptFrame`（RVA 0x2FD0）与公开的
`Encrypt(vector<unsigned char>&)`（RVA 0x31D0）。

**CEnDecryptAlg**：组合类（ctor 0x2000/0x3CE0、dtor 0x3CF0、operator= 0x2010、vtable 0x51BC）。

依赖（dumpbin /imports）：**仅** MSVCP100/MSVCR100/KERNEL32；KERNEL32 全集为
CRT 安全 cookie 锅炉代码（IsDebuggerPresent、QueryPerformanceCounter、
GetSystemTimeAsFileTime、GetTickCount、GetCurrentProcessId/ThreadId、
SetUnhandledExceptionFilter 等）+ `Sleep` + `DisableThreadLibraryCalls` + `TerminateProcess`。
**无 CreateFile/ReadFile、无 Reg*、无 socket/WSA**【实证】→ 密钥不可能在调用期从文件/注册表/网络加载。

### 2.2 CAESAlg 对象与工作区布局（反汇编重建）【实证】

对象本体 = vtable + 1 指针（8 B）；752 B（0x2F0）工作区（ctor 内 malloc、dtor 内 free）：

| 偏移 | 内容 | 证据 |
|---|---|---|
| +0x00..0x10 | 当前 16 B 输入块（原样拷贝） | 核心 0x1D70 开头 `mov edi,10h` 16 字节拷贝循环 |
| +0x10..0x20 | 状态矩阵（列主序转置） | 0x1DA4..0x1DD1 转置循环（dst[+0x10..]=src[0]/[4]/[8]/[0xC]） |
| +0x20..0x30 | **密钥**（`*(&keyLen)` 字节，非虚路径=16） | 0x1D8B..0x1D9C 拷贝循环，长度来自 `edx` 指向的槽 |
| +0x30..0xE0 | 扩展密钥（0xB0 = 176 B = **AES-128 轮密钥**） | 空间尺寸 0x2F0 与输出位置 0xE0 反推 |
| +0xE0..0xF0 | **输出块**（16 B；`Cipher` 的返回值指向这里） | 核心 0x1D70 结尾 `lea eax,[esi+0E0h]` |
| +0xF0..0x1F0 | **标准 AES S-box**（0x15A0 以 64 个小端 dword 立即数写入） | `mov dword ptr [...],7B777C63h`（=S-box[0..3]=63 7C 77 7B）起 |
| +0x1F0..0x2F0 | **标准 AES 逆 S-box**（0x1810 写入） | `mov dword ptr [...],0D56A0952h`（=52 09 6A D5）起 |

AES 核心流程（加密 0x1D70；解密 0x1EC0 镜像）：输入块拷入工作区 → 列主序状态转置 →
密钥拷入 +0x20 → 10 轮轮函数（0x1DD1 `call 100011D0`（首轮 AddRoundKey）、
轮循环 `cmp eax,0Ah; jl`、末轮 0x1E01 `call 10001480`）→ 状态反转置 → 输出写 +0xE0。
**每次调用只处理 16 字节（单块）**——多块模式（如 ECB/CBC）由调用方自行串接【实证（反汇编）】。

### 2.3 PASSWORDTYPE 枚举与硬编码密钥（核心发现）【实证】

`GetPassword(u8* out, PASSWORDTYPE type)`（RVA 0x2220）反汇编：

- `cmp type,3; ja 跳出` → **枚举取值 0..3（4 个）**，跳转表 RVA 0x100022E8；
- 每个分支把 **4 个 dword（16 字节）** 从固定 .rdata 地址拷入 `out` 后 `return true`；
- `GetPasswordN`（RVA 0x2300）同构，跳转表 RVA 0x1000239C。

.rdata 实际字节（文件偏移 = RVA-0x10005000+0x3E00）【实证，直接从文件读出】：

| 族 | PASSWORDTYPE | 密钥（16 字节 ASCII） | .rdata 地址 |
|---|---|---|---|
| GP（GetPassword） | 0 | `HC.0793.20140217` | 0x1000512C |
| GP | 1 | `HC.0793.20140312` | 0x10005140 |
| GP | 2 | `HC.PWD.0793.0306` | 0x10005154 |
| GP | 3 | `HC.0793.20130306` | 0x10005168 |
| GPN（GetPasswordN） | 0 | `Hc.1012.20160217` | 0x1000517C |
| GPN | 1 | `Hc.1012.20160312` | 0x10005190 |
| GPN | 2 | `Hc.PWD.1012.0312` | 0x100051A4 |
| GPN | 3 | **= type 1**（跳转表 [0x100023A8] 指向 type 1 的分支） | — |

**语义判读**【推断】：`HC`/`Hc` 前缀 + 产品/项目代号（0793 / 1012）+ 构建日期
（2014-02-17、2014-03-12、2013-03-06、2016-02-17、2016-03-12）；`PWD` 标记的 type 2
疑为口令相关用途。GPN 族的日期（2016）与 EnDecrypt.dll 构建时间（2016-01-14）同期 →
GPN = 较新的密钥族。**每个取值对应哪个文件/用途未解**（AutoShop 不引用本库，见 2.5）。

`GetPassword` 返回值语义：仅当 out 为 NULL 时返回 false；type>3 落到 `return true`（不写 out）——厂商代码瑕疵【实证（反汇编）】。

### 2.4 CEncryptAlg 密钥机制（第二套）【实证】

- ctor（0x2730）：置 vtable、清零成员，调 **GetKey(局部 vector)** 后拷入成员向量 →
  **成员密钥无条件来自硬编码常量**，调用方传入的 key/data 向量在构造期未被用作密钥【实证（反汇编）】。
- `GetKey(vector<unsigned>& out)`（0x2920）：向调用方 vector push_back **8 个硬编码
  32 位常量字（32 B，对称排列 [A,B,C,D,D,C,B,A]）**【实证（运行导出）】：
  `5D195C26 7B300A41 14A003B7 62C591D5 62C591D5 14A003B7 7B300A41 5D195C26`
  （静态反汇编逐字提取见 `??1` 前 6 处 `mov dword ptr [eax],imm32`；62C591D5 两处为
  寄存器装载形式，运行导出补全为 8 字）。
- `CreateNewKey(vector<unsigned>& out)`（0x2C40）：把 out 清空（size→0、容量保留）后
  遍历**成员**向量（this+4）逐字变换写入 out；变换中的 XOR 项来自成员 this+0x14 的
  dword——在零初始化探针对象上该 dword=0 → 变换为恒等，输出 = 成员的 8 个字【实证（运行）】。
  完整密钥扩展调度（Rcon/轮数）在真实使用语境下才触发，**本实验未复现出扩展**
  （输出仍 8 字）——扩展语义【未解】。
- `Encrypt(vector<unsigned char>&)`（0x31D0）：**0x800 = 2048 B 分帧**
  （`shr edi,0Bh` 计满帧数、`push 800h` 逐帧拷贝），逐帧调私有 `EncryptBlock`
  （帧、数据、扩展密钥向量、0x800），尾部不足 2048 B 的余量单独处理【实证（反汇编）】。
  这是 P3 观察到的"循环挂起区=加密器帧边界"假说的候选对应物，但 A1 payload
  9,541 B = 4×2048+1,349 与 17 处 1-13 B 挂起区不对应【推断，未解】。

### 2.5 密钥消费者定位（谁用 EnDecrypt.dll）【实证】

| 二进制 | 引用方式 | 证据 |
|---|---|---|
| `EncryptionToolDll.dll`（AutoShop 根，1.6 MB） | **静态导入 3 个导出**：`??0CAESAlg@@QAE@XZ`（ctor）、`??1CAESAlg@@UAE@XZ`（dtor）、`?CipherN@CAESAlg@@QAEPBEPBEW4PASSWORDTYPE@@@Z` | dumpbin //imports |
| `InoLoaderPro_V100R002B021.B5\MainControl.dll`（**独立产品**：Qt5 烧录工具，非 AutoShop IDE） | **运行时动态解析 5 个导出**（GetProcAddress 名字表在 0x18988A..0x189948）：ctor、dtor、`CipherN`、`InvCipher`、`InvCipherN` + "EnDecrypt.dll" | 全文件字节检索 |
| `InoLoaderPro\EnDecrypt.dll` + `EnDecrypt.lib` + `libIvtAes.so` | 同一源工程的第二份拷贝（.so 含相同 `HC.0793`/`Hc.1012` 密钥串 = Linux 移植） | 全文件字节检索 |
| **AutoShop.exe 与全部自装 DLL** | **零引用**：无静态导入（逐 DLL dumpbin 复核）、无 "EnDecrypt" 字符串（ASCII/UTF-16 全目录检索，排除模板目录） | dumpbin + 字节检索 |

**结论：Main blob 的加密不经过 EnDecrypt.dll**（AutoShop 无任何引用路径）；
`libcrypto-3.dll`（OpenSSL，AutoShop 根）只被 AutoShop.exe（EVP_Digest*/EVP_PKEY*/RSA_public_encrypt，
无 EVP_aes）与 EncrptionInstall.dll（仅 EVP_Digest*/EVP_sha256）使用，也无 AES 加密导入
【实证】→ **Main blob 的加密机制另有其处，仍然未解**。

## 三、密钥机制表（来源 → 证据 → 置信度）

| 密钥 | 来源 | 证据 | 置信度 |
|---|---|---|---|
| CAESAlg 8 个 16 B ASCII 密钥（`HC.0793.*`/`Hc.1012.*`） | **硬编码 .rdata**（0x1000512C..0x100051B3），GetPassword/GetPasswordN 按表拷出 | 反汇编（跳转表 + 4×dword 拷贝）与文件字节、运行实调三方一致 | **确定**【实证】 |
| 密钥长度 = 16 B（AES-128） | GetPassword 每分支拷 16 B；非虚 Cipher 传 `keyLen=0x10` 槽；扩展密钥空间 176 B（0x30..0xE0） | 反汇编 | **确定**【实证】 |
| 算法 = 标准 AES-128-ECB 单块 | 10 轮循环（`cmp eax,0Ah`）、标准 S-box/逆 S-box 立即数、FIPS-197 自检后 8/8 密钥密文与标准实现逐字节一致 | 反汇编 + 运行对照 | **确定**【实证】 |
| PASSWORDTYPE = 0..3（4 值） | GetPassword/GetPasswordN `cmp type,3; ja` | 反汇编 | **确定**【实证】 |
| PASSWORDTYPE 各值语义（哪个文件/用途用哪个） | 密钥串含产品代号+日期+PWD 标记 | 字符串判读 | **推断**（未解） |
| CEncryptAlg 密钥 = 8 个硬编码 32 位字（32 B） | GetKey push_back 立即数 + 运行导出 | 反汇编 + 运行 | **确定**【实证】 |
| CreateNewKey 完整扩展调度 | 零初始化探针下恒等（XOR 项 this+0x14=0）；真实语境扩展行为 | 运行 + 反汇编 | **未解**（部分实证） |
| Main blob 的密钥/机制 | 不在 EnDecrypt.dll（AutoShop 零引用）；全部导出密钥解密阴性 | 逐块厂商解密 + Python 横扫全阴性 | **阴性结论确定**；blob 机制**未解** |

## 四、T2：x86 垫片实验（S2 方法学）

### 4.1 垫片设计（`tools/AutoShopNativeBridge/src/crypto/`）

- **CryptoShim.dll**：运行时解析（`SetDllDirectory(安装目录)` + `LoadLibrary("EnDecrypt.dll")`
  + `GetProcAddress` 精确修饰名白名单）——build.cmd 不归本任务所有，未改动；解析白名单
  = 12 个 CAESAlg/CEncryptAlg 导出（含 4 个 private-but-exported）；
  **批量加密器（CEncryptAlg::Encrypt/EncryptBlock/EncryptFrame）与 CEnDecryptAlg 组合类
  刻意不解析**。平面 C API：`PlcMcpCrypt_Version/VendorPath/Load/Unload/GetPassword/
  CipherBlock/InvCipherBlock/CipherBlockWithKey/InvCipherBlockWithKey/RoundTrip/
  DecryptPayload/Analyze/GetKeyWords/CreateNewKeyWords`。纯内存只读：无文件写、无注册表、
  无网络、无 PLC。对象生命周期：`Unload` 在宿主退出前析构 CAESAlg（厂商 dtor 释放
  752 B 工作区）并 FreeLibrary【实证】。
- **CryptoHost.exe**：验证 harness。阶段 A 加载；阶段 B 密钥导出（2 族×4 type）；
  阶段 C 厂商往返（Cipher→InvCipher→原文）；阶段 D **FIPS-197 自检 + 厂商密文 vs
  标准实现对照**（harness 内置最小标准 AES-128，独立于厂商代码）；
  阶段 E blob 验证回路（反交错→厂商 InvCipher 逐块→可打印率/熵/已知明文标记）；
  阶段 F CEncryptAlg 密钥导出探针；阶段 G 释放。

### 4.2 运行结果【实证，2026-10-28，`%LOCALAPPDATA%\AutoShopNativeBridge-run\`】

```
[A] PlcMcpCrypt_Load("D:\Inovance Control\AutoShop") -> rc=0
    vendor: D:\Inovance Control\AutoShop\EnDecrypt.dll
[B] 密钥导出 8/8：GP type0..3 = HC.0793.20140217 / HC.0793.20140312 /
    HC.PWD.0793.0306 / HC.0793.20130306；GPN type0..2 = Hc.1012.20160217 /
    Hc.1012.20160312 / Hc.PWD.1012.0312；GPN type3 = Hc.1012.20160312（=type1）
    ——与静态 .rdata 提取逐字节一致
[C] 厂商往返 8/8 MATCH（Cipher→InvCipher→原文逐字节返回）
[D] FIPS-197 自检 PASS；厂商 vs 标准 AES-128-ECB：8/8 密钥密文一致
[E] blob 验证回路（阴性）：
    A1 payload 9541 B（596 块）/ 立包机 132016 B（8251 块），
    2 族 × 4 type 全部：printable≈0.38、entropy≈7.98-7.999、已知明文标记 0 命中
    CEncryptAlg 8 字密钥（32 B）经虚 InvCipher：同样阴性
[F] GetKey -> 8 words：5D195C26 7B300A41 14A003B7 62C591D5 62C591D5 14A003B7
    7B300A41 5D195C26；CreateNewKey（零初始化探针）-> 8 words（恒等，未扩展）
[G] PlcMcpCrypt_Unload -> vendor object + library released
```

辅助 Python 横扫（`%TEMP%\plcmcp-handoff\p3-blob-recon\`，不入仓库）：
8 密钥 × {解密, 加密} × {ECB, CBC(IV=0), CFB, OFB, CTR} × 偏移 0..31 ×
{反交错 payload, 原始 blob}，最高可打印率 0.479（≈随机基线）、标记 0 命中——
与厂商调用结果一致【实证】。

## 五、实验结论（诚实记录）

1. **密钥可导出 = 成立**：CAESAlg 的 8 个密钥与 CEncryptAlg 的 8 个密钥字均可
   （a）静态提取（.rdata/立即数）或（b）经导出函数运行取得；两种途径 8/8 一致。
   无需附加到任何 AutoShop 进程——密钥是编译期常量，任何加载 EnDecrypt.dll 的
   进程都能拿到。
2. **blob 不用 EnDecrypt.dll = 成立（阴性）**：AutoShop 自身零引用 EnDecrypt.dll；
   全部导出密钥对 Main blob payload 的解密全阴性（厂商调用 + Python 横扫）。
   **P3 的"CAESAlg 密钥 → blob 可解密"假设被证伪**；Main blob 加密机制未解
   （候选：AutoShop.exe/Global.dll 内的另一份 AES 拷贝 + 独立密钥、或非 AES 机制）。
3. **密钥不可导出的情形未发生**（任务书 T2 要求的"不可导出证据"分支不适用）——
   反汇编层面不存在随机派生/硬件绑定/进程内句柄：密钥全为编译期常量，无
   文件/注册表/网络导入。

## 六、给集成阶段（D1 下载协议、程序合成）的接口建议

1. **程序合成的封锁点不在 EnDecrypt.dll**：Main blob 加密机制需另行定位
   （建议：对 AutoShop.exe/Global.dll 搜 AES T-table/密钥调度特征、或动态跟踪
   "加载Hcp文件中的工程属性信息错误" 失败路径的解密调用栈）。
2. **EnDecrypt.dll 侧的可复用产物**（对 InoLoaderPro 生态/未来版本比对有用）：
   - 8 个密钥 + PASSWORDTYPE 语义（2.3 节表）；
   - 厂商 AES = 标准 AES-128-ECB 单块 → **集成侧可用任意标准 crypto 库等价复现**，
     不必加载厂商 DLL（垫片仅用于验证与未来版本比对）；
   - `CryptoShim.dll` 平面 C API（4.1 节）可直接用于未来的密钥变化检测
     （对比新旧版本 GetPassword 输出）。
3. **D1 下载协议**不受本实验影响：12939 原生协议（mp_core.dll
   `download_file_handler`）仍是编译产物装载的必经之路；本实验确认
   TestHost 无下载能力、本任务亦未触碰 12939。
4. **版本漂移防护**：EnDecrypt.dll PE 时间戳 2016-01-14 与 AutoShop 4.12.0.2 的
   2026-07 安装包共存（厂商复用旧库）；垫片的导出白名单解析失败即报错
   （`ResolveRequired` 负值码），可作版本闸门。

## 七、限制与无厂商背书声明

- 全部结论针对 `D:\Inovance Control\AutoShop\EnDecrypt.dll`（24576 B，
  PE 时间戳 2016-01-14，VC10 x86）；**无汇川厂商背书**，随版本漂移。
- PASSWORDTYPE 各取值与文件/用途的对应关系【未解】（AutoShop 不引用本库，
  无本机可观察的使用点）。
- CreateNewKey 的完整密钥扩展调度【未解】（零初始化探针下恒等）。
- Main blob 加密机制【未解】：本实验给出的是**阴性排除**
  （不是 EnDecrypt.dll 的任何导出密钥），不是正向定位。
- CEncryptAlg::Encrypt 的 2048 B 分帧与 P3 的挂起区对应关系【未解】。
- 垫片未做 PE 时间戳/导出集合自动闸门（S2 既有已知限制，见原生桥文档第八节）。
- 实验红线合规：安装目录零写入（EnDecrypt.dll mtime 保持 Jul 23）、
  无注册表写、无网络、无 PLC 数据、harness 退出前 Unload 释放全部句柄。
