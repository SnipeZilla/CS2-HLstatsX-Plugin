## HLstatsX Plugin for CounterStrikeSharp (CS2) 🎯
This plugin extends CounterStrikeSharp to enable full(mostly) HLstatsX:CE support on CS2 servers.

## ✨ Overview
* This plugin emulates the classic `hlstats.smx` behavior, allowing CounterStrikeSharp CS2 servers to communicate seamlessly with HLstatsX backends.
* It receives RCON commands from your daemon (`HLX_SM_XX`) and also dispatches enriched event logs via UDP, making it compatible with both legacy and modern HLstatsX backends— included the updated dual-mode HTTP/UDP Perl daemon (hlxce 2025)!

## 🔧 Key Features
- ☝ Spoofs SOURCEMOD's hlstats.smx; Server Mod set as 'SOURCEMOD', Events unchanged hlx_sm_xx 
- ✅ Sends additional UDP game event which are not logged by cs2
- 🚀 Optimized for modern CS2 servers and the revamped HLstatsX ecosystem
- 🧩️ Minimal performance and Fully configurable
- ⚙️ CS2 x-server.cfg:

## ⚙️ Config
# Easy server setup—just two config changes
- CS2 x-server.cfg:
  
`log on`<br>
`logaddress_add_http "http://127.0.0.1:27500"`<br>

-  CounterStrikeSharp\Configs\Plugins\HLstatsZ\HLstatsZ.json:
  
`{`<br>
  `"Log_Address": "127.0.0.1",`<br>
  `"Log_Port": 27500,`<br>
  `"BroadcastAll": 0,`<br>
`}`<br>

* logaddress_add_http & Log_Address: IP address of the server where your HLstatsX daemon is running

* Log_Port: The UDP port your daemon is listening on (make sure port are open for UDP and TCP)

* BroadcastAll: 1 to Emulate old hlstatsx.smx (tf2,css...), 0 for sourcemod csgo
  
✅ Works best with the updated HLxce daemon, which supports both UDP and HTTP log ingestion on the same port.


## 🧪 Current Status and disclamer
* 🐣 Early-stage plugin!
* not everything work.
* But the core idea is here. I couldn't make everything I wanted. (My first css plugin!)
* The menu is horrible.
* Teams color are done by hlxce 2025 (for now, until i figure out how to do it inside the plugin)
* Needs community to improve this plugin for the love of HlstatsX!



