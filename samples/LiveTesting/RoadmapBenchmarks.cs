using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Ceras;
using Ceras.Generated;

namespace LiveTesting
{
    // ==================================================================
    // Roadmap Phase 1.3 baselines: separate robust / network workloads.
    // Robust = CerasRobustSerializer (persistent format, schema, refs).
    // Network = CerasFastSerializer (transient, no schema/ref tables).
    // ==================================================================

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
                Tags = Enumerable.Range(0, 16).Select(i => "tag" + i).ToList(),
                Secret = new BenchPrivateGeneric<BenchPayload>(new BenchPayload { A = 1.5f, B = 2.5, S = "secret" }),
                Label = "world"
            };
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
    }

    public static class RoadmapBenchmarks
    {
        /// <summary>
        /// Lightweight, deterministic measurement (no BenchmarkDotNet harness) used for CI / quick
        /// verification. It reports complete Save/Load, isolated encode/decode, allocations, output
        /// size, cold initialization and steady state for the robust and network workloads.
        /// </summary>
        public static void RunQuick()
        {
            const int iterations = 2000;

            var robust = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
            var world = BenchData.MakeWorld();
            var robustBytes = robust.Save(world, 1);

            Console.WriteLine("=== Roadmap quick baselines ===");
            Console.WriteLine($"Robust payload size: {robustBytes.Length} bytes");

            Measure("Robust Save (fresh graph)", iterations, () => robust.Save(BenchData.MakeWorld(), 1));
            Measure("Robust Load (complete)", iterations, () => robust.Load<BenchWorld>(robustBytes, 1));

            var fast = new CerasFastSerializer(c => c.UseGeneratedFormatters("Tests"));
            var move = new BenchMove { Id = 7, X = 1.5f, Y = -2.5f };
            var chat = new BenchChat { Sender = 3, Text = "hello world" };
            var moveBytes = fast.Serialize(move);
            var chatBytes = fast.Serialize(chat);
            Console.WriteLine($"Network struct size: {moveBytes.Length} bytes");
            Console.WriteLine($"Network chat size:   {chatBytes.Length} bytes");

            Measure("Network Encode small struct", iterations, () => fast.Serialize(move));
            Measure("Network Decode small struct", iterations, () => fast.Deserialize<BenchMove>(moveBytes));
            Measure("Network Encode chat string", iterations, () => fast.Serialize(chat));
            Measure("Network Decode chat string", iterations, () => fast.Deserialize<BenchChat>(chatBytes));

            Measure("Cold init robust serializer", 50, () => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests")));
            Measure("Cold init fast serializer", 50, () => new CerasFastSerializer(c => c.UseGeneratedFormatters("Tests")));

            // Steady state: reuse one serializer and a pre-built graph over many iterations.
            var steady = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
            var steadyBytes = steady.Save(world, 1);
            Measure("Robust Load steady state", iterations, () => steady.Load<BenchWorld>(steadyBytes, 1));
        }

        static void Measure(string name, int iterations, Action action)
        {
            // Warm up.
            for (int i = 0; i < Math.Min(50, iterations); i++) action();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) action();
            sw.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Console.WriteLine($"{name,-34} {sw.Elapsed.TotalMilliseconds / iterations,8:F4} ms/op  {allocated / (double)iterations,10:F1} B/op");
        }
    }
}
