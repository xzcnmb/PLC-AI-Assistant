# 开发分工与契约边界

本版采用 Contracts → Core/Runtime → Adapters → Server 的依赖方向，测试可以引用所有层。以下规则供并行开发者共享，不需要共同修改同一文件才能实现新后端。

## 目录所有权

| 模块 | 负责内容 | 不应包含 |
|---|---|---|
| PlcMcp.Contracts | TargetProfile、EndpointProfile、TagDefinition、CapabilitySet、计划/结果、MCP 描述 | 网络 IO、厂商 DLL、业务调度 |
| PlcMcp.Core | 协议目录、默认模拟目标目录 | 声称未安装的 worker 已支持 |
| PlcMcp.Runtime | 目标锁、别名/类型/量程校验、计划、审计、IPlcRuntimeClient | Socket、S7/COM/UIA 调用 |
| PlcMcp.Adapters | 配置校验/装配、协议编解码与连接 | MCP 消息处理、隐式现场写入 |
| PlcMcp.Server | stdio JSON-RPC、工具 schema、参数路由、启动配置 | 再实现别名消歧/量程规则、猜测 PLC 类型 |
| tests/PlcMcp.Tests | 领域回归、MCP 消息、localhost 协议模拟 | 生产 IP、真实 CPU 下载或输出控制 |

分工时契约变更由集成负责人协调，新增参数必须有默认值或明确升版。各代理只编辑授权目录；需要另一个目录修改时报告接口需求，避免覆盖其他代理工作。

## 运行时接口

`IPlcRuntimeClient` 接收已登记 `TargetProfile` 与类型化 `TagDefinition`，提供 ProbeAsync、ReadAsync、WriteAsync。物理组合使用 ProtocolRuntimeClient，WriteAsync 总是拒绝；内存组合才提供模拟写。

`IPlcProtocolAdapter` 的 ReadAsync 只接受标签清单，不能接收任意原始帧。实现方保证：

1. 校验类型、地址/长度、目标协议和可读性，再建立连接；读取只发送对应 read/handshake 命令。
2. 每批受 Endpoint.TimeoutMs 总预算约束，取消或失败释放 socket，不把超时悄悄转成成功值。
3. 响应校验命令、长度、站号/事务号/服务号、协议错误码，不能按收到字节数随意切片解码。
4. TagValue 必须包含 DataType、Timestamp、Quality；模拟使用 Simulated，物理正常返回 Good；这些质量码不代表安全认证。
5. 不用读取的寄存器值猜 CPU RUN 模式。Probe 的 TCP_CONNECTED 仅表示 TCP 可达，S7_SESSION 仅表示协商成功。

当前每批建立独立连接、逐标签读取。目标 SemaphoreSlim 仅保证同一 Server 实例内串行；不是全网 PLC 独占锁。批读不是一致扫描快照。后续连接池、PDU 合并与采样率控制必须保持这些语义。

## 计划与结果

- PlanWriteAsync 支持规范名和 alias，重复映射同一标签拒绝。
- 计划最多 128 项、5 分钟；pending 存储有容量上限，新增时清理过期项。
- StateHash 只覆盖计划涉及标签的名称、值、类型和质量，不包含现场所有联锁；不能代表现场状态整体未变。
- ApprovalToken 是模拟消费令牌，由同一进程产生，不能充当人工审批。生产版本须用独立受信审批身份并绑定目标序列号、工程哈希、动作摘要、有效期。
- 一次 apply 尝试即消费计划，包括 token 错误/过期/状态不匹配；取消不恢复，避免对已提交操作自动重试。
- 每项先 write_intent，再写客户端，成功后 write_tag；失败结果保留已完成的 Values，无整批原子性或自动回滚承诺。
- 审计是内存追加记录，当前不持久、不具备防篡改证明。

## 能力查询

Supported 仅用于本机已实现且有匹配证据的能力；物理四协议均 Experimental（localhost 已测、真机待验）；Unsupported 表示没有实现/安装条件。

`browse_symbols` 对应 plc_list_tags/plc_browse_symbols；当前 `diagnostics` 只对应 plc_probe_target 的连接检查。任何扩展诊断、在线模式、订阅、编译、下载必须单独增加实际后端和测试。协议目录是候选协议说明，具体 TargetProfile.Capabilities 才是此实例执行能力。

## 后续工程 worker 契约（设计，未实现）

输入 EngineeringRequest：jobId、backendId、IDE 版本/位数、项目工作副本 artifactId、sourceHash、操作类型、超时、目标型号；输出 EngineeringResult：状态、诊断列表（块/网络/行/严重性/原文）、IDE/compiler/library/device-pack 版本、源与产物哈希、回读证据和能力限制。

项目写入始终在副本上，原工程不被隐式重存。导入/编译/保存之后重新打开文件或导出核对，不能把退出码 0 当成工程完整性证据。下载另用 DeploymentPlan，包含 CPU 身份、差异、保持值影响、备份、受信批准；现场是否可回滚由目标能力报告，不能预先保证。

S7-200 SMART 与 TIA 后端分离；CODESYS 标准与 InoProShop OEM profile 分离；欧姆龙/三菱先验证官方接口或版本固定的 UIA，UIA 不能伪装成厂商稳定 API。HMI/SCADA worker 与 PLC 工程 worker 独立，共享符号 manifest，不共享私有画面格式。
