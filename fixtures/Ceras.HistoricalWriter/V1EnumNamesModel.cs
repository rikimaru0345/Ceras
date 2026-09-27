// A third "old" model, in its own serialization context, for the enum *wire name*: how a renamed or
// renumbered enum member is matched between two builds.
//
// It lives in its own context for the same reason V1PooledModel.cs does: a context has one finite
// manifest, so adding types to the frozen V1 model above would renumber every type id in
// SaveGame.bin / Inventory.bin / PlayerCycle.bin / Relic.bin / RelicVault.bin. A separate context
// keeps those fixtures byte-identical, and a reader still has to migrate this file for real: the current
// model (tests/Ceras.Test/Tests/EnumWireNameMigrationTests.cs) declares different names *and different
// numbers* for the same members.
//
// See the folder README for how to regenerate.
using Ceras;
using Ceras.Generated;

namespace Ceras.HistoricalWriter.V1EnumNames
{
    /// <summary>
    /// v1's enum, with one member per way a member can be identified across a migration. Every value
    /// here is renumbered in the current model to a number the current enum does not have, so a member
    /// that fails to match cannot be mistaken for one that matched: it arrives as an unnamed number.
    /// </summary>
    public enum Tier : int
    {
        /// <summary>Untouched, and the control: the same name and the same number in both builds.</summary>
        Bronze = 0,

        /// <summary>
        /// Renumbered only. With no attribute the wire name is the C# name, so this member is matched by
        /// that name in every generation of the format - including the files written before the schema
        /// recorded an explicit wire name.
        /// </summary>
        Silver = 1,

        /// <summary>
        /// Renumbered, with its wire name pinned here. The file therefore records <c>WireAce</c>, and
        /// this is the member a reader has to match by that name rather than by the C# spelling.
        /// <para>
        /// The C# name is deliberately left alone in the current model, because that is what makes this
        /// member readable by *both* generations of the format: a file written before the schema recorded
        /// the wire name says <c>Ace</c>, and the migration still has to find the member.
        /// </para>
        /// </summary>
        [CerasStableName("WireAce")]
        Ace = 2,

        /// <summary>
        /// Renamed in the current model with no wire name anywhere, so the only way to match it is an
        /// explicit [PreviousName] alias there. Nothing in this file records the future name.
        /// </summary>
        Adept = 3,

        /// <summary>
        /// Renamed in the current model while keeping its pinned wire name: the case
        /// <c>[CerasStableName]</c> exists for, and the case a file written *before* the schema recorded
        /// that wire name cannot be migrated through. See the reader test for the documented limit.
        /// </summary>
        [CerasStableName("WireCrown")]
        Crown = 4,
    }

    [CerasContext("HistoricalV1EnumNames")]
    [CerasSerializable]
    public class Loadout
    {
        public Tier Bronze;   // Bronze
        public Tier Silver;   // Silver    (renumbered)
        public Tier Ace;      // Ace       (renumbered, pinned wire name WireAce)
        public Tier Adept;    // Adept     (renamed in the current model, reachable by alias only)
        public Tier Crown;    // Crown     (renamed in the current model, pinned wire name WireCrown)
        public int Tail;      // a field after the enum members, so a misread value is visible
    }
}
