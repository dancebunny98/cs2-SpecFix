using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Listeners;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace SpecFix;

public class SpecFix : BasePlugin, IPluginConfig<SpecFixConfig>
{
    public override string ModuleName => "SpecFix";
    public override string ModuleAuthor => "Nip0s";
    public override string ModuleVersion => "3.7.0";
    public override string ModuleDescription => "Proactively controls the spectator switch cycle so the camera never lands on the phantom body, kills a dead player's still-functional body instead of letting it become a ghost, and rate-limits rapid team switching so the ghost state can't be triggered in the first place.";

    public SpecFixConfig Config { get; set; } = new();
    public void OnConfigParsed(SpecFixConfig config) => Config = config;

    private const byte TEAM_T = (byte)CsTeam.Terrorist;         // 2
    private const byte TEAM_CT = (byte)CsTeam.CounterTerrorist; // 3

    // ObserverMode_t (NONE=0 FIXED=1 IN_EYE=2 CHASE=3 ROAMING=4)
    private const byte OBS_MODE_IN_EYE = 2;

    private const byte LIFE_ALIVE = 0;

    // Per-player jointeam timestamps within the sliding window, and a
    // cooldown deadline for anyone who tripped the rate limit.
    private readonly Dictionary<ulong, List<float>> _switchTimestamps = new();
    private readonly Dictionary<ulong, float> _switchBlockedUntil = new();

    public override void Load(bool hotReload)
    {
        AddCommandListener("spec_next", OnSpecNext, HookMode.Pre);
        AddCommandListener("spec_prev", OnSpecPrev, HookMode.Pre);
        AddCommandListener("spec_mode", OnSpecMode, HookMode.Pre);

        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterListener<Listeners.OnTick>(OnGameTick);

        // НОВОЕ: срабатывает ПОСЛЕ смены команды — идеальный момент
        // для зачистки фантомного pawn, оставшегося от старой команды.
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);

        AddCommandListener("jointeam", OnJoinTeamCommand, HookMode.Pre);

        RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
        {
            var sid = @event.Userid?.SteamID ?? 0;
            _switchTimestamps.Remove(sid);
            _switchBlockedUntil.Remove(sid);
            return HookResult.Continue;
        });

        Console.WriteLine($"[{ModuleName}] Loaded v{ModuleVersion} (Enabled={Config.Enabled})");
    }

    private HookResult OnSpecNext(CCSPlayerController? player, CommandInfo command)
        => HandleSpecCycle(player, forward: true);

    private HookResult OnSpecPrev(CCSPlayerController? player, CommandInfo command)
        => HandleSpecCycle(player, forward: false);

    private HookResult HandleSpecCycle(CCSPlayerController? player, bool forward)
    {
        if (!Config.Enabled)
            return HookResult.Continue;

        if (player is null || !player.IsValid || player.IsHLTV)
            return HookResult.Continue;

        if (player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            return HookResult.Continue;

        var observerPawn = player.Pawn?.Value;
        var obs = observerPawn?.ObserverServices;
        if (observerPawn is null || !observerPawn.IsValid || obs is null)
            return HookResult.Continue;

        var targets = GetLiveTargetPawns();
        if (targets.Count == 0)
            return HookResult.Continue;

        uint currentTargetIndex = 0;
        var currentTarget = obs.ObserverTarget?.Value;
        if (currentTarget is not null && currentTarget.IsValid)
            currentTargetIndex = currentTarget.Index;

        int currentPos = targets.FindIndex(p => p.Index == currentTargetIndex);

        int nextPos;
        if (currentPos < 0)
            nextPos = 0;
        else
            nextPos = forward
                ? (currentPos + 1) % targets.Count
                : (currentPos - 1 + targets.Count) % targets.Count;

        var next = targets[nextPos];

        obs.ObserverTarget!.Raw = next.EntityHandle.Raw;
        obs.ObserverMode = OBS_MODE_IN_EYE;
        Utilities.SetStateChanged(observerPawn, "CBasePlayerPawn", "m_pObserverServices");

        if (Config.DebugLog)
            Console.WriteLine($"[{ModuleName}] {player.PlayerName} spec {(forward ? "next" : "prev")} -> live pawn #{next.Index} (skipped engine cycle).");

        return HookResult.Stop;
    }

    private HookResult OnSpecMode(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Enabled)
            return HookResult.Continue;

        if (player is null || !player.IsValid || player.IsHLTV)
            return HookResult.Continue;

        if (player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            return HookResult.Continue;

        var controller = player;
        AddTimer(0.03f, () => RescueFromPhantom(controller));

        return HookResult.Continue;
    }

    private void RescueFromPhantom(CCSPlayerController controller)
    {
        if (!Config.Enabled)
            return;

        if (controller is null || !controller.IsValid || controller.IsHLTV)
            return;

        if (controller.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            return;

        var body = controller.PlayerPawn?.Value;
        if (body is null || !body.IsValid)
            return;
        if (body.TeamNum != TEAM_T && body.TeamNum != TEAM_CT)
            return;

        uint bodyIndex = body.Index;

        var observerPawn = controller.Pawn?.Value;
        var obs = observerPawn?.ObserverServices;
        if (observerPawn is null || !observerPawn.IsValid || obs is null)
            return;

        var target = obs.ObserverTarget?.Value;
        bool stuckOnOwnBody = target is not null && target.IsValid && target.Index == bodyIndex;
        if (!stuckOnOwnBody)
            return;

        var targets = GetLiveTargetPawns();
        if (targets.Count == 0)
            return;

        var next = targets[0];

        obs.ObserverTarget!.Raw = next.EntityHandle.Raw;
        obs.ObserverMode = OBS_MODE_IN_EYE;
        Utilities.SetStateChanged(observerPawn, "CBasePlayerPawn", "m_pObserverServices");

        if (Config.DebugLog)
            Console.WriteLine($"[{ModuleName}] {controller.PlayerName} spec_mode landed on own body #{bodyIndex} -> rescued to live pawn #{next.Index}.");
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!Config.Enabled || !Config.FixGhostOnDeath)
            return HookResult.Continue;

        var victim = @event.Userid;
        if (victim is null || !victim.IsValid || victim.IsHLTV)
            return HookResult.Continue;

        Server.NextFrame(() => FixGhostBody(victim));

        return HookResult.Continue;
    }

    // НОВОЕ: реакция на фактическую смену команды. Здесь игрок уже в новой
    // команде, но старый pawn может продолжать существовать как "призрак".
    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        if (!Config.Enabled || !Config.FixGhostOnDeath)
            return HookResult.Continue;

        var player = @event.Userid;
        if (player is null || !player.IsValid || player.IsHLTV || player.IsBot)
            return HookResult.Continue;

        var p = player;
        Server.NextFrame(() => CleanupGhostPawn(p));

        return HookResult.Continue;
    }

    // НОВОЕ: мягкая зачистка. Если игрок уже не в T/CT, а его pawn
    // всё ещё "жив" — это фантом. Удаляем сущность физически.
    private void CleanupGhostPawn(CCSPlayerController controller)
    {
        if (!Config.Enabled || !Config.FixGhostOnDeath)
            return;

        if (controller is null || !controller.IsValid || controller.IsHLTV)
            return;

        // Если игрок УЖЕ в T/CT — это его нормальное активное тело.
        // Трогать нельзя ни в коем случае.
        if (controller.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            return;

        var pawn = controller.PlayerPawn?.Value;
        if (pawn is null || !pawn.IsValid)
            return;

        // Фантом = "живой" pawn у игрока вне активной игры.
        bool pawnStillFunctional = pawn.Health > 0 && pawn.LifeState == LIFE_ALIVE;
        if (!pawnStillFunctional)
            return;

        try
        {
            pawn.Remove();

            if (Config.DebugLog)
                Console.WriteLine($"[{ModuleName}] Removed phantom pawn of {controller.PlayerName} after team change.");
        }
        catch (System.Exception ex)
        {
            Console.WriteLine($"[{ModuleName}] Failed to remove phantom pawn for {controller.PlayerName}: {ex.Message}");
        }
    }

    private void OnGameTick()
    {
        if (!Config.Enabled || !Config.FixGhostOnDeath)
            return;

        foreach (var player in Utilities.GetPlayers())
        {
            if (player is null || !player.IsValid || player.IsHLTV)
                continue;

            bool notActivelyPlaying = player.Team is CsTeam.Spectator or CsTeam.None;
            if (!notActivelyPlaying)
                continue;

            var pawn = player.PlayerPawn?.Value;
            if (pawn is null || !pawn.IsValid)
                continue;

            bool pawnStillFunctional = pawn.Health > 0 && pawn.LifeState == LIFE_ALIVE;
            if (!pawnStillFunctional)
                continue;

            var p = player;
            Server.NextFrame(() => FixGhostBody(p));
        }
    }

    private void FixGhostBody(CCSPlayerController controller)
    {
        if (!Config.Enabled || !Config.FixGhostOnDeath)
            return;

        if (controller is null || !controller.IsValid || controller.IsHLTV)
            return;

        var body = controller.PlayerPawn?.Value;

        if (body is not null && body.IsValid && body.Health > 0 && body.LifeState == LIFE_ALIVE)
        {
            controller.CommitSuicide(false, true);

            if (Config.DebugLog)
                Console.WriteLine($"[{ModuleName}] {controller.PlayerName} had a functional body after death/spectate - forced to actually die.");
        }

        var observerPawn = controller.Pawn?.Value;
        var obs = observerPawn?.ObserverServices;
        if (observerPawn is null || !observerPawn.IsValid || obs is null)
            return;

        var targets = GetLiveTargetPawns();
        if (targets.Count == 0)
            return;

        var currentTarget = obs.ObserverTarget?.Value;
        bool alreadyOnLiveTarget = currentTarget is not null && currentTarget.IsValid
            && targets.Any(t => t.Index == currentTarget.Index);
        if (alreadyOnLiveTarget)
            return;

        var next = targets[0];

        obs.ObserverTarget!.Raw = next.EntityHandle.Raw;
        obs.ObserverMode = OBS_MODE_IN_EYE;
        Utilities.SetStateChanged(observerPawn, "CBasePlayerPawn", "m_pObserverServices");

        if (Config.DebugLog)
            Console.WriteLine($"[{ModuleName}] {controller.PlayerName} died -> camera moved to live pawn #{next.Index}.");
    }

    private HookResult OnJoinTeamCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Config.Enabled || !Config.RateLimitTeamSwitch)
            return HookResult.Continue;

        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return HookResult.Continue;

        var steamId = player.SteamID;
        var now = Server.CurrentTime;

        if (_switchBlockedUntil.TryGetValue(steamId, out var until) && now < until)
            return HookResult.Stop;

        if (!_switchTimestamps.TryGetValue(steamId, out var list))
        {
            list = new List<float>();
            _switchTimestamps[steamId] = list;
        }

        list.RemoveAll(t => now - t > Config.TeamSwitchWindowSeconds);
        list.Add(now);

        if (list.Count > Config.MaxTeamSwitchesInWindow)
        {
            _switchBlockedUntil[steamId] = now + Config.TeamSwitchCooldownSeconds;
            list.Clear();

            var p = player;
            Server.NextFrame(() => ForceCleanReset(p));

            if (Config.DebugLog)
                Console.WriteLine($"[{ModuleName}] {player.PlayerName} switched teams too fast ({Config.MaxTeamSwitchesInWindow}+ in {Config.TeamSwitchWindowSeconds:0.#}s) - blocked and reset to Spectator.");

            return HookResult.Stop;
        }

        // НОВОЕ: даже когда смена разрешена, после её выполнения
        // запускаем зачистку — на случай, если движок оставит старый pawn.
        var pAllowed = player;
        Server.NextFrame(() => CleanupGhostPawn(pAllowed));

        return HookResult.Continue;
    }

    private void ForceCleanReset(CCSPlayerController controller)
    {
        if (controller is null || !controller.IsValid)
            return;

        var pawn = controller.PlayerPawn?.Value;
        if (pawn is not null && pawn.IsValid && pawn.Health > 0)
            controller.CommitSuicide(false, true);

        controller.ChangeTeam(CsTeam.Spectator);
    }

    private static List<CCSPlayerPawn> GetLiveTargetPawns()
    {
        var list = new List<CCSPlayerPawn>();

        foreach (var c in Utilities.GetPlayers().OrderBy(c => c.Slot))
        {
            if (c is null || !c.IsValid || c.IsHLTV)
                continue;
            if (c.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist))
                continue;

            var p = c.PlayerPawn?.Value;
            if (p is not null && p.IsValid && p.Health > 0 && p.LifeState == LIFE_ALIVE)
                list.Add(p);
        }

        return list;
    }

    [ConsoleCommand("css_specfix", "Toggle SpecFix on/off")]
    [RequiresPermissions("@css/generic")]
    [CommandHelper(minArgs: 0, usage: "[on|off]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnSpecFixCommand(CCSPlayerController? player, CommandInfo command)
    {
        var arg = command.ArgCount >= 2 ? command.GetArg(1).ToLowerInvariant() : "toggle";

        Config.Enabled = arg switch
        {
            "on" or "1" or "true" => true,
            "off" or "0" or "false" => false,
            _ => !Config.Enabled,
        };

        var state = Config.Enabled ? Localizer["StateOn"] : Localizer["StateOff"];
        command.ReplyToCommand($"[{ModuleName}] {state}");
    }
}
