// #Misfits Add - Marker for Enclave-issue equipment. Only members of the Enclave faction can put it on,
// so a suit stripped off a dead trooper is a trophy rather than a free set of the best armor on the map.

namespace Content.Shared._Misfits.Enclave;

/// <summary>
///     Blocks equipping the item unless the wearer belongs to <see cref="Faction"/>.
///     Starting gear is equipped with force: true, so this never blocks a spawning Enclave role
///     even though job specials add the faction membership after gear is handed out.
/// </summary>
[RegisterComponent]
public sealed partial class MisfitsEnclaveIssueComponent : Component
{
    /// <summary>
    ///     Faction the wearer must belong to.
    /// </summary>
    [DataField]
    public string Faction = "Enclave";

    /// <summary>
    ///     Set false on trophy or admin variants to skip the gate entirely.
    /// </summary>
    [DataField]
    public bool RequiresFaction = true;
}
