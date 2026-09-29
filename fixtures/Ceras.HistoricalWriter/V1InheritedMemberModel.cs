// The v1 models for the *inherited member* fixtures: the two evolutions a game makes most often and that
// no checked-in fixture covered together — a member declared on a **base type** that is renamed and
// widened, and a member of a **fixed-size struct that is a collection element** whose width changes while
// the struct itself does not. Both are reached through a nested collection, and the instantiated type is
// written more than once so the sharing has to survive the migration as well.
//
// Two contexts, for the reason every other pair of contexts in this project is split (see
// V1ArrayShapeModel.cs): a context's schema section records *every* type registered in it, and the
// migration plans are built for every type a file's section describes, so a single rejected type in a
// shared context would abort the load of the file that has nothing to do with it. `…Narrow` holds the one
// shape that must be refused, and `…Inherited` holds the ones that must keep loading.
//
// Every crate ends in a fixed-width `uint Marker` written after every member it is pinning, so a member
// whose elements were mis-decoded shows up as a silently wrong number rather than as a load that happens
// to succeed. The member order is not the declaration order: the writers sort members by
// (member type name, member name, declaring type), so the names are chosen to put the marker last and to
// make the collection precede the polymorphic slot (see the table in README.md).
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;

namespace Ceras.HistoricalWriter.V1Inherited
{
    /// <summary>
    /// v1's base type. It is **never instantiated**: no value in the file has this runtime type, so the
    /// only way its members reach the payload is through <see cref="Soldier"/>. It still gets an entry of
    /// its own in the schema section, and that entry has to resolve to this build's base type or the load
    /// is refused before a byte is read — which is what makes the file a test of an <em>inherited</em>
    /// rename rather than of a rename on a type the file happens to instantiate.
    /// <para>
    /// It is a plain class rather than an <c>abstract</c> one, and that is load-bearing rather than
    /// cosmetic: a generated manifest registers an abstract or interface type with a <em>placeholder</em>
    /// schema whose <c>previousTypeNames</c> is <c>Array.Empty</c> (the generator's
    /// <c>AppendMinimalRegistration</c>), so a renamed abstract base has no alias for a file to resolve and
    /// the load is refused. See the note on the reader-side model in
    /// tests/Ceras.Test/Tests/HistoricalInheritedMemberFixtureTests.cs.
    /// </para>
    /// <para>
    /// <see cref="Power"/> is the whole point: v1 spells it one way and gives it an <c>int</c>, and the
    /// current model both renames it and widens it, so the reader has to find it by its old name on a base
    /// type it never instantiates and then coerce the value. <see cref="Armour"/> is removed outright, so
    /// its four bytes have to be skipped between the two.
    /// </para>
    /// </summary>
    [CerasContext("HistoricalV1Inherited")]
    [CerasSerializable]
    public class Unit
    {
        /// <summary>Renamed <em>and</em> widened (<c>int</c> to <c>long</c>) in the current model.</summary>
        public int Power;

        /// <summary>Removed in the current model; its bytes have to be skipped.</summary>
        public int Armour;
    }

    /// <summary>
    /// v1's concrete type. It declares one member of its own, so the file's payload for it holds a base
    /// member, a base member that was removed, and a derived one — and the order is decided by the sort
    /// key rather than by the base chain, which is why the reader has to follow the file's own order.
    /// </summary>
    [CerasContext("HistoricalV1Inherited")]
    [CerasSerializable]
    public class Soldier : Unit
    {
        public string Name;
    }

    /// <summary>
    /// A fixed-size value. The current model widens <see cref="Count"/>, so the two builds' declared byte
    /// sizes for this type disagree (8 and 12) while the *file* is still internally consistent: a struct
    /// element is a plain count of bytes with no marker and no length, so anything that advanced by this
    /// build's own width would shift the second element and everything after it.
    /// </summary>
    [CerasContext("HistoricalV1Inherited")]
    [CerasSerializable]
    public struct Ammo
    {
        /// <summary>Widened to <c>long</c> in the current model.</summary>
        public int Count;

        public int Kind;
    }

    /// <summary>
    /// The save root. <see cref="Leader"/> is the <b>same instance</b> as <see cref="Squad"/>'s first
    /// element, so the renamed and widened inherited member is read once and the second occurrence has to
    /// come back as the very same object — a reader that built a second instance would report success.
    /// <see cref="Loadout"/> and <see cref="Reserve"/> carry the widened struct element on the list and the
    /// keyed positions, which are separate code paths.
    /// </summary>
    [CerasContext("HistoricalV1Inherited")]
    [CerasSerializable]
    public class Roster
    {
        public Soldier Leader;
        public Dictionary<string, Ammo> Reserve;
        public List<Ammo> Loadout;
        public List<Soldier> Squad;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1InheritedNarrow
{
    /// <summary>
    /// The same fixed-size value type, on the direction that must be <b>refused</b>: the current model
    /// narrows <see cref="Grams"/> from <c>int</c> to <c>short</c>, which is not one of the five
    /// conversions Ceras performs. A save holding this file has to be rejected by name rather than have
    /// the value dropped, and the interesting part is <em>where</em> the refusal can name it: this type
    /// is only ever reached as a collection's argument, so there is no crate member to point at.
    /// </summary>
    [CerasContext("HistoricalV1InheritedNarrow")]
    [CerasSerializable]
    public struct Weight
    {
        public int Grams;
        public int Kind;
    }

    /// <summary>A crate whose only member is a collection of the refused struct, plus the sentinel.</summary>
    [CerasContext("HistoricalV1InheritedNarrow")]
    [CerasSerializable]
    public class Crate
    {
        public List<Weight> Loads;
        public uint Marker;
    }
}
