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
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Core.Capabilities;
using GameTimer = CounterStrikeSharp.API.Modules.Timers.Timer; 
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;

namespace HLstatsZ;

public class HLZMenuManager
{
    private readonly BasePlugin _plugin;
    public CCSPlayerController player { get; set; } = null!;
    private readonly Dictionary<ulong, int> _menuPages = new();
    private readonly Dictionary<ulong, int> _selectedIndex = new();
    private readonly Dictionary<ulong, List<(string Text, Action<CCSPlayerController> Callback)>> _pageOptions = new();
    public readonly Dictionary<ulong, CenterHtmlMenu> _activeMenus = new();
    private string _lastContent = "";
    private List<string[]>? _lastPages;
    private readonly Dictionary<ulong, Stack<(string Content, Dictionary<string, Action<CCSPlayerController>>? Callbacks)>> _menuHistory = new();

    public HLZMenuManager(BasePlugin plugin)
    {
        _plugin = plugin;
    }

    private static List<string[]> PartitionPages(string[] lines)
    {
        var pages = new List<List<string>>();
        List<string>? current = null;

        foreach (var line in lines)
        {

            if (line.Length > 2 && line[0] == '-' && line[1] == '>' && char.IsDigit(line[2]))
            {
                current = new List<string>();
                pages.Add(current);
            }

            current ??= new List<string>();
            current.Add(line);
        }

        return pages.Select(p => p.ToArray()).ToList();
    }

    private static string NormalizeHeading(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Stats";

        var s = raw.Trim();

        if (s.Length > 3 && s[0] == '-' && s[1] == '>' && char.IsDigit(s[2]))
        {
            int dashIndex = s.IndexOf('-', 3);
            if (dashIndex >= 0 && dashIndex + 1 < s.Length)
                s = s.Substring(dashIndex + 1).Trim();
        }

        return string.IsNullOrWhiteSpace(s) ? "Stats" : s;
    }

    public void Open(CCSPlayerController player, string content, int page = 0,
                     Dictionary<string, Action<CCSPlayerController>>? callbacks = null,bool pushHistory = true)
    {
        var steamId = player.SteamID;

        if (!_menuHistory.TryGetValue(steamId, out var stack))
        {
            stack = new Stack<(string, Dictionary<string, Action<CCSPlayerController>>?)>();
            _menuHistory[steamId] = stack;
        }

        if (pushHistory && (stack.Count == 0 || stack.Peek().Content != content))
            stack.Push((content, callbacks));

        if (_lastPages == null || !string.Equals(_lastContent, content, StringComparison.Ordinal))
        {
            var rawLines = content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\\n", "\n")
                                  .Split('\n')
                                  .Select(l => l.Trim())
                                  .Where(l => !string.IsNullOrWhiteSpace(l))
                                  .ToArray();

            _lastPages = PartitionPages(rawLines);
            _lastContent = content;
        }

        var pages = _lastPages!;
        var totalPages = pages.Count;
        if (totalPages == 0)
        {
            DestroyMenu(player);
            return;
        }

        page = Math.Clamp(page, 0, totalPages - 1);

        var pageLines = pages[page];
        var heading = NormalizeHeading(pageLines.FirstOrDefault());
        var displayLines = pageLines.Skip(1).ToArray();

        _menuPages[steamId] = page;
        if (!_selectedIndex.ContainsKey(steamId))
            _selectedIndex[steamId] = 0;

        if (displayLines.Length == 0)
            _selectedIndex[steamId] = 0;

        string main = $"<font color='#FFFFFF'><b>HLstats</b></font><font color='#FF2A2A'><b>Z</b></font> - " +
                      $"<font color='#FFFFFF'>{heading} (Page {page + 1}/{totalPages})</font><br>";

        var options = new List<(string, Action<CCSPlayerController>)>();

        for (int i = 0; i < displayLines.Length; i++)
        {
            var cleanLine = Regex.Replace(displayLines[i], @"^!\d+\s*", "").Trim();

            if (callbacks != null && callbacks.TryGetValue(cleanLine, out var cb)) {
                options.Add((cleanLine, cb));
            } else {
                options.Add((cleanLine, _ => { /* no-op */ }));
                if ( i == _selectedIndex[player.SteamID] )
                    _selectedIndex[player.SteamID]++;
            }

            main += (i == _selectedIndex[steamId]
                ? $"<font color='#00FF00'>▶ {cleanLine} ◀</font><br>"
                : $"<font color='#FFFFFF'>{cleanLine}</font><br>");
        }

        var closeLabel = "[ Close ]";
        var closeIndex = displayLines.Length;

        var maxIndex = closeIndex;
        _selectedIndex[steamId] = Math.Clamp(_selectedIndex[steamId], 0, maxIndex);

        if (displayLines.Length == 0)
            _selectedIndex[steamId] = closeIndex;

        main += (_selectedIndex[steamId] == closeIndex
            ? $"<font color='#00FF00'>▶ {closeLabel} ◀</font><br>"
            : $"<font color='#FF2A2A'>{closeLabel}</font><br>");

        options.Add((closeLabel, p => DestroyMenu(p)));

        _pageOptions[steamId] = options;

        var menu = new CenterHtmlMenu(main, _plugin) { ExitButton = false };
        _activeMenus[steamId] = menu;
        menu.Open(player);
        player.Freeze();
    }

    private Action<CCSPlayerController> autoClose(
        Action<CCSPlayerController> inner,
        int delaySeconds = 5)
    {
        return player =>
        {
            inner(player);

            _ = new GameTimer(delaySeconds, () =>
            {
                foreach (var p in Utilities.GetPlayers())
                    if (p?.IsValid == true)
                        MenuManager.CloseActiveMenu(p!);
            });
        };
    }

    public void DestroyMenu(CCSPlayerController player)
    {
        if (player == null || !player.IsValid) return;
        player.UnFreeze();
        if (_activeMenus.TryGetValue(player.SteamID, out var menu))
        {
            MenuManager.CloseActiveMenu(player);
            _activeMenus.Remove(player.SteamID);
        }
        _pageOptions.Remove(player.SteamID);
        _menuPages.Remove(player.SteamID);
        _selectedIndex.Remove(player.SteamID);
        _menuHistory.Remove(player.SteamID);
    }

    public void HandleBack(CCSPlayerController player)
    {
        var steamId = player.SteamID;
        if (!_menuHistory.TryGetValue(steamId, out var stack) || stack.Count <= 1)
            return;

        if (stack.Count >1)
            stack.Pop();
        var (prevContent, prevCallbacks) = stack.Peek();

        Open(player, prevContent, 0, prevCallbacks, pushHistory: false);
    }

    public void HandlePage(CCSPlayerController player, int delta)
    {
        var steamId = player.SteamID;
        if (!_menuPages.TryGetValue(steamId, out var currentPage))
            currentPage = 0;

        var newPage = Math.Max(0, currentPage + delta);

    Dictionary<string, Action<CCSPlayerController>>? callbacks = null;
    if (_menuHistory.TryGetValue(steamId, out var stack))
    {
        var (_, prevCallbacks) = stack.Peek();
        callbacks = prevCallbacks;
    }

    Open(player, _lastContent, newPage, callbacks);

    }

    public void HandleNavigation(CCSPlayerController player, int delta)
    {
        var steamId = player.SteamID;

        if (!_selectedIndex.TryGetValue(steamId, out var index))
            index = 0;

        var count = (_pageOptions.TryGetValue(steamId, out var opts) ? opts.Count : 1);
        if (count == 0) return;

        index = (index + delta + count) % count;
        _selectedIndex[steamId] = index;

        var page = _menuPages.TryGetValue(steamId, out var p) ? p : 0;

        Dictionary<string, Action<CCSPlayerController>>? callbacks = null;
        if (_menuHistory.TryGetValue(steamId, out var stack))
        {
            var (_, prevCallbacks) = stack.Peek();
            callbacks = prevCallbacks;
        }

        Open(player, _lastContent, page, callbacks, pushHistory: false);
    }

    public void HandleSelect(CCSPlayerController player)
    {
        var steamId = player.SteamID;
        if (!_selectedIndex.TryGetValue(steamId, out var index)) return;
        if (!_pageOptions.TryGetValue(steamId, out var options)) return;

        if (index >= 0 && index < options.Count)
        {
            var (_, cb) = options[index];
            cb(player);
        }
    }
}
