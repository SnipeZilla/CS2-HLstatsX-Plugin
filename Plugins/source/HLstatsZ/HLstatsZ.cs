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
using System.Net.Sockets;
using System.Net;
using System.Text;

namespace HLstatsZ;

public class HLstatsZConfig : IBasePluginConfig
{
    [JsonPropertyName("Log_Address")] public string Log_Address { get; set; } = "127.0.0.1";
    [JsonPropertyName("Log_Port")] public int Log_Port { get; set; } = 27500;
    [JsonPropertyName("BroadcastAll")] public int BroadcastAll { get; set; } = 0;
    public int Version { get; set; } = 1;
}

public class HLstatsZ : BasePlugin, IPluginConfig<HLstatsZConfig>
{
    public static HLstatsZ? Instance;
    public HLstatsZConfig Config { get; set; } = new();

    private string? _lastPsayHash;

    public override string ModuleName => "HLstatsZ";
    public override string ModuleVersion => "0.3.0";
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
        if (isPrefixed)
            message = message.Substring(1); // Strip prefix for command handling

        var validCommands = new[] {
            "top10", "rank", "session", "weaponstats",
            "accuracy", "clans", "commands", "menu"
        };

        if (validCommands.Contains(message) || Regex.IsMatch(message, @"^top\d{1,2}$"))
        {
            if (isPrefixed)
            {
                SendLog(player, message);
                return HookResult.Handled;
            }

            if (message == "menu")
            {
                new HLXMenu().ShowMainMenu(player);
            }
            else
            {
                DispatchHLXEvent("psay", player, message);
            }
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    // ------------------ Console Commands ------------------

    [ConsoleCommand("hlx_sm_psay")]
    public void OnHlxSmPsayCommand(CCSPlayerController? _, CommandInfo command)
    {
        var arg = command.ArgByIndex(1);
        int.TryParse(arg, out var userid);
        var message = command.ArgByIndex(command.ArgCount - 1);
        var target  = FindPlayerByUserId(userid);
        var hash = $"{target?.UserId}:{message}";
        if (_lastPsayHash == hash) {
           Instance?.Logger.LogInformation("Duplicate message: {hash}", hash);
           return;
        }
        _lastPsayHash = hash;
        int? optionalArg = null;
        if (command.ArgCount > 2) {
            var arg2 = command.ArgByIndex(2);
            if (int.TryParse(arg2, out var parsed))
            {
                optionalArg = parsed;
            }
        }
        Server.NextFrame(() =>
        {
            if (optionalArg == 1 && Config.BroadcastAll == 1) {
                DispatchHLXEvent("say", null, message);
            } else {
                DispatchHLXEvent("psay", target, message);
            }
        });
    }

    [ConsoleCommand("hlx_sm_csay")]
    public void OnHlxSmCsayCommand(CCSPlayerController? _, CommandInfo command)
    {
        var message = command.ArgByIndex(1);
        Server.NextFrame(() =>
        {
            DispatchHLXEvent("csay", null, message);
        });
    }

    [ConsoleCommand("hlx_sm_hint")]
    public void OnHlxSmHintCommand(CCSPlayerController? _, CommandInfo command)
    {
        if (!int.TryParse(command.ArgByIndex(1), out var userid)) return;

        var message = command.ArgByIndex(command.ArgCount - 1);
        var target  = FindPlayerByUserId(userid);
        if (target == null) return;
        Server.NextFrame(() =>
        {
            DispatchHLXEvent("hint", target, message);
        });
    }

    [ConsoleCommand("hlx_sm_msay")]
    public void OnHlxSmMsayCommand(CCSPlayerController? _, CommandInfo command)
    {
        if (!int.TryParse(command.ArgByIndex(2), out var userid)) return;

        var message = command.ArgByIndex(command.ArgCount - 1);
        var target  = FindPlayerByUserId(userid);
        if (target == null) return;
        Server.NextFrame(() =>
        {
            DispatchHLXEvent("msay", target, message);
        });
    }

    // ------------------ Core Logic ------------------

    private void SendLog(CCSPlayerController player, string Message)
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
        var hostPort = ConVar.Find("hostport")?.GetPrimitiveValue<int>() ?? 27015;
        var logLine = $"L {DateTime.Now:MM/dd/yyyy - HH:mm:ss}: \"{name}<{userid}><[U:1:{steamid}]><{team}>\" say \"{Message}\""; // / Extra log for hidden msg
        try
        {
            var localEP = new IPEndPoint(IPAddress.Any, hostPort);
            var client = new UdpClient(localEP);
            var bytes = Encoding.UTF8.GetBytes(logLine);
            client.Send(bytes, bytes.Length, Config.Log_Address, Config.Log_Port);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HLstatsZ] UDP log send failed: {ex.Message}");
        }
    }

    public static void DispatchHLXEvent(string type, CCSPlayerController? player, string message)
    {
        if (Instance == null) return;

        switch (type)
        {
            case "psay":
                if (player != null) SendPrivateChat(player, message);
                else Instance?.Logger.LogInformation($"Player is null from message: {message}");
                break;
            case "csay":
                BroadcastCenterMessage(message);
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

    public static void BroadcastCenterMessage(string message)
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p?.IsValid == true)
                p.PrintToCenter($"{message}");
        }
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
