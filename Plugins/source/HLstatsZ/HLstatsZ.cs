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
    public HLstatsZConfig Config { get; set; } = new();

    private string? _lastPsayHash;

    public override string ModuleName => "HLstatsZ";
    public override string ModuleVersion => "1.3.1";
    public override string ModuleAuthor => "SnipeZilla";

    public void OnConfigParsed(HLstatsZConfig config)
    {
        Config = config;
        Console.WriteLine($"[HLstatsZ] Config loaded: {Config.Log_Address}:{Config.Log_Port}");
    }

    public override void Load(bool hotReload)
    {
        Instance = this;
        RegisterEventHandler<EventPlayerChat>(OnPlayerChat);
    }

    private HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        var player = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.UserId == @event.Userid);
        if (player == null) return HookResult.Continue;

        var originalMessage = @event.Text?.Trim() ?? "";
        var message = originalMessage.ToLower();

        if (string.IsNullOrEmpty(message)) return HookResult.Continue;

        bool isPrefixed = message.StartsWith("/") || message.StartsWith("!");
        if (isPrefixed) {
            message = message.Substring(1); // Strip prefix for command handling

        var validCommands = new[] {
            "top10", "rank", "session", "weaponstats",
            "accuracy", "clans", "commands", "hlx_menu"
        };

        if (validCommands.Contains(message) || Regex.IsMatch(message, @"^top\d{1,2}$"))
        {
            if (isPrefixed)
            {
                _ = SendLog(player, message);
                return HookResult.Handled;
            }

            if (message == "hlx_menu")
            {
                new HLXMenu().ShowMainMenu(player);
            }
            else
            {
                DispatchHLXEvent("psay", player, message);
            }
            return HookResult.Handled;
        }
}

        return HookResult.Continue;
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

    // ------------------ Core Logic ------------------

    public static string GetLocalIPAddress()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
        socket.Connect("8.8.8.8", 65530); // Google's DNS
        var endPoint = socket.LocalEndPoint as IPEndPoint;
        return endPoint?.Address.ToString() ?? "127.0.0.1";
    }

    private async Task SendLog(CCSPlayerController player, string message)
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

        var logLine = $"L {DateTime.Now:MM/dd/yyyy - HH:mm:ss}: \"{name}<{userid}><[U:1:{steamid}]><{team}>\" say \"{message}\"";

        try
        {
            using var httpClient = new HttpClient();
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
                if (player != null) openChatMenu(Instance, player, message);
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

    public static void openChatMenu(BasePlugin plugin, CCSPlayerController player, string content)
    {
        var rawLines = content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        var headingRaw = rawLines.FirstOrDefault() ?? "Stats";
        var heading = Regex.Replace(headingRaw, @"->\d+\s*-\s*", "").Trim();

        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\\n", "\n"); // sourcemod

        var lines = normalized.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        var menu = new ChatMenu($"hlx_menu_{player}")
        {
            Title = $"HLstats\x07Z \x01- {heading}",
            ExitButton  = true,
            TitleColor  = ChatColors.White,
            EnabledColor = ChatColors.Green,
            DisabledColor = ChatColors.Grey
        };

        foreach (var line in lines)
        {
            menu.AddMenuOption(line, (_, _) => { }, true);
        }

        menu.Open(player);
    }

    public class HLXMenu
    {
        private readonly string _menuIdPrefix = "hlx_menu";

        public void ShowMainMenu(CCSPlayerController player)
        {
            var menu = new ChatMenu($"{_menuIdPrefix}_{player.SteamID}")
            {
                Title = $"HLstats\x07Z \x01- Main Menu",
                ExitButton = true,
                EnabledColor = ChatColors.Green,
                DisabledColor = ChatColors.Grey
            };

            menu.AddMenuOption("- Top Players", (_, _) => OnTopPlayersSelected(player));
            menu.AddMenuOption("- Rank", (_, _) => OnWeaponStatsSelected(player));
            menu.AddMenuOption("- Accuracy", (_, _) => OnAccuracySelected(player));
            menu.AddMenuOption("- Session", (_, _) => OnClansSelected(player));
            menu.AddMenuOption("- Help", (_, _) => OnHelpSelected(player));

            menu.Open(player);
        }
        private static void OnTopPlayersSelected(CCSPlayerController player) => Instance?.SendLog(player, "top10");
        private static void OnWeaponStatsSelected(CCSPlayerController player) => Instance?.SendLog(player, "rank");
        private static void OnAccuracySelected(CCSPlayerController player) => Instance?.SendLog(player,  "accuracy");
        private static void OnClansSelected(CCSPlayerController player) => Instance?.SendLog(player, "session");
        private static void OnHelpSelected(CCSPlayerController player) => Instance?.SendLog(player, "help");
    }

}
