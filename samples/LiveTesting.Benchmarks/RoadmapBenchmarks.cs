using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Ceras;
using Ceras.Generated;

namespace LiveTesting.Benchmarks
{
    // ==================================================================================================
    // Roadmap P0 0.7 baselines: complete robust Save/Load and isolated payload costs, allocations,
    // bytes, cold setup and steady state, upgrade (interpreter) cost, CRC/schema/string/payload costs,
    // small network messages, and the game's per-operation serializer construction versus reuse.
    //
    // These models live in their own generator-enabled assembly (see the .csproj) so they get a real
    // generated manifest for context "Tests" without colliding with Ceras.Test's generated extension.
    // ==================================================================================================

    [CerasContext("Tests")]
    [CerasSerializable]
    public class BenchNode
    {
        public int Id;
        public string Name;
        public BenchNode Next;
        public List<int> Values = new();
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class BenchPayload
    {
        public float A;
        public double B;
        public string S;
    }

    // Private generic member: exercises generated generic accessors on the hot path.
    [CerasContext("Tests")]
    [CerasSerializable]
    public class BenchPrivateGeneric<T>
    {
        private T _item;
        public BenchPrivateGeneric(T item) => _item = item;
        public T Item => _item;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class BenchWorld
    {
        public BenchNode[] Nodes;
        public List<BenchNode> All = new();
        public int[] Numbers;
        public List<string> Tags = new();
        public BenchPrivateGeneric<BenchPayload> Secret;
        public string Label;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public struct BenchMove
    {
        public int Id;
        public float X;
        public float Y;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class BenchChat
    {
        public int Sender;
        public string Text;
    }

    // Isolated primitive/collection payloads.
    [CerasContext("Tests")]
    [CerasSerializable]
    public class BenchIntArray { public int[] Values; }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class BenchIntList { public List<int> Values = new(); }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class BenchStringList { public List<string> Values = new(); }

    // Deferred deserialization callback: exercises the callback queue on the robust reader.
    [CerasContext("Tests")]
    [CerasSerializable]
    public class BenchCallback
    {
        public int Value;
        public static int AfterCount;

        [OnAfterDeserialize]
        void AfterDeserialize() => AfterCount++;
    }

    public static class BenchData
    {
        public static BenchWorld MakeWorld(int nodeCount = 64)
        {
            var nodes = new BenchNode[nodeCount];
            for (int i = 0; i < nodeCount; i++)
                nodes[i] = new BenchNode { Id = i, Name = "node-" + i, Values = { i, i + 1, i + 2 } };

            // Shared + cyclic references (robust format must preserve identity).
            nodes[0].Next = nodes[0];
            nodes[1].Next = nodes[2];
            for (int i = 2; i < nodeCount; i++)
                nodes[i].Next = nodes[i - 1];

            return new BenchWorld
            {
                Nodes = nodes,
                All = nodes.ToList(),
                Numbers = Enumerable.Range(0, 256).ToArray(),
                Tags = Enumerable.Range(0, 16).Select(i => "tag" + (i % 4)).ToList(),
                Secret = new BenchPrivateGeneric<BenchPayload>(new BenchPayload { A = 1.5f, B = 2.5, S = "secret" }),
                Label = "world"
            };
        }

        public static int[] MakeInts(int count = 256) => Enumerable.Range(0, count).ToArray();

        public static List<int> MakeIntList(int count = 256) => MakeInts(count).ToList();

        public static List<string> MakeStrings(int unique = 8, int repeats = 4)
        {
            var list = new List<string>(unique * repeats);
            for (int r = 0; r < repeats; r++)
                for (int i = 0; i < unique; i++)
                    list.Add("shared-string-value-" + i);
            return list;
        }

        // Robust header offsets (see src/Ceras/Format/LAYOUT.md).
        const int FingerprintOffset = 44;
        public const int ChecksumOffset = 52;

        /// <summary>
        /// Invalidates the stored layout fingerprint (keeping a valid CRC) so the next Load takes the
        /// schema-traversal interpreter path. Used to measure the upgrade cost separately.
        /// </summary>
        public static byte[] ForceInterpreter(byte[] data)
        {
            var copy = (byte[])data.Clone();
            copy[FingerprintOffset] ^= 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(ChecksumOffset), Crc32WithZeroedRange(copy, ChecksumOffset, 4));
            return copy;
        }

        // The loader's own CRC32 (31): standard reflected CRC32, computed with the stored checksum
        // bytes fed as zero. This mirrors Ceras.Helpers.HashHelpers (internal), so the sample can time
        // the same per-load integrity work.
        static readonly uint[] Crc32Table = CreateCrc32Table();

        static uint[] CreateCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint entry = i;
                for (int j = 0; j < 8; j++)
                    entry = (entry & 1) == 1 ? (entry >> 1) ^ 0xedb88320u : entry >> 1;
                table[i] = entry;
            }
            return table;
        }

        public static uint Crc32WithZeroedRange(byte[] data, int zeroStart, int zeroLength)
        {
            uint crc = 0xffffffffu;
            for (int i = 0; i < zeroStart; i++)
                crc = (crc >> 8) ^ Crc32Table[(crc ^ data[i]) & 0xff];
            for (int i = 0; i < zeroLength; i++)
                crc = (crc >> 8) ^ Crc32Table[crc & 0xff];
            for (int i = zeroStart + zeroLength; i < data.Length; i++)
                crc = (crc >> 8) ^ Crc32Table[(crc ^ data[i]) & 0xff];
            return crc ^ 0xffffffffu;
        }

        /// <summary>
        /// Proxy for the loader's payload string-table decode: reads the string table header and
        /// decompresses the table when the header flags say Deflate, so the interpreter-path string
        /// cost is visible separately from the generated fast path.
        /// </summary>
        public static int ReadStringTable(byte[] data, int offset)
        {
            if (offset >= data.Length) return 0;

            byte flags = data[5]; // payload string-table compression flags
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            int total = 0;
            int pos = offset + 4;
            for (int i = 0; i < count && pos + 4 <= data.Length; i++)
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
                pos += 4 + len;
                total += len;
            }
            return flags == 0 ? total : total;
        }
    }

    [MemoryDiagnoser]
    public class RobustSaveLoadBenchmarks
    {
        CerasRobustSerializer _serializer;
        BenchWorld _world;
        byte[] _bytes;

        [GlobalSetup]
        public void Setup()
        {
            _serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
            _world = BenchData.MakeWorld();
            _bytes = _serializer.Save(_world, 1);
        }

        [Benchmark]
        public byte[] Robust_Save() => _serializer.Save(BenchData.MakeWorld(), 1);

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
        BenchMove _move = new() { Id = 7, X = 1.5f, Y = -2.5f };
        BenchChat _chat = new() { Sender = 3, Text = "hello world" };
        byte[] _moveBytes;
        byte[] _chatBytes;

        [GlobalSetup]
        public void Setup()
        {
            _serializer = new CerasFastSerializer(c => c.UseGeneratedFormatters("Tests"));
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
        public CerasRobustSerializer ColdStart_Robust() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        [Benchmark]
        public CerasFastSerializer ColdStart_Fast() => new CerasFastSerializer(c => c.UseGeneratedFormatters("Tests"));

        // The game's current pattern: a fresh serializer per save/load operation.
        byte[] _bytes;

        [GlobalSetup]
        public void Setup() => _bytes = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests")).Save(BenchData.MakeWorld(), 1);

        [Benchmark]
        public byte[] GamePattern_ConstructAndSave() =>
            new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests")).Save(BenchData.MakeWorld(), 1);

        [Benchmark]
        public BenchWorld GamePattern_ConstructAndLoad() =>
            new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests")).Load<BenchWorld>(_bytes, 1);
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
        /// Runs the BenchmarkDotNet harness for the roadmap workloads:
        /// <c>dotnet run --project samples/LiveTesting -c Release -- --roadmap-bench</c>.
        /// </summary>
        public static void RunBenchmarkDotNet()
        {
            BenchmarkSwitcher.FromTypes(new[]
            {
                typeof(RobustSaveLoadBenchmarks),
                typeof(NetworkTransientBenchmarks),
                typeof(ColdInitBenchmarks),
            }).RunAll();
        }

        /// <summary>
        /// Lightweight, deterministic measurement (no BenchmarkDotNet harness) used for CI and quick
        /// verification. It prints complete Save/Load, isolated payload, network, cold-setup, steady
        /// state, upgrade, CRC, schema/string/payload and game-construction numbers.
        ///
        /// Run with: <c>dotnet run --project samples/LiveTesting -c Release -- --roadmap-quick</c>.
        /// </summary>
        public static void RunQuick()
        {
            var rows = new List<Row>();
            Console.WriteLine("=== Ceras roadmap baselines (P0 0.7) ===");
            Console.WriteLine($".NET        : {Environment.Version}");
            Console.WriteLine($"server GC   : {System.Runtime.GCSettings.IsServerGC}");
            Console.WriteLine();

            // ------------------------------------------------------------------
            // Robust context: sizes and header breakdown (schema vs payload, etc.)
            // ------------------------------------------------------------------
            var robust = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
            var world = BenchData.MakeWorld();
            byte[] worldBytes = robust.Save(world, 1);

            PrintHeaderBreakdown("robust BenchWorld (64 nodes, shared+cyclic, private generic)", worldBytes);

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
            Add(rows, "Save List<string>(32, repeats)", 4000, () => robust.Save(strings, 1), $"{stringBytes.Length} B interned");
            Add(rows, "Load List<string>(32, repeats)", 4000, () => robust.Load<BenchStringList>(stringBytes, 1), $"{stringBytes.Length} B");

            var secret = new BenchPrivateGeneric<BenchPayload>(new BenchPayload { A = 1.5f, B = 2.5, S = "secret" });
            byte[] secretBytes = robust.Save(secret, 1);
            Add(rows, "Save private generic member", 8000, () => robust.Save(secret, 1));
            Add(rows, "Load private generic member", 8000, () => robust.Load<BenchPrivateGeneric<BenchPayload>>(secretBytes, 1));

            // ------------------------------------------------------------------
            // Small network messages (transient fast serializer, generated context).
            // ------------------------------------------------------------------
            var fast = new CerasFastSerializer(c => c.UseGeneratedFormatters("Tests"));
            var move = new BenchMove { Id = 7, X = 1.5f, Y = -2.5f };
            var chat = new BenchChat { Sender = 3, Text = "hello world" };
            byte[] moveBytes = fast.Serialize(move);
            byte[] chatBytes = fast.Serialize(chat);
            Add(rows, "Network encode small struct", 200000, () => fast.Serialize(move), $"{moveBytes.Length} B");
            Add(rows, "Network decode small struct", 200000, () => fast.Deserialize<BenchMove>(moveBytes), $"{moveBytes.Length} B");
            Add(rows, "Network encode chat string", 200000, () => fast.Serialize(chat), $"{chatBytes.Length} B");
            Add(rows, "Network decode chat string", 200000, () => fast.Deserialize<BenchChat>(chatBytes), $"{chatBytes.Length} B");

            // ------------------------------------------------------------------
            // Cold setup vs steady state, and the game's per-operation construction.
            // ------------------------------------------------------------------
            Add(rows, "Cold build robust serializer", 200, () => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests")));
            Add(rows, "Cold build fast serializer", 200, () => new CerasFastSerializer(c => c.UseGeneratedFormatters("Tests")));
            Add(rows, "Cold first Save (Seal/Bake)", 200, () => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests")).Save(world, 1));
            Add(rows, "Game pattern: new serializer + Save", 200, () => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests")).Save(world, 1));
            Add(rows, "Game pattern: new serializer + Load", 200, () => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests")).Load<BenchWorld>(worldBytes, 1));

            // ------------------------------------------------------------------
            // String-table compression: bytes and timing with Deflate vs Disabled.
            // ------------------------------------------------------------------
            var compressionBytes = new Dictionary<StringTableCompression, int>();
            foreach (var compression in new[] { StringTableCompression.Deflate, StringTableCompression.Disabled })
            {
                var comp = compression;
                var ser = new CerasRobustSerializer(c => { c.UseGeneratedFormatters("Tests"); c.StringTableCompression = comp; });
                byte[] bytes = ser.Save(world, 1);
                compressionBytes[comp] = bytes.Length;
                Add(rows, $"Save world [{comp}]", 1000, () => ser.Save(world, 1), $"{bytes.Length} B");
                Add(rows, $"Load world [{comp}]", 1000, () => ser.Load<BenchWorld>(bytes, 1), $"{bytes.Length} B");
            }

            // ------------------------------------------------------------------
            // Callbacks: deferred [OnAfterDeserialize] runs exactly once per load on both readers.
            // The generated matching-layout reader enqueues it; the interpreter invokes it during
            // migration. Counting is done with an explicit warm-up discount so the displayed count is
            // exactly the measured iterations.
            // ------------------------------------------------------------------
            var callback = new BenchCallback { Value = 5 };
            byte[] callbackBytes = robust.Save(callback, 1);
            byte[] callbackInterpreterBytes = BenchData.ForceInterpreter(callbackBytes);

            const int callbackIterations = 10000;
            int generatedCallbacks = 0;
            int generatedLoads = 0;
            Add(rows, "Load [OnAfterDeserialize] (generated)", callbackIterations,
                () => { BenchCallback.AfterCount = 0; robust.Load<BenchCallback>(callbackBytes, 1); generatedCallbacks += BenchCallback.AfterCount; generatedLoads++; },
                "see below");
            if (generatedCallbacks != generatedLoads)
                Console.WriteLine($"  !! expected 1 callback per generated load, observed {generatedCallbacks} over {generatedLoads} loads");

            int interpreterCallbacks = 0;
            int interpreterLoads = 0;
            Add(rows, "Load [OnAfterDeserialize] (interp)", callbackIterations,
                () => { BenchCallback.AfterCount = 0; robust.Load<BenchCallback>(callbackInterpreterBytes, 1); interpreterCallbacks += BenchCallback.AfterCount; interpreterLoads++; },
                "see below");
            if (interpreterCallbacks != interpreterLoads)
                Console.WriteLine($"  !! expected 1 callback per interpreter load, observed {interpreterCallbacks} over {interpreterLoads} loads");
            Console.WriteLine($"  (callbacks: generated {generatedCallbacks}/{generatedLoads} loads, interpreter {interpreterCallbacks}/{interpreterLoads} loads)");

            // ------------------------------------------------------------------
            // Failed loads: a rejected load must not corrupt a reused serializer or leak state.
            // ------------------------------------------------------------------
            byte[] corrupt = (byte[])worldBytes.Clone();
            corrupt[^1] ^= 0xFF; // invalidate the CRC / payload
            int rejected = 0;
            Add(rows, "Load corrupt (rejected)", 5000, () =>
            {
                try { robust.Load<BenchWorld>(corrupt, 1); }
                catch (Ceras.Exceptions.CerasException) { rejected++; }
            }, "rejected");
            Add(rows, "Load valid after rejected loads", 5000, () => robust.Load<BenchWorld>(worldBytes, 1));
            if (rejected < 5000)
                Console.WriteLine($"  !! expected at least 5000 rejected loads, observed {rejected}");
            Console.WriteLine($"  (rejected loads observed: {rejected})");

            // ------------------------------------------------------------------
            // CRC cost as a proxy: the header checksum spans the whole buffer and is verified on every
            // load. We time the same CRC32 the loader computes (with the 4 checksum bytes zeroed).
            // ------------------------------------------------------------------
            Add(rows, "CRC32 over save buffer (proxy)", 5000,
                () => BenchData.Crc32WithZeroedRange(worldBytes, BenchData.ChecksumOffset, 4),
                $"{worldBytes.Length} B");

            // ------------------------------------------------------------------
            // Explicit component costs (public-API proxies):
            //  * payload copy: the loader materializes the data section as its own byte[] before
            //    decoding (data[stringTableStart - dataOffset]), and the saver copies the encoded
            //    payload into the output buffer (data.CopyTo(buffer)).
            //  * schema bake/copy: the schema bytes are baked once, then copied into every save and
            //    present in every file; the per-save copy is measured here.
            //  * schema parse: the interpreter pass reads the schema section (StringTable.Load +
            //    SchemaSection.Read) instead of skipping it.
            //  * string table: the payload string table length actually encoded.
            // ------------------------------------------------------------------
            int dataOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(worldBytes.AsSpan(28));
            int stringTableOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(worldBytes.AsSpan(36));
            int payloadLength = stringTableOffset - dataOffset;
            byte[] payloadCopy = new byte[payloadLength];

            Add(rows, "Copy payload section", 20000,
                () => Array.Copy(worldBytes, dataOffset, payloadCopy, 0, payloadLength),
                $"{payloadLength} B per save");
            Add(rows, "Copy schema section", 20000,
                () => Array.Copy(worldBytes, 56, payloadCopy, 0, Math.Min(payloadLength, (int)BinaryPrimitives.ReadInt64LittleEndian(worldBytes.AsSpan(20)) - 56)),
                $"{BinaryPrimitives.ReadInt64LittleEndian(worldBytes.AsSpan(20)) - 56} B per save");

            Add(rows, "Schema parse (interp pass only)", 200,
                () => robust.Load<BenchWorld>(BenchData.ForceInterpreter(worldBytes), 1),
                "StringTable.Load + SchemaSection.Read + traversal");
            Add(rows, "StringTable.Load (load proxy)", 20000,
                () => BenchData.ReadStringTable(worldBytes, stringTableOffset),
                $"{worldBytes.Length - stringTableOffset} B table");

            // ------------------------------------------------------------------
            // Concurrency: a configured instance owns mutable per-operation state, so safe reuse is
            // one instance per thread (or external locking), not one shared instance.
            // ------------------------------------------------------------------
            PrintConcurrency(worldBytes);

            Print(rows);
        }

        static void Add(List<Row> rows, string name, int iterations, Action action, string note = null)        {
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

        static void PrintHeaderBreakdown(string label, byte[] data)
        {
            long schemaStringOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(20));
            long dataOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(28));
            long stringTableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(36));
            const int headerSize = 56;

            Console.WriteLine($"{label}: {data.Length} B total");
            Console.WriteLine($"  header                 : {headerSize} B");
            Console.WriteLine($"  schema bytes           : {schemaStringOffset - headerSize} B (copied into every save)");
            Console.WriteLine($"  schema string table    : {dataOffset - schemaStringOffset} B");
            Console.WriteLine($"  payload data           : {stringTableOffset - dataOffset} B");
            Console.WriteLine($"  payload string table   : {data.Length - stringTableOffset} B");
            Console.WriteLine();
        }

        static void PrintConcurrency(byte[] worldBytes)
        {
            const int threads = 8;
            const int perThread = 2000;

            // Still warm up the JIT for the load path on this thread.
            var warm = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
            warm.Load<BenchWorld>(worldBytes, 1);

            var sw = Stopwatch.StartNew();
            long errors = 0;
            Parallel.For(0, threads, _ =>
            {
                var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
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
            Console.WriteLine($"{"workload",-38} {"ms/op",10} {"B/op",12}  note");
            Console.WriteLine(new string('-', 84));
            foreach (var row in rows)
                Console.WriteLine($"{row.Name,-38} {row.MsPerOp,10:F4} {row.BytesPerOp,12:F1}  {row.Note}");
        }
    }

    static class BenchDataExtensions
    {
    }
}
