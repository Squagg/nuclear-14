// #Misfits Add - Power armour combat drop out of a cruising vertibird.
using System.Numerics;
using Content.Server.Camera;
using Content.Shared._Misfits.PowerArmor;
using Content.Shared._Misfits.Vehicles.Vertibird;
using Content.Shared.Camera;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server._Misfits.Vehicles.Vertibird;

public sealed partial class VertibirdSystem
{
    [Dependency] private readonly CameraRecoilSystem _recoil = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;

    private void InitializeCombatDrop()
    {
        SubscribeLocalEvent<VertibirdCombatDropComponent, VertibirdCombatDropActionEvent>(OnCombatDrop);
        SubscribeLocalEvent<VertibirdCombatDropComponent, ComponentShutdown>(OnCombatDropShutdown);
    }

    /// <summary>
    /// Grants or revokes the drop action as a seat changes hands. Only power armour
    /// occupants get it; anyone else would be jumping to their death.
    /// </summary>
    private void RefreshCombatDropSeat(Entity<VertibirdComponent> ent, EntityUid? occupant, bool boarding)
    {
        if (occupant is not { } rider || !TryComp<VertibirdCombatDropComponent>(ent, out var drop))
            return;

        if (!boarding)
        {
            if (drop.DropActions.Remove(rider, out var existing))
                _actions.RemoveAction(rider, existing);

            return;
        }

        if (!HasComp<PowerArmorWornComponent>(rider) || drop.DropActions.ContainsKey(rider))
            return;

        EntityUid? action = null;
        _actions.AddAction(rider, ref action, drop.DropAction, ent.Owner);

        if (action != null)
            drop.DropActions[rider] = action.Value;
    }

    private void OnCombatDrop(Entity<VertibirdCombatDropComponent> ent, ref VertibirdCombatDropActionEvent args)
    {
        if (args.Handled)
            return;

        var rider = args.Performer;

        if (!TryComp<VertibirdComponent>(ent, out var vertibird) ||
            vertibird.State != VertibirdFlightState.Cruising)
        {
            _popup.PopupEntity(Loc.GetString("vertibird-drop-not-cruising"), ent, rider);
            return;
        }

        // The suit is what makes this survivable. Losing it mid-flight revokes the option.
        if (!HasComp<PowerArmorWornComponent>(rider))
        {
            _popup.PopupEntity(Loc.GetString("vertibird-drop-needs-power-armor"), ent, rider);
            return;
        }

        if (!TryComp(ent.Owner, out TransformComponent? xform) ||
            xform.MapUid is not { } mapUid ||
            !_multiZ.TryMapOffset(mapUid, -1, out _))
        {
            _popup.PopupEntity(Loc.GetString("vertibird-drop-no-ground"), ent, rider);
            return;
        }

        args.Handled = true;

        var landingPosition = _transform.GetWorldPosition(xform);

        // Passing a null user sidesteps the airborne unbuckle block, which exists to stop
        // unarmoured passengers stepping out. The suit is the exemption.
        _buckle.TryUnbuckle(rider, user: null, popup: false);

        // TryMove teleports between levels rather than routing through MZ falling,
        // so no impact damage is applied. That is the point: they land unscathed.
        if (!_multiZ.TryMove(rider, -1, worldPosition: landingPosition))
            return;

        SendVertibirdEmote(ent.Owner, "vertibird-rp-combat-drop");
        DoImpact(ent, rider);
    }

    /// <summary>
    /// Cracks the ground and rattles the screens of everyone close enough to see it.
    /// </summary>
    private void DoImpact(Entity<VertibirdCombatDropComponent> ent, EntityUid rider)
    {
        var landing = _transform.GetMapCoordinates(rider);

        Spawn(ent.Comp.ImpactEffect, landing);
        _audio.PlayPvs(ent.Comp.ImpactSound, rider);

        var range = ent.Comp.ShakeRange;
        foreach (var nearby in _lookup.GetEntitiesInRange(landing, range))
        {
            if (nearby == rider || !HasComp<CameraRecoilComponent>(nearby))
                continue;

            var delta = _transform.GetMapCoordinates(nearby).Position - landing.Position;
            var distance = delta.Length();

            // Straight linear falloff; anyone at the edge of the radius feels nothing.
            var strength = ent.Comp.ShakeStrength * (1f - distance / range);
            if (strength <= 0f)
                continue;

            // Kick away from the impact. Directly on top of it, pick an arbitrary axis.
            var direction = distance > 0.01f ? delta / distance : new Vector2(0f, 1f);
            _recoil.KickCamera(nearby, direction * strength);
        }
    }

    private void OnCombatDropShutdown(Entity<VertibirdCombatDropComponent> ent, ref ComponentShutdown args)
    {
        foreach (var (rider, action) in ent.Comp.DropActions)
        {
            _actions.RemoveAction(rider, action);
        }

        ent.Comp.DropActions.Clear();
    }
}
