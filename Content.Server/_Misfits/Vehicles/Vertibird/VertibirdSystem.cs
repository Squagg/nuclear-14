// #Misfits Add - Server-side flyable vertibird POC.
using System.Numerics;
using Content.Server.Chat.Systems;
using Content.Shared._Misfits.Vehicles.Vertibird;
using Content.Shared._MultiZ.Core.Components;
using Content.Server._MultiZ.Core;
using Content.Shared.Actions;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Chat;
using Content.Shared.IdentityManagement;
using Content.Shared.Popups;
using Content.Shared.Stealth;
using Content.Shared.Stealth.Components;
using Content.Shared.UserInterface;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;
using Robust.Server.GameObjects;
using Robust.Shared.Player;

namespace Content.Server._Misfits.Vehicles.Vertibird;

public sealed partial class VertibirdSystem : EntitySystem
{
    private static readonly string[] StartupProgressEmotes =
    [
        "vertibird-rp-startup-switches",
        "vertibird-rp-startup-avionics",
        "vertibird-rp-startup-rotors",
        "vertibird-rp-startup-throttle",
    ];

    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedBuckleSystem _buckle = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private MZSystem _multiZ = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedStealthSystem _stealth = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private readonly Dictionary<EntityUid, int> _pendingSeatSelections = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<VertibirdComponent, StrapAttemptEvent>(OnStrapAttempt);
        SubscribeLocalEvent<VertibirdComponent, StrappedEvent>(OnStrapped);
        SubscribeLocalEvent<VertibirdComponent, UnstrapAttemptEvent>(OnUnstrapAttempt);
        SubscribeLocalEvent<VertibirdComponent, UnstrappedEvent>(OnUnstrapped);
        SubscribeLocalEvent<VertibirdComponent, VertibirdFlightActionEvent>(OnFlightAction);
        SubscribeLocalEvent<VertibirdComponent, VertibirdLandActionEvent>(OnLandAction);
        SubscribeLocalEvent<VertibirdComponent, VertibirdMoveUpActionEvent>(OnMoveUpAction);
        SubscribeLocalEvent<VertibirdComponent, VertibirdMoveDownActionEvent>(OnMoveDownAction);
        SubscribeLocalEvent<VertibirdComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<VertibirdComponent, AfterActivatableUIOpenEvent>(OnAfterUiOpen);
        SubscribeLocalEvent<VertibirdComponent, VertibirdSelectSeatMessage>(OnSelectSeat);
        SubscribeNetworkEvent<VertibirdControlInputMessage>(OnControlInput);

        InitializeTurret();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        UpdateTurretEyes();

        var query = EntityQueryEnumerator<VertibirdComponent, MZPhysicsComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var vertibird, out var mzPhysics, out var physics, out var xform))
        {
            switch (vertibird.State)
            {
                case VertibirdFlightState.Starting:
                    UpdateStartup((uid, vertibird));
                    break;
                case VertibirdFlightState.TakingOff:
                    UpdateTakeoff(uid, vertibird, mzPhysics, frameTime);
                    break;
                case VertibirdFlightState.Landing:
                    UpdateLanding(uid, vertibird, mzPhysics, frameTime);
                    break;
                case VertibirdFlightState.Cruising:
                    HoldHover(uid, vertibird, mzPhysics);
                    UpdateCruising(uid, vertibird, physics, xform, frameTime);
                    break;
                case VertibirdFlightState.ChangingAltitude:
                    UpdateAltitudeTransition(uid, vertibird, mzPhysics, xform);
                    break;
            }
        }
    }

    private void OnStrapAttempt(Entity<VertibirdComponent> ent, ref StrapAttemptEvent args)
    {
        var occupant = args.Buckle.Owner;

        if (!_pendingSeatSelections.TryGetValue(occupant, out var seatIndex))
        {
            args.Cancelled = true;
            _popup.PopupEntity(Loc.GetString("vertibird-use-seat-manifest"), ent, occupant);
            return;
        }

        if (!IsValidSeat(seatIndex) || ent.Comp.SeatOccupants[seatIndex] != null)
        {
            args.Cancelled = true;
            return;
        }

        if (seatIndex == 0 && !HasComp<VertibirdPilotPerkComponent>(occupant))
        {
            args.Cancelled = true;
            _popup.PopupEntity(Loc.GetString("vertibird-pilot-required"), ent, occupant);
        }
    }

    private void OnStrapped(Entity<VertibirdComponent> ent, ref StrappedEvent args)
    {
        var occupant = args.Buckle.Owner;
        if (!_pendingSeatSelections.Remove(occupant, out var seatIndex) || !IsValidSeat(seatIndex))
            return;

        ent.Comp.SeatOccupants[seatIndex] = occupant;
        HideOccupant(occupant);

        if (seatIndex == 0)
        {
            ent.Comp.Pilot = occupant;
            AddPilotActions(occupant, ent);
        }

        RefreshTurretSeat(ent, seatIndex, occupant);

        Dirty(ent);
        UpdateUi(ent);
    }

    private void OnUnstrapAttempt(Entity<VertibirdComponent> ent, ref UnstrapAttemptEvent args)
    {
        // Pressing Land commits the craft to a controlled descent and should
        // immediately unlock the cabin for egress; waiting for the final
        // Grounded tick left occupants trapped through the landing sequence.
        if (ent.Comp.State is VertibirdFlightState.Landing or VertibirdFlightState.Grounded)
            return;

        if (args.User == null || args.User != args.Buckle.Owner)
            return;

        args.Cancelled = true;

        if (args.Popup)
            _popup.PopupEntity(Loc.GetString("vertibird-unbuckle-blocked"), ent, args.User.Value);
    }

    private void OnUnstrapped(Entity<VertibirdComponent> ent, ref UnstrappedEvent args)
    {
        if (ent.Comp.Pilot != args.Buckle.Owner)
        {
            var seat = GetSeatIndex(ent.Comp, args.Buckle.Owner);
            if (seat != null)
            {
                ent.Comp.SeatOccupants[seat.Value] = null;
                RefreshTurretSeat(ent, seat.Value, null);
            }

            UnhideOccupant(args.Buckle.Owner);
            Dirty(ent);
            UpdateUi(ent);
            return;
        }

        RemovePilotAction(args.Buckle.Owner, ent.Comp);
        RemovePilotRelay(args.Buckle.Owner, ent.Owner);
        ent.Comp.Pilot = null;

        var pilotSeat = GetSeatIndex(ent.Comp, args.Buckle.Owner);
        if (pilotSeat != null)
        {
            ent.Comp.SeatOccupants[pilotSeat.Value] = null;
            RefreshTurretSeat(ent, pilotSeat.Value, null);
        }

        UnhideOccupant(args.Buckle.Owner);
        Dirty(ent);
        UpdateUi(ent);
    }

    private void OnFlightAction(Entity<VertibirdComponent> ent, ref VertibirdFlightActionEvent args)
    {
        if (args.Handled)
            return;

        if (ent.Comp.Pilot != args.Performer)
            return;

        if (!HasComp<VertibirdPilotPerkComponent>(args.Performer))
            return;

        switch (ent.Comp.State)
        {
            case VertibirdFlightState.Grounded:
                StartTakeoff(ent);
                args.Handled = true;
                break;
        }
    }

    private void OnLandAction(Entity<VertibirdComponent> ent, ref VertibirdLandActionEvent args)
    {
        if (args.Handled || !CanUsePilotAction(ent, args.Performer))
            return;

        if (ent.Comp.State is VertibirdFlightState.TakingOff or VertibirdFlightState.Cruising)
        {
            StartLanding(ent);
            args.Handled = true;
        }
        else if (ent.Comp.State == VertibirdFlightState.Starting)
        {
            CancelStartup(ent);
            args.Handled = true;
        }
    }

    private void OnMoveUpAction(Entity<VertibirdComponent> ent, ref VertibirdMoveUpActionEvent args)
    {
        if (args.Handled || !CanUsePilotAction(ent, args.Performer))
            return;

        args.Handled = TryMoveZ(ent, 1);
    }

    private void OnMoveDownAction(Entity<VertibirdComponent> ent, ref VertibirdMoveDownActionEvent args)
    {
        if (args.Handled || !CanUsePilotAction(ent, args.Performer))
            return;

        args.Handled = TryMoveZ(ent, -1);
    }

    private bool CanUsePilotAction(Entity<VertibirdComponent> ent, EntityUid performer)
    {
        if (ent.Comp.Pilot != performer)
            return false;

        if (HasComp<VertibirdPilotPerkComponent>(performer))
            return true;

        _popup.PopupEntity(Loc.GetString("vertibird-pilot-required"), ent, performer);
        return false;
    }

    private void StartTakeoff(Entity<VertibirdComponent> ent)
    {
        ent.Comp.State = VertibirdFlightState.Starting;
        ent.Comp.StartupStartedAt = _timing.CurTime;
        ent.Comp.StartupFinishedAt = _timing.CurTime + TimeSpan.FromSeconds(ent.Comp.StartupDuration);
        ent.Comp.StartupEmoteIndex = 0;
        ent.Comp.DriftVelocity = Vector2.Zero;
        ent.Comp.HeldInputs = VertibirdControlInput.None;
        ent.Comp.StartupSoundStream = _audio.Stop(ent.Comp.StartupSoundStream);

        if (ent.Comp.StartupSound != null)
            ent.Comp.StartupSoundStream = _audio.PlayPvs(ent.Comp.StartupSound, ent.Owner)?.Entity;

        SendVertibirdEmote(ent.Owner, "vertibird-rp-startup");
        Dirty(ent);
        UpdateUi(ent);
    }

    private void CancelStartup(Entity<VertibirdComponent> ent)
    {
        ent.Comp.StartupSoundStream = _audio.Stop(ent.Comp.StartupSoundStream);
        ent.Comp.State = VertibirdFlightState.Grounded;
        ent.Comp.StartupStartedAt = TimeSpan.Zero;
        ent.Comp.StartupFinishedAt = TimeSpan.Zero;
        ent.Comp.StartupEmoteIndex = 0;
        ent.Comp.DriftVelocity = Vector2.Zero;
        ent.Comp.HeldInputs = VertibirdControlInput.None;
        Dirty(ent);
        UpdateUi(ent);
    }

    private void UpdateStartup(Entity<VertibirdComponent> ent)
    {
        SendStartupProgressEmotes(ent);

        if (_timing.CurTime < ent.Comp.StartupFinishedAt)
            return;

        StartTakeoffLift(ent);
    }

    private void SendStartupProgressEmotes(Entity<VertibirdComponent> ent)
    {
        if (ent.Comp.StartupStartedAt == TimeSpan.Zero || ent.Comp.StartupDuration <= 0f)
            return;

        var startupElapsed = (_timing.CurTime - ent.Comp.StartupStartedAt).TotalSeconds;
        var emoteInterval = ent.Comp.StartupDuration / (StartupProgressEmotes.Length + 1);

        while (ent.Comp.StartupEmoteIndex < StartupProgressEmotes.Length &&
               startupElapsed >= emoteInterval * (ent.Comp.StartupEmoteIndex + 1))
        {
            SendVertibirdEmote(ent.Owner, StartupProgressEmotes[ent.Comp.StartupEmoteIndex]);
            ent.Comp.StartupEmoteIndex++;
            Dirty(ent);
        }
    }

    private void StartTakeoffLift(Entity<VertibirdComponent> ent)
    {
        if (!TryComp<MZPhysicsComponent>(ent, out var mzPhysics) ||
            !TryComp(ent.Owner, out TransformComponent? xform) ||
            !TryComp<PhysicsComponent>(ent, out var physics) ||
            xform.MapUid is not { } mapUid)
            return;

        // MultiZ only supports entities parented directly to their map. Using
        // SetMapCoordinates here reattaches the vertibird to the grid under it,
        // causing MultiZ to reset LocalPosition to zero every update.
        var worldPosition = _transform.GetWorldPosition(xform);
        _transform.SetCoordinates(ent.Owner, new EntityCoordinates(mapUid, worldPosition));
        Log.Info($"[Vertibird] Startup complete -> TakingOff | Parent={Transform(ent.Owner).ParentUid} | Map={mapUid}");

        mzPhysics.LocalPosition = 0f;
        mzPhysics.Velocity = 0f;
        _physics.SetBodyStatus(ent.Owner, physics, BodyStatus.InAir);
        ent.Comp.State = VertibirdFlightState.TakingOff;
        ent.Comp.StartupStartedAt = TimeSpan.Zero;
        ent.Comp.StartupFinishedAt = TimeSpan.Zero;
        ent.Comp.StartupEmoteIndex = 0;
        ent.Comp.StartupSoundStream = _audio.Stop(ent.Comp.StartupSoundStream);
        ent.Comp.DriftVelocity = Vector2.Zero;
        ent.Comp.HeldInputs = VertibirdControlInput.None;
        SendVertibirdEmote(ent.Owner, "vertibird-rp-takeoff");
        Dirty(ent);
        RemComp<MZFallingComponent>(ent.Owner);
        UpdateUi(ent);
    }

    private void StartLanding(Entity<VertibirdComponent> ent)
    {
        if (!TryComp<MZPhysicsComponent>(ent, out var mzPhysics))
            return;

        ent.Comp.StartupSoundStream = _audio.Stop(ent.Comp.StartupSoundStream);
        StopFlightLoop(ent.Comp);

        if (ent.Comp.LandingSound != null)
            _audio.PlayPvs(ent.Comp.LandingSound, ent.Owner);

        mzPhysics.Velocity = 0f;
        ent.Comp.State = VertibirdFlightState.Landing;
        ent.Comp.DriftVelocity = Vector2.Zero;
        ent.Comp.HeldInputs = VertibirdControlInput.None;
        Dirty(ent);
        RemComp<MZFallingComponent>(ent.Owner);
        UpdateUi(ent);
    }

    private void UpdateTakeoff(EntityUid uid, VertibirdComponent vertibird, MZPhysicsComponent mzPhysics, float frameTime)
    {
        var next = MathF.Min(vertibird.HoverAltitude, mzPhysics.LocalPosition + vertibird.VerticalSpeed * frameTime);
        mzPhysics.LocalPosition = next;
        mzPhysics.Velocity = 0f;
        RemComp<MZFallingComponent>(uid);

        if (next < vertibird.HoverAltitude)
            return;

        vertibird.State = VertibirdFlightState.Cruising;
        // #Misfits Debug - confirm transition (remove after fix)
        Log.Info($"[Vertibird] Takeoff complete -> Cruising | Pilot={ToPrettyString(vertibird.Pilot)}");
        StartFlightLoop(uid, vertibird);
        Dirty(uid, vertibird);
        UpdateUi((uid, vertibird));
    }

    private void UpdateLanding(EntityUid uid, VertibirdComponent vertibird, MZPhysicsComponent mzPhysics, float frameTime)
    {
        vertibird.DriftVelocity = Vector2.Zero;

        var next = MathF.Max(0f, mzPhysics.LocalPosition - vertibird.VerticalSpeed * frameTime);
        mzPhysics.LocalPosition = next;
        mzPhysics.Velocity = 0f;
        RemComp<MZFallingComponent>(uid);

        if (next > 0f)
            return;

        vertibird.State = VertibirdFlightState.Grounded;
        vertibird.HeldInputs = VertibirdControlInput.None;
        StopFlightLoop(vertibird);
        SendVertibirdEmote(uid, "vertibird-rp-landing");

        if (TryComp<PhysicsComponent>(uid, out var physics))
            _physics.SetBodyStatus(uid, physics, BodyStatus.OnGround);

        // Landing is the one point where the craft should become grid-parented
        // again so ordinary ground collision and grid movement semantics resume.
        _transform.SetMapCoordinates(uid, _transform.GetMapCoordinates(uid));
        Dirty(uid, vertibird);
        RemComp<MZFallingComponent>(uid);
        UpdateUi((uid, vertibird));
    }

    private void HoldHover(EntityUid uid, VertibirdComponent vertibird, MZPhysicsComponent mzPhysics)
    {
        mzPhysics.LocalPosition = vertibird.HoverAltitude;
        mzPhysics.Velocity = 0f;
        RemComp<MZFallingComponent>(uid);
    }

    private void UpdateCruising(EntityUid uid, VertibirdComponent vertibird, PhysicsComponent physics, TransformComponent xform, float frameTime)
    {
        // Read the server-authoritative input state sent by the pilot's controls.
        var inputs = vertibird.HeldInputs;

        var rotation = _transform.GetWorldRotation(xform);
        var turn = 0f;

        if ((inputs & VertibirdControlInput.Left) != 0)
            turn += 1f;

        if ((inputs & VertibirdControlInput.Right) != 0)
            turn -= 1f;

        if (turn != 0f)
            rotation += Angle.FromDegrees(vertibird.TurnSpeedDegrees * turn * frameTime);

        var thrust = Vector2.Zero;
        var forward = rotation.ToWorldVec();

        if ((inputs & VertibirdControlInput.Forward) != 0)
            thrust += forward * vertibird.ThrustAcceleration;

        if ((inputs & VertibirdControlInput.Back) != 0)
            thrust -= forward * vertibird.ReverseAcceleration;

        vertibird.DriftVelocity += thrust * frameTime;

        if (thrust == Vector2.Zero)
        {
            var drag = MathF.Max(0f, 1f - vertibird.FlightDrag * frameTime);
            vertibird.DriftVelocity *= drag;
        }

        var speed = vertibird.DriftVelocity.Length();
        if (speed > vertibird.MaxFlightSpeed)
            vertibird.DriftVelocity = vertibird.DriftVelocity / speed * vertibird.MaxFlightSpeed;

        // The vertibird is a dynamic body. Drive its physics velocity instead of
        // teleporting its transform, which the physics step immediately corrects.
        _physics.SetLinearVelocity(uid, vertibird.DriftVelocity, body: physics);
        _physics.SetAngularVelocity(uid, 0f, body: physics);
        _transform.SetWorldRotation(uid, rotation);
    }

    private bool TryMoveZ(Entity<VertibirdComponent> ent, int offset)
    {
        if (ent.Comp.State != VertibirdFlightState.Cruising)
            return false;

        if (!TryComp<MZPhysicsComponent>(ent, out var mzPhysics))
            return false;

        if (!TryComp(ent.Owner, out TransformComponent? xform) ||
            xform.MapUid is not { } mapUid ||
            !_multiZ.TryMapOffset(mapUid, offset, out var targetMap) ||
            targetMap is not { } resolvedTargetMap ||
            !HasComp<MapComponent>(resolvedTargetMap.Owner))
            return false;

        ent.Comp.State = VertibirdFlightState.ChangingAltitude;
        ent.Comp.AltitudeTransitionFinishedAt = _timing.CurTime + TimeSpan.FromSeconds(5);
        ent.Comp.AltitudeTargetMap = resolvedTargetMap.Owner;
        ent.Comp.AltitudeOffset = offset;
        ent.Comp.HeldInputs = VertibirdControlInput.None;
        mzPhysics.Velocity = 0f;
        ent.Comp.DriftVelocity = Vector2.Zero;
        Dirty(ent);
        UpdateUi(ent);
        return true;
    }

    private void UpdateAltitudeTransition(
        EntityUid uid,
        VertibirdComponent vertibird,
        MZPhysicsComponent mzPhysics,
        TransformComponent xform)
    {
        mzPhysics.Velocity = 0f;
        vertibird.DriftVelocity = Vector2.Zero;

        if (_timing.CurTime < vertibird.AltitudeTransitionFinishedAt)
            return;

        if (vertibird.AltitudeTargetMap is not { } targetMap ||
            !HasComp<MapComponent>(targetMap))
        {
            vertibird.State = VertibirdFlightState.Cruising;
            vertibird.AltitudeTargetMap = null;
            Dirty(uid, vertibird);
            UpdateUi((uid, vertibird));
            return;
        }

        var worldPosition = _transform.GetWorldPosition(xform);
        var altitudeOffset = vertibird.AltitudeOffset;
        _transform.SetCoordinates(uid, new EntityCoordinates(targetMap, worldPosition));
        mzPhysics.LocalPosition = altitudeOffset > 0 ? 0.05f : 0.95f;
        mzPhysics.Velocity = 0f;
        vertibird.State = VertibirdFlightState.Cruising;
        vertibird.AltitudeTargetMap = null;
        vertibird.AltitudeTransitionFinishedAt = TimeSpan.Zero;
        vertibird.AltitudeOffset = 0;
        Dirty(uid, vertibird);
        UpdateUi((uid, vertibird));
        SendVertibirdEmote(uid, altitudeOffset > 0 ? "vertibird-rp-z-up" : "vertibird-rp-z-down");
    }

    private void StartFlightLoop(EntityUid uid, VertibirdComponent vertibird)
    {
        if (vertibird.FlightSoundStream != null || vertibird.FlightLoopSound == null)
            return;

        vertibird.FlightSoundStream = _audio.PlayPvs(vertibird.FlightLoopSound, uid)?.Entity;
    }

    private void StopFlightLoop(VertibirdComponent vertibird)
    {
        vertibird.FlightSoundStream = _audio.Stop(vertibird.FlightSoundStream);
    }

    private void OnShutdown(Entity<VertibirdComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.StartupSoundStream = _audio.Stop(ent.Comp.StartupSoundStream);
        StopFlightLoop(ent.Comp);

        foreach (var occupant in ent.Comp.SeatOccupants)
        {
            if (occupant != null)
                UnhideOccupant(occupant.Value);
        }
    }

    private void OnAfterUiOpen(Entity<VertibirdComponent> ent, ref AfterActivatableUIOpenEvent args)
    {
        UpdateUi(ent);
    }

    private void OnSelectSeat(Entity<VertibirdComponent> ent, ref VertibirdSelectSeatMessage args)
    {
        var user = args.Actor;
        var seatIndex = args.SeatIndex;

        if (!IsValidSeat(seatIndex))
            return;

        if (ent.Comp.State != VertibirdFlightState.Grounded)
        {
            _popup.PopupEntity(Loc.GetString("vertibird-seat-airborne-blocked"), ent, user);
            return;
        }

        if (ent.Comp.SeatOccupants[seatIndex] != null)
            return;

        if (seatIndex == 0 && !HasComp<VertibirdPilotPerkComponent>(user))
        {
            _popup.PopupEntity(Loc.GetString("vertibird-pilot-required"), ent, user);
            return;
        }

        var currentSeat = GetSeatIndex(ent.Comp, user);
        if (currentSeat != null)
        {
            ent.Comp.SeatOccupants[currentSeat.Value] = null;
            ent.Comp.SeatOccupants[seatIndex] = user;

            if (currentSeat.Value == 0)
            {
                RemovePilotAction(user, ent.Comp);
                ent.Comp.Pilot = null;
            }

            if (seatIndex == 0)
            {
                ent.Comp.Pilot = user;
                AddPilotActions(user, ent);
            }

            RefreshTurretSeat(ent, currentSeat.Value, null);
            RefreshTurretSeat(ent, seatIndex, user);

            Dirty(ent);
            UpdateUi(ent);
            return;
        }

        _pendingSeatSelections[user] = seatIndex;
        if (!_buckle.TryBuckle(user, user, ent.Owner))
            _pendingSeatSelections.Remove(user);

        UpdateUi(ent);
    }

    private void HideOccupant(EntityUid occupant)
    {
        if (HasComp<VertibirdHiddenOccupantComponent>(occupant))
            return;

        var hidden = EnsureComp<VertibirdHiddenOccupantComponent>(occupant);
        hidden.HadStealth = TryComp<StealthComponent>(occupant, out var stealth);
        hidden.PreviousVisibility = hidden.HadStealth && stealth != null
            ? _stealth.GetVisibility(occupant, stealth)
            : 1f;

        stealth ??= EnsureComp<StealthComponent>(occupant);
        _stealth.SetVisibility(occupant, -1f, stealth);
    }

    private void UnhideOccupant(EntityUid occupant)
    {
        if (!TryComp<VertibirdHiddenOccupantComponent>(occupant, out var hidden))
            return;

        if (hidden.HadStealth)
        {
            if (TryComp<StealthComponent>(occupant, out var stealth))
                _stealth.SetVisibility(occupant, hidden.PreviousVisibility, stealth);
        }
        else
        {
            RemComp<StealthComponent>(occupant);
        }

        RemComp<VertibirdHiddenOccupantComponent>(occupant);
    }

    private static bool IsValidSeat(int seatIndex)
    {
        return seatIndex is >= 0 and < 9;
    }

    private static int? GetSeatIndex(VertibirdComponent vertibird, EntityUid occupant)
    {
        for (var i = 0; i < vertibird.SeatOccupants.Length; i++)
        {
            if (vertibird.SeatOccupants[i] == occupant)
                return i;
        }

        return null;
    }

    private void UpdateUi(Entity<VertibirdComponent> ent)
    {
        _ui.SetUiState(ent.Owner, VertibirdUiKey.Key, BuildUiState(ent.Comp));
    }

    private VertibirdSeatBoundUserInterfaceState BuildUiState(VertibirdComponent vertibird)
    {
        var seats = new VertibirdSeatUiState[vertibird.SeatOccupants.Length];
        for (var i = 0; i < seats.Length; i++)
        {
            var occupant = vertibird.SeatOccupants[i];
            seats[i] = new VertibirdSeatUiState(
                i,
                GetSeatName(i),
                occupant == null ? null : Identity.Name(occupant.Value, EntityManager),
                i == 0);
        }

        return new VertibirdSeatBoundUserInterfaceState(vertibird.State, seats);
    }

    private string GetSeatName(int index)
    {
        return index switch
        {
            0 => Loc.GetString("vertibird-seat-pilot"),
            // #Misfits Add - seat 1 mans the turret.
            1 => Loc.GetString("vertibird-seat-copilot"),
            _ => Loc.GetString("vertibird-seat-passenger", ("number", index)),
        };
    }

    private void SendVertibirdEmote(EntityUid vertibird, string locId)
    {
        _chat.TrySendInGameICMessage(
            vertibird,
            Loc.GetString(locId),
            InGameICChatType.Emote,
            ChatTransmitRange.Normal,
            ignoreActionBlocker: true);
    }

    private void RemovePilotRelay(EntityUid pilot, EntityUid vertibird)
    {
        if (TryComp<VertibirdComponent>(vertibird, out var component))
            component.HeldInputs = VertibirdControlInput.None;
    }

    private void OnControlInput(VertibirdControlInputMessage message, EntitySessionEventArgs session)
    {
        if (message.Input is not (VertibirdControlInput.Forward or VertibirdControlInput.Back or
            VertibirdControlInput.Left or VertibirdControlInput.Right))
        {
            return;
        }

        if (session.SenderSession.AttachedEntity is not { } pilot)
            return;

        var query = EntityQueryEnumerator<VertibirdComponent>();
        while (query.MoveNext(out var uid, out var vertibird))
        {
            if (vertibird.Pilot != pilot || vertibird.State != VertibirdFlightState.Cruising)
                continue;

            if (message.Pressed)
                vertibird.HeldInputs |= message.Input;
            else
                vertibird.HeldInputs &= ~message.Input;

            return;
        }
    }

    private void AddPilotActions(EntityUid pilot, Entity<VertibirdComponent> ent)
    {
        _actions.AddAction(pilot, ref ent.Comp.FlightActionEntity, ent.Comp.FlightAction, ent.Owner);
        _actions.AddAction(pilot, ref ent.Comp.LandActionEntity, ent.Comp.LandAction, ent.Owner);
        _actions.AddAction(pilot, ref ent.Comp.MoveUpActionEntity, ent.Comp.MoveUpAction, ent.Owner);
        _actions.AddAction(pilot, ref ent.Comp.MoveDownActionEntity, ent.Comp.MoveDownAction, ent.Owner);
    }

    private void RemovePilotAction(EntityUid pilot, VertibirdComponent vertibird)
    {
        _actions.RemoveAction(pilot, vertibird.FlightActionEntity);
        _actions.RemoveAction(pilot, vertibird.LandActionEntity);
        _actions.RemoveAction(pilot, vertibird.MoveUpActionEntity);
        _actions.RemoveAction(pilot, vertibird.MoveDownActionEntity);
        vertibird.FlightActionEntity = null;
        vertibird.LandActionEntity = null;
        vertibird.MoveUpActionEntity = null;
        vertibird.MoveDownActionEntity = null;
    }

}
