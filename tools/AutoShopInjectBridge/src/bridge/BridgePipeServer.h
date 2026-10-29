// BridgePipeServer.h
//
// Named-pipe server of the AutoShop inject bridge. Two modes:
//
//   threaded   (Bridge_Init)      - server thread accepts one client at a
//                                   time and serves JSON lines until
//                                   shutdown; used by the injector path.
//   manual     (Bridge_Pump)      - harness/test mode: each Pump call
//                                   accepts or drains one request/response
//                                   round deterministically.
//
// The pipe is created with PIPE_REJECT_REMOTE_CLIENTS (local machine only)
// and the default DACL of the hosting process. Protocol is one JSON object
// per line (UTF-8, LF); the C# mirror is InjectBridgeProtocol.cs.

#pragma once

#include "BridgeInternal.h"

namespace AutoShopInjectBridge {

class BridgePipeServer
{
public:
    // Threaded mode: copies the config, starts the server thread.
    static bool Start(const BridgeConfig& config);

    // True while the threaded server is running.
    static bool IsRunning();

    // Manual mode: sets the pipe name before any pump/init. Refused (false)
    // when the threaded server is already running.
    static bool SetManualPipeName(const char* name);

    // Manual mode: one accept/drain cycle. Returns handled-line count,
    // 0 when idle, negative on error (-2 when threaded mode is running).
    static int Pump(int timeoutMs);

    // Stops the threaded server (or tears down manual state).
    static void Stop();
};

} // namespace AutoShopInjectBridge
