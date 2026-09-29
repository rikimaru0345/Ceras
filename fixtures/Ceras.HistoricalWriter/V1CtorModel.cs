// The v1 models for the *constructor-argument* fixtures: a type that, in a later build, gained a
// [CerasConstructor] whose parameters come out of the payload — and whose bodies do not simply keep what
// they were handed.
//
// This is the shape the generated reader has always had (it reads every member into a local, runs the
// constructor, and then writes *all* of the locals back, so a constructor that rewrites a value cannot
// change the result) and the shape the migration interpreter disagreed with, because it assigned only
// the members no parameter fed. The disagreement was worst for the members a value comparison cannot
// check: `Cover.Inner` is a reference the constructor drops, and a reader that leaves it dropped returns
// a load that reports success with the graph's sharing and its cycle broken.
//
// The file has to be a real cross-version file rather than a same-layout one with the fingerprint
// flipped, so the current model changes the schema in a way the fingerprint notices: `Retired` is removed
// and `Label` is added. A rename alone would not have been enough — the layout fingerprint hashes the
// member list and the members' names and types, not their `previousNames`, so a renamed member on its
// own fingerprints the same and would have taken the generated reader.
//
// Its own context, for the reason every other model here is one: a context's schema section records
// every type registered in it and is fingerprinted as a whole, so adding these types to a context that
// already has fixtures would renumber their type ids and change their bytes. See README.md.
using Ceras;
using Ceras.Generated;

namespace Ceras.HistoricalWriter.V1Ctor
{
    /// <summary>
    /// v1's ledger row: a plain class with no constructor at all. In the current model this is a
    /// <c>Ledger</c> again, but it has gained a <c>[CerasConstructor]</c> that takes <c>Amount</c> — and
    /// <em>normalizes</em> it — so the value the file holds has to come back even though the constructor
    /// would have replaced it.
    /// </summary>
    [CerasContext("HistoricalV1Ctor")]
    [CerasSerializable]
    public class Ledger
    {
        /// <summary>Fed to the current model's constructor, which clamps a negative value to zero.</summary>
        public int Amount;

        /// <summary>Renamed to <c>Memo</c> in the current model.</summary>
        public string Note;

        /// <summary>Removed in the current model; its four bytes have to be skipped between the two.</summary>
        public int Retired;

        /// <summary>A self-cycle, so the instance has to be registered before the constructor runs.</summary>
        public Ledger Twin;
    }

    /// <summary>
    /// A cover over a ledger row. The current model gives it a <c>[CerasConstructor]</c> whose one
    /// parameter is a <em>reference</em> — the member the file shares with <c>LedgerBook.Main</c> — and
    /// whose body drops it. Nothing but the stored value can put the shared instance back, so this is the
    /// case a value comparison cannot make and an identity assertion can.
    /// </summary>
    [CerasContext("HistoricalV1Ctor")]
    [CerasSerializable]
    public class Cover
    {
        /// <summary>The <b>same instance</b> as <see cref="LedgerBook.Main"/>.</summary>
        public Ledger Inner;

        /// <summary>Not fed to any constructor; the current model rewrites it, and the file must still win.</summary>
        public int Count;
    }

    /// <summary>
    /// The save root. <see cref="Main"/> and <see cref="Cover"/>'s <c>Inner</c> were one instance, and
    /// <see cref="Main"/> is its own twin, so the graph holds three references to one object and a cycle.
    /// The trailing sentinel is a fixed-width <c>uint</c> written after everything else, so a skip that is
    /// off by anything at all shows up as a wrong number instead of as a load that happens to succeed.
    /// </summary>
    [CerasContext("HistoricalV1Ctor")]
    [CerasSerializable]
    public class LedgerBook
    {
        public Ledger Main;
        public Cover Wrap;
        public uint Marker;
    }
}
