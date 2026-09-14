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
    public override string ModuleVersion => "3.5.0";
    public override string ModuleDescription => "Proactively controls the spectator switch cycle so the camera never lands on the phantom body, and makes sure a dead player's old body actually stops moving/shooting instead of becoming a ghost.";

    public SpecFixConfig Config { get; set; } = new();
    public void OnConfigParsed(SpecFixConfig config) => Config = config;

    private const byte TEAM_T = (byte)CsTeam.Terrorist;         // 2
    private const byte TEAM_CT = (byte)CsTeam.CounterTerrorist; // 3

    // ObserverMode_t (NONE=0 FIXED=1 IN_EYE=2 CHASE=3 ROAMING=4)
    private const byte OBS_MODE_IN_EYE = 2;

    private const byte LIFE_ALIVE = 0;

    public override void Load(bool hotReload)
    {
        // VARIANT 1 (+ narrow variant-B safety net).
        //
        // Variant 3 proved the phantom IS the dead PlayerPawn that persists
        // while the player sits in spectators; killing it does not remove it.
        //
        // spec_next / spec_prev  (proactive, NO flicker):
        //   We take over the switch cycle. We pick the next/previous LIVE player
        //   ourselves (never the phantom), set the observer target, and BLOCK the
        //   original command so the engine never gets a chance to select the
        //   phantom. This is the normal left/right click cycling.
        //
        // spec_mode  (reactive safety net, path B):
        //   When you're free-roaming and click to lock onto a target, the engine
        //   chooses the target you are AIMING at - geometry we cannot replicate
        //   from managed code. So we DON'T block spec_mode (that would break
        //   "lock onto who I'm looking at"). Instead we let it run, then one tick
        //   later we check: if the engine happened to land the camera on our OWN
        //   phantom body, we bump it to a live player. If it landed on a real
        //   target, we leave it exactly as the engine chose.
        //
        // Fully crash-safe: no Remove(), no controller-handle swap, no player
        // teleport. The observer sub-object is networked the CORRECT way, by
        // marking the containing pointer dirty on CBasePlayerPawn.
        AddCommandListener("spec_next", OnSpecNext, HookMode.Pre);
        AddCommandListener("spec_prev", OnSpecPrev, HookMode.Pre);
        AddCommandListener("spec_mode", OnSpecMode, HookMode.Pre);

        // GHOST FIX (death side of the same phantom-body problem).
        //
        // On a normal death, CS2 leaves the old CCSPlayerPawn behind instead
        // of removing it - that's the "phantom" the spec_next/spec_prev code
        // above avoids looking at. Separately, if a player is exploited or
        // race-conditioned into spectator/dead state while that pawn is
        // still fully alive (health > 0, LifeState == ALIVE), the pawn goes
        // on being fully functional: it can still walk and shoot even
        // though its owner is "dead". Two layers close this:
        //
        //   1. EventPlayerDeath: one frame after every death, make sure the
        //      body actually stopped functioning, then point that player's
        //      own camera at a live target instead of their own corpse.
        //   2. A per-tick sweep as a safety net, for cases where a death
        //      event doesn't fire cleanly (e.g. the same team-switch abuse
        //      this plugin already guards spectating against).
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterListener<Listeners.OnTick>(OnGameTick);

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

        // Only take over the cycle for players who are NOT actively playing.
        if (player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            return HookResult.Continue;

        var observerPawn = player.Pawn?.Value;
        var obs = observerPawn?.ObserverServices;
        if (observerPawn is null || !observerPawn.IsValid || obs is null)
            return HookResult.Continue;

        // Build the ordered list of valid LIVE targets (never the phantom).
        var targets = GetLiveTargetPawns();
        if (targets.Count == 0)
            return HookResult.Continue; // nobody to watch -> let engine decide

        // Where is the camera now?
        uint currentTargetIndex = 0;
        var currentTarget = obs.ObserverTarget?.Value;
        if (currentTarget is not null && currentTarget.IsValid)
            currentTargetIndex = currentTarget.Index;

        int currentPos = targets.FindIndex(p => p.Index == currentTargetIndex);

        // Step to the next/previous live target (wrapping around).
        int nextPos;
        if (currentPos < 0)
            nextPos = 0; // camera was on something invalid (e.g. phantom) -> first live
        else
            nextPos = forward
                ? (currentPos + 1) % targets.Count
                : (currentPos - 1 + targets.Count) % targets.Count;

        var next = targets[nextPos];

        obs.ObserverTarget.Raw = next.EntityHandle.Raw;
        obs.ObserverMode = OBS_MODE_IN_EYE;
        // Correct way to network the embedded observer-services sub-object.
        Utilities.SetStateChanged(observerPawn, "CBasePlayerPawn", "m_pObserverServices");

        if (Config.DebugLog)
            Console.WriteLine($"[{ModuleName}] {player.PlayerName} spec {(forward ? "next" : "prev")} -> live pawn #{next.Index} (skipped engine cycle).");

        // Block the engine's own switch so it can never select the phantom.
        return HookResult.Stop;
    }

    // Path B: don't block spec_mode (engine keeps "lock onto who I'm aiming at"),
    // but schedule a one-tick-later check to rescue the camera if it landed on
    // our own phantom body.
    private HookResult OnSpecMode(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Enabled)
            return HookResult.Continue;

        if (player is null || !player.IsValid || player.IsHLTV)
            return HookResult.Continue;

        if (player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            return HookResult.Continue;

        // Capture the controller; re-validate inside the delayed callback.
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

        // The phantom is this spectator's own leftover T/CT body.
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

        // Only rescue if the engine actually landed the camera on our own body.
        var target = obs.ObserverTarget?.Value;
        bool stuckOnOwnBody = target is not null && target.IsValid && target.Index == bodyIndex;
        if (!stuckOnOwnBody)
            return; // engine picked a real target -> leave the player's choice alone

        var targets = GetLiveTargetPawns();
        if (targets.Count == 0)
            return;

        var next = targets[0];

        obs.ObserverTarget.Raw = next.EntityHandle.Raw;
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

        // Let the engine finish its own death processing (ragdoll, stats,
        // freezecam target selection) for one frame first, then take over:
        // confirm the body stopped functioning and move the camera off it.
        Server.NextFrame(() => FixGhostBody(victim));

        return HookResult.Continue;
    }

    // Per-tick safety net: catches a player sitting in spectator (or with
    // no team) whose old PlayerPawn is still reporting alive/functional -
    // the exact signature of the "dead but can walk and shoot" exploit,
    // regardless of whether it was reached via death, team-switch abuse,
    // or anything else.
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

        // If the old body is still alive and functional, actually kill it
        // so it can no longer move or shoot - this is the core of the fix.
        if (body is not null && body.IsValid && body.Health > 0 && body.LifeState == LIFE_ALIVE)
        {
            controller.CommitSuicide(false, true);

            if (Config.DebugLog)
                Console.WriteLine($"[{ModuleName}] {controller.PlayerName} had a functional body after death/spectate - forced to actually die.");
        }

        // Make sure this player is watching a live target, not sitting on
        // (or drifting back onto) their own now-disabled body.
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
            return; // engine already picked a real target (e.g. normal killcam) - leave it alone

        var next = targets[0];

        obs.ObserverTarget.Raw = next.EntityHandle.Raw;
        obs.ObserverMode = OBS_MODE_IN_EYE;
        Utilities.SetStateChanged(observerPawn, "CBasePlayerPawn", "m_pObserverServices");

        if (Config.DebugLog)
            Console.WriteLine($"[{ModuleName}] {controller.PlayerName} died -> camera moved to live pawn #{next.Index}.");
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
