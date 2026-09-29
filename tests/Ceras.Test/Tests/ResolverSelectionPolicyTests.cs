// A formatter a *resolver* selects, in a robust context: the last door the opaque-payload machinery
// was not built around, and the one that still writes bytes no schema section describes.
//
// A user custom formatter's bytes are only describable when the manifest records the selection: the
// entry becomes `Opaque`, the payload gets a declared length in front of it, and the formatter's
// identity is hashed into the layout fingerprint (see RobustCustomFormatterPolicy and
// Format/LAYOUT.md, "A user custom formatter's payload is an opaque block"). `TypeConfig.CustomFormatter`
// gets that treatment because `CerasSerializer.WrapOpaqueIfSelected` asks the manifest at the moment the
// formatter is chosen for the type. The two resolver doors do not go through it:
//
//     GetSpecificFormatter(type, meta)
//       CustomFormatter != null  ->  WrapOpaqueIfSelected(...)              <- framed, recorded, hashed
//       CustomResolver  != null  ->  typeConfig.CustomResolver(this, type)   <- straight into the payload
//       foreach (_userResolvers)  ->  r(this, type)                          <- straight into the payload
//
// So a save through either door wrote the resolver's own bytes with no declared length, under a manifest
// entry that still advertised an ordinary class, and — the part that makes it undetectable — with the
// **same layout fingerprint a save with no formatter at all would carry**. Two builds whose resolvers
// answer differently therefore produced files claiming the same layout, and the second one read the
// first one's payload without being asked whether it could. (Measured on the unguarded build: baseline
// and resolver saves both fingerprinted `301D730F51413D4C`, 401 vs 398 bytes, same `CLASS` entry.)
//
// The type below is the shape that makes this reachable at all, and it is the shape a game has: an
// engine object the generator emits no formatter for (see Repros/GodotStyleRepro.cs). A type the
// generator *does* emit a formatter for is unreachable through a resolver door at all — its generated
// registration installs `CustomFormatter`, which is consulted first and is mutually exclusive with
// `CustomResolver` — so the hole is exactly the "no formatter of our own" case.
//
// The tests pin that divergence, the refusal on both the Save and the Load path, the late
// `OnConfigNewType` variant, and the three selections that must keep working: a raw context, which
// describes nothing; the context's own generated formatter, recognised by the identity rule
// `CustomFormatter` already uses; and the built-in resolvers Ceras installs itself.
//
// Both refusal paths throw `CerasSchemaException` naming the type, the resolver door and the formatter,
// and point at `TypeConfig.CustomFormatter` — the door that does work.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Formatters;
using Ceras.Generated;
using Ceras.ImmutableCollections;
using Xunit;

// Kept out of the generator, so the context's manifest holds a *minimal* entry for it ("a class with
// no members") and nothing ConfigTypes it with a formatter of the library's own. That is the shape an
// engine object has, and it is the only shape a resolver can be reached for: for any type the generator
// emits a formatter for, the generated registration installs `CustomFormatter`, which
// `GetSpecificFormatter` consults before both resolver doors — and which is mutually exclusive with
// `CustomFormatter`. Both facts are asserted below, so a generator that changed its mind would fail the
// tests rather than quietly turn this file into a test of something else.
[assembly: Ceras.CerasGeneratorBlacklist("Ceras.Test.ResolverSelection.EngineObject")]

namespace Ceras.Test.ResolverSelection
{
    /// <summary>The root of this file's context. It holds the payload, so a save walks into it.</summary>
    [CerasContext("ResolverSelection")]
    [CerasSerializable]
    public class Root
    {
        public int Sentinel;
        public EngineObject Payload;
    }

    /// <summary>
    /// The type a resolver selects a formatter for. It is blacklisted (see the assembly attribute
    /// above), so the generator emits no formatter for it and registers only a minimal manifest entry —
    /// which is precisely what makes the manifest's answer for this type a *member-less class* while
    /// the payload holds whatever the resolver chose to write.
    /// </summary>
    public class EngineObject
    {
        public int Id;
    }

    /// <summary>
    /// A root made entirely of the context's own generated formatters, used to show that a refused
    /// resolver selection leaves the rest of the serializer working.
    /// </summary>
    [CerasContext("ResolverSelection")]
    [CerasSerializable]
    public class Safe
    {
        public string Text;
        public int Count;
    }

    /// <summary>One encoding of <see cref="EngineObject"/>: a tag, then the id.</summary>
    public class PayloadFormatterA : IFormatter<EngineObject>
    {
        public const byte Tag = 0xA1;
        public int SerializeCalls;
        public int DeserializeCalls;

        public void Serialize(IBufferWriter<byte> writer, EngineObject value)
        {
            SerializeCalls++;
            SerializerBinary.WriteByte(writer, Tag);
            SerializerBinary.WriteInt32Fixed(writer, value.Id);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref EngineObject value)
        {
            DeserializeCalls++;
            byte tag = SerializerBinary.ReadByte(ref reader);
            if (tag != Tag)
                throw new InvalidOperationException(
                    $"PayloadFormatterA writes the tag 0x{Tag:X2}; this payload starts with 0x{tag:X2}. A payload written by a different " +
                    "formatter is not one of this one's — and nothing in the file said which formatter wrote it, which is the whole defect.");
            value = new EngineObject { Id = SerializerBinary.ReadInt32Fixed(ref reader) };
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (EngineObject)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            EngineObject value = null;
            Deserialize(ref reader, ref value);
            if (member is FieldInfo field) field.SetValue(container, value);
            else throw new NotSupportedException(
                $"A custom formatter for '{typeof(EngineObject)}' writes through a field; this save names a '{member?.MemberType}'.");
        }
    }

    /// <summary>
    /// A second, differently encoded formatter for the same type — same shape, same length, opposite
    /// meaning. It exists to show what the selection costs: an encoding in neither the schema section nor
    /// the fingerprint, producing a file whose layout claim is indistinguishable from another's.
    /// </summary>
    public class PayloadFormatterB : IFormatter<EngineObject>
    {
        public const byte Tag = 0xB1;

        public void Serialize(IBufferWriter<byte> writer, EngineObject value)
        {
            SerializerBinary.WriteByte(writer, Tag);
            SerializerBinary.WriteInt32Fixed(writer, ~value.Id);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref EngineObject value)
        {
            byte tag = SerializerBinary.ReadByte(ref reader);
            if (tag != Tag)
                throw new InvalidOperationException(
                    $"PayloadFormatterB writes the tag 0x{Tag:X2}; this payload starts with 0x{tag:X2}. Nothing in the file records that a " +
                    "different formatter wrote it, so only the tag byte itself catches this.");
            value = new EngineObject { Id = ~SerializerBinary.ReadInt32Fixed(ref reader) };
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (EngineObject)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            EngineObject value = null;
            Deserialize(ref reader, ref value);
            if (member is FieldInfo field) field.SetValue(container, value);
            else throw new NotSupportedException(
                $"A custom formatter for '{typeof(EngineObject)}' writes through a field; this save names a '{member?.MemberType}'.");
        }
    }

    /// <summary>
    /// A formatter that imitates a Ceras-shipped one: the same shape and the same generic arity, in this
    /// file's own namespace. It pins that the built-in approvals are decided by the formatter's
    /// <em>runtime type identity</em> — a <see cref="Type"/> Ceras never registered — and not by anything
    /// a caller can declare, imitate or name.
    /// </summary>
    public class PayloadFormatterLookalike : IFormatter<EngineObject>
    {
        public int SerializeCalls;
        public int DeserializeCalls;

        public void Serialize(IBufferWriter<byte> writer, EngineObject value)
        {
            SerializeCalls++;
            SerializerBinary.WriteInt32Fixed(writer, ~value.Id);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref EngineObject value)
        {
            DeserializeCalls++;
            value = new EngineObject { Id = ~SerializerBinary.ReadInt32Fixed(ref reader) };
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (EngineObject)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            EngineObject value = null;
            Deserialize(ref reader, ref value);
            if (member is FieldInfo field) field.SetValue(container, value);
        }
    }

    /// <summary>
    /// A formatter declaring the static <c>Schema</c> field the source generator gives every formatter
    /// class it emits, set to the instance this context's manifest holds for the type. That identity is
    /// what makes a formatter the context's own rather than a user payload, so this one is never framed
    /// and never refused — including when it arrives through a resolver.
    /// <para>
    /// It writes no bytes, which is exactly what the manifest's entry for a blacklisted type describes: a
    /// class with no members. The file is therefore self-consistent, which is as much as a
    /// hand-installed context-own formatter can be.
    /// </para>
    /// </summary>
    public class EngineOwnSchemaFormatter : IFormatter<EngineObject>
    {
        /// <summary>
        /// The generator's <c>internal static readonly SchemaTypeInfo Schema</c>. Read by reflection off
        /// the runtime type of the installed formatter, so only this exact member holding the exact
        /// instance the manifest holds is recognised as a generated formatter
        /// (<see cref="RobustCustomFormatterPolicy"/>).
        /// </summary>
        public static SchemaTypeInfo Schema;

        public int SerializeCalls;
        public int DeserializeCalls;

        public void Serialize(IBufferWriter<byte> writer, EngineObject value) => SerializeCalls++;

        public void Deserialize(ref SequenceReader<byte> reader, ref EngineObject value)
        {
            DeserializeCalls++;
            value = new EngineObject();
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (EngineObject)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            EngineObject value = null;
            Deserialize(ref reader, ref value);
            if (member is FieldInfo field) field.SetValue(container, value);
        }
    }

    /// <summary>
    /// A resolver that declines: returns null for everything, which is how a callback says "not mine,
    /// ask the next one". Ceras must treat that as *no selection*, not as a refused one — it is the
    /// documented contract of every <c>OnResolveFormatter</c> entry, and the immutable-collection
    /// resolver declines every type it does not handle.
    /// </summary>
    public class DecliningResolver
    {
        public int Calls;

        public IFormatter Answer(CerasSerializer serializer, Type type)
        {
            Calls++;
            return null;
        }
    }
}

namespace Ceras.Test.Tests
{
    using Ceras.Test.ResolverSelection;

    public class ResolverSelectionPolicyTests : TestBase
    {
        const string ContextName = "ResolverSelection";
        const int PayloadId = 0x2BADF00D;

        static Root Model(int sentinel = 7) => new Root { Sentinel = sentinel, Payload = new EngineObject { Id = PayloadId } };

        static ulong Fingerprint(byte[] save) => BitConverter.ToUInt64(save, RobustHeader.FingerprintOffset);

        static CerasRobustSerializer NewRobust(Action<CerasRobustConfig> configure = null)
            => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(ContextName);
                configure?.Invoke(c);
            });

        // ── the shape that makes this reachable ──────────────────────────────────────────────

        /// <summary>
        /// Precondition for everything below: the payload type is one the generator emits no formatter
        /// for, and the resolver doors are therefore the only ones that can supply one. Without this the
        /// tests would be passing for the wrong reason — a generated type's formatter is installed as
        /// <c>CustomFormatter</c> by the context registration and is consulted first, so
        /// <c>CustomResolver</c> could not even be set for it.
        /// </summary>
        [Fact]
        public void ThePayloadTypeHasNoGeneratedFormatterSoTheResolverDoorsAreReachable()
        {
            var serializer = NewRobust();
            var pick = typeof(CerasSerializer).GetMethod("GetSpecificFormatter",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(Type) }, null);

            var chosen = pick.Invoke(serializer, new object[] { typeof(EngineObject) });

            // Not a generated formatter, or a generated one named after the type: the generator emits
            // nothing for a blacklisted type, so nothing has installed `CustomFormatter` and the resolver
            // doors are the only ones that can supply this type's formatter at all.
            Assert.Equal("ReflectionFallbackFormatter`1", chosen.GetType().Name);
            Assert.Null(serializer.Config.GetExistingTypeConfig(typeof(EngineObject), false)?.CustomFormatter);
        }

        // ── the defect ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The divergence, and the reason it was undetectable. On the unguarded build a save whose
        /// formatter came from a resolver carried the <em>same layout fingerprint</em> as a save of the
        /// same graph with no formatter at all — measured, both <c>0x301D730F51413D4C</c> — under a
        /// <c>CLASS</c> entry that still described an ordinary class while the payload held the
        /// resolver's tag + id unframed. The reader therefore took the generated fast path and handed
        /// the payload to whichever resolver that build installed; the migration interpreter, the one
        /// layer that might have noticed the schema and the payload disagreeing, was never reached.
        /// <para>
        /// After the change there is no fingerprint left to compare: the save is refused before a byte
        /// of the graph is written, so the refusal <em>is</em> the assertion. The control save in the
        /// same test is what shows the refusal is about the resolver selection and nothing else.
        /// </para>
        /// </summary>
        [Fact]
        public void AResolverSelectedFormatterIsRefusedRatherThanWrittenUnframed()
        {
            var control = NewRobust();
            byte[] reference = control.Save(Model(), 1);

            var serializer = NewRobust(c => c.ConfigType<EngineObject>().CustomResolver = (s, t) => new PayloadFormatterA());
            Assert.Throws<CerasSchemaException>(() => serializer.Save(Model(), 1));

            // The control still saves and reads back, so the refusal above is about the resolver
            // selection and not about the context, the model or the save itself.
            Assert.NotEmpty(reference);
            Assert.Equal(PayloadId, control.Load<Root>(reference, 1).Payload.Id);
        }

        /// <summary>
        /// What that cost when the answer changed between builds: the same context, a second serializer
        /// whose resolver answers differently, a file read with a <em>matching</em> fingerprint — so a
        /// wrong value rather than a complaint. The two encodings here are the same shape and the same
        /// length, so the only thing that distinguishes them is a byte they happen to disagree about.
        /// </summary>
        [Fact]
        public void TwoSameShapeEncodingsDecodeEachOthersPayloadWithoutComplaint()
        {
            var scratch = new ArrayBufferWriter<byte>();
            new PayloadFormatterA().Serialize(scratch, new EngineObject { Id = PayloadId });
            byte[] written = scratch.WrittenSpan.ToArray();

            // The payload a build that resolves to formatter B reads: same length, same shape, and the
            // one byte that tells the two apart now says B. Nothing in a real file carries that byte's
            // meaning — which is the point: A wrote the file, and the file never said so.
            written[0] = PayloadFormatterB.Tag;
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(written));
            var decoded = new EngineObject();
            new PayloadFormatterB().Deserialize(ref reader, ref decoded);

            // Every byte is consumed and nothing is reported: the id comes back as its complement,
            // because B inverts what it reads. That is the whole damage — a complete, aligned, unremarked
            // read of a payload that means something else.
            Assert.Equal(0, reader.Remaining);
            Assert.Equal(~PayloadId, decoded.Id);
            Assert.NotEqual(PayloadId, decoded.Id);
        }

        // ── refusals: CustomResolver ─────────────────────────────────────────────────────────

        [Fact]
        public void ACustomResolverThatSelectsAUserFormatterIsRefusedOnSave()
        {
            var serializer = NewRobust(c => c.ConfigType<EngineObject>().CustomResolver = (s, t) => new PayloadFormatterA());

            var error = Assert.Throws<CerasSchemaException>(() => serializer.Save(Model(), 1));

            Assert.Contains(nameof(EngineObject), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(PayloadFormatterA), error.Message, StringComparison.Ordinal);
            Assert.Contains("CustomResolver", error.Message, StringComparison.Ordinal);
            Assert.Contains("CustomFormatter", error.Message, StringComparison.Ordinal);
        }

        // ── refusals: OnResolveFormatter ──────────────────────────────────────────────────────

        [Fact]
        public void AnOnResolveFormatterCallbackThatSelectsAUserFormatterIsRefusedOnSave()
        {
            var serializer = NewRobust(c =>
                c.OnResolveFormatter.Add((s, t) => t == typeof(EngineObject) ? new PayloadFormatterA() : null));

            var error = Assert.Throws<CerasSchemaException>(() => serializer.Save(Model(), 1));

            Assert.Contains(nameof(EngineObject), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(PayloadFormatterA), error.Message, StringComparison.Ordinal);
            Assert.Contains("OnResolveFormatter", error.Message, StringComparison.Ordinal);
            Assert.Contains("CustomFormatter", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The refusal is not a Save-only guard. A load resolves formatters through the same two lines, so
        /// the same selection is refused while <em>reading</em> — before the payload is interpreted.
        /// <para>
        /// The file is a plain save with no formatter at all, so this build's manifest holds no opaque
        /// selection for the type and the fingerprints match: the fast path is taken and the reading
        /// serializer's resolver is the only thing that can go wrong. Before the change the load succeeded
        /// and returned the complement of the saved id.
        /// </para>
        /// </summary>
        [Fact]
        public void TheLoadPathRefusesTheSameSelection()
        {
            byte[] plain = NewRobust().Save(Model(), 1);

            var reader = NewRobust(c => c.ConfigType<EngineObject>().CustomResolver = (s, t) => new PayloadFormatterLookalike());

            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<Root>(plain, 1));

            Assert.Contains(nameof(EngineObject), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(PayloadFormatterLookalike), error.Message, StringComparison.Ordinal);
            Assert.Contains("CustomResolver", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A file that <em>does</em> record an opaque selection is still refused by the check that already
        /// covered it — the recorded formatter's name against this build's — and the new guard is not what
        /// answers. Pinned so the two refusals cannot be confused for one another: the newer one is about a
        /// selection this build could never have recorded, the older one about a selection it did record and
        /// cannot match.
        /// </summary>
        [Fact]
        public void AFormatterNameMismatchIsStillRefusedByTheOpaqueCheckNotTheNewGuard()
        {
            byte[] framed = NewRobust(c => c.ConfigType<EngineObject>().CustomFormatter = new PayloadFormatterA()).Save(Model(), 1);

            var reader = NewRobust(c => c.ConfigType<EngineObject>().CustomFormatter = new PayloadFormatterB());

            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<Root>(framed, 1));

            // The opaque wording, not the resolver wording: this build does have a formatter for the type,
            // it is just not the one the file recorded.
            Assert.Contains("opaque block", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("selected by", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The late door: <c>OnConfigNewType</c> fires for a type the context was still discovering, from
        /// the middle of the operation, after the reconciliation at its top and after the bake. A
        /// <c>CustomResolver</c> assigned there lands after both, and is refused at the moment the
        /// formatter is chosen — before a byte of that value exists.
        /// </summary>
        [Fact]
        public void ALateResolverInstalledFromOnConfigNewTypeIsRefusedOnSave()
        {
            var serializer = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(ContextName);
                c.OnConfigNewType = tc =>
                {
                    if (tc.Type == typeof(EngineObject)) tc.CustomResolver = (s, t) => new PayloadFormatterA();
                };
            });

            var error = Assert.Throws<CerasSchemaException>(() => serializer.Save(Model(), 1));

            Assert.Contains(nameof(PayloadFormatterA), error.Message, StringComparison.Ordinal);
            Assert.Contains("CustomResolver", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ALateResolverInstalledFromOnConfigNewTypeIsRefusedOnLoadToo()
        {
            // A plain save, written with no formatter at all, so the reading serializer's late resolver is
            // the only thing that can go wrong. The lookalike is used because it has no tag to disagree
            // about: before the change this load succeeded and handed back a wrong id, which is the
            // failure mode the whole file is about.
            byte[] plain = NewRobust().Save(Model(), 1);

            var reader = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(ContextName);
                c.OnConfigNewType = tc =>
                {
                    if (tc.Type == typeof(EngineObject)) tc.CustomResolver = (s, t) => new PayloadFormatterLookalike();
                };
            });

            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<Root>(plain, 1));

            Assert.Contains(nameof(PayloadFormatterLookalike), error.Message, StringComparison.Ordinal);
            Assert.Contains("CustomResolver", error.Message, StringComparison.Ordinal);
        }

        // ── what must keep working ───────────────────────────────────────────────────────────

        /// <summary>
        /// A raw context describes nothing and accounts for nothing, so a resolver's bytes there are
        /// exactly what they have always been. This is the zero-raw-change requirement, pinned on both
        /// doors.
        /// </summary>
        [Fact]
        public void ARawContextStillHonoursBothResolverDoors()
        {
            var viaResolver = new CerasFastSerializer(c =>
            {
                c.AllowReflectionFallback = true;
                c.ConfigType<EngineObject>().CustomResolver = (s, t) => new PayloadFormatterA();
            });
            byte[] bytes = viaResolver.Serialize(Model());
            Assert.Equal(PayloadId, viaResolver.Deserialize<Root>(bytes).Payload.Id);

            var viaCallback = new CerasFastSerializer(c =>
            {
                c.AllowReflectionFallback = true;
                c.OnResolveFormatter.Add((s, t) => t == typeof(EngineObject) ? new PayloadFormatterA() : null);
            });
            byte[] other = viaCallback.Serialize(Model());
            Assert.Equal(PayloadId, viaCallback.Deserialize<Root>(other).Payload.Id);
        }

        /// <summary>
        /// A callback that <em>declines</em> — returns null, which is how every resolver says "not mine,
        /// ask the next one" — is not a selection and is not refused. The immutable-collection resolver
        /// declines every type it does not handle, so this is the ordinary case for any callback added
        /// alongside it.
        /// </summary>
        [Fact]
        public void AResolverThatReturnsNullIsADeclineNotASelection()
        {
            var declining = new DecliningResolver();
            var serializer = NewRobust(c => c.OnResolveFormatter.Add((s, t) => declining.Answer(s, t)));

            byte[] bytes = serializer.Save(Model(), 1);
            var clone = serializer.Load<Root>(bytes, 1);

            Assert.True(declining.Calls > 0, "the declining callback was never asked, so this proves nothing");
            // Nothing was selected for the payload, so the reflection fallback wrote it and the entry —
            // a member-less class — describes exactly that.
            Assert.Equal(PayloadId, clone.Payload.Id);
            Assert.DoesNotContain("OPAQUE", CerasRobustSerializer.DumpSchema(bytes), StringComparison.Ordinal);
        }

        /// <summary>
        /// The context's <em>own</em> formatter, selected through a resolver. The identity rule
        /// <c>TypeConfig.CustomFormatter</c> already uses applies unchanged: a formatter that declares the
        /// generator's static <c>Schema</c> field and holds the very instance this context's manifest
        /// holds for the type is the context's own — its bytes are what the entry describes, so nothing is
        /// recorded, nothing is framed and nothing is refused.
        /// </summary>
        [Fact]
        public void AResolverThatAnswersWithTheContextsOwnFormatterIsHonoured()
        {
            var own = new EngineOwnSchemaFormatter();
            var serializer = NewRobust(c =>
            {
                // Take the manifest entry for the type and hand it back as the declaration, which is
                // what a generated formatter does. The generated class itself is internal to this
                // compilation, so the test states the identity rather than naming it.
                Assert.True(c.SchemaRegistry.TryGetInfo(typeof(EngineObject), out var registered),
                    "the context's manifest holds no entry for the payload type, so the identity check has nothing to compare against");
                EngineOwnSchemaFormatter.Schema = registered;
                c.ConfigType<EngineObject>().CustomResolver = (s, t) => own;
            });

            byte[] bytes = serializer.Save(Model(), 1);
            var clone = serializer.Load<Root>(bytes, 1);

            // Its own encoding ran (it writes nothing, which is what the member-less entry describes),
            // and the entry stayed an ordinary class — nothing was framed.
            Assert.Equal(1, own.SerializeCalls);
            Assert.Equal(7, clone.Sentinel);
            Assert.DoesNotContain("OPAQUE", CerasRobustSerializer.DumpSchema(bytes), StringComparison.Ordinal);
        }

        /// <summary>
        /// The built-in resolvers Ceras installs itself are not user payloads, and one of them —
        /// <c>UseImmutableFormatters</c> — is the only resolver the shipped test suite runs in a robust
        /// context (<c>TestBase.Config_WithVersioning</c>). Its answers are approved by exact runtime type
        /// identity against the open generic formatter types the immutable-collections assembly registers,
        /// never by name, namespace or assembly string, so this keeps working while the refusals above
        /// stand.
        /// </summary>
        [Fact]
        public void TheBuiltInImmutableCollectionResolverStillWorksInARobustContext()
        {
            var serializer = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters("ImmutableResolverRoot");
                c.UseImmutableFormatters();
                c.DeclareRoot<System.Collections.Immutable.ImmutableList<int>>();
            });

            var bytes = serializer.Save(System.Collections.Immutable.ImmutableList.Create(4, 5, 6), 1);
            var clone = serializer.Load<System.Collections.Immutable.ImmutableList<int>>(bytes, 1);

            Assert.Equal(new[] { 4, 5, 6 }, clone);
            // Described, not opaque: the structural entry already says what these bytes are.
            Assert.DoesNotContain("OPAQUE", CerasRobustSerializer.DumpSchema(bytes), StringComparison.Ordinal);
        }

        /// <summary>
        /// The built-in approval is type identity, and this is what pins that. A user formatter of the
        /// same shape, in this file's own namespace, is refused through both doors — it is not one of the
        /// registered open generic types, and it is not the answer of a resolver Ceras installed itself.
        /// </summary>
        [Fact]
        public void AFormatterThatMerelyLooksLikeALibraryOneIsStillRefused()
        {
            var lookalike = new PayloadFormatterLookalike();

            var viaCallback = NewRobust(c =>
                c.OnResolveFormatter.Add((s, t) => t == typeof(EngineObject) ? lookalike : null));
            var callbackError = Assert.Throws<CerasSchemaException>(() => viaCallback.Save(Model(), 1));
            Assert.Contains(nameof(PayloadFormatterLookalike), callbackError.Message, StringComparison.Ordinal);

            var viaResolver = NewRobust(c => c.ConfigType<EngineObject>().CustomResolver = (s, t) => lookalike);
            var resolverError = Assert.Throws<CerasSchemaException>(() => viaResolver.Save(Model(), 1));
            Assert.Contains(nameof(PayloadFormatterLookalike), resolverError.Message, StringComparison.Ordinal);

            Assert.Equal(0, lookalike.SerializeCalls);
        }

        /// <summary>
        /// The approval for Ceras' own force-dynamic resolver (<c>TypeConfigDefaults</c>, which installs a
        /// <c>CustomResolver</c> for <c>Version</c>, an <c>Expression</c> and
        /// <c>TrueReadOnlyCollection&lt;&gt;</c>) is scoped to <em>that resolver</em>, not to the formatter
        /// type it answers with. <c>ReflectionFallbackFormatter&lt;T&gt;</c> is an
        /// <c>internal sealed</c> class, which stops a caller from naming it — and stops nothing else: a
        /// caller can construct one for a type of its own choosing by reflection, and a formatter-type
        /// exemption would accept exactly that. Scoping the approval to the library-installed resolver
        /// means a user resolver handing back such a formatter is still refused, which this asserts.
        /// </summary>
        [Fact]
        public void AUserResolverReturningTheForcedDynamicAnswerIsStillRefused()
        {
            var serializer = NewRobust(c =>
                c.OnResolveFormatter.Add((s, t) => t == typeof(EngineObject) ? ReflectionFallbackOf(s, typeof(EngineObject)) : null));

            var error = Assert.Throws<CerasSchemaException>(() => serializer.Save(Model(), 1));

            Assert.Contains(nameof(EngineObject), error.Message, StringComparison.Ordinal);
            Assert.Contains("OnResolveFormatter", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The library's own <c>CustomResolver</c> keeps working. <c>TypeConfigDefaults</c> installs one for
        /// <c>Version</c>, an <c>Expression</c> and <c>TrueReadOnlyCollection&lt;&gt;</c> — types it forces
        /// into reflection-based serialization — and the guard exempts it, because it is the library's own
        /// delegate installed by the library. It has to stay exempt: the resolver is reached, answers, and
        /// the save is then refused by the layer that always refused it, because the type is not part of
        /// this context's manifest. That refusal is unchanged, and this pins it — a guard that refused the
        /// library's resolver here would replace a long-standing message about an undeclared type with a
        /// new one about a resolver, for no gain.
        /// </summary>
        [Fact]
        public void TheLibrarysOwnForcedDynamicResolverIsExemptAndKeepsItsExistingRefusal()
        {
            var serializer = NewRobust(c => c.AllowReflectionFallback = true);
            var model = new VersionSlot { Slot = new Version(1, 2, 3), Tail = 5 };

            var error = Record.Exception(() => serializer.Save(model, 1));

            Assert.NotNull(error);
            Assert.DoesNotContain("selected by", error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(Version), error.Message, StringComparison.Ordinal);
        }

        // ── the approval table is read and written from different threads ────────────────────

        /// <summary>
        /// Registering an approved formatter type and reading the table happen on different threads: the
        /// registration comes from a friend assembly's type initializer, once, while the read happens on
        /// whatever thread is resolving a formatter — and several serializers may be resolving at once.
        /// This drives both at once and asserts the two properties that matter: no thread sees a broken
        /// table, and every save and load through the approved resolver still succeeds while a
        /// registration is in flight.
        /// <para>
        /// <b>What this does and does not prove.</b> It is a smoke test for the read path under concurrent
        /// use, not a detector for an unsynchronized table. It was measured against an unsynchronized
        /// <c>HashSet</c> and passed there too, for two reasons worth recording rather than hiding: a
        /// duplicate <c>Add</c> does not resize the set, so nothing observable moves; and the writes are
        /// already serialized with each other by the CLR's type-initialization lock. What the copy-on-write
        /// snapshot buys is that the <em>only</em> remaining window — one write against many reads — cannot
        /// expose a half-updated collection at all, and that is by construction rather than by this test.
        /// </para>
        /// <para>
        /// The registrations are the eight the immutable-collections assembly makes, repeated, and repeating
        /// them is a no-op by design — which is what keeps this test from approving anything of its own and
        /// so from leaking into the tests running beside it.
        /// </para>
        /// </summary>
        [Fact]
        public void TheApprovalTableIsSafeToReadWhileItIsBeingWritten()
        {
            var failures = new List<Exception>();
            var barrier = new Barrier(NReaders + 1);

            void Guarded(Action body)
            {
                try { body(); }
                catch (Exception e) { lock (failures) failures.Add(e); }
            }

            // Writers: re-register exactly what Ceras.ImmutableCollections registers. Idempotent, so the
            // table's contents are the same before and after and no test can be affected by this.
            var writer = new Thread(() => Guarded(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < 200; i++)
                    foreach (var open in ImmutableFormatterTypes)
                        Ceras.Format.RobustResolverPolicy.RegisterApprovedFormatterType(open);
            }));

            var readers = new List<Thread>();
            for (int n = 0; n < NReaders; n++)
            {
                var thread = new Thread(() => Guarded(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < 40; i++)
                    {
                        var serializer = new CerasRobustSerializer(c =>
                        {
                            c.UseGeneratedFormatters("ImmutableResolverRoot");
                            c.UseImmutableFormatters();
                            c.DeclareRoot<System.Collections.Immutable.ImmutableList<int>>();
                        });

                        var bytes = serializer.Save(System.Collections.Immutable.ImmutableList.Create(1, 2, 3), 1);
                        var clone = serializer.Load<System.Collections.Immutable.ImmutableList<int>>(bytes, 1);

                        // The approved resolver answered: the value came back, and nothing was framed.
                        Assert.Equal(new[] { 1, 2, 3 }, clone);
                    }
                }));
                readers.Add(thread);
            }

            writer.Start();
            foreach (var t in readers) t.Start();
            foreach (var t in readers) t.Join();
            writer.Join();

            Assert.Empty(failures);
        }

        const int NReaders = 4;

        /// <summary>
        /// The open generic definitions <c>Ceras.ImmutableCollections</c> registers, restated here so the
        /// concurrency test can re-register them without referencing that assembly's formatter types
        /// directly (and so a change to that list shows up as a change to this one).
        /// </summary>
        static readonly Type[] ImmutableFormatterTypes =
        {
            typeof(Ceras.ImmutableCollections.ImmutableArrayFormatter<>),
            typeof(Ceras.ImmutableCollections.ImmutableDictionaryFormatter<,>),
            typeof(Ceras.ImmutableCollections.ImmutableHashSetFormatter<>),
            typeof(Ceras.ImmutableCollections.ImmutableListFormatter<>),
            typeof(Ceras.ImmutableCollections.ImmutableQueueFormatter<>),
            typeof(Ceras.ImmutableCollections.ImmutableSortedDictionaryFormatter<,>),
            typeof(Ceras.ImmutableCollections.ImmutableSortedSetFormatter<>),
            typeof(Ceras.ImmutableCollections.ImmutableStackFormatter<>),
        };

        // ── a refused selection must leave nothing cached ───────────────────────────────────

        /// <summary>
        /// The refusal has to leave the serializer exactly as it found it. A resolved formatter is cached
        /// on the type's metadata, and the next lookup returns that cache without consulting anything —
        /// so a refusal raised <em>after</em> the cache was written leaves the refused formatter sitting in
        /// it, and the second save on the same serializer then takes the cache, skips the guard entirely,
        /// and writes the very payload the first one refused. This is the shape of that bypass, and it is
        /// why the guard runs before the cache is written rather than after.
        /// </summary>
        [Fact]
        public void ARefusedSaveLeavesNothingCachedSoASecondSaveIsRefusedToo()
        {
            var formatter = new PayloadFormatterA();
            var serializer = NewRobust(c => c.ConfigType<EngineObject>().CustomResolver = (s, t) => formatter);

            var first = Assert.Throws<CerasSchemaException>(() => serializer.Save(Model(), 1));
            var second = Assert.Throws<CerasSchemaException>(() => serializer.Save(Model(), 1));

            Assert.Equal(first.Message, second.Message);

            // The refusal happens before the value has a byte, so the formatter was asked for and never
            // invoked. A second save that had slipped past the guard would have invoked it.
            Assert.Equal(0, formatter.SerializeCalls);
        }

        /// <summary>
        /// The same on the read side, and it is the sharper half: a load refused for a resolver selection
        /// must not leave that selection cached, or the second load hands the payload to the formatter the
        /// first one rejected.
        /// </summary>
        [Fact]
        public void ARefusedLoadLeavesNothingCachedSoASecondLoadIsRefusedToo()
        {
            byte[] plain = NewRobust().Save(Model(), 1);

            var formatter = new PayloadFormatterLookalike();
            var reader = NewRobust(c => c.ConfigType<EngineObject>().CustomResolver = (s, t) => formatter);

            var first = Assert.Throws<CerasSchemaException>(() => reader.Load<Root>(plain, 1));
            var second = Assert.Throws<CerasSchemaException>(() => reader.Load<Root>(plain, 1));

            Assert.Equal(first.Message, second.Message);
            Assert.Equal(0, formatter.DeserializeCalls);
        }

        /// <summary>
        /// The same reuse question on the callback door. It answers itself differently today and has to
        /// keep doing so: this loop already writes the cache after the guard, so a refusal here leaves
        /// nothing behind either. Pinned separately because the two doors are two separate edits, and one
        /// of them can be right while the other is not.
        /// </summary>
        [Fact]
        public void ARefusedCallbackSaveLeavesNothingCachedSoASecondSaveIsRefusedToo()
        {
            var formatter = new PayloadFormatterA();
            var serializer = NewRobust(c =>
                c.OnResolveFormatter.Add((s, t) => t == typeof(EngineObject) ? formatter : null));

            var first = Assert.Throws<CerasSchemaException>(() => serializer.Save(Model(), 1));
            var second = Assert.Throws<CerasSchemaException>(() => serializer.Save(Model(), 1));

            Assert.Equal(first.Message, second.Message);
            Assert.Equal(0, formatter.SerializeCalls);
        }

        /// <summary>
        /// A refusal is scoped to the selection that caused it, not to the serializer. The rest of the
        /// context keeps working on the very serializer that just refused, so the guard is a statement
        /// about one formatter rather than a serializer left unusable.
        /// </summary>
        [Fact]
        public void ARefusedSelectionDoesNotPoisonTheRestOfTheSerializer()
        {
            var serializer = NewRobust(c =>
            {
                c.AllowReflectionFallback = true;
                c.ConfigType<EngineObject>().CustomResolver = (s, t) => new PayloadFormatterA();
            });

            Assert.Throws<CerasSchemaException>(() => serializer.Save(Model(), 1));

            // A root whose types are all the context's own: no resolver selection is involved at all.
            var clean = new Safe { Text = "still fine", Count = 3 };
            byte[] bytes = serializer.Save(clean, 1);
            var clone = serializer.Load<Safe>(bytes, 1);

            Assert.Equal(clean.Text, clone.Text);
            Assert.Equal(clean.Count, clone.Count);
        }

        /// <summary>
        /// Builds the very formatter <c>TypeConfigDefaults.ForceDynamicResolver</c> answers with, for the
        /// given type, through the same reflection the library uses. That type is
        /// <c>internal sealed</c>, so it can be neither named nor constructed by name here — which is
        /// exactly why this goes through reflection, and why scoping the approval to the resolver rather
        /// than to the formatter type is what closes it.
        /// </summary>
        static IFormatter ReflectionFallbackOf(CerasSerializer serializer, Type type)
        {
            var open = typeof(CerasSerializer).Assembly.GetType("Ceras.Formatters.ReflectionFallbackFormatter`1", throwOnError: true);
            var closed = open.MakeGenericType(type);
            return (IFormatter)Activator.CreateInstance(closed,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: new object[] { serializer, false }, culture: null);
        }

        // ── the supported door is untouched ──────────────────────────────────────────────────

        /// <summary>
        /// The change is scoped to the resolver doors. <c>TypeConfig.CustomFormatter</c> is the supported
        /// way to install a user formatter in a robust context, and it still works exactly as v5 made it:
        /// framed with a declared length, recorded as an <c>Opaque</c> entry carrying the formatter's name,
        /// and refused on a load whose build has a different formatter. Nothing here is turned into a
        /// refusal.
        /// </summary>
        [Fact]
        public void TheSupportedCustomFormatterDoorIsUnaffected()
        {
            var writer = NewRobust(c => c.ConfigType<EngineObject>().CustomFormatter = new PayloadFormatterA());
            byte[] bytes = writer.Save(Model(), 1);

            var dump = CerasRobustSerializer.DumpSchema(bytes);
            Assert.Contains("OPAQUE", dump, StringComparison.Ordinal);
            Assert.Contains(nameof(PayloadFormatterA), dump, StringComparison.Ordinal);

            Assert.Equal(PayloadId, writer.Load<Root>(bytes, 1).Payload.Id);

            // A build with a different formatter is told so, by name, rather than decoding.
            var reader = NewRobust(c => c.ConfigType<EngineObject>().CustomFormatter = new PayloadFormatterB());
            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<Root>(bytes, 1));
            Assert.Contains(nameof(PayloadFormatterA), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(PayloadFormatterB), error.Message, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// The root for the built-in-resolver test, in its own context so that declaring
/// <c>ImmutableList&lt;int&gt;</c> as a root shape cannot leak into any other file's manifest.
/// </summary>
[Ceras.CerasContext("ImmutableResolverRoot")]
[Ceras.CerasSerializable]
public class ImmutableResolverRoot
{
    public int Tail;
}

/// <summary>
/// Holds a value of a type the library forces into reflection-based serialization, through a slot. The
/// member is declared <c>object</c> rather than <c>Version</c> because the source generator refuses a
/// declared <c>System.*</c> member a robust context cannot describe (CERAS029) — which is the generator
/// doing this file's job one step earlier, leaving the slot as the only way the type is met at all.
/// </summary>
[Ceras.CerasContext("ResolverSelection")]
[Ceras.CerasSerializable]
public class VersionSlot
{
    public object Slot;
    public int Tail;
}