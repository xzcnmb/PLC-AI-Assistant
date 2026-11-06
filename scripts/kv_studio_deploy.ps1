#Requires -Version 5.1
<#
.SYNOPSIS
  KEYENCE KV STUDIO 自动化部署: MNM 导入 -> 变量登记 -> 转换(编译) -> 保存落盘.
.DESCRIPTION
  供 C# MCP 服务 (plc_keyence_deploy_program) 调用。严格按 kv-import-playbook (A/E/B/C/D/F 节)
  实现; UIA 原语全部经 tools\kv-uia\kv-uia.exe (regex-scan / Invoke / Expand / SetValue / SendKeys),
  脚本不内嵌大段 UIA C#。安全默认: 不传 -AllowKvs 时绝不触碰真实 KV STUDIO, 只输出干跑.
.USAGE
  powershell -NoProfile -ExecutionPolicy Bypass -File kv_studio_deploy.ps1
    -ProjectDir "<.kpr 工程目录绝对路径>" -MnmPath "<.mnm 绝对路径>" -ModuleName "<模块名>"
    [-VariablesTsv "<TSV 绝对路径>"] [-KvsExe "<Kvs.exe>"] [-KvUia "<kv-uia.exe>"]
    [-WaitCompileSeconds 40] [-OutDir "<结果目录>"] [-DryRun] [-AllowKvs]
  -VariablesTsv: 本地变量表, 无表头, 每行 "名称<TAB>数据类型" (契约 = playbook C 节 2 列 NameType).
.EXITCODES
  0=全过 1=参数错 2=环境缺失(含 mutex 占用) 3=无法启动/绑定 KV STUDIO
  10=导入 FAIL 20=变量登记 FAIL 30=编译 NG 40=保存/落盘 FAIL 50=超时 99=致命异常
.OUTPUTS
  <OutDir>\kv-deploy-result.json (UTF-8, 契约见 spec), <OutDir>\kv-deploy-run.log (UTF-8 全转录),
  <OutDir>\compile_result_copied.txt (真实执行且抓取成功时).
  stdout 末行: "KV-DEPLOY-RESULT <exitcode> import=I variables=V compile=C persist=P".
.NOTES
  scriptVersion 1.0.0。已知 kv-uia 局限 (只按 Name 正则匹配, 不暴露 AutomationId/AccessKey/
  AcceleratorKey/焦点元素/右键坐标): 打开对话框文件名框走 ^a ^v {ENTER} 剪贴板兜底 (B 节 fallback);
  编译菜单 AcceleratorKey='Ctrl+F9' 校验不可达 -> %a 后直接发访问键 c (D 节 L3 注释: 不用 raw
  ^{F9}, 会被原生梯形编辑器吞掉; 不用 Invoke, 会同步阻塞在转换模态); 结果树 header 坐标按 editor
  底部 dock 启发 (D 节 header-left+80, top+12 的近似); 错误抓取唯一可靠路径 = WM_CONTEXTMENU +
  Name 恰为 复制(C) 的菜单项 Invoke + 剪贴板序列号验证.
#>
[CmdletBinding()]
param(
    [string]$ProjectDir,
    [string]$MnmPath,
    [string]$ModuleName,
    [string]$VariablesTsv,
    [string]$KvsExe,
    [string]$KvUia,
    [int]$WaitCompileSeconds = 40,
    [string]$OutDir,
    [switch]$DryRun,
    [switch]$AllowKvs
)

$ScriptVersion = '1.0.0'
$EXIT_OK = 0; $EXIT_PARAM = 1; $EXIT_ENV = 2; $EXIT_BIND = 3
$EXIT_IMPORT = 10; $EXIT_VARS = 20; $EXIT_COMPILE_NG = 30; $EXIT_PERSIST = 40
$EXIT_TIMEOUT = 50; $EXIT_FATAL = 99

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ---------------------------------------------------------------- state
$script:Log = New-Object System.Collections.Generic.List[string]
$script:Messages = New-Object System.Collections.Generic.List[string]
$script:GateImport = 'SKIP'; $script:GateVars = 'SKIP'; $script:GateCompile = 'SKIP'; $script:GatePersist = 'SKIP'
$script:Compile = [ordered]@{ state = ''; errorCount = 0; warningCount = 0; rawHeader = ''; errors = @(); rawText = '' }
$script:ImportDetail = [ordered]@{ moduleFoundInTree = $false; dialogs = (New-Object System.Collections.Generic.List[string]) }
$script:Persist = [ordered]@{ fingerprintBefore = ''; fingerprintAfter = ''; changed = $false }
$script:OutDirPath = $null
$script:ProjDirFull = $null
$script:MnmFull = $null
$script:DryRunEffective = [bool]$DryRun
$script:KvUiaPath = $null
$script:KvsExePath = $null
$script:Finished = $false
$script:MutexObj = $null
$script:KvsPid = 0
$script:EditorHwnd = [IntPtr]::Zero
$script:ProjectName = ''

function Write-Log([string]$msg) {
    $line = ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $msg)
    $script:Log.Add($line)
    Write-Host $line
    if ($script:OutDirPath) {
        try { [System.IO.File]::AppendAllText((Join-Path $script:OutDirPath 'kv-deploy-run.log'), $line + "`r`n", (New-Object System.Text.UTF8Encoding($false))) } catch { }
    }
}
function Add-Message([string]$msg) { $script:Messages.Add($msg); Write-Log ('MESSAGE ' + $msg) }

function Write-ResultFile([int]$code) {
    if (-not $script:OutDirPath) { return }
    $res = [ordered]@{
        scriptVersion = $ScriptVersion
        projectDir    = $script:ProjDirFull
        moduleName    = $ModuleName
        dryRun        = [bool]$script:DryRunEffective
        gates         = [ordered]@{ import = $script:GateImport; variables = $script:GateVars; compile = $script:GateCompile; persist = $script:GatePersist }
        compile       = $script:Compile
        importDetail  = $script:ImportDetail
        persist       = $script:Persist
        messages      = @($script:Messages)
    }
    $json = ConvertTo-Json -InputObject $res -Depth 8
    [System.IO.File]::WriteAllText((Join-Path $script:OutDirPath 'kv-deploy-result.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Finish([int]$code, [string]$reason) {
    if ($script:Finished) { return }
    $script:Finished = $true
    if ($reason) { Write-Log ('FINISH code=' + $code + ' reason=' + $reason) }
    try { Write-ResultFile $code } catch { Write-Log ('RESULT-WRITE-FAILED ' + $_.Exception.Message) }
    Write-Output ('KV-DEPLOY-RESULT {0} import={1} variables={2} compile={3} persist={4}' -f $code, $script:GateImport, $script:GateVars, $script:GateCompile, $script:GatePersist)
    exit $code
}

# ---------------------------------------------------------------- Win32 (compact)
Add-Type -Namespace KvDeploy -Name Win32 -MemberDefinition @'
    [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

# ---------------------------------------------------------------- kv-uia primitives
function Invoke-KvUia {
    param([string]$Pattern, [switch]$InvokeUia, [string]$Set, [string]$Keys, [int]$ProcessId)
    if ($Keys) {
        $a = @('.', '--keys', $Keys)
    } else {
        $a = @($Pattern)
        if ($InvokeUia) { $a += '--invoke' }
        if ($Set)       { $a += '--set'; $a += $Set }
        if ($ProcessId) { $a += '--pid'; $a += $ProcessId }
    }
    $out = (& $script:KvUiaPath @a 2>&1 | Out-String)
    Write-Log ('kv-uia ' + (($a | ForEach-Object { '"' + $_ + '"' }) -join ' ') + ' -> ' + ((@($out -split "`r?`n" | Where-Object { $_ -match 'printed=|INVOKED|EXPANDED|SET|KEYS-' }) -join ' | ')))
    return $out
}
function Send-Keys([string]$keys) { return (Invoke-KvUia -Keys $keys) }
function Test-UiaHit($out)   { return ($out -match 'printed=([1-9][0-9]*)') }
function Test-UiaActed($out) { return ($out -match 'INVOKED|EXPANDED|SET ') }
function Get-UiaNames($out)  { @([regex]::Matches($out, 'name=\[([^\]]*)\]') | ForEach-Object { $_.Groups[1].Value }) }
function Wait-Uia([string]$pattern, [double]$seconds, [int]$processId = 0) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $out = Invoke-KvUia -Pattern $pattern -ProcessId $processId
        if (Test-UiaHit $out) { return $out }
        Start-Sleep -Milliseconds 500
    }
    return $null
}
function Ensure-CapsLockOn() {
    # playbook D: 中文 IME 下加速键必须 Latin 模式; VK 0x14 bit0 = toggled-on
    for ($i = 0; $i -lt 3; $i++) {
        if (([KvDeploy.Win32]::GetKeyState(0x14) -band 1) -eq 1) { return }
        Send-Keys '{CAPSLOCK}' | Out-Null
        Start-Sleep -Milliseconds 200
    }
    Add-Message 'CapsLock 无法确认打开, 加速键可能被中文 IME 吞掉'
}

# ---------------------------------------------------------------- dialogs
$CONFIRM_RX = '^(OK|确定|是|Yes|はい)$'
$FAIL_RX    = '读取助记符列表失败|助记符.{0,12}失败|不正确.{0,12}助记符|机型不同|最大行数|程序名.{0,12}重复|结构体.{0,12}重复|不存在的指令'
function Dismiss-Dialogs([string]$tag, [int]$maxRounds = 6) {
    for ($i = 0; $i -lt $maxRounds; $i++) {
        $out = Invoke-KvUia -Pattern $CONFIRM_RX -ProcessId $script:KvsPid
        $btn = @(Get-UiaNames $out | Where-Object { $_ -match $CONFIRM_RX }) | Select-Object -First 1
        if (-not $btn) { break }
        $script:ImportDetail.dialogs.Add(('{0} [{1}]' -f $tag, $btn))
        Invoke-KvUia -Pattern ('^' + [regex]::Escape($btn) + '$') -ProcessId $script:KvsPid -InvokeUia | Out-Null
        Start-Sleep -Milliseconds 400
    }
}
function Test-FailureDialog([string]$tag) {
    $out = Invoke-KvUia -Pattern $FAIL_RX -ProcessId $script:KvsPid
    $names = @(Get-UiaNames $out | Where-Object { $_ -match $FAIL_RX })
    if ($names.Count -gt 0) {
        $txt = ($names -join ' / ')
        Write-Log ('FAIL-DIALOG(' + $tag + ') ' + $txt)
        Dismiss-Dialogs ('sweep-' + $tag) 12
        return $txt
    }
    return $null
}

# ---------------------------------------------------------------- process binding (A/E)
function Find-KvsProcesses([string]$kprPath) {
    $needle = [regex]::Escape($kprPath)
    @(Get-CimInstance Win32_Process -Filter "Name='Kvs.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -match ('(?i)(?:^|\s)"?' + $needle + '"?(?:\s|$)') })
}
function Get-EditorHwnd($proc) {
    if ($null -eq $proc -or $proc.MainWindowHandle -eq 0) { return [IntPtr]::Zero }
    $t = $proc.MainWindowTitle
    if ($t -match '模拟器') { return [IntPtr]::Zero }            # 拒绝模拟器 (D 节步骤2)
    # 实机: KV STUDIO 单实例恢复旧工程名 (标题 QA_BlankProject 但内容为目标工程),
    # Bind-Kvs 已用 UIA 项目树锚点确认工程就绪; 此处标题不符不再拒绝 (防聚焦失败)。
    # 仍拒绝明确的模拟器标题; 若未来需要更强校验可在调用方加 ProjectTreeView 锚点二次确认。
    if ($t -like '*KV STUDIO*') { return $proc.MainWindowHandle }
    [IntPtr]::Zero
}
function Bind-Kvs([string]$kprPath) {
    $procs = @(Find-KvsProcesses $kprPath)   # 函数返回会被 unroll, 必须强制数组 (PowerShell 经典坑)
    if ($procs.Count -gt 1) {
        Finish $EXIT_BIND ('KV_COMPILE_PROJECT_PROCESS_AMBIGUOUS: ' + $procs.Count + ' 个 Kvs.exe 命令行含同一工程')
        return
    }
    if ($procs.Count -eq 0) {
        Write-Log ('启动 KV STUDIO: ' + $script:KvsExePath + ' "' + $kprPath + '"')
        Start-Process -FilePath $script:KvsExePath -ArgumentList ('"' + $kprPath + '"') | Out-Null
        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            $procs = @(Find-KvsProcesses $kprPath)   # 强制数组, 防 unroll
            if ($procs.Count -eq 1) {
                $p0 = Get-Process -Id $procs[0].ProcessId -ErrorAction SilentlyContinue
                if ($p0 -and $p0.MainWindowHandle -ne 0) { break }   # 窗口就绪即可; 标题/工程锚点校验在绑定后统一做
            } elseif ($procs.Count -gt 1) {
                Finish $EXIT_BIND 'KV_COMPILE_PROJECT_PROCESS_AMBIGUOUS (启动后出现多进程)'
                return
            }
            $procs = @()
        }
        if ($procs.Count -eq 0) { Finish $EXIT_BIND 'KV_COMPILE_PROJECT_PROCESS_NOT_FOUND (启动后 90s 内无带该工程命令行且窗口就绪的 Kvs.exe)'; return }
    }
    $proc = Get-Process -Id $procs[0].ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $proc) { Finish $EXIT_BIND '绑定进程已退出'; return }
    $script:KvsPid = $proc.Id
    if ($proc.MainWindowTitle -notlike ('*' + $script:ProjectName + '*')) {
        # 实机发现: KV STUDIO 单实例打开 .kpr 后窗口标题可能沿用旧工程名
        # (副本首启显示 QA_BlankProject 但项目树实际已是目标工程)。此处用 UIA
        # 项目树语义锚点兜底: 顶层窗口内能找到目标模块名/常用模块节点即视为工程已就绪,
        # 避免把"标题没同步但内容正确"误判为绑定失败。兜底仍失败才 fail fast。
        Write-Log ('标题不含工程名, 尝试 UIA 项目树锚点兜底: [' + $proc.MainWindowTitle + '] needle=' + $script:ProjectName)
        $anchorHit = $false
        # 项目树模块名来自 MNM header; 用目标模块名或通用模块锚点扫描。
        $treeScan = Invoke-KvUia -Pattern ('Main|' + [regex]::Escape($script:ModuleName)) -ProcessId $script:KvsPid
        if (Test-UiaHit $treeScan) {
            $anchorHit = $true
            Write-Log ('UIA 项目树锚点命中: ' + ($treeScan -split "`n")[0])
        }
        if (-not $anchorHit) {
            Finish $EXIT_BIND ('标题不含工程名且项目树锚点未命中, fail fast: [' + $proc.MainWindowTitle + '] needle=' + $script:ProjectName)
            return
        }
    }
    $script:EditorHwnd = Get-EditorHwnd $proc
    Write-Log ('绑定 pid=' + $script:KvsPid + ' editorHwnd=0x' + ('{0:X}' -f [long]$script:EditorHwnd) + ' title=[' + $proc.MainWindowTitle + ']')
    Add-Message ('已绑定 KV STUDIO pid=' + $script:KvsPid)
}
function Focus-Editor() {
    if ($script:EditorHwnd -eq [IntPtr]::Zero) {
        $p = Get-Process -Id $script:KvsPid -ErrorAction SilentlyContinue
        if ($p) { $script:EditorHwnd = Get-EditorHwnd $p }
    }
    if ($script:EditorHwnd -eq [IntPtr]::Zero) { return $false }
    for ($i = 0; $i -lt 10; $i++) {
        [void][KvDeploy.Win32]::ShowWindow($script:EditorHwnd, 3)      # SW_MAXIMIZE
        [void][KvDeploy.Win32]::SetForegroundWindow($script:EditorHwnd)
        Start-Sleep -Milliseconds 300
        $p = Get-Process -Id $script:KvsPid -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowTitle -like ('*KV STUDIO*' + $script:ProjectName + '*') -and $p.MainWindowTitle -notmatch '模拟器') { return $true }
    }
    $false
}

# ---------------------------------------------------------------- MNM / TSV validation
function Test-MnmHeader([string]$path) {
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::Unicode)   # UTF-16LE+BOM (KeyenceMnmGenerator 契约)
    $have = [ordered]@{ 'DEVICE:' = $false; ';MODULE:' = $false; ';MODULE_TYPE:' = $false; 'END' = $false; 'ENDH' = $false; 'AREA_ST' = $false }
    $headerModule = ''; $moduleType = ''
    foreach ($l in ($text -split "`r?`n")) {
        $t = $l.Trim()
        if ($t -like 'DEVICE:*') { $have['DEVICE:'] = $true }
        if ($t -match '^;MODULE:(.+)$') { $have[';MODULE:'] = $true; $headerModule = $Matches[1].Trim() }
        if ($t -match '^;MODULE_TYPE:(\d+)') { $have[';MODULE_TYPE:'] = $true; $moduleType = $Matches[1] }
        if ($t -eq 'AREA_ST') { $have['AREA_ST'] = $true }
        if ($t -eq 'END') { $have['END'] = $true }
        if ($t -eq 'ENDH') { $have['ENDH'] = $true }
    }
    $missing = @($have.Keys | Where-Object { $_ -ne 'AREA_ST' -and -not $have[$_] })
    if ($missing.Count -gt 0) { return @{ ok = $false; why = ('缺 header 项: ' + ($missing -join ',')); module = $headerModule; type = $moduleType } }
    if (-not $have['AREA_ST']) { Add-Message 'MNM 无 AREA_ST 段 (ST 嵌入缺失, 提示)' }
    @{ ok = $true; why = ''; module = $headerModule; type = $moduleType }
}
function Read-VariablesTsv([string]$path) {
    $raw = @([System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) -split "`r?`n" | Where-Object { $_.Trim() -ne '' })
    $rows = @()
    foreach ($l in $raw) {
        if ($l.Contains([string][char]0xFFFD)) { return @{ ok = $false; why = ('TSV 含 U+FFFD 乱码 (mojibake guard): ' + $l); rows = @() } }
        $c = $l -split "`t"
        if ($c.Count -lt 2) { return @{ ok = $false; why = ('TSV 行非 2 列 名称<TAB>数据类型: ' + $l); rows = @() } }
        $rows += ,@($c[0].Trim(), $c[1].Trim())
    }
    if ($rows.Count -eq 0) { return @{ ok = $false; why = 'TSV 为空'; rows = @() } }
    @{ ok = $true; why = ''; rows = $rows }
}

# ---------------------------------------------------------------- fingerprint (gate 5)
function Get-ManifestFingerprint([string]$dir) {
    $files = @(Get-ChildItem $dir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'All.prgx' -or $_.Name -eq 'WsTreeEnv.xml' -or $_.Extension -eq '.kstx' -or $_.Extension -eq '.mnm' } |
        Sort-Object Name)
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in $files) { [void]$sb.AppendLine(('{0}|{1}|{2:o}' -f $f.Name, $f.Length, $f.LastWriteTimeUtc)) }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    ((($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($sb.ToString()))) | ForEach-Object { $_.ToString('x2') }) -join '')
}

# ---------------------------------------------------------------- compile parse (D/L10)
function Parse-CompileText([string]$text) {
    $state = ''; $header = ''; $ec = -1; $wc = -1
    $errs = New-Object System.Collections.Generic.List[object]
    foreach ($l in ($text -split "`r?`n")) {
        # 注意: 内层 -match 会覆写 $Matches, 外层字段必须先取出再用
        if ($l -match '转换结果\s*(?<s>OK|NG)\s*(?:\(错误数量:(?<e>\d+)\s*警告数量:(?<w>\d+)\))?') {
            $hs = $Matches.s; $he = $Matches.e; $hw = $Matches.w
            if (-not $state) { $state = $hs; $header = $l.Trim() }
            if ($he) { $ec = [int]$he }
            if ($hw) { $wc = [int]$hw }
        }
        if ($l -match '^(?<module>[^(!]+?)\(行:?(?<line>\d+)\)(?:\(ST行:?\s*(?<stline>\d+)\))?\[(?<kind>错误|警告)\s*(?<code>\d+)\](：|:)?(?<rest>.*)$') {
            $m = $Matches.module; $ln = $Matches.line; $sk = $Matches.stline; $kd = $Matches.kind; $cd = $Matches.code; $rs = $Matches.rest
            $sym = ''
            if ($rs -match '\[([^\]]*)\]\s*$') { $sym = $Matches[1] }
            $st = if ($sk) { [int]$sk } else { $null }
            $errs.Add([ordered]@{ module = $m.Trim(); line = [int]$ln; stLine = $st; code = [int]$cd; kind = $kd; message = $rs.Trim(); symbol = $sym })
            continue
        }
        if ($l -match '^\s*\[(?<kind>错误|警告)\s*(?<code>\d+)\](：|:)?(?<rest>.*)$') {
            $kd = $Matches.kind; $cd = $Matches.code; $rs = $Matches.rest
            $sym = ''
            if ($rs -match '\[([^\]]*)\]\s*$') { $sym = $Matches[1] }
            $errs.Add([ordered]@{ module = ''; line = 0; stLine = $null; code = [int]$cd; kind = $kd; message = $rs.Trim(); symbol = $sym })
        }
    }
    $nerr = @($errs | Where-Object { $_.kind -eq '错误' }).Count
    $nwarn = @($errs | Where-Object { $_.kind -eq '警告' }).Count
    $ecFinal = $nerr; if ($ec -ge 0) { $ecFinal = $ec }
    $wcFinal = $nwarn; if ($wc -ge 0) { $wcFinal = $wc }
    $errArr = @($errs | ForEach-Object { $_ })   # PS5.1 坑: List[object] 直接 @($list) 抛 ArgumentException, 走管道枚举
    @{ state = $state; header = $header; errorCount = $ecFinal; warningCount = $wcFinal; errors = $errArr }
}

# ---------------------------------------------------------------- gate 2: import (B)
function Invoke-ImportGate() {
    Ensure-CapsLockOn
    $dlgOut = $null
    # 路线1: 加速键 %frr (Alt+F,R,R), <=8s 出现标准打开对话框 (#32770, 标题恰为 打开/Open)
    Write-Log '导入路线1: SendKeys %frr'
    Send-Keys '%frr' | Out-Null
    $dlgOut = Wait-Uia '^(打开|Open)$' 8 $script:KvsPid
    if (-not $dlgOut) {
        # 路线2: UIA 结构链 文件>助记符列表>读取(R)... (拒绝坐标点击——B 节: 会误开梯形在线编辑器)
        Write-Log '导入路线2: UIA 菜单链'
        Send-Keys '{ESC}' | Out-Null
        Invoke-KvUia -Pattern '^文件' -ProcessId $script:KvsPid -InvokeUia | Out-Null
        Start-Sleep -Milliseconds 600
        Invoke-KvUia -Pattern '助记符|列表' -ProcessId $script:KvsPid -InvokeUia | Out-Null
        Start-Sleep -Milliseconds 600
        $rd = Invoke-KvUia -Pattern '^读取\(R\)' -ProcessId $script:KvsPid -InvokeUia
        if (-not (Test-UiaActed $rd)) {
            Start-Sleep -Milliseconds 2500
            Invoke-KvUia -Pattern '^读取\(R\)' -ProcessId $script:KvsPid -InvokeUia | Out-Null   # command-router handoff 重试一次
        }
        $dlgOut = Wait-Uia '^(打开|Open)$' 8 $script:KvsPid
    }
    if (-not $dlgOut) {
        $why = Test-FailureDialog 'pre-open'
        Finish $EXIT_IMPORT ('导入: 打开对话框未出现' + $(if ($why) { '; 失败弹窗: ' + $why } else { '' }))
        return
    }
    # 文件名: 首选 AutomationId=1148 SetValue (kv-uia 只按 Name -> 试 文件名* Edit), 兜底 对话框聚焦 + ^a ^v {ENTER}
    Set-Clipboard $script:MnmFull
    $setOut = Invoke-KvUia -Pattern '^文件名' -ProcessId $script:KvsPid -Set $script:MnmFull
    if ($setOut -match 'SET ') {
        Send-Keys '{ENTER}' | Out-Null
    } else {
        Send-Keys '^a' | Out-Null
        Send-Keys '^v' | Out-Null
        Send-Keys '{ENTER}' | Out-Null
    }
    # 实测(2026-11-06, KV STUDIO 12.42 中文): {ENTER} 打开后必现「选择程序种类」Window
    # (ComboBox 默认 = 每次扫描执行型模块 for MODULE_TYPE:0; 点 OK), 随后才出现「选择程序(M)」面板
    # 与「插入」按钮。漏掉它会让模块永远不进树 (此前把该窗口当普通确认弹窗扫掉)。
    $kindDlg = Wait-Uia '^选择程序种类$' 8 $script:KvsPid
    if ($kindDlg) {
        Write-Log '选择程序种类 窗口出现, 点 OK (ComboBox 默认每次扫描执行型模块)'
        $script:ImportDetail.dialogs.Add('kind-dialog [OK]')
        Invoke-KvUia -Pattern '^OK$' -ProcessId $script:KvsPid -InvokeUia | Out-Null
        Start-Sleep -Seconds 2
        $why = Test-FailureDialog 'kind-ok'
        if ($why) { Finish $EXIT_IMPORT ('导入失败弹窗(选择程序种类OK后): ' + $why); return }
    }
    # 「选择程序(M)」面板: 先等面板(带插入按钮), 再点插入——不能直接^覆盖/插入(会撞工具栏按钮)
    $selPanel = Wait-Uia '^选择程序\(M\)$' 10 $script:KvsPid
    if ($selPanel) {
        Write-Log '选择程序(M) 面板出现, 点 [插入]'
        $script:ImportDetail.dialogs.Add('select-program-panel [插入]')
        $ins = Invoke-KvUia -Pattern '^插入$' -ProcessId $script:KvsPid -InvokeUia
        if (-not (Test-UiaActed $ins)) { Finish $EXIT_IMPORT '导入: 选择程序面板 [插入] 点击失败'; return }
        Start-Sleep -Seconds 2
    } else {
        # 兼容旧流程: 可能直接出 覆盖/插入 面板 (老版本/不同机型)
        $panel2 = $null
        $deadline2 = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline2) {
            Start-Sleep -Milliseconds 700
            $panel2 = Invoke-KvUia -Pattern '^(覆盖|全部覆盖|插入)' -ProcessId $script:KvsPid
            if (Test-UiaHit $panel2) { break }
        }
        if ($panel2) {
            $btnName2 = @(Get-UiaNames $panel2 | Where-Object { $_ -match '^(全部覆盖|覆盖)' }) | Select-Object -First 1
            if (-not $btnName2) { $btnName2 = @(Get-UiaNames $panel2 | Where-Object { $_ -match '^插入' }) | Select-Object -First 1 }
            $script:ImportDetail.dialogs.Add('panel [' + $btnName2 + ']')
            Invoke-KvUia -Pattern ('^' + [regex]::Escape($btnName2)) -ProcessId $script:KvsPid -InvokeUia | Out-Null
            Start-Sleep -Seconds 2
        } else {
            Finish $EXIT_IMPORT '导入: 选择程序(M) 面板与覆盖/插入面板均未出现'
            return
        }
    }
    Dismiss-Dialogs 'confirm' 10          # OK/确定/是/はい 清扫
    $why = Test-FailureDialog 'post-insert'
    if ($why) { Finish $EXIT_IMPORT ('导入失败弹窗(插入后): ' + $why); return }
    Dismiss-Dialogs 'sweep-instruction' 10   # 不存在的指令 循环清扫 (S1 DismissInstructionErrorDialogs)
    # 工程树验证: 命名 ^Name(?::显示名)?(?: \[\d+\])?$; 禁 -like '*[*' (L12); 折叠文件夹先 Expand
    $treeRx = '^' + [regex]::Escape($ModuleName) + '(?::.{0,60})?(?: \[\d+\])?$'
    $found = Wait-Uia $treeRx 6 $script:KvsPid
    if (-not $found) {
        Invoke-KvUia -Pattern '^程序\(显示执行顺序\)' -ProcessId $script:KvsPid -InvokeUia | Out-Null
        Start-Sleep -Milliseconds 500
        Invoke-KvUia -Pattern '^每次扫描执行型模块' -ProcessId $script:KvsPid -InvokeUia | Out-Null
        Start-Sleep -Milliseconds 500
        Invoke-KvUia -Pattern '^功能块' -ProcessId $script:KvsPid -InvokeUia | Out-Null
        Start-Sleep -Milliseconds 500
        $found = Wait-Uia $treeRx 6 $script:KvsPid
    }
    if ($found) {
        $script:ImportDetail.moduleFoundInTree = $true
        $script:GateImport = 'PASS'
        Add-Message ('导入 PASS: 工程树中找到 ' + $ModuleName)
    } else {
        Finish $EXIT_IMPORT ('导入后工程树未找到模块 ' + $ModuleName + ' (moduleFoundInTree=false)')
    }
}

# ---------------------------------------------------------------- gate 3: variables (C)
function Invoke-VariablesGate($rows) {
    # 打开变量编辑器: 视图(V) -> 变量编辑(L)... (Alt+V, l); 验证 KvVariableForm (标题 *变量编辑*)
    Send-Keys '%v' | Out-Null
    Start-Sleep -Milliseconds 600
    Send-Keys 'l' | Out-Null
    $form = Wait-Uia '变量编辑' 15 $script:KvsPid
    if (-not $form) { Finish $EXIT_VARS '变量编辑器窗口 (KvVariableForm) 未出现'; return }
    # 局部页 (TabItem 局部/_tabPageLocal)
    $tab = Invoke-KvUia -Pattern '^局部$' -ProcessId $script:KvsPid -InvokeUia
    if (-not (Test-UiaActed $tab)) { Invoke-KvUia -Pattern '局部' -ProcessId $script:KvsPid -InvokeUia | Out-Null }
    Start-Sleep -Milliseconds 600
    # 模块选择 (_comboBoxModuleName): 选目标 -> 回车. kv-uia 无 AutomationId/SelectionItem, 首击
    # 可能命中树项而非 combo; combo 现值==目标时 Name 命中即选中, 最终以回读/编译 214 判据兜底.
    $mod = Invoke-KvUia -Pattern ('^' + [regex]::Escape($ModuleName) + '$') -ProcessId $script:KvsPid -InvokeUia
    if (Test-UiaHit $mod) {
        Invoke-KvUia -Pattern ('^' + [regex]::Escape($ModuleName) + '$') -ProcessId $script:KvsPid -InvokeUia | Out-Null
        Send-Keys '{ENTER}' | Out-Null
    } else {
        Add-Message '警告: 未扫到目标模块名 (下拉当前值), 依赖后续回读验证'
    }
    Start-Sleep -Milliseconds 400
    # 网格聚焦锚定 (C 节验证路径): %l 过滤框 -> +{TAB} x<=3 -> Ctrl+Home. UIA SetFocus/点击 _grid
    # 是诊断兜底 (可能弹在线编辑器污染粘贴目标) -> 不点击. owner-drawn: 绝不 ValuePattern 写格.
    Send-Keys '%l' | Out-Null
    Start-Sleep -Milliseconds 300
    Send-Keys '+{TAB}' | Out-Null
    Send-Keys '+{TAB}' | Out-Null
    Send-Keys '+{TAB}' | Out-Null
    Send-Keys '^{HOME}' | Out-Null
    # 2 列 TSV 粘贴
    $tsv = ($rows | ForEach-Object { $_ -join "`t" }) -join "`r`n"
    Set-Clipboard $tsv
    Send-Keys '^v' | Out-Null
    # 粘贴确认弹窗: _buttonOverwriteAll / 变量名被更改,要覆盖吗? (取消/覆盖/全部覆盖) -> 全部覆盖
    $oaName = $null
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline -and -not $oaName) {
        $oa = Invoke-KvUia -Pattern '全部覆盖' -ProcessId $script:KvsPid
        $oaName = @(Get-UiaNames $oa | Where-Object { $_ -match '全部覆盖' }) | Select-Object -First 1
        if (-not $oaName) { Start-Sleep -Milliseconds 600 }
    }
    if ($oaName) {
        $script:ImportDetail.dialogs.Add('paste [' + $oaName + ']')
        Invoke-KvUia -Pattern ('^' + [regex]::Escape($oaName) + '$') -ProcessId $script:KvsPid -InvokeUia | Out-Null
    } else {
        $ov = Invoke-KvUia -Pattern '^覆盖' -ProcessId $script:KvsPid -InvokeUia
        if (-not (Test-UiaActed $ov)) { Add-Message '未出现粘贴覆盖确认弹窗 (可能 KV 直接接受)' }
    }
    Start-Sleep -Milliseconds 500
    $skipWarn = Invoke-KvUia -Pattern '粘贴数据中存在错误' -ProcessId $script:KvsPid
    if (Test-UiaHit $skipWarn) { Send-Keys '{ENTER}' | Out-Null; Add-Message '警告: 粘贴数据中存在错误(部分跳过), 由回读判据兜住' }
    Dismiss-Dialogs 'var-confirm' 8
    # 回读验证 (必须): ^a ^c, 剪贴板序列号变化 + 首两列含目标变量
    $prevSeq = [KvDeploy.Win32]::GetClipboardSequenceNumber()
    Send-Keys '^a' | Out-Null
    Send-Keys '^c' | Out-Null
    $clip = $null
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        if ([KvDeploy.Win32]::GetClipboardSequenceNumber() -ne $prevSeq) {
            $t = (Get-Clipboard) -join "`r`n"
            if ($t) { $clip = $t; if ($t -ne $tsv) { break } }
        }
    }
    if (-not $clip -or $clip -match '向剪贴板复制失败') {
        $why = Test-FailureDialog 'var-readback'
        Finish $EXIT_VARS ('回读剪贴板失败 (网格焦点未证实 / owner-drawn 复制失败弹窗)' + $(if ($why) { '; ' + $why } else { '' }))
        return
    }
    $missing = @()
    foreach ($r in $rows) {
        $okRow = @(($clip -split "`r?`n") | Where-Object { $c = $_ -split "`t"; ($c.Count -ge 2 -and $c[0].Trim() -eq $r[0] -and $c[1].Trim() -match ('(?i)^' + [regex]::Escape($r[1]) + '(\b|$)')) })
        if ($okRow.Count -eq 0) { $missing += ($r -join '=') }
    }
    if ($missing.Count -gt 0) {
        [System.IO.File]::WriteAllText((Join-Path $script:OutDirPath 'variables_readback.txt'), $clip, (New-Object System.Text.UTF8Encoding($false)))
        Finish $EXIT_VARS ('变量回读缺失: ' + ($missing -join ', ') + ' (详见 variables_readback.txt)')
        return
    }
    Send-Keys '^{F4}' | Out-Null          # 关变量编辑器 (落盘统一在关5)
    Start-Sleep -Milliseconds 600
    Dismiss-Dialogs 'var-close' 6
    $script:GateVars = 'PASS'
    Add-Message ('变量登记 PASS: ' + $rows.Count + ' 行回读一致')
}

# ---------------------------------------------------------------- gate 4: compile (D)
function Invoke-CompileGate() {
    # 1. 关阻断弹窗 (变量窗 / 残留 转换结果)
    $stale = Invoke-KvUia -Pattern '转换结果' -ProcessId $script:KvsPid
    if (Test-UiaHit $stale) { Dismiss-Dialogs 'stale-convert' 6; Send-Keys '{ENTER}' | Out-Null }
    $varForm = Invoke-KvUia -Pattern '变量编辑' -ProcessId $script:KvsPid
    if (Test-UiaHit $varForm) { Send-Keys '^{F4}' | Out-Null; Dismiss-Dialogs 'stale-varform' 6 }
    # 2. 聚焦 editor (标题 KV STUDIO*<工程>*, 拒绝 模拟器)
    if (-not (Focus-Editor)) { Finish $EXIT_BIND '编译前无法聚焦编辑器窗口 (或处于模拟器)'; return }
    Ensure-CapsLockOn
    # 3. 转换(A) 菜单 -> 访问键 c (=Ctrl+F9 项). pad 注: L3 要求先校验 MenuItem
    #    AcceleratorKey='Ctrl+F9' + GetWindow(GW_HWNDPOPUP) popup 存在再发 c; kv-uia 不暴露
    #    AcceleratorKey/popup HWND -> 按 F 节配方 12 直接 %a+c. raw ^{F9} 会被原生梯形编辑器吞
    #    (L3:365), UIA Invoke 会阻塞在同步转换模态 (L3:157) -> 均不用.
    Send-Keys '%a' | Out-Null
    Start-Sleep -Milliseconds 700
    Send-Keys 'c' | Out-Null
    # 4. 等 转换结果 弹窗 (owner-drawn, 身份即接受) <=WaitCompileSeconds
    $done = Wait-Uia '转换结果|转换成功|转换失败' ([double]$WaitCompileSeconds) $script:KvsPid
    if (-not $done) { Finish $EXIT_TIMEOUT ('编译结果 ' + $WaitCompileSeconds + 's 内未出现 (转换结果/outputTreeControl1 均不可证)'); return }
    $hint = @(Get-UiaNames $done | Where-Object { $_ -match '转换成功|转换失败|错误数量' }) -join ' '
    # 关模态 (转换失败。/成功。按 {ENTER}, L3:287; 再扫确认按钮)
    Send-Keys '{ENTER}' | Out-Null
    Start-Sleep -Milliseconds 600
    Dismiss-Dialogs 'convert-modal' 4
    # 5. 错误抓取唯一可靠路径: WM_CONTEXTMENU 结果树 + Name 恰为 复制(C) 的菜单项 Invoke + 剪贴板序列
    $raw = ''
    if ($script:EditorHwnd -ne [IntPtr]::Zero) {
        $rect = New-Object 'KvDeploy.Win32+RECT'
        [void][KvDeploy.Win32]::GetWindowRect($script:EditorHwnd, [ref]$rect)
        # D 节 (header-left+80, top+12) 针对结果树 dock 头; kv-uia 无 AutomationId 匹配 ->
        # 启发: editor 底部 dock 区 (left+80, bottom-120)
        $cx = $rect.Left + 80
        $cy = [Math]::Max($rect.Top + 12, $rect.Bottom - 120)
        $sentinel = 'KV-COMPILE-SENTINEL-' + (Get-Date).Ticks
        Set-Clipboard $sentinel
        $lParam = [IntPtr]((($cy -band 0xFFFF) * 65536) + ($cx -band 0xFFFF))
        [void][KvDeploy.Win32]::PostMessage($script:EditorHwnd, 0x007B, $script:EditorHwnd, $lParam)   # WM_CONTEXTMENU
        Start-Sleep -Milliseconds 700
        $copy = Invoke-KvUia -Pattern '^复制\(C\)$' -ProcessId $script:KvsPid -InvokeUia
        if (Test-UiaActed $copy) {
            $deadline = (Get-Date).AddSeconds(3)
            while ((Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 250
                $t = (Get-Clipboard) -join "`r`n"
                if ($t -and $t -ne $sentinel) { $raw = $t; break }
            }
        }
        Send-Keys '{ESC}' | Out-Null   # 收掉残留上下文菜单
    }
    if ($raw -notmatch '^转换结果\s*(OK|NG)') {
        # 兜底: 弹窗文本启发 (L4:152: 成功弹窗合成 转换结果 OK)
        if ($hint -match '转换失败') { $raw = "转换结果 NG`r`n" + $hint }
        elseif ($hint -match '转换成功') { $raw = "转换结果 OK`r`n" + $hint }
        elseif ($raw) { }
        else { Finish $EXIT_TIMEOUT '编译结果无法抓取 (复制(C) 菜单/剪贴板/弹窗启发均失败)'; return }
    }
    [System.IO.File]::WriteAllText((Join-Path $script:OutDirPath 'compile_result_copied.txt'), $raw, (New-Object System.Text.UTF8Encoding($false)))
    $script:Compile.rawText = $raw
    $parsed = Parse-CompileText $raw
    $script:Compile.state = $parsed.state
    $script:Compile.rawHeader = $parsed.header
    $script:Compile.errorCount = $parsed.errorCount
    $script:Compile.warningCount = $parsed.warningCount
    $script:Compile.errors = $parsed.errors
    if ($parsed.state -eq 'OK') {
        $script:GateCompile = 'PASS'
        Add-Message ('编译 OK (错误 ' + $parsed.errorCount + ' 警告 ' + $parsed.warningCount + ')')
    } elseif ($parsed.state -eq 'NG') {
        $script:GateCompile = 'FAIL'
        Add-Message ('编译 NG: 错误数=' + $parsed.errorCount + ' 首条=' + $(if ($parsed.errors.Count) { ($parsed.errors[0] | ConvertTo-Json -Compress) } else { '无明细' }))
        Dismiss-Dialogs 'post-ng' 8
        Finish $EXIT_COMPILE_NG '转换结果 NG'
    } else {
        Finish $EXIT_TIMEOUT '转换结果标题解析失败 (剪贴板文本不含 转换结果 OK|NG)'
    }
}

# ---------------------------------------------------------------- gate 5: persist
function Invoke-PersistGate() {
    Ensure-CapsLockOn
    [void](Focus-Editor)
    $script:Persist.fingerprintBefore = Get-ManifestFingerprint $script:ProjDirFull
    Send-Keys '^s' | Out-Null
    # <=20s 等标题脏标记消失 (canonical: ^KV STUDIO.* - \[<proj>( \*)?\]$, A5)
    $clean = $false
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $p = Get-Process -Id $script:KvsPid -ErrorAction SilentlyContinue
        if (-not $p) { break }
        $t = $p.MainWindowTitle
        if ($t -match ('^KV STUDIO.* - \[' + [regex]::Escape($script:ProjectName) + '\]$')) { $clean = $true; break }
        if ($t -like ('*' + $script:ProjectName + '*') -and $t.IndexOf($script:ProjectName) -ge 0 -and -not $t.Contains($script:ProjectName + ' *')) { $clean = $true; break }
        Dismiss-Dialogs 'save-confirm' 3
    }
    Start-Sleep -Seconds 1
    $script:Persist.fingerprintAfter = Get-ManifestFingerprint $script:ProjDirFull
    $script:Persist.changed = ($script:Persist.fingerprintBefore -ne $script:Persist.fingerprintAfter)
    if (-not $clean -and -not $script:Persist.changed) {
        Finish $EXIT_PERSIST '保存失败: 标题脏标记未消失且关键文件指纹未变'
        return
    }
    if (-not $script:Persist.changed) {
        Add-Message '注意: 保存后关键文件指纹未变化 (脏标记已清除; 如实报 persist.changed=false)'
    }
    $script:GatePersist = 'PASS'
    Add-Message ('落盘 PASS changed=' + $script:Persist.changed + ' dirtyCleared=' + $clean)
}

# ================================================================ main
try {
    # ---------------- 0. OutDir 先行 (保证参数错也落 result.json)
    if (-not $OutDir) { $OutDir = Join-Path $env:TEMP 'kv-deploy' }
    $script:OutDirPath = $OutDir
    New-Item -ItemType Directory -Force -Path $script:OutDirPath | Out-Null
    Write-Log ('kv_studio_deploy.ps1 v' + $ScriptVersion + ' 启动; OutDir=' + $script:OutDirPath)

    # ---------------- 1. 参数校验 (exit 1)
    if (-not $ProjectDir -or -not $MnmPath -or -not $ModuleName) { Finish $EXIT_PARAM '参数缺失: ProjectDir/MnmPath/ModuleName 必填'; return }
    if (-not [System.IO.Path]::IsPathRooted($ProjectDir) -or -not [System.IO.Path]::IsPathRooted($MnmPath)) { Finish $EXIT_PARAM 'ProjectDir/MnmPath 必须为绝对路径'; return }
    if (-not (Test-Path -LiteralPath $ProjectDir -PathType Container)) { Finish $EXIT_PARAM ('ProjectDir 不存在: ' + $ProjectDir); return }
    if (-not (Test-Path -LiteralPath $MnmPath -PathType Leaf)) { Finish $EXIT_PARAM ('MnmPath 不存在: ' + $MnmPath); return }
    if ($ModuleName -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,47}$') { Finish $EXIT_PARAM ('模块名非法 (KV 规则): ' + $ModuleName); return }
    if ($WaitCompileSeconds -le 0) { Finish $EXIT_PARAM 'WaitCompileSeconds 必须 > 0'; return }
    $script:ProjDirFull = (Resolve-Path -LiteralPath $ProjectDir).Path
    $script:MnmFull = (Resolve-Path -LiteralPath $MnmPath).Path

    # ---------------- 2. 环境校验 (exit 1/2)
    if (-not $KvUia) {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $KvUia = Join-Path $repoRoot 'tools\kv-uia\bin\Release\net48\kv-uia.exe'
    }
    if (-not (Test-Path -LiteralPath $KvUia -PathType Leaf)) { Finish $EXIT_ENV ('kv-uia.exe 不存在: ' + $KvUia); return }
    $script:KvUiaPath = (Resolve-Path -LiteralPath $KvUia).Path
    if (-not $KvsExe) { $KvsExe = 'C:\Program Files (x86)\KEYENCE\KVS12G\KVS12\KVS\Kvs.exe' }   # A 节默认安装路径
    $script:KvsExePath = $KvsExe
    if (-not (Test-Path -LiteralPath $script:KvsExePath -PathType Leaf)) { Finish $EXIT_ENV ('Kvs.exe 不存在: ' + $script:KvsExePath); return }
    $kpr = @(Get-ChildItem $script:ProjDirFull -Filter *.kpr -File | Sort-Object Length -Descending)
    if ($kpr.Count -eq 0) { Finish $EXIT_ENV ('工程目录内无 .kpr: ' + $script:ProjDirFull); return }
    if ($kpr.Count -gt 1) { Add-Message ('目录含多个 .kpr, 取最大者: ' + $kpr[0].Name) }
    $kprPath = $kpr[0].FullName
    $script:ProjectName = [System.IO.Path]::GetFileNameWithoutExtension($kpr[0].Name)
    Write-Log ('工程: ' + $kprPath)

    $hdr = Test-MnmHeader $script:MnmFull
    if (-not $hdr.ok) { Finish $EXIT_PARAM ('MNM header 校验失败: ' + $hdr.why); return }
    Write-Log ('MNM header OK: MODULE=' + $hdr.module + ' MODULE_TYPE=' + $hdr.type)
    if ($hdr.module -ne $ModuleName) {
        Add-Message ('提示: MNM 内 MODULE=' + $hdr.module + ' != -ModuleName ' + $ModuleName + '; KV 按 header 放置 (B 节), 树验证用 -ModuleName')
    }
    $varRows = $null
    if ($VariablesTsv) {
        if (-not (Test-Path -LiteralPath $VariablesTsv -PathType Leaf)) { Finish $EXIT_PARAM ('VariablesTsv 不存在: ' + $VariablesTsv); return }
        $vr = Read-VariablesTsv $VariablesTsv
        if (-not $vr.ok) { Finish $EXIT_PARAM ('VariablesTsv 校验失败: ' + $vr.why); return }
        $varRows = $vr.rows
    }

    # ---------------- 3. dry-run 判定 (安全第一: 无 -AllowKvs 一律干跑)
    if ($DryRun) {
        Add-Message 'DryRun: 仅做环境/参数校验, 不触碰 KV STUDIO'
    } elseif (-not $AllowKvs) {
        $script:DryRunEffective = $true
        Add-Message '未授权真实操作: 未传 -AllowKvs, 按 dry-run 模式执行'
    }
    if ($script:DryRunEffective) {
        $steps = @(
            ('[1 绑定] 按 CommandLine 绑定 Kvs.exe(含 {0}); 无则 Kvs.exe "{0}" 启动并等窗口<=90s; >1 同工程=歧义(exit3); 标题须含 {1}' -f $kprPath, $script:ProjectName),
            ('[2 导入] CapsLock ON -> %frr (兜底 UIA 文件>助记符列表>读取(R)...) -> 打开对话框(标题 打开/Open) 文件名 {0} (1148 SetValue 或 ^a ^v 回车) -> 覆盖/全部覆盖/插入 -> OK/确定/是/はい + 程序种类弹窗 + 不存在的指令 清扫 -> 树验证 /{1}/ ; FAIL=10' -f $script:MnmFull, $ModuleName),
            ($(if ($varRows) { ('[3 变量] Alt+V,l 开 KvVariableForm -> 局部页 -> 模块下拉={0} -> %l + Shift+Tab x<=3 聚焦网格 -> Ctrl+Home -> 剪贴板 {1} 行 TSV 回车粘贴 -> 全部覆盖 -> Ctrl+A/Ctrl+C 回读验证 -> Ctrl+F4; FAIL=20' -f $ModuleName, $varRows.Count) } else { '[3 变量] SKIP (未给 -VariablesTsv)' })),
            ('[4 编译] 关阻断弹窗 -> 聚焦 editor(拒模拟器) -> CapsLock ON -> %a + c (Ctrl+F9) -> 等 转换结果 <=' + $WaitCompileSeconds + 's -> WM_CONTEXTMENU + 复制(C) + 剪贴板序列号 -> 解析 errors[]; NG=30 超时=50'),
            ('[5 落盘] Ctrl+S -> 等标题脏标记消失<=20s -> 指纹 before/after (All.prgx/*.kstx/*.mnm/WsTreeEnv.xml) SHA256; FAIL=40')
        )
        foreach ($s in $steps) { Write-Log ('DRY 步骤 ' + $s) }
        Finish $EXIT_OK 'dry-run 完成'
        return
    }

    # ---------------- 4. mutex (E 节)
    $script:MutexObj = New-Object System.Threading.Mutex($false, 'Local\KeyenceAgent.KvStudio.UI')
    if (-not $script:MutexObj.WaitOne(0)) {
        Add-Message 'KV_UI_WORKFLOW_BUSY: 另一 KV STUDIO UI 自动化持有桌面 mutex, 拒绝并发操作'
        Finish $EXIT_ENV 'mutex 占用'
        return
    }
    Write-Log '已获取桌面 mutex Local\KeyenceAgent.KvStudio.UI'

    # ---------------- 5. 关卡 1..5
    Bind-Kvs $kprPath
    Invoke-ImportGate
    if ($varRows) { Invoke-VariablesGate $varRows }
    else { $script:GateVars = 'SKIP'; Add-Message '变量登记 SKIP (无 -VariablesTsv)' }
    Invoke-CompileGate
    Invoke-PersistGate

    Finish $EXIT_OK '全部关卡 PASS'
}
catch {
    Add-Message ('致命异常: ' + $_.Exception.Message + ' @ ' + $_.InvocationInfo.PositionMessage)
    if ($_.ScriptStackTrace) { Write-Log $_.ScriptStackTrace }
    if (-not $script:Finished) { Finish $EXIT_FATAL ('fatal: ' + $_.Exception.GetType().Name) }
}
finally {
    if ($script:KvsPid -gt 0 -and $script:KvUiaPath) {
        try { Dismiss-Dialogs 'final-sweep' 4 } catch { }   # 失败弹窗一律 (OK|确定|是|Yes|はい) 关掉再退
    }
    if ($script:MutexObj) { try { $script:MutexObj.ReleaseMutex(); $script:MutexObj.Dispose() } catch { } }
    if (-not $script:Finished) { Finish $EXIT_FATAL 'unwind without Finish' }
}
