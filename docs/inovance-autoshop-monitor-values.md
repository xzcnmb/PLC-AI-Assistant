# 汇川 AutoShop 通信栈"读取结果回填"机制逆向文档（G1，2026-10-29）

> 目标：把注入桥监视从"调用级证据"推进到真实值——先静态逆向 AutoShop 4.12.0.2
> 通信栈的**值回传路径**（谁发请求、应答落在哪个结构哪个字段、UI 如何消费），再在
> 注入桥进程内（自启实例 + 127.0.0.1:12939 模拟器）实证各候选路径，并据结果升级
> `read_elements`。
>
> 对象：AutoShop V4.12.0.2（x86，`D:\Inovance Control\AutoShop`）。
> 工具链：VS2022 v143 x86（桥构建）、dumpbin + pefile + capstone 5.0.7（静态反汇编，
> 全部在 `%TEMP%\plcmcp-handoff\g1-monitor-values\` 的 DLL **只读副本**上做，安装目录零写入）。
>
> 与以下文档配合阅读：`docs/inovance-autoshop-native-bridge.md`（S2/F2，第十五节真实
> 原语与 tag 布局）、`docs/inovance-autoshop-write-signatures.md`（F1 枚举/调用链/构造序列）、
> `docs/inovance-autoshop-inject-bridge.md`（E2 注入桥）、`docs/inovance-autoshop-download-protocol.md`（D1 模拟器侧 12939 协议）。
>
> **所有接口均为逆向所得，无厂商背书；版本锁定 4.12.0.2 / x86。**
> 标注约定：【实证】= 本次亲自执行并复核；【推断】= 基于实证的推理；【未验证】= 禁止当作事实。
> 证据分级：**A** = 反汇编+运行双证；**B** = 反汇编单证；**C** = 推断。

---

## 〇、结论摘要（先读这一节）

| 问题 | 结论 |
|---|---|
| 值回填到哪个槽？ | **不是 tag+0x20 double**（F2 §15.5 的"强证据待证实"被运行时写点证据**推翻**）。真实回填槽是 **tagSoftAddr+0x0C 的 CString**（值字符串）。两条厂商读路径的应答解析都写 +0x0C：位路径 CE+0xDE20 尾部（静态"1"/"0"串），监视/字路径 CE+0xA610（格式化数值串）；AutoShop 监视表自己的取值函数 `g_GetMonElemValue`（InterfaceMgr 0x3CDE0）读的也是 +0x0C【实证 A：反汇编写点+读点闭环；全盘 `fstp/movsd [reg+0x20]` 扫描证明 InterfaceMgr/CommunicateEthernet 中 +0x20 只有构造器 fldz 初始化写点】 |
| ReadElemValue 读不读字元件？ | **不读**。map 执行器 CE+0xDE20 用 GetNextAssoc 遍历传入 map，**只对 tag+0x10==1（位）建帧**（opcode `a3 c1`），type!=1（字，如 D）条目直接跳过——F2/F3 里 D100 从未被 `ReadElemValue` 请求过，抓到的单帧是 M0 的。字元件读取属 `ReadWatchElemValue`/`CMonitElemBuffer` 监视路径（帧族 `a3 c1/a3 c2/a3 c3` 三类计数）【实证 A】 |
| 异步完成回调形态？ | `RunCommThread`（CE+0x2FB0→0x10810）把命令对象入队并由**通信工作线程**执行；完成通知 = `SetEvent` + 对 `[cmd+0x20]`（= pWnd 的 `m_hWnd`，仅当 HWND 有效时 `IsWindow` 后）`PostMessageW(0x4CA, 0x49, rc)`——**窗口只收"完成通知"，值不靠窗口投递**；因此 F3"HWND 投递触发异常"的根因是把裸 HWND 当 `CWnd*` 传给形参（厂商对它做 `[p+0x20]` 解引用），而非"投递路径缺 UI 状态"【实证 A：0xE2B0/0xDDE0 通知代码；G1 shim 实验（正确形态的 CWnd 形状对象，+0x20 放真 HWND）不再崩溃，rc=0 正常返回，delivered=0】 |
| 注入桥进程内能不能闭合值回填？ | **当前不能**（实证矩阵全部 delivered=0）。新证据：独立进程（F2）里 `ReadElemValue` 的请求帧真实上网（daemon 关连接不回话）；**注入的 AutoShop 进程内反而一根字节都不上网**（12940 回环代理零连接记录）——进程内的 CCommunicateEth 单例/会话对象与独立 harness 不同，异步命令入队后 worker **没有把帧发到 set_target 配置的目标**（`IsEthOnLine`=1 也不产生任何 TCP）。结论修正 F2 §15.3 的"注入桥进程内会话机器已就绪"假设：**会话建立是独立的前置，进程身份不自动带来会话**【实证 A：代理抓包零流量 + rc/online/activeMap 全回执】 |
| SimpleCommand "上线探测"？ | CE+0x3400（F1 判真实现）以手工 MFC90 CStringData 镜像传空命令串调用 → **SEH 捕获 0xC0000005**（桥存活）。根因【推断 B】：AutoShop.exe 静态链 MFC（窗口类 `Afx:00400000`）而 CommunicateEthernet 走 mfc90u，两侧 `CStringT` 共享态不互通，合成串不被接受。此路不通，代码保留为带诊断的可选变体 |
| 监视表窗口路径的"定时器/消息"？ | 监视数据回传**不走 UI 定时器**：应答在 worker 线程内联写 tag+0x0C，然后一条 PostMessage 通知（0x4CA/0x49）。UI 取值是被动读 map（g_GetMonElemValue→+0x0C）【实证 A】 |
| 桥的 read_elements 升级后诚实边界 | **delivered=N** 计数器：任何 tag+0x0C CString 指针相对调用前快照发生变化 = 应答读闭合；否则 note 明示"CALL-LEVEL evidence, never PLC data"。当前模拟器实证矩阵下恒 delivered=0（如实），一旦后端接通（真机/未来会话建立逆向完成）同一调用面自动产出真值，无需再改协议 |

**一句话**：值回传路径已经完全定位（结构→字段→回调全链路，下表逐条证据）；`read_elements` 已从"读错槽"升级为"读对槽 + delivered 判别"；但在"无工程在线会话"的自启实例里，注入进程内的以太网 worker 根本没把请求帧发向配置目标——**卡点从"不知道值写哪"收敛为"会话建立未逆向"**（下一步方向见 §6）。

---

## 一、T1 值回传路径图（结构→字段→回调→证据→置信度）

### 1.1 位元件读（ReadElemValue，map 路径）

```
调用方线程（UI 或注入桥管道线程）
 └─ CCommunicateEth::ReadElemValue(map, pWnd, mode, level)      CommunicateEthernet RVA 0x37B0
     ├─ Instance() 单例守卫 0x10002F40（空则走 0xF390=62352 失败分支）           [S2 §5.2 实证]
     ├─ new 0x2C 命令对象（ctor CE+0xDD60，vtable 0x387AC）
     │    +0x1C = map 指针、+0x20 = (pWnd?[pWnd+0x20]:0) 即 m_hWnd、
     │    +0x24 = 0x55（命令码 = 元件读）、+0x28 = 1                          【实证 A，本文】
     ├─ RunCommThread(this, cmdObj, mode)  CE+0x2FB0 → 入队 0x10810（SEH/锁/队列）
     │    rc=0 = 受理（不代表执行完成）                                        【实证 A】
     ▼
通信 worker 线程（Execute = 命令 vtable+0x40）
 └─ CE+0xDDE0:  vtable+0x3C → [cmd+0x30]==4（当前通道=以太网，F1 的 4=以太网）才继续，否则跳过   【实证 A】
     └─ CE+0xDE20:
         ├─ [cmd+0x1C]（map）GetCount/GetNextAssoc 遍历
         │    ★ 只对 tag+0x10 == 1（位类型）建帧；其它类型 continue 跳过 ★          【实证 A】
         ├─ tag+0x08（地址串 "M0"）→ CE+0x9C70 编码 → htons → 请求元素
         ├─ opcode 写入 0xA3 0xC1（CE+0xDF05-07；= F2 抓包 21 字节帧中的 a3 c1）      【实证 A】
         ├─ CE+0x95C0 会话收发：守卫 ((commObj+4)+0x20)==0 → 直接返回失败（-32=会话未建立）
         │    正常路径：逐命令 select 100ms×…、send(0x9820→WSAsend 0x8740)、
         │    recv 循环(0x8790，最多 3 次重试+Sleep(10))                            【实证 A】
         ├─ rc==1（收到应答）→ 解析 CE+0x9BC0（把应答 16-bit 字 memcpy 出来）
         │    → ★ CString::operator=(tag+0x0C, 静态串) ★
         │      应答字==0 → 0x10046070；!=0 → 0x1004606C（两条预建 CString，位值文本）【实证 A】
         ├─ rc==-9 → 返回 17；rc==-7 → 返回 3（worker 侧内部码，不外递）             【实证 B】
         └─ 完成通知：vtable+0x20 → CE+0x27D0: 仅当 [obj+8]==1（mode=同步等待）时
            WaitForSingleObject(msgObj+0x10, 120000ms)；否则跳过                    【实证 B】
            另一路径 CE+0xE2B0：IsWindow([cmd+0x20]) → PostMessageW(hwnd,0x4CA,0x49,rc)
            （0x4CA/0x49 = 监视刷新消息族，UI 线程收到后重读 map 刷新表格）          【实证 A】
```

**要点**：值在 **worker 线程内联**写进 `tag+0x0C` CString；HWND 只收完成通知。F3 实验传裸 HWND 崩溃 = 把句柄值当 CWnd* 解引用（+0x20）。

### 1.2 监视表/字元件读（ReadWatchElemValue，CMonitElemBuffer 路径）

```
CCommunicateEth::ReadWatchElemValue(buffer, pWnd, mode, level)   CE RVA 0x36C0
 └─ 0x28 命令（ctor 0xE1F0，vtable 0x388B4）：+0x1C=buffer、+0x20=m_hWnd、+0x24=0x56?（命令族）
    Execute → CE+0xE250 →（通道==4）→ CE+0xE2F0:
      ├─ GetPublicMgr（InterfaceMgr 导入 IAT 0x35064）→ vtable+0x80 → **活动 map**（UI 监视表那张）【实证 A】
      ├─ buffer 接口取三类计数（vtable+0xC/+0x18/+0x14）→ 位/字/其它三组
      ├─ 三帧族：0xA3 0xC1（位）、0xA3 0xC2（字）、0xA3 0xC3（其它）逐组 CE+0x95C0 事务        【实证 A】
      ├─ 成功 → CE+0xA610 解析应答：GetNextAssoc 遍历 **活动 map**，
      │    对每个元素把应答值写进 tag：位 → 静态"1"/"0"串；字 → 数值格式化
      │    → ★ CString::operator=(tag+0x0C) ★；再 buffer vtable+0x8(1, ...) 收尾                【实证 A】
      └─ 通知：IsWindow([cmd+0x20]) → PostMessageW(hwnd, 0x4CA, 0x49, rc)                        【实证 A】
```

**CMonitElemBuffer**（`??0CMonitElemBuffer@@QAE@XZ` IM+0x4FAF0，vtable IM+0xB4AAC）：
基类 vtable 大量槽为桩（0x5080/0x70e7a）——**基类实例没有元素数组**，G1 实证调用 rc=0 受理但零帧（见 §4）。真实监视表缓冲是 AutoShop.exe 内 MFC CObject 派生（`CElemMonItem`/`CH530ElemMonItemBase`/`CLDElement`，InterfaceMgr 0x65E00/0x25CB0/0x55270 构造桩多、真实数据在 exe 的监视文档对象里），填表入口是 `g_AddMonitorElem`（IM+0x3CCF0，F2 已实证=官方监视表填充：malloc 0x48→tagCtor→+0x08 地址、+0x10 类型→operator[] 入 map），监视文档对象持有到 CMonitElemBuffer 的引用——**该引用链未还原**【未验证】。

### 1.3 UI 消费侧

`g_GetMonElemValue`（IM+0x3CDE0）：GetAssocAt(map, addr) → **读 tag+0x0C CString** 作为显示值（type 0x800 且 tag+0x28==0 时强制回 0）；`g_GetIntElemVal`/`g_GetBitElemVal`（0x3D620/0x3D6B0）都转调它。写侧 `g_AddElemValue`（0x3D2D0/0x3D3C0）同样把值**格式化进 tag+0x0C**。→ 监视表显示值 = tag+0x0C 字符串，闭环成立。

### 1.4 与旧文档的冲突裁决

| 旧结论（出处） | G1 裁决 |
|---|---|
| F2 §15.5："tag+0x20 double 数值槽写回目标（强证据，待闭环）" | **推翻**。+0x20 无任何运行时写点（fstp/movsd 扫描 0 命中，仅 ctor fldz）；回填槽 = +0x0C CString（读路径全部写点在此）|
| F2 §15.3："须在注入桥进程内（会话机器已就绪）" | **修正**。注入进程内同样无会话：全部变体 delivered=0 且 **零线流量**（代理抓包）；独立 harness 反而有帧。进程身份≠会话就绪 |
| F3 实验："HWND 投递触发结构化异常=回传需 UI 状态" | **根因改写**：崩溃=把 HWND 当 CWnd*；正确 CWnd 形状（shim）不崩。异常与值回传无关 |
| E2/F3 "两套实验阴性记录"（tag+0x20 恒 0） | 阴性原因确认：槽读错 + 字元件根本不被 map 路径请求 + 进程内 worker 未上线（三层叠加）|

---

## 二、daemon/模拟器侧的判别事实（补充 D1）

G1 抓包记录（回环代理 127.0.0.1:12940→12939，注入进程全部实验期间）：**代理 accept 计数 = 0**——注入桥进程内的 CCommunicateEth 从未连接配置目标（含 `IsEthOnLine`=1 的调用窗口）。对比 F2 独立 harness（同代码、同配置）：`IsEthOnLine` 与 `a3c1` 帧都真实上网。即：**独立进程有"半会话"（能连不能得应答），注入进程连"半会话"都没有**。daemon 侧唯一可见交互仍是 D1 实证的文件/登录链（本项目实验期间 daemon 日志只出现 project.hcpp 登录/下载条目，与监视链无关）。`ReadElemValue` 帧族（serverId 元素链）在 daemon 侧的应答语义仍【未验证】（F2 的"请求上网、S→C 无字节、连接关闭"维持）。

---

## 三、桥 read_elements 升级（交付）

### 3.1 原生侧（`tools/AutoShopInjectBridge/src/bridge/`）

- `VendorComm::ReadElements(ReadElementsRequest&, ReadElementsOutcome*)` 重写：
  - **每元件一张私有 map** 依次调用 `ReadElemValue`（执行器只处理首个 type==1 条目，多元件共享 map 会漏读——§1.1）；
  - 调用前对每个 tag 快照 **+0x0C CString 指针**与 **+0x20 double**；
  - settle 窗口（默认 6s，参数 `settleMs` 上限 20s）内 200ms 轮询 Lookup，任一槽变化即判 **delivered** 并跳出；
  - 应答值经 SEH 从 CString 解引用读出（宽字符→UTF-8，上限 39 字），输出 `values`（double 镜像）与 `valueSlots`（+0x0C 交付槽：`addr=值` 或 `addr=-` 未交付）；
  - 变体：`mode`/`level`（默认 0/0；F1 的 UI 组合 1/0 实测 rc=17 拒绝，见 §4）、`wnd`（CWnd 形状 shim：FindMainAfxFrame 的 HWND 放 shim+0x20，绝不传裸句柄）、`activeMap`（GetPublicMgr→vtable+0x80 只读解析+GetCount，**不插入**——无 Remove 导出，插入会抢活体 map 所有权，红线）、`watch`（基类 CMonitElemBuffer ctor→ReadWatchElemValue→dtor，全程 SEH，记录 rc）、`online`（SimpleCommand 空串探测，SEH）；
  - note 动态化：`delivered>0` → "VALUE WRITE-BACK CLOSED … these values are PLC data as read by the vendor stack"；否则 → "VALUE WRITE-BACK NOT OBSERVED … CALL-LEVEL evidence, never PLC data"。
- `BridgePipeServer::HandleReadElements` 解析上述可选参数并回 `count/delivered/values/valueSlots/watchRc/onlineRc/activeMap/activeMapCount/onlineBefore/variant/note` 全量 JSON（响应缓冲 4096→8192）。
- 构建复现：`tools\AutoShopInjectBridge\build.cmd`（VS2022 v143 x86）；`BridgeTestHost` 14 项检查 **0 failures**（升级后复跑）。
- **诚实边界不变**：写面仍解析不调用；一切厂商调用 SEH 隔离；回环硬校验前置照旧。

### 3.2 C# 侧（`src/PlcMcp.Engineering/Workers/AutoShop/InjectBridge/`）

- `InjectBridgeRequestParams` 新增 `mode/level/settleMs/wnd/activeMap/watch/online`；
- `InjectBridgeResult` 新增 `delivered/valueSlots/watchRc/onlineRc/activeMap/activeMapCount/onlineBefore/variant`；
- `InjectBridgeReadVariant`（记录）+ `ReadElementsAsync(addresses, variant?)`：默认调用与升级前协议兼容；
- `InjectBridgeElementReadReport` 新增 `Delivered/ValueSlots/Variant/WatchRc/OnlineRc`；
- 消息分级：`Delivered>0` → 报为**厂商栈读到的真值（模拟器/回环实证口径，仍 Experimental）**；`Delivered==0/null` → 维持 CALL-LEVEL 原话；绝不软化。

### 3.3 测试（全离线）

- `InjectBridgeTests`：+7 新用例（G1 交付字段映射、delivered=0 保持调用级措辞、变体参数线上传递、onlineRc 映射、基线不泄漏开关），全量 Engineering 354 通过/0 失败（4 skip 为环境门控项）。
- `AutoShopInjectToolsTests`：+2 新用例（G1 交付措辞经 `plc_autoshop_monitor_read` 逐字透出、delivered=0 时"NOT OBSERVED"保持），全量 PlcMcp.Tests 381+2 通过。

---

## 四、T2 实证矩阵（127.0.0.1 模拟器，注入自启实例，全程可回滚）

基线：daemon 502 `FC6 D100=0x1234` 写读一致、`FC5 M0=1` 写读一致（G1 复核）；桥 `set_target(127.0.0.1, 12940)`（回环代理，daemon 在 12939）。

| # | 路径/变体 | 调用形态 | 返回值 | 帧证据（代理） | 结果 |
|---|---|---|---|---|---|
| 1 | 基线位元件 | `read_elements M0`（mode0/level0/wnd NULL） | count=1, **delivered=0**, values=`M0=0?` | 无 | +0x0C 未变（无应答）|
| 2 | 基线字元件 | `read_elements D100` | 同上 `D100=0?` | 无 | map 路径本就不请求字元件（§1.1）|
| 3 | 组合 | `M0,D100` 单调用 | count=2, delivered=0 | 无 | 逐元件调用后仍零 |
| 4 | F1 UI 枚举 | `M0, mode=1, level=0` | **rc=17**（-32002） | 无 | (1,0) 被拒（F1 §2.1 rc=17 观察复现）|
| 5 | 主框架窗口 | `M0, wnd=1`（CWnd 形状 shim） | rc=0 受理, delivered=0，**无 SEH** | 无 | F3 崩溃根因确证=类型误用；通知与回传无关 |
| 6 | 活动 map | `M0,D100, activeMap=1` | 解析成功 `activeMap=0x17286628 count=0`，delivered=0 | 无 | UI 活动 map 存在但空（未开监视表）|
| 7 | 监视缓冲 | `D100, watch=1` | **watchRc=0 受理**，delivered=0 | 无 | 基类 buffer 计数全 0 → 三帧族全跳过（§1.2）|
| 8 | 位族扫描 | M0/X0/Y0/S10（+D100 对照） | 全部 delivered=0 | 无 | 非前缀判别问题，是会话缺失 |
| 9 | 上线探测 | `M0, online=1`（SimpleCommand 空串） | `online=fault(0xC0000005)`（SEH 兜住，宿主存活），随后读 delivered=0 | 无 | 合成 CString 被 MFC 静态/共享态混链拒绝（§〇）|
| 10 | E2/F3 旧路径（对照，run-final5） | 旧桥 `D100,M0`，tag+0x20 | values 恒 0 | 无 | 与本文机制解释一致 |

**判定标准复核**：FC6 写 D100=0x1234 后桥回读 ≠ 0x1234（无任何真值闭环）；全部路径零 daemon 应答帧。阴性结论 + 机制定位（§1/§1.4）成立。

---

## 五、能力矩阵同步（C# 侧如实声明）

| 能力 | 升级前 | 升级后（本交付） |
|---|---|---|
| `variable_read` | Experimental（调用级；tag+0x20 恒 0） | **Experimental**：调用级；但交付判别已就位——`delivered=N`（+0x0C CString 变化）即真值闭环，当前模拟器矩阵恒 0 |
| `comm_stack_read_only` | Experimental（调用回执级） | 不变 |
| `element_write/force/download` | Unsupported（门+闸门） | 不变（本任务未触写面） |
| `compile` | Experimental（F1 链） | 不变 |

**真值 = 尚无**；`plc_autoshop_monitor_read` 的 E4 措辞（"WRITE-BACK is not closed"）在本矩阵下仍准确；但其附带句"the frame-HWND delivery experiment faults"应改为"raw-HWND 误用已解释（CWnd* vs HWND），shim 形态无故障但同样无回传"——Server 文件不在 G1 编辑范围，由集成负责人落笔；`InjectBridgeElementReadReport` 已提供 `Delivered/ValueSlots/Variant/WatchRc/OnlineRc` 供其直接引用。handoff.json 的 **`monitorValues`** 键已同步本结论。

---

## 六、给下一步的收敛接口

1. **唯一剩余卡点 = 进程内会话建立**：让注入进程内的 CCommunicateEth 把 `a3c1` 帧发到配置目标（独立 harness 已证会）。候选侦察方向（均未执行）：
   - `IsEthOnLine`→`0x8570` 序列在 harness/进程内的分叉（`[commObj+5C]` 配置对象是否同一个、`[obj+4]+0x20` 会话对象创建点）；
   - UI 监视启动链（命令 0x807D 处理器→buffer/文档构造→SetCommunicatConfig 调用序）静态还原；
   - daemon 侧元素链（serverId 3）应答条件（D1 §七.3 的 parse_cmd_para 校验）——即使帧上网，S→C 应答仍可能需要登录链配合。
2. 桥的读面 API 不需要再改：会话一通，`delivered=1` + `valueSlots` 自动给出真值（无需新协议字段）。
3. 若后续在任何后端（真机除外）观测到 `delivered>0`，`variable_read` 可升 **Experimental(实证)** 并引用本文 §4 表格补一行即可。

## 七、红线合规声明（G1 实验全程）

- 零真机 PLC 动作；目标仅 127.0.0.1（代理 12940→daemon 12939、Modbus 502）。
- 注入实例全部由注入器自启（owned.jsonl 归属记录），实验后归属核验终止（taskkill 复核无残留 AutoShop 进程）；代理进程已停；daemon 为环境既有进程（非本次启动，保留原状）。
- 安装目录零写入；反汇编对象为 `%TEMP%` 只读副本；实验数据全在 `C:\Users\18735\AppData\Local\Temp\plcmcp-handoff\g1-monitor-values\`（`wire.log` 0 字节即阴性证据本体、`matrix-12940.log`、`g1run/` E2 全链路输出）。
- 每个厂商调用 SEH 隔离；online 变体的 0xC0000005 被捕获、宿主存活、后续调用正常（实验内连续复跑证实）。
