// BridgePipeServer.h
//
// M4 native side: a minimal JSON-line named-pipe server that runs inside the
// plugin host process. Protocol (one JSON object per line, UTF-8):
//
//   -> {"id":"<string>","method":"handshake","params":{"client":"<name>"}}
//   <- {"id":"<string>","result":{...handshake fields...}}
//   -> {"id":"<string>","method":"capabilities"}
//   <- {"id":"<string>","result":{...capability map...}}
//   -> {"id":"<string>","method":"read_status"}
//   <- {"id":"<string>","result":{...}}  or  {"id":"<string>","error":{...}}
//   -> {"id":"<string>","method":"read_elements","params":{"addresses":["D100","D101"]}}
//   -> {"id":"<string>","method":"shutdown"}
//
// The C# mirror lives in
// src/PlcMcp.Engineering/Workers/AutoShop/NativeBridge/NativeBridgeProtocol.cs.
// Read-only only: there is deliberately no write/force/download method.

#pragma once

namespace PlcMcpBridge
{

class BridgePipeServer
{
public:
    /// Starts the pipe server on a background thread. Returns false when the
    /// pipe cannot be created.
    static bool Start();

    /// Signals the server to stop and waits briefly for the thread.
    static void Stop();

    /// True while the server thread is running.
    static bool IsRunning();
};

} // namespace PlcMcpBridge
