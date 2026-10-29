# 汇川 InoVarSharedService ITP 协议逆向文档

> 本文件是 AutoShop 共享变量服务（InoVarSharedService）本地 TCP 协议（内部称 ITP）的逆向结论。
> 所有结论均标注 [实证]（本机探测/日志/反汇编直接证据）或 [推断]（无直接证据的假设）。
> **本接口为逆向所得，无厂商背书，版本锁定 AutoShop 4.12.0.2**（本机 `AutoShop.exe` FileVersion 4.12.0.0，
> 与任务书记载的 4.12.0.2 安装版对应；探测主机上的安装目录为 `D:\Inovance Control\AutoShop`）。
> 本文档对应实现：`src/PlcMcp.Engineering/Workers/AutoShop/**`（只读桥）。

## 一、环境与证据来源

| 项 | 值 | 证据 |
|---|---|---|
| AutoShop.exe | `D:\Inovance Control\AutoShop\AutoShop.exe`，FileVersion 4.12.0.0，进程 PID 24492 运行中 | [实证] `Get-Process`/`VersionInfo` |
| 服务 | `InoVarSharedService`（显示名"汇川变量共享服务"），WIN32_OWN_PROCESS，AUTO_START，LocalSystem，STATE=RUNNING | [实证] `sc qc/query InoVarSharedService` |
| 服务镜像 | `D:\Inovance Control\AutoShop\AutoVarShared\InoVarSharedService.exe`（x86/WOW64，13 KB，加载 PluginCommunicate.dll） | [实证] `sc qc` + 目录枚举 |
| 监听 | TCP `127.0.0.1:13939` LISTENING（属主 PID 22488 = InoVarSharedService） | [实证] `Get-NetTCPConnection` |
| 端口持久化 | `HKLM\SOFTWARE\WOW6432Node\AutoServicePort`，值 `Data`（REG_BINARY，2 字节小端）= `73 36` = 0x3673 = 13939；`Info=unsigned short`，`Length=2` | [实证] `reg query`；键名字符串 `SOFTWARE\AutoServicePort` 见 PluginCommunicate.dll [实证] |
| 日志 | `D:\Inovance Control\AutoShop\AutoVarShared\SharedVaribleLog\2026-10-23_1.log`（只读证据源） | [实证] |
| 符号 | PluginCommunicate.pdb：`CPacketHandler::{ProcessPacketHeader,IsValidCompletePacket,HandlerRegister,HandlerRegAnswer,HandlerSendRequest,HandlerClose,GetSendConnectVec,GetITPConnectVec,DelRegisterInfo,RecycleConnectOjbect}`、`PacketHeader`、`CVarDataMgr`、`CRegHandler::{GetServicePort,SaveServicePort,GetAvaliablePort,GetTcpPortState}`、`g_GenCrc16Code`；源码路径 `E:\ReleaseCode\autoshop_main\Src\InoVarSharedService\*` | [实证] PDB 符号表 |
| 官方测试客户端 | 同目录 `TestITPClient.exe`（x86 Debug 构建，jsoncpp），字符串含 `varModify`、`VarSize`、`defaultValue` | [实证] 二进制字符串 |

## 二、探测方法（只读）

1. 对 `127.0.0.1:13939` 发起 TCP 连接，发送候选 JSON 文本（裸文本、换行结尾、4 字节长度前缀），
   观察应答字节与服务日志增量。
2. 以 `dumpbin -disasm` 反汇编 `PluginCommunicate.dll`，定位报文头校验、CRC、分发逻辑。
3. 依据反汇编结论构造完整报文（魔数 + 头 + CRC + JSON），完成 register 握手并记录应答。
4. 全程未操作 AutoShop UI / 工程文件 / 注册表写入 / 服务启停 / PLC；仅发送 register 类 JSON 探测报文。

探测过程产生的服务日志错误（均为预期内的协议拒绝，属证据）：
- `Header Data!=HEADER_DATA_MAGIC;CPacketHandler::ProcessPacketHeader;23`（裸 JSON/长度前缀均触发）
- `Socket=764;CompletePackey Crc Error:;CPacketHandler::IsValidCompletePacket;69`（CRC 错误触发）

## 三、报文结构 [实证]

ITP 报文 = **28 字节定长头 + 负载**。单包上限 1460 字节（0x5B4），负载上限 1432 字节（0x598）。

### 3.1 报文头（28 字节，小端）

| 偏移 | 长度 | 字段 | 说明 |
|---|---|---|---|
| 0x00 | 4 | magic | `0xA50EA50E`，线上字节 `0E A5 0E A5`（反汇编 `cmp dword ptr [ecx],0A50EA50Eh`） |
| 0x04 | 4 | dataLen | 负载字节数；总包长 = 0x1C + dataLen，超过 0x5B4 拒绝 |
| 0x08 | 4 | packetType | 报文类型，取值 1..10（见 3.3） |
| 0x0C | 4 | connectId | 应答中为 0；请求中探测未使用 |
| 0x10 | 2 | packetId | 类型 2 日志 `ParsePacket packetID=%d` 对应字段 |
| 0x12 | 2 | （未确认） | 应答中为 1 [推断：与 platform/连接索引相关，未验证] |
| 0x14 | 2 | connect id | 应答日志 `Header Connect id=%d` 对应字段 |
| 0x16 | 4 | （未确认） | 应答中为 `FF FF FF FF` [推断：保留/-1] |
| 0x1A | 2 | crc16 | CRC-16，见 3.2 |

字段偏移依据：反汇编 `ProcessPacketHeader` 读 `[hdr+4]` 作长度、`HandlerRequest` 以 `[mem+8]`（即 hdr+0x08）做 1..10 跳转表分发；`HandlerRegAnswer` 日志打印 `[mem+5E0h]`（连接 id）。[实证：偏移来自反汇编；0x12/0x16 语义为推断]

### 3.2 CRC-16 [实证]

`g_GenCrc16Code(const char*, uint32)` 反汇编等价于：

```
crc = 0xFFFF
for b in data:            # 逐字节
    crc ^= b
    repeat 8: crc = (crc>>1) ^ 0xA001  if crc&1 else crc>>1
return byteswap16(crc)    # 高低字节交换后写入 0x1A
```

即反射多项式 0xA001、初值 0xFFFF、无最终异或，**返回值字节序交换**。校验范围（`IsValidCompletePacket`）：
- `dataLen == 0`：对报文头前 26 字节（0x1A 之前，即不含 CRC 字段）计算；
- `dataLen > 0`：对负载前 min(dataLen, 0x598) 字节计算。

实证交叉验证：应答报文 `Data=24`、负载 `{\n   "Response" : "3"\n}\n`，服务日志 `Header data Crc=20279`（=0x4F37），
与应答头 0x1A 处 `37 4F` 一致；我方请求 CRC 0xAB75 被服务接受。

### 3.3 报文类型 [实证：分发跳转表；语义部分为推断]

| type | 处理 | 说明 |
|---|---|---|
| 1 | `IOCPHandle::HandlerRegister` → `CPacketHandler::HandlerRegister` | register 握手，负载为 JSON |
| 2 | 记录 `ParsePacket packetID=%d` 后转发（relay） | 非查询接口 |
| 3..7 | `CPacketHandler::HandlerSendRequest` | 共享变量数据推送（二进制负载，格式未验证） |
| 8..10 | 其他/默认分支 | 未探测 |

> 本桥只构造 type=1，只接收应答/推送；不构造、不发送任何数据类报文。

## 四、Register 握手时序 [实证]

```
client                                InoVarSharedService (127.0.0.1:13939)
  |  TCP connect -------------------->  "New Connect Success,socket=N"
  |  type=1 packet                     ProcessPacketHeader: magic/len ok
  |  {"Path": "<path>"}                IsValidCompletePacket: CRC ok
  | ---------------------------------> HandlerRegister: Json parse "Path"
  |                                      "Parse register sPath=<path>"
  |                                      "Socket=N;Register path success:"
  |                                      GetSendConnectVec (按 path 找发布者)
  |                                      "Query Fail,ResponseMsg = 3"   <- 无发布者时
  |                                      HandlerRegAnswer: {"Response":"3"}
  | <--------------------------------- type=1 packet, 负载为 styled jsoncpp JSON
  |  close -------------------------->  DelRegisterInfo / RecycleConnectOjbect
```

### 4.1 实测报文样例（2026-10-23 22:54，唯一一次成功握手记录）

请求头（28 字节）：
```
0E A5 0E A5  2A 00 00 00  01 00 00 00  00 00 00 00
01 00        00 00        00 00 00 00  75 AB
```
请求负载：`{"Path": "D:\\PLCmcp\\itp-readonly-probe"}`

应答头（28 字节）：
```
0E A5 0E A5  18 00 00 00  01 00 00 00  00 00 00 00
00 00        01 00        01 00 FF FF FF FF  37 4F
```
应答负载（jsoncpp styled 输出，含换行）：
```json
{
   "Response" : "3"
}
```

### 4.2 应答码

| Response | 含义 | 证据 |
|---|---|---|
| `"3"` | register 被接受，但该 path 下**无发布者**（服务日志 `Query Fail,ResponseMsg = 3`） | [实证] |
| 其它（0/1/2…） | 未知 | [未验证] 未观测到 |

服务侧日志顺序佐证：`Register path success:` → `GetSendConnectVec End;size =0` → `Query Fail,ResponseMsg = 3`
→ `HandlerRegister vecSocket size = 0:` → `Header Connect id=1` / `Response Msg = 3` / `Header Data Len=24`
→ `socket=N;HandlerRegAnswer byte=52`（= 28 头 + 24 负载）。

### 4.3 错误行为 [实证]

| 输入 | 服务行为 |
|---|---|
| 魔数错误（含裸 JSON、LE/BE 长度前缀 + JSON） | `Header Data!=HEADER_DATA_MAGIC`，无应答 |
| CRC 错误 | `CompletePackey Crc Error` + `Error packet`，无应答，连接被回收 |
| 19 字节以下过短输入 | 不触发头解析（服务等待凑满头部） |
| type=2 带 JSON | 无应答（relay 路径） |
| register 后保持连接 8 s | 无推送（无发布者） |

## 五、共享变量数据推送 [未验证]

- 设计（[推断]，依据符号与日志格式）：AutoShop 作为**发布者**以自身 path 注册；接收方 register 同一 path；
  变量变化时服务以 `HandlerSendRequest`（日志 `datatype=%d, dataLength=%d, socket=%d, packId=%d`）向接收方
  推送二进制数据报文（type 3..7）。
- 本次探测时 AutoShop.exe 虽在运行，但服务持续报 `Query Connect Status` 失败（每 15 s），
  且我方 register 一律得到 `Response=3`（无发布者），**未观测到任何数据推送报文**。
- `TestITPClient.exe` 字符串 `varModify`/`VarSize`/`defaultValue` 提示客户端侧存在"变量定义/默认值"类
  报文字段，但该路径可能**创建/修改共享变量定义**（写路径），本任务书禁止，未做任何探测。

**结论：数据推送报文的二进制布局、字段语义、应答码均未实证。** 因此本桥不实现变量值解码，
相关能力一律标 `Unsupported` 并写明原因（见实现 `AutoShopBridgeServices.BuildCapabilities`）。

## 六、未验证项清单（禁止在实现中当作事实）

1. `Response` 码除 `"3"` 以外的取值与语义。
2. 数据推送报文（type 3..7）的二进制布局、dataType 枚举、packId 语义。
3. 报文头 0x0C/0x12/0x16 字段语义（连接 id、platform、保留位）。
4. `TestITPClient` 的变量定义类报文（varModify/VarSize/defaultValue）——疑似写路径，未探测。
5. register 的 `Path` 取值空间（空串、AutoShop 安装目录、工程路径均返回 3，未找到返回非 3 的取值）。
6. 端口发现机制细节：`GetPort` 失败 → `GetAvaliablePort` → `SaveServicePort`（注册表
   `HKLM\SOFTWARE\WOW6432Node\AutoServicePort\Data`）已实证；但 `GetTcpPortState` 的探测目标未验证。
7. 多包流水线、超大包（>1432 字节负载）分帧行为未实测（上限来自反汇编）。

## 七、实现映射（只读桥）

| 实现 | 说明 |
|---|---|
| `ItpProtocol.cs` | 报文头/CRC/JSON 模型；CRC 黄金值测试绑定 4.1 节实测报文 |
| `ItpClient.cs` | 仅构造 type=1 register、仅读应答/推送；超时抛 `TimeoutException`，不把超时转成成功 |
| `AutoShopEnvironmentDetector.cs` | exe/版本、服务进程、注册表端口、TCP 监听、日志文件证据；全部探针可注入 |
| `AutoShopBridgeServices.cs` | 环境检查 + 握手 + 受控只读变量访问（握手 + 有界观察窗口，只计数推送包不解码） |
| `AutoShopBridgeWorker.cs` | `IEngineeringWorker`；仅接受 InspectProject；硬拒绝 Download/RunStop/Force 类操作；结果最高 `Experimental` |
| MCP 工具 | `plc_autoshop_inspect`、`plc_autoshop_read_shared_variables`，仅在 `--autoshop-bridge` 启用时注册 |

安全边界：不提供写变量、Force、下载、编译、RUN/STOP 的任何执行路径；不对 AutoShop UI/工程/注册表/服务做任何操作；
不连接 PLC。
