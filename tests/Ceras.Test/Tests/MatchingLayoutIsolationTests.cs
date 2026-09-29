// What a robust `Load` reads, pinned by what it refuses to read.
//
// TODO.md, Milestone 2, asks for exactly this: *"Pin zero graph builds and zero schema parses on the
// fingerprint-matched generated path with exact counters, plus a representative allocation check; do not
// let migration cost leak into matching-layout loads."*
//
// ── The instrument, and why it is not a counter ───────────────────────────────────────────────────────
//
// The first thing to check was whether a precise counter already exists. None does: the only internal
// counters in `src/Ceras` are `CerasBufferPool.LiveRentals` (a rental balance, so a missing return is
// detectable) and the post-seal mutation counter in `RobustCustomFormatterPolicy`. There is nothing that
// counts schema parses and nothing that counts graph builds, so one would have to be added — and both
// candidate places cost something:
//
//   * inside `SchemaSection.ReadUnvalidated` it is a static increment on the *schema-parsing* path, which
//     is the path the rest of the suite already spends microseconds in; the generated path would pay
//     nothing, but the library would still carry mutable static state it does not otherwise have;
//   * inside `OldDataGraphBuilder` it is a static increment in a file another agent owns, and the
//     builder is a type no production `Load` can reach today, so the counter would be dead weight in
//     shipped code.
//
// This file adds **no production code and no counter at all**. The trap is injected into the input bytes
// instead, and it is the stronger instrument for the same price:
//
//   * a counter can only say "that function was not entered" — it is blind to a parser that has been
//     inlined, copied, renamed or reintroduced under another call site, all of which are exactly how
//     "this work cannot put graph construction on the generated-reader path" would be lost;
//   * the poisoned bytes say something stronger: *the file's schema section was never interpreted.* Every
//     way of parsing it fails, loudly, and the assertion is made on the same bytes.
//
// The poison is the file's whole **schema region** — the schema bytes and their own string table, that
// is `[RobustHeader.Size, dataSectionOffset)` — overwritten with `0xFF` and the CRC32 recomputed. Nothing
// else changes: the header, the section offsets, the layout fingerprint, the data section and the payload
// string table are all byte-identical, and the fingerprint therefore still selects the same reader. `0xFF`
// is not an arbitrary garbage pattern: the first LEB128 any parser reads there is five bytes of `0xFF`,
// which runs past the format's own five-byte bound and is refused before a single entry is interpreted.
// Every test that relies on it also asserts, on the same bytes, that a reader that *does* parse the
// schema is refused — that is what stops a pass from being a coincidence of a poison nothing would have
// noticed.
//
// ── What "a real v5 file" means on each side ─────────────────────────────────────────────────────────
//
// The two halves need different files, and the asymmetry is not a convenience:
//
//   * The **matching** half needs a file whose stored fingerprint equals this build's. Every checked-in
//     golden describes a *previous* model, so its fingerprint cannot match by construction (that is what
//     `HistoricalFixtureFiles.AssertDoesNotMatchCurrentLayout` exists to state). Forcing one to match by
//     flipping its fingerprint would be TODO.md's "infer cross-version compatibility from a fingerprint
//     flip", and it would also make the file lie about its own layout. So the file is produced by this
//     build's real `Save`, and `AssertRealV5Envelope` pins every envelope fact that makes it a v5 file
//     rather than a byte array: magic, format version 5, an intact CRC32, ordered section offsets, a
//     non-empty schema region, and a schema section that really names this build's types.
//   * The **mismatched** half needs the opposite, and uses the checked-in goldens: real v5 files written
//     by `fixtures/Ceras.HistoricalWriter`, a separate assembly this one does not reference, so the v1
//     CLR model is absent and a load has to answer from the bytes. That is the cross-version claim, and
//     it is not made from a fingerprint flip or a same-version roundtrip.
//
// ── What is deliberately *not* pinned, and why ───────────────────────────────────────────────────────
//
// Two things this file could have asserted and does not, because each one is expected to stop being true
// rather than being wrong today.
//
// **Which reader implements the mismatched path.** A mismatched load currently goes through
// `SchemaTraversalEngine` and the migration plans. It is intended to stop doing that: the old-data graph
// is being built precisely so that path can be redirected onto it (TODO.md, Milestone 2). So nothing here
// asserts that the mismatched path is the streaming interpreter, and nothing asserts it is *not* a graph
// build either. The only claim is the one that has to survive the redirection: **a mismatched load reads
// the file's own schema section**, because whatever ends up implementing it has to map a schema this
// build does not recognise. That is asserted on a real historical v5 file, with the poison, and it does
// not care what does the mapping.
//
// **Whether the graph reader can decode every shape.** An earlier version of this file pinned "no graph
// build on the matched path" behaviourally, by loading a save holding a `Nullable<T>` and a `Tuple` — two
// shapes the graph had no node kind for, so a load that succeeded proved it had not gone through the
// graph. That proof was sound and it was also temporary: the positional node kinds being added to the
// builder remove exactly those refusals, and the assertions would have gone red on a change that is
// progress. They are gone. What replaces them does not depend on the graph's coverage at all: a graph
// build can only begin by parsing the file's schema section, so a matched load that *succeeds* on a file
// whose schema section is unparseable did not build one, whatever the builder can represent today.
//
// For the same reason the allocation test below reports a number and checks only self-calibrating
// properties of one body, rather than comparing it against a multiple of what the graph or the migration
// path costs. Those multiples were measured, and they were accurate, but they are ratios against two
// moving products; the exact poison comparison is the durable claim, and a reported figure is what a
// future change needs in order to be compared against something.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.GameplayLoop;
using Ceras.Generated;
using Ceras.Test.HistoricalFixtures;
using Xunit;

namespace Ceras.Test
{
    /// <summary>
    /// A referenced node: two of them point at each other, and the second also points at the first, so
    /// one file holds a cycle <em>and</em> a shared reference. A load that quietly lost either would
    /// still produce plausible values, which is the whole reason the poison tests below assert identity
    /// and not only field contents.
    /// </summary>
    [CerasContext("MatchingPathIsolation")]
    [CerasSerializable]
    public class IsolatedNode
    {
        public int Id;
        public string Label;
        public IsolatedNode Peer;
    }

    /// <summary>
    /// The root the matching-layout half writes and reads. Its own context, so the manifest is exactly
    /// these two types: the fingerprint a save records is this build's, and nothing else in the test
    /// assembly can move it.
    /// <para>
    /// The members are deliberately ordinary — an <see cref="int"/>, a <see cref="string"/>, a
    /// <see cref="List{T}"/> and a <see cref="Dictionary{TKey,TValue}"/>, all of which the file-only graph
    /// reader has always been able to decode. Nothing here is a positional value type, so every assertion
    /// in this file stays valid whatever the graph builder does and does not yet have a node kind for.
    /// </para>
    /// </summary>
    [CerasContext("MatchingPathIsolation")]
    [CerasSerializable]
    public class IsolatedSave
    {
        public int Version;
        public string Title;
        public List<IsolatedNode> Nodes;
        public Dictionary<string, int> Scores;
    }

    public class MatchingLayoutIsolationTests
    {
        const string Ctx = "MatchingPathIsolation";

        // ── 1. The matched path never parses the schema section ──────────────────────────────

        /// <summary>
        /// The property TODO.md Milestone 2 asks to be pinned: a load whose stored layout fingerprint
        /// matches this context's does not parse the file's schema section.
        /// <para>
        /// The whole of the schema region is overwritten with `0xFF` and the CRC recomputed, so the file
        /// is still a valid v5 block that still selects the same reader — the header, the offsets, the
        /// fingerprint, the data section and the payload string table are all byte-identical to the file
        /// the same serializer wrote, and the <em>only</em> difference is the region a schema parse would
        /// have to read. The load then returns the graph the unpoisoned file returns, with its reference
        /// identity and its cycle intact.
        /// </para>
        /// <para>
        /// This also rules out a graph build on this path, and it does so without asking anything of the
        /// graph's coverage: a build can only begin by parsing the file's schema section, so a load that
        /// succeeds on bytes no schema parse can read did not build one. The two controls below make that
        /// a statement about the bytes rather than about the code: on these <em>same</em> poisoned bytes,
        /// a reader that does parse the schema refuses, and the file-only graph reader refuses too.
        /// </para>
        /// </summary>
        [Fact]
        public void A_Matching_Fingerprint_Load_Never_Reads_The_Files_Schema_Section()
        {
            var serializer = Writer();
            byte[] clean = serializer.Save(Model(), 1);
            AssertRealV5Envelope(clean, typeof(IsolatedSave).FullName);

            // The stored fingerprint really is this build's, which is what makes "the matched path" the
            // thing being measured rather than an assumption.
            Assert.Equal(BitConverter.ToUInt64(clean, RobustHeader.FingerprintOffset),
                FingerprintOf(serializer.Config.SchemaRegistry));

            byte[] poisoned = PoisonSchemaSection(clean);

            // The poison changed the schema region and the CRC, and nothing else. If it had moved the
            // fingerprint or an offset, the load below would not be testing what it claims to.
            Assert.Equal(BitConverter.ToUInt64(clean, RobustHeader.FingerprintOffset),
                BitConverter.ToUInt64(poisoned, RobustHeader.FingerprintOffset));
            Assert.Equal(clean.Length, poisoned.Length);
            Assert.Equal(SectionOffset(clean, RobustHeader.DataSectionOffsetOffset),
                SectionOffset(poisoned, RobustHeader.DataSectionOffsetOffset));
            for (int i = (int)SectionOffset(clean, RobustHeader.DataSectionOffsetOffset); i < clean.Length; i++)
                Assert.Equal(clean[i], poisoned[i]);
            Assert.NotEqual(clean[RobustHeader.ChecksumOffset], poisoned[RobustHeader.ChecksumOffset]);

            // ── the control, in three parts.
            // (i) the clean bytes are good: they load, and they are what the poisoned load is compared
            //     against;
            // (ii) the clean file is representable as a graph, so the graph's refusal below is about the
            //      poison and not about the file having no graph shape — this is the one place the graph's
            //      coverage is consulted, and it is consulted about shapes this model has always had;
            // (iii) both readers that parse the schema refuse these exact bytes.
            AssertSameGraph(Model(), serializer.Load<IsolatedSave>(clean, 1));
            Assert.NotNull(OldFileGraph.Read(clean));
            Assert.Throws<CerasDataCorruptionException>(() => OldFileGraph.Read(poisoned));
            Assert.Throws<CerasDataCorruptionException>(
                () => serializer.Load<IsolatedSave>(ForceMismatch(poisoned), 1));

            // ── the property.
            AssertSameGraph(Model(), serializer.Load<IsolatedSave>(poisoned, 1));
        }

        /// <summary>
        /// The same claim as a measurement rather than as a survival: a warm run of the matched path over
        /// a poisoned file moves the allocation counter by <em>exactly</em> the same number of bytes as a
        /// warm run over the unpoisoned one. Not "about the same" — the same total.
        /// <para>
        /// This is the closest thing to an exact zero the property admits, and it is exact because it is
        /// structural rather than statistical. The two files differ only inside a region the matched path
        /// never reads; every allocation on that path is a function of the bytes it <em>does</em> read plus
        /// the size of the object it returns; and the file's length — the one thing a parse of the region
        /// would have shown up in — is identical. One added dictionary, list, string array or schema entry
        /// moves the total, so this fails on any amount of schema parsing, not only on an amount large
        /// enough to notice on its own.
        /// </para>
        /// <para>
        /// Two things make the equality stable rather than lucky, and both were needed: the two bodies are
        /// <b>interleaved</b> rather than measured in consecutive blocks, and the comparison is over the
        /// <b>minimum</b> of several passes.
        /// </para>
        /// <list type="bullet">
        /// <item>Interleaving, with the counter read around each call, puts both files under the same
        /// conditions. Measured as separate blocks the first one absorbed a one-off allocation on the
        /// measuring thread — a few hundred bytes, and only when the whole suite ran in parallel, since
        /// nothing else on that thread is under this test's control.</item>
        /// <item>Taking the minimum of a few passes removes that noise for good. A one-off allocation can
        /// only ever inflate a pass, never deflate it, so the minimum is the cleanest estimate of each
        /// body's steady cost, and it is the estimate the two are compared on. Reporting all of them
        /// keeps a real per-pass difference visible instead of hiding it behind the minimum.</item>
        /// </list>
        /// <para>
        /// Both bodies are also warmed before anything is measured, because a byte-for-byte comparison
        /// between a tier-0 body and a tier-1 one is a comparison of compilation tiers. See the ratio
        /// test below for what that costs when it is skipped.
        /// </para>
        /// </summary>
        [Fact]
        public void Poisoning_The_Schema_Section_Changes_Not_One_Byte_Of_The_Matched_Paths_Allocations()
        {
            const int WarmupIterations = 256, Measured = 200, Passes = 3;

            var serializer = Writer();
            byte[] clean = serializer.Save(Model(), 1);
            byte[] poisoned = PoisonSchemaSection(clean);
            AssertRealV5Envelope(clean, typeof(IsolatedSave).FullName);

            var loadClean = new Action(() => serializer.Load<IsolatedSave>(clean, 1));
            var loadPoisoned = new Action(() => serializer.Load<IsolatedSave>(poisoned, 1));

            // The trap is armed before anything is measured. Two identical totals are only a statement
            // about the schema region if the poison would have cost something to read, and "would have" is
            // the part that cannot be observed from inside a load that does not parse it. Without this, a
            // poison that stopped being lethal would leave this test passing while proving nothing.
            Assert.Throws<CerasDataCorruptionException>(() => OldFileGraph.Read(poisoned));
            Assert.Throws<CerasDataCorruptionException>(
                () => serializer.Load<IsolatedSave>(ForceMismatch(poisoned), 1));

            // Time-based, for both bodies, before either is measured — see the summary for why.
            foreach (var body in new[] { loadClean, loadPoisoned })
                LoopMeasurementRunner.WarmUp(body, WarmupIterations, LoopMeasurementRunner.DefaultWarmupMilliseconds);

            long bestClean = long.MaxValue, bestPoisoned = long.MaxValue;
            for (int pass = 0; pass < Passes; pass++)
            {
                long cleanBytes = 0, poisonedBytes = 0;
                for (int i = 0; i < Measured; i++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    loadClean();
                    long afterClean = GC.GetAllocatedBytesForCurrentThread();
                    loadPoisoned();
                    long afterPoisoned = GC.GetAllocatedBytesForCurrentThread();

                    cleanBytes += afterClean - before;
                    poisonedBytes += afterPoisoned - afterClean;
                }

                bestClean = Math.Min(bestClean, cleanBytes);
                bestPoisoned = Math.Min(bestPoisoned, poisonedBytes);
                Console.WriteLine($"[MatchingLayoutIsolation] pass {pass}: clean={cleanBytes} poisoned={poisonedBytes} over {Measured} loads each");
            }

            Assert.Equal(bestClean, bestPoisoned);

            // And the loads really ran, so the equality above is not two empty loops.
            AssertSameGraph(Model(), serializer.Load<IsolatedSave>(clean, 1));
            AssertSameGraph(Model(), serializer.Load<IsolatedSave>(poisoned, 1));
        }

        /// <summary>
        /// The representative allocation check for a matching-layout load of a real v5 file: it reports
        /// what one costs, and it pins the two properties of that figure which do not depend on anything
        /// else in the tree.
        /// <para>
        /// <b>It is positive.</b> A load allocates the object graph it hands back and decodes the payload
        /// string table on every call, so "0 B/op" would be a false claim. The figure is the baseline a
        /// later change is compared against, which is the whole reason it is printed: on this tree a warm
        /// matching-layout load of <see cref="IsolatedSave"/> costs roughly 1.5 kB, in Release and in
        /// Debug alike.
        /// </para>
        /// <para>
        /// <b>It does not grow.</b> Every pass is within a tenth of the best one, so a load neither
        /// accumulates state across calls nor varies with how many calls preceded it. This is checked
        /// against the body itself — best pass against worst pass — rather than against a constant or
        /// against what some other code path costs, which is what makes it self-calibrating: it stays true
        /// when the object grows, when the builder changes shape, and when the mismatched path is
        /// redirected. Ten percent is a wide band for a deterministic counter; it is there to absorb a
        /// tier-1 promotion landing mid-run, not to accommodate real variation.
        /// </para>
        /// <para>
        /// The exactness this milestone asks for is not here, and does not need to be: the test above
        /// establishes it, by comparing a poisoned file against an unpoisoned one on the same body. This
        /// one measures a body against itself, which cannot go stale as the rest of the tree moves.
        /// </para>
        /// </summary>
        [Fact]
        public void A_Warm_Matching_Layout_Load_Reports_An_Allocation_Figure_That_Does_Not_Grow()
        {
            const int WarmupIterations = 256, Measured = 200, Passes = 3;
            const double SteadinessBand = 1.10;

            var serializer = Writer();
            byte[] clean = serializer.Save(Model(), 1);
            AssertRealV5Envelope(clean, typeof(IsolatedSave).FullName);

            var load = new Action(() => serializer.Load<IsolatedSave>(clean, 1));

            // Warm for time, not for a bare iteration count: tiered compilation leaves a freshly-called
            // method at tier-0 and suspends call counting for 100 ms, and a 256-iteration warm-up of a
            // microsecond loop finishes long before promotion can start. Skipping this is not theoretical
            // — it is why the first pass below used to read about 3% high. Tier-0 allocates at least as
            // much as tier-1, so the direction of that error is a false negative, never a false positive.
            LoopMeasurementRunner.WarmUp(load, WarmupIterations, LoopMeasurementRunner.DefaultWarmupMilliseconds);

            var passes = new long[Passes];
            for (int pass = 0; pass < Passes; pass++)
                passes[pass] = LoopMeasurementRunner.MeasureBytes(load, 0, Measured);

            long best = passes.Min();
            Console.WriteLine(
                $"[MatchingLayoutIsolation] matching-layout load of {clean.Length} B: " +
                $"{string.Join(", ", passes.Select(p => $"{p / (double)Measured:F0} B/load"))} " +
                $"(best {best / (double)Measured:F0} B/load)");

            Assert.True(best > 0,
                "a load allocates the graph it returns, so a zero here would mean the measured loop did nothing");

            Assert.True(passes.Max() <= best * SteadinessBand,
                $"the matching-layout load's cost moved between passes: {string.Join(", ", passes)} bytes over " +
                $"{Measured} loads, against a best of {best}. A load is expected to cost the same however many " +
                "loads preceded it, so a spread this wide means something on the path is growing or varying.");

            // The loads really did run and really did produce the graph, so the figure above describes a
            // working reader rather than a fast no-op.
            AssertSameGraph(Model(), serializer.Load<IsolatedSave>(clean, 1));
        }

        // ── 2. A mismatched load does read the schema section ───────────────────────────────

        /// <summary>
        /// The other half of the milestone, on the one file in the repository that is a real v5 save from
        /// a real old writer: <c>SaveGame.bin</c>, written by <c>fixtures/Ceras.HistoricalWriter</c> in a
        /// compilation this assembly does not reference, so v1's CLR model is absent at load time and
        /// every answer has to come from the file.
        /// <para>
        /// Two things are pinned, and the second is the one that has to survive a redirection:
        /// </para>
        /// <list type="number">
        /// <item>the stored fingerprint cannot match this context, so the load cannot take the generated
        /// path — asserted directly rather than assumed, since a file that did match would make this test
        /// silently test the wrong reader;</item>
        /// <item>the same file with its schema region poisoned is <b>refused</b>, so a mismatched load
        /// reads the file's own schema section. This holds for the migration interpreter that runs today
        /// and for whatever replaces it: mapping a schema this build does not recognise requires reading
        /// it, and there is no other source for the names, members and shapes that mapping needs. That is
        /// the whole claim, and nothing here says which component does the mapping.</item>
        /// </list>
        /// <para>
        /// The load is also asserted to produce current objects — the widened <see cref="long"/>, a member
        /// this build added taking its declared default, and one instance behind two names. Not because
        /// the fixture's contents need re-verifying (<c>HistoricalFixtureTests</c> owns that) but because
        /// a mismatch that returned something *other* than the current model would satisfy the two claims
        /// above while accomplishing nothing.
        /// </para>
        /// </summary>
        [Fact]
        public void A_Mismatched_Load_Reads_The_Files_Schema_Section_And_Still_Produces_Current_Objects()
        {
            byte[] file = HistoricalFixtureFiles.Fixture("SaveGame.bin");
            HistoricalFixtureFiles.AssertIntactChecksum(file);

            var reader = HistoricalFixtureFiles.MigrationReader("HistoricalCurrent");
            HistoricalFixtureFiles.AssertDoesNotMatchCurrentLayout(file, "HistoricalCurrent");
            AssertRealV5Envelope(file, "Ceras.HistoricalWriter.V1.SaveGame");

            // (1) the real file, poisoned, is refused: this path parses the schema section.
            Assert.Throws<CerasDataCorruptionException>(
                () => reader.Load<SaveGame>(PoisonSchemaSection(file), 1));

            // (2) unpoisoned, the same load produces the current model.
            SaveGame game = reader.Load<SaveGame>(file, 1);
            Assert.IsType<SaveGame>(game);
            Assert.Equal(1234L, game.Players[0].Mana);      // v1 wrote an int; the current model widened it
            Assert.Equal(0, game.Players[0].Level);          // a member v1 never wrote, so its declared default
            Assert.Same(game.Players[0], game.Main);         // one instance behind two names, mapped through
            Assert.Equal("Ayla", game.Players[0].Name);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        static CerasRobustSerializer Writer()
            => new(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                // The build timestamp is what makes a save reproducible byte for byte, and nothing here
                // depends on which build wrote it.
                c.SetUnknownBuildTimestamp();
            });

        /// <summary>
        /// A graph with a shared reference and a cycle, so the matching-layout loads below assert
        /// identity and not only field values: a reader that lost the sharing would still produce
        /// plausible numbers.
        /// </summary>
        static IsolatedSave Model()
        {
            var first = new IsolatedNode { Id = 1, Label = "alpha" };
            var second = new IsolatedNode { Id = 2, Label = "beta", Peer = first };
            first.Peer = second;

            return new IsolatedSave
            {
                Version = 7,
                Title = "the title",
                Nodes = new List<IsolatedNode> { first, second },
                Scores = new Dictionary<string, int> { ["Ayla"] = 100, ["Bryn"] = 250 },
            };
        }

        static void AssertSameGraph(IsolatedSave expected, IsolatedSave actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Scores.Count, actual.Scores.Count);
            foreach (var pair in expected.Scores)
                Assert.Equal(pair.Value, actual.Scores[pair.Key]);

            Assert.Equal(expected.Nodes.Count, actual.Nodes.Count);
            for (int i = 0; i < expected.Nodes.Count; i++)
            {
                Assert.Equal(expected.Nodes[i].Id, actual.Nodes[i].Id);
                Assert.Equal(expected.Nodes[i].Label, actual.Nodes[i].Label);
            }

            // The two nodes reference each other, and the second also references the first. Two separate
            // copies of the same values would pass everything above; this is the part that does not.
            // `Model` writes first.Peer = second and second.Peer = first, so the cycle runs
            // Nodes[0] -> Nodes[1] -> Nodes[0] with no third instance anywhere.
            Assert.Same(actual.Nodes[1], actual.Nodes[0].Peer);
            Assert.Same(actual.Nodes[0], actual.Nodes[1].Peer);
        }

        static long SectionOffset(byte[] file, int offsetField)
            => BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(offsetField));

        /// <summary>
        /// Everything that makes a byte array a v5 robust save rather than a plausible one, asserted
        /// rather than assumed. The schema region is the one this file poisons, so it is checked to be
        /// present and to hold a real manifest — a zero-length or empty region would make the poison
        /// vacuous, and a region that does not describe the model would mean the trap is pointed at the
        /// wrong bytes.
        /// </summary>
        static void AssertRealV5Envelope(byte[] file, string expectedStableName = null)
        {
            Assert.True(file.Length >= RobustHeader.Size, "a robust file is at least a header");
            Assert.Equal((byte)'C', file[0]);
            Assert.Equal((byte)'E', file[1]);
            Assert.Equal((byte)'R', file[2]);
            Assert.Equal((byte)'A', file[3]);
            Assert.Equal(CerasRobustSerializer.FormatVersion, file[RobustHeader.FormatVersionOffset]);
            HistoricalFixtureFiles.AssertIntactChecksum(file);

            long schemaStrings = SectionOffset(file, RobustHeader.SchemaStringOffsetOffset);
            long dataSection = SectionOffset(file, RobustHeader.DataSectionOffsetOffset);
            long stringTable = SectionOffset(file, RobustHeader.StringTableOffsetOffset);

            Assert.InRange(schemaStrings, RobustHeader.Size, dataSection);
            Assert.InRange(dataSection, schemaStrings, stringTable);
            Assert.InRange(stringTable, dataSection, file.Length);

            // The region the poison overwrites has to be big enough for the poison to be read as a
            // five-byte LEB128 that runs past the format's own bound, and non-empty so there is something
            // to poison at all.
            int schemaRegion = (int)dataSection - RobustHeader.Size;
            Assert.True(schemaRegion >= 8, $"the schema region is {schemaRegion} bytes; too small for the poison to mean anything");

            // The dump decodes the file's own schema section, so a stable name in it is proof that the
            // poisoned region is a real manifest and not padding. The name is the file's: the current
            // model's for a save this build wrote, v1's for a historical one.
            string dump = CerasRobustSerializer.DumpSchema(file);
            Assert.Contains(expectedStableName, dump);
            Assert.Contains("// Type count:", dump);
        }

        /// <summary>
        /// Overwrites the file's whole schema region — the schema bytes and their own string table, that
        /// is <c>[RobustHeader.Size, dataSectionOffset)</c> — with a byte no schema parse can survive, and
        /// recomputes the CRC32 so the block is otherwise a valid, verifiable v5 save.
        /// <para>
        /// <c>0xFF</c> is chosen for a reason rather than for convenience: the first LEB128 any reader
        /// takes from this region is five bytes of <c>0xFF</c>, which hits the format's five-byte bound
        /// and is refused before an entry is interpreted. The header, the section offsets, the layout
        /// fingerprint, the data section and the payload string table are left exactly as they were, so the
        /// file still selects the same reader and the only variable is whether the schema region is read.
        /// </para>
        /// </summary>
        static byte[] PoisonSchemaSection(byte[] file)
        {
            var copy = (byte[])file.Clone();
            int end = (int)SectionOffset(copy, RobustHeader.DataSectionOffsetOffset);
            for (int i = RobustHeader.Size; i < end; i++)
                copy[i] = 0xFF;

            FixChecksum(copy);
            return copy;
        }

        /// <summary>
        /// A copy whose stored layout fingerprint no longer matches, with the CRC recomputed. This is
        /// how the schema-parsing path is selected, and nothing more: the bytes still describe the current
        /// model, so no compatibility claim in this file is drawn from it — the cross-version claim is the
        /// checked-in golden above, written by a writer that no longer exists.
        /// </summary>
        static byte[] ForceMismatch(byte[] file)
        {
            var copy = (byte[])file.Clone();
            copy[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixChecksum(copy);
            return copy;
        }

        static void FixChecksum(byte[] file)
        {
            uint crc = Ceras.Helpers.HashHelpers.CalculateCrc32WithZeroedRange(
                file, RobustHeader.ChecksumOffset, RobustHeader.ChecksumSize);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(RobustHeader.ChecksumOffset), crc);
        }

        static ulong FingerprintOf(SchemaRegistry registry)
        {
            registry.Seal();
            return registry.ComputeFingerprint();
        }
    }
}
