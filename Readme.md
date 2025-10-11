## HLstatsZ Plugin for CounterStrikeSharp (CS2) 🎯
This plugin extends CounterStrikeSharp to enable full(mostly) HLstatsX:CE support on CS2 servers.

## ✨ Overview
* This plugin emulates the classic `hlstats.smx` behavior, allowing CounterStrikeSharp CS2 servers to communicate seamlessly with HLstatsX backends.
* It receives RCON commands from your daemon (`HLX_SM_XX`) and also dispatches enriched event logs via HTTP

## 🔧 Key Features
- ☝ Spoofs SOURCEMOD's hlstats.smx; Server Mod set as 'SOURCEMOD', Events unchanged hlx_sm_xx 
- ✅ Sends additional HTTP game event which are not logged by cs2
- 🚀 Optimized for modern CS2 servers and the revamped HLstatsX ecosystem
- 🧩️ Minimal performance and Fully configurable
- 😻 WASDE menu
- ⚙️ CS2 x-server.cfg:

## ⚙️ Config
# Easy server setup—just two config changes
- CS2 x-server.cfg:
```
log on
logaddress_add_http "http://127.0.0.1:27500"
sv_visiblemaxplayers 32 // RCON status does not report max players
```
-  CounterStrikeSharp\Configs\Plugins\HLstatsZ\HLstatsZ.json:
  
```
  "Log_Address": "127.0.0.1",
  "Log_Port": 27500,
  "BroadcastAll": 0,
  "ServerAddr":"64.74.97.164:27015"
```
✅ Works with the updated HLxce daemon version >= 2.3.4, which supports both async UDP and HTTP log ingestion on the same port.

* Log_Port: The UDP/HTTP port your daemon is listening on (make sure port are open for UDP and TCP)

* BroadcastAll: 1 to Emulate old hlstatsx.smx (tf2,css...), 0 for sourcemod csgo/cs2

* ServerAddr: force ip:port if you see not authorized in the log. still highly recommended to force log on known ip:port

* BroadcastAll: 1 to Emulate old hlstatsx.smx (tf2,css...), 0 for sourcemod csgo




