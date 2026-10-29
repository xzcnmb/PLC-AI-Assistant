# 汇川 AutoShop H5U 模拟器 12939 下载协议逆向文档（D1）

> 本文档固化 h5u_daemon 模拟器 TCP 127.0.0.1:12939 原生协议（"编译产物→模拟器装载"必经之路）的
> 全部逆向结论与实证记录。
>
> 证据来源：(1) 模拟器二进制的静态逆向（capstone/dumpbin，工作副本，未修改安装目录）；
> (2) 本地自启 h5u_daemon.exe 实例上的 socket 实测（全程 127.0.0.1 回环，零物理风险，
> 实验 daemon 为 `C:\Users\18735\AppData\Local\Temp\plcmcp-handoff\d1-download-workspace\Simulator\`
> 的工作副本，安装目录零写入）。
>
> 标注约定：【实证】= 本次亲自执行并复核（反汇编 + 实机收发）；【推断】= 基于实证的推理；
> 【未验证】= 无直接证据、禁止在实现中当作事实。能力声明纪律沿用 `docs/contracts.md`。
>
> **协议为逆向所得，无汇川厂商背书；版本锁定 AutoShop 4.12.0.2 模拟器二进制
> （mp_core.dll / public.dll / monitor.dll，2026-07-21 构建，MinGW GCC 4.8.0 x86）。**
> 注意：本协议是 h5u_daemon 的原生二进制协议，与 InoVarSharedService 的 ITP JSON 协议
> （`docs/inovance-itp-protocol.md`，端口 13939）**完全无关，不要混淆**。

对应实现：

| 层 | 位置 |
|---|---|
| 帧模型（帧构造/解析/命令码/状态码） | `src/PlcMcp.Engineering/Workers/AutoShop/DownloadProtocol/DownloadFrame.cs` |
| 客户端（硬回环校验/登录/下载/上传） | `src/PlcMcp.Engineering/Workers/AutoShop/DownloadProtocol/DownloadProtocolClient.cs` |
| mock 单测（帧回放/坏帧/超时/拒绝）+ 实机 env-gated 测试 | `tests/PlcMcp.Engineering.Tests/AutoShopDownloadProtocolTests.cs`（21 个） |

---

## 一、结论摘要（先读这一节）

| 项 | 结论 |
|---|---|
| 12939 监听者 | `monitor.dll` 线程 `monitor`：`mp_init(0)` → `monitor_modbus_init()`（向 modbus.dll 注册回调）→ **`monitor_tcp_server(0x328B=12939, backlog=16)`**（**bind 127.0.0.1**，实证 `mov [esp],7F000001h`）→ 每连接线程 `mp_connect(socket, 0x81)` + recv 循环 + **`mp_input(socket, buf, &len)`**【实证】 |
| 帧格式 | **8 字节定长头 + 载荷**；`[0..2)=总长(LE16, 含头)`、`[2]=版本(≤2 才进 handler)`、`[3]=服务器链 id`、`[4..8)=保留(实测 0, 回显)`；载荷从偏移 8 起。应答写回同一缓冲：`[0..2)=载荷长度+8`【实证（mp_input 反汇编 + 全部实机收发）】 |
| 服务器链 id | 1=寄存器/内存读写（命令字 0xFF000010..0xFF001002）、3=元素块读写（parse_cmd_para）、**4=文件传输（下载/上传/完成）**、0x80=密码登录/登出、0x81=登录态查询/登出；未注册 id → 8 字节探测应答 `[len(旧词), 2, 0xFF, 0]`【实证】 |
| 握手/探测 | 任何版本字节 >2 的帧 → 规范化应答 8 字节 `[旧长度词不更新, 2, 0xFF, 0]`（rc=0x21）；**长度词是陈旧的，接收方必须按实际字节读**【实证（E1/E3 实验）】 |
| 登录 | **先登录才能下载受保护文件**（.hcpp/.foid）：0x80 链 sub-type 1 = `[1][0][user 32B][password 32B]` → `check_userlogin_data` → 成功后 `[conn+4]=1`；模拟器 `check_userlogin_data` 为恒成功桩（任意凭据可登录）；真机 `check_userlogin_default` 决定是否强制【实证（L1/登录态查询）】 |
| 下载事务（客户端→daemon） | 4 步：**登录 → 会话命令（type 2）→ 数据块（type 5）×N → 完成（0x0F）**；会话命令即截断目标文件（fopen "wb+"），.hcpp 先备份为 project_backup.hcpp；完成后 download_file_finished + 提交 + 日志 `"Download %s finished."`【实证（d1test.foid / project.hcpp / stprog.o 三组实验）】 |
| 上传事务（daemon→客户端） | **登录 → 会话命令（type 1, chunk_size@0x0A）→ 块请求（type 6, index@2）×N → 完成（0x0F）**；每请求应答 `[05][index][len][data]`；total = ftell（实际文件大小）【实证（Layout B 完整往返）】 |
| 程序装载 | **模拟器内 `load_down_file_start`/`load_down_file_status` 是桩（ret 1/ret 0）**——文件经本协议真实落盘，但"编译产物→运行时"的 ELF 装载在模拟器中未接通；下载后 Modbus 寄存器无变化（全 0 STOP）【实证（诚实记录）】 |
| CRC | **命令/数据帧无 CRC 字段**（与 ITP 不同）；元素块链（id 3）按元素带校验【实证（帧解析器反汇编 + 实机接受任意载荷）】 |

---

## 二、帧格式表（偏移→字段→证据→置信度）

### 2.1 帧头（8 字节，小端）【实证】

| 偏移 | 长度 | 字段 | 说明 | 证据 | 置信度 |
|---|---|---|---|---|---|
| 0x00 | 2 | totalLength | **含 8 字节头**的总帧长；请求=8+载荷；应答=载荷+8（mp_input 尾部 `mov word [eax],dx`（dx=载荷+8）） | mp_input 反汇编 + 实测全部帧（如版本读应答 250=242+8） | 高【实证】 |
| 0x02 | 1 | version | 必须 **≤2** 才进入 handler 分发；**>2 → 探测应答**（见 2.2） | mp_input `cmp al,2; jbe 分发` + E1/E3 实验 | 高【实证】 |
| 0x03 | 1 | serverId | **handler 链选择子**：mp_input 按 `handler[0]==frame[3]` 遍历该连接注册的链 | mp_input 反汇编（`movzx eax,byte [eax+3]`）+ 实测 4/0x80/0x81 各链 | 高【实证】 |
| 0x04 | 4 | 保留 | 实测恒 0，应答中原样回显 | 实测 | 高【实证】 |
| 0x08 | … | 载荷 | 结构按 serverId 定（见三） | 各 handler 反汇编 | 高【实证】 |

### 2.2 探测/握手应答（8 字节）【实证】

对 version>2（或未注册 serverId）的帧，daemon 应答 8 字节：`[请求时的旧长度词不更新][2][0xFF][0]`，rc=0x21。
**陷阱：长度词是陈旧的**（E1：12 字节探测 → 应答 8 字节但首词仍为 12；E3 同）。接收方必须按
**实际收到的字节数**处理，不能按首词。

实测样例：
```
tx(12): 0c 00 03 00 00 00 00 00 00 00 00 00
rx(8):  0a 00 02 ff 00 00 00 00      <- 首词 0a=10 仍是请求侧的旧值, 实发 8 字节
```

### 2.3 文件传输帧（serverId=4）载荷【实证】

| type（载荷[0]） | 名称 | 载荷布局 | 应答 |
|---|---|---|---|
| 0x01 | 上传会话（daemon 读文件发给客户端） | `[01][00][保留 8B][chunkSize@0x0A][未用@0x0E][filename NUL 串@0x12]` | `[06][00][total=ftell 文件实际大小@+2]`（6 字节载荷=14 字节帧） |
| 0x02 | 下载会话（客户端发文件给 daemon） | `[02][00][保留 8B][chunkSize@0x0A][totalSize@0x0E][filename NUL 串@0x12]` | `[06][00][total=请求帧的 total@+2]`（同上） |
| 0x05 | 数据块（客户端→daemon；或上传读的应答） | `[05][00][index@+2][len@+6][data@+0x0A]` | ACK：`[06][00][index@+2]`（6 字节载荷） |
| 0x06 | 上传块请求（客户端→daemon） | `[06][00][index@+2][保留@+6]` | `[05][00][index@+2][len@+6][data@+0x0A]` |
| 0x0F | 完成/关闭会话 | `[0F][00][保留 4B]` | **无应答**（out_len=0） |
| 0x0F | 错误应答（daemon→客户端） | `[0F][00][status@+2]`（status 见 2.4） | — |

实测样例（下载 "project\project.hcpp"，7 字节，chunk=64）：
```
cmd  tx: 26 00 02 04 00000000 | 02 00 00000000 00000000 | 40 00 00 00 | 07 00 00 00 | "project\project.hcpp\0"
cmd  rx: 0e 00 02 04 00000000 | 06 00 | 07 00 00 00            <- total 回显
data tx: 12 00 02 04 00000000 | 05 00 00000000 | 07 00 00 00 | "Empty\r\n"
data rx: 0e 00 02 04 00000000 | 06 00 | 00 00 00 00            <- ACK 回显 index
fin  tx: 0e 00 02 04 00000000 | 0F 00 00000000
fin  rx: (无)
```

### 2.4 daemon 状态码【实证 + 反汇编】

错误载荷 `[0F][00][status]`。来源：文件分发器/命令处理器/cmd_execute/load_down_file_start 结果映射
`{0→0x00, 1→0x82, 2→0x80, 3→0x83, 4→0x41, 5→0x84}`。

| status | 含义 | 证据 |
|---|---|---|
| 0x00 | 成功 | 反汇编（各 handler 返回 0） |
| 0x20 | 长度错误（载荷 ≤5 / 长度词不匹配）→ **无应答** | mp_input/dispatcher 反汇编 + E2/E4 实验（无响应） |
| 0x21 | 探测应答（8 字节规范化头） | E1/E3 实验 |
| 0x40 | 参数长度错误 / 元素校验不匹配 | handler 反汇编（`cmp [ebp+14h],期望长度`） |
| 0x41 | **未授权**（受保护文件 .hcpp/.foid 未登录） / handler 缺失 | 实测（未登录下载 d1test.foid → `0f 00 41 00 00 00`） |
| 0x43 | 密码错误 | 0x80 链反汇编（check_userlogin_data != 0 → 0x43） |
| 0x60 | 块序号超前（index > 期望值） | 数据接收 handler 反汇编 |
| 0x61 | 会话不存在（该连接无活动会话） | 数据/上传 handler 反汇编 |
| 0x82 | load_down_file_start 结果 1（模拟器桩恒返回 1） | 0xFF000010 反汇编 + 结果映射表（RVA 0xF2D0） |
| 0x83 | 会话忙：同连接已有别的文件会话 / 同文件被其他连接占用 | 命令处理器反汇编 + 实测（陈旧会话未 finish → 新下载被拒） |
| 0x84 | 打开失败（fopen 失败 / 路径非法 / 空 / 无登录态读取） | 实测（文件名放 0x0E 时 fn 错位 → fopen 失败） |
| 0x85 | 上传已完成（total 已达，无更多数据） | 上传 builder 反汇编 |
| 0x86 | 描述符分配失败 | 反汇编 |
| 0x87 | load_down_file_status 结果越界（>5） | 0xFF001001 反汇编 |

### 2.5 登录帧（serverId=0x80）与登录态查询（serverId=0x81）【实证】

| 链 | sub-type | 载荷 | 应答 | 语义 |
|---|---|---|---|---|
| 0x80 | 0x01 | `[01][00][user 32B][password 32B]`（74 字节帧） | `[06]`（1 字节载荷=9 字节帧）/ 错误 `[0F][43]` | **登录**：check_userlogin_data(user,pwd) → 成功后 `[conn+4]=1`；日志 `[monitor]login event:0/1` |
| 0x80 | 0x02 | `[02][00]` | `[06]` | 登出：`[conn+4]=0`；日志 `[monitor]login event:2` |
| 0x81 | 0x02 | `[02][00]` | `[06]` | **daemon 侧登出**（同一 cb(conn,2) 路径，也会清授权标志）【实证】 |
| 0x81 | 0x03 | `[03][00][保留 4B]` | `[03][00][result@+2]` | **登录态查询**：result=1 已授权 / 0 未授权【实证（登录前后 0→1→0）】 |

授权规则（文件分发器 65908A36 反汇编 + 实测）：
- 连接已授权（`[conn+4]==1`）→ 任意文件名放行；
- 未授权 → 文件名经 659076A4：设备无密码（`check_userlogin_default==0`，真机默认）→ 任意放行；
  有密码要求 → **仅非 .hcpp/.foid 后缀放行**（`.hcpp/.foid → 0x41`）。
- 模拟器中 `check_userlogin_default` 恒返回 1（PASSWORD.dll 桩）→ **.hcpp/.foid 必须先登录**；
  `check_userlogin_data` 恒返回 0（任意凭据可登录）【实证：plcmcp/0 登录成功】。

---

## 三、命令码表

### 3.1 文件传输链（serverId=4）载荷 type【实证：mp_core 65908A00 跳转表 RVA 0xF088】

16 槽跳转表，仅 0x01/0x02 → 命令处理器、0x05 → 数据接收、0x06 → 上传读、0x0F → 会话清理；
其余 type 被忽略（**无应答**）。

### 3.2 寄存器/内存链（serverId=1）命令字【实证：注册表反汇编；语义部分为推断】

mp_core 启动注册（`register_cmd(cmd, read_fn, write_fn, 验证器×2, flags)`，命令字高字节必须 0xFF）：

| 命令字 | read/write handler | 语义（推断+字符串证据） |
|---|---|---|
| 0xFF100000 | read=版本读 | PLC/模块版本信息读；**载荷长度必须 0xF0=240**；实测应答 [05][240B]（版本表） |
| 0xFF000000 | read/write | 未细查 |
| 0xFF000010 | write=0xFF0000B038 | **PLCProgram.down 装载状态**：para_len=4；access("project/PLCProgram.down"/"updown") → load_down_file_start(path, 0/1) → 结果映射状态码 |
| 0xFF100020 / 0xFF000020 / 0xFF000030 / 0xFF000050 | 各 handler | 0xFF000020 write → **g_simulator_control_word（模拟器 RUN/STOP 控制字）**——客户端**故意不实现**（未实测的控制路径） |
| 0xFF000100 | write=0xFF0000AFE0 | 程序下载启动/默认应用装载（load_default_app / g_userprog_init_state） |
| 0xFF001000 | read/write | lem_write_start（0xFF001000 write，para_len=4） |
| 0xFF001001 | read/write | 升级控制（g_upgrade_control_word，para_len=0x12；=1 时清 g_upgrade_status） |
| 0xFF001002 | read/write | 未细查 |

**客户端只实现已实证部分**：版本读（0xFF100000，只读）。0xFF000020（RUN/STOP）、0xFF000010
（装载轮询）、元素链（id 3）均未实现——标 Unsupported。

---

## 四、下载事务时序（准备→传输→完成）【实证】

```
client                                  h5u_daemon (127.0.0.1:12939)
  |  TCP connect ---------------------->  monitor_tcp_server accept
  |                                        mp_connect(socket, 0x81): 注册链 1/3/4/5/0x80/0x81
  |  [0x80 链] login (user32+pwd32) --->   check_userlogin_data -> [conn+4]=1
  |  <----------------------------------  [06]  (9 字节帧)
  |  [4 链] type2 cmd (cs@0A,total@0E,   file_path_create_dir + 备份舞蹈
  |        filename@0x12)                  (.hcpp: 旧文件→project_backup.hcpp; 日志 Download %s restart./start.)
  |                                        fopen(filename, "wb+")   <- 会话打开即截断
  |  <----------------------------------  [06][total]  (14 字节帧)
  |  [4 链] type5 data (idx@2,len@6,     download_file_handler: fwrite(data,1,len,流)
  |        data@0x0A) ×N                   每块后: index 匹配则 [desc+0x18]++, transferred+=len
  |  <----------------------------------  [06][index] ACK ×N
  |  [4 链] type0F finish ------------>   transferred==total: download_file_finished + 提交
  |                                        (PrgBackup 舞蹈 + 日志 "Download %s finished.")
  |                                        fclose + free(会话出队)
  |  (无应答)
```

关键时序事实【实证】：
1. **会话命令即截断**：type 2 会话的 fopen("wb+") 在会话打开瞬间截断目标文件——若数据未到/中止，
   目标文件为 0 字节（本实验中 stprog.o 被截断后由我方恢复；真机由 project_backup/.incomplete 舞蹈保护）。
2. **.hcpp 下载的备份舞蹈**：旧 project\project.hcpp 先改名 project\project_backup.hcpp（mtime 保留），
   再写入新文件。
3. **完成判定**：transferred == total（请求帧的 total@0x0E，type 1 用 ftell）→ download_file_finished + 提交；
   否则记 "failed"。
4. **无 finish 的连接断开 = 会话残留**：该文件被锁（后续其他连接的同文件传输 → 0x83）直至 daemon 重启
   【实证：上传会话未 finish 断连后，project.hcpp 新下载被拒】。
5. 上传读（type 6）的应答 data 长度 = min(会话 chunk_size, 剩余)；chunk_size=0 → 恒 0 字节（"failed"）。

---

## 五、T2 实验证据（全部对本地自启 daemon，回环）

实验环境：`%TEMP%\plcmcp-handoff\d1-download-workspace\Simulator\`（安装目录的完整工作副本，
robocopy 排除 app\；启动前 stprog.o/symbol.sym/project.hcpp 与安装目录 md5 一致）。
基线：Modbus D100-109/D0-9/R100/线圈全 0（STOP 态）；app\ 14 文件 md5 存档（baseline_md5.txt）。

| # | 实验 | 结果 | 证据 |
|---|---|---|---|
| 1 | 握手/探测帧（8B/10B/12B，ver=3/0xFF） | 8 字节应答 `[旧词][2][FF][0]`；8 字节探测（total=8）**无应答**（daemon 零长 recv 路径），≥10 字节正常 | E1/E3/probe 日志 |
| 2 | 版本读（id=1，cmd 0xFF100000，len 0xF0） | 250 字节应答 `[05][240B 版本表]`，长度词=250 一致 | 实测帧 |
| 3 | 登录（0x80/1，plcmcp/0） | `[06]` 接受；日志 `login event:0/1`；登录态查询 0→1 | L1 + subtype3 探测 |
| 4 | 未登录下载 .foid | `[0F][41]`（NotAuthorized） | 实测 |
| 5 | 登录下载新文件 d1test.foid（89B，2 块） | 全部 ACK；**md5 与源 varsinfo.foid 完全一致**；日志 `Download d1test.foid start./finished.` | 实测 + daemon 日志 |
| 6 | 登录下载 project\project.hcpp（内容=当前值，非破坏） | 成功；**旧文件改名 project_backup.hcpp（mtime 保留）**；新文件 md5 不变、mtime 更新；日志 `Download project\project.hcpp start./finished.` | 实测 + 文件状态 |
| 7 | 回滚演示：下载 "D1-RESTORE-TEST\r\n" 再下载回 "Empty\r\n" | 两次成功；**最终 md5 恢复基线值** 647096e8…；全程可回滚 | 实测 |
| 8 | Modbus 502 寄存器（全部下载后） | **无变化**（全 0 STOP）——程序装载未接通（load_down_file_start 为桩） | modbus_read.py |
| 9 | 完整 stprog.o 装载实验（832B 下载回原内容） | 7 块全部 ACK；**stprog.o md5 恢复原版** b0616e43…；Modbus 无变化 | 实测 |
| 10 | 中止会话副作用（type 2 打开会话不传数据） | **stprog.o 被截断为 0 字节**（fopen "wb+" 瞬时截断）→ 我方从 default\app 副本恢复（md5 复核一致） | 实测（已记录为事务语义） |
| 11 | 上传方向（type 1 + type 6 ×N） | **"Empty\r\n" 完整回读**（total=ftell）；Layout B 成功/日志 "Upload finished."；C 布局（chunk_size@0x0E）会话开但数据恒空 | 实测 |
| 12 | C# 客户端端到端（env-gated，真实 daemon） | probe → 版本读 → 登录 → 下载唯一文件 → 上传回读（**内容一致**）→ 登出（登录态 1→0）全通过 | AutoShopDownloadProtocolTests.Real_daemon_* |

退出清理：我启动的每个 daemon 实例（PID 50676/55216/49000/54248）均以 taskkill 干净停止；
安装目录零修改（stprog.o 实验载荷在 d1-download-workspace 上操作，安装目录 md5 复核不变）。

## 六、与 ITP / 通信栈 / P3 的关系

1. **与 ITP（`inovance-itp-protocol.md`）无关**：ITP 是 InoVarSharedService 的 13939 JSON 协议
   （28 字节头 + 0xA50EA50E 魔数 + CRC16）；本协议是 h5u_daemon 的 12939 二进制协议
   （8 字节头、无 CRC、0xFF 高字节命令字）。两者不要混淆。
2. **与通信栈（CommunicateEthernet.dll）**：厂商客户端 `CCommunicateEth::Download/DownloadPlcSoft/
   CompleteDownloadCmd`（48 字节命令对象 → RunCommThread 队列 → 通信线程组帧）是本协议的**对端实现**；
   `tagEthernetInfo` 默认 192.168.1.1:12939（第十节实证）。本协议逆向以 daemon 侧为准
   （服务器 = 协议定义），客户端构造帧经 mock 回放 + 实机收发验证闭环。
3. **与 P3（`inovance-autoshop-program-blob.md`）**：P3 的"装载路径不可注入"卡点由本文档解除——
   12939 下载协议已逆向并实证（文件真实落盘）；但"程序装载"（ELF 进运行时）在模拟器中仍是桩，
   见第七节衔接建议。

---

## 七、未验证项清单（禁止当作事实）+ 给密钥导出任务的衔接建议

**未验证项**：
1. 寄存器链（id 1）0xFF000000/0xFF100020/0xFF000030/0xFF000050/0xFF001000/0xFF001002 的完整语义。
2. 0xFF000020（模拟器 RUN/STOP）的写行为——客户端故意不实现、未实测。
3. 元素块链（id 3）的元素布局与按元素校验算法（parse_cmd_para）。
4. 真实 PLC（非模拟器）上的协议行为：登录凭据哈希（check_userlogin_data 的真机实现）、
   密码强制策略、帧长上限随版本变化。
5. 帧头 [4..8) 保留字段的语义（实测恒 0；疑为会话/序号回显位）。
6. type 2 会话 total 与实际写入字节数不一致时的收尾行为（0x83/0x61 之外的路径）。
7. 多连接并发下载同一文件时的竞争行为（仅验证了 0x83 拒绝）。

**给 P3 密钥导出任务的衔接建议（拿到可解密 payload 后如何用此协议装载合成程序）**：
1. **密钥侧**（P3 六.3）：`EnDecrypt.dll` 的 `CAESAlg::CreateNewKey/GetKey` 经 x86 垫片直调
   （CommStackShim 先例）导出密钥 → blob payload 可解密 → `.hcp` 源工程可合成
   （S1 生成器）→ AutoShop IDE 编译 → **stprog.o/symbol.sym/*.foid 编译产物**。
2. **装载侧（本协议）**：编译产物放不进本协议的文件名白名单（.o/.sym 不是 .hcpp/.foid——
   659076A4 校验 + 登录规则）；**可行路径 = 打包为 project.hcpp**（PPCH 容器，含 stprog.o 等成员）：
   登录 → type 2 会话下载 project.hcpp（daemon 自动备份旧文件）→ 0xFF000010/0xFF000100
   （寄存器链）触发装载 → 但**模拟器 load_down_file_start 为桩**，需真机或在模拟器工作副本上
   补桩实证；装载验收 = Modbus 读回（P2 通道）。
3. **更直接的路**（无需 12939）：daemon 启动不读 app\ 内文件（P3 实证）——把编译产物直接写入
   daemon 工作副本的 app\（首烧复制会覆盖，需 daemon 启动后写入）→ 但运行时是否在启动后
   重读/热装载未实证（0xFF000100 语义待查）。
4. **工程包内文件名**：`net.down/net.tmp/net.up` 是工程包内成员（P3 结论不变），不是本协议的传输帧；
   本协议的传输帧 = 第二节的 type 1/2/5/6/0F。
