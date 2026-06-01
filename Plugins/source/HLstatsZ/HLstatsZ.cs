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
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
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
    [JsonPropertyName("HLZ_Prefix")] public string HLZ_Prefix { get; set; } = "";
    public int Version { get; set; } = 1;
}

public class HLstatsZ : BasePlugin, IPluginConfig<HLstatsZConfig>
{
    public static HLstatsZ? Instance;
    public HLstatsZConfig Config { get; set; } = new();

    private static readonly HttpClient httpClient = new();
    public static HttpClient Http => httpClient;
    public string Trunc(string s, int max=20)
        => s.Length > max ? s.Substring(0, Math.Max(0, max - 3)) + "..." : s;

    public static LogDispatcher? LogQueue;

    private static GameTimer? _centerHTML;

    public static string HLZ_Prefix = "";

    private struct WeaponStats
    {
        public int shots;
        public int hits;
        public int headshots;
        public int damage;
        public int kills;
        public int deaths;
    }

    private struct HitgroupStats
    {
        public int head;
        public int chest;
        public int stomach;
        public int leftarm;
        public int rightarm;
        public int leftleg;
        public int rightleg;
    }

    private static readonly Dictionary<ulong, Dictionary<string, WeaponStats>> _Statsme  = new();
    private static readonly Dictionary<ulong, Dictionary<string, HitgroupStats>> _Statsme2 = new();

    private string? _lastPsayHash;

    public override string ModuleName => "HLstatsZ";
    public override string ModuleVersion => "1.9";
    public override string ModuleAuthor => "SnipeZilla";

    public void OnConfigParsed(HLstatsZConfig config)
    {
        Config = config;
    }

    private HLZMenuManager _menuManager = null!;

    public override void Load(bool hotReload)
    {
        Instance = this;

        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventRoundMvp>(OnRoundMvp);
        RegisterEventHandler<EventBombAbortdefuse>(OnBombAbortdefuse);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);

        AddCommandListener(null, ComamndListenerHandler, HookMode.Pre);

        _menuManager = new HLZMenuManager(this);

        if (!string.IsNullOrWhiteSpace(Config.HLZ_Prefix))
        {
            HLZ_Prefix = Colors(" "+Config.HLZ_Prefix.Trim()+"\x01 ");
        }

        if (string.IsNullOrWhiteSpace(Config.ServerAddr))
        {
            var hostPort = ConVar.Find("hostport")?.GetPrimitiveValue<int>() ?? 27015;
            var serverIP = ConVar.Find("ip")?.StringValue ?? GetLocalIPAddress();
            Config.ServerAddr = $"{serverIP}:{hostPort}";
        }

            LogQueue = new LogDispatcher(Config.Log_Address, Config.Log_Port, Config.ServerAddr);

    }

    public override void Unload(bool hotReload)
    {

        DeregisterEventHandler<EventRoundEnd>(OnRoundEnd);
        DeregisterEventHandler<EventRoundMvp>(OnRoundMvp);
        DeregisterEventHandler<EventBombAbortdefuse>(OnBombAbortdefuse);
        DeregisterEventHandler<EventBombDefused>(OnBombDefused);
        DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        DeregisterEventHandler<EventWeaponFire>(OnWeaponFire);
        DeregisterEventHandler<EventPlayerHurt>(OnPlayerHurt);

        RemoveCommandListener(null!, ComamndListenerHandler, HookMode.Pre);

        LogQueue?.Dispose();
    }


    private static readonly Dictionary<string, char> ColorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"]     = '\x01',
        ["white"]       = '\x01',
        ["darkred"]     = '\x02',
        ["green"]       = '\x04',
        ["lightyellow"] = '\x09',
        ["yellow"]      = '\x09',
        ["lightblue"]   = '\x0B',
        ["blue"]        = '\x0B',
        ["darkblue"]    = '\x0C',
        ["olive"]       = '\x05',
        ["lime"]        = '\x06',
        ["red"]         = '\x07',
        ["lightpurple"] = '\x03',
        ["purple"]      = '\x0E',
        ["magenta"]     = '\x0E',
        ["grey"]        = '\x08',
        ["orange"]      = '\x10',
        ["gold"]        = '\x10',
        ["silver"]      = '\x0A',
        ["bluegrey"]    = '\x0A',
        ["lightred"]    = '\x0F',
    };

    // ------------------ Core Logic ------------------
    public static string GetLocalIPAddress()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
        socket.Connect("8.8.8.8", 65530); // Google's DNS
        var endPoint = socket.LocalEndPoint as IPEndPoint;
        return endPoint?.Address.ToString() ?? "127.0.0.1";
    }

    private static readonly Dictionary<string, string> WeaponCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ak47"]          = "ak47",
        ["aug"]           = "aug",
        ["awp"]           = "awp",
        ["famas"]         = "famas",
        ["g3sg1"]         = "g3sg1",
        ["galilar"]       = "galilar",
        ["m4a1_silencer"] = "m4a1_silencer",
        ["m4a1"]          = "m4a1",
        ["scar20"]        = "scar20",
        ["sg553"]         = "sg553",
        ["ssg08"]         = "ssg08",

        ["mac10"]         = "mac10",
        ["mp5sd"]         = "mp5sd",
        ["mp7"]           = "mp7",
        ["mp9"]           = "mp9",
        ["bizon"]         = "bizon",
        ["p90"]           = "p90",
        ["ump45"]         = "ump45",

        ["mag7"]          = "mag7",
        ["nova"]          = "nova",
        ["sawedoff"]      = "sawedoff",
        ["xm1014"]        = "xm1014",
        ["m249"]          = "m249",
        ["negev"]         = "negev",

        ["cz75a"]         = "cz75a",
        ["deagle"]        = "deagle",
        ["elite"]         = "elite",
        ["fiveseven"]     = "fiveseven",
        ["glock"]         = "glock",
        ["hkp2000"]       = "hkp2000",
        ["p250"]          = "p250",
        ["revolver"]      = "revolver",
        ["tec9"]          = "tec9",
        ["usp_silencer"]  = "usp_silencer",
        ["usp"]           = "usp",
    };

    private static Dictionary<string, WeaponStats> GetStatsme(ulong steamId)
    {
        if (!_Statsme.TryGetValue(steamId, out var dict))
        {
            dict = new Dictionary<string, WeaponStats>(8);
            _Statsme[steamId] = dict;
        }
        return dict;
    }

    private static Dictionary<string, HitgroupStats> GetStatsme2(ulong steamId)
    {
        if (!_Statsme2.TryGetValue(steamId, out var dict))
        {
            dict = new Dictionary<string, HitgroupStats>(8);
            _Statsme2[steamId] = dict;
        }
        return dict;
    }

    private static ref WeaponStats GetWeaponStats(Dictionary<string, WeaponStats> dict, string weaponCode)
    {
        return ref CollectionsMarshal.GetValueRefOrAddDefault(dict, weaponCode, out _);
    }

    private static ref HitgroupStats GetHitgroupStats(Dictionary<string, HitgroupStats> dict, string weaponCode)
    {
        return ref CollectionsMarshal.GetValueRefOrAddDefault(dict, weaponCode, out _);
    }

    private void SendStatsme(CCSPlayerController player, string statsVerb, Dictionary<string, string> props)
    {
        var sb = new StringBuilder();
        sb.Append(statsVerb);
        sb.Append('"');

        foreach (var kv in props)
        {
            sb.Append(" (");
            sb.Append(kv.Key);
            sb.Append(" \"");
            sb.Append(kv.Value);
            sb.Append("\")");
        }

        SendLog(player, sb.ToString(), "triggered");
    }

    private void FlushPlayerWeaponStats(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || player.IsBot)
            return;

        var steamId = player.SteamID;

        if (_Statsme.TryGetValue(steamId, out var dict))
        {
            foreach (var kv in dict)
            {
                var weaponCode = kv.Key;
                var s = kv.Value;

                var props = new Dictionary<string, string>
                {
                    ["weapon"]    = weaponCode,
                    ["shots"]     = s.shots.ToString(),
                    ["hits"]      = s.hits.ToString(),
                    ["headshots"] = s.headshots.ToString(),
                    ["damage"]    = s.damage.ToString(),
                    ["kills"]     = s.kills.ToString(),
                    ["deaths"]    = s.deaths.ToString()
                };

                SendStatsme(player, "weaponstats", props);
            }
            _Statsme.Remove(steamId);
        }

        if (_Statsme2.TryGetValue(steamId, out var hdict))
        {
            foreach (var kv in hdict)
            {
                var weaponCode = kv.Key;
                var hs = kv.Value;

                var props2 = new Dictionary<string, string>
                {
                    ["weapon"]   = weaponCode,
                    ["head"]     = hs.head.ToString(),
                    ["chest"]    = hs.chest.ToString(),
                    ["stomach"]  = hs.stomach.ToString(),
                    ["leftarm"]  = hs.leftarm.ToString(),
                    ["rightarm"] = hs.rightarm.ToString(),
                    ["leftleg"]  = hs.leftleg.ToString(),
                    ["rightleg"] = hs.rightleg.ToString()
                };

                SendStatsme(player, "weaponstats2", props2);
            }
            _Statsme2.Remove(steamId);
        }
    }

    private static readonly Regex ColorRegex = new(@"\{([a-zA-Z]+)\}", RegexOptions.Compiled);

    public static string Colors(string input)
    {
        return ColorRegex.Replace(input, m =>
        {
            var key = m.Groups[1].Value;
            return ColorCodes.TryGetValue(key, out var code) ? code.ToString() : m.Value;
        });
    }

    public sealed class LogDispatcher : IDisposable
    {
        private readonly ConcurrentQueue<string> _queue = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;
        private readonly HttpClient _http;

        private readonly string _url;
        private readonly string _serverAddr;

        // --- Adaptive parameters ---
        private int _batchSize = 10;
        private int _delayMs = 10;
        private const int MaxBatchSize = 200;
        private const int MinBatchSize = 1;
        private const int MaxDelayMs = 200;
        private const int MinDelayMs = 1;

        // --- Thresholds ---
        private const int HighLatencyMs = 250;
        private const int CriticalLatencyMs = 500;
        private const int LowLatencyMs = 80;

        private const int HighQueueDepth = 5000;
        private const int CriticalQueueDepth = 20000;

        public LogDispatcher(string logAddress, int logPort, string serverAddr)
        {
            _url = $"http://{logAddress}:{logPort}";
            _serverAddr = serverAddr;

            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            _worker = Task.Run(ProcessQueueAsync);
        }

        public void Enqueue(string line)
        {
            _queue.Enqueue(line);
        }

        private async Task ProcessQueueAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (_queue.IsEmpty)
                    {
                        await Task.Delay(5, _cts.Token);
                        continue;
                    }

                    var batch = new List<string>(_batchSize);

                    while (batch.Count < _batchSize && _queue.TryDequeue(out var line))
                        batch.Add(line);

                    var latency = await SendBatchAsync(batch);

                    AdjustBehavior(latency, _queue.Count);

                    await Task.Delay(_delayMs, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Instance?.Logger.LogInformation($"[HLstatsZ] Log dispatcher error: {ex.Message}");
                }
            }
        }

        private static bool IsRetryable(Exception ex)
        {
            if (ex is HttpRequestException hre && hre.InnerException is HttpIOException ioex &&
                ioex.Message.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase))
                return true;

            if (ex is HttpRequestException hre2 && hre2.InnerException is IOException)
                return true;

            if (ex is TaskCanceledException) // timeout
                return true;

            return false;
        }

        private async Task<HttpResponseMessage?> SendOnceAsync(HttpRequestMessage request, CancellationToken token)
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                             .ConfigureAwait(false);
        }

        private async Task<long> SendBatchAsync(List<string> batch)
        {
            if (batch.Count == 0) return 0;

            var payload = string.Join("\n", batch);

            var sw = Stopwatch.StartNew();
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    using var request = new HttpRequestMessage(HttpMethod.Post, _url);
                    request.Headers.TryAddWithoutValidation("X-Server-Addr", _serverAddr);
                    request.Content = new StringContent(payload, Encoding.UTF8, "text/plain");

                    try
                    {
                        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false);

                        _ = await response.Content.ReadAsByteArrayAsync(_cts.Token).ConfigureAwait(false);

                        sw.Stop();

                        if (!response.IsSuccessStatusCode)
                            Instance?.Logger.LogInformation($"[HLstatsZ] Log Batch send failed: {response.StatusCode}");

                        return sw.ElapsedMilliseconds;
                    }
                    catch (Exception ex) when (attempt < 2 && IsRetryable(ex))
                    {
                        var backoffMs = (int)(50 * Math.Pow(2, attempt)) + Random.Shared.Next(0, 50);
                        await Task.Delay(backoffMs, _cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Instance?.Logger.LogInformation($"[HLstatsZ] Log Batch send exception: {ex}");
            }

            return sw.ElapsedMilliseconds;
        }

        private void AdjustBehavior(long latencyMs, int queueDepth)
        {
            // --- Latency-based ---
            if (latencyMs > CriticalLatencyMs)
            {
                _batchSize = Math.Max(MinBatchSize, _batchSize / 2);
                _delayMs = Math.Min(MaxDelayMs, _delayMs + 20);
            }
            else if (latencyMs > HighLatencyMs)
            {
                _batchSize = Math.Max(MinBatchSize, _batchSize - 2);
                _delayMs = Math.Min(MaxDelayMs, _delayMs + 5);
            }
            else if (latencyMs < LowLatencyMs)
            {
                _batchSize = Math.Min(MaxBatchSize, _batchSize + 2);
                _delayMs = Math.Max(MinDelayMs, _delayMs - 2);
            }

            // --- Queue-depth  ---
            if (queueDepth > CriticalQueueDepth)
            {
                _batchSize = Math.Min(MaxBatchSize, _batchSize + 20);
                _delayMs = Math.Min(MaxDelayMs, _delayMs + 20);
            }
            else if (queueDepth > HighQueueDepth)
            {
                _batchSize = Math.Min(MaxBatchSize, _batchSize + 10);
                _delayMs = Math.Min(MaxDelayMs, _delayMs + 10);
            }

        }

        public void Dispose()
        {
            _cts.Cancel();
            _worker.Wait(2000);
            _cts.Dispose();
            _http.Dispose();
        }
    }

    public void SendLog(CCSPlayerController? player, string message, string? verb)
    {
        string logLine;

        if (player?.IsValid == true && !string.IsNullOrWhiteSpace(verb))
        {

            var name    = player.PlayerName;
            var userid  = player.UserId;
            var steamid = (uint)(player.SteamID - 76561197960265728);
            var team    = player.TeamNum switch {2 => "TERRORIST", 3 => "CT", _ => "UNASSIGNED"};

            logLine = $"L {DateTime.Now:MM/dd/yyyy - HH:mm:ss}: \"{name}<{userid}><[U:1:{steamid}]><{team}>\" {verb} \"{message}\"";

        } else if (string.IsNullOrWhiteSpace(verb)) {

            logLine = $"L {DateTime.Now:MM/dd/yyyy - HH:mm:ss}: {message}";

        } else { return; }

        LogQueue?.Enqueue(logLine);

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

        // userid - hlstats
        if (int.TryParse(token, out var uid))
            return Utilities.GetPlayers().FirstOrDefault(p => p?.IsValid == true && p.UserId == uid);

        // #userid - sb
        if (token[0] == '#' && int.TryParse(token.AsSpan(1), out var uid2))
            return Utilities.GetPlayers().FirstOrDefault(p => p?.IsValid == true && p.UserId == uid2);

        // SteamID64
        if (ulong.TryParse(token, out var sid64) && token.Length >= 17)
            return Utilities.GetPlayers().FirstOrDefault(p => p?.IsValid == true && p.SteamID == sid64);

        // Name
        var name = NormalizeName(token);

        var players = Utilities.GetPlayers().Where(p => p?.IsValid == true).ToList();

        // 1. Exact match
        var exactMatches = players.Where(p => NormalizeName(p.PlayerName) == name).ToList();
        if (exactMatches.Count == 1) return exactMatches[0];
        if (exactMatches.Count > 1) return null;

        // 2. Unique "starts with"
        var prefixMatches = players.Where(p => NormalizeName(p.PlayerName).StartsWith(name)).ToList();
        if (prefixMatches.Count == 1) return prefixMatches[0];
        if (prefixMatches.Count > 1) return null;

        // 3. Unique "contains"
        var containsMatches = players.Where(p => NormalizeName(p.PlayerName).Contains(name)).ToList();
        if (containsMatches.Count == 1) return containsMatches[0];
        if (containsMatches.Count > 1) return null;

    return null;
    }

    private static IEnumerable<CCSPlayerController> GetPlayersList()
    {
        return Utilities.GetPlayers().Where(p => p?.IsValid == true && !p.IsBot).ToList();
    }

    public static void SendPrivateChat(CCSPlayerController player, string message)
    {
        player.PrintToChat(message);
    }

    public static void SendChatToAll(string message)
    {
        var players = GetPlayersList();

        foreach (var player in players)
            player.PrintToChat(message);

    }

    public static void SendHTMLToAll(string message)
    {
        if (_centerHTML != null) return;

        var players = GetPlayersList();

        int ticks = 2;
        float interval = ticks / 64f;
        int repeats = (int)Math.Ceiling(5 / interval);
        int count = 0;

        _centerHTML = Instance?.AddTickTimer(ticks, () =>
        {
            if (++count > repeats)
            {
                _centerHTML?.Kill();
                _centerHTML = null;
                return;
            }

            foreach (var player in players)
            {
                if (player?.IsValid == true)
                    player.PrintToCenterHtml(message, 5);
            }

        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    public void BroadcastCenterMessage(string message, int duration = 5)
    {
        string messageHTML = message.Replace("HLstatsZ","<font color='#FFFFFF'>HLstats</font><font color='#FF2A2A'>Z</font>");
        string htmlContent = $"<font color='#FFFFFF'>{messageHTML}</font>";
        SendHTMLToAll(htmlContent);
    }

    public static void ShowHintMessage(CCSPlayerController player, string message)
    {
        player.PrintToCenter(message);
    }

    // ------------------ Listener -----------------
    private HookResult ComamndListenerHandler(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid)
            return HookResult.Continue;

        bool MenuIsOpen = _menuManager._activeMenus.ContainsKey(player.SteamID);
        var command = info.ArgByIndex(0).ToLower().Trim();

        if (MenuIsOpen && command == "single_player_pause")
            Instance!._menuManager.DestroyMenu(player);

        if (!command.StartsWith("say"))
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
        // ----- HLstatsZ -> Daemon -----
        if (silenced && parts.Length == 1)
        {
            switch (cmd)
            {
                case "rank":
                case "next":
                case "top10":
                case "top20":
                case "top30":
                case "session":
                SendLog(player, cmd, "say");
                return HookResult.Handled;
                default:
                break;
            }
        }

        // ---- Handle Public Command ----
        if ((cmd == "menu" || cmd == "hlx_menu") && parts.Length == 1)
        {
            if (MenuIsOpen)
                Instance!._menuManager.DestroyMenu(player);

            var builder = new HLZMenuBuilder("Main Menu");

            builder.Add("Rank", p => SendLog(p, "rank", "say"));
            builder.Add("Next Rank", p => SendLog(p, "next", "say"));
            builder.Add("TOP 10", p => SendLog(p, "top10", "say"));

            builder.Open(player, Instance!._menuManager);

            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    // --------------------- Console ---------------------
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
            SendChatToAll(HLZ_Prefix + message);
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

            SendPrivateChat(target, HLZ_Prefix + message);
        }
    }

    [ConsoleCommand("hlx_sm_csay")]
    public void OnHlxSmCsayCommand(CCSPlayerController? _, CommandInfo command)
    {
        var message = command.ArgByIndex(1);
        Instance?.BroadcastCenterMessage(message);
    }

    [ConsoleCommand("hlx_sm_hint")]
    public void OnHlxSmHintCommand(CCSPlayerController? _, CommandInfo command)
    {
        if (!int.TryParse(command.ArgByIndex(1), out var userid)) return;

        var message = command.ArgByIndex(command.ArgCount - 1);
        var target  = FindTarget(userid);
        if (target == null || !target.IsValid) return;
        ShowHintMessage(target, message);
    }

    [ConsoleCommand("hlx_sm_msay")]
    public void OnHlxSmMsayCommand(CCSPlayerController? _, CommandInfo command)
    {
        if (!int.TryParse(command.ArgByIndex(2), out var userid)) return;

        var message = command.ArgByIndex(command.ArgCount - 1);
        var target  = FindTarget(userid);
        if (target == null || !target.IsValid) return;
        _menuManager.Open(target,message);
    }

    // ------------------ Event Handler ------------------
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        var players = GetPlayersList();

        foreach (var player in players)
        {
            FlushPlayerWeaponStats(player);
            _menuManager.DestroyMenu(player);
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
            _  => $"with best overall"
        };

        SendLog(player, $"round_mvp {reasonText}", "triggered");
        return HookResult.Continue;
    }

    public HookResult OnBombAbortdefuse(EventBombAbortdefuse @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;

        SendLog(player, "Defuse_Aborted", "triggered");
        return HookResult.Continue;
    }

    public HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;

        SendLog(player, "Defused_The_Bomb", "triggered");
        return HookResult.Continue;
    }

    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid)
        {
            _menuManager.DestroyMenu(player);
            FlushPlayerWeaponStats(player);
        }

        return HookResult.Continue;
    }

    public HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var attacker = @event.Userid;
        if (attacker == null || !attacker.IsValid || attacker.IsBot)
            return HookResult.Continue;

        var steamId = attacker.SteamID;

        var weapon = @event.Weapon;

        if (string.IsNullOrEmpty(weapon))
            return HookResult.Continue;

        if (weapon.StartsWith("weapon_"))
            weapon = weapon.Substring(7);

        if (!WeaponCode.TryGetValue(weapon, out var wCode))
            return HookResult.Continue;

        var dict = GetStatsme(steamId);
        ref var stats = ref GetWeaponStats(dict, wCode);
        stats.shots++;

        return HookResult.Continue;
    }

    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim   = @event.Userid;

        if (attacker == null || !attacker.IsValid || attacker.IsBot)
            return HookResult.Continue;
        if (victim == null || !victim.IsValid)
            return HookResult.Continue;

        var steamId = attacker.SteamID;
        var weapon  = @event.Weapon;

        if (string.IsNullOrEmpty(weapon))
            return HookResult.Continue;

        if (weapon.StartsWith("weapon_"))
            weapon = weapon.Substring(7);

        if (!WeaponCode.TryGetValue(weapon, out var wCode))
            return HookResult.Continue;

        var dmg = @event.DmgHealth;
        var hg  = @event.Hitgroup;

        // ---- STATSME ----
        var dict = GetStatsme(steamId);
        ref var stats = ref GetWeaponStats(dict, wCode);
        stats.hits++;
        stats.damage += dmg;
        if (hg == 1)
            stats.headshots++;

        // ---- STATSME2 ----
        var hdict = GetStatsme2(steamId);
        ref var hstats = ref GetHitgroupStats(hdict, wCode);

        switch (hg)
        {
            case 1: hstats.head++;     break;
            case 2: hstats.chest++;    break;
            case 3: hstats.stomach++;  break;
            case 4: hstats.leftarm++;  break;
            case 5: hstats.rightarm++; break;
            case 6: hstats.leftleg++;  break;
            case 7: hstats.rightleg++; break;
        }

        return HookResult.Continue;
    }

    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim   = @event.Userid;
        var weapon  = @event.Weapon;

        if (victim != null && victim.IsValid)
            _menuManager.DestroyMenu(victim);

        if (string.IsNullOrEmpty(weapon))
            return HookResult.Continue;

        if (weapon.StartsWith("weapon_"))
            weapon = weapon.Substring(7);

        if (!WeaponCode.TryGetValue(weapon, out var wCode))
            return HookResult.Continue;

        if (attacker != null && attacker.IsValid && !attacker.IsBot)
        {
            var steamId = attacker.SteamID;
            var dict = GetStatsme(steamId);
            ref var stats = ref GetWeaponStats(dict, wCode);
            stats.kills++;
        }

        if (victim != null && victim.IsValid && !victim.IsBot)
        {
            var steamId = victim.SteamID;
            var dict = GetStatsme(steamId);
            ref var stats = ref GetWeaponStats(dict, wCode);
            stats.deaths++;
            FlushPlayerWeaponStats(victim);
        }

        return HookResult.Continue;
    }

}

