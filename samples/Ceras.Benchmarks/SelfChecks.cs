using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ceras.Exceptions;

namespace Ceras.Benchmarks
{
    /// <summary>
    /// Correctness gate for the benchmark tool. A measurement that silently times a broken build is
    /// worse than no measurement, so every run of <c>--roadmap-quick</c> verifies the behaviour it is
    /// about to time first, and a failure makes the process exit non-zero.
    /// <para>
    /// These are cheap (a few hundred operations total) and deliberately cover the properties the
    /// reported numbers depend on: graph fidelity, reference identity, string interning, the upgrade
    /// path, rejection of corrupt data, and that the per-section cost proxies really decode.
    /// </para>
    /// </summary>
    public static class SelfChecks
    {
        /// <summary>A check's verdict plus the optional evidence it observed.</summary>
        public readonly struct Outcome
        {
            public bool Passed { get; }
            public string Detail { get; }

            Outcome(bool passed, string detail)
            {
                Passed = passed;
                Detail = detail ?? "";
            }

            public static Outcome Ok() => new Outcome(true, "");
            public static Outcome Ok(string detail) => new Outcome(true, detail);
            public static Outcome Fail(string detail) => new Outcome(false, detail);
        }

        /// <summary>One check result, printed as a line of the summary table.</summary>
        public readonly struct Result
        {
            public string Name { get; }
            public bool Passed { get; }
            public string Detail { get; }

            public Result(string name, Outcome outcome)
            {
                Name = name;
                Passed = outcome.Passed;
                Detail = outcome.Detail;
            }
        }

        public static List<Result> RunAll()
        {
            var results = new List<Result>();

            var robust = NewRobust();
            var world = BenchData.MakeWorld();
            byte[] worldBytes = robust.Save(world, 1);
            var envelope = RobustEnvelope.Read(worldBytes);

            Check(results, "robust save is a well-formed envelope", () =>
            {
                if (envelope.HeaderLength < 0 || envelope.SchemaStringTableLength < 0 ||
                    envelope.DataLength < 0 || envelope.StringTableLength(worldBytes.Length) < 0)
                    return Outcome.Fail($"section offsets out of order: schema={envelope.SchemaStringTableOffset} data={envelope.DataSectionOffset} strings={envelope.StringTableOffset} len={worldBytes.Length}");

                var (stored, computed) = envelope.VerifyChecksum(worldBytes);
                if (stored != computed) return Outcome.Fail($"CRC mismatch: stored 0x{stored:X8}, computed 0x{computed:X8}");

                if (envelope.Fingerprint == 0) return Outcome.Fail("layout fingerprint is zero");
                return Outcome.Ok($"fingerprint 0x{envelope.Fingerprint:X16}, {worldBytes.Length} B");
            });

            Check(results, "generated path round-trips the graph", () =>
                DescribeGraphDiff(world, robust.Load<BenchWorld>(worldBytes, 1)));

            Check(results, "load into a reused target matches a fresh target", () =>
            {
                var reused = new BenchWorld();
                robust.Load(ref reused, worldBytes, 1);
                var againstSource = DescribeGraphDiff(world, reused);
                if (!againstSource.Passed) return againstSource;

                var fresh = robust.Load<BenchWorld>(worldBytes, 1);
                return DescribeGraphDiff(fresh, reused);
            });

            Check(results, "ForceInterpreter keeps a valid CRC and changes the fingerprint", () =>
            {
                byte[] forced = BenchData.ForceInterpreter(worldBytes);
                var forcedEnvelope = RobustEnvelope.Read(forced);
                var (stored, computed) = forcedEnvelope.VerifyChecksum(forced);
                if (stored != computed) return Outcome.Fail($"CRC not repaired: stored 0x{stored:X8}, computed 0x{computed:X8}");
                if (forcedEnvelope.Fingerprint == envelope.Fingerprint) return Outcome.Fail("fingerprint was not invalidated");
                if (forcedEnvelope.Fingerprint == 0) return Outcome.Fail("fingerprint is zero after invalidation");
                return Outcome.Ok();
            });

            Check(results, "interpreter/upgrade path reproduces the graph", () =>
                DescribeGraphDiff(world, robust.Load<BenchWorld>(BenchData.ForceInterpreter(worldBytes), 1)));

            Check(results, "payload strings are interned per load", () =>
            {
                var clone = robust.Load<BenchWorld>(worldBytes, 1);
                if (clone.Tags.Count != world.Tags.Count) return Outcome.Fail($"tag count {clone.Tags.Count} != {world.Tags.Count}");
                for (int i = 0; i < clone.Tags.Count; i++)
                {
                    if (clone.Tags[i] != world.Tags[i]) return Outcome.Fail($"tag[{i}] = '{clone.Tags[i]}' != '{world.Tags[i]}'");
                    if (!ReferenceEquals(clone.Tags[i], clone.Tags[i % 4])) return Outcome.Fail($"tag[{i}] is a separate instance");
                }
                return Outcome.Ok($"{world.Tags.Count} tags over {world.Tags.Count / 4} table entries");
            });

            Check(results, "payload string-table proxy really decodes", () =>
            {
                var table = BenchData.LoadPayloadStringTable(worldBytes, envelope);
                // Index 0 is the null slot, then one entry per distinct interned string: 64 node
                // names + 4 tags + "secret" + "world".
                if (table.Length != 71) return Outcome.Fail($"payload table has {table.Length} entries (index 0 + 70 expected)");
                if (table[0] != null) return Outcome.Fail("payload table index 0 is not null");
                if (table.Skip(1).Distinct().Count() != 70) return Outcome.Fail("payload table holds duplicate strings");
                if (!table.Contains("secret")) return Outcome.Fail("payload table is missing an interned string");
                return Outcome.Ok($"{envelope.Compression}, 70 entries");
            });

            Check(results, "schema-section proxy decodes the manifest", () =>
            {
                var schemas = BenchData.ReadSchemaSection(worldBytes, envelope);
                if (schemas.Count == 0) return Outcome.Fail("no schema entries were decoded");
                if (!schemas.Values.Any(s => s.StableName.EndsWith("BenchWorld", StringComparison.Ordinal)))
                    return Outcome.Fail("decoded schemas do not mention BenchWorld");
                return Outcome.Ok($"{schemas.Count} types from {envelope.HeaderLength + envelope.SchemaStringTableLength} B");
            });

            Check(results, "deferred callback runs exactly once per load", () =>
            {
                var callback = new BenchCallback { Value = 5 };
                byte[] bytes = robust.Save(callback, 1);
                byte[] forced = BenchData.ForceInterpreter(bytes);

                BenchCallback.AfterCount = 0;
                robust.Load<BenchCallback>(bytes, 1);
                int generated = BenchCallback.AfterCount;

                BenchCallback.AfterCount = 0;
                robust.Load<BenchCallback>(forced, 1);
                int interpreted = BenchCallback.AfterCount;

                if (generated != 1) return Outcome.Fail($"generated path ran the callback {generated} times");
                if (interpreted != 1) return Outcome.Fail($"interpreter path ran the callback {interpreted} times");
                return Outcome.Ok();
            });

            Check(results, "corrupt data is rejected and the serializer survives", () =>
            {
                byte[] corrupt = (byte[])worldBytes.Clone();
                corrupt[^1] ^= 0xFF;

                bool rejected = false;
                try
                {
                    robust.Load<BenchWorld>(corrupt, 1);
                }
                catch (CerasException)
                {
                    rejected = true;
                }
                if (!rejected) return Outcome.Fail("a corrupted payload was accepted");

                // The rejected load must not have left per-operation state behind.
                return DescribeGraphDiff(world, robust.Load<BenchWorld>(worldBytes, 1));
            });

            Check(results, "transient serializer round-trips small messages", () =>
            {
                var fast = BenchData.Fast();

                var move = BenchData.MakeMove();
                var moveBack = fast.Deserialize<BenchMove>(fast.Serialize(move));
                if (moveBack.Id != move.Id || moveBack.X != move.X || moveBack.Y != move.Y)
                    return Outcome.Fail($"move round-trip mismatch: {moveBack} != {move}");

                var chat = BenchData.MakeChat();
                var chatBack = fast.Deserialize<BenchChat>(fast.Serialize(chat));
                if (chatBack.Sender != chat.Sender || chatBack.Text != chat.Text)
                    return Outcome.Fail($"chat round-trip mismatch: {chatBack.Sender}/{chatBack.Text} != {chat.Sender}/{chat.Text}");

                return Outcome.Ok();
            });

            Check(results, "one serializer per thread stays independent", () =>
            {
                const int threads = 8;
                const int perThread = 250;
                long errors = 0;

                // Warm the JIT for the load path on this thread first.
                NewRobust().Load<BenchWorld>(worldBytes, 1);

                Parallel.For(0, threads, _ =>
                {
                    var serializer = NewRobust();
                    for (int i = 0; i < perThread; i++)
                    {
                        try
                        {
                            var clone = serializer.Load<BenchWorld>(worldBytes, 1);
                            if (clone.Nodes.Length != world.Nodes.Length)
                                Interlocked.Increment(ref errors);
                        }
                        catch
                        {
                            Interlocked.Increment(ref errors);
                        }
                    }
                });

                if (errors != 0) return Outcome.Fail($"{errors} failed loads across {threads} threads");
                return Outcome.Ok($"0 errors over {threads * perThread} loads");
            });

            return results;
        }

        static CerasRobustSerializer NewRobust() => BenchData.Robust();

        static void Check(List<Result> results, string name, Func<Outcome> body)
        {
            Outcome outcome;
            try
            {
                outcome = body();
            }
            catch (Exception e)
            {
                outcome = Outcome.Fail($"threw {e.GetType().Name}: {e.Message}");
            }
            results.Add(new Result(name, outcome));
        }

        /// <summary>
        /// Compares a loaded graph against the source graph: every value, plus the reference identity
        /// that a robust save exists to preserve.
        /// </summary>
        static Outcome DescribeGraphDiff(BenchWorld expected, BenchWorld actual)
        {
            if (actual == null) return Outcome.Fail("the load returned null");
            if (expected.Label != actual.Label) return Outcome.Fail($"Label '{actual.Label}' != '{expected.Label}'");

            if (actual.Nodes == null || actual.Nodes.Length != expected.Nodes.Length)
                return Outcome.Fail($"Nodes length {actual.Nodes?.Length} != {expected.Nodes.Length}");

            for (int i = 0; i < expected.Nodes.Length; i++)
            {
                var e = expected.Nodes[i];
                var a = actual.Nodes[i];
                if (a.Id != e.Id) return Outcome.Fail($"Nodes[{i}].Id {a.Id} != {e.Id}");
                if (a.Name != e.Name) return Outcome.Fail($"Nodes[{i}].Name '{a.Name}' != '{e.Name}'");
                if (!a.Values.SequenceEqual(e.Values)) return Outcome.Fail($"Nodes[{i}].Values differ");

                // The successor chain is a cycle over the node array, so identity is what has to match,
                // not just field equality.
                int expectedIndex = ReferenceEquals(e.Next, e) ? i : IndexOfNode(expected, e.Next);
                int actualIndex = ReferenceEquals(a.Next, a) ? i : IndexOfNode(actual, a.Next);
                if (expectedIndex != actualIndex) return Outcome.Fail($"Nodes[{i}].Next points at index {actualIndex}, expected {expectedIndex}");
            }

            if (actual.All == null || actual.All.Count != expected.All.Count)
                return Outcome.Fail($"All count {actual.All?.Count} != {expected.All.Count}");
            for (int i = 0; i < expected.All.Count; i++)
                if (!ReferenceEquals(actual.All[i], actual.Nodes[i]))
                    return Outcome.Fail($"All[{i}] is not the same instance as Nodes[{i}]");

            if (actual.Numbers == null || !actual.Numbers.SequenceEqual(expected.Numbers))
                return Outcome.Fail("Numbers differ");
            if (actual.Tags == null || !actual.Tags.SequenceEqual(expected.Tags))
                return Outcome.Fail("Tags differ");

            if (actual.Secret == null) return Outcome.Fail("Secret is null");
            if (actual.Secret.Item == null) return Outcome.Fail("Secret.Item is null");
            if (actual.Secret.Item.A != 1.5f || actual.Secret.Item.B != 2.5 || actual.Secret.Item.S != "secret")
                return Outcome.Fail("Secret.Item differs");

            return Outcome.Ok($"{expected.Nodes.Length} nodes, shared + cyclic");
        }

        static int IndexOfNode(BenchWorld world, BenchNode node)
        {
            for (int i = 0; i < world.Nodes.Length; i++)
                if (ReferenceEquals(world.Nodes[i], node)) return i;
            return -1;
        }
    }
}
