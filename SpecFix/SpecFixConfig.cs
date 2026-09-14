using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace SpecFix;

public class SpecFixConfig : BasePluginConfig
{
    public override int Version { get; set; } = 3;

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
}
