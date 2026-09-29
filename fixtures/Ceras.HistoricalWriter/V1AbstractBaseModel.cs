// The v1 models for the *abstract/interface base rename* fixtures: the evolution a game makes when it
// finally makes a shared base type abstract — or swaps one interface for another — and renames it at the
// same time, while values of the concrete subtypes keep flowing through the save.
//
// The point of the file is a shape no other fixture in this project has. Every other base type here is a
// plain class, so this build's manifest registers it with a **generated** schema, whose `previousTypeNames`
// is read off the author's `[PreviousTypeName]`. A base that is `abstract` (or an `interface`) is
// registered instead with a field-less *placeholder* schema, and that is a different code path in the
// source generator: `CerasFormatterGenerator.AppendMinimalRegistration`. A file that names such a base —
// which it does, because a context's schema section records *every* registered type, instantiated or not —
// therefore has a name this build has no alias for, and the load is refused before a payload byte is
// interpreted. `tests/Ceras.Test/Tests/HistoricalAbstractBaseFixtureTests.cs` is the reader side.
//
// The base's own entry is worth pinning precisely because no value in the payload has that runtime type:
// every value here is a `Wraith`, a `Golem` or a `Sentinel`. So a file describing the base exercises the
// *identity* path (does this build know the name?) and not the value path, which is what makes it a
// distinct failure from the inherited-member fixtures in `V1InheritedMemberModel.cs`.
//
// One context, for the reason every other model here has its own: a context's schema section records
// every type registered in it and is fingerprinted as a whole, so adding a type to a shared context would
// renumber the ids of every fixture already written through it.
//
// Every crate ends in a fixed-width `uint Marker` written after every member, so a member whose elements
// were mis-decoded shows up as a silently wrong number rather than as a load that happens to succeed. The
// member order is not the declaration order: the writers sort members by (member type name, member name,
// declaring type), so the names are chosen to put the marker last.
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;

namespace Ceras.HistoricalWriter.V1Abstract
{
    /// <summary>
    /// v1's base type. A plain class in v1 — v1 still had one concrete member, and the current model both
    /// renames this to <c>Menace</c> and makes it <c>abstract</c>, which is the ordinary "we finally have
    /// enough subtypes to justify it" refactor. <see cref="HitPoints"/> is renamed and widened
    /// (<c>int</c> to <c>long</c>) at the same time, so the base contributes a member that is neither
    /// spelled nor sized the same on the two sides; <see cref="Faction"/> is removed outright, so its
    /// bytes have to be skipped between the two.
    /// </summary>
    [CerasContext("HistoricalV1Abstract")]
    [CerasSerializable]
    public class Threat
    {
        /// <summary>Renamed (<c>Rating</c>) and widened (<c>int</c> to <c>long</c>) in the current model.</summary>
        public int HitPoints;

        /// <summary>Removed in the current model; its bytes have to be skipped.</summary>
        public string Faction;
    }

    /// <summary>
    /// v1's first concrete subtype. It is <em>also</em> written more than once — it is <c>Battle.Leader</c>
    /// and <c>Battle.Wave[0]</c> — so the sharing has to survive the migration as well, and the abstract
    /// base's rename cannot be papered over by a reader that quietly rebuilt a second object.
    /// </summary>
    [CerasContext("HistoricalV1Abstract")]
    [CerasSerializable]
    public class Wraith : Threat
    {
        public string Name;
    }

    /// <summary>The second concrete subtype, so the polymorphic slots hold two different concrete ids.</summary>
    [CerasContext("HistoricalV1Abstract")]
    [CerasSerializable]
    public class Golem : Threat
    {
        public int Charge;
    }

    /// <summary>
    /// v1's second base, this one already an <b>interface</b>, so it reaches the manifest through the same
    /// placeholder registration as an abstract class would. The current model renames it to <c>ITarget</c>
    /// and keeps it an interface — the ordinary "the engine's target contract got a better name" change.
    /// It declares no members, because an interface cannot: everything it promises is declared by the
    /// types that implement it, which is exactly why a rename of it has to be carried by the identity alone.
    /// </summary>
    [CerasContext("HistoricalV1Abstract")]
    [CerasSerializable]
    public interface ITargetable
    {
    }

    /// <summary>The one implementation of <see cref="ITargetable"/> this save holds a value of.</summary>
    [CerasContext("HistoricalV1Abstract")]
    [CerasSerializable]
    public class Sentinel : ITargetable
    {
        public int Armour;
    }

    /// <summary>
    /// The save root. <see cref="Leader"/> and <see cref="Wave"/>'s first element are the same instance,
    /// <see cref="Focus"/> is a polymorphic slot through the interface, and the sentinel follows all three.
    /// </summary>
    [CerasContext("HistoricalV1Abstract")]
    [CerasSerializable]
    public class Battle
    {
        /// <summary>A polymorphic slot declared as the base: the file records the concrete runtime type.</summary>
        public Threat Leader;

        /// <summary>The same base, one level in, where the element is written behind its own marker.</summary>
        public List<Threat> Wave;

        /// <summary>The same shape through the interface rather than the abstract class.</summary>
        public ITargetable Focus;

        public uint Marker;
    }
}
