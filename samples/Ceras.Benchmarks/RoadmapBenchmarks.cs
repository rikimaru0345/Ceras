using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Ceras.Generated;

namespace Ceras.Benchmarks
{
    [MemoryDiagnoser]
    public class RobustSaveLoadBenchmarks
    {
        CerasRobustSerializer _serializer;
        BenchWorld _world;
        byte[] _bytes;

        [GlobalSetup]
        public void Setup()
        {
            _serializer = BenchData.Robust();
            _world = BenchData.MakeWorld();
            _bytes = _serializer.Save(_world, 1);
        }

        /// <summary>
        /// Steady-state save of an unchanged graph: the same row as "--roadmap-quick"'s
        /// "Robust Save (reused graph)", so the two tables are comparable.
        /// <para>
        /// The graph comes from <see cref="Setup"/> and is deliberately *not* rebuilt per iteration.
        /// <c>BenchData.MakeWorld()</c> allocates 64 <see cref="BenchNode"/> objects, each with its own
        /// <c>List&lt;int&gt;</c>, plus <c>All</c>, <c>Tags</c>, a <c>BenchPrivateGeneric</c> and the
        /// two arrays - 66 collections in all, so "the world" is not a handful of allocations. Measured
        /// as the difference between this row and <see cref="Robust_Save_FreshGraph"/>, that
        /// construction adds about 5 us and 13 KB to a save of about 29 us and 25 KB: roughly a fifth of
        /// the time and half the allocation. Building it inside this body therefore reported the cost of
        /// <c>MakeWorld</c> under the serializer's name and made the B/op column mostly graph
        /// construction. Anything that changes the graph between iterations belongs in
        /// <see cref="Robust_Save_FreshGraph"/>, which says so in its name.
        /// </para>
        /// </summary>
        [Benchmark]
        public byte[] Robust_Save() => _serializer.Save(_world, 1);

        /// <summary>
        /// Save plus a full rebuild of the graph, i.e. what a caller that serializes a freshly
        /// produced object graph pays. It is the sum of <see cref="Robust_Save"/> and
        /// <c>BenchData.MakeWorld</c>, not a property of the serializer alone - hence the separate,
        /// explicitly named row.
        /// </summary>
        [Benchmark]
        public byte[] Robust_Save_FreshGraph() => _serializer.Save(BenchData.MakeWorld(), 1);

        [Benchmark]
        public int Robust_PayloadSize() => _bytes.Length;

        [Benchmark]
        public BenchWorld Robust_Load() => _serializer.Load<BenchWorld>(_bytes, 1);

        [Benchmark]
        public BenchWorld Robust_Load_Interpreter() => _serializer.Load<BenchWorld>(BenchData.ForceInterpreter(_bytes), 1);
    }

    [MemoryDiagnoser]
    public class NetworkTransientBenchmarks
    {
        CerasFastSerializer _serializer;
        BenchMove _move = BenchData.MakeMove();
        BenchChat _chat = BenchData.MakeChat();
        byte[] _moveBytes;
        byte[] _chatBytes;

        [GlobalSetup]
        public void Setup()
        {
            _serializer = BenchData.Fast();
            _moveBytes = _serializer.Serialize(_move);
            _chatBytes = _serializer.Serialize(_chat);
        }

        [Benchmark]
        public byte[] Network_EncodeSmallStruct() => _serializer.Serialize(_move);

        [Benchmark]
        public BenchMove Network_DecodeSmallStruct() => _serializer.Deserialize<BenchMove>(_moveBytes);

        [Benchmark]
        public byte[] Network_EncodeChatString() => _serializer.Serialize(_chat);

        [Benchmark]
        public BenchChat Network_DecodeChatString() => _serializer.Deserialize<BenchChat>(_chatBytes);
    }

    [MemoryDiagnoser]
    public class ColdInitBenchmarks
    {
        [Benchmark]
        public CerasRobustSerializer ColdStart_Robust() => BenchData.Robust();

        [Benchmark]
        public CerasFastSerializer ColdStart_Fast() => BenchData.Fast();

        // The game's current pattern: a fresh serializer per save/load operation.
        byte[] _bytes;

        [GlobalSetup]
        public void Setup() => _bytes = BenchData.Robust().Save(BenchData.MakeWorld(), 1);

        [Benchmark]
        public byte[] GamePattern_ConstructAndSave() => BenchData.Robust().Save(BenchData.MakeWorld(), 1);

        [Benchmark]
        public BenchWorld GamePattern_ConstructAndLoad() => BenchData.Robust().Load<BenchWorld>(_bytes, 1);
    }

    public static class RoadmapBenchmarks
    {
        struct Row
        {
            public string Name;
            public double MsPerOp;
            public double BytesPerOp;
            public string Note;
        }

        /// <summary>
        /// Runs the BenchmarkDotNet harness for the roadmap workloads.
        /// <c>dotnet run --project samples/Ceras.Benchmarks -c Release -- --roadmap-bench</c>.
        /// Any further arguments are handed to BenchmarkDotNet itself, so
        /// <c>--roadmap-bench --filter *Load* --job short</c> works.
        /// </summary>
        public static void RunBenchmarkDotNet(string[] args = null)
        {
            BenchmarkSwitcher.FromTypes(new[]
            {
                typeof(RobustSaveLoadBenchmarks),
                typeof(NetworkTransientBenchmarks),
                typeof(ColdInitBenchmarks),
            }).Run(args);
        }

        /// <summary>
        /// Lightweight, deterministic measurement (no BenchmarkDotNet harness) used for CI and quick
        /// verification. It prints complete Save/Load, isolated payload, network, cold-setup, steady
        /// state, upgrade, CRC, schema/string/payload and game-construction numbers.
        /// <para>
        /// The self-checks run first (see <see cref="SelfChecks"/>): the numbers below are only
        /// meaningful if the operations actually behave as described, so a failing check aborts the
        /// run and makes the process exit non-zero.
        /// </para>
        /// <c>dotnet run --project samples/Ceras.Benchmarks -c Release -- --roadmap-quick</c>.
        /// </summary>
        /// <returns>0 when every self-check passed, 1 otherwise.</returns>
        public static int RunQuick()
        {
            var rows = new List<Row>();
            Console.WriteLine("=== Ceras roadmap baselines (P0 0.7) ===");
            Console.WriteLine($".NET        : {Environment.Version}");
            Console.WriteLine($"server GC   : {System.Runtime.GCSettings.IsServerGC}");
            Console.WriteLine($"context     : {BenchContext.Name}");
            Console.WriteLine();

            // ------------------------------------------------------------------
            // Robust context: sizes and header breakdown (schema vs payload, etc.)
            // ------------------------------------------------------------------
            var robust = BenchData.Robust();
            var world = BenchData.MakeWorld();
            byte[] worldBytes = robust.Save(world, 1);
            var envelope = RobustEnvelope.Read(worldBytes);

            PrintHeaderBreakdown("robust BenchWorld (64 nodes, shared+cyclic, private generic)", worldBytes, envelope);

            // Warm the save, load, interpreter, transient and per-section paths before any row is
            // timed. Without this the first rows are the ones paying tiered-JIT promotion and the
            // initial Gen0 growth, which made "Robust Load" look ~4x slower than the identical load
            // measured later in the table.
            WarmUp(worldBytes);

            // Complete robust Save/Load.
            Add(rows, "Robust Save (fresh graph)", 500, () => robust.Save(BenchData.MakeWorld(), 1));
            Add(rows, "Robust Save (reused graph)", 2000, () => robust.Save(world, 1));
            Add(rows, "Robust Load (fresh target)", 2000, () => robust.Load<BenchWorld>(worldBytes, 1));

            var reuseTarget = new BenchWorld();
            Add(rows, "Robust Load (reused target)", 2000, () => robust.Load(ref reuseTarget, worldBytes, 1));

            // Upgrade / migration path: force the interpreter by invalidating the stored fingerprint
            // (same layout, so the traversal engine reproduces the object without field migration).
            byte[] interpreterBytes = BenchData.ForceInterpreter(worldBytes);
            Add(rows, "Robust Load (interpreter/upgrade)", 200, () => robust.Load<BenchWorld>(interpreterBytes, 1));

            // ------------------------------------------------------------------
            // Isolated payloads: primitive array, primitive list, string list (interning),
            // private generic member.
            // ------------------------------------------------------------------
            var ints = new BenchIntArray { Values = BenchData.MakeInts() };
            byte[] intBytes = robust.Save(ints, 1);
            Add(rows, "Save int[256]", 4000, () => robust.Save(ints, 1), $"{intBytes.Length} B");
            Add(rows, "Load int[256]", 4000, () => robust.Load<BenchIntArray>(intBytes, 1), $"{intBytes.Length} B");

            var intList = new BenchIntList { Values = BenchData.MakeIntList() };
            byte[] intListBytes = robust.Save(intList, 1);
            Add(rows, "Save List<int>(256)", 4000, () => robust.Save(intList, 1), $"{intListBytes.Length} B");
            Add(rows, "Load List<int>(256)", 4000, () => robust.Load<BenchIntList>(intListBytes, 1), $"{intListBytes.Length} B");

            var strings = new BenchStringList { Values = BenchData.MakeStrings() };
            byte[] stringBytes = robust.Save(strings, 1);
            Add(rows, "Save List<string>(32, 8 unique)", 4000, () => robust.Save(strings, 1), $"{stringBytes.Length} B interned");
            Add(rows, "Load List<string>(32, 8 unique)", 4000, () => robust.Load<BenchStringList>(stringBytes, 1), $"{stringBytes.Length} B");

            var secret = new BenchPrivateGeneric<BenchPayload>(new BenchPayload { A = 1.5f, B = 2.5, S = "secret" });
            byte[] secretBytes = robust.Save(secret, 1);
            Add(rows, "Save private generic member", 8000, () => robust.Save(secret, 1));
            Add(rows, "Load private generic member", 8000, () => robust.Load<BenchPrivateGeneric<BenchPayload>>(secretBytes, 1));

            // ------------------------------------------------------------------
            // Small network messages (transient fast serializer, generated context).
            // ------------------------------------------------------------------
            var fast = BenchData.Fast();
            var move = BenchData.MakeMove();
            var chat = BenchData.MakeChat();
            byte[] moveBytes = fast.Serialize(move);
            byte[] chatBytes = fast.Serialize(chat);
            Add(rows, "Network encode small struct", 200000, () => fast.Serialize(move), $"{moveBytes.Length} B");
            Add(rows, "Network decode small struct", 200000, () => fast.Deserialize<BenchMove>(moveBytes), $"{moveBytes.Length} B");
            Add(rows, "Network encode chat string", 200000, () => fast.Serialize(chat), $"{chatBytes.Length} B");
            Add(rows, "Network decode chat string", 200000, () => fast.Deserialize<BenchChat>(chatBytes), $"{chatBytes.Length} B");

            // The pooled write buffer, which is what a real send loop uses: one rented buffer, a
            // message per call, nothing per message. This is the row that says whether the write path
            // allocates at all - the convenience overload above necessarily allocates its result, so
            // its B/op can never be zero however the write path behaves.
            var pooledBuffer = CerasBufferPool.Rent(64 * 1024);
            var pooledOffset = 0;
            fast.Serialize(move, ref pooledBuffer, ref pooledOffset);

            Action encodePooledMove = () => { pooledOffset = 0; fast.Serialize(move, ref pooledBuffer, ref pooledOffset); };
            Action encodePooledChat = () => { pooledOffset = 0; fast.Serialize(chat, ref pooledBuffer, ref pooledOffset); };
            Add(rows, "Network encode pooled (write only)", 200000, encodePooledMove, $"{moveBytes.Length} B, no result array");
            Add(rows, "Network encode pooled chat", 200000, encodePooledChat, $"{chatBytes.Length} B, no result array");
            CerasBufferPool.Return(pooledBuffer);

            // The same encode at two field counts, so the per-field cost and the fixed cost of an
            // operation are both visible. Subtracting these two rows gives the per-field cost; the
            // 1-field row is what a message costs before any field is written.
            var wide1 = new Wide1 { F0 = 1 };
            var wide81 = BenchData.MakeWide81();
            Action encodeWide1 = () => { pooledOffset = 0; fast.Serialize(wide1, ref pooledBuffer, ref pooledOffset); };
            Action encodeWide81 = () => { pooledOffset = 0; fast.Serialize(wide81, ref pooledBuffer, ref pooledOffset); };
            var wideBuffer = CerasBufferPool.Rent(64 * 1024);
            fast.Serialize(wide1, ref wideBuffer, ref pooledOffset);
            pooledOffset = 0;
            Add(rows, "Network encode pooled 1 field", 200000, encodeWide1, "fixed cost of an operation, ~no fields");
            Add(rows, "Network encode pooled 81 fields", 60000, encodeWide81, "fixed cost + 80 more fields");
            CerasBufferPool.Return(wideBuffer);

            // ------------------------------------------------------------------
            // Cold setup vs steady state, and the game's per-operation construction.
            // ------------------------------------------------------------------
            Add(rows, "Cold build robust serializer", 200, () => BenchData.Robust());
            Add(rows, "Cold build fast serializer", 200, () => BenchData.Fast());
            Add(rows, "Cold first Save (Seal/Bake)", 200, () => BenchData.Robust().Save(world, 1));
            Add(rows, "Game pattern: new serializer + Save", 200, () => BenchData.Robust().Save(world, 1));
            Add(rows, "Game pattern: new serializer + Load", 200, () => BenchData.Robust().Load<BenchWorld>(worldBytes, 1));

            // ------------------------------------------------------------------
            // String-table compression: bytes and timing with Deflate vs Disabled.
            // ------------------------------------------------------------------
            var compressionBytes = new Dictionary<StringTableCompression, int>();
            foreach (var compression in new[] { StringTableCompression.Deflate, StringTableCompression.Disabled })
            {
                var comp = compression;
                var ser = new CerasRobustSerializer(c => { c.UseGeneratedFormatters(BenchContext.Name); c.StringTableCompression = comp; });
                byte[] bytes = ser.Save(world, 1);
                compressionBytes[comp] = bytes.Length;
                Add(rows, $"Save world [{comp}]", 1000, () => ser.Save(world, 1), $"{bytes.Length} B");
                Add(rows, $"Load world [{comp}]", 1000, () => ser.Load<BenchWorld>(bytes, 1), $"{bytes.Length} B");
            }

            long deflated = compressionBytes[StringTableCompression.Deflate];
            long uncompressed = compressionBytes[StringTableCompression.Disabled];
            Console.WriteLine($"String-table compression: Deflate {deflated} B vs Disabled {uncompressed} B for the same save " +
                              $"({100.0 * (uncompressed - deflated) / uncompressed:F1}% smaller; the cost is in the two rows above).");
            Console.WriteLine();

            // ------------------------------------------------------------------
            // CRC cost as a proxy: the header checksum spans the whole buffer and is verified on every
            // load, so the loader pays this once per load. Timed with the library's own implementation
            // (RobustEnvelope.Checksum) rather than a re-implementation that could drift from it.
            // ------------------------------------------------------------------
            Add(rows, "CRC32 over save buffer (loader step)", 5000,
                () => RobustEnvelope.Checksum(worldBytes),
                $"{worldBytes.Length} B");

            // ------------------------------------------------------------------
            // Explicit component costs. Each of these is the *actual* code the corresponding step
            // runs, called directly on a real save, so the cost can be attributed instead of guessed:
            //  * payload copy: the loader materializes the data section as its own byte[] before
            //    decoding, and the saver copies the encoded payload into the output buffer.
            //  * schema bake/copy: the schema bytes are baked once, then copied into every save and
            //    present in every file; the per-save copy is measured here.
            //  * schema parse: StringTable.Load of the schema region + SchemaSection.Read, which the
            //    interpreter pass repeats on every load and the generated fast path skips entirely.
            //  * payload string table: the real decode, including the bounded Deflate expansion.
            // ------------------------------------------------------------------
            int dataOffset = (int)envelope.DataSectionOffset;
            int payloadLength = envelope.DataLength;
            byte[] payloadCopy = new byte[payloadLength];

            Add(rows, "Copy payload section", 20000,
                () => Array.Copy(worldBytes, dataOffset, payloadCopy, 0, payloadLength),
                $"{payloadLength} B per save");
            Add(rows, "Copy schema section", 20000,
                () => Array.Copy(worldBytes, envelope.HeaderSize, payloadCopy, 0, Math.Min(payloadLength, envelope.HeaderLength)),
                $"{envelope.HeaderLength} B per save");

            Add(rows, "Decode payload string table", 20000,
                () => BenchData.LoadPayloadStringTable(worldBytes, envelope),
                $"{envelope.StringTableLength(worldBytes.Length)} B, {envelope.Compression}");
            Add(rows, "Parse schema section (interp only)", 2000,
                () => BenchData.ReadSchemaSection(worldBytes, envelope),
                $"{envelope.HeaderLength} B schema + {envelope.SchemaStringTableLength} B names");

            // ------------------------------------------------------------------
            // Concurrency: a configured instance owns mutable per-operation state, so safe reuse is
            // one instance per thread (or external locking), not one shared instance.
            // ------------------------------------------------------------------
            PrintConcurrency(worldBytes);

            Print(rows);
            Console.WriteLine();
            Console.WriteLine("Caveats: absolute values are specific to this machine, runtime and GC mode; the");
            Console.WriteLine("iteration counts are small, so a row is an order-of-magnitude figure rather than");
            Console.WriteLine("a statistically tight one, and B/op counts only allocations on the measured thread.");
            return 0;
        }

        /// <summary>
        /// Exercises every path the table times before the first row is recorded, so tiered-JIT
        /// promotion and initial GC growth land in the warm-up instead of in whichever row happens to
        /// be printed first.
        /// </summary>
        static void WarmUp(byte[] worldBytes)
        {
            const int rounds = 2000;
            var envelope = RobustEnvelope.Read(worldBytes);
            byte[] interpreterBytes = BenchData.ForceInterpreter(worldBytes);

            var robust = BenchData.Robust();
            var fast = BenchData.Fast();
            var world = BenchData.MakeWorld();
            var move = BenchData.MakeMove();
            var chat = BenchData.MakeChat();
            byte[] moveBytes = fast.Serialize(move);
            byte[] chatBytes = fast.Serialize(chat);
            var payloadCopy = new byte[Math.Max(1, envelope.DataLength)];

            for (int i = 0; i < rounds; i++)
            {
                byte[] bytes = robust.Save(world, 1);
                robust.Load<BenchWorld>(bytes, 1);
                robust.Load<BenchWorld>(interpreterBytes, 1);
                fast.Serialize(move);
                fast.Deserialize<BenchMove>(moveBytes);
                fast.Serialize(chat);
                fast.Deserialize<BenchChat>(chatBytes);
                Array.Copy(worldBytes, (int)envelope.DataSectionOffset, payloadCopy, 0, envelope.DataLength);
                RobustEnvelope.Checksum(worldBytes);
            }

            // The rare, expensive paths are warmed separately so they are not dominated by first-call cost.
            for (int i = 0; i < 100; i++)
            {
                BenchData.LoadPayloadStringTable(worldBytes, envelope);
                BenchData.ReadSchemaSection(worldBytes, envelope);
                BenchData.Robust();
                BenchData.Fast();
            }
        }

        static void Add(List<Row> rows, string name, int iterations, Action action, string note = null)
        {
            rows.Add(Measure(name, iterations, action, note));
        }

        static Row Measure(string name, int iterations, Action action, string note)
        {
            for (int i = 0; i < Math.Min(50, iterations); i++) action();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) action();
            sw.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            return new Row
            {
                Name = name,
                MsPerOp = sw.Elapsed.TotalMilliseconds / iterations,
                BytesPerOp = allocated / (double)iterations,
                Note = note,
            };
        }

        static void PrintHeaderBreakdown(string label, byte[] data, RobustEnvelope envelope)
        {
            Console.WriteLine($"{label}: {data.Length} B total");
            Console.WriteLine($"  header                 : {envelope.HeaderSize} B");
            Console.WriteLine($"  schema bytes           : {envelope.HeaderLength} B (copied into every save)");
            Console.WriteLine($"  schema string table    : {envelope.SchemaStringTableLength} B");
            Console.WriteLine($"  payload data           : {envelope.DataLength} B");
            Console.WriteLine($"  payload string table   : {envelope.StringTableLength(data.Length)} B ({envelope.Compression})");
            Console.WriteLine($"  layout fingerprint     : 0x{envelope.Fingerprint:X16}");
            Console.WriteLine();
        }

        static void PrintConcurrency(byte[] worldBytes)
        {
            const int threads = 8;
            const int perThread = 2000;

            // Still warm up the JIT for the load path on this thread.
            BenchData.Robust().Load<BenchWorld>(worldBytes, 1);

            var sw = Stopwatch.StartNew();
            long errors = 0;
            Parallel.For(0, threads, _ =>
            {
                var serializer = BenchData.Robust();
                for (int i = 0; i < perThread; i++)
                {
                    try { serializer.Load<BenchWorld>(worldBytes, 1); }
                    catch { Interlocked.Increment(ref errors); }
                }
            });
            sw.Stop();

            double totalOps = threads * perThread;
            Console.WriteLine($"Concurrency: {threads} threads x {perThread} loads, one serializer per thread");
            Console.WriteLine($"  {sw.Elapsed.TotalMilliseconds / totalOps:F4} ms/op aggregate, errors={errors}");
            Console.WriteLine("  (a single configured serializer is not safe for concurrent use; it owns mutable");
            Console.WriteLine("   per-operation state. Reuse one instance per thread or lock externally.)");
            Console.WriteLine();
        }

        static void Print(List<Row> rows)
        {
            // Five decimals: the per-section proxies are tens of microseconds, which "0.0000" would hide.
            Console.WriteLine($"{"workload",-38} {"ms/op",10} {"B/op",12}  note");
            Console.WriteLine(new string('-', 84));
            foreach (var row in rows)
                Console.WriteLine($"{row.Name,-38} {row.MsPerOp,10:F5} {row.BytesPerOp,12:F1}  {row.Note}");
        }
    }
}
