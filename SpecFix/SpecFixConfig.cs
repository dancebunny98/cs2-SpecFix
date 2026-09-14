using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace SpecFix;

public class SpecFixConfig : BasePluginConfig
{
    public override int Version { get; set; } = 4;

    /// <summary>Master switch for the fix.</summary>
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Print a debug line to server console whenever a phantom pawn is removed.</summary>
    [JsonPropertyName("DebugLog")]
    public bool DebugLog { get; set; } = true;

    /// <summary>
    /// When a player dies (or ends up in spectator/no-team with no clean
    /// death), make sure their old body actually stops moving/shooting
    /// and their camera is pointed at a live target instead of it.
    /// </summary>
    [JsonPropertyName("FixGhostOnDeath")]
    public bool FixGhostOnDeath { get; set; } = true;

    /// <summary>
    /// Rate-limit jointeam so rapid T/CT/Spectator cycling can't trigger
    /// the ghost state in the first place, on any team.
    /// </summary>
    [JsonPropertyName("RateLimitTeamSwitch")]
    public bool RateLimitTeamSwitch { get; set; } = true;

    /// <summary>How many jointeam calls are allowed within the sliding window before blocking.</summary>
    [JsonPropertyName("MaxTeamSwitchesInWindow")]
    public int MaxTeamSwitchesInWindow { get; set; } = 3;

    /// <summary>Sliding window (seconds) the switch count above is measured over.</summary>
    [JsonPropertyName("TeamSwitchWindowSeconds")]
    public float TeamSwitchWindowSeconds { get; set; } = 4.0f;

    /// <summary>How long (seconds) a player is blocked from jointeam after tripping the limit.</summary>
    [JsonPropertyName("TeamSwitchCooldownSeconds")]
    public float TeamSwitchCooldownSeconds { get; set; } = 3.0f;
}
