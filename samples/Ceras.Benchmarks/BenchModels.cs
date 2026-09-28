using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Benchmarks
{
    // ==================================================================================================
    // Roadmap baselines: complete robust Save/Load and isolated payload costs, allocations,
    // bytes, cold setup and steady state, upgrade (interpreter) cost, CRC/schema/string/payload costs,
    // small network messages, and the game's per-operation serializer construction versus reuse.
    //
    // These models live in their own generator-enabled compilation (see the .csproj) so they get a real
    // generated manifest for this project's own context, without colliding with Ceras.Test's generated
    // Ceras.Generated.CerasConfigExtensions / RobustFormatters (two generator-enabled assemblies
    // referenced from one compilation would produce ambiguous extension methods).
    // ==================================================================================================

    /// <summary>
    /// The serialization context every benchmark model below belongs to. It is named after this
    /// project rather than reusing Ceras.Test's "Tests", so a context name appearing in a result or
    /// in a failure message always identifies which compilation produced the manifest.
    /// </summary>
    public static class BenchContext
    {
        public const string Name = "CerasBenchmarks";
    }

    [CerasContext(BenchContext.Name)]
    [CerasSerializable]
    public class BenchNode
    {
        public int Id;
        public string Name;
        public BenchNode Next;
        public List<int> Values = new();
    }

    [CerasContext(BenchContext.Name)]
    [CerasSerializable]
    public class BenchPayload
    {
        public float A;
        public double B;
        public string S;
    }

    // Private generic member: exercises generated generic accessors on the hot path.
    [CerasContext(BenchContext.Name)]
    [CerasSerializable]
    public class BenchPrivateGeneric<T>
    {
        private T _item;
        public BenchPrivateGeneric(T item) => _item = item;
        public T Item => _item;
    }

    [CerasContext(BenchContext.Name)]
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

    [CerasContext(BenchContext.Name)]
    [CerasSerializable]
    public struct BenchMove
    {
        public int Id;
        public float X;
        public float Y;
    }

    [CerasContext(BenchContext.Name)]
    [CerasSerializable]
    public class BenchChat
    {
        public int Sender;
        public string Text;
    }

    // Isolated primitive/collection payloads.
    [CerasContext(BenchContext.Name)]
    [CerasSerializable]
    public class BenchIntArray { public int[] Values; }

    [CerasContext(BenchContext.Name)]
    [CerasSerializable]
    public class BenchIntList { public List<int> Values = new(); }

    [CerasContext(BenchContext.Name)]
    [CerasSerializable]
    public class BenchStringList { public List<string> Values = new(); }

    // Deferred deserialization callback: exercises the callback queue on the robust reader.
    [CerasContext(BenchContext.Name)]
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
        /// <summary>A robust serializer bound to this project's generated context.</summary>
        public static CerasRobustSerializer Robust() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(BenchContext.Name));

        /// <summary>A transient (raw) serializer bound to this project's generated context.</summary>
        public static CerasFastSerializer Fast() => new CerasFastSerializer(c => c.UseGeneratedFormatters(BenchContext.Name));

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

        public static BenchChat MakeChat() => new BenchChat { Sender = 3, Text = "hello world" };

        public static BenchMove MakeMove() => new BenchMove { Id = 7, X = 1.5f, Y = -2.5f };

        /// <summary>Spread over the varint width range, so the field count is not 81 one-byte values.</summary>
        public static Wide81 MakeWide81()
        {
            var w = new Wide81();
            w.F0 = 1; w.J9 = 300; w.I19 = 70000; w.H5 = 5; w.G17 = 12;
            return w;
        }

        /// <summary>
        /// Invalidates the stored layout fingerprint (keeping a valid CRC) so the next Load takes the
        /// schema-traversal interpreter path. Used to measure the upgrade cost separately.
        /// <para>
        /// The offset and the checksum are the library's own (<see cref="RobustEnvelope"/>), and the CRC
        /// is recomputed with the library's algorithm, so this keeps working across a format change
        /// instead of only on today's header.
        /// </para>
        /// </summary>
        public static byte[] ForceInterpreter(byte[] data)
        {
            var envelope = RobustEnvelope.Read(data);
            var copy = (byte[])data.Clone();
            copy[envelope.FingerprintOffset] ^= 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(envelope.ChecksumOffset), RobustEnvelope.Checksum(copy));
            return copy;
        }

        /// <summary>
        /// Decodes the payload string-table region exactly as the loader does - including the
        /// bounded Deflate expansion - so the string cost is visible separately from the generated
        /// fast path. This is a real decode, not a scan of the length prefixes.
        /// </summary>
        public static string[] LoadPayloadStringTable(byte[] data, RobustEnvelope envelope)
            => StringTable.Load(data, (int)envelope.StringTableOffset, envelope.StringTableLength(data.Length), envelope.Compression);

        /// <summary>
        /// Decodes the schema string-table region and then the schema section itself, i.e. the metadata
        /// work the migration/interpreter path repeats on every load and the generated fast path skips
        /// entirely. Returned so callers can assert it produced a usable schema.
        /// </summary>
        public static Dictionary<string, OldTypeSchema> ReadSchemaSection(byte[] data, RobustEnvelope envelope)
        {
            var schemaStrings = StringTable.Load(data, (int)envelope.SchemaStringTableOffset,
                envelope.SchemaStringTableLength, StringTableCompression.Disabled);
            int offset = envelope.HeaderSize;
            return SchemaSection.Read(data, ref offset, schemaStrings);
        }
    }


    /// <summary>
    /// Field-count probes. The gap between encoding a 1-field and an 81-field struct is the cost
    /// <em>per field</em>; what the 1-field case costs is the fixed cost of an operation - the
    /// formatter lookup, entering and leaving recursion, and the per-operation caches. Devirtualizing
    /// only removes part of the first of those, so the split is what decides whether it is worth
    /// doing: on a three-field network message the fixed cost is most of the total.
    /// </summary>
    [CerasContext(BenchContext.Name)]
    [CerasSerializable]
    public struct Wide1 { public int F0; }

    [CerasSerializable]
    public struct Wide9 { public int F0, F1, F2, F3, F4, F5, F6, F7, F8; }

    [CerasContext(BenchContext.Name)]

    [CerasContext(BenchContext.Name)]
    [CerasSerializable]
    public struct Wide81
    {
        public int F0, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15, F16, F17, F18, F19;
        public int G0, G1, G2, G3, G4, G5, G6, G7, G8, G9, G10, G11, G12, G13, G14, G15, G16, G17, G18, G19;
        public int H0, H1, H2, H3, H4, H5, H6, H7, H8, H9, H10, H11, H12, H13, H14, H15, H16, H17, H18, H19;
        public int I0, I1, I2, I3, I4, I5, I6, I7, I8, I9, I10, I11, I12, I13, I14, I15, I16, I17, I18, I19;
        public int J0, J1, J2, J3, J4, J5, J6, J7, J8, J9;
}

}
