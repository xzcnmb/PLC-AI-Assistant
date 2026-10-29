# -*- coding: utf-8 -*-
# CODESYS offline probe: only inspects ScriptEngine object surface.
# Opens no project, touches no PLC, performs no online call.
from __future__ import print_function
import json
import sys
import traceback


def emit(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False, separators=(',', ':')) + "\n")
    sys.stdout.flush()


g = globals()
emit({"probe": "started", "version": str(sys.version), "executable": str(getattr(sys, "executable", ""))})
emit({
    "probe": "globals",
    "hasProjects": "projects" in g,
    "hasSystem": "system" in g,
    "hasOnline": "online" in g,
    "names": sorted([n for n in g.keys() if not n.startswith("_")])[:60]
})

try:
    if "projects" in g:
        emit({"probe": "projects", "type": str(type(g["projects"])),
              "hasOpen": hasattr(g["projects"], "open"),
              "hasCreate": hasattr(g["projects"], "create"),
              "hasPrimary": hasattr(g["projects"], "primary")})
    if "system" in g:
        emit({"probe": "system", "type": str(type(g["system"])),
              "hasGetMessages": hasattr(g["system"], "get_messages"),
              "hasExit": hasattr(g["system"], "exit")})
    if "online" in g:
        emit({"probe": "online", "type": str(type(g["online"])),
              "hasCreateOnlineApplication": hasattr(g["online"], "create_online_application")})
except Exception:
    emit({"probe": "error", "detail": traceback.format_exc()})

emit({"probe": "done"})

try:
    if "system" in g:
        g["system"].exit(0)
except Exception:
    pass
