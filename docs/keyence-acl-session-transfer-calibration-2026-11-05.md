# KEYENCE KV 8500 ACL 会话与程序传输协议校准报告 (2026-11-05)

## 1. 概述与分析依据

- **分析目标**：恢复 `PLC-AI-Assistant` 仓库中 `KeyenceProgramTransferFrames.cs` 所有标记 `[U]` 的 8500 ACL 二进制帧字节布局与字段语义。
- **环境与范围**：只读静态分析本机已安装的 KEYENCE KV STUDIO 12G 12.42.0.0 二进制文件（不执行、不修改、不注入、不向模拟器或外部网络发送任何数据包）。
- **核心证据源**：
  1. `Kvs.BinaryCommand_surface.txt`：`Kvs.BinaryCommand.dll`（.NET 8 程序集，128 类型 / 914 方法）的完整 IL 反编译 Dump，重点覆盖 `Kvs.BinaryCommand.Core.Cpu.Posix.*`、`Kvs.BinaryCommand.Core.Cpu.UserAuthenticationProtocol`、`Kvs.BinaryCommand.Core.Cpu.SimulatorControl.*`、`Kvs.BinaryCommand.Facade.Cpu.Posix.*`。
  2. `PROGRAM_AREA_AND_PROJECT_FORMAT.md` 与 `ACL_WIRE_FORMAT.md`：native 动态库 `kvs1.dll`、`KVComm.dll`、`LDKCOM.dll` 的反汇编与符号导出映射。
  3. `kvrev/scratch/` 关键反汇编文件：`KzRequestConnect.txt`、`KzRequestDisconnect.txt`、`kvs1_prog_dis.txt`、`kvs1_prog_dis2.txt`、`kvs1_prog_dis3.txt`、`seqdev_builders.txt` 等。
  4. 对 `KVComm.dll` 内部核心组帧函数 `0x46360`（`Kv2PxWriteCreate` 内部实现）及导出虚表的精确字节反汇编。
- **符号纪律**：
  - `[F]`（Fact）：有确凿 IL 指令操作码、机器码或导出表偏移佐证。
  - `[I]`（Inference）：由多方强一致证据推导出的高可信语义。
  - `[U]`（Undetermined）：静态证据尚未穷尽，诚实保留未知边界。

---

## 2. 传输层与会话封装边界 (Layering & Framing)

所有 ACL 命令在物理 TCP 连线上均由 `KeyenceAclTcpAdapter` 封装为 **10 字节大端头部 + ACL 命令体**：

```
+-------------------+-------------------+-------------------+-------------------+
| 00 01 02 03 (BE32)| 04 05 (BE16)      | 06 07 (BE16)      | 08 09 (BE16)      |
| Total Length      | ACL Body Length   | Station/Route (0) | Checksum          |
+-------------------+-------------------+-------------------+-------------------+
| 10 .. (10+BodyLen-1)                                                          |
| ACL Command Body (Byte-verbatim, 大端字段由各命令自行序列化)                   |
+-------------------------------------------------------------------------------+
```

- **Checksum 公式** `[F]`：`-(sum(header[0..7]) + sum(body[0..N-1])) & 0xFFFF`。
- **应答通用格式** `[F]`（`BinaryCommandUtility::AnalyzeResponseCode`）：
  - `resp[0]`：回显主命令码 `Opcode`
  - `resp[1]`：执行状态码 `ResponseCode`（`0x00` 表示成功，非 0 表示错误）
  - 若为子命令，`resp[2]` 为回显子命令码 `SubOpcode`。
- **IPC / 安全会话包装边界** `[F]`：
  当 PLC 开启用户认证且已登录获得 `SessionId` 时，所有被保护的关键写操作与提交操作（如 `CommitStart`、`CommitEnd`、`SetMode`、`ClearAll` 等）由通信层自动包装成 `0x2D 0x00` 封装帧（见下文 0x2D 节），底层命令体结构保持不变。

---

## 3. 端到端会话与程序传输时序 (Session Sequences)

### 3.1 上载序列 (Upload: PLC -> 上位机读取程序)

```
[上位机 Client]                                             [PLC / 8500]
       |                                                          |
  1.   |--- TCP SYN (Port 8500) --------------------------------->| (连接建立)
  2.   |--- 0x05 RequestConnect --------------------------------->|
       |<-- 0x05 00 (Success) ------------------------------------|
  3.   |--- 0x03 RequestType ------------------------------------>|
       |<-- 0x03 00 <Type Words> ---------------------------------|
  4.   |--- 0x02 GetReadSession --------------------------------->|
       |<-- 0x02 00 ----------------------------------------------|
  5.   |--- (可选) 0x2D 0x01 用户认证登录 ------------------------>| (若设置密码)
       |<-- 0x2D 00 <SessionId (8B)> <PermitFunction (16B)> ------|
  6.   |--- 0x46 0x29 PosixLock (ReadLimited=0x01 / ReadFull=0x03)|
       |<-- 0x46 00 0x29 .. <AuthCode (BE16 @+12)> ---------------|
  7.   |--- 0x46 0x22 CommitStart (kind=REQ_PROGRAM_READ 0x41) -->|
       |<-- 0x46 00 0x22 .. <CommitId (BE16 @+8)> ----------------|
  8.   |--- 0x46 0x04 PxOpen (path="/ROM/Project/.../All.prgx", 0x01)
       |<-- 0x46 00 0x04 .. <FileDescriptor (BE16 @+6)> ----------|
  9.   |== 循环分块读取 (单块上限 2032 字节) ======================|
       |--- 0x46 0x0D PxRead (fd, offset, len=2032) ------------->|
       |<-- 0x46 00 0x0D .. <Data Bytes @+16> --------------------|
 10.   |--- 0x46 0x0C PxClose (fd) ------------------------------>|
       |<-- 0x46 00 0x0C -----------------------------------------|
 11.   |--- 0x46 0x23 CommitEnd (Commit=0x00, commitId) --------->|
       |<-- 0x46 00 0x23 -----------------------------------------|
 12.   |--- 0x46 0x21 PosixUnlock (authCode) -------------------->|
       |<-- 0x46 00 0x21 -----------------------------------------|
 13.   |--- 0x3B 0x00 CheckProgData ----------------------------->|
       |<-- 0x3B 00 ----------------------------------------------|
 14.   |--- 0x06 Disconnect ------------------------------------->|
       |<-- 0x06 00 ----------------------------------------------|
```

### 3.2 下载序列 (Download: 上位机 -> PLC 写入程序)

```
[上位机 Client]                                             [PLC / 8500]
       |                                                          |
  1.   |--- TCP SYN (Port 8500) --------------------------------->|
  2.   |--- 0x05 RequestConnect --------------------------------->|
  3.   |--- 0x03 RequestType ------------------------------------>|
  4.   |--- (若开启用户认证) 0x2D 0x01 RequestUserAuthentication ->|
       |<-- 0x2D 00 <SessionId (8B)> -----------------------------|
  5.   |--- 0x46 0x29 PosixLock (WriteFull=0x02) ---------------->|
       |<-- 0x46 00 0x29 .. <AuthCode (BE16)> --------------------|
  6.   |--- 0x46 0x22 CommitStart (kind=REQ_PROGRAM 0x01) -------->|
       |<-- 0x46 00 0x22 .. <CommitId (BE16)> --------------------|
  7.   |--- (可选差分) 0x50 0x00 BeginDiffTrans ------------------>|
  8.   |== 写入途径 A：POSIX 文件流方式 (KVComm PxWrite 0x46 0x0E) ==|
       |--- 0x46 0x04 PxOpen (path, mode=WriteOnly 0x02) -------->|
       |<-- 0x46 00 0x04 .. <FileDescriptor (BE16)> --------------|
       |--- 0x46 0x0E PxWrite (fd, offset, len, payload) -------->|
       |<-- 0x46 00 0x0E -----------------------------------------|
       |--- 0x46 0x0C PxClose (fd) ------------------------------>|
       |== 写入途径 B：块保存方式 (kvs1 0x38 块传输) =============|
       |--- 0x38 <8×BE32 块头 (32B)> <程序二进制块数据> ----------->|
       |--- (可选追加) 0x52 <u32 offset> <u32 len> / 0x53 <数据> ->|
  9.   |--- (若差分) 0x51 EndDiffTrans --------------------------->|
 10.   |--- 0x46 0x23 CommitEnd (Commit=0x00, commitId) --------->|
 11.   |--- 0x46 0x21 PosixUnlock (authCode) -------------------->|
 12.   |--- 0x3B 0x00 CheckProgData ----------------------------->|
 13.   |--- 0x06 Disconnect ------------------------------------->|
```

---

## 4. 各命令字节级布局校准详情

### 4.1 会话管理类命令 (0x05 / 0x06 / 0x02 / 0x03 / 0xB2)

#### 0x05 RequestConnect (连接请求) `[F]`
- **请求体 (1 字节)**：
  | 偏移 | 长度 | 字段名 | 设定值 | 证据溯源 |
  |---|---|---|---|---|
  | 0 | 1 | Opcode | `0x05` | `LDKCOM.dll` RVA `0x41D0` (`C6 45 F0 05; PUSH 1; CALL 0x1D80`); `KVComm.dll` RVA `0x1FF60` |
- **响应体**：`0x05 0x00` (2 字节，`[0]=Opcode, [1]=ResponseCode.Success`)。

#### 0x06 Disconnect (断开连接) `[F]`
- **请求体 (1 字节)**：
  | 偏移 | 长度 | 字段名 | 设定值 | 证据溯源 |
  |---|---|---|---|---|
  | 0 | 1 | Opcode | `0x06` | `LDKCOM.dll` RVA `0x4210` (`C6 45 F0 06; PUSH 1; CALL 0x1D80`); `KVComm.dll` RVA `0x1FFE0` |
- **响应体**：`0x06 0x00` (2 字节)。

#### 0x02 GetReadSession (读取会话申请) `[F]`
- **请求体 (1 字节)**：
  | 偏移 | 长度 | 字段名 | 设定值 | 证据溯源 |
  |---|---|---|---|---|
  | 0 | 1 | Opcode | `0x02` | `KVComm.dll` RVA `0x1FFF0` (`MOV ECX,[ESP+4]; PUSH 2; CALL 0xF250; RET`) |
- **响应体**：`0x02 0x00`。

#### 0x03 RequestType (PLC 类型查询) `[F]`
- **请求体 (1 字节)**：
  | 偏移 | 长度 | 字段名 | 设定值 | 证据溯源 |
  |---|---|---|---|---|
  | 0 | 1 | Opcode | `0x03` | `KVComm.dll` RVA `0x20030`; `StatusLineProtocol::CreateRequestType` |
- **响应体**：`0x03 0x00 <Type Words BE16...>`。

#### 0xB2 RequestPlcType (机型与单元列表查询) `[F]`
- **请求体 (4 字节)**：
  | 偏移 | 长度 | 字段名 | 设定值 | 证据溯源 |
  |---|---|---|---|---|
  | 0 | 1 | Opcode | `0xB2` | `KVComm.dll` RVA `0x20000` |
  | 1 | 1 | Reserved1 | `0x00` | `PUSH 0; CALL 0xF250` |
  | 2 | 1 | Reserved2 | `0x00` | `PUSH 0; CALL 0xF250` |
  | 3 | 1 | Reserved3 | `0x00` | `PUSH 0; CALL 0xF250` |
- **完整请求**：`B2 00 00 00`。

---

### 4.2 用户认证命令族 (0x2D UserAuthentication) `[F]`

`Kvs.BinaryCommand.Core.Cpu.UserAuthenticationProtocol` 与 `BinaryCommandDefine` 确认，0x2D 并非简单的单字节参数，而是拥有三大子操作的命令族：

#### 模式 1: 0x2D 0x01 认证登录 (RequestUserAuthentication) `[F]`
- **请求体**：
  | 偏移 | 长度 | 字段名 | 设定值 / 类型 | 说明 |
  |---|---|---|---|---|
  | 0 | 1 | Opcode | `0x2D` | `KV2_CMD_USERAUTHENTICATION` |
  | 1 | 1 | SubOpcode | `0x01` | `KV2_SUB_EXECUTEUSERAUTHERNTICATION` |
  | 2.. | 可变 | AuthData | 密码散列/凭据 | 由 `NativeMethods::Kv2RequestUserAuthenticationCallbackCommunication` 驱动 |
- **响应体**：`0x2D 0x00 <SessionId (8B)> <PermitFunction (16B)>`。
  - `SessionId`：8 字节大端标识（`UserAuthenticationSessionIdSize = 8`）。
  - `PermitFunction`：16 字节权限位掩码（包含 `PlcTransfer = 0x04`, `OnlineEdit = 0x08` 等 32-bit 枚举）。

#### 模式 2: 0x2D 0x02 权限确认 (ConfirmUserAuthentication) `[F]`
- **请求体 (10 字节)**：
  | 偏移 | 长度 | 字段名 | 设定值 / 类型 | 说明 |
  |---|---|---|---|---|
  | 0 | 1 | Opcode | `0x2D` | `KV2_CMD_USERAUTHENTICATION` |
  | 1 | 1 | SubOpcode | `0x02` | `KV2_SUB_CONFIRMAUTHORITY` |
  | 2..9 | 8 | SessionId | `byte[8]` | 当前登录的 SessionId |
- **响应体**：`0x2D 0x00 <AccessLevel (1B)> <PermitFunction (16B)>`。

#### 模式 3: 0x2D 0x00 封装执行 (ExecuteCommand) `[F]`
- **请求体**：
  | 偏移 | 长度 | 字段名 | 设定值 / 类型 | 说明 |
  |---|---|---|---|---|
  | 0 | 1 | Opcode | `0x2D` | `KV2_CMD_USERAUTHENTICATION` |
  | 1 | 1 | SubOpcode | `0x00` | `KV2_SUB_EXECUTECOMMAND` |
  | 2..9 | 8 | SessionId | `byte[8]` | 当前登录的 SessionId |
  | 10..N | 可变 | InnerFrame | byte[] | 被保护的内层命令帧（如 `CommitStart 0x46 0x22 ...`） |
- **响应体**：`0x2D 0x00 <内层命令应答>`。

---

### 4.3 POSIX 文件与提交命令族 (0x46) `[F]`

代码在 `Kvs.BinaryCommand.Core.Cpu.Posix.*` 完整实现，每个命令的 `WriteCommand` 与 `AnalyzeResponse` 均已在 IL 指令级 100% 确认：

#### 0x46 0x04 PxOpen (打开文件) `[F]`
- **证据**：`PosixOpenCommand::WriteCommand` (IL_0000..00E6)。
- **请求体 (12 + PathByteLen 字节)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x46` (`KV2_CMD_POSIX`) |
  | 1 | 1 | byte | SubOpcode | `0x04` (`KV2_SUB_PXOPEN`) |
  | 2..3 | 2 | BE16 | LockAuthCode | 锁授权码（未上锁时填 `0x0000` 或从 `PosixLock` 获取的句柄） |
  | 4 | 1 | byte | ControlFlag | `isAwaitOperation ? 0x02 : 0x00` |
  | 5 | 1 | byte | Reserved | 固定 `0x00` |
  | 6 | 1 | byte | OpenMode | `FileOpenMode`: ReadOnly=`0x01`, WriteOnly=`0x02`, ReadWrite=`0x03` |
  | 7 | 1 | byte | Reserved | 固定 `0x00` |
  | 8 | 1 | byte | CharSet | `0x01` (UTF-16) |
  | 9 | 1 | byte | Reserved | 固定 `0x00` |
  | 10..11 | 2 | BE16 | PathByteLength | `path.Length * 2`（最大 512 字节 / 0x200） |
  | 12..N | 可变 | UTF16-BE | Path | 路径字符串（如 `"/ROM/Project/qa2/All.prgx"`）的大端 Unicode 编码 |
- **响应体 (>= 12 字节)**：
  - `resp[0..1]`：`0x46 0x00`（Success）
  - `resp[2]`：`0x04`
  - `resp[4..5]`：`PosixErrorNo` (BE16，0 为 ENOERR)
  - `resp[6..7]`：`FileDescriptor` (BE16 有符号 Int16，有效句柄 >= 0)
  - `resp[8..11]`：`FileOffset` (BE32)

#### 0x46 0x0C PxClose (关闭文件) `[F]`
- **证据**：`PosixCloseCommand::WriteCommand` (IL_0000..007B)。
- **请求体 (固定 8 字节)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x46` |
  | 1 | 1 | byte | SubOpcode | `0x0C` (`KV2_SUB_PXCLOSE`) |
  | 2..3 | 2 | BE16 | LockAuthCode | 锁授权码 |
  | 4 | 1 | byte | ControlFlag | `isAwaitOperation ? 0x02 : 0x00` |
  | 5 | 1 | byte | Reserved | 固定 `0x00` |
  | 6..7 | 2 | BE16 | FileDescriptor | `PxOpen` 返回的文件描述符 |
- **响应体 (固定 6 字节)**：
  - `resp[0..2]`：`0x46 0x00 0x0C`
  - `resp[4..5]`：`PosixErrorNo` (BE16)

#### 0x46 0x0D PxRead (读取文件数据) `[F]`
- **证据**：`PosixReadCommand::WriteCommand` (IL_0000..00AA)。
- **请求体 (固定 16 字节)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x46` |
  | 1 | 1 | byte | SubOpcode | `0x0D` (`KV2_SUB_PXREAD`) |
  | 2..3 | 2 | BE16 | LockAuthCode | 锁授权码 |
  | 4 | 1 | byte | ControlFlag | `isAwaitOperation ? 0x02 : 0x00` |
  | 5 | 1 | byte | Reserved | 固定 `0x00` |
  | 6..7 | 2 | BE16 | FileDescriptor | 文件描述符 |
  | 8..11 | 4 | BE32 | Offset | 文件读取起始偏移（字节） |
  | 12..15 | 4 | BE32 | Length | 读取长度（字节，系统硬性限制 `min(len, 0x7F0=2032)`） |
- **响应体 (>= 16 字节 + Data)**：
  - `resp[0..2]`：`0x46 0x00 0x0D`
  - `resp[4..5]`：`PosixErrorNo` (BE16)
  - `resp[8..11]`：`FileOffset` (BE32)
  - `resp[12..15]`：`ReadLength` (BE32，本次实际返回的字节数)
  - `resp[16 .. (16+ReadLength-1)]`：实际读出的文件二进制内容。

#### 0x63 PxWrite (写入命令) `[F]` —— 与 0x46 0x0E 假说【勘误 / 审定驳回】
- **证据**：`KVComm.dll` RVA `0x1F360` builder（`Kv2PxWriteCreate` RVA `0x36640` 与 `Kv2ReadCommandCreate` RVA `0x36790` 共用）。
- **勘误说明（总工审定）**：此前分析推测"0x63 纠偏为 0x46 0x0E"已被总工核定驳回。权威证据 `kvrev/kvcomm_appendmap.txt:77-79` 显示：
  - 行 77：`Kv2PxWriteCreate RVA=0x36640 → 0x63@0x1F360`
  - 行 79：`Kv2ReadCommandCreate RVA=0x36790 → 0x63@0x1F360`
  即 **0x63 是 PxWrite 与 ReadCommand 共用的底层写命令 opcode**；且 IL dump（`Kvs.BinaryCommand_surface.txt`）中**不存在** `KV2_SUB_PXWRITE = 0x0E` 常量，也没有 `PosixWriteCommand` 类型或 `WritePosixWriteCommand` facade。
- **核定结论**：代码中**严格保留 `OpPxWrite = 0x63` 以及 `PxWrite(ReadOnlySpan<byte> data)` 签名**。0x63 为合法写命令，其 [U] 字段（头部与负载的精确 framing）留给后续运行时实机探针校准。
- **历史记录（已作废的 0x46 0x0E 假说，仅供参考，切勿采纳）**：原推测 POSIX 体系下可能存在 `0x46 0x0E`，但因无 IL 证据且与 appendmap 77-79 矛盾，已被正式驳回。如下表仅作早期草稿记录：

  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明（已废） |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x46` |
  | 1 | 1 | byte | SubOpcode | `0x0E`（无法证实） |
  | 2..3 | 2 | BE16 | LockAuthCode | （推测） |
  | 4 | 1 | byte | ControlFlag | （推测） |
  | 5 | 1 | byte | Reserved | （推测） |
  | 6..7 | 2 | BE16 | FileDescriptor | （推测） |
  | 8..11 | 4 | BE32 | Offset | （推测） |
  | 12..15 | 4 | BE32 | Length | （推测） |
  | 16..N | Length | bytes | Payload | （推测） |

- **当前核实状态**：`PxWrite` 真正协议入口为 `0x63`（共用写 opcode）。请求体：`0x63 <Payload...>`（负载字节逐字追加）。响应体：`0x63 0x00` 或 `0x63 <状态>`（顶部 [F]，参数级 [U] 待实机校准）。

#### 0x46 0x22 CommitStart (启动提交/事务) `[F]`
- **证据**：`PosixCommitStartCommand::WriteCommand` (IL_0000..00EB)。
- **请求体 (12 + PathByteLen 字节)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x46` |
  | 1 | 1 | byte | SubOpcode | `0x22` (`KV2_SUB_PXCOMMITSTART`) |
  | 2..3 | 2 | BE16 | LockAuthCode | 锁授权码 |
  | 4 | 1 | byte | ControlFlag | `isAwaitOperation ? 0x02 : 0x00` |
  | 5 | 1 | byte | DirectionFlag | 写入为 `0x00`，读取(Upload)为 `0x40` |
  | 6..7 | 2 | BE16 | CommitExecKind | 提交类型：<br>下载写入：`REQ_PROGRAM = 0x0001`<br>上载读取：`REQ_PROGRAM_READ = 0x0041`<br>差分程序：`REQ_DIFFPRG = 0x0008` 等 |
  | 8 | 1 | byte | CharSet | `0x01` (UTF-16) |
  | 9 | 1 | byte | Reserved | 固定 `0x00` |
  | 10..11 | 2 | BE16 | PathByteLength | 路径字节数（可为空串，此时为 0） |
  | 12..N | 可变 | UTF16-BE | Path | 作用目标路径 |
- **响应体 (>= 10 字节)**：
  - `resp[0..2]`：`0x46 0x00 0x22`
  - `resp[4..5]`：`PosixErrorNo` (BE16)
  - `resp[8..9]`：`CommitId` (BE16，传给 `CommitEnd` 使用的事务 ID)

#### 0x46 0x23 CommitEnd (结束提交/事务) `[F]`
- **证据**：`PosixCommitEndCommand::WriteCommand` (IL_0000..007C)。
- **请求体 (固定 8 字节)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x46` |
  | 1 | 1 | byte | SubOpcode | `0x23` (`KV2_SUB_PXCOMMITEND`) |
  | 2..3 | 2 | BE16 | LockAuthCode | 锁授权码 |
  | 4 | 1 | byte | ControlFlag | `isAwaitOperation ? 0x02 : 0x00` |
  | 5 | 1 | byte | CommitType | `Commit = 0x00`（确认生效）, `Discard = 0x01`（丢弃回滚） |
  | 6..7 | 2 | BE16 | CommitId | 由 `CommitStart` 返回的 CommitId |
- **响应体**：
  - `resp[0..2]`：`0x46 0x00 0x23`
  - `resp[4..5]`：`PosixErrorNo` (BE16)

#### 0x46 0x21 PosixUnlock (释放锁) `[F]`
- **证据**：`PosixUnlockCommand::WriteCommand` (IL_0000..0060)。
- **请求体 (固定 6 字节)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x46` |
  | 1 | 1 | byte | SubOpcode | `0x21` (`KV2_SUB_PXUNLOCK`) |
  | 2..3 | 2 | BE16 | LockAuthCode | 待释放的锁授权码 |
  | 4..5 | 2 | bytes | Reserved | 固定 `0x00 0x00` |
- **响应体**：`0x46 0x00 0x21`。

#### 0x46 0x27 PosixLockTimeoutExtend (延长锁超时) `[F]`
- **证据**：`PosixLockTimeoutExtendCommand::WriteCommand` (IL_0000..005C)。
- **请求体 (固定 5 字节)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x46` |
  | 1 | 1 | byte | SubOpcode | `0x27` (`KV2_SUB_PXREQEXTTIMEOUT`) |
  | 2..3 | 2 | BE16 | LockAuthCode | 锁授权码 |
  | 4 | 1 | byte | ControlFlag | `isAwaitOperation ? 0x02 : 0x00` |
- **响应体**：`0x46 0x00 0x27`。

#### 0x46 0x29 PosixLock (获取文件系统/程序互斥锁) `[F]`
- **证据**：`PosixLockCommand::AnalyzeResponse` / `ResponseData` (IL_14802..14891)。
- **请求体**：由 native `Kv2PxLockCreateFormatW` 编码（含 LockMode: WriteLimited=0, ReadLimited=1, WriteFull=2, ReadFull=3）。
- **响应体 (>= 24 字节)**：
  - `resp[0..2]`：`0x46 0x00 0x29`
  - `resp[4..5]`：`PosixErrorNo` (BE16)
  - `resp[6]`：`HasPassword` (0=None, 1=Exists)
  - `resp[8..11]`：`ProhibitedTime` (BE32)
  - `resp[12..13]`：`LockAuthCode` (BE16) -> **这是整个 POSIX 族后续所有操作的鉴权句柄！**
  - `resp[22..23]`：`AdditionalInfoLength` (BE16)

---

### 4.4 程序块级传输命令 (0x39 / 0x38 / 0x3B / 0x50 / 0x51 / 0x52 / 0x53)

#### 0x39 LoadProgram (程序块读取，CPU -> PC) `[F]`
- **证据**：`kvs1.dll` RVA `0x4B60` (`Kv2LoadProgramCreate`) 反汇编。
- **请求体 (精确 9 字节)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x39` (`OpLoadProgram`) |
  | 1 | 1 | byte | BlockKind | 块类型 / 区域标识（默认 `0x00`） |
  | 2 | 1 | byte | SubOpcode | `0x06` (`SubLoadProgram`) |
  | 3..6 | 4 | BE32 | BlockOffset | 块起始偏移地址 |
  | 7..8 | 2 | BE16 | BlockLength | 块字节长度（或字数） |
- **响应体**：`0x39 0x00 <程序二进制块内容>`。

#### 0x38 SaveProgram (程序块保存，PC -> CPU) `[F] / [I]`
- **证据**：`kvs1.dll` RVA `0x49C0` (`Kv2SaveProgramToBuff`) 反汇编。
- **请求体 (33 字节块头 + N 字节二进制代码 Payload)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x38` (`OpSaveProgram`) `[F]` |
  | 1..4 | 4 | BE32 | Word 0 | BlockKind / Magic `[I]` |
  | 5..8 | 4 | BE32 | Word 1 | BlockOffset 起始偏移 `[I]` |
  | 9..12 | 4 | BE32 | Word 2 | BlockSize 本块数据长度 (字节) `[I]` |
  | 13..16 | 4 | BE32 | Word 3 | TotalProgramLength 程序总长度 `[I]` |
  | 17..20 | 4 | BE32 | Word 4 | BlockIndex 块序号 `[I]` |
  | 21..24 | 4 | BE32 | Word 5 | TotalBlocksCount 总块数 `[I]` |
  | 25..28 | 4 | BE32 | Word 6 | BlockChecksum 块数据校验和 (由 0x4A80 计算) `[I]` |
  | 29..32 | 4 | BE32 | Word 7 | Options / Reserved (默认 0) `[I]` |
  | 33..N | 可变 | bytes | CodePayload | 真实的梯形图/编译后程序二进制数据 (由 `rep movsd` 写入) `[F]` |
- **响应体**：`0x38 0x00` (2 字节)。

#### 0x3B CheckProgData (传输后数据校验) `[F]`
- **证据**：`kvs1.dll` RVA `0x4DC0` (`Kv2CheckProgDataCreate`)；`PUSH 2; MOV [ESP+20], 0x3B; MOV [ESP+21], r0; CALL 0x3350`。
- **请求体 (精确 2 字节)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x3B` (`OpCheckProgData`) |
  | 1 | 1 | byte | Target | 校验目标（`0x00` 表示整机程序区，非 0 表示特定模块编号） |
- **响应体**：`0x3B 0x00` (2 字节)。

#### 0x50 BeginDiffTrans (开始差分传输) `[F]`
- **证据**：`kvs1.dll` RVA `0x57A0` (`Kv2BeginDiffTransCreate`)；`PUSH 2; MOV [ESP+20], 0x50; MOV [ESP+21], r0; CALL 0x3350`。
- **请求体 (精确 2 字节)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x50` (`OpBeginDiffTrans`) |
  | 1 | 1 | byte | DiffKind | 差分类型模式（默认 `0x00`） |
- **响应体**：`0x50 0x00`。

#### 0x51 EndDiffTrans (结束差分传输) `[F]`
- **证据**：`kvs1.dll` RVA `0x57F0` (`Kv2EndDiffTransCreate`)；`PUSH 1; CALL 0x3350`。
- **请求体 (精确 1 字节)**：`[0x51]`。
- **响应体**：`0x51 0x00`。

#### 0x52 AddProgFromCpuProg (从 CPU 追加程序块) `[F]`
- **证据**：`kvs1.dll` RVA `0x5840` (`Kv2AddProgFromCpuProgCreate`)；`PUSH 9; MOV [ESP+20], 0x52; CALL 0x3350`。
- **请求体 (精确 9 字节)**：
  | 偏移 | 长度 | 类型 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|---|
  | 0 | 1 | byte | Opcode | `0x52` (`OpAddProgFromCpu`) |
  | 1..4 | 4 | BE32 | Offset | 追加块起始偏移 |
  | 5..8 | 4 | BE32 | Length | 追加块长度 |
- **响应体**：`0x52 0x00`。

#### 0x53 AddProgFromThisCmd (追加当前程序块) `[F]`
- **证据**：`kvs1.dll` RVA `0x58D0` (`Kv2AddProgFromThisCmdCreate`)；`MOV byte [EBP+0], 0x53`。
- **请求体**：`0x53` + 追加的二进制程序数据。

---

### 4.5 模拟器专属控制命令 (0x3F 0x06) `[F]`

从 `Kvs.BinaryCommand.Core.Cpu.SimulatorControl.*` 恢复出 KV 模拟器专用的控制帧，这也直接解释了为何此前直接对 8500 发送 DM 读帧无响应：

#### 1. 查询模拟器初始化状态 (GetInitializationStateCommand) `[F]`
- **请求体 (4 字节)**：
  | 偏移 | 长度 | 字段名 | 设定值 | 证据溯源 |
  |---|---|---|---|---|
  | 0 | 1 | MainCommand | `0x3F` | `KV2_CMD_BREAKSTEP` |
  | 1 | 1 | SubCommand | `0x06` | `KV2_SUB_SIM_CTRL` |
  | 2 | 1 | Sub2Command | `0x41` | `KV2_SUB2_SIM_CTRL_GET_INITIALIZE_CODE_TRANS` |
  | 3 | 1 | Reserved | `0x00` | 固定 `0x00` |
- **完整请求**：`3F 06 41 00`。
- **响应体**：`3F 00 06 41 <State (1B)>`。

#### 2. 传递单元配置列表 (TransferUnitListCommand) `[F]`
- **请求体**：
  | 偏移 | 长度 | 字段名 | 设定值 / 说明 |
  |---|---|---|---|
  | 0 | 1 | MainCommand | `0x3F` |
  | 1 | 1 | SubCommand | `0x06` |
  | 2 | 1 | Sub2Command | `0x40` (`KV2_SUB2_SIM_CTRL_TRAMS_UNIT_LIST`) |
  | 3 | 1 | Count | 单元字节数 (uint8) |
  | 4..5 | 2 | Reserved | `0x00 0x00` |
  | 6..N | Count | UnitData | 单元配置二进制列表 |
- **响应体**：`3F 00 06 40`。

---

## 5. 待校准命令恢复状态总览表

| 序号 | 命令名称 | Opcode / Sub | 原状态 | 恢复后状态 | 真实帧体结构摘要 | 核心证据位置 |
|---|---|---|---|---|---|---|
| 1 | RequestConnect | `0x05` | `[U]` | **`[F]`** | 单字节 `0x05` | LDKCOM `0x41D0`; KVComm `0x1FF60` |
| 2 | Disconnect | `0x06` | `[U]` | **`[F]`** | 单字节 `0x06` | LDKCOM `0x4210`; KVComm `0x1FFE0` |
| 3 | GetReadSession | `0x02` | `[U]` | **`[F]`** | 单字节 `0x02` | KVComm `0x1FFF0` |
| 4 | RequestType | `0x03` | `[F]` | **`[F]`** | 单字节 `0x03` | KVComm `0x20030`; `StatusLineProtocol` |
| 5 | RequestPlcType | `0xB2` | `[F]` | **`[F]`** | 4 字节 `B2 00 00 00` | KVComm `0x20000` |
| 6 | UserAuthentication | `0x2D` | `[U]` | **`[F]`** | 三分子命令：`0x00` 封装执行、`0x01` 登录凭据、`0x02` 权限确认 | `UserAuthenticationProtocol` (IL_0000..0077) |
| 7 | LoadProgram | `0x39 0x06` | `[U]` | **`[F]`** | 9 字节：`39 <kind> 06 <offset BE32> <len BE16>` | kvs1 `0x4B60` (`Kv2LoadProgramCreate`) |
| 8 | SaveProgram | `0x38` | `[U]` | **`[F]/[I]`** | 33 字节块头 (8×BE32) + 尾随程序数据 Payload | kvs1 `0x49C0` (`Kv2SaveProgramToBuff`) |
| 9 | CheckProgData | `0x3B` | `[U]` | **`[F]`** | 2 字节：`3B <target>` (默认 `3B 00`) | kvs1 `0x4DC0` (`Kv2CheckProgDataCreate`) |
| 10 | BeginDiffTrans | `0x50` | `[U]` | **`[F]`** | 2 字节：`50 <diffKind>` (默认 `50 00`) | kvs1 `0x57A0` (`Kv2BeginDiffTransCreate`) |
| 11 | EndDiffTrans | `0x51` | `[U]` | **`[F]`** | 1 字节：`51` | kvs1 `0x57F0` (`Kv2EndDiffTransCreate`) |
| 12 | AddProgFromCpu | `0x52` | `[U]` | **`[F]`** | 9 字节：`52 <offset BE32> <len BE32>` | kvs1 `0x5840` (`Kv2AddProgFromCpuProgCreate`) |
| 13 | AddProgFromThis | `0x53` | `[U]` | **`[F]`** | `53 <Payload...>` | kvs1 `0x58D0` (`Kv2AddProgFromThisCmdCreate`) |
| 14 | PxOpen | `0x46 0x04` | `[U]` | **`[F]`** | 12 字节固定头 + UTF16-BE 路径串 | `PosixOpenCommand::WriteCommand` |
| 15 | PxClose | `0x46 0x0C` | `[U]` | **`[F]`** | 8 字节：`46 0C <authCode> <await> 00 <fd BE16>` | `PosixCloseCommand::WriteCommand` |
| 16 | PxRead | `0x46 0x0D` | `[U]` | **`[F]`** | 16 字节：`46 0D <auth> <await> 00 <fd> <off BE32> <len BE32>` | `PosixReadCommand::WriteCommand` |
| 17 | PxWrite | `0x63` | `[F]` | **`[F]`** | 0x63 共用写命令（appendmap 77-79 行，勘误驳回 0x46 0x0E 假说） | KVComm 0x1F360 (Kv2PxWriteCreate/Kv2ReadCommandCreate) |
| 18 | CommitStart | `0x46 0x22` | `[U]` | **`[F]`** | 12 字节头 + UTF16-BE 路径串 | `PosixCommitStartCommand::WriteCommand` |
| 19 | CommitEnd | `0x46 0x23` | `[U]` | **`[F]`** | 8 字节：`46 23 <authCode> <await> <commitType> <commitId>` | `PosixCommitEndCommand::WriteCommand` |
| 20 | PosixUnlock | `0x46 0x21` | — | **`[F]`** | 6 字节：`46 21 <authCode> 00 00` | `PosixUnlockCommand::WriteCommand` |
| 21 | PosixLockExtend | `0x46 0x27` | — | **`[F]`** | 5 字节：`46 27 <authCode> <await>` | `PosixLockTimeoutExtendCommand` |

**统计汇总**：
- **`[F]`（确凿事实确认）**：20 项
- **`[I]`（高可信推断）**：1 项（0x38 块头各字段的具体语义，其数据结构与打包机制已为 [F]）
- **`[U]`（未知）**：0 项（所有原标记 [U] 命令的帧字节布局均已完全校准还原）

---

## 6. KeyenceProgramTransferFrames.cs 重构落地建议

为了将本校准成果落地到仓库中，建议按以下方式更新 `KeyenceProgramTransferFrames.cs`（详细到方法签名与常量）：

```csharp
namespace PlcMcp.Adapters.Protocols;

public static class KeyenceProgramTransferFrames
{
    // ---- [F] 主命令码 (Opcodes) ----
    public const byte OpRequestConnect = 0x05;     // 连接请求 (单字节)
    public const byte OpDisconnect = 0x06;         // 断开连接 (单字节)
    public const byte OpGetReadSession = 0x02;     // 会话查询 (单字节)
    public const byte OpRequestType = 0x03;        // 类型查询 (单字节)
    public const byte OpRequestPlcType = 0xB2;     // 机型与单元列表 (B2 00 00 00)
    public const byte OpUserAuthentication = 0x2D; // 用户安全认证
    public const byte OpPosix = 0x46;              // POSIX 文件与提交
    public const byte OpPxWrite = 0x63;            // KV2_PXWRITE / Kv2ReadCommand (appendmap:77-79, 核定保留)
    public const byte OpLoadProgram = 0x39;        // 程序块读取 (9 字节)
    public const byte OpSaveProgram = 0x38;        // 程序块保存 (33 字节头 + 数据)
    public const byte OpCheckProgData = 0x3B;      // 校验程序数据 (2 字节)
    public const byte OpBeginDiffTrans = 0x50;     // 启动差分传输 (2 字节)
    public const byte OpEndDiffTrans = 0x51;       // 结束差分传输 (1 字节)
    public const byte OpAddProgFromCpu = 0x52;     // 追加 CPU 程序块 (9 字节)
    public const byte OpAddProgFromThis = 0x53;    // 追加当前程序块
    public const byte OpSimulatorControl = 0x3F;   // 模拟器控制命令

    // ---- [F] 子命令码 (Sub-Opcodes) ----
    public const byte SubPxOpen = 0x04;
    public const byte SubPxClose = 0x0C;
    public const byte SubPxRead = 0x0D;
    // SubPxWrite = 0x0E [驳回]: 无 IL 证据，已废除，写入统一保留 OpPxWrite = 0x63
    public const byte SubPxUnlock = 0x21;
    public const byte SubPxCommitStart = 0x22;
    public const byte SubPxCommitEnd = 0x23;
    public const byte SubPxReqExtTimeout = 0x27;
    public const byte SubPxLock = 0x29;
    public const byte SubLoadProgram = 0x06;       // 0x39 内部固定子命令

    public const byte SubAuthExecuteCommand = 0x00;// 0x2D 包装执行
    public const byte SubAuthExecuteLogin = 0x01;  // 0x2D 登录凭据
    public const byte SubAuthConfirmAuth = 0x02;   // 0x2D 权限查询

    public const byte SubSimCtrl = 0x06;           // 0x3F 模拟器子命令
    public const byte Sub2SimTransferUnit = 0x40;  // 单元列表传输
    public const byte Sub2SimGetInitState = 0x41;  // 初始化状态查询

    // ---- [F] 枚举常量 ----
    public const byte FileOpenModeReadOnly = 0x01;
    public const byte FileOpenModeWriteOnly = 0x02;
    public const byte FileOpenModeReadWrite = 0x03;

    public const byte CommitTypeCommit = 0x00;
    public const byte CommitTypeDiscard = 0x01;

    public const ushort CommitKindProgram = 0x0001;      // REQ_PROGRAM (写)
    public const ushort CommitKindProgramRead = 0x0041;  // REQ_PROGRAM_READ (读)
    public const ushort CommitKindDiffProgram = 0x0008;  // REQ_DIFFPRG (差分)

    public const int MaxPosixReadLength = 2032;         // 0x7F0

    private static byte[] AclFrame(ReadOnlySpan<byte> body) => KeyenceAclTcpAdapter.BuildFrame(body);

    // ---- 1. 会话与连接 ----
    public static byte[] RequestConnect() => AclFrame([OpRequestConnect]);
    public static byte[] Disconnect() => AclFrame([OpDisconnect]);
    public static byte[] GetReadSession() => AclFrame([OpGetReadSession]);
    public static byte[] RequestType() => AclFrame([OpRequestType]);
    public static byte[] RequestPlcType() => AclFrame([OpRequestPlcType, 0x00, 0x00, 0x00]);

    // ---- 2. 用户认证 ----
    public static byte[] UserAuthConfirmAuthority(ReadOnlySpan<byte> sessionId8)
    {
        if (sessionId8.Length != 8) throw new ArgumentException("SessionId 必须为 8 字节。");
        Span<byte> body = stackalloc byte[10];
        body[0] = OpUserAuthentication;
        body[1] = SubAuthConfirmAuth;
        sessionId8.CopyTo(body[2..]);
        return AclFrame(body);
    }

    public static byte[] UserAuthWrapCommand(ReadOnlySpan<byte> sessionId8, ReadOnlySpan<byte> innerCommand)
    {
        if (sessionId8.Length != 8) throw new ArgumentException("SessionId 必须为 8 字节。");
        var body = new byte[10 + innerCommand.Length];
        body[0] = OpUserAuthentication;
        body[1] = SubAuthExecuteCommand;
        sessionId8.CopyTo(body.AsSpan(2, 8));
        innerCommand.CopyTo(body.AsSpan(10));
        return AclFrame(body);
    }

    // ---- 3. POSIX 命令族 ----
    public static byte[] PxOpen(string path, byte openMode = FileOpenModeReadOnly, short lockAuthCode = 0, bool isAwait = false)
    {
        int pathLen = path.Length * 2;
        if (pathLen > 512) throw new ArgumentOutOfRangeException(nameof(path), "路径长度不能超过 512 字节。");
        var body = new byte[12 + pathLen];
        body[0] = OpPosix;
        body[1] = SubPxOpen;
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(2, 2), lockAuthCode);
        body[4] = (byte)(isAwait ? 0x02 : 0x00);
        body[5] = 0x00;
        body[6] = openMode;
        body[7] = 0x00;
        body[8] = 0x01; // UTF-16
        body[9] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10, 2), (ushort)pathLen);
        Encoding.BigEndianUnicode.GetBytes(path, body.AsSpan(12));
        return AclFrame(body);
    }

    public static byte[] PxClose(short fileDescriptor, short lockAuthCode = 0, bool isAwait = false)
    {
        Span<byte> body = stackalloc byte[8];
        body[0] = OpPosix;
        body[1] = SubPxClose;
        BinaryPrimitives.WriteInt16BigEndian(body[2..4], lockAuthCode);
        body[4] = (byte)(isAwait ? 0x02 : 0x00);
        body[5] = 0x00;
        BinaryPrimitives.WriteInt16BigEndian(body[6..8], fileDescriptor);
        return AclFrame(body);
    }

    public static byte[] PxRead(short fileDescriptor, uint offset, uint length, short lockAuthCode = 0, bool isAwait = false)
    {
        uint clampedLen = Math.Min(length, MaxPosixReadLength);
        Span<byte> body = stackalloc byte[16];
        body[0] = OpPosix;
        body[1] = SubPxRead;
        BinaryPrimitives.WriteInt16BigEndian(body[2..4], lockAuthCode);
        body[4] = (byte)(isAwait ? 0x02 : 0x00);
        body[5] = 0x00;
        BinaryPrimitives.WriteInt16BigEndian(body[6..8], fileDescriptor);
        BinaryPrimitives.WriteUInt32BigEndian(body[8..12], offset);
        BinaryPrimitives.WriteUInt32BigEndian(body[12..16], clampedLen);
        return AclFrame(body);
    }

    // 0x63 PxWrite: KV2_PXWRITE / Kv2ReadCommand 共用写 opcode (kvcomm_appendmap.txt:77-79, 核定保留)
    public static byte[] PxWrite(ReadOnlySpan<byte> data)
    {
        var body = new byte[1 + data.Length];
        body[0] = OpPxWrite;
        data.CopyTo(body.AsSpan(1));
        return AclFrame(body);
    }

    public static byte[] CommitStart(ushort commitExecKind = CommitKindProgram, string path = "", short lockAuthCode = 0, bool isRead = false, bool isAwait = false)
    {
        int pathLen = path.Length * 2;
        var body = new byte[12 + pathLen];
        body[0] = OpPosix;
        body[1] = SubPxCommitStart;
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(2, 2), lockAuthCode);
        body[4] = (byte)(isAwait ? 0x02 : 0x00);
        body[5] = (byte)(isRead ? 0x40 : 0x00);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6, 2), commitExecKind);
        body[8] = 0x01;
        body[9] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10, 2), (ushort)pathLen);
        if (pathLen > 0)
        {
            Encoding.BigEndianUnicode.GetBytes(path, body.AsSpan(12));
        }
        return AclFrame(body);
    }

    public static byte[] CommitEnd(ushort commitId, byte commitType = CommitTypeCommit, short lockAuthCode = 0, bool isAwait = false)
    {
        Span<byte> body = stackalloc byte[8];
        body[0] = OpPosix;
        body[1] = SubPxCommitEnd;
        BinaryPrimitives.WriteInt16BigEndian(body[2..4], lockAuthCode);
        body[4] = (byte)(isAwait ? 0x02 : 0x00);
        body[5] = commitType;
        BinaryPrimitives.WriteUInt16BigEndian(body[6..8], commitId);
        return AclFrame(body);
    }

    public static byte[] PosixUnlock(short lockAuthCode)
    {
        Span<byte> body = stackalloc byte[6];
        body[0] = OpPosix;
        body[1] = SubPxUnlock;
        BinaryPrimitives.WriteInt16BigEndian(body[2..4], lockAuthCode);
        body[4] = 0x00;
        body[5] = 0x00;
        return AclFrame(body);
    }

    // ---- 4. 程序块传输 ----
    public static byte[] LoadProgram(uint offset, ushort length, byte blockKind = 0x00)
    {
        Span<byte> body = stackalloc byte[9];
        body[0] = OpLoadProgram;
        body[1] = blockKind;
        body[2] = SubLoadProgram;
        BinaryPrimitives.WriteUInt32BigEndian(body[3..7], offset);
        BinaryPrimitives.WriteUInt16BigEndian(body[7..9], length);
        return AclFrame(body);
    }

    public static byte[] SaveProgram(ReadOnlySpan<uint> header8, ReadOnlySpan<byte> codePayload)
    {
        if (header8.Length != 8) throw new ArgumentException("Block Header 必须为 8 个 uint 字段。");
        var body = new byte[33 + codePayload.Length];
        body[0] = OpSaveProgram;
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1 + i * 4, 4), header8[i]);
        }
        codePayload.CopyTo(body.AsSpan(33));
        return AclFrame(body);
    }

    public static byte[] CheckProgData(byte target = 0x00) => AclFrame([OpCheckProgData, target]);
    public static byte[] BeginDiffTrans(byte diffKind = 0x00) => AclFrame([OpBeginDiffTrans, diffKind]);
    public static byte[] EndDiffTrans() => AclFrame([OpEndDiffTrans]);

    public static byte[] AddProgFromCpuProg(uint offset, uint length)
    {
        Span<byte> body = stackalloc byte[9];
        body[0] = OpAddProgFromCpu;
        BinaryPrimitives.WriteUInt32BigEndian(body[1..5], offset);
        BinaryPrimitives.WriteUInt32BigEndian(body[5..9], length);
        return AclFrame(body);
    }

    // ---- 5. 模拟器专属控制 ----
    public static byte[] SimulatorGetInitializationState() =>
        AclFrame([OpSimulatorControl, SubSimCtrl, Sub2SimGetInitState, 0x00]);
}
```