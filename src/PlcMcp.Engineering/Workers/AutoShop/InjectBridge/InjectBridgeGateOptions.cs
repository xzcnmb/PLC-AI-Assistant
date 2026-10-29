namespace PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

/// <summary>
/// C#-side gating of the inject bridge write surface. This is the PRIMARY
/// control layer; the bridge DLL carries a second, independent gate (its
/// pending config) and a per-call loopback hard check.
///
/// Defaults encode the phase-three red lines:
/// <list type="bullet">
/// <item>element write: gate CLOSED (the bridge-side reservation pipeline is
/// reachable for evidence, but nothing is ever sent unless an operator opens
/// this gate AND the loopback check passes);</item>
/// <item>force / download: gate CLOSED and the bridge DLL answers Unsupported
/// directly (its own default gate is closed too);</item>
/// <item>compile: gate OPEN by default (F1-verified compile-all chain; the
/// bridge DLL answers with a submit receipt; offline behaviour, no PLC
/// action); the C# hint must exist for the client to send it;</item>
/// <item><see cref="RequireLoopbackTarget"/> cannot be disabled: every target
/// parameter and every set_target call is validated against 127.0.0.0/8.</item>
/// </list>
/// </summary>
public sealed class InjectBridgeGateOptions
{
    /// <summary>Loopback addresses accepted by the hard validation.</summary>
    public const string LoopbackPrefix = "127.";

    /// <summary>
    /// Gate for the <c>write_elements</c> pipeline. Even when open, the bridge
    /// answers resolved-but-not-invoked until the vendor signature is verified.
    /// </summary>
    public bool AllowElementWrite { get; init; } = false;

    /// <summary>Gate for <c>force</c>. Default closed; the DLL gate is closed too.</summary>
    public bool AllowForce { get; init; } = false;

    /// <summary>Gate for <c>download</c>. Default closed; the DLL gate is closed too.</summary>
    public bool AllowDownload { get; init; } = false;

    /// <summary>
    /// E3 compile entry-point slot. Null (default) keeps compile Unsupported.
    /// When set, the bridge still answers Unsupported until E3 lands a
    /// verified signature; the hint only documents the intended configuration.
    /// </summary>
    public string? CompileEntryHint { get; init; } = null;

    /// <summary>
    /// F1 (2026-10-28): the compile entry point verified by signature evidence
    /// (no-arg __thiscall on the compile-all manager; async submit; requires an
    /// open project). See docs/inovance-autoshop-write-signatures.md section 5.
    /// </summary>
    public const string CompileEntryRvaDefault = "0x15A2EC0";

    /// <summary>
    /// F1 (2026-10-28): the eth-channel element-write export of the vendor comm
    /// stack is a deterministic stub (CommunicateEthernet.dll 4.12.0.2 maps
    /// WriteElemValue/Unforce/ProgramValidate to a 6-byte 'mov eax,0xF390; ret'
    /// thunk returning 62352, the stack's failure code). Writes to the local
    /// simulator go through Modbus TCP 502 FC5/6 instead; the bridge's write
    /// surface stays resolved-but-not-invoked for this reason. See
    /// docs/inovance-autoshop-write-signatures.md.
    /// </summary>
    public const string WriteSurfaceStubFinding =
        "vendor write export is a stub (CommunicateEthernet.dll 4.12.0.2: WriteElemValue/Unforce return 0xF390=62352 with no function body; F1 runtime-verified)";

    /// <summary>
    /// Hard loopback requirement. Init-only and validated: constructing the
    /// options with false throws, so the red line cannot be switched off by
    /// configuration.
    /// </summary>
    public bool RequireLoopbackTarget { get; init; } = true;

    public InjectBridgeGateOptions()
    {
        if (!RequireLoopbackTarget)
        {
            throw new ArgumentException(
                "RequireLoopbackTarget cannot be disabled: the inject bridge only ever talks to 127.0.0.0/8.");
        }
    }

    /// <summary>True when the address is a loopback dotted quad (127.0.0.0/8).</summary>
    public static bool IsLoopbackAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }
        if (string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var parts = address.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }
        foreach (string part in parts)
        {
            if (!int.TryParse(part, out int value) || value is < 0 or > 255)
            {
                return false;
            }
        }
        return address.StartsWith(LoopbackPrefix, StringComparison.Ordinal);
    }

    /// <summary>Validates options; throws on impossible/imunsafe combinations.</summary>
    public void Validate()
    {
        if (!RequireLoopbackTarget)
        {
            throw new ArgumentException(
                "RequireLoopbackTarget cannot be disabled: the inject bridge only ever talks to 127.0.0.0/8.");
        }
    }

    /// <summary>
    /// Human-readable gate-open conditions, quoted verbatim in Unsupported
    /// answers so a caller always knows why a surface stays closed.
    /// F1 (2026-10-28) updates: compile is OPEN by default (the F1-verified
    /// compile-all chain root 0x1581A70 -> 0x1581CA0 -> 0x15A2EC0, project
    /// gate 0x161B620 checked first; offline behaviour, no PLC action);
    /// element write is deterministically unavailable on the comm-stack eth
    /// channel (stub finding); force stays closed (runtime-verified
    /// SEH-uncatchable process termination).
    /// </summary>
    public string DescribeGateConditions()
    {
        return "Gate conditions: element write requires AllowElementWrite=true (operator decision) plus " +
               "set_target(127.0.0.1) - and is deterministically unavailable on the comm-stack eth channel (" +
               WriteSurfaceStubFinding + "); force requires " +
               "AllowForce=true plus the bridge-side gate plus S4 approval gating (F1: the vendor Force body " +
               "terminates the process in a way user SEH cannot catch - gate stays closed); download requires " +
               "AllowDownload=true plus a wired download payload workflow (D1 layer); compile is OPEN by " +
               "default (F1 signature evidence: root 0x1581A70 -> cmpMgr 0x1581CA0 -> compileAll 0x15A2EC0, " +
               "no-arg __thiscall, async submit, project gate 0x161B620 checked first; " +
               "docs/inovance-autoshop-write-signatures.md section 5) and can be disabled with the " +
               "--compile-rva none injector flag. " +
               "Loopback-only is not negotiable.";
    }
}
