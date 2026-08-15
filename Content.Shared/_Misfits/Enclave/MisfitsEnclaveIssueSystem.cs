using Content.Shared.Inventory.Events;
using Content.Shared.NPC.Systems;

namespace Content.Shared._Misfits.Enclave;

/// <summary>
///     Gates <see cref="MisfitsEnclaveIssueComponent"/> items to members of the Enclave faction.
///     Mirrors PowerArmorProficiencySystem and MisfitsC27ArmorSystem: runs shared so client
///     prediction cancels the equip animation immediately instead of snapping it back.
/// </summary>
public sealed class MisfitsEnclaveIssueSystem : EntitySystem
{
    [Dependency] private readonly NpcFactionSystem _faction = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MisfitsEnclaveIssueComponent, BeingEquippedAttemptEvent>(OnEquipAttempt);
    }

    private void OnEquipAttempt(Entity<MisfitsEnclaveIssueComponent> item, ref BeingEquippedAttemptEvent args)
    {
        if (!item.Comp.RequiresFaction)
            return;

        if (_faction.IsMember(args.EquipTarget, item.Comp.Faction))
            return;

        args.Reason = "enclave-issue-faction-required";
        args.Cancel();
    }
}
