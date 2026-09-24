# -*- coding: utf-8 -*-
from __future__ import print_function
import sys
import os
import json
import hashlib
import time
import traceback

WORKER_NAME = "Codesys-ScriptEngine-Worker"
WORKER_VERSION = "1.0"
PROTOCOL_VERSION = "1.0"
ALLOWED_OPERATIONS = set([
    "inspect", "inspectproject", "export", "exportpou",
    "compile", "compileproject", "validate", "validatepou"
])
PROHIBITED_OPERATIONS = set([
    "download", "downloadproject", "run", "stop", "runstop", "setrunmode",
    "force", "forceio", "hmipublish", "publishhmi", "online", "login",
    "logout", "reset", "upload", "save", "saveas", "import", "delete", "move"
])
PROHIBITED_KEYWORD_PATTERNS = [
    "download", "run", "stop", "force", "online", "plc_write", "flash",
    "login", "logout", "reset", "hmipublish", "upload", "save", "import",
    "delete", "move", "write"
]


def send_message(identifier, result=None, error=None):
    response = {"jsonrpc": "2.0", "id": identifier}
    if error is not None:
        response["error"] = error
    else:
        response["result"] = result
    sys.stdout.write(json.dumps(response, ensure_ascii=True, separators=(',', ':')) + "\n")
    sys.stdout.flush()


def log_stderr(message):
    try:
        sys.stderr.write(str(message) + "\n")
        sys.stderr.flush()
    except Exception:
        pass


def compute_sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        while True:
            chunk = stream.read(65536)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest().lower()


def write_utf8(path, text):
    data = text.encode("utf-8")
    with open(path, "wb") as stream:
        stream.write(data)


class CodesysWorkerEngine(object):
    def __init__(self):
        self.jobs = {}
        self.artifacts = {}
        self.has_scriptengine = "projects" in globals() and "system" in globals() and "online" in globals()
        if not self.has_scriptengine:
            raise RuntimeError("CODESYS ScriptEngine globals are unavailable; standalone Python is not a vendor worker")

    def handle_handshake(self, request_id, params):
        try:
            runtime = str(sys.version).replace("\r", " ").replace("\n", " ")
        except Exception:
            runtime = "CODESYS ScriptEngine (IronPython)"
        result = {
            "workerName": WORKER_NAME,
            "workerVersion": WORKER_VERSION,
            "protocolVersion": PROTOCOL_VERSION,
            "vendor": "CODESYS",
            "bitness": "64-bit" if sys.maxsize > 2 ** 32 else "32-bit",
            "runtimeEnvironment": runtime,
            "capabilities": ["InspectProject", "ExportPou", "ValidatePou", "CompileProject"],
            "capabilityEvidence": {
                "scriptEngineGlobals": "projects, system, online present",
                "projectApi": "projects.open / ScriptProject.export_xml",
                "applicationApi": "ScriptApplication.build / clean / rebuild",
                "onlineApiUsed": "false"
            }
        }
        send_message(request_id, result=result)

    def handle_doctor(self, request_id, params):
        checks = [
            {"name": "ScriptEngine", "passed": self.has_scriptengine,
             "message": "projects/system/online globals are present."},
            {"name": "OfflineOnly", "passed": True,
             "message": "No ScriptOnline API is called by this worker."},
            {"name": "OneShotProcess", "passed": True,
             "message": "Worker runs in a dedicated CODESYS process and supports graceful shutdown."}
        ]
        send_message(request_id, result={
            "healthy": all(c["passed"] for c in checks),
            "installed": True,
            "toolchainPath": getattr(sys, "executable", "CODESYS.exe"),
            "toolchainVersion": str(sys.version).replace("\r", " ").replace("\n", " "),
            "bitness": "64-bit" if sys.maxsize > 2 ** 32 else "32-bit",
            "details": "CODESYS ScriptEngine worker is available; startup probe opened no project.",
            "checks": checks
        })

    def check_safety(self, operation, options):
        normalized = str(operation).strip().lower()
        if normalized in PROHIBITED_OPERATIONS:
            return False, "Operation '" + str(operation) + "' is prohibited by the offline safety policy."
        if normalized not in ALLOWED_OPERATIONS:
            return False, "Operation '" + str(operation) + "' is not supported by the CODESYS offline worker."
        if options:
            for key, value in options.items():
                key_lower = str(key).lower()
                value_lower = str(value).lower()
                for pattern in PROHIBITED_KEYWORD_PATTERNS:
                    if pattern in key_lower or pattern in value_lower:
                        return False, "Option '" + str(key) + "' contains prohibited keyword '" + pattern + "'."
        return True, ""

    def validate_project_path(self, project_path):
        path = os.path.abspath(project_path)
        if not os.path.isfile(path):
            raise ValueError("Project file does not exist.")
        return path

    def output_path(self, project_path, name):
        if not isinstance(name, str) or not name or name in (".", ".."):
            raise ValueError("Artifact name must be a simple filename.")
        if os.path.basename(name) != name or "/" in name or "\\" in name:
            raise ValueError("Artifact path must not contain directory components.")
        root = os.path.dirname(os.path.abspath(project_path))
        path = os.path.abspath(os.path.join(root, name))
        if os.path.dirname(path) != root:
            raise ValueError("Artifact path escaped the working directory.")
        return path

    def register_artifact(self, job_id, path, artifact_type):
        self.artifacts[job_id] = [{
            "name": os.path.basename(path),
            "relativePath": os.path.basename(path),
            "artifactType": artifact_type,
            "sizeBytes": os.path.getsize(path),
            "sha256": compute_sha256(path)
        }]

    def handle_submit(self, request_id, params):
        if not isinstance(params, dict):
            send_message(request_id, error={"code": -32602, "message": "Invalid params: expected an object."})
            return
        job_id = params.get("jobId")
        operation = params.get("operation", "")
        options = params.get("options") or {}
        if not isinstance(job_id, str) or not job_id or len(job_id) > 80 or any(c in job_id for c in "\\/:*?\"<>|\r\n"):
            send_message(request_id, error={"code": -32602, "message": "Invalid jobId."})
            return
        if not isinstance(options, dict):
            send_message(request_id, error={"code": -32602, "message": "Invalid options."})
            return

        safe, reason = self.check_safety(operation, options)
        if not safe:
            send_message(request_id, error={"code": -32600, "message": "Safety policy rejection: " + reason})
            return

        try:
            project_path = self.validate_project_path(params.get("projectPath", ""))
            op = str(operation).strip().lower()
            if op in ("inspect", "inspectproject"):
                self.execute_inspect(job_id, project_path)
            elif op in ("export", "exportpou"):
                self.execute_export(job_id, project_path, options)
            else:
                self.execute_compile(job_id, project_path, options)

            result = {
                "jobId": job_id,
                "state": "completed",
                "progress": 1.0,
                "message": "CODESYS offline operation completed on the isolated working copy.",
                "exitCode": 0
            }
        except Exception as exc:
            log_stderr("Offline job failed: " + str(exc) + "\n" + traceback.format_exc())
            result = {
                "jobId": job_id,
                "state": "failed",
                "progress": 1.0,
                "message": "Offline operation failed: " + str(exc),
                "exitCode": 1
            }
        self.jobs[job_id] = result
        send_message(request_id, result=result)

    def execute_inspect(self, job_id, project_path):
        project = None
        try:
            project = projects.open(project_path, allow_readonly=False)
            applications = []
            objects = []
            for obj in project.get_children(recursive=True):
                try:
                    name = obj.get_name()
                except Exception:
                    name = "unknown"
                is_application = bool(getattr(obj, "is_application", False))
                if is_application:
                    applications.append(name)
                else:
                    objects.append({"name": name, "type": str(getattr(obj, "type", ""))})
            payload = {
                "projectPath": project_path,
                "inspectedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "toolchain": "CODESYS ScriptEngine",
                "applications": applications,
                "objects": objects
            }
        finally:
            if project is not None:
                project.close()
        output = self.output_path(project_path, "codesys_inspect.json")
        write_utf8(output, json.dumps(payload, indent=2, ensure_ascii=True))
        self.register_artifact(job_id, output, "JSON")

    def execute_export(self, job_id, project_path, options):
        output_name = options.get("output_file", "plcopen_export.xml")
        output = self.output_path(project_path, output_name)
        project = None
        try:
            project = projects.open(project_path, allow_readonly=False)
            children = project.get_children(recursive=False)
            project.export_xml(children, path=output, recursive=True)
        finally:
            if project is not None:
                project.close()
        if not os.path.isfile(output) or os.path.getsize(output) == 0:
            raise RuntimeError("CODESYS did not create a non-empty PLCopen XML export.")
        self.register_artifact(job_id, output, "PLCopenXML")

    def find_application(self, project):
        try:
            active = project.active_application
            if active is not None:
                return active
        except Exception:
            pass
        for obj in project.get_children(recursive=True):
            try:
                if bool(getattr(obj, "is_application", False)):
                    return obj
            except Exception:
                continue
        return None

    def execute_compile(self, job_id, project_path, options):
        mode = str(options.get("mode", "build")).lower()
        if mode not in ("build", "clean", "rebuild"):
            raise ValueError("Compile mode must be build, clean, or rebuild.")
        messages = []
        project = None
        try:
            if hasattr(system, "get_messages"):
                system.clear_messages("{194B48A9-AB51-43ae-B9A9-51D3EDAADDF3}")
            project = projects.open(project_path, allow_readonly=False)
            application = self.find_application(project)
            if application is None:
                raise RuntimeError("No CODESYS application object was found in the working copy.")
            if mode == "clean":
                application.clean()
            elif mode == "rebuild":
                application.rebuild()
            else:
                application.build()
            if hasattr(system, "get_messages"):
                messages = [str(message) for message in system.get_messages()]
        finally:
            if project is not None:
                project.close()

        report = {
            "toolchain": "CODESYS ScriptEngine",
            "mode": mode,
            "projectPath": project_path,
            "completedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "messages": messages
        }
        output = self.output_path(project_path, "codesys_compile_report.json")
        write_utf8(output, json.dumps(report, indent=2, ensure_ascii=True))
        self.register_artifact(job_id, output, "BuildReport")

    def handle_status(self, request_id, params):
        job_id = params.get("jobId") if isinstance(params, dict) else None
        result = self.jobs.get(job_id)
        if result is None:
            send_message(request_id, error={"code": -32004, "message": "Job not found."})
        else:
            send_message(request_id, result=result)

    def handle_artifacts(self, request_id, params):
        job_id = params.get("jobId") if isinstance(params, dict) else None
        send_message(request_id, result={"jobId": job_id, "artifacts": self.artifacts.get(job_id, [])})

    def handle_cancel(self, request_id, params):
        job_id = params.get("jobId") if isinstance(params, dict) else None
        send_message(request_id, result={"cancelled": False, "jobId": job_id,
                                         "message": "Synchronous ScriptEngine operations are cancelled by terminating the worker process."})

    def handle_shutdown(self, request_id, params):
        send_message(request_id, result={"shutdown": True})


def main():
    engine = CodesysWorkerEngine()
    exit_code = 0
    while True:
        line = sys.stdin.readline()
        if not line:
            break
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except Exception as exc:
            send_message(None, error={"code": -32700, "message": "Parse error: " + str(exc)})
            continue

        request_id = request.get("id")
        method = request.get("method")
        params = request.get("params")
        try:
            if method == "handshake":
                engine.handle_handshake(request_id, params)
            elif method == "doctor":
                engine.handle_doctor(request_id, params)
            elif method == "submit":
                engine.handle_submit(request_id, params)
            elif method == "status":
                engine.handle_status(request_id, params)
            elif method == "artifacts":
                engine.handle_artifacts(request_id, params)
            elif method == "cancel":
                engine.handle_cancel(request_id, params)
            elif method == "shutdown":
                engine.handle_shutdown(request_id, params)
                break
            else:
                send_message(request_id, error={"code": -32601, "message": "Method not found."})
        except Exception as exc:
            exit_code = 1
            log_stderr("RPC method '" + str(method) + "' failed: " + str(exc) + "\n" + traceback.format_exc())
            send_message(request_id, error={"code": -32001, "message": str(exc)})

    system.exit(exit_code)


main()
