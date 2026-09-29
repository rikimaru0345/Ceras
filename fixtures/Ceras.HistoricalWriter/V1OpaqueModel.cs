// The opaque fixtures' "old" model, in a context of its own.
//
// A user custom formatter's payload is a length-prefixed block described by an Opaque manifest entry,
// so a file that contains one cannot be written by a generated context alone. This is the only model in
// the writer whose values are produced by a formatter Ceras did not generate, and the only one whose
// serializer carries a custom formatter (see Program.NewOpaqueSerializer). That is visible in the bytes:
// these two files are the only ones whose schema section holds an Opaque entry, and the only ones whose
// payload carries a declared length in front of a value.
//
// It lives in its own context for the same reason every other fixture context does: a context has one
// finite manifest, so adding these types to the frozen V1 model would renumber every type id in the
// other fixtures. A separate context keeps them byte-identical.
//
// See the folder README for how to regenerate.
using Ceras;
using Ceras.Formatters;
using Ceras.Generated;
using System;
using System.Buffers;

namespace Ceras.HistoricalWriter.V1Opaque
{
    /// <summary>
    /// v1's custom-formatted type. It is a plain [CerasSerializable] class with one field, and the
    /// generated formatter for it is registered as usual — the custom formatter replaces the *values*,
    /// not the type's place in the manifest. That is the sharp case the format changed for: an
    /// overridden type that also has a generated formatter, whose manifest entry becomes opaque so the
    /// schema stops advertising members the payload does not contain.
    /// </summary>
    [CerasContext("HistoricalV1Opaque")]
    [CerasSerializable]
    public class RecipeRef
    {
        public int RecipeId;
    }

    /// <summary>
    /// v1's user custom formatter for <see cref="RecipeRef"/>. It owns its bytes completely: a
    /// one-byte tag and a varint recipe id, an encoding the generated formatter would never produce.
    /// The identity of *this class* is what the file records, so a reader has to be configured with a
    /// formatter of the same name to read the block at all.
    /// </summary>
    public class RecipeRefFormatter : IFormatter<RecipeRef>
    {
        /// <summary>The first recipe id the fixtures use, so the writer's values are named in one place.</summary>
        public const int FirstRecipeId = 4001;

        /// <summary>Tag byte v1 wrote in front of every payload. A second tag exists so the fixtures can
        /// show that a value is read back through the formatter that wrote it, not through a default.</summary>
        public const byte CompactTag = 0xA1;

        /// <summary>The second tag: a different encoding of the same field, which is what a *replaced*
        /// formatter would do, and what makes "which formatter wrote this" observable in a payload.</summary>
        public const byte WideTag = 0xA2;

        public void Serialize(IBufferWriter<byte> writer, RecipeRef value)
        {
            if (value == null) { SerializerBinary.WriteByte(writer, 0); return; }

            // The tag chooses the width, so the payload of one value is not the same shape as another's
            // and a reader that ignored the frame would still see a self-describing value — which is
            // exactly the illusion the frame exists to remove.
            if (value.RecipeId % 2 == 0)
            {
                SerializerBinary.WriteByte(writer, CompactTag);
                SerializerBinary.WriteInt32(writer, value.RecipeId);      // zigzag varint: one byte per seven bits
            }
            else
            {
                SerializerBinary.WriteByte(writer, WideTag);
                SerializerBinary.WriteInt32Fixed(writer, value.RecipeId);
            }
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref RecipeRef value)
        {
            byte tag = SerializerBinary.ReadByte(ref reader);
            if (tag == 0) { value = null; return; }

            if (tag == CompactTag) value = new RecipeRef { RecipeId = SerializerBinary.ReadInt32(ref reader) };
            else if (tag == WideTag) value = new RecipeRef { RecipeId = SerializerBinary.ReadInt32Fixed(ref reader) };
            else throw new InvalidOperationException(
                $"v1's RecipeRefFormatter only wrote tags 0x{CompactTag:X2} and 0x{WideTag:X2} (and 0 for null); this save's payload starts with 0x{tag:X2}.");
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value)
            => Serialize(writer, (RecipeRef)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, System.Reflection.MemberInfo member)
        {
            RecipeRef value = null;
            Deserialize(ref reader, ref value);
            if (member is System.Reflection.FieldInfo field) field.SetValue(container, value);
            else if (member is System.Reflection.PropertyInfo property) property.SetValue(container, value);
            else throw new NotSupportedException(
                $"v1's RecipeRefFormatter writes through a field or a property; this save names '{member?.MemberType}'.");
        }
    }

    /// <summary>
    /// The shape a current build can still read: the custom-formatted member is still there, and the
    /// same formatter reads the block. The sentinel comes first in the member order, so a frame that is
    /// off by anything at all shows up as a wrong number rather than as a plausible value.
    /// </summary>
    [CerasContext("HistoricalV1Opaque")]
    [CerasSerializable]
    public class FrameHolder
    {
        public int ASentinel;

        /// <summary>A reference-typed custom payload, so this fixture also carries a reference marker
        /// in front of the frame.</summary>
        public RecipeRef Target;
    }

    /// <summary>
    /// The same shape with the custom-formatted member <em>gone</em> in the current model. This is the
    /// case the frame exists for: the bytes are accounted for by the declared length, so everything
    /// after them is still found, and the value is lost.
    /// </summary>
    [CerasContext("HistoricalV1Opaque")]
    [CerasSerializable]
    public class DroppedMemberHolder
    {
        public int ASentinel;

        /// <summary>Removed in the current model; its bytes have to be skipped by the declared length.</summary>
        public RecipeRef Target;
    }
}
