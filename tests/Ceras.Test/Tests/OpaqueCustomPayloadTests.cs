// A user custom formatter's payload in a robust context: the format's own contract.
//
// A custom formatter writes bytes Ceras cannot interpret, and until this format version neither the
// schema section nor the layout fingerprint said so — a build that kept the member saw a *matching*
// fingerprint and took the generated reader over opaque bytes, and a build that removed the member
// could not skip the value and returned wrong data for everything after it. The format now says it:
//
//   * the manifest entry for a custom-formatted type is `Opaque`: no member list, the formatter's
//     identity, and whether a reference marker stands in front of the value;
//   * the payload is a 4-byte declared length followed by exactly that many bytes, per value;
//   * the formatter's identity is hashed into the layout fingerprint, so swapping a formatter changes
//     the layout instead of matching it;
//   * a migration consumes the value through the same formatter, or refuses — and refusing is the
//     default, because losing a value is the caller's decision (CerasRobustConfig
//     .AllowDroppingCustomPayloads).
//
// These tests are about the contract, in one assembly, on the paths a test can drive directly: round
// trips, byte-level frame checks, the refusals that are left, and the migration interpreter. A *real*
// old file read by this build — with the v1 model, its own context and its own formatter class, none of
// which exist here — is Tests/OpaqueCustomPayloadFixtureTests.cs. The smallest form of the same case
// on both the robust and the raw path is in Tests/RobustCustomFormatterTests.cs.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Formatters;
using Ceras.Generated;
using Ceras.Helpers;
using Xunit;

namespace Ceras.Test.Opaque
{
    /// <summary>
    /// A plain [CerasSerializable] class whose values are written by a <em>user</em> custom formatter.
    /// <para>
    /// It is deliberately the sharp case: a type that has a generated formatter and that the application
    /// then overrides. The manifest entry for it becomes <c>Opaque</c>, so the schema stops advertising
    /// members the payload does not contain.
    /// </para>
    /// <para>
    /// It is a root of the old context and reachable from the next context's models, so it is a manifest
    /// entry in both — which is what a migration needs in order to resolve the file's type at all.
    /// </para>
    /// </summary>
    [CerasContext("OpaquePayloadOld")]
    [CerasSerializable]
    public class Payload
    {
        public int Id;
    }

    /// <summary>The same thing as a value type, for the half of the contract with no reference marker.</summary>
    [CerasContext("OpaquePayloadOld")]
    [CerasSerializable]
    public struct PayloadStruct
    {
        public int Id;
    }

    /// <summary>
    /// A type in a context of its own, so no <c>OpaquePayload*</c> manifest holds it. A custom formatter
    /// for it has no entry to record the selection in, which is one of the two shapes the format still
    /// refuses.
    /// </summary>
    [CerasContext("OpaquePayloadUnreachable")]
    [CerasSerializable]
    public class Unreachable
    {
        public int Value;
    }

    /// <summary>
    /// v1's formatter for <see cref="Payload"/>. It owns its bytes completely, and the payload's width
    /// depends on the value, so a frame's declared length is a fact about that value and not a constant
    /// of the type:
    /// <list type="bullet">
    /// <item><see cref="ShortTag"/>: the tag plus the id as a fixed 16-bit value — three bytes;</item>
    /// <item><see cref="LongTag"/>: the tag, the id as a fixed 32-bit value, and a filler the generated
    /// formatter would never write — nine bytes.</item>
    /// </list>
    /// The filler is what makes the encoding observable in the file's bytes: a value read back through
    /// the generated formatter could not possibly contain it.
    /// </summary>
    public class PayloadFormatter : IFormatter<Payload>
    {
        public const byte NullTag = 0x00;
        public const byte ShortTag = 0xC0;
        public const byte LongTag = 0xC1;
        public const int Filler = 0x5A5A5A5A;

        /// <summary>An id the byte-level tests look for, so "the formatter's own bytes" is a fixed pattern.</summary>
        public const int ShortProbeId = 0x7EED;

        public static int SerializeCalls;
        public static int DeserializeCalls;

        public void Serialize(IBufferWriter<byte> writer, Payload value)
        {
            SerializeCalls++;
            if (value == null) { SerializerBinary.WriteByte(writer, NullTag); return; }

            if (value.Id >= 0 && value.Id <= short.MaxValue)
            {
                SerializerBinary.WriteByte(writer, ShortTag);
                SerializerBinary.WriteInt16Fixed(writer, (short)value.Id);
            }
            else
            {
                SerializerBinary.WriteByte(writer, LongTag);
                SerializerBinary.WriteInt32Fixed(writer, value.Id);
                SerializerBinary.WriteInt32Fixed(writer, Filler);
            }
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref Payload value)
        {
            DeserializeCalls++;
            byte tag = SerializerBinary.ReadByte(ref reader);

            switch (tag)
            {
                case NullTag:
                    value = null;
                    return;
                case ShortTag:
                    value = new Payload { Id = SerializerBinary.ReadInt16Fixed(ref reader) };
                    return;
                case LongTag:
                    int id = SerializerBinary.ReadInt32Fixed(ref reader);
                    int filler = SerializerBinary.ReadInt32Fixed(ref reader);
                    if (filler != Filler)
                        throw new InvalidOperationException(
                            $"PayloadFormatter's long form ends with the filler 0x{Filler:X8}; this block has 0x{filler:X8}. These bytes are not " +
                            "this formatter's — which is what a payload written by a different formatter looks like from inside it.");
                    value = new Payload { Id = id };
                    return;
                default:
                    throw new InvalidOperationException(
                        $"PayloadFormatter writes the tags 0x{NullTag:X2}, 0x{ShortTag:X2} and 0x{LongTag:X2}; this block starts with 0x{tag:X2}.");
            }
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Payload)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            Payload value = null;
            Deserialize(ref reader, ref value);
            WriteBack(member, container, value);
        }

        /// <summary>The write-through a container/member read is, shared by the three formatters here.</summary>
        internal static void WriteBack(MemberInfo member, object container, Payload value)
        {
            if (member is FieldInfo field) field.SetValue(container, value);
            else if (member is PropertyInfo property) property.SetValue(container, value);
            else throw new NotSupportedException($"PayloadFormatter writes through a field or a property; this save names '{member?.MemberType}'.");
        }
    }

    /// <summary>
    /// A second, <em>differently named</em> formatter for the same type: the same value, a different
    /// encoding. Installing it instead of <see cref="PayloadFormatter"/> is what a build that changed
    /// its encoding looks like, and the two must not be interchangeable.
    /// </summary>
    public class OtherPayloadFormatter : IFormatter<Payload>
    {
        public const byte Tag = 0xD0;

        public void Serialize(IBufferWriter<byte> writer, Payload value)
        {
            SerializerBinary.WriteByte(writer, Tag);
            SerializerBinary.WriteString(writer, "other:" + (value?.Id ?? 0));
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref Payload value)
        {
            byte tag = SerializerBinary.ReadByte(ref reader);
            if (tag != Tag)
                throw new InvalidOperationException($"OtherPayloadFormatter writes the tag 0x{Tag:X2}; this block starts with 0x{tag:X2}.");
            string text = SerializerBinary.ReadString(ref reader);
            value = new Payload { Id = int.Parse(text.Substring("other:".Length)) };
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Payload)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            Payload value = null;
            Deserialize(ref reader, ref value);
            PayloadFormatter.WriteBack(member, container, value);
        }
    }

    /// <summary>A custom formatter for a value type, so the "written inline" half of the entry is exercised.</summary>
    public class PayloadStructFormatter : IFormatter<PayloadStruct>
    {
        /// <summary>Xor'd into the id, so a value read back through the generated formatter is visibly wrong.</summary>
        public const int Mask = 0x0F0F0F0F;

        public void Serialize(IBufferWriter<byte> writer, PayloadStruct value)
            => SerializerBinary.WriteInt32Fixed(writer, value.Id ^ Mask);

        public void Deserialize(ref SequenceReader<byte> reader, ref PayloadStruct value)
            => value.Id = SerializerBinary.ReadInt32Fixed(ref reader) ^ Mask;

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (PayloadStruct)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            PayloadStruct value = default;
            Deserialize(ref reader, ref value);
            if (member is FieldInfo field) field.SetValue(container, value);
            else if (member is PropertyInfo property) property.SetValue(container, value);
            else throw new NotSupportedException($"PayloadStructFormatter writes through a field or a property; this save names '{member?.MemberType}'.");
        }
    }

    /// <summary>
    /// A custom formatter that declares a static <c>Schema</c> field holding an instance that is <em>not</em>
    /// the entry the manifest holds for the type. That is the shape a generated formatter has, so the
    /// policy holds it to the identity it claims and refuses it: a schema that contradicts the bytes is
    /// the defect itself, and no framing can make it truthful.
    /// </summary>
    public class ImpostorSchemaFormatter : IFormatter<Payload>
    {
        /// <summary>The name the generator gives the field on every formatter class it emits.</summary>
        internal static readonly SchemaTypeInfo Schema = new SchemaTypeInfo(
            stableName: "Ceras.Test.Opaque.Payload",
            previousTypeNames: Array.Empty<string>(),
            kind: SchemaTypeKind.Class,
            fields: Array.Empty<SchemaFieldInfo>(),
            fixedByteSize: 0);

        public void Serialize(IBufferWriter<byte> writer, Payload value) => new PayloadFormatter().Serialize(writer, value);
        public void Deserialize(ref SequenceReader<byte> reader, ref Payload value) => new PayloadFormatter().Deserialize(ref reader, ref value);
        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Payload)value);
        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            Payload value = null;
            Deserialize(ref reader, ref value);
            PayloadFormatter.WriteBack(member, container, value);
        }
    }

    /// <summary>
    /// A formatter that declares the manifest's <em>own</em> instance for the type, which is what the
    /// identity check is actually about. Assigned the registered instance by the test, standing in for
    /// the generator's <c>internal static readonly</c> field, which a test cannot write.
    /// </summary>
    public class OwnSchemaFormatter : IFormatter<Payload>
    {
        /// <summary>Null until the test hands it the manifest's entry for the type.</summary>
        internal static SchemaTypeInfo Schema;

        public void Serialize(IBufferWriter<byte> writer, Payload value) => new PayloadFormatter().Serialize(writer, value);
        public void Deserialize(ref SequenceReader<byte> reader, ref Payload value) => new PayloadFormatter().Deserialize(ref reader, ref value);
        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Payload)value);
        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            Payload value = null;
            Deserialize(ref reader, ref value);
            PayloadFormatter.WriteBack(member, container, value);
        }
    }

    /// <summary>A formatter for a type none of the tested contexts describes.</summary>
    public class UnreachableFormatter : IFormatter<Unreachable>
    {
        public void Serialize(IBufferWriter<byte> writer, Unreachable value) => SerializerBinary.WriteInt32Fixed(writer, value?.Value ?? 0);

        public void Deserialize(ref SequenceReader<byte> reader, ref Unreachable value)
            => value = new Unreachable { Value = SerializerBinary.ReadInt32Fixed(ref reader) };

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Unreachable)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            Unreachable value = null;
            Deserialize(ref reader, ref value);
            if (member is FieldInfo field) field.SetValue(container, value);
            else throw new NotSupportedException("UnreachableFormatter writes through a field.");
        }
    }

    /// <summary>A custom formatter for a type the reserved table answers by name.</summary>
    public class ReservedNameFormatter : IFormatter<string>
    {
        public void Serialize(IBufferWriter<byte> writer, string value) => SerializerBinary.WriteString(writer, "s:" + value);

        public void Deserialize(ref SequenceReader<byte> reader, ref string value)
        {
            string text = SerializerBinary.ReadString(ref reader);
            value = text.Substring("s:".Length);
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (string)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            string value = null;
            Deserialize(ref reader, ref value);
            if (member is FieldInfo field) field.SetValue(container, value);
            else if (member is PropertyInfo property) property.SetValue(container, value);
            else throw new NotSupportedException("This formatter writes through a field or a property.");
        }
    }
}

namespace Ceras.Test.Opaque.Old
{
    using Ceras.Test.Opaque;

    /// <summary>Keeps its custom-formatted member: the case that works, and the reason the format changed.</summary>
    [CerasContext("OpaquePayloadOld")]
    [CerasSerializable]
    public class KeptHolder
    {
        /// <summary>
        /// A sentinel, and first in schema order (members are ordered by type name, and "Int32" sorts
        /// before "Payload"), so a frame that is off by anything at all shows up as a wrong number rather
        /// than as a plausible value.
        /// </summary>
        public int Sentinel;

        public Payload Value;

        /// <summary>The same instance as <see cref="Value"/>: a custom payload that keeps its reference
        /// marker comes back shared.</summary>
        public Payload Same;
    }

    /// <summary>The custom-formatted member is gone in the current model: the case the frame exists for.</summary>
    [CerasContext("OpaquePayloadOld")]
    [CerasSerializable]
    public class RemovedHolder
    {
        public int Sentinel;
        public Payload Value;
    }

    /// <summary>As <see cref="RemovedHolder"/>, and the payload's <em>type</em> is gone as well.</summary>
    [CerasContext("OpaquePayloadOld")]
    [CerasSerializable]
    public class GoneHolder
    {
        public int Sentinel;
        public Payload Value;
    }

    /// <summary>
    /// A value-type payload and nothing else, so the member order — and therefore the bytes — are fixed:
    /// the sentinel's varint, then the frame. That is what lets a test assert that no reference marker
    /// stands between them.
    /// </summary>
    [CerasContext("OpaquePayloadOld")]
    [CerasSerializable]
    public class InlineHolder
    {
        public int Sentinel;
        public PayloadStruct Value;
    }

    /// <summary>Every position a formatter can be written from, so "framed on every path" is one test.</summary>
    [CerasContext("OpaquePayloadOld")]
    [CerasSerializable]
    public class ShapedHolder
    {
        public int Sentinel;
        public List<Payload> Items;
        public Dictionary<string, Payload> Map;
        public ValueTuple<int, Payload> Pair;
        public object Poly;
    }
}

namespace Ceras.Test.Opaque.Next
{
    using Ceras.Test.Opaque;

    /// <summary>The same shape as v1's <c>KeptHolder</c>, so a migration has something to deliver into.</summary>
    [PreviousTypeName("Ceras.Test.Opaque.Old.KeptHolder")]
    [CerasContext("OpaquePayloadNext")]
    [CerasSerializable]
    public class KeptHolder
    {
        public int Sentinel;
        public Payload Value;
        public Payload Same;
    }

    /// <summary>v1's <c>RemovedHolder</c> with the custom-formatted member deleted.</summary>
    [PreviousTypeName("Ceras.Test.Opaque.Old.RemovedHolder")]
    [CerasContext("OpaquePayloadNext")]
    [CerasSerializable]
    public class RemovedHolder
    {
        public int Sentinel;

        /// <summary>Where the test reads what the migration reported: a dropped payload is observable as a
        /// default, and a <em>removed</em> member is reported.</summary>
        [OnAfterDeserialize]
        private void After(MigrationContext context) => LastContext = context;

        public static MigrationContext LastContext;
    }

    /// <summary>v1's <c>InlineHolder</c>, unchanged.</summary>
    [PreviousTypeName("Ceras.Test.Opaque.Old.InlineHolder")]
    [CerasContext("OpaquePayloadNext")]
    [CerasSerializable]
    public class InlineHolder
    {
        public int Sentinel;
        public PayloadStruct Value;
    }

    /// <summary>v1's <c>ShapedHolder</c>, unchanged.</summary>
    [PreviousTypeName("Ceras.Test.Opaque.Old.ShapedHolder")]
    [CerasContext("OpaquePayloadNext")]
    [CerasSerializable]
    public class ShapedHolder
    {
        public int Sentinel;
        public List<Payload> Items;
        public Dictionary<string, Payload> Map;
        public ValueTuple<int, Payload> Pair;
        public object Poly;
    }
}

namespace Ceras.Test.Opaque.Gone
{
    /// <summary>
    /// v1's <c>GoneHolder</c> without the member, in a context that describes neither the member nor the
    /// payload type: the "the type is gone too" half of a removed custom payload.
    /// </summary>
    [PreviousTypeName("Ceras.Test.Opaque.Old.GoneHolder")]
    [CerasContext("OpaquePayloadGone")]
    [CerasSerializable]
    public class GoneHolder
    {
        public int Sentinel;
    }

    /// <summary>
    /// v1's <c>PlainHolder</c> with an ordinary described member removed, for the contrast: that removal
    /// is a plain skip and is never refused.
    /// </summary>
    [PreviousTypeName("Ceras.Test.Opaque.GoneOld.PlainHolder")]
    [CerasContext("OpaquePayloadGone")]
    [CerasSerializable]
    public class PlainHolder
    {
        public int Sentinel;
        public string Text;
    }
}

namespace Ceras.Test.Opaque.GoneOld
{
    /// <summary>v1's holder whose removed member is a plain string, not a custom payload.</summary>
    [CerasContext("OpaquePayloadGoneOld")]
    [CerasSerializable]
    public class PlainHolder
    {
        public int Sentinel;
        public string Text;

        /// <summary>Removed in the current model; its bytes have to be skipped.</summary>
        public string Retired;
    }
}

namespace Ceras.Test.Tests
{
    using Ceras.Test.Opaque;
    using Gone = Ceras.Test.Opaque.Gone;
    using GoneOld = Ceras.Test.Opaque.GoneOld;
    using Next = Ceras.Test.Opaque.Next;
    using Old = Ceras.Test.Opaque.Old;

    /// <summary>
    /// The five things the format now does for a user custom formatter, and the refusals that are left.
    /// See the file header for the contract and <c>Format/LAYOUT.md</c> for the wire grammar.
    /// </summary>
    public class OpaqueCustomPayloadTests : TestBase
    {
        const string OldContext = "OpaquePayloadOld";
        const string NextContext = "OpaquePayloadNext";
        const string GoneContext = "OpaquePayloadGone";

        // ── contexts ───────────────────────────────────────────────────────────────────────────

        /// <summary>The writer's context, with <see cref="PayloadFormatter"/> on the payload type.</summary>
        static CerasRobustSerializer Old_(IFormatter<Payload> formatter = null, bool allowDropping = false)
            => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(OldContext);
                c.AllowDroppingCustomPayloads = allowDropping;
                c.ConfigType<Payload>().CustomFormatter = formatter ?? new PayloadFormatter();
            });

        /// <summary>The same, plus the value type's formatter — the half with no reference marker.</summary>
        static CerasRobustSerializer OldWithStruct()
            => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(OldContext);
                c.ConfigType<Payload>().CustomFormatter = new PayloadFormatter();
                c.ConfigType<PayloadStruct>().CustomFormatter = new PayloadStructFormatter();
            });

        /// <summary>The reading context, with the same formatter under the same name.</summary>
        static CerasRobustSerializer Next_(IFormatter<Payload> formatter = null, bool allowDropping = false)
            => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(NextContext);
                c.AllowDroppingCustomPayloads = allowDropping;
                c.ConfigType<Payload>().CustomFormatter = formatter ?? new PayloadFormatter();
            });

        /// <summary>The reading context for the value type, which the v1 file also framed.</summary>
        static CerasRobustSerializer NextWithStruct()
            => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(NextContext);
                c.ConfigType<Payload>().CustomFormatter = new PayloadFormatter();
                c.ConfigType<PayloadStruct>().CustomFormatter = new PayloadStructFormatter();
            });

        // ── byte-level helpers ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The save's data section, which is where a payload lives: between the two section offsets in
        /// the header. Every search below is confined to it, so a byte pattern cannot be found in the
        /// schema section, the string table or the CRC by accident — which would make a frame assertion
        /// pass (or fail) for the wrong reason.
        /// </summary>
        static (int Start, int End) DataSection(byte[] save)
        {
            int start = BitConverter.ToInt32(save, RobustHeader.DataSectionOffsetOffset);
            int end = BitConverter.ToInt32(save, RobustHeader.StringTableOffsetOffset);
            Assert.InRange(start, 0, save.Length);
            Assert.InRange(end, start, save.Length);
            return (start, end);
        }

        static int IndexOf(byte[] save, byte[] needle, int from = 0)
        {
            var (start, end) = DataSection(save);
            for (int i = Math.Max(from, start); i <= end - needle.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                    if (save[i + j] != needle[j]) { hit = false; break; }
                if (hit) return i;
            }
            return -1;
        }

        static int CountOf(byte[] save, byte[] needle)
        {
            int n = 0;
            for (int i = IndexOf(save, needle); i >= 0; i = IndexOf(save, needle, i + 1)) n++;
            return n;
        }

        /// <summary>
        /// The same search over a whole buffer, for a <em>raw</em> save — which has no schema section, no
        /// section offsets and therefore no data section to confine the search to. A raw save is a stream
        /// of values, so a pattern found anywhere in it is the value's bytes.
        /// </summary>
        static int IndexOfWholeBuffer(byte[] buffer, byte[] needle, int from = 0)
        {
            for (int i = Math.Max(from, 0); i <= buffer.Length - needle.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                    if (buffer[i + j] != needle[j]) { hit = false; break; }
                if (hit) return i;
            }
            return -1;
        }

        /// <summary>Where the payload's bytes are, asserting that they are in the data section at all.</summary>
        static int At(byte[] save, byte[] payload)
        {
            int at = IndexOf(save, payload);
            Assert.True(at >= 0, $"the payload's bytes {BitConverter.ToString(payload)} are not in the save's data section");
            return at;
        }

        /// <summary>The declared length in front of a payload, read as the format's own fixed int32.</summary>
        static int DeclaredLengthInFrontOf(byte[] save, byte[] payload)
        {
            int at = At(save, payload);
            Assert.True(at >= DataSection(save).Start + 4, "the payload is at the very start of the data section, so nothing can precede it");
            return BitConverter.ToInt32(save, at - 4);
        }

        /// <summary>PayloadFormatter's short form, byte for byte: the tag and the probe id.</summary>
        static byte[] ShortPayload() => new byte[] { PayloadFormatter.ShortTag, 0xED, 0x7E };

        /// <summary>PayloadFormatter's long form for id -1: the tag, the id, and the filler.</summary>
        static byte[] LongPayload() => new byte[]
        {
            PayloadFormatter.LongTag, 0xFF, 0xFF, 0xFF, 0xFF, 0x5A, 0x5A, 0x5A, 0x5A,
        };

        static ulong FingerprintOf(byte[] bytes) => BitConverter.ToUInt64(bytes, RobustHeader.FingerprintOffset);

        /// <summary>The header CRC as it is stored, and recomputed the same way the loader does it, so a
        /// rewritten save can be shown to be internally consistent rather than merely plausible.</summary>
        static uint CrcOf(byte[] bytes) => BitConverter.ToUInt32(bytes, RobustHeader.ChecksumOffset);

        static uint RecomputedCrcOf(byte[] bytes)
            => HashHelpers.CalculateCrc32WithZeroedRange(bytes, RobustHeader.ChecksumOffset, RobustHeader.ChecksumSize);

        /// <summary>
        /// The <c>internal static readonly SchemaTypeInfo Schema</c> the source generator emits on every
        /// formatter class, read off the formatter's runtime type — the same member, and only this member,
        /// that <c>RobustCustomFormatterPolicy</c> classifies a formatter by. Null for a formatter the
        /// generator did not emit.
        /// </summary>
        static SchemaTypeInfo DeclaredSchemaOf(Type formatterType)
            => formatterType
                .GetField("Schema", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(null) as SchemaTypeInfo;

        static string Dump(byte[] bytes) => CerasRobustSerializer.DumpSchema(bytes);

        /// <summary>
        /// The one dump line for an opaque entry, asked by <see cref="Type"/> and matched on the stable
        /// name the schema section records — the type's canonical name — with both surrounding spaces
        /// required. Both details are load-bearing: "Payload" is a prefix of "PayloadStruct", so a
        /// substring match would let an assertion about one type pass on another's line.
        /// </summary>
        static string OpaqueLineFor(string dump, Type type)
        {
            string stableName = TypeIdentity.GetCanonicalName(type);
            var line = dump.Split('\n').FirstOrDefault(l => l.StartsWith("OPAQUE", StringComparison.Ordinal)
                                                          && l.Contains($"  {stableName}  ", StringComparison.Ordinal));
            Assert.True(line != null, $"the dump has no OPAQUE entry for '{stableName}':\n{dump}");
            return line;
        }

        static Old.KeptHolder KeptValue(int sentinel = unchecked((int)0x11223344))
            => new Old.KeptHolder
            {
                Sentinel = sentinel,
                Value = new Payload { Id = PayloadFormatter.ShortProbeId },
            };

        // ── 1. the schema describes an opaque block with a declared length ────────────────────

        [Fact]
        public void TheManifestEntryIsOpaque_AndHoldsNoMemberList()
        {
            var bytes = Old_().Save(KeptValue(), 1);
            var dump = Dump(bytes);

            // The entry: the formatter's identity, which side of a reference marker the frame is on, and
            // the fact that the width is declared per value.
            var line = OpaqueLineFor(dump, typeof(Payload));
            Assert.Contains(nameof(PayloadFormatter), line, StringComparison.Ordinal);
            Assert.Contains("behind a reference marker", line, StringComparison.Ordinal);
            Assert.Contains("declared length", line, StringComparison.Ordinal);

            // And no member list, asked of the manifest rather than of the dump: the generated schema of
            // Payload is one field, `Id`, and the payload does not contain it — the custom formatter
            // writes a tag. An entry that still described the field would send a migration reader looking
            // for bytes that are not there, which is the whole lie this entry kind replaces.
            //
            // A dump assertion cannot carry that: a bare "Id" search finds PayloadStruct's member (that
            // type is described here, since only Payload has a formatter), and a search shaped to avoid
            // that would be asserting the absence of a string rather than of a member list.
            var serializer = Old_();
            serializer.Save(KeptValue(), 1);

            Assert.True(serializer._config.SchemaRegistry.TryGetInfo(typeof(Payload), out var entry));
            Assert.Equal(SchemaTypeKind.Opaque, entry.Kind);
            Assert.Empty(entry.Fields);
            Assert.Empty(entry.EnumValues);
            Assert.Equal(0, entry.FixedByteSize);
        }

        [Fact]
        public void ThePayloadIsADeclaredLengthFollowedByTheFormattersOwnBytes()
        {
            var bytes = Old_().Save(KeptValue(), 1);

            int at = At(bytes, ShortPayload());

            // The four bytes in front of the formatter's own bytes are the declared length, little-endian,
            // and it is this value's width: not a constant of the type, and not something the schema
            // section carries (the same type writes a different width for a different value).
            Assert.Equal(3, BitConverter.ToInt32(bytes, at - 4));
        }

        [Fact]
        public void EachValueDeclaresItsOwnLength()
        {
            var bytes = Old_().Save(new Old.KeptHolder
            {
                Sentinel = 7,
                Value = new Payload { Id = PayloadFormatter.ShortProbeId },   // three bytes
                Same = new Payload { Id = -1 },                              // nine
            }, 1);

            Assert.Equal(3, DeclaredLengthInFrontOf(bytes, ShortPayload()));
            Assert.Equal(9, DeclaredLengthInFrontOf(bytes, LongPayload()));
        }

        [Fact]
        public void AValueTypeEntryRecordsThatTheValueIsWrittenInline()
        {
            var bytes = OldWithStruct().Save(new Old.InlineHolder { Sentinel = 1, Value = new PayloadStruct { Id = 2 } }, 1);
            var dump = Dump(bytes);

            // The same entry kind and the other flag: a value type has no reference marker in front of its
            // frame, and the file says so, because a reader that no longer has the type has nothing else
            // to go on.
            Assert.Contains("inline", OpaqueLineFor(dump, typeof(PayloadStruct)), StringComparison.Ordinal);
            Assert.Contains("behind a reference marker", OpaqueLineFor(dump, typeof(Payload)), StringComparison.Ordinal);
        }

        [Fact]
        public void AValueTypeFrameHasNoReferenceMarkerInFrontOfIt()
        {
            var bytes = OldWithStruct().Save(new Old.InlineHolder { Sentinel = 1, Value = new PayloadStruct { Id = 0x0BADF00D } }, 1);

            // The data section here is: the root type id (a LEB128), the sentinel (a one-byte varint,
            // 0x02 for 1), the declared length, and the formatter's four bytes. Nothing stands between the
            // sentinel and the length, which is what "inline" means on the wire — a reference-typed value
            // would have a marker in exactly that place, and the byte test above for the class case is the
            // one that shows a marker does not disturb the length that follows it.
            int payload = At(bytes, BitConverter.GetBytes(0x0BADF00D ^ PayloadStructFormatter.Mask));

            Assert.Equal(4, BitConverter.ToInt32(bytes, payload - 4));

            // The byte in front of the length is the sentinel, and nothing is between the two. That is
            // the whole claim of this test: a reference-typed value would have a marker in exactly this
            // place (the class case above shows a marker *is* written, and that it does not disturb the
            // length that follows it), so a member whose type is a value type has its frame right behind
            // the previous member's bytes.
            Assert.Equal(0x02, bytes[payload - 5]);
        }

        [Fact]
        public void AReferenceValuedPayloadKeepsItsReferenceMarker()
        {
            // One instance in two members: written once behind a marker, and read back shared. That works
            // only if the marker stands *outside* the frame and the object-cache slot is reserved in the
            // same place it always is — the frame is the formatter's bytes and nothing else.
            var shared = new Payload { Id = PayloadFormatter.ShortProbeId };
            var bytes = Old_().Save(new Old.KeptHolder { Sentinel = 3, Value = shared, Same = shared }, 1);

            // The second member is a back-reference, not a second frame.
            Assert.Equal(1, CountOf(bytes, ShortPayload()));

            var clone = Old_().Load<Old.KeptHolder>(bytes, 1);
            Assert.NotNull(clone.Value);
            Assert.Same(clone.Value, clone.Same);
            Assert.Equal(PayloadFormatter.ShortProbeId, clone.Value.Id);
        }

        [Fact]
        public void ANullCustomPayloadIsAMarkerAndNoFrame()
        {
            // The reference wrapper writes the null marker and never calls the formatter, so there is no
            // frame to read back. The sentinel after it is what proves the write and the read agree.
            var bytes = Old_().Save(new Old.KeptHolder { Sentinel = 99, Value = null, Same = null }, 1);

            Assert.Equal(-1, IndexOf(bytes, ShortPayload()));

            var clone = Old_().Load<Old.KeptHolder>(bytes, 1);
            Assert.Null(clone.Value);
            Assert.Null(clone.Same);
            Assert.Equal(99, clone.Sentinel);
        }

        [Fact]
        public void ACustomPayloadRoundTripsThroughItsOwnFormatter()
        {
            PayloadFormatter.SerializeCalls = 0;
            PayloadFormatter.DeserializeCalls = 0;

            var bytes = Old_().Save(KeptValue(), 1);
            var clone = Old_().Load<Old.KeptHolder>(bytes, 1);

            Assert.Equal(unchecked((int)0x11223344), clone.Sentinel);
            Assert.Equal(PayloadFormatter.ShortProbeId, clone.Value.Id);

            // And it was the custom formatter on both sides, not the generated one: the counters are
            // static and the values are the ones this write/read produced.
            Assert.Equal(1, PayloadFormatter.SerializeCalls);
            Assert.Equal(1, PayloadFormatter.DeserializeCalls);
        }

        [Fact]
        public void AValueTypeCustomPayloadRoundTrips()
        {
            var bytes = OldWithStruct().Save(new Old.InlineHolder { Sentinel = 42, Value = new PayloadStruct { Id = 0x0BADF00D } }, 1);
            var clone = OldWithStruct().Load<Old.InlineHolder>(bytes, 1);

            Assert.Equal(42, clone.Sentinel);
            Assert.Equal(0x0BADF00D, unchecked((int)clone.Value.Id));
        }

        [Fact]
        public void EveryPositionThatWritesATypeIsFramed()
        {
            var bytes = Old_().Save(Shaped(1), 1);

            // One member, one collection, a dictionary value, a tuple item and a polymorphic slot. The
            // wrap is installed as the type's specific formatter, which is what every one of these paths
            // resolves, so none of them needed a generated-code change to be framed.
            var clone = Old_().Load<Old.ShapedHolder>(bytes, 1);
            AssertSameShape(1, clone);

            // And the same model read by the *next* build, where the interpreter has to account for and
            // read every one of them itself.
            AssertSameShape(1, Next_().Load<Next.ShapedHolder>(bytes, 1));
        }

        static Old.ShapedHolder Shaped(int sentinel) => new Old.ShapedHolder
        {
            Sentinel = sentinel,
            Items = new List<Payload> { new Payload { Id = 1 }, new Payload { Id = 2 } },
            Map = new Dictionary<string, Payload> { ["a"] = new Payload { Id = 3 } },
            Pair = (4, new Payload { Id = 5 }),
            Poly = new Payload { Id = 6 },
        };

        static void AssertSameShape(int sentinel, Old.ShapedHolder clone)
        {
            Assert.Equal(sentinel, clone.Sentinel);
            Assert.Equal(2, clone.Items.Count);
            Assert.Equal(1, clone.Items[0].Id);
            Assert.Equal(2, clone.Items[1].Id);
            Assert.Equal(3, clone.Map["a"].Id);
            Assert.Equal(4, clone.Pair.Item1);
            Assert.Equal(5, clone.Pair.Item2.Id);
            Assert.Equal(6, Assert.IsType<Payload>(clone.Poly).Id);
        }

        static void AssertSameShape(int sentinel, Next.ShapedHolder clone)
        {
            Assert.Equal(sentinel, clone.Sentinel);
            Assert.Equal(2, clone.Items.Count);
            Assert.Equal(1, clone.Items[0].Id);
            Assert.Equal(2, clone.Items[1].Id);
            Assert.Equal(3, clone.Map["a"].Id);
            Assert.Equal(4, clone.Pair.Item1);
            Assert.Equal(5, clone.Pair.Item2.Id);
            Assert.Equal(6, Assert.IsType<Payload>(clone.Poly).Id);
        }

        // ── 2. the formatter's selection is part of the layout fingerprint ────────────────────

        [Fact]
        public void TwoFormattersForOneTypeAreTwoLayouts()
        {
            var bytes = KeptValue();
            var withPayloadFormatter = FingerprintOf(Old_().Save(bytes, 1));
            var withOtherFormatter = FingerprintOf(Old_(new OtherPayloadFormatter()).Save(bytes, 1));
            var withNone = FingerprintOf(new CerasRobustSerializer(c => c.UseGeneratedFormatters(OldContext)).Save(bytes, 1));

            Assert.NotEqual(withPayloadFormatter, withOtherFormatter);
            Assert.NotEqual(withPayloadFormatter, withNone);

            // So a file written by one is not read by the other on the generated path: the fingerprints
            // differ, the migration path runs, and its verdict refuses rather than misreading a payload
            // as another formatter's bytes.
            Assert.Throws<CerasSchemaException>(() =>
                Old_(new OtherPayloadFormatter()).Load<Old.KeptHolder>(Old_().Save(KeptValue(), 1), 1));
        }

        [Fact]
        public void TheFormattersNameIsInTheSchemaSection()
        {
            var bytes = Old_().Save(KeptValue(), 1);

            // The canonical name of the formatter's CLR type: namespace and name, no assembly version or
            // public key token, so a file and a later build can agree on it.
            Assert.Contains("Ceras.Test.Opaque.PayloadFormatter", Dump(bytes), StringComparison.Ordinal);
        }

        [Fact]
        public void TheForcedInterpreterAgreesWithTheGeneratedReader()
        {
            // A same-layout cross-check: the bytes describe this context's schema exactly, and only the
            // fingerprint (which selects the reader) differs. The interpreter can only produce the same
            // value by reading the block through the same formatter, so the two readers are pinned
            // against each other.
            var bytes = Old_().Save(KeptValue(), 1);

            var generated = Old_().Load<Old.KeptHolder>(bytes, 1);
            var interpreted = Old_().Load<Old.KeptHolder>(SlowPath(bytes), 1);

            Assert.Equal(generated.Sentinel, interpreted.Sentinel);
            Assert.Equal(generated.Value.Id, interpreted.Value.Id);
        }

        // ── 3. a matching-fingerprint load meeting an unexpected block refuses ───────────────

        [Fact]
        public void ADeclaredLengthTheFileCannotDeliverIsRefusedAsCorruption()
        {
            var bytes = Old_().Save(KeptValue(), 1);
            int at = At(bytes, ShortPayload());

            // A length of int.MaxValue: the block claims more than the file holds. There is no way to find
            // where the next value starts, so this is corruption and not a value.
            BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, at - 4);
            FixRobustChecksum(bytes);

            var error = Assert.Throws<CerasDataCorruptionException>(() => Old_().Load<Old.KeptHolder>(bytes, 1));
            Assert.Contains("declares a length", error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(Payload), error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ANegativeDeclaredLengthIsRefusedAsCorruption()
        {
            var bytes = Old_().Save(KeptValue(), 1);
            int at = At(bytes, ShortPayload());

            BitConverter.GetBytes(-1).CopyTo(bytes, at - 4);
            FixRobustChecksum(bytes);

            var error = Assert.Throws<CerasDataCorruptionException>(() => Old_().Load<Old.KeptHolder>(bytes, 1));
            Assert.Contains("negative", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ALengthTheFormatterDoesNotAgreeWithIsRefused()
        {
            // The frame's one promise is that the declared length is how many bytes the formatter wrote.
            // A length that is too *large* hands the formatter bytes that are not its own, so it reads
            // less than the block declares — which is checked, because the alternative is a value the
            // formatter never finished reading, and nothing in the file would say so.
            var bytes = Old_().Save(KeptValue(), 1);
            int at = At(bytes, ShortPayload());

            BitConverter.GetBytes(6).CopyTo(bytes, at - 4);   // two more than the formatter wrote
            FixRobustChecksum(bytes);

            var error = Assert.Throws<CerasDataCorruptionException>(() => Old_().Load<Old.KeptHolder>(bytes, 1));
            Assert.Contains("unaccounted for", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ATruncatedBlockIsRefused()
        {
            // And the other direction: a length shorter than the formatter needs, so the block ends in the
            // middle of the value. The formatter's own read reports it, because it is the one thing that
            // knows which bytes were its own.
            var bytes = Old_().Save(KeptValue(), 1);
            int at = At(bytes, ShortPayload());

            BitConverter.GetBytes(2).CopyTo(bytes, at - 4);
            FixRobustChecksum(bytes);

            Assert.ThrowsAny<CerasException>(() => Old_().Load<Old.KeptHolder>(bytes, 1));
        }

        [Fact]
        public void AnUnframedPayloadIsNotSilentlyAccepted()
        {
            // The negative of "there is a frame": remove the four length bytes and the reader reads the
            // payload's own first bytes as a length. Nothing here is a valid save any more, and the load
            // says so rather than handing back a value built from a tag byte.
            var bytes = Old_().Save(KeptValue(), 1);
            int at = At(bytes, ShortPayload());

            // The payload's three bytes move into the four-byte length field, and the length field's own
            // first byte is replaced with the next byte of the stream, so the block claims a width no value
            // in this file has.
            var spliced = new List<byte>();
            spliced.AddRange(bytes.Take(at - 4));
            spliced.AddRange(ShortPayload());
            spliced.Add(bytes[at - 1]);
            spliced.AddRange(bytes.Skip(at + 3));

            var mutated = spliced.ToArray();
            FixRobustChecksum(mutated);

            Assert.ThrowsAny<CerasException>(() => Old_().Load<Old.KeptHolder>(mutated, 1));
        }

        // ── 4. migration consumes it, or the caller authorizes losing it ──────────────────────

        [Fact]
        public void TheSameFormatterIsReadThroughTheFrameOnTheMigrationPath()
        {
            // A real cross-version load, not a fingerprint flip: the file was written in the old context
            // and is read in the next one, so the schemas differ and the interpreter runs. The payload's
            // type resolves, this build's formatter for it is the one the file recorded, and the block is
            // read through the frame.
            var clone = Next_().Load<Next.KeptHolder>(Old_().Save(KeptValue(), 1), 1);

            Assert.Equal(unchecked((int)0x11223344), clone.Sentinel);
            Assert.Equal(PayloadFormatter.ShortProbeId, clone.Value.Id);
        }

        [Fact]
        public void AValueTypeIsReadThroughTheFrameOnTheMigrationPath()
        {
            var bytes = OldWithStruct().Save(new Old.InlineHolder { Sentinel = 8, Value = new PayloadStruct { Id = 0x0BADF00D } }, 1);
            var clone = NextWithStruct().Load<Next.InlineHolder>(bytes, 1);

            Assert.Equal(8, clone.Sentinel);
            Assert.Equal(0x0BADF00D, unchecked((int)clone.Value.Id));
        }

        [Fact]
        public void ADifferentFormatterIsRefusedAndBothNamesAreNamed()
        {
            var bytes = Old_().Save(KeptValue(), 1);

            var error = Assert.Throws<CerasSchemaException>(() => Next_(new OtherPayloadFormatter()).Load<Next.KeptHolder>(bytes, 1));

            // The refusal has to be actionable, so it names the formatter the file recorded, the one this
            // build has, the type, and the setting that authorizes losing the value.
            Assert.Contains(nameof(PayloadFormatter), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(OtherPayloadFormatter), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(Payload), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(CerasRobustConfig.AllowDroppingCustomPayloads), error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void NoCustomFormatterAtAllIsRefused()
        {
            var bytes = Old_().Save(KeptValue(), 1);

            // A build that dropped the custom formatter: the type is described by its members here, and
            // the file says those bytes are an opaque block. Reading it as a described value would hand
            // back a tag byte as an int.
            var reader = new CerasRobustSerializer(c => c.UseGeneratedFormatters(NextContext));
            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<Next.KeptHolder>(bytes, 1));

            Assert.Contains("no custom formatter", error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(Payload), error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ARemovedMembersBlockIsRefusedByDefault()
        {
            var bytes = Old_().Save(new Old.RemovedHolder { Sentinel = 5, Value = new Payload { Id = 9 } }, 1);

            // The bytes are accounted for — the declared length is the whole of the skip, so the sentinel
            // after it would be found — and the value is still not delivered to anything. The standing
            // skip-path asymmetry is deliberately *not* extended to a custom payload: the file records
            // only that a formatter wrote some bytes, which is not enough to reason about losing it, so
            // the default asks.
            var error = Assert.Throws<CerasSchemaException>(() => Next_().Load<Next.RemovedHolder>(bytes, 1));
            Assert.Contains(nameof(PayloadFormatter), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(CerasRobustConfig.AllowDroppingCustomPayloads), error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ARemovedMemberOfADescribedTypeIsStillAPlainSkip()
        {
            // The contrast, and the reason the refusal above is a decision rather than an accident: a
            // removed member of an ordinary *described* type is not gated, with or without the setting. The
            // file's schema says what that value was, so the application can reason about losing it.
            var writer = new CerasRobustSerializer(c => c.UseGeneratedFormatters("OpaquePayloadGoneOld"));
            var bytes = writer.Save(new GoneOld.PlainHolder { Sentinel = 4, Text = "kept", Retired = "gone" }, 1);

            var clone = new CerasRobustSerializer(c => c.UseGeneratedFormatters(GoneContext)).Load<Gone.PlainHolder>(bytes, 1);

            Assert.Equal(4, clone.Sentinel);
            Assert.Equal("kept", clone.Text);
        }

        [Fact]
        public void AllowDroppingSkipsARemovedMembersBlockAndReportsIt()
        {
            var bytes = Old_().Save(new Old.RemovedHolder { Sentinel = 5, Value = new Payload { Id = 9 } }, 1);

            Next.RemovedHolder.LastContext = null;
            var clone = Next_(allowDropping: true).Load<Next.RemovedHolder>(bytes, 1);

            // The value is lost and the load continues: the sentinel is found, because the skip advanced by
            // the declared length and by nothing else.
            Assert.Equal(5, clone.Sentinel);

            // And the member really was skipped, so it is reported. The report is the only observable of
            // the loss for a *removed* member — there is nothing left in the model to show a default on.
            Assert.NotNull(Next.RemovedHolder.LastContext);
            Assert.Contains(nameof(Old.RemovedHolder.Value), Next.RemovedHolder.LastContext.SkippedFields);
        }

        [Fact]
        public void ARemovedTypeIsRefusedAsAProblemWithNoFormatter()
        {
            // The member is gone *and* the reading context does not configure the payload's formatter, so
            // there is nothing that could read the block even in principle.
            //
            // The wording here is "no custom formatter for '<type>'", and that is the accurate answer for
            // this build rather than a weaker one: the type *is* present — it is this very assembly's
            // `Payload` — and the registry resolves a name to any CLR type it can see
            // (SchemaRegistry.TryResolveType, step 2) whether or not a manifest entry describes it. So
            // "install the formatter" is the fix a caller can act on, and "no such type" would send them
            // looking for a type that is right there. The "no such type" wording is for a name this build
            // genuinely cannot resolve, which is pinned separately below and by the fixture suite, where
            // the v1 type does not exist at all.
            var bytes = Old_().Save(new Old.GoneHolder { Sentinel = 6, Value = new Payload { Id = 9 } }, 1);

            var reader = new CerasRobustSerializer(c => c.UseGeneratedFormatters(GoneContext));
            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<Gone.GoneHolder>(bytes, 1));

            Assert.Contains("no custom formatter", error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(Payload), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(PayloadFormatter), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(CerasRobustConfig.AllowDroppingCustomPayloads), error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A binder that answers for every name except one, which it declines by returning null.
        /// <para>
        /// <c>ITypeBinder.GetTypeFromBase</c> is documented to throw when it cannot name a type, and the
        /// default <see cref="SimpleTypeBinder"/> does — which is why a removed type usually fails earlier
        /// and elsewhere, while resolving the file's schema section. Returning null is the other documented
        /// shape, and Ceras normalizes it to "no such type" rather than to a failure
        /// (SchemaRegistry.ResolveThroughBinder, SchemaTraversalEngine.ResolveFullName). A binder that
        /// declines one name is therefore a real configuration, and it is the only way to reach the
        /// "no such type" refusal from inside a single assembly.
        /// </para>
        /// </summary>
        sealed class DecliningBinder : ITypeBinder
        {
            readonly ITypeBinder _inner = new SimpleTypeBinder();
            readonly string _declined;

            public DecliningBinder(string declined) => _declined = declined;

            public string GetBaseName(Type type) => _inner.GetBaseName(type);

            public Type GetTypeFromBase(string baseTypeName)
                => baseTypeName == _declined ? null : _inner.GetTypeFromBase(baseTypeName);

            public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments)
                => baseTypeName == _declined ? null : _inner.GetTypeFromBaseAndArguments(baseTypeName, genericTypeArguments);
        }

        [Fact]
        public void ATypeTheBuildCannotResolveIsRefusedAsNoSuchType()
        {
            // The other half of the wording, and the case the two answers have to be told apart on: the
            // recorded type name resolves to nothing at all, so there is no type to install a formatter
            // *for*. The refusal says that, and still names the formatter the file recorded and the
            // setting that authorizes losing the value — a caller reading only the first clause would not
            // know that dropping the payload is a decision they can make.
            var bytes = Old_().Save(new Old.GoneHolder { Sentinel = 6, Value = new Payload { Id = 9 } }, 1);
            string payloadName = TypeIdentity.GetCanonicalName(typeof(Payload));

            var reader = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(GoneContext);
                c.TypeBinder = new DecliningBinder(payloadName);
            });

            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<Gone.GoneHolder>(bytes, 1));

            Assert.Contains("no such type", error.Message, StringComparison.Ordinal);
            Assert.Contains(payloadName, error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(PayloadFormatter), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(CerasRobustConfig.AllowDroppingCustomPayloads), error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AllowDroppingAlsoCoversARemovedType()
        {
            var bytes = Old_().Save(new Old.GoneHolder { Sentinel = 6, Value = new Payload { Id = 9 } }, 1);

            var reader = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(GoneContext);
                c.AllowDroppingCustomPayloads = true;
            });

            Assert.Equal(6, reader.Load<Gone.GoneHolder>(bytes, 1).Sentinel);
        }

        [Fact]
        public void AllowDroppingDropsARetainedButUnreadableMember()
        {
            var bytes = Old_().Save(KeptValue(), 1);

            // The member is still in the model and the formatter is the same, so the setting changes
            // nothing about the ordinary readable case.
            Assert.Equal(PayloadFormatter.ShortProbeId, Next_(allowDropping: true).Load<Next.KeptHolder>(bytes, 1).Value.Id);

            // The other half: the member is retained but this build cannot read the block, so the value
            // is dropped. What lands in the slot is the reader's own default — for a reference member, the
            // instance the reader allocated and registered for the slot, whose members keep their
            // defaults, exactly as a field added since the save gets. It is not null, because the reader
            // allocated the object this member belongs to and the member is part of it; and it is not the
            // value the file recorded, which is the difference that matters.
            var dropped = Next_(new OtherPayloadFormatter(), allowDropping: true).Load<Next.KeptHolder>(bytes, 1);
            Assert.Equal(unchecked((int)0x11223344), dropped.Sentinel);
            Assert.NotNull(dropped.Value);
            Assert.Equal(0, dropped.Value.Id);

            // And a retained member that was filled with nothing is *not* in SkippedFields: it was not
            // skipped, and there is no separate "dropped payload" channel to report it in. The observable
            // of the drop is the default above — which is the residual this leaves, stated rather than
            // papered over.
        }

        [Fact]
        public void TheRefusalIsDecidedBeforeAnyPayloadByteIsRead()
        {
            // The verdict is asked first, in both directions. So a file whose payload is *also* damaged
            // reports the schema refusal and not corruption: the interpreter never started reading the
            // block, which is the one thing a length prefix cannot protect against — a length says nothing
            // about what the bytes mean.
            var bytes = Old_().Save(KeptValue(), 1);
            int at = At(bytes, ShortPayload());

            for (int i = at; i < at + 3; i++) bytes[i] = 0xFF;
            BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, at - 4);
            FixRobustChecksum(bytes);

            var error = Assert.Throws<CerasSchemaException>(() => Next_(new OtherPayloadFormatter()).Load<Next.KeptHolder>(bytes, 1));
            Assert.Contains(nameof(OtherPayloadFormatter), error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AllowDroppingIsASettingAndIsSealedWithTheContext()
        {
            // It is a config setting like every other one, so it cannot be changed after the context is
            // sealed: the reading decision has to be made before the first Load, not halfway through a
            // migration. (ThrowIfSealed is the config's own rule, shared with every other setting; the
            // point here is that this property goes through it rather than setting the field directly.)
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters(OldContext));
            Assert.Throws<InvalidOperationException>(() => serializer._config.AllowDroppingCustomPayloads = true);
        }

        // ── 5. a raw/transient context keeps permitting custom formatters, unframed ────────────

        [Fact]
        public void ARawContextKeepsTheCustomFormattersBytesUnframed()
        {
            var model = KeptValue();
            var robust = Old_().Save(model, 1);
            Assert.Equal(3, DeclaredLengthInFrontOf(robust, ShortPayload()));

            // The same formatter and the same value in a raw context: the bytes are exactly what it wrote,
            // with no declared length in front of them, because a raw context describes nothing and so has
            // nothing to account for.
            var raw = new CerasFastSerializer(c =>
            {
                c.AllowReflectionFallback = true;
                c.ConfigType<Payload>().CustomFormatter = new PayloadFormatter();
            });
            var rawBytes = raw.Serialize(model);

            // A raw save has no schema section, no section offsets and no frame, so the search is over
            // the whole buffer and there is no declared length to read in front of the payload. What
            // there is to check is that the formatter's own bytes are there at all, in the same order,
            // and that nothing was inserted in front of them.
            // The decisive difference, stated over the same six bytes in both saves: the frame's declared
            // length is in the robust one and nowhere in the raw one. Searching for the *pair* rather
            // than reading four bytes at an offset is what makes this robust — the payload can sit at
            // offset 0 in a raw save (nothing precedes it if reflection writes that member first), so
            // there may be no four bytes in front of it to read at all.
            var framed = new byte[] { 3, 0, 0, 0 }.Concat(ShortPayload()).ToArray();
            Assert.True(IndexOf(robust, framed) >= 0, "the robust save has no declared length in front of the payload");
            Assert.Equal(-1, IndexOfWholeBuffer(rawBytes, framed));

            // And the payload's own bytes are present in the raw save, in the same order — the custom
            // formatter ran there too, which is the point of requirement 5: a raw context permits a custom
            // formatter, it just does not frame it.
            Assert.True(IndexOfWholeBuffer(rawBytes, ShortPayload()) >= 0, "the custom formatter's own bytes are not in the raw save");

            var clone = raw.Deserialize<Old.KeptHolder>(rawBytes);
            Assert.Equal(unchecked((int)0x11223344), clone.Sentinel);
            Assert.Equal(PayloadFormatter.ShortProbeId, clone.Value.Id);
        }

        [Fact]
        public void AContextWithNoCustomFormatterIsUnaffected()
        {
            // The other half of requirement 5 in the negative: nothing about a context that has no custom
            // formatter changed, byte for byte. The BatchB context in SerializerConfigNullabilityTests
            // pins those bytes exactly, and the whole fixture set is regenerated for the version.
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters(OldContext));
            var bytes = serializer.Save(KeptValue(), 1);

            Assert.DoesNotContain("OPAQUE", Dump(bytes), StringComparison.Ordinal);
            Assert.Equal(unchecked((int)0x11223344), serializer.Load<Old.KeptHolder>(bytes, 1).Sentinel);
        }

        // ── what is still refused, and what the identity check is ──────────────────────────────

        [Fact]
        public void AFormatterDeclaringAnotherSchemasInstanceIsRefused()
        {
            // The identity mechanism, unchanged: a formatter that declares the generator's static Schema
            // field is held to the *instance* the manifest holds, and a declaration that is not that
            // instance is refused. It is not recognised by name, by namespace, or by anything a caller
            // can assert about it.
            var error = Assert.Throws<CerasSchemaException>(() => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(OldContext);
                c.ConfigType<Payload>().CustomFormatter = new ImpostorSchemaFormatter();
            }));

            Assert.Contains("not the entry this context's manifest holds", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AFormatterDeclaringTheManifestsOwnInstanceIsTheContextsOwn()
        {
            // The positive half of the same check: holding the very instance the manifest registered makes
            // the formatter the context's own, so its entry is *not* made opaque and nothing is framed.
            // That is the whole rule, and it is what keeps a generated formatter recognised after this
            // change.
            var probe = new CerasRobustSerializer(c => c.UseGeneratedFormatters(OldContext));
            Assert.True(probe._config.SchemaRegistry.TryGetInfo(typeof(Payload), out var registered),
                "the generated manifest holds no entry for the payload type, so the identity check has nothing to compare against");

            OwnSchemaFormatter.Schema = registered;

            var serializer = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(OldContext);
                c.ConfigType<Payload>().CustomFormatter = new OwnSchemaFormatter();
            });

            // This is the documented forge — SchemaRegistry.Register is public, so a hand-registered
            // schema plus a hand-written formatter still satisfies the identity check. It is pre-existing
            // and it is *not* widened here (a forged registration is not an opaque selection, so it
            // neither gains the frame nor loses anything); closing it means verifying a schema against
            // the type's real members. So the save is written and never read back here: the schema
            // describes members the payload does not contain, which is exactly the state this mechanism
            // exists to make describable and does not.
            var bytes = serializer.Save(KeptValue(), 1);
            Assert.DoesNotContain("OPAQUE", Dump(bytes), StringComparison.Ordinal);
        }

        [Fact]
        public void EveryGeneratedFormatter_DeclaresTheSchemaTheManifestHolds()
        {
            // The invariant the identity check above rests on, pinned across the *whole* test assembly
            // rather than for the one type it happens to be shown on. A generated formatter is the
            // context's own because the static `Schema` field it declares is the very instance the context
            // registered for its type: the generator emits that field on every formatter class it writes
            // and the registration hands the registry that same field
            // (`SchemaRegistry.Register(typeof(X), XFormatter.Schema)`).
            //
            // Both halves of that drift silently, which is why a single type proves nothing:
            //
            //   * a formatter class that stopped declaring the field would be classified as a *user*
            //     payload, so its entry would silently become Opaque and the generated members of an
            //     ordinary type would stop being described — a save that still round-trips and is no
            //     longer what the type actually is;
            //   * a registry that held an equal-but-distinct copy would refuse every ordinary generated
            //     formatter in a robust context, and the refusal would name the user's types rather than
            //     the generator.
            //
            // The sweep therefore covers the manifest of the "Tests" context, which the generated
            // registration fills from every [CerasSerializable] type in this assembly.
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters(TestBase.Context));
            serializer.Save(new HeroWithEnum { State = StateEnum.On }, 1);   // any root of the context; seals + bakes

            int entries = 0, withFormatter = 0;
            var declaringNamespaces = new SortedSet<string>(StringComparer.Ordinal);
            var declaringNothing = new List<string>();
            var notIdentical = new List<string>();

            foreach (var (type, _, info) in serializer.Config.SchemaRegistry.All())
            {
                entries++;
                declaringNamespaces.Add(type.Namespace ?? "<global>");

                // Null for the minimal registrations: an interface/abstract type, and a built-in closed
                // shape declared as a structural root. Neither has a generated formatter, so there is no
                // declaration to compare and nothing to hold one to.
                var formatter = serializer.Config.GetExistingTypeConfig(type, false)?.CustomFormatter;
                if (formatter == null) continue;

                withFormatter++;
                var declared = DeclaredSchemaOf(formatter.GetType());
                var name = $"{TypeIdentity.GetCanonicalName(type)} -> {formatter.GetType().FullName}";

                if (declared == null) declaringNothing.Add(name);
                else if (!ReferenceEquals(declared, info)) notIdentical.Add(name);
            }

            // Breadth first, and reported as a number so a shrinking manifest is visible as one. Without
            // it a sweep that quietly examined three types would satisfy both assertions below, which is
            // exactly how a test like this rots.
            Assert.True(withFormatter > 100,
                $"expected a populated manifest, found {withFormatter} formatters across {entries} entries");

            // "Every", and not just "most": the manifest has to span the assembly rather than one
            // namespace's worth of it, so that "every generated formatter" cannot quietly become
            // "every generated formatter in the file this test happens to live in".
            Assert.True(declaringNamespaces.Count >= 10,
                $"expected the manifest to span the test assembly, found {declaringNamespaces.Count} declaring namespaces: {string.Join(", ", declaringNamespaces)}");

            // Listed by name, so a failure says which type drifted rather than only that something did.
            Assert.Empty(declaringNothing);
            Assert.Empty(notIdentical);
        }

        [Fact]
        public void AFormatterThatLooksGeneratedButDeclaresNoSchemaIsAUserCustomFormatter()
        {
            // Name and namespace prove nothing: a user class in Ceras.Generated with a generated-looking
            // name is framed like any other user custom formatter, because the check reads a member and
            // compares an instance.
            var serializer = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(OldContext);
                c.ConfigType<Payload>().CustomFormatter = new ImpostorLookingFormatter();
            });

            var bytes = serializer.Save(KeptValue(), 1);
            Assert.Contains("OPAQUE", Dump(bytes), StringComparison.Ordinal);
            Assert.Contains(nameof(ImpostorLookingFormatter), Dump(bytes), StringComparison.Ordinal);
        }

        [Fact]
        public void AReservedNameTypeIsRefused()
        {
            // A type Ceras addresses by a reserved one-byte id has nowhere to record which formatter wrote
            // the value, whatever the manifest holds. (A *closed* built-in collection is not one of these:
            // the reserved table deliberately does not answer a closed canonical name, because such a type
            // is a manifest entry of its own, so an opaque entry can carry the selection.)
            var error = Assert.Throws<CerasSchemaException>(() => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(OldContext);
                c.ConfigType<string>().CustomFormatter = new ReservedNameFormatter();
            }));

            Assert.Contains("reserved well-known id", error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(ReservedNameFormatter), error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ATypeOutsideTheManifestIsRefused()
        {
            // The other half: no entry, so no place to record the selection, and a value of such a type
            // cannot be described in a save at all. Asked at the bake, because a bake is what registers a
            // shape nothing declared — so a selection that looks undescribable before the bake can still
            // be describable after it, and the other half (a reserved name) is what is asked up front.
            var serializer = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(OldContext);
                c.ConfigType<Unreachable>().CustomFormatter = new UnreachableFormatter();
            });

            var error = Assert.Throws<CerasSchemaException>(() => serializer.Save(KeptValue(), 1));
            Assert.Contains("not part of the selected serialization context's manifest", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AHandBuiltFrameIsRefusedInARawContext()
        {
            // The wrapper is internal, and CerasSerializer.WrapOpaqueIfSelected only builds one for a
            // robust config — but the type is reachable by reflection, and a frame written into a raw
            // context's bytes would be bytes nothing describes. The constructor is the last line of
            // defence, and it asks for the config's *type* so the answer cannot depend on when the wrap
            // happened. Instantiator is used rather than Activator because a Ceras refusal must arrive as
            // itself rather than wrapped in a TargetInvocationException.
            var raw = new CerasFastSerializer(c => c.AllowReflectionFallback = true);
            var frameType = typeof(CerasSerializer).Assembly
                .GetType("Ceras.Formatters.OpaqueFramedFormatter`1")
                .MakeGenericType(typeof(Payload));

            var error = Assert.Throws<CerasSchemaException>(() =>
                Instantiator.Create(frameType, new PayloadFormatter(), raw));

            Assert.Contains("not a robust one", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AValueTypeMemberIsBoundThroughTheFormatterInterface()
        {
            // The generator change, pinned where it is observable. A *value-type* member's formatter field
            // is IFormatter<T> and not the generated formatter class, because naming the class would decide
            // at compile time which formatter writes the member — and a formatter the application installs
            // at runtime could not then be used at all. A reference-typed member needs no such change:
            // ReferenceFormatter<T> already resolves its inner formatter at runtime, which is why the
            // reference half of this contract never needed a generated-code change.
            var fieldTypes = typeof(Ceras.Generated.Ceras_Test_Opaque_Old_InlineHolderFormatter)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(f => f.FieldType)
                .ToList();

            Assert.Contains(typeof(IFormatter<PayloadStruct>), fieldTypes);

            var referenceFieldTypes = typeof(Ceras.Generated.Ceras_Test_Opaque_Old_KeptHolderFormatter)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(f => f.FieldType)
                .ToList();

            Assert.Contains(typeof(ReferenceFormatter<Payload>), referenceFieldTypes);
        }

        // ── the version boundary this change created ─────────────────────────────────────────────

        /// <summary>
        /// The version this build writes and the only one it reads. Pinned as a literal, not as a
        /// tautology against a header byte: the two-region envelope has been stable since v4, so the
        /// version number is the one place a deliberate change to the file contract is recorded, and it
        /// moved from 4 to 5 for exactly one reason — v4 could hold a user custom formatter's opaque
        /// bytes under a schema (and a matching layout fingerprint) that described generated fields,
        /// which is the one state the file cannot describe and therefore cannot validate.
        /// </summary>
        [Fact]
        public void TheCurrentFormatVersion_IsFive()
        {
            Assert.Equal(5, CerasRobustSerializer.FormatVersion);

            // The byte in the header is the constant, and the two-region envelope the v4 layout introduced
            // is still the one being written: schema strings, then the data section, then the payload
            // string table.
            var bytes = Old_().Save(KeptValue(), 1);
            Assert.Equal(CerasRobustSerializer.FormatVersion, bytes[RobustHeader.FormatVersionOffset]);

            long schemaStrings = BitConverter.ToInt64(bytes, RobustHeader.SchemaStringOffsetOffset);
            long dataSection = BitConverter.ToInt64(bytes, RobustHeader.DataSectionOffsetOffset);
            long stringTable = BitConverter.ToInt64(bytes, RobustHeader.StringTableOffsetOffset);
            Assert.InRange(schemaStrings, (long)RobustHeader.Size, dataSection);
            Assert.InRange(dataSection, schemaStrings, stringTable);
        }

        /// <summary>
        /// A v4 file cannot be told apart from a good one by its fingerprint, and that is the reason for
        /// the bump. This rewinds <b>only</b> the version byte of a save the current build just wrote, so
        /// every other byte — the layout fingerprint and the CRC included — is exactly what this context
        /// produces, and the file stays internally consistent. The fingerprint is therefore deliberately
        /// left matching, and the version gate is the only thing left that can reject it.
        /// <para>
        /// That is what makes this test bite: with the version check removed the load would take the
        /// generated fast path over a matching fingerprint and <b>succeed</b>, silently handing back a
        /// file that was written by a format this build no longer promises to read.
        /// </para>
        /// </summary>
        [Fact]
        public void AV4Save_IsRejected_EvenWhenTheLayoutFingerprintMatches()
        {
            var model = KeptValue();
            var bytes = Old_().Save(model, 1);

            // The control, and the reason the file is a fair test: this is a good save for the *same*
            // context, and the reader's own fingerprint is the one stored in it. It is loaded once first
            // because the fingerprint is only final once the registry is sealed.
            var reader = Old_();
            Assert.Equal(model.Sentinel, reader.Load<Old.KeptHolder>(bytes, 1).Sentinel);
            Assert.Equal(reader.Config.SchemaRegistry.ComputeFingerprint(), FingerprintOf(bytes));

            var asV4 = (byte[])bytes.Clone();
            asV4[RobustHeader.FormatVersionOffset] = 4;
            FixRobustChecksum(asV4);

            Assert.Equal(4, asV4[RobustHeader.FormatVersionOffset]);

            // Nothing else moved. Checked by locating every differing byte rather than asserted in a
            // comment, so "only the version byte changed" is a fact about the file rather than a claim
            // about how the test was written. The CRC is expected to differ — it covers the version.
            var changed = Enumerable.Range(0, bytes.Length).Where(i => bytes[i] != asV4[i]).ToList();
            Assert.Contains(RobustHeader.FormatVersionOffset, changed);
            Assert.All(changed, i => Assert.True(
                    i == RobustHeader.FormatVersionOffset
                    || (i >= RobustHeader.ChecksumOffset && i < RobustHeader.ChecksumOffset + RobustHeader.ChecksumSize),
                    $"byte {i} differs, and it is neither the version byte nor the checksum: a version rewind has to leave the schema, the payload and the fingerprint exactly as this build wrote them"));

            // The fingerprint is deliberately unchanged — that it still matches is the whole problem —
            // and the checksum is genuinely valid, so the rejection below cannot be explained by damage.
            Assert.Equal(FingerprintOf(bytes), FingerprintOf(asV4));
            Assert.Equal(RecomputedCrcOf(asV4), CrcOf(asV4));

            var error = Assert.Throws<CerasVersionMismatchException>(() => Old_().Load<Old.KeptHolder>(asV4, 1));

            // Typed, and carrying both ends, so a caller can branch on it rather than parse a message.
            Assert.Equal(4u, error.FileVersion);
            Assert.Equal(5u, error.LibraryVersion);

            // And it has to explain itself. The experimental format carried no upgrade promise, so a
            // version number alone reads like a skew the user can wait out; the reason a v4 file cannot be
            // read at all is the one thing the user has to be told.
            Assert.Contains("no longer accepted", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("custom formatter", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}

namespace Ceras.Generated
{
    using Ceras.Test.Opaque;

    /// <summary>
    /// A user class that looks like something the generator emits: the right namespace, the right name
    /// shape. It declares no <c>Schema</c> field, so it is a user custom formatter and is framed like
    /// one — the identity check reads a member and compares an instance, and there is no member here.
    /// </summary>
    public class ImpostorLookingFormatter : IFormatter<Payload>
    {
        public void Serialize(IBufferWriter<byte> writer, Payload value) => new PayloadFormatter().Serialize(writer, value);
        public void Deserialize(ref SequenceReader<byte> reader, ref Payload value) => new PayloadFormatter().Deserialize(ref reader, ref value);
        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Payload)value);
        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            Payload value = null;
            Deserialize(ref reader, ref value);
            PayloadFormatter.WriteBack(member, container, value);
        }
    }
}
