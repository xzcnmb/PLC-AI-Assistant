# E3：AutoShop 打开失败根因与编译管线（实验证据链）

> 本目录固化 `docs/inovance-autoshop-open-and-compile.md`（E3）的全部实验证据链。
> **实验红线合规**：零 PLC 动作；全部 AutoShop 实例为脚本自启（实验后 taskkill 清理）；
> 安装目录零写入（InterfaceMgr.dll 仅复制到本目录实调）；注册表 Recent File List 已回滚。

## 文件清单

| 文件 | 用途 |
|---|---|
| `e3lib.py` | 静态分析工具集：反汇编（capstone）、imm32 交叉引用、UTF-16/ASCII 串定位、IAT 解析、资源串读取 |
| `e3ppch.py` | PPCH 容器解析/提取（对齐 P1 HcppReader 语义） |
| `e3build.py` | 从 Template A1.hcpp 构造"可打开工程目录"（变体实验用） |
| `e3variants.py` | 构造变体 .hcp（Version=2 / 无 `<file>` 元素）验证 7597/62220 触发 |
| `run_exp.py` | 实验运行器：自启 AutoShop → 窗口快照 → 对话框子控件取证 → taskkill 清理 → 文件 SHA 前后比对 |
| `win_enum.py` / `win_child.py` | 窗口枚举（复制自 cli-re） |
| `UcodeProbe.cpp` / `dbg1.cpp` / `dbg2.cpp` | CUCodeCrypt 语义实证 harness：UCodeDecrypt(PB_W0)=文件→文件原地带内解密 |
| `E3CompileBridge.cpp` | 进程内编译触发桥（注入 AutoShop 实例后按 0x80E1 同款链调用）——已交付 `tools/AutoShopNativeBridge/src/e3/` |
| `E3Injector2.cpp` | 注入器（CreateRemoteThread+LoadLibraryW，只注入自启实例）——已交付 `tools/AutoShopNativeBridge/src/e3/` |
| `enc_main.bin` / `plain_main.bin` | Template A1 的 Main blob（加密）/ 解密后 UTF-16LE XML（来自 d3-encrypt-recon） |
| `proj1..proj6` / `proj_v2` / `proj_nofile` | 变体实验工程目录（含 app 打开后写回的 BackUP/Temp/config.ini 副作用样本） |
| `loader*.txt` / `core_loader_0x41d600.txt` / `compile_*.txt` / `handler_*.txt` | 反汇编摘录（0x16ADD F0 错误报告、0x41D600 核心加载器、5 个加载器、0x15A2EC0/0x15A3780 编译、0xA847A0/0xA81B00 处理器） |
| `bridge_log.txt` / `injector*_log.txt` | 注入桥运行日志（3 次编译触发全部成功） |

## 关键复现命令（节选）

```bash
# 1) 错误串定位
python e3lib.py rstr 7596   # 加载Hcp文件中的工程属性信息错误...
python e3lib.py rstr 7597   # Hcp文件版本不兼容...
python e3lib.py rstr 7598   # 加载Hcp文件的文件列表信息错误...

# 2) 变体实验（自启实例 + 窗口取证 + taskkill 清理）
python e3variants.py                                   # 构造 proj_v2 / proj_nofile
E3_WATCH_FILES="<abs proj3 path>.hcp" python run_exp.py "<abs>\proj3\模板A.hcp"   # 打开成功（title 含工程目录）
E3_WATCH_FILES="<abs proj_v2 path>.hcp" python run_exp.py "<abs>\proj_v2\模板A.hcp" # → 7597 对话框
E3_WATCH_FILES="<abs nofile path>.hcp" python run_exp.py "<abs>\proj_nofile\模板A.hcp" # → 打开工程失败（62220）

# 3) CUCodeDecrypt 语义（自建进程实调，InterfaceMgr.dll 本地副本）
cmd //c build_probe.cmd && ./dbg2.exe    # enc_main.bin → q1_out.bin = 明文 XML

# 4) 进程内编译触发（注入桥，自启实例）
cmd //c build_bridge.cmd
./E3Injector2.exe "<abs>\proj6\模板A.hcp"   # 20s 等打开 → 注入 → 事件触发 0x15A2EC0 → 取 <proj>\Compile\*.compile
```

## 实验结果一览

| 实验 | 结果 |
|---|---|
| E1：CLI 打开 .hcpp 副本 | 7596 对话框；文件字节未变（UCodeDecrypt 门失败，未解密） |
| E2：CLI 打开 enc.hcp（加密 XML） | 文件被原地带内解密；属性装载通过；报 "打开文件：VarList.gdt 时出错" |
| proj1（文件丢 31 字节头） | 仍 VarList.gdt 报错 |
| proj2/proj3/proj5/proj6（全部文件含 31 字节头） | **打开成功**：title=`AutoShop V4.12.0.2 <dir> - [MAIN]`；proj3 的 11 个声明文件打开后全字节一致 |
| proj_v2（Version=2） | "所选工程文件版本高于当前程序，请先升级程序"（7597） |
| proj_nofile（无 `<file>` 元素） | "打开工程失败"（62220 / 错误码 4） |
| 注入桥编译触发 ×3 | `compileAll returned` 无异常；`Compile\SBR_001.compile`(480B) + `ecttrans.foid`(9B) 落盘 |

详细结论、调用链与接口说明见仓库 `docs/inovance-autoshop-open-and-compile.md`。
