using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

using RetakesPlugin.Configs;
using RetakesPlugin.Utils;

namespace RetakesPlugin.Services;

// OCB Inside ranks for retakes: counts raw values per player, sends them in
// batches to Inside ({Url}/cs2/stats, game "retakes") and shows the rank
// Inside answers with. The rank is shared with our deathmatch servers. All
// counting happens on the game thread; only the HTTP calls run in the
// background, their results come back with Server.NextFrame. It registers
// its own event handlers and never changes the retakes logic.
public class InsideRanksService
{
    private const string Game = "retakes";
    private const float MoveThreshold = 8f;

    private readonly RetakesPlugin _plugin;
    private readonly InsideRanksSettings _cfg;
    private readonly bool _autoPlant;

    private readonly Dictionary<string, Buffer> _buffers = new();
    private readonly Dictionary<int, Activity> _activity = new();
    private readonly Dictionary<string, string> _clanTags = new();
    private readonly LinkedList<Batch> _pending = new();
    private readonly object _pendingLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Per round: who played, and per team the player left alone (clutch).
    private readonly HashSet<string> _roundPlayers = new();
    private readonly Dictionary<CsTeam, string> _clutchers = new();
    private readonly Dictionary<string, int> _streaks = new();

    public InsideRanksService(RetakesPlugin plugin, InsideRanksSettings cfg, bool autoPlant)
    {
        _plugin = plugin;
        _cfg = cfg;
        _autoPlant = autoPlant;
    }

    private bool Enabled => _cfg.Enabled && !string.IsNullOrWhiteSpace(_cfg.ServerKey);
    private string Url(string path) => _cfg.Url.TrimEnd('/') + "/cs2/" + path;
    private string PendingFile => Path.Combine(_plugin.ModuleDirectory, "inside-pending.json");

    public void Load()
    {
        if (_cfg.AdvertEnabled && _cfg.AdvertIntervalSeconds > 0)
        {
            _plugin.AddTimer(Math.Max(60, _cfg.AdvertIntervalSeconds), Advert, TimerFlags.REPEAT);
        }
        _plugin.RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
        if (!Enabled)
        {
            return;
        }
        LoadPending();
        _plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        _plugin.RegisterEventHandler<EventPlayerHurt>(OnHurt);
        _plugin.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        _plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect, HookMode.Pre);
        _plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnFreezeEnd);
        _plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        _plugin.RegisterEventHandler<EventRoundMvp>(OnMvp);
        _plugin.RegisterEventHandler<EventBombPlanted>(OnPlanted);
        _plugin.RegisterEventHandler<EventBombDefused>(OnDefused);
        _plugin.RegisterListener<Listeners.OnMapEnd>(() => Flush());
        _plugin.AddTimer(1.0f, Tick, TimerFlags.REPEAT);
        _plugin.AddTimer(Math.Max(15, _cfg.FlushSeconds), () => Flush(), TimerFlags.REPEAT);
        foreach (var cmd in _cfg.RankCommands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _plugin.AddCommand(cmd, "Shows your OCB rank", (player, _) => RankCommand(player));
        }
        foreach (var cmd in _cfg.TopCommands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _plugin.AddCommand(cmd, "Shows the OCB top 10", (player, _) => TopCommand(player));
        }
        _ = SendPending();
    }

    public void Unload()
    {
        if (!Enabled)
        {
            return;
        }
        Flush(send: false);
        SavePending();
    }

    private string T(string key, params object[] args) => _plugin.Localizer[key, args];
    private string Prefix => _plugin.Localizer["retakes.inside.prefix"];

    private void Advert()
    {
        Server.PrintToChatAll($"{Prefix} {T("retakes.inside.advert", _cfg.AdvertStatsUrl, _cfg.AdvertApplyUrl)}");
    }

    private static bool IsHuman(CCSPlayerController? p) =>
        p != null && p.IsValid && !p.IsBot && !p.IsHLTV && p.SteamID.ToString().Length == 17;

    private static bool IsWarmup() => GameRulesHelper.GetGameRulesOrNull()?.WarmupPeriod ?? false;

    private Buffer Buf(CCSPlayerController p)
    {
        var id = p.SteamID.ToString();
        if (!_buffers.TryGetValue(id, out var b))
        {
            b = new Buffer { SteamID64 = id };
            _buffers[id] = b;
        }
        b.Name = p.PlayerName;
        return b;
    }

    private void MarkActive(CCSPlayerController p)
    {
        if (_activity.TryGetValue(p.Slot, out var a))
        {
            a.LastActive = Server.CurrentTime;
        }
    }

    // Once per second: active time for every living player who moved or
    // dealt damage within the AFK window.
    private void Tick()
    {
        var now = Server.CurrentTime;
        foreach (var p in Utilities.GetPlayers())
        {
            if (!IsHuman(p))
            {
                continue;
            }
            if (!_activity.TryGetValue(p.Slot, out var a))
            {
                a = new Activity { LastActive = now };
                _activity[p.Slot] = a;
            }
            var pawn = p.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || !p.PawnIsAlive || pawn.AbsOrigin == null)
            {
                a.HasPosition = false;
                continue;
            }
            var o = pawn.AbsOrigin;
            if (a.HasPosition)
            {
                float dx = o.X - a.X, dy = o.Y - a.Y, dz = o.Z - a.Z;
                if (dx * dx + dy * dy + dz * dz >= MoveThreshold * MoveThreshold)
                {
                    a.LastActive = now;
                }
            }
            a.X = o.X;
            a.Y = o.Y;
            a.Z = o.Z;
            a.HasPosition = true;
            if (now - a.LastActive <= _cfg.AfkSeconds && !IsWarmup())
            {
                Buf(p).ActiveSeconds++;
            }
        }
    }

    private HookResult OnConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var p = @event.Userid;
        if (!IsHuman(p))
        {
            return HookResult.Continue;
        }
        var steamId = p!.SteamID.ToString();
        // Welcome with the advert once the player sees the chat.
        _plugin.AddTimer(15f, () =>
        {
            var online = Online(steamId);
            if (online != null && _cfg.AdvertEnabled)
            {
                online.PrintToChat($"{Prefix} {T("retakes.inside.advert", _cfg.AdvertStatsUrl, _cfg.AdvertApplyUrl)}");
            }
        });
        if (!Enabled)
        {
            return HookResult.Continue;
        }
        _activity[p.Slot] = new Activity { LastActive = Server.CurrentTime };
        _ = FetchRank(steamId, r =>
        {
            if (r?.Player != null)
            {
                ApplyClanTag(steamId, r.Player);
            }
        });
        return HookResult.Continue;
    }

    private HookResult OnDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var p = @event.Userid;
        if (p == null || !p.IsValid)
        {
            return HookResult.Continue;
        }
        _activity.Remove(p.Slot);
        _clanTags.Remove(p.SteamID.ToString());
        Flush();
        return HookResult.Continue;
    }

    private HookResult OnSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var p = @event.Userid;
        if (_cfg.ShowClanTag && IsHuman(p) && _clanTags.TryGetValue(p!.SteamID.ToString(), out var tag))
        {
            SetClan(p, tag);
        }
        return HookResult.Continue;
    }

    private HookResult OnFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        _roundPlayers.Clear();
        _clutchers.Clear();
        if (IsWarmup())
        {
            return HookResult.Continue;
        }
        foreach (var p in Utilities.GetPlayers())
        {
            if (IsHuman(p) && p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            {
                _roundPlayers.Add(p.SteamID.ToString());
            }
        }
        return HookResult.Continue;
    }

    private HookResult OnDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (IsWarmup())
        {
            return HookResult.Continue;
        }
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        var assister = @event.Assister;
        if (victim == null || !victim.IsValid)
        {
            return HookResult.Continue;
        }
        bool byPlayer = attacker != null && attacker.IsValid && attacker != victim;
        if (IsHuman(victim))
        {
            _streaks[victim.SteamID.ToString()] = 0;
            if (byPlayer)
            {
                Buf(victim).Deaths++;
            }
        }
        if (IsHuman(attacker) && attacker != victim)
        {
            var id = attacker!.SteamID.ToString();
            var streak = (_streaks.TryGetValue(id, out var s) ? s : 0) + 1;
            _streaks[id] = streak;
            var b = Buf(attacker);
            b.Kills++;
            if (@event.Headshot)
            {
                b.Headshots++;
            }
            if (@event.Weapon.Contains("knife") || @event.Weapon.Contains("bayonet"))
            {
                b.KnifeKills++;
            }
            b.BestStreak = Math.Max(b.BestStreak, streak);
            if (b.KillStreaks.Count < 500)
            {
                b.KillStreaks.Add(streak);
            }
            MarkActive(attacker);
        }
        if (IsHuman(assister) && assister != attacker && assister != victim)
        {
            Buf(assister!).Assists++;
        }
        // The event runs before the victim counts as dead: decide the clutch
        // on the next frame.
        Server.NextFrame(CheckClutch);
        return HookResult.Continue;
    }

    // A player left alone against at least one enemy is the clutcher of his
    // team for this round; winning the round makes it a clutch.
    private void CheckClutch()
    {
        var alive = Utilities.GetPlayers().Where(p => p.IsValid && p.PawnIsAlive && p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist).ToList();
        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            var own = alive.Where(p => p.Team == team).ToList();
            var enemies = alive.Count(p => p.Team != team);
            if (own.Count == 1 && enemies >= 1 && !_clutchers.ContainsKey(team) && IsHuman(own[0]))
            {
                _clutchers[team] = own[0].SteamID.ToString();
            }
        }
    }

    private HookResult OnHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (IsWarmup() || !IsHuman(attacker) || attacker == victim || @event.DmgHealth <= 0)
        {
            return HookResult.Continue;
        }
        var b = Buf(attacker!);
        b.Damage += Math.Min(@event.DmgHealth, 500);
        b.Hits++;
        MarkActive(attacker!);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (IsWarmup() || _roundPlayers.Count == 0)
        {
            return HookResult.Continue;
        }
        var winner = (CsTeam)@event.Winner;
        foreach (var p in Utilities.GetPlayers())
        {
            if (!IsHuman(p) || !_roundPlayers.Contains(p.SteamID.ToString()))
            {
                continue;
            }
            var b = Buf(p);
            b.RoundsPlayed++;
            if (p.Team == winner)
            {
                b.RoundsWon++;
            }
        }
        if (_clutchers.TryGetValue(winner, out var clutcher))
        {
            var p = Online(clutcher);
            if (p != null && p.Team == winner)
            {
                Buf(p).Clutches++;
            }
        }
        _roundPlayers.Clear();
        _clutchers.Clear();
        return HookResult.Continue;
    }

    private HookResult OnMvp(EventRoundMvp @event, GameEventInfo info)
    {
        var p = @event.Userid;
        if (!IsWarmup() && IsHuman(p))
        {
            Buf(p!).Mvps++;
        }
        return HookResult.Continue;
    }

    private HookResult OnPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        // With auto plant the bomb is planted for the T at round start: no credit.
        var p = @event.Userid;
        if (!_autoPlant && !IsWarmup() && IsHuman(p))
        {
            Buf(p!).Plants++;
        }
        return HookResult.Continue;
    }

    private HookResult OnDefused(EventBombDefused @event, GameEventInfo info)
    {
        var p = @event.Userid;
        if (!IsWarmup() && IsHuman(p))
        {
            Buf(p!).Defuses++;
        }
        return HookResult.Continue;
    }

    // Moves the counted values into a batch and sends everything pending.
    public void Flush(bool send = true)
    {
        if (!Enabled)
        {
            return;
        }
        var players = _buffers.Values.Where(b => !b.IsEmpty).ToList();
        _buffers.Clear();
        if (players.Count > 0)
        {
            var batch = new Batch { Map = Server.MapName, Players = players };
            lock (_pendingLock)
            {
                _pending.AddLast(batch);
                while (_pending.Count > Math.Max(1, _cfg.MaxPendingBatches))
                {
                    _pending.RemoveFirst();
                }
            }
        }
        if (send)
        {
            _ = SendPending();
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Server-Key", _cfg.ServerKey);
        return req;
    }

    // Sends the pending batches in order; stops at the first failure and keeps
    // the rest (Inside ignores a batch it already has).
    private async Task SendPending()
    {
        if (!await _sendLock.WaitAsync(0))
        {
            return;
        }
        try
        {
            while (true)
            {
                Batch? batch;
                lock (_pendingLock)
                {
                    batch = _pending.First?.Value;
                }
                if (batch == null)
                {
                    return;
                }
                using var req = Request(HttpMethod.Post, Url("stats"));
                req.Content = JsonContent.Create(batch);
                HttpResponseMessage resp;
                try
                {
                    resp = await Http.SendAsync(req);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Inside", $"Inside not reachable, {_pending.Count} batch(es) pending: {ex.Message}");
                    return;
                }
                using (resp)
                {
                    if ((int)resp.StatusCode >= 500)
                    {
                        Logger.LogWarning("Inside", $"Inside answered {(int)resp.StatusCode}, retrying later");
                        return;
                    }
                    lock (_pendingLock)
                    {
                        _pending.Remove(batch);
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        Logger.LogError("Inside", $"Inside rejected a batch ({(int)resp.StatusCode}), check Url and ServerKey");
                        continue;
                    }
                    var body = await resp.Content.ReadFromJsonAsync<StatsResponse>();
                    if (body != null)
                    {
                        Server.NextFrame(() => ApplyStatuses(body.Players));
                    }
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static CCSPlayerController? Online(string steamId) =>
        Utilities.GetPlayers().FirstOrDefault(p => IsHuman(p) && p.SteamID.ToString() == steamId);

    private void ApplyStatuses(List<PlayerStatus> statuses)
    {
        foreach (var st in statuses)
        {
            ApplyClanTag(st.SteamID64, st);
            var p = Online(st.SteamID64);
            if (p == null || !_cfg.AnnounceRankUp)
            {
                continue;
            }
            if (st.RankedUp)
            {
                Server.PrintToChatAll($"{Prefix} {T("retakes.inside.rank_up", p.PlayerName, st.RankName)}");
            }
            foreach (var m in st.NewMilestones)
            {
                p.PrintToChat($"{Prefix} {T("retakes.inside.milestone", m)}");
            }
        }
    }

    private void ApplyClanTag(string steamId, PlayerStatus st)
    {
        if (!_cfg.ShowClanTag || string.IsNullOrEmpty(st.RankName))
        {
            return;
        }
        var tag = "[" + st.RankName + "]";
        _clanTags[steamId] = tag;
        var p = Online(steamId);
        if (p != null)
        {
            SetClan(p, tag);
        }
    }

    private static void SetClan(CCSPlayerController p, string tag)
    {
        if (p.Clan == tag)
        {
            return;
        }
        p.Clan = tag;
        Utilities.SetStateChanged(p, "CCSPlayerController", "m_szClan");
    }

    private async Task FetchRank(string steamId, Action<RankResponse?> onGameThread)
    {
        try
        {
            using var req = Request(HttpMethod.Get, Url("rank") + "?steam_id64=" + Uri.EscapeDataString(steamId));
            using var resp = await Http.SendAsync(req);
            var body = resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<RankResponse>() : null;
            Server.NextFrame(() => onGameThread(body));
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Inside", $"Inside rank lookup failed: {ex.Message}");
            Server.NextFrame(() => onGameThread(null));
        }
    }

    private void RankCommand(CCSPlayerController? player)
    {
        if (!IsHuman(player))
        {
            return;
        }
        var steamId = player!.SteamID.ToString();
        _ = FetchRank(steamId, r =>
        {
            var p = Online(steamId);
            if (p == null)
            {
                return;
            }
            if (r?.Player == null)
            {
                p.PrintToChat($"{Prefix} {T("retakes.inside.offline")}");
                return;
            }
            var st = r.Player;
            p.PrintToChat($"{Prefix} {T("retakes.inside.rank", st.RankName, st.XP, r.Position?.ToString() ?? "-")}");
            if (!string.IsNullOrEmpty(st.NextRankName))
            {
                p.PrintToChat($"{Prefix} {T("retakes.inside.next_rank", st.NextRankName, Math.Max(0, st.NextXP - st.XP))}");
            }
            p.PrintToChat($"{Prefix} {T("retakes.inside.advert", _cfg.AdvertStatsUrl, _cfg.AdvertApplyUrl)}");
        });
    }

    private void TopCommand(CCSPlayerController? player)
    {
        if (!IsHuman(player))
        {
            return;
        }
        var steamId = player!.SteamID.ToString();
        _ = Task.Run(async () =>
        {
            TopResponse? body = null;
            try
            {
                using var req = Request(HttpMethod.Get, Url("top") + "?limit=10");
                using var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    body = await resp.Content.ReadFromJsonAsync<TopResponse>();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Inside", $"Inside top lookup failed: {ex.Message}");
            }
            Server.NextFrame(() =>
            {
                var p = Online(steamId);
                if (p == null)
                {
                    return;
                }
                if (body == null)
                {
                    p.PrintToChat($"{Prefix} {T("retakes.inside.offline")}");
                    return;
                }
                p.PrintToChat($"{Prefix} {T("retakes.inside.top_header")}");
                foreach (var e in body.Players)
                {
                    p.PrintToChat(T("retakes.inside.top_line", e.Position, e.Name, e.RankName, e.XP));
                }
            });
        });
    }

    private void LoadPending()
    {
        try
        {
            if (!File.Exists(PendingFile))
            {
                return;
            }
            var list = JsonSerializer.Deserialize<List<Batch>>(File.ReadAllText(PendingFile));
            File.Delete(PendingFile);
            if (list == null)
            {
                return;
            }
            lock (_pendingLock)
            {
                foreach (var b in list)
                {
                    _pending.AddLast(b);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Inside", $"Could not read pending Inside batches: {ex.Message}");
        }
    }

    private void SavePending()
    {
        try
        {
            List<Batch> list;
            lock (_pendingLock)
            {
                list = _pending.ToList();
            }
            if (list.Count == 0)
            {
                if (File.Exists(PendingFile))
                {
                    File.Delete(PendingFile);
                }
                return;
            }
            File.WriteAllText(PendingFile, JsonSerializer.Serialize(list));
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Inside", $"Could not save pending Inside batches: {ex.Message}");
        }
    }

    private sealed class Activity
    {
        public float X, Y, Z;
        public bool HasPosition;
        public float LastActive;
    }

    public sealed class Buffer
    {
        [JsonPropertyName("steam_id64")] public string SteamID64 { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("kills")] public int Kills { get; set; }
        [JsonPropertyName("deaths")] public int Deaths { get; set; }
        [JsonPropertyName("headshots")] public int Headshots { get; set; }
        [JsonPropertyName("knife_kills")] public int KnifeKills { get; set; }
        [JsonPropertyName("assists")] public int Assists { get; set; }
        [JsonPropertyName("damage")] public int Damage { get; set; }
        [JsonPropertyName("hits")] public int Hits { get; set; }
        [JsonPropertyName("active_seconds")] public int ActiveSeconds { get; set; }
        [JsonPropertyName("best_streak")] public int BestStreak { get; set; }
        [JsonPropertyName("kill_streaks")] public List<int> KillStreaks { get; set; } = new();
        [JsonPropertyName("rounds_played")] public int RoundsPlayed { get; set; }
        [JsonPropertyName("rounds_won")] public int RoundsWon { get; set; }
        [JsonPropertyName("clutches")] public int Clutches { get; set; }
        [JsonPropertyName("plants")] public int Plants { get; set; }
        [JsonPropertyName("defuses")] public int Defuses { get; set; }
        [JsonPropertyName("mvps")] public int Mvps { get; set; }

        [JsonIgnore]
        public bool IsEmpty => Kills == 0 && Deaths == 0 && Assists == 0 && Damage == 0 && ActiveSeconds == 0 && RoundsPlayed == 0;
    }

    public sealed class Batch
    {
        [JsonPropertyName("batch_id")] public string BatchId { get; set; } = Guid.NewGuid().ToString();
        [JsonPropertyName("game")] public string Game { get; set; } = InsideRanksService.Game;
        [JsonPropertyName("map")] public string Map { get; set; } = "";
        [JsonPropertyName("players")] public List<Buffer> Players { get; set; } = new();
    }

    public sealed class PlayerStatus
    {
        [JsonPropertyName("steam_id64")] public string SteamID64 { get; set; } = "";
        [JsonPropertyName("xp")] public long XP { get; set; }
        [JsonPropertyName("rank_name")] public string RankName { get; set; } = "";
        [JsonPropertyName("next_rank_name")] public string? NextRankName { get; set; }
        [JsonPropertyName("next_xp")] public long NextXP { get; set; }
        [JsonPropertyName("ranked_up")] public bool RankedUp { get; set; }
        [JsonPropertyName("new_milestones")] public List<string> NewMilestones { get; set; } = new();
    }

    private sealed class StatsResponse
    {
        [JsonPropertyName("players")] public List<PlayerStatus> Players { get; set; } = new();
    }

    private sealed class RankResponse
    {
        [JsonPropertyName("player")] public PlayerStatus? Player { get; set; }
        [JsonPropertyName("position")] public int? Position { get; set; }
    }

    private sealed class TopEntry
    {
        [JsonPropertyName("position")] public int Position { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("xp")] public long XP { get; set; }
        [JsonPropertyName("rank_name")] public string RankName { get; set; } = "";
    }

    private sealed class TopResponse
    {
        [JsonPropertyName("players")] public List<TopEntry> Players { get; set; } = new();
    }
}
