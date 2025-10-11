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

public class HLstatsZConfig : IBasePluginConfig
{
    [JsonPropertyName("Log_Address")] public string Log_Address { get; set; } = "127.0.0.1";
    [JsonPropertyName("Log_Port")] public int Log_Port { get; set; } = 27500;
    [JsonPropertyName("BroadcastAll")] public int BroadcastAll { get; set; } = 0;
    [JsonPropertyName("ServerAddr")] public string ServerAddr { get; set; } = "";
    public int Version { get; set; } = 1;
}

public class HLstatsZ : BasePlugin, IPluginConfig<HLstatsZConfig>
{
    public static HLstatsZ? Instance;
    private static readonly HttpClient httpClient = new();
    public HLstatsZConfig Config { get; set; } = new();

    public static readonly Dictionary<int, HLZMenuManager> Players = new();

    private string? _lastPsayHash;

    public override string ModuleName => "HLstatsZ";
    public override string ModuleVersion => "2.0.0";
    public override string ModuleAuthor => "SnipeZilla";

    public void OnConfigParsed(HLstatsZConfig config)
    {
        Config = config;
        Console.WriteLine($"[HLstatsZ] Config loaded: {Config.Log_Address}:{Config.Log_Port}");
    }
    private HLZMenuManager _menuManager = null!;
    public override void Load(bool hotReload)
    {
        Instance = this;
        _menuManager = new HLZMenuManager(this);
        RegisterEventHandler<EventPlayerChat>(OnPlayerChat);
        RegisterEventHandler<EventRoundMvp>(OnRoundMvp);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterListener<Listeners.OnTick>(OnTick);

    }

    public override void Unload(bool hotReload)
    {
        DeregisterEventHandler<EventPlayerChat>(OnPlayerChat);
        DeregisterEventHandler<EventRoundMvp>(OnRoundMvp);
        DeregisterEventHandler<EventBombDefused>(OnBombDefused);
        DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RemoveListener<Listeners.OnTick>(OnTick);
    }
    // ------------------ Console Commands ------------------

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
        if (Config.BroadcastAll == 1 && !isPrivateOnly)
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
            var target = FindPlayerByUserId(userid);
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
        var target  = FindPlayerByUserId(userid);
        if (target == null || !target.IsValid) return;
        DispatchHLXEvent("hint", target, message);
    }

    [ConsoleCommand("hlx_sm_msay")]
    public void OnHlxSmMsayCommand(CCSPlayerController? _, CommandInfo command)
    {
        if (!int.TryParse(command.ArgByIndex(2), out var userid)) return;

        var message = command.ArgByIndex(command.ArgCount - 1);
        var target  = FindPlayerByUserId(userid);
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

        var serverAddr = Config.ServerAddr;
        if (string.IsNullOrWhiteSpace(serverAddr))
        {
            var hostPort = ConVar.Find("hostport")?.GetPrimitiveValue<int>() ?? 27015;
            var serverIP = GetLocalIPAddress();
            serverAddr = $"{serverIP}:{hostPort}";
        }

        var logLine = $"L {DateTime.Now:MM/dd/yyyy - HH:mm:ss}: \"{name}<{userid}><[U:1:{steamid}]><{team}>\" {verb} \"{message}\"";

        try
        {
            if (!httpClient.DefaultRequestHeaders.Contains("X-Server-Addr"))
                httpClient.DefaultRequestHeaders.Add("X-Server-Addr", serverAddr);

            var content = new StringContent(logLine, Encoding.UTF8, "text/plain");
            var response = await httpClient.PostAsync($"http://{Config.Log_Address}:{Config.Log_Port}/log", content);

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

    public static CCSPlayerController? FindPlayerByUserId(int userid)
    {
        return Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.UserId == userid);
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
            if (p?.IsValid == true)
                menu.Open(p!);

        _ = new GameTimer(durationInSeconds, () =>
        {
            foreach (var p in Utilities.GetPlayers())
                if (p?.IsValid == true)
                    MenuManager.CloseActiveMenu(p!);
        });
    }

    public static void ShowHintMessage(CCSPlayerController player, string message)
    {
        player.PrintToCenter($"{message}"); //something else for hint?
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
                        { "4. Admin", p => _ = SendLog(p, "admin", "say") }
                    };
                    Instance?._menuManager.Open(player, content, 0, callbacks);
                    
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

    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid)
            _menuManager.DestroyMenu(player);
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
