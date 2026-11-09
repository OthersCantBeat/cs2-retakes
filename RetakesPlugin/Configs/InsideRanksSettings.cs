using System.Text.Json.Serialization;

namespace RetakesPlugin.Configs;

// Ranks from OCB Inside: the plugin only counts raw values (kills, damage,
// rounds, clutches, plants, defuses, MVPs, active time) and sends them in
// batches; Inside computes XP, the rank (shared with our deathmatch servers)
// and achievements, and answers with the rank shown as clan tag.
public class InsideRanksSettings
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = false;

    // Base URL of the game server API, e.g. https://gs.ocb.gg (requests go to {Url}/cs2/...).
    [JsonPropertyName("Url")]
    public string Url { get; set; } = "https://gs.ocb.gg";

    // Key of this server from Inside (Technik -> CS2-Deathmatch -> Server), starts with "ocbdm_".
    [JsonPropertyName("ServerKey")]
    public string ServerKey { get; set; } = "";

    [JsonPropertyName("FlushSeconds")]
    public int FlushSeconds { get; set; } = 60;

    // Without movement or damage for this long a player counts as AFK: no active time.
    [JsonPropertyName("AfkSeconds")]
    public int AfkSeconds { get; set; } = 20;

    [JsonPropertyName("ShowClanTag")]
    public bool ShowClanTag { get; set; } = true;

    [JsonPropertyName("AnnounceRankUp")]
    public bool AnnounceRankUp { get; set; } = true;

    [JsonPropertyName("RankCommands")]
    public string RankCommands { get; set; } = "css_rank,css_ocbrank";

    [JsonPropertyName("TopCommands")]
    public string TopCommands { get; set; } = "css_top,css_ocbtop";

    [JsonPropertyName("MaxPendingBatches")]
    public int MaxPendingBatches { get; set; } = 50;

    // Recurring chat message telling players where to see their stats.
    // Works without Enabled/ServerKey too.
    [JsonPropertyName("AdvertEnabled")]
    public bool AdvertEnabled { get; set; } = true;

    [JsonPropertyName("AdvertIntervalSeconds")]
    public int AdvertIntervalSeconds { get; set; } = 300;

    [JsonPropertyName("AdvertStatsUrl")]
    public string AdvertStatsUrl { get; set; } = "inside.ocb.gg";

    [JsonPropertyName("AdvertApplyUrl")]
    public string AdvertApplyUrl { get; set; } = "ocb.gg";
}
