using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Plugin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using GameTimer = CounterStrikeSharp.API.Modules.Timers.Timer;
using System.Net.Sockets;
using System.Net;
using System.Text;

namespace HLstatsZ;

public class HLstatsZConfig
{
    [JsonPropertyName("Log_Address")] public string Log_Address { get; set; } = "127.0.0.1";
    [JsonPropertyName("Log_Port")] public int Log_Port { get; set; } = 27500;
    [JsonPropertyName("BroadcastAll")] public int BroadcastAll { get; set; } = 0;
    [JsonPropertyName("ServerAddr")] public string ServerAddr { get; set; } = "";
}

public class SourceBansConfig
{
    [JsonPropertyName("Host")] public string Host { get; set; } = "127.0.0.1";
    [JsonPropertyName("Port")] public int Port { get; set; } = 3306;
    [JsonPropertyName("Database")] public string Database { get; set; } = "";
    [JsonPropertyName("Prefix")] public string Prefix { get; set; } = "sb";
    [JsonPropertyName("User")] public string User { get; set; } = "";
    [JsonPropertyName("Password")] public string Password { get; set; } = "";
    [JsonPropertyName("MapCycle")] public MapCycleSection MapCycle { get; set; } = new();
}

public class MapCycleSection
{
    [JsonPropertyName("Admin")] public MapCycleConfig Admin { get; set; } = new();
    [JsonPropertyName("Public")] public MapCycleConfig Public { get; set; } = new();
}

public class MapCycleConfig
{
    [JsonPropertyName("Maps")] public List<string> Maps { get; set; } = new();
    [JsonPropertyName("WorkShop")] public Dictionary<string, string> WorkShop { get; set; } = new();
}


public class HLstatsZMainConfig : IBasePluginConfig
{
    [JsonPropertyName("HLstatsZ")] public HLstatsZConfig HLstatsZ { get; set; } = new();
    [JsonPropertyName("SourceBans")] public SourceBansConfig SourceBans { get; set; } = new();
    public int Version { get; set; } = 2;
}

public class HLstatsZ : BasePlugin, IPluginConfig<HLstatsZMainConfig>
{
    public static HLstatsZ? Instance;
    private static readonly HttpClient httpClient = new();
    public HLstatsZMainConfig Config { get; set; } = new();

    public static readonly Dictionary<int, HLZMenuManager> Players = new();
    public string Trunc(string s, int max=20)
        => s.Length > max ? s.Substring(0, Math.Max(0, max - 3)) + "..." : s;

    private string? _lastPsayHash;

    public override string ModuleName => "HLstatsZ";
    public override string ModuleVersion => "2.0.0";
    public override string ModuleAuthor => "SnipeZilla";

    public void OnConfigParsed(HLstatsZMainConfig config)
    {
        Config = config;

    }

    private HLZMenuManager _menuManager = null!;

    public override void Load(bool hotReload)
    {
        Instance = this;

        RegisterListener<Listeners.OnTick>(OnTick);

        //RegisterEventHandler<EventPlayerChat>(OnPlayerChat);
        RegisterEventHandler<EventRoundMvp>(OnRoundMvp);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);

        AddCommandListener(null, ComamndListenerHandler, HookMode.Pre);

        _menuManager = new HLZMenuManager(this);

        var serverAddr = Config.HLstatsZ.ServerAddr;
        if (string.IsNullOrWhiteSpace(serverAddr))
        {
            var hostPort = ConVar.Find("hostport")?.GetPrimitiveValue<int>() ?? 27015;
            var serverIP = GetLocalIPAddress();
            serverAddr = $"{serverIP}:{hostPort}";
            Config.HLstatsZ.ServerAddr=serverAddr;
        }
        SourceBans.serverAddr = serverAddr;
        SourceBans.Init(Config.SourceBans, Logger);
        _ = SourceBans.GetSid();
        SourceBans.PrimeConnectedAdmins();
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<Listeners.OnTick>(OnTick);

        //DeregisterEventHandler<EventPlayerChat>(OnPlayerChat);
        DeregisterEventHandler<EventRoundMvp>(OnRoundMvp);
        DeregisterEventHandler<EventBombDefused>(OnBombDefused);
        DeregisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        DeregisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath);

        RemoveCommandListener(null!, ComamndListenerHandler, HookMode.Pre);

        SourceBans._cleanupTimer?.Dispose();
        SourceBans._cleanupTimer = null;
    }

    // ------------------ Core Logic ------------------
    public static string GetLocalIPAddress()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
        socket.Connect("8.8.8.8", 65530); // Google's DNS
        var endPoint = socket.LocalEndPoint as IPEndPoint;
        return endPoint?.Address.ToString() ?? "127.0.0.1";
    }

    public async Task SendLog(CCSPlayerController player, string message, string verb)
    {
        if (!player.IsValid) return;
        var name    = player.PlayerName;
        var userid  = player.UserId;
        var steamid = (uint)(player.SteamID - 76561197960265728);
        var team    = player.TeamNum switch
        {
            2 => "TERRORIST",
            3 => "CT",
            _ => "UNASSIGNED"
        };

        var serverAddr = Config.HLstatsZ.ServerAddr;
        //if (string.IsNullOrWhiteSpace(serverAddr))
        //{
        //    var hostPort = ConVar.Find("hostport")?.GetPrimitiveValue<int>() ?? 27015;
        //    var serverIP = GetLocalIPAddress();
        //    serverAddr = $"{serverIP}:{hostPort}";
        //    Config.HLstatsZ.ServerAddr=serverAddr;
        //}

        var logLine = $"L {DateTime.Now:MM/dd/yyyy - HH:mm:ss}: \"{name}<{userid}><[U:1:{steamid}]><{team}>\" {verb} \"{message}\"";

        try
        {
            if (!httpClient.DefaultRequestHeaders.Contains("X-Server-Addr"))
                httpClient.DefaultRequestHeaders.Add("X-Server-Addr", serverAddr);

            var content = new StringContent(logLine, Encoding.UTF8, "text/plain");
            var response = await httpClient.PostAsync($"http://{Config.HLstatsZ.Log_Address}:{Config.HLstatsZ.Log_Port}/log", content);

            if (!response.IsSuccessStatusCode)
            {
                Instance?.Logger.LogInformation($"[HLstatsZ] HTTP log send failed: {response.StatusCode} - {response.ReasonPhrase}");
            }
        }
        catch (Exception ex)
        {
            Instance?.Logger.LogInformation($"[HLstatsZ] HTTP log send exception: {ex.Message}");
        }
    }

    public static void DispatchHLXEvent(string type, CCSPlayerController? player, string message)
    {
        if (Instance == null) return;

        switch (type)
        {
            case "psay":
                if (player != null) SendPrivateChat(player, message);
                else Instance?.Logger.LogInformation($"Player null from message: {message}");
                break;
            case "csay":
                Instance.BroadcastCenterMessage(message);
                break;
            case "msay":
                if (player != null && Instance?._menuManager != null)
                    Instance._menuManager.Open(player, message);
                break;
            case "say":
                SendChatToAll(message);
                break;
            case "hint":
                if (player != null) ShowHintMessage(player, message);
                break;
            default:
                if (player != null) player.PrintToChat($"Unknown HLX type: {type}");
                else Instance?.Logger.LogInformation($"Unknown HLX type: {type}"); 
                break;
        }
    }

    private static string NormalizeName(string name)
    {
        var normalized = name.ToLowerInvariant();
        normalized = new string(normalized
            .Where(c => !char.IsControl(c) && c != '\u200B' && c != '\u200C' && c != '\u200D')
            .ToArray());
        normalized = new string(normalized.Where(c => c <= 127).ToArray());
        return normalized.Trim();
    }

    public static CCSPlayerController? FindTarget(object pl)
    {
        string? token = pl as string ?? pl?.ToString();
        if (string.IsNullOrWhiteSpace(token)) return null;

        // #userid
        if (token[0] == '#' && int.TryParse(token.AsSpan(1), out var uid))
            return Utilities.GetPlayers().FirstOrDefault(p => p?.IsValid == true && p.UserId == uid);

        // userid
        if (int.TryParse(token, out var uid2))
            return Utilities.GetPlayers().FirstOrDefault(p => p?.IsValid == true && p.UserId == uid2);

        // SteamID64
        if (ulong.TryParse(token, out var sid64) && token.Length >= 17)
            return Utilities.GetPlayers().FirstOrDefault(p => p?.IsValid == true && p.SteamID == sid64);

        // Name
        var name = NormalizeName(token);

        var exactMatches = Utilities.GetPlayers()
            .Where(p => p?.IsValid == true &&
                        NormalizeName(p.PlayerName) == name)
            .ToList();

        if (exactMatches.Count == 1)
            return exactMatches[0];

        if (exactMatches.Count > 1)
        {
            Server.PrintToConsole($"[HLstats{ChatColors.Red}Z{ChatColors.Default}] Multiple players have that name. Use #userid or !menu.");
            return null;
        }

        return null;
    }

    public static void SendPrivateChat(CCSPlayerController player, string message)
    {
        player.PrintToChat($"{message}");
    }

    public static void SendChatToAll(string message)
    {
        Server.PrintToChatAll($"{message}");
    }

    public void BroadcastCenterMessage(string message, int durationInSeconds = 5)
    {
        message = message.Replace(
            "HLstatsZ",
            "<font color='#FFFFFF'><b>HLstats</b></font><font color='#FF2A2A'><b>Z</b></font>");
        message = message.Replace(
            "HLstatsX:CE",
            "<font color='#FFFFFF'><b>HLstats</b></font><font color='#3AA0FF'><b>X</b></font><font color='#FFFFFF'>:CE</font>");

        string htmlContent = $"<font color='#FFFFFF'>{message}</font>";

        var menu = new CenterHtmlMenu(htmlContent, this)
        {
            ExitButton = false
        };

        foreach (var p in Utilities.GetPlayers())
            if (p?.IsValid == true && !_menuManager._activeMenus.TryGetValue(p.SteamID, out var mmenu))
                menu.Open(p!);

        _ = new GameTimer(durationInSeconds, () =>
        {
            foreach (var p in Utilities.GetPlayers())
                if (p?.IsValid == true && !_menuManager._activeMenus.TryGetValue(p.SteamID, out var mmenu))
                    MenuManager.CloseActiveMenu(p!);
        });
    }

    public static void ShowHintMessage(CCSPlayerController player, string message)
    {
        player.PrintToCenter($"{message}");
    }

    // ------------------ Console Commands ------------------
   //public HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
   //{
   //    var player = Utilities.GetPlayerFromUserid(@event.Userid);
   //    if (player == null || !player.IsValid) return HookResult.Continue;
   //
   //    var originalMessage = @event.Text?.Trim() ?? "";
   //    var message = originalMessage.ToLower();
   //
   //    if (string.IsNullOrEmpty(message)) return HookResult.Continue;
   //
   //    bool isPrefixed = message.StartsWith("/") || message.StartsWith("!");
   //    if (isPrefixed)
   //        message = message.Substring(1); // Strip prefix for command handling
   //
   //        var validCommands = new[] {
   //            "top10", "rank", "session", "weaponstats",
   //            "accuracy", "next", "clans", "commands", "hlx_menu", "menu"
   //        };
   //
   //        if (validCommands.Contains(message) || Regex.IsMatch(message, @"^top\d{1,2}$"))
   //        {
   //
   //            if (message == "hlx_menu" || message == "menu")
   //            {
   //                var content = "->1 - Menu\n1. Rank\n2. TOP 10\n3. Next Player\n4. Admin";
   //                var callbacks = new Dictionary<string, Action<CCSPlayerController>>(StringComparer.OrdinalIgnoreCase)
   //                {
   //                    { "1. Rank", p => _ = SendLog(p, "rank", "say") },
   //                    { "2. TOP 10", p => _ = SendLog(p, "top10", "say") },
   //                    { "3. Next Player", p => _ = SendLog(p, "next", "say") },
   //                    { "4. Admin", p => AdminMenu(p) }
   //                };
   //                Instance?._menuManager.Open(player, content, 0, callbacks);
   //                return HookResult.Handled;
   //            }
   //
   //            if (isPrefixed)
   //            {
   //                _ = SendLog(player, message, "say");
   //                return HookResult.Handled;
   //            }
   //
   //            DispatchHLXEvent("psay", player, message);
   //            return HookResult.Handled;
   //
   //        }
   //
   //
   //    return HookResult.Continue;
   //}

    private HookResult ComamndListenerHandler(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !SourceBans._userCache.TryGetValue(player.SteamID, out var userData))
            return HookResult.Continue;

        var raw = info.ArgCount > 1 ? (info.GetArg(1) ?? string.Empty) : string.Empty;
        raw = raw.Trim();

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw.Substring(1, raw.Length - 2);

        bool silenced = raw.StartsWith("/");
        bool prefixed = raw.StartsWith("!") || silenced;
        string text = prefixed ? raw.Substring(1) : raw;

        var parts = text.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return HookResult.Continue;

        var cmd = parts[0].ToLowerInvariant();
        var args  = parts.Length > 1 ? parts[1].Trim() : "";

        // ---- Handle Public Command ----
        if ((cmd == "menu" || cmd == "hlx_menu") && parts.Length == 1)
        {
            if (Instance!._menuManager._activeMenus.TryGetValue(player.SteamID, out var menu))
            {
                Instance!._menuManager.DestroyMenu(player);
            }
            var builder = new HLZMenuBuilder("Main Menu")
                             .Add("Rank", p => _ = SendLog(p, "rank", "say"))
                             .Add("TOP 10", p => _ = SendLog(p, "top10", "say"))
                             .Add("Vote", p => VoteMenu(p));
            if (userData.IsAdmin)
                builder.Add("Admin", p => AdminMenu(p));

            builder.Open(player, Instance!._menuManager);

            return HookResult.Handled;
        }

        if (cmd == "map" && prefixed)
        {
            if (!userData.IsAdmin) return HookResult.Continue;
            if (parts.Length == 1)
            {
                MapMenu(player);
                return HookResult.Handled;
            }
            var map = text.Substring(3).Trim();
            AdminAction(player,cmd,player,$"{map}",0);
            return HookResult.Handled;
        }

        // ---- Handle log to HLstatsZ Daemon ----
        var Cmds = new[]
        {
            "top10","rank","session","weaponstats","accuracy","next","clans"
        };

        if (silenced && parts.Length == 1 && (Cmds.Contains(cmd, StringComparer.OrdinalIgnoreCase) || Regex.IsMatch(cmd, @"^top\d{1,2}$", RegexOptions.CultureInvariant)))
        {
            _ = SendLog(player, cmd, "say");
            return HookResult.Handled;
        }

        // ---- Handle Gag ----
        if ((userData.Ban & SourceBans.BanType.Gag)>0) return HookResult.Handled;

        // ---- Handle Admin command ----
        if (!userData.IsAdmin) return HookResult.Continue;

        if (cmd == "say" && prefixed && !string.IsNullOrWhiteSpace(args))
        {
            if (!string.IsNullOrWhiteSpace(args))
                SendChatToAll($"[HLstats{ChatColors.Red}Z{ChatColors.Default}] {args}");
            return HookResult.Handled;
        }

        if (cmd == "admin" && prefixed && string.IsNullOrWhiteSpace(args))
        {
            AdminMenu(player);
            return HookResult.Handled;
        }

        if (cmd == "kick" && prefixed)
        {
            if (parts.Length == 1)
            {
                AdminPlayer(player);
                return HookResult.Handled;
            }
            if (parts.Length < 3) 
            {
                SendPrivateChat(player, $"[HLstats{ChatColors.Red}Z{ChatColors.Default}] Usage: !kick <#userid|name> <reason>, or type !menu");
                return HookResult.Handled;
            }
            var who    = parts[1];
            var reason = parts.Length >= 3 ? (" (" + parts[2] + ")") : "";
            var target = FindTarget(who);
            if (target != null)
            {
                AdminAction(player,"kick",target,reason);
            }
            else
            {
                SendPrivateChat(player, $"[HLstats{ChatColors.Red}Z{ChatColors.Default}] Target '{who}' not found. Use !menu");
            }
            return HookResult.Handled;
        }

        if ((cmd == "ban" || cmd == "gag" || cmd == "mute" || cmd == "silence") ||
            (cmd == "unban" || cmd == "ungag" || cmd == "unmute" || cmd == "unsilence") && prefixed)
        {
            var reason = parts.Length >= 4 ? (" (" + parts[3] + ")") : "";
            var length = parts.Length >= 2 ?  parts[2] : "";
            if (parts.Length < 3 || !(int.TryParse(length, out int min) && min >= 0)) 
            {
                SendPrivateChat(player, $"[HLstats{ChatColors.Red}Z{ChatColors.Default}] Usage: !{cmd} <#userid|name> <minutes|0> <reason>, or type !menu");
                return HookResult.Handled;
            }
            var who    = parts[1];
            var target = FindTarget(who);
            if (target != null && target.IsValid)
            {
                AdminAction(player,cmd,target,reason,min);
            }
            else
            {
                SendPrivateChat(player, $"[HLstats{ChatColors.Red}Z{ChatColors.Default}] Target '{who}' not found. Use !menu");
            }
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    [ConsoleCommand("hlx_sm_psay")]
    public void OnHlxSmPsayCommand(CCSPlayerController? _, CommandInfo command)
    {
        if (command.ArgCount < 2) return; // hlx_sm_psay "1" 1 "message"

        var arg = command.ArgByIndex(1);
        var message = command.ArgByIndex(command.ArgCount - 1);

        string[] privateOnlyPatterns =
        {
            "kills to get regular points",
            "You have been banned"
        };
        bool isPrivateOnly = privateOnlyPatterns.Any(p => message?.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
        // Broadcast to all
        if (Config.HLstatsZ.BroadcastAll == 1 && !isPrivateOnly)
        {
            var hash = $"ALL:{message}";
            if (_lastPsayHash == hash) return;

            _lastPsayHash = hash;
            DispatchHLXEvent("say", null, message);
            return;
        }

        // users?
        var userIds = new List<int>();
        foreach (var idStr in arg.Split(','))
        {
            if (int.TryParse(idStr, out var id)) userIds.Add(id);
        }

        // Broadcast to user
        foreach (var userid in userIds)
        {
            var target = FindTarget(userid);
            if (target == null || !target.IsValid) continue;

            var hash = $"{userid}:{message}";
            if (_lastPsayHash == hash) continue;

            _lastPsayHash = hash;
            DispatchHLXEvent("psay", target, message);
        }
    }

    [ConsoleCommand("hlx_sm_csay")]
    public void OnHlxSmCsayCommand(CCSPlayerController? _, CommandInfo command)
    {
        var message = command.ArgByIndex(1);
        DispatchHLXEvent("csay", null, message);
    }

    [ConsoleCommand("hlx_sm_hint")]
    public void OnHlxSmHintCommand(CCSPlayerController? _, CommandInfo command)
    {
        if (!int.TryParse(command.ArgByIndex(1), out var userid)) return;

        var message = command.ArgByIndex(command.ArgCount - 1);
        var target  = FindTarget(userid);
        if (target == null || !target.IsValid) return;
        DispatchHLXEvent("hint", target, message);
    }

    [ConsoleCommand("hlx_sm_msay")]
    public void OnHlxSmMsayCommand(CCSPlayerController? _, CommandInfo command)
    {
        if (!int.TryParse(command.ArgByIndex(2), out var userid)) return;

        var message = command.ArgByIndex(command.ArgCount - 1);
        var target  = FindTarget(userid);
        if (target == null || !target.IsValid) return;
        DispatchHLXEvent("msay", target, message);
    }

    // --------------------- Menu ---------------------
    private const int PollInterval = 2; 
    private int _tickCounter;
    private readonly Dictionary<ulong, PlayerButtons> _lastButtons = new();

    private void OnTick() {
        if (_menuManager._activeMenus.Count == 0) return;
        if (++_tickCounter % PollInterval != 0)
            return;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!_menuManager._activeMenus.TryGetValue(player.SteamID, out var menu)) continue;
            var steamId = player.SteamID;
            if (player == null || !player.IsValid)
            {
                _menuManager.DestroyMenu(player!);
                _lastButtons.Remove(steamId);
                continue;
            }
            var current = player.Buttons;
            var last = _lastButtons.TryGetValue(steamId, out var prev) ? prev : PlayerButtons.Cancel;

            if (current.HasFlag(PlayerButtons.Forward) && !last.HasFlag(PlayerButtons.Forward))
                HandleWasdPress(player, "W");

            if (current.HasFlag(PlayerButtons.Back) && !last.HasFlag(PlayerButtons.Back))
                HandleWasdPress(player, "S");

            if (current.HasFlag(PlayerButtons.Moveleft) && !last.HasFlag(PlayerButtons.Moveleft))
                HandleWasdPress(player, "A");

            if (current.HasFlag(PlayerButtons.Moveright) && !last.HasFlag(PlayerButtons.Moveright))
                HandleWasdPress(player, "D");

            if (current.HasFlag(PlayerButtons.Use) && !last.HasFlag(PlayerButtons.Use))
                HandleWasdPress(player, "E");

            _lastButtons[steamId] = current;

        }
    }

    private void HandleWasdPress(CCSPlayerController player, string key)
    {
        switch (key)
        {
            case "W": _menuManager.HandleNavigation(player,-1); break;
            case "S": _menuManager.HandleNavigation(player,+1); break;
            case "A": _menuManager.HandleBack(player); break;
            case "D": _menuManager.HandlePage(player,+1); break;
            case "E": _menuManager.HandleSelect(player); break;
        }
    }

    public static void AdminMenu(CCSPlayerController player)
    {
        if (!SourceBans._enabled || player == null || !player.IsValid)
            return;

        var builder = new HLZMenuBuilder("Admin Menu")
                     .Add("Players", _ => AdminPlayer(player))
                     .Add("Remove Ban (session)", _ => AdminPlayer(player,1))
                     .Add("Map Change", _ => MapMenu(player));
        builder.Open(player, Instance!._menuManager);

    }

    public static void VoteMenu(CCSPlayerController player)
    {
        if (!SourceBans._enabled || player == null || !player.IsValid)
            return;

        var builder = new HLZMenuBuilder("Vote Menu") 
                      .Add("Kick player", _ => VotePlayer(player))
                      .Add("Change Map", _ => VoteMap(player))
                      .Add("Active Vote", _ => VoteActive(player));

        builder.Open(player, Instance!._menuManager);
    }

    public static void VotePlayer(CCSPlayerController player)
    {
        if (!SourceBans._enabled || player == null || !player.IsValid)
            return;
        var name = "";
        var builder = new HLZMenuBuilder("Vote Kick"); 
        foreach (var target in Utilities.GetPlayers())
        {
            name = Instance?.Trunc(target.PlayerName, 20);
            builder.AddNoOp($"{name}");
        }

        builder.Open(player, Instance!._menuManager);
    }

    public static void VoteMap(CCSPlayerController player)
    {
        if (!SourceBans._enabled || player == null || !player.IsValid)
            return;
        var maps = SourceBans.GetAvailableMaps(Instance!.Config, false);

        var builder = new HLZMenuBuilder("Vote Map"); 
        foreach (var entry in maps)
        {
            string label = entry.IsSteamWorkshop ? $"[WS] {entry.DisplayName}" : entry.DisplayName;
            builder.AddNoOp(label);
        }

        builder.Open(player, Instance!._menuManager);
    }

    public static void VoteActive(CCSPlayerController player)
    {
        if (!SourceBans._enabled || player == null || !player.IsValid)
            return;
        var maps = SourceBans.GetAvailableMaps(Instance!.Config, false);

        var builder = new HLZMenuBuilder("Active vote").WithoutNumber(); 
            builder.AddNoOp("There is no active vote");

        builder.Open(player, Instance!._menuManager);
    }

    public static void MapMenu(CCSPlayerController player)
    {
        if (!SourceBans._enabled || player == null || !player.IsValid)
            return;

        bool isAdmin = SourceBans.isAdmin(player);
        var maps = SourceBans.GetAvailableMaps(Instance!.Config, isAdmin);

        var builder = new HLZMenuBuilder("Map Menu");
        foreach (var entry in maps)
       {
            string label = entry.IsSteamWorkshop ? $"[WS] {entry.DisplayName}" : entry.DisplayName;
            builder.Add(label, _ => AdminConfirm(player, player, $"map 1 {entry.DisplayName}"));
        }

        builder.Open(player, Instance!._menuManager);
    }

    public static void AdminPlayer(CCSPlayerController player, int type = 0)
    {
        if (!SourceBans._enabled || player == null || !player.IsValid)
            return;

        var title = type == 0 ? "Admin Players" : "Admin Unban";
        var builder = new HLZMenuBuilder($"title").WithoutNumber();
        var count =0;
        var name = "";
        var label = "";
        foreach (var target in Utilities.GetPlayers())
        {
            if (target?.IsValid != true) continue;
            SourceBans._userCache.TryGetValue(target.SteamID, out var userData);

            switch (type)
            {
                case 0:
                    if ((userData.Ban & (SourceBans.BanType.Kick | SourceBans.BanType.Ban))>0) continue;
                    name = Instance?.Trunc(target.PlayerName, 20);
                    label = $"{target.Slot} - {name}";
                    //if (player != target)
                        builder.Add(label, _ => AdminCMD(player, target));
                    //else
                    //    builder.AddNoOp(label);
                break;
                case 1:
                    if ((userData.Ban ^ SourceBans.BanType.None)==0) continue;
                    count++;
                    name = Instance?.Trunc(target.PlayerName, 20);
                    label = $"{target.Slot} - {name}";
                    builder.Add(label, _ => AdminCMD2(player, target));
                break;
                default: break;

            }
        }
        if (count == 0 && type == 1)
            builder.AddNoOp("No players have a ban");
        builder.Open(player, Instance!._menuManager);

    }

    public static void AdminCMD(CCSPlayerController player,CCSPlayerController target)
    {
        if (!SourceBans._enabled || player == null || !player.IsValid)
            return;

        var name = Instance?.Trunc(target.PlayerName,10);
        var builder = new HLZMenuBuilder($"{name}");

        builder.Add("Kick", _ => AdminConfirm(player,target,"kick"));
        if (!target.IsBot) {
            builder.Add("Ban", _ => AdminCMD1(player,target,"Ban"));
            builder.Add("Gag (Disable Chat)", _ => AdminCMD1(player,target,"Gag"));
            builder.Add("Mute (Disable Voice)", _ => AdminCMD1(player,target,"Mute"));
            builder.Add("Silence (Disable Voice/Chat", _ => AdminCMD1(player,target,"Silence"));
        }
        builder.Open(player, Instance!._menuManager);

    }

    public static void AdminCMD1(CCSPlayerController player,CCSPlayerController target, string cmd)
    {
        if (!SourceBans._enabled || player == null || !player.IsValid)
            return;

        var name = Instance?.Trunc(target.PlayerName,10);
        var builder = new HLZMenuBuilder($"{name}");

        builder.Add($"{cmd} 15 minutes", _ => AdminConfirm(player,target,$"{cmd} 900"));
        builder.Add($"{cmd} 30 minutes", _ => AdminConfirm(player,target,$"{cmd} 1800"));
        builder.Add($"{cmd} 60 minutes", _ => AdminConfirm(player,target,$"{cmd} 3600"));
        builder.Add($"{cmd} 24 hours", _ => AdminConfirm(player,target,$"{cmd} 1440"));
        builder.Add($"{cmd} permanently", _ => AdminConfirm(player,target,$"{cmd} 0"));

        builder.Open(player, Instance!._menuManager);
    }

    public static void AdminCMD2(CCSPlayerController player,CCSPlayerController target)
    {
        if (!SourceBans._enabled || player == null || !player.IsValid)
            return;

        var name = Instance?.Trunc(target.PlayerName,10);
        var builder = new HLZMenuBuilder($"{name}");
        SourceBans._userCache.TryGetValue(target.SteamID, out var targetData);
        DateTime now = DateTime.UtcNow; //DateTime.MaxValue
        if ((targetData.Ban & SourceBans.BanType.Kick)>0) {
            var remaining = targetData.ExpiryBan == DateTime.MaxValue ? "permanently" : SourceBans.FormatTimeLeft(targetData.ExpiryBan-now);
            builder.Add($"Remove Kick ({remaining})", _ => AdminConfirm(player,target,"unkick"));
        }
        if ((targetData.Ban & SourceBans.BanType.Ban)>0) {
            var remaining = targetData.ExpiryBan == DateTime.MaxValue ? "permanently" : SourceBans.FormatTimeLeft(targetData.ExpiryBan-now);
            builder.Add($"Remove Ban ({remaining})", _ => AdminConfirm(player,target,"unban"));
        }
        if ((targetData.Ban & SourceBans.BanType.Gag)>0) {
            var remaining = targetData.ExpiryBan == DateTime.MaxValue ? "permanently" : SourceBans.FormatTimeLeft(targetData.ExpiryComm - now);
            builder.Add($"Remove Gag ({remaining})", _ => AdminConfirm(player,target,"ungag"));
        }
        if ((targetData.Ban & SourceBans.BanType.Mute)>0) {
            var remaining = targetData.ExpiryBan == DateTime.MaxValue ? "permanently" : SourceBans.FormatTimeLeft(targetData.ExpiryComm-now);
            builder.Add($"Remove Mute ({remaining})", _ => AdminConfirm(player,target,"unmute"));
        }
        if ((targetData.Ban & SourceBans.BanType.Gag)>0 && (targetData.Ban & SourceBans.BanType.Mute)>0) {
            var remaining = targetData.ExpiryBan == DateTime.MaxValue ? "permanently" : SourceBans.FormatTimeLeft(targetData.ExpiryComm-now);
            builder.Add($"Remove Silence ({remaining})", _ => AdminConfirm(player,target,"unsilence"));
        }
 
        builder.Open(player, Instance!._menuManager);

    }

    public static void AdminConfirm(CCSPlayerController player,CCSPlayerController target, string cmd)
    {
        if (!SourceBans._enabled || player == null || !player.IsValid)
            return;

        var name = Instance?.Trunc(target.PlayerName,10);
        var builder = new HLZMenuBuilder($"{name}");
        var parts  = cmd.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        int length = parts.Length > 1 ?  int.Parse(parts[1]) : 0;
        string? level = parts.Length > 2 ? parts[2] : null;

        builder.Add("Confirm", _ => AdminAction(player,parts[0],target,level,length));

        builder.Open(player, Instance!._menuManager);

    }

    public static bool AdminAction(CCSPlayerController admin,string action, CCSPlayerController? target = null, string? args = null, int durationSeconds = 0)
    {
        if (admin == null || !admin.IsValid) return false;
        if (!SourceBans.isAdmin(admin)) return false;
        var cmd    = action.Trim().ToLowerInvariant();
        var reason = (args ?? "").Trim();
        var type = SourceBans.BanType.None;
        var page = 0;
        if (string.IsNullOrEmpty(reason)) reason = "Admin action";

        switch (cmd)
        {
            case "kick":
                if (target == null || !target.IsValid) return false;
                Server.ExecuteCommand($"kickid {target.UserId} \"Kicked {target.PlayerName} ({reason})\"");
                SourceBans.UpdateBanUser(target, SourceBans.BanType.Kick, DateTime.UtcNow.AddMinutes(2));
                SendChatToAll($"[HLstats{ChatColors.Red}Z{ChatColors.Default}] {admin.PlayerName} kicked {target.PlayerName} ({reason})");
            break;
            //case "ban":
            //    if (target == null || !target.IsValid) return false;
            //    _ = SourceBans.WriteBan(target, admin, SourceBans.BanType.Ban, durationSeconds, reason);
            //    Server.ExecuteCommand($"kickid {target.UserId} \"Banned {target.PlayerName} ({reason})\"");
            //    SourceBans.UpdateBanUser(target, SourceBans.BanType.Ban, durationSeconds == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(durationSeconds));
            //    SendChatToAll($"[HLstats{ChatColors.Red}Z{ChatColors.Default}] {admin.PlayerName} Banned {target.PlayerName} ({reason})");
            //break;
            case "map": 
                page = 2;
                if (args == null) return false;
                var availableMaps = SourceBans.GetAvailableMaps(Instance!.Config, true);
                var match = availableMaps.FirstOrDefault(m =>
                    string.Equals(m.DisplayName, args, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.MapName, args, StringComparison.OrdinalIgnoreCase) ||
                    (m.WorkshopId != null && string.Equals(m.WorkshopId, args, StringComparison.OrdinalIgnoreCase)));
                if (match == null)
                {
                    SendPrivateChat(admin, $"[HLstats{ChatColors.Red}Z{ChatColors.Default}] Map '{args}' not found in config. Use !menu");
                    return false;
                }
                SendChatToAll($"[HLstats{ChatColors.Red}Z{ChatColors.Default}] {admin.PlayerName} changed map to {match.DisplayName}");
                var command = match.IsSteamWorkshop ? $"host_workshop_map {match.WorkshopId}" :
                                                      $"changelevel {match.MapName}";
                Server.NextFrame(() => Server.ExecuteCommand($"{command}"));
            break;
            case "ban":
            case "gag":
            case "mute":
            case "silence":
                if (target == null || !target.IsValid) return false;
                type = cmd == "ban" ? SourceBans.BanType.Ban :
                       cmd == "gag" ? SourceBans.BanType.Gag :
                       cmd == "mute" ? SourceBans.BanType.Mute : SourceBans.BanType.Silence;
                if (cmd == "mute" || cmd == "silence")
                    target.VoiceFlags = VoiceFlags.Muted;
                _ = SourceBans.WriteBan(target, admin, type, durationSeconds, reason);
                SourceBans.UpdateBanUser(target, type, durationSeconds == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(durationSeconds));
                SendChatToAll($"[HLstats{ChatColors.Red}Z{ChatColors.Default}] {admin.PlayerName} '{cmd.ToUpper()}' {target.PlayerName} ({reason})");
            break;
            case "ungag":
            case "unmute":
            case "unsilence":
            case "unban":
                if (target == null || !target.IsValid) return false;
                page = 1;
                type = cmd == "ungag" ? SourceBans.BanType.Gag :
                       cmd == "unmute" ? SourceBans.BanType.Mute :
                       cmd == "unsilence" ?  SourceBans.BanType.Silence : SourceBans.BanType.Ban;
                if (cmd == "unmute" || cmd == "unsilence")
                    target.VoiceFlags = VoiceFlags.Normal;
                _ = SourceBans.WriteUnBan(target, admin, type, reason);
                SourceBans.UpdateBanUser(target, type, DateTime.UtcNow,true);
                SendChatToAll($"[HLstats{ChatColors.Red}Z{ChatColors.Default}] {admin.PlayerName} '{cmd.ToUpper()}' {target.PlayerName} ({reason})");
            break;
            default: break;
        }

        if (Instance!._menuManager._activeMenus.TryGetValue(admin.SteamID, out var menu))
        {

            if (page == 0 || page == 1)
            {
                    Instance!._menuManager.HandleBack(admin,false);
                    Instance!._menuManager.HandleBack(admin,false);
                    Instance!._menuManager.HandleBack(admin,false);
                    Instance!._menuManager.HandleBack(admin,false);
                    AdminPlayer(admin,page);
            }

           if ( page == 2)
           {
               Instance!._menuManager.DestroyMenu(admin);
           }

        }
        return true;
    }

    // ------------------ Event Handler ------------------
    public HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        var player = Utilities.GetPlayerFromUserid(@event.Userid);
        if (player == null || !player.IsValid) return HookResult.Continue;

        var originalMessage = @event.Text?.Trim() ?? "";
        var message = originalMessage.ToLower();

        if (string.IsNullOrEmpty(message)) return HookResult.Continue;

        bool isPrefixed = message.StartsWith("/") || message.StartsWith("!");
        if (isPrefixed)
            message = message.Substring(1); // Strip prefix for command handling

            var validCommands = new[] {
                "top10", "rank", "session", "weaponstats",
                "accuracy", "next", "clans", "commands", "hlx_menu", "menu"
            };

            if (validCommands.Contains(message) || Regex.IsMatch(message, @"^top\d{1,2}$"))
            {

                if (message == "hlx_menu" || message == "menu")
                {
                    var content = "->1 - Menu\n1. Rank\n2. TOP 10\n3. Next Player\n4. Admin";
                    var callbacks = new Dictionary<string, Action<CCSPlayerController>>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "1. Rank", p => _ = SendLog(p, "rank", "say") },
                        { "2. TOP 10", p => _ = SendLog(p, "top10", "say") },
                        { "3. Next Player", p => _ = SendLog(p, "next", "say") },
                        { "4. Admin", _ => { /* no-op */ } }
                    };
                    Instance?._menuManager.Open(player, content, 0, callbacks);
                    return HookResult.Handled;
                }

                if (isPrefixed)
                {
                    _ = SendLog(player, message, "say");
                    return HookResult.Handled;
                }

                DispatchHLXEvent("psay", player, message);
                return HookResult.Handled;

            }


        return HookResult.Continue;

    }

    public HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        var reason = @event.Reason;
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;

        string reasonText = reason switch
        {
            1  => "with most eliminations",
            2  => "with bomb planted",
            3  => "with bomb defused",
            4  => "with hostage rescued",
            11 => "with HE grenade",
            14 => "with a clutch defuse",
            15 => "with most kills",
            _  => $"with event {reason}"
        };

        _ = SendLog(player, $"round_mvp {reasonText}", "triggered");
        return HookResult.Continue;
    }

    public HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;

        _ = SendLog(player, "Defused_The_Bomb", "triggered");
        return HookResult.Continue;
    }

    public HookResult OnPlayerConnect(EventPlayerConnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null) return HookResult.Continue;
    
        _ = SourceBans.isAdmin(player);
    
        return HookResult.Continue;
    }

    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null) return HookResult.Continue;

        SourceBans.isAdmin(player);
        if (SourceBans._userCache.TryGetValue(player.SteamID, out var userData))
        {
            if ((userData.Ban & (SourceBans.BanType.Ban | SourceBans.BanType.Kick))>0)
            {
                DateTime now = DateTime.UtcNow;
                if (userData.ExpiryBan > now)
                {
                    var timeleft = SourceBans.FormatTimeLeft(userData.ExpiryBan - DateTime.UtcNow);
                    var remain = DateTime.MaxValue > userData.ExpiryBan ? $"({timeleft} remaining)" : "(permanently)";
                    Server.ExecuteCommand($"kickid {player.UserId} \"You are banned from this server {remain}\"");
                    Server.PrintToChatAll($"[HLstats{ChatColors.Red}Z{ChatColors.Default}] {player.PlayerName} tried to join while banned {remain}");
                }
            }
            if ((userData.Ban & (SourceBans.BanType.Mute | SourceBans.BanType.Silence))>0)
            {
                var timeleft = SourceBans.FormatTimeLeft(userData.ExpiryBan - DateTime.UtcNow);
                var remain = DateTime.MaxValue > userData.ExpiryBan ? $"({timeleft} remaining)" : "(permanently)";
                player.VoiceFlags = VoiceFlags.Muted;
                player.PrintToChat($"[HLstats{ChatColors.Red}Z{ChatColors.Default}] {player.PlayerName}, you are muted {remain}");
            }
        }
        return HookResult.Continue;
    }

    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid) {
            _menuManager.DestroyMenu(player);
        }
        return HookResult.Continue;
    }

    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid)
            _menuManager.DestroyMenu(player);
        return HookResult.Continue;
    }

}
