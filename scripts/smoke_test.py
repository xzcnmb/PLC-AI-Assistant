"""Black-box stdio checks. Only local simulator and config introspection are used."""
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
dll = root / "src/PlcMcp.Server/bin/Release/net8.0/PlcMcp.Server.dll"
assert dll.exists(), "Build the Release configuration before running this script."

def request(identifier, method, params=None):
    message = {"jsonrpc": "2.0", "method": method}
    if identifier is not None:
        message["id"] = identifier
    if params is not None:
        message["params"] = params
    return message

def exchange(messages, *arguments):
    result = subprocess.run(
        ["dotnet", str(dll), *arguments],
        input="".join(json.dumps(m, ensure_ascii=False) + "\n" for m in messages),
        capture_output=True, text=True, encoding="utf-8", timeout=20,
    )
    assert result.returncode == 0, result.stderr
    responses = [json.loads(line.lstrip("\ufeff")) for line in result.stdout.splitlines() if line.strip()]
    assert all(r["jsonrpc"] == "2.0" for r in responses), "Unexpected stdout output"
    return responses

initialize = request(1, "initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}})
messages = [initialize, request(None, "notifications/initialized"), request(2, "tools/list"),
    request(3, "tools/call", {"name": "plc_read_tags", "arguments": {"targetId": "sim-siemens", "tags": ["目标压力"]}}),
    request(4, "tools/call", {"name": "plc_plan_write", "arguments": {"targetId": "sim-siemens", "changes": {"目标压力": "999999"}}}),
    request(None, "tools/call", {"name": "plc_list_targets", "arguments": {}})]
responses = exchange(messages)
assert [r["id"] for r in responses] == [1, 2, 3, 4], "Notification produced a response"
tool_names = {t["name"] for t in responses[1]["result"]["tools"]}
assert {"plc_list_targets", "plc_read_tags", "plc_doctor", "plc_lint_program", "plc_get_audit"} <= tool_names
assert "plc_download_project" not in tool_names and "plc_force_io" not in tool_names
assert "plc_smart_validate" not in tool_names, "SMART worker must be explicitly configured"
assert responses[3]["result"]["isError"] is True, "Out-of-range value was accepted"
value = json.loads(responses[2]["result"]["content"][0]["text"])["values"][0]
assert value["value"] == 2.5 and value["quality"] == "simulated"

configured = exchange([initialize, request(None, "notifications/initialized"), request(2, "tools/list"),
    request(3, "tools/call", {"name": "plc_list_targets", "arguments": {}})],
    "--config", str(root / "profiles/readonly.example.json"))
names = {t["name"] for t in configured[1]["result"]["tools"]}
assert "plc_plan_write" not in names and "plc_apply_write" not in names
profiles = json.loads(configured[2]["result"]["content"][0]["text"])["targets"]
assert len(profiles) == 4 and all(not t["isSimulation"] for t in profiles)
print("PASS: Release stdio, UTF-8 aliases, annotations, notifications, range rejection, read-only configuration (no device probes)")
