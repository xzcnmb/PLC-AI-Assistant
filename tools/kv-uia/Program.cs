using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

// kv-uia: UIA tree walker + one-shot invoker for KV STUDIO automation.
// usage: kv-uia <regex> [--invoke] [--set <value>] [--keys <keys>] [--pid <pid>] [--max <nodes>]
//   walks the UIA tree printing matching controls; --pid only filters what is printed/acted
//   on, the walk still traverses elements of other pids (RootElement itself has pid 0).
//   --invoke   invokes/expands the first on-screen match.
//   --set      same selection as --invoke but sets ValuePattern.Value to <value>;
//              prints SET / SET-FAILED (reason=NoValuePattern|ElementOffscreen|NoMatch).
//   --keys     skips the walk entirely and sends <keys> (e.g. "{ENTER}", "{TAB}") to the
//              foreground window via the WScript.Shell COM SendKeys method.
internal static class Program
{
    private static int Main(string[] args)
    {
        string pat = args.Length > 0 ? args[0] : "模拟";
        bool invoke = Array.IndexOf(args, "--invoke") >= 0;
        int pid = 0;
        int max = 400000;
        string set = null;
        string keys = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pid" && i + 1 < args.Length) pid = int.Parse(args[i + 1]);
            if (args[i] == "--max" && i + 1 < args.Length) max = int.Parse(args[i + 1]);
            if (args[i] == "--set" && i + 1 < args.Length) set = args[i + 1];
            if (args[i] == "--keys" && i + 1 < args.Length) keys = args[i + 1];
        }

        if (keys != null)
        {
            // --keys: no UIA walk, no selection logic — send to the foreground window.
            object shell = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t != null) shell = Activator.CreateInstance(t);
            }
            catch { }
            if (shell == null)
            {
                Console.WriteLine("KEYS-FAILED reason=NoWScriptShell");
                return 1;
            }
            Thread.Sleep(300);
            Console.WriteLine("KEYS-SENT " + keys);
            try
            {
                shell.GetType().InvokeMember("SendKeys", BindingFlags.InvokeMethod, null, shell, new object[] { keys });
            }
            catch
            {
                Console.WriteLine("KEYS-FAILED reason=SendKeysError");
                return 1;
            }
            Console.WriteLine("KEYS-EXEC");
            return 0;
        }

        var r = new Regex(pat, RegexOptions.IgnoreCase);
        var sw = Stopwatch.StartNew();
        var stack = new Stack<AutomationElement>();
        stack.Push(AutomationElement.RootElement);
        int scanned = 0, printed = 0;
        while (stack.Count > 0 && scanned < max)
        {
            var el = stack.Pop();
            scanned++;
            try
            {
                int elPid = el.Current.ProcessId;
                // pid mismatch: do not print/act, but still traverse (children are pushed below).
                bool pidOk = (pid == 0 || elPid == pid);
                string name = el.Current.Name;
                if (pidOk && !string.IsNullOrEmpty(name) && r.IsMatch(name))
                {
                    printed++;
                    Console.WriteLine(scanned + " pid=" + elPid + " name=[" + name + "] type=" + el.Current.ControlType.ProgrammaticName +
                                      " hwnd=0x" + el.Current.NativeWindowHandle.ToString("X") + " offscreen=" + el.Current.IsOffscreen);
                    if ((invoke || set != null) && printed == 1)
                    {
                        if (set != null)
                        {
                            if (el.Current.IsOffscreen)
                            {
                                Console.WriteLine("SET-FAILED pid=" + elPid + " name=[" + name + "] reason=ElementOffscreen");
                            }
                            else
                            {
                                object vp;
                                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out vp))
                                {
                                    ((ValuePattern)vp).SetValue(set);
                                    Console.WriteLine("SET pid=" + elPid + " name=[" + name + "]");
                                }
                                else
                                {
                                    Console.WriteLine("SET-FAILED pid=" + elPid + " name=[" + name + "] reason=NoValuePattern");
                                }
                            }
                        }
                        else if (!el.Current.IsOffscreen)
                        {
                            object p = null;
                            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out p))
                            {
                                ((InvokePattern)p).Invoke();
                                Console.WriteLine("INVOKED pid=" + elPid);
                            }
                            else if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p))
                            {
                                ((ExpandCollapsePattern)p).Expand();
                                Console.WriteLine("EXPANDED pid=" + elPid);
                            }
                            else
                            {
                                Console.WriteLine("NO-INVOKE-PATTERN");
                            }
                        }
                    }
                }
            }
            catch { }
            try
            {
                foreach (AutomationElement c in el.FindAll(TreeScope.Children, Condition.TrueCondition))
                {
                    stack.Push(c);
                }
            }
            catch { }
        }

        if (set != null && printed == 0)
            Console.WriteLine("SET-FAILED pid=0 name=[] reason=NoMatch");

        Console.WriteLine("scanned=" + scanned + " printed=" + printed + " ms=" + sw.ElapsedMilliseconds);
        return 0;
    }
}