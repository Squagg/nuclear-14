// #Misfits Add — Smoke Signal server system.
// Allows any Tribe-department player to activate a signal fire, type a short message,
// and broadcast it to all online Tribe-department players as a styled popup + chat notice.
//
// Flow:
//   1. Player right-clicks a SmokeSignalComponent entity → verb appears if they are in Tribe department.
//   2. Server opens the BUI (text-input window) on the activator's session.
//   3. Player types message and confirms → SmokeSignalSendMessage arrives.
//   4. Server validates cooldown, clamps message, records cooldown end time.
//   5. Message is broadcast as a styled announcement pop-up to every living Tribe-dept player.

using Content.Shared._Misfits.SmokeSignal;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Mind;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Roles; // #Misfits Add - resolve dual-department Willower jobs.
using Content.Shared.Roles.Jobs;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Player; // #Misfits Fix - ActorComponent lives in Robust.Shared.Player
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Misfits.SmokeSignal;

/// <summary>
/// Handles the smoke signal activation verb, BUI messaging, cooldown management, and department broadcast.
/// </summary>
public sealed class SmokeSignalSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedJobSystem _jobs = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!; // #Misfits Add - resolve dual-department Willower jobs

    private readonly HashSet<EntityUid> _nearbyBuffer = new();

    public override void Initialize()
    {
        base.Initialize();

        // Register the activation verb on any entity with SmokeSignalComponent
        SubscribeLocalEvent<SmokeSignalComponent, GetVerbsEvent<ActivationVerb>>(OnGetVerb);

        // E/complex activation should open the signal UI for tribe members before bonfire extinguishing handles it.
        SubscribeLocalEvent<SmokeSignalComponent, ActivateInWorldEvent>(OnActivateInWorld, before: new[] { typeof(FlammableSystem) });

        // Handle message sent from the BUI text input
        SubscribeLocalEvent<SmokeSignalComponent, SmokeSignalSendMessage>(OnSendMessage);
    }

    // ──────────────────────────────────────────────────────────────────────────────────
    //  Verb: "Send Smoke Signal"
    // ──────────────────────────────────────────────────────────────────────────────────

    private void OnGetVerb(EntityUid uid, SmokeSignalComponent component, GetVerbsEvent<ActivationVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        // #Misfits Change - respect optional activator role allowlist.
        if (!CanUse(args.User, component))
            return;

        args.Verbs.Add(new ActivationVerb
        {
            Text = Loc.GetString(component.Verb), // #Misfits Change - allow Tree-specific wording
            Act = () =>
            {
                TryOpenSignalUi(uid, component, args.User);
            },
            Priority = 1,
        });
    }

    private void OnActivateInWorld(EntityUid uid, SmokeSignalComponent component, ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        if (!component.OpenOnActivate || !CanUse(args.User, component)) // #Misfits Change - Tree activation stays verb-only.
            return;

        if (!TryOpenSignalUi(uid, component, args.User))
            return;

        args.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────────────────────
    //  BUI message handler: validate and broadcast
    // ──────────────────────────────────────────────────────────────────────────────────

    private void OnSendMessage(EntityUid uid, SmokeSignalComponent component, SmokeSignalSendMessage args)
    {
        if (args.Actor is not { Valid: true } sender) // #Misfits Fix - .Session removed from BUI messages; use args.Actor
            return;

        if (!CanUse(sender, component)) // #Misfits Change - enforce optional sender role allowlist.
            return;

        // Re-validate cooldown (race guard)
        if (component.CooldownEnd.HasValue && _timing.CurTime < component.CooldownEnd.Value)
            return;

        // Clamp and sanitize message
        var message = args.Message.Trim();
        if (message.Length == 0)
            return;

        if (message.Length > component.MaxMessageLength)
            message = message[..component.MaxMessageLength];

        // Record cooldown
        component.CooldownEnd = _timing.CurTime + component.Cooldown;

        // Build the broadcast text
        var broadcastText = Loc.GetString(component.BroadcastMessage, ("message", message)); // #Misfits Change - allow Tree-specific wording

        // #Misfits Change - use the tested living department recipient selection.
        foreach (var playerUid in GetRecipients(component.TargetDepartment))
            _popup.PopupEntity(broadcastText, playerUid, playerUid, PopupType.Large);

        // Also send an atmospheric notice to nearby non-tribe bystanders
        // so the signal is observable in-world (and testable without a tribe job)
        if (component.NearbyRange > 0f)
        {
            var nearbyText = Loc.GetString("smoke-signal-nearby");
            _nearbyBuffer.Clear();
            _lookup.GetEntitiesInRange(Transform(uid).Coordinates, component.NearbyRange, _nearbyBuffer);

            foreach (var nearbyUid in _nearbyBuffer)
            {
                if (!HasComp<ActorComponent>(nearbyUid))
                    continue;

                if (_mobState.IsDead(nearbyUid))
                    continue;

                // Tribe members already got the full message above; skip duplicates
                if (IsInDepartment(nearbyUid, component.TargetDepartment))
                    continue;

                _popup.PopupEntity(nearbyText, nearbyUid, nearbyUid, PopupType.Medium);
            }
        }

        // Log so admins can see in the server log
        Log.Info($"[SmokeSignal] {ToPrettyString(sender)} sent: {message}");
    }

    // ──────────────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────────────────

    private bool TryOpenSignalUi(EntityUid uid, SmokeSignalComponent component, EntityUid user)
    {
        // Check cooldown before opening the UI
        if (component.CooldownEnd.HasValue && _timing.CurTime < component.CooldownEnd.Value)
        {
            var remaining = (int) Math.Ceiling((component.CooldownEnd.Value - _timing.CurTime).TotalSeconds);
            _popup.PopupEntity(
                Loc.GetString(component.CooldownMessage, ("seconds", remaining)), // #Misfits Change - allow Tree-specific wording
                uid, user, PopupType.SmallCaution);
            return false;
        }

        // Open the text input window for the activating player
        _ui.OpenUi(uid, SmokeSignalUiKey.Key, user);
        return true;
    }

    // #Misfits Change - exact department membership supports dual-department Willower roles.
    internal bool CanUse(EntityUid uid, SmokeSignalComponent component)
    {
        if (!_mind.TryGetMind(uid, out var mindId, out _)
            || !_jobs.MindTryGetJob(mindId, out _, out var job))
            return false;

        if (!_prototypes.TryIndex<DepartmentPrototype>(component.TargetDepartment, out var department)
            || !department.Roles.Contains(job.ID))
            return false;

        return component.ActivatorJobs is not { Count: > 0 }
            || component.ActivatorJobs.Contains(job.ID);
    }

    internal bool IsInDepartment(EntityUid uid, string departmentId)
    {
        if (!_mind.TryGetMind(uid, out var mindId, out _)
            || !_jobs.MindTryGetJob(mindId, out _, out var job)
            || !_prototypes.TryIndex<DepartmentPrototype>(departmentId, out var department))
            return false;

        return department.Roles.Contains(job.ID);
    }

    // #Misfits Add - share exact living Actor selection between delivery and regression coverage.
    internal IEnumerable<EntityUid> GetRecipients(string departmentId)
    {
        var query = EntityQueryEnumerator<ActorComponent>();
        while (query.MoveNext(out var playerUid, out _))
        {
            if (_mobState.IsDead(playerUid) || !IsInDepartment(playerUid, departmentId))
                continue;

            yield return playerUid;
        }
    }
}
