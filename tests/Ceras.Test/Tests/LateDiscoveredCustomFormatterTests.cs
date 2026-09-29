// A user custom formatter installed for a type the context is *discovering* — the last door, and the
// one place a v5 save could still be written that says nothing about its own payload.
//
// A custom formatter's bytes are only describable when the manifest records the selection: the entry
// becomes `Opaque`, the payload gets a declared length, and the formatter's identity is hashed into the
// layout fingerprint (see RobustCustomFormatterPolicy and Format/LAYOUT.md). `Save` and `Load` therefore
// run `RobustCustomFormatterPolicy.Reconcile` before they touch anything — but Reconcile can only see
// what the *config* holds, and a type Ceras has not reached yet has no `TypeConfig` at all. The one door
// that creates one after the config was sealed is `OnConfigNewType`, which fires for exactly such a type,
// from the middle of the operation, after that Reconcile and after the manifest has been sealed and
// baked:
//
//     Save  ->  Reconcile  ->  EnsureRootDeclared  ->  registry.Seal()  ->  write the graph
//                                                                             ^ OnConfigNewType fires here
//                                                                             ^ the manifest is already baked
//
// Before the fix, `CerasSerializer.WrapOpaqueIfSelected` asked the manifest whether the type was an
// opaque selection, was told no, and returned the user's formatter *unwrapped*. The payload then went
// into the file with no declared length, the file's schema described an ordinary class, and the
// fingerprint was the one the file would have had with no formatter at all — which is exactly the state
// v5 exists to make impossible, produced by the one path nobody looked at. The second `Save` on the same
// serializer already refused (its Reconcile sees the assignment), so the bad file was the *first* one,
// and it was the only one.
//
// The tests below pin the window itself (it is real, and it is where the callback runs), the refusal on
// both the Save and the Load path, that nothing is committed when it is refused, and the two paths that
// must keep working: a raw context, which describes nothing, and a late install the identity rule
// recognises as the context's own.
using System;
using System.Buffers;
using System.Linq;
using System.Reflection;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Formatters;
using Ceras.Generated;
using Xunit;

// The late type is kept out of the generator's discovery, so it gets no formatter of the library's own.
// This is the shape an engine type has (see Repros/GodotStyleRepro.cs: a GodotObject subclass, or any type
// the project excludes from generation with CerasGeneratorBlacklist), and it is the shape this file needs:
// a type the context's manifest describes, but which nothing ConfigTypes up front. Both the exclusion and
// its consequence are asserted by the tests below, so a generator that changed its mind about a
// blacklisted type would fail them rather than quietly turn this file into a test of something else.
[assembly: CerasGeneratorBlacklist("Ceras.Test.LateDiscovered.Late")]

namespace Ceras.Test.LateDiscovered
{
    /// <summary>The root of this file's context. It holds the late type, so a save walks into it.</summary>
    [CerasContext("LateDiscovered")]
    [CerasSerializable]
    public class Root
    {
        public int Sentinel;
        public Late Late;
    }

    /// <summary>
    /// The late type, and the whole point of it: a type no declaration names and that the generator
    /// generates no formatter for (the <c>CerasGeneratorBlacklist</c> above).
    /// <para>
    /// Such a type gets a <em>minimal</em> manifest entry — "a class with no members" — so the bake can
    /// resolve the member's type id, and it is deliberately <em>not</em> configured: nothing calls
    /// <c>ConfigType</c> for it. Its <c>TypeConfig</c>, and with it the <c>OnConfigNewType</c> callback, is
    /// therefore created the first time a save or load reaches it — inside the operation, which is the
    /// window this file is about. A generated formatter would be installed up front instead, by the
    /// context's registration, and the window would not exist.
    /// </para>
    /// </summary>
    public class Late
    {
        public int Id;
    }

    /// <summary>One encoding of <see cref="Late"/>: its own tag, then the id.</summary>
    public class LateFormatterA : IFormatter<Late>
    {
        public const byte Tag = 0xA1;
        public int SerializeCalls;
        public int DeserializeCalls;

        public void Serialize(IBufferWriter<byte> writer, Late value)
        {
            SerializeCalls++;
            SerializerBinary.WriteByte(writer, Tag);
            SerializerBinary.WriteInt32Fixed(writer, value.Id);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref Late value)
        {
            DeserializeCalls++;
            byte tag = SerializerBinary.ReadByte(ref reader);
            if (tag != Tag)
                throw new InvalidOperationException(
                    $"LateFormatterA writes the tag 0x{Tag:X2}; this payload starts with 0x{tag:X2}. A payload written by a " +
                    "different formatter, or by Ceras' own reflection fallback, is not one of this one's.");
            value = new Late { Id = SerializerBinary.ReadInt32Fixed(ref reader) };
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Late)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            Late value = null;
            Deserialize(ref reader, ref value);
            LateValues.WriteBack(container, member, value);
        }
    }

    /// <summary>
    /// A second, differently encoded formatter for the same type. It exists to show what a selection
    /// costs the file: an encoding that is in neither the schema section nor the fingerprint is a file
    /// whose layout claims to be the same as another's.
    /// </summary>
    public class LateFormatterB : IFormatter<Late>
    {
        public const byte Tag = 0xB1;

        public void Serialize(IBufferWriter<byte> writer, Late value)
        {
            SerializerBinary.WriteByte(writer, Tag);
            SerializerBinary.WriteInt32Fixed(writer, ~value.Id);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref Late value)
        {
            byte tag = SerializerBinary.ReadByte(ref reader);
            if (tag != Tag)
                throw new InvalidOperationException($"LateFormatterB writes the tag 0x{Tag:X2}; this payload starts with 0x{tag:X2}.");
            value = new Late { Id = ~SerializerBinary.ReadInt32Fixed(ref reader) };
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Late)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            Late value = null;
            Deserialize(ref reader, ref value);
            LateValues.WriteBack(container, member, value);
        }
    }

    /// <summary>
    /// A formatter holding the <c>Schema</c> field the generator gives every formatter class, set to the
    /// very instance this context's manifest holds for <see cref="Late"/>. That identity is what makes a
    /// formatter the context's own rather than a user payload, so this one is never framed and never
    /// refused — including when it is installed from the late callback, which is the point.
    /// <para>
    /// It writes no bytes, and that is what the manifest's entry for <see cref="Late"/> describes: a class
    /// with no members. The file is therefore self-consistent, which is as much as a hand-installed
    /// context-own formatter can be.
    /// </para>
    /// </summary>
    public class LateOwnSchemaFormatter : IFormatter<Late>
    {
        /// <summary>
        /// The static <c>Schema</c> field the generator puts on every formatter class it emits, holding the
        /// manifest entry for the type. Only this exact member, holding the exact instance the manifest
        /// holds, is recognised as a generated formatter (RobustCustomFormatterPolicy).
        /// </summary>
        public static SchemaTypeInfo Schema;

        public int SerializeCalls;
        public int DeserializeCalls;

        public void Serialize(IBufferWriter<byte> writer, Late value) => SerializeCalls++;

        public void Deserialize(ref SequenceReader<byte> reader, ref Late value)
        {
            DeserializeCalls++;
            value = new Late();
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Late)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            Late value = null;
            Deserialize(ref reader, ref value);
            LateValues.WriteBack(container, member, value);
        }
    }

    /// <summary>
    /// The write-through a container/member read is, shared by the three formatters above. The late type
    /// is only ever written through a field (the member of <see cref="Root"/>), so anything else is named
    /// here rather than reported as a cast the caller cannot act on.
    /// </summary>
    internal static class LateValues
    {
        public static void WriteBack(object container, MemberInfo member, Late value)
        {
            if (member is FieldInfo field) { field.SetValue(container, value); return; }
            throw new NotSupportedException(
                $"A custom formatter for '{typeof(Late)}' writes through a field; this save names a '{member?.MemberType}'.");
        }
    }
}

namespace Ceras.Test.Tests
{
    using Ceras.Test.LateDiscovered;

    public class LateDiscoveredCustomFormatterTests : TestBase
    {
        const string ContextName = "LateDiscovered";
        const int PayloadId = 0x2BADF00D;

        static Root Model(int sentinel = 7, int id = PayloadId) => new Root { Sentinel = sentinel, Late = new Late { Id = id } };

        // ── the window ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// That there is a window at all, and where in the operation it sits. Everything else here
        /// depends on it: the callback has to run <em>after</em> the reconciliation at the top of the save
        /// and <em>after</em> the manifest was sealed and baked, or the refusals below would be testing
        /// nothing — a formatter installed before the first save is reconciled, framed and hashed like any
        /// other, which is <see cref="TheSameFormatterInstalledBeforeTheSerializerExistsIsFramed"/>.
        /// <para>
        /// The save's outcome is deliberately not asserted here: this test is about the window, so it has
        /// to keep passing whatever the save does. The outcome is asserted in the two tests below.
        /// </para>
        /// </summary>
        [Fact]
        public void TheCallbackForTheLateTypeRunsAfterTheReconcileAtTheTopOfTheSaveAndAfterTheBake()
        {
            SchemaRegistry registry = null;
            bool? sealedWhenCallbackRan = null, selectedWhenCallbackRan = null;
            int callbacks = 0;

            var serializer = NewRobust(c =>
            {
                registry = c.SchemaRegistry;
                c.OnConfigNewType = tc =>
                {
                    if (tc.Type != typeof(Late)) return;
                    // Asked *before* the assignment, so what is recorded is the manifest the callback
                    // found rather than one the assignment went on to affect.
                    sealedWhenCallbackRan = registry.IsSealed;
                    selectedWhenCallbackRan = registry.IsOpaqueSelection(typeof(Late));
                    callbacks++;
                    tc.CustomFormatter = new LateFormatterA();
                };
            });

            // Nothing configured the late type and nothing has fired the callback for it: the generated
            // registration configures the types it owns, and this one is not one of them.
            Assert.Null(serializer._config.GetExistingTypeConfig(typeof(Late), false));
            Assert.Equal(0, callbacks);

            Record.Exception(() => serializer.Save(Model(), 1));

            Assert.Equal(1, callbacks);
            Assert.True(sealedWhenCallbackRan,
                "the callback ran before the manifest was sealed, so this is not the window the refusal is about — the " +
                "reconciliation at the top of the save would have seen the formatter");
            Assert.False(selectedWhenCallbackRan,
                "the manifest already held an opaque selection for the late type, so the payload was describable without a new reconciliation");
        }

        /// <summary>
        /// The control for the test above, and for the refusals: the same formatter, the same type, the
        /// same context — installed before the serializer exists. That selection the reconciliation
        /// <em>can</em> see, so the entry becomes opaque, the payload is framed, and the formatter's
        /// identity is in the fingerprint. Two formatters are two layouts.
        /// </summary>
        [Fact]
        public void TheSameFormatterInstalledBeforeTheSerializerExistsIsFramed()
        {
            var formatterA = new LateFormatterA();
            var formatterB = new LateFormatterB();
            var withA = NewRobust(c => c.ConfigType<Late>().CustomFormatter = formatterA);
            var withB = NewRobust(c => c.ConfigType<Late>().CustomFormatter = formatterB);

            var a = withA.Save(Model(), 1);
            var b = withB.Save(Model(), 1);

            // The declared length in front of each payload, and each payload's own bytes behind it.
            Assert.Equal(5, DeclaredLengthInFrontOf(a, PayloadOf(LateFormatterA.Tag, PayloadId)));
            Assert.Equal(5, DeclaredLengthInFrontOf(b, PayloadOf(LateFormatterB.Tag, ~PayloadId)));

            // The manifest says which formatter wrote it, ...
            Assert.Contains(FormatterName(formatterA), OpaqueLine(a), StringComparison.Ordinal);
            Assert.Contains(FormatterName(formatterB), OpaqueLine(b), StringComparison.Ordinal);

            // ... and the fingerprint moves with it, so the two files are not the same layout.
            Assert.NotEqual(Fingerprint(a), Fingerprint(b));
        }

        // ── the refusal ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The defect. Before the fix this save <em>succeeded</em> and returned a file whose schema
        /// described an ordinary class with no members, whose payload was the formatter's five unframed
        /// bytes, and whose layout fingerprint was the one the file would have had with no custom
        /// formatter at all. Now it is refused, naming both the type and the formatter, and no file comes
        /// back: a refused save discards its buffer rather than returning a partial one.
        /// </summary>
        [Fact]
        public void ASaveThatSelectsAUserFormatterForATypeItIsDiscoveringIsRefusedAndCommitsNothing()
        {
            var model = Model();
            var serializer = NewRobust(Selecting(new LateFormatterA()));

            int liveBeforeRefused = liveRentals();
            var error = Assert.Throws<CerasSchemaException>(() => serializer.Save(model, 1));
            int liveAfterRefused = liveRentals();

            Assert.Contains(TypeIdentity.GetCanonicalName(typeof(Late)), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(LateFormatterA), error.Message, StringComparison.Ordinal);
            Assert.Contains("sealed and baked", error.Message, StringComparison.Ordinal);

            // Nothing was committed, and the buffer the save rented is back in the pool: measured against
            // a save of the same graph that is *allowed* to finish, because that is the only comparison
            // that means anything here. (A context's first save always adds one rental whatever it does -
            // the baked schema section is a pooled array the registry keeps for the context's life, and
            // the pool's rental counter never learns that it is not coming back. A refused save that also
            // left a rented array behind, or handed one back twice, would show up as a larger or smaller
            // delta than the control's.)
            int liveBeforeControl = liveRentals();
            NewRobust(c => c.ConfigType<Late>().CustomFormatter = new LateFormatterA()).Save(model, 1);
            int liveAfterControl = liveRentals();

            Assert.Equal(liveAfterControl - liveBeforeControl, liveAfterRefused - liveBeforeRefused);
        }

        /// <summary>
        /// ...and on the other door. The load is the mirror of the save: the same reconciliation at the
        /// top, the same bake, and the same callback firing in the middle of the operation. The file here
        /// is a legitimate one — a context that installs no formatter for the late type and drops its one
        /// member, so the payload's member list is empty and the manifest's member-less entry describes it
        /// exactly (see <see cref="AWriterWhoseMemberListIsDescribedCanProduceTheFile"/>) — and a reader
        /// that would install a formatter while reading it is refused for the same reason a writer is.
        /// </summary>
        [Fact]
        public void ALoadThatSelectsAUserFormatterForATypeItIsDiscoveringIsRefused()
        {
            var bytes = NewRobust(AWriterWhoseMemberListIsDescribed).Save(Model(), 1);
            var reader = NewRobust(Selecting(new LateFormatterA()));

            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<Root>(bytes, 1));

            Assert.Contains(TypeIdentity.GetCanonicalName(typeof(Late)), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(LateFormatterA), error.Message, StringComparison.Ordinal);

            // A refused load hands back no value, and leaves the serializer able to say the same thing
            // again (TheRefusalRepeats…): a load has no output buffer of its own, so the only thing that
            // could be left behind is state, and a reader that had half-consumed the file would fail the
            // next one differently.
            Assert.Throws<CerasSchemaException>(() => reader.Load<Root>(bytes, 1));
        }

        /// <summary>
        /// The planted file, on its own. A robust context refuses the reflection fallback for a type whose
        /// reflected member list is not the one its manifest entry describes, and the generator registers a
        /// <em>member-less</em> entry for the late type (it emits no formatter for it), so the plain
        /// "nothing configured, save the model" context of this file can no longer write a value of it at
        /// all. Dropping the one member makes the two lists agree — an empty member list is what a
        /// member-less entry describes — which is what a file this file's tests can be built from looks
        /// like.
        /// <para>
        /// Stated as its own test because it is a fact about the door rather than about a formatter: the
        /// same context with the member kept is refused, and the refusal names the divergence.
        /// </para>
        /// </summary>
        [Fact]
        public void AWriterWhoseMemberListIsDescribedCanProduceTheFile()
        {
            byte[] bytes = NewRobust(AWriterWhoseMemberListIsDescribed).Save(Model(), 1);

            // The value is there, and the schema section says the type has no members — so the file
            // describes its own payload.
            var round = NewRobust(AWriterWhoseMemberListIsDescribed).Load<Root>(bytes, 1);
            Assert.Equal(Model().Sentinel, round.Sentinel);
            Assert.NotNull(round.Late);
            Assert.Equal(0, round.Late.Id);

            // With the member kept, the same context is refused: the payload would carry a member the
            // member-less entry does not describe.
            var error = Assert.Throws<CerasSchemaException>(() => NewRobust().Save(Model(), 1));
            Assert.Contains(TypeIdentity.GetCanonicalName(typeof(Late)), error.Message, StringComparison.Ordinal);
            Assert.Contains("reflection fallback", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A refusal is not a state the serializer can be talked out of, and it does not damage it. The
        /// same serializer refuses the same graph the same way, on both doors, and a <em>different</em>
        /// serializer over the same context still saves and loads normally — the context is what is caught,
        /// not the process.
        /// </summary>
        [Fact]
        public void TheRefusalRepeats_AndTheSerializerStaysUsable()
        {
            var serializer = NewRobust(Selecting(new LateFormatterA()));
            var model = Model();

            var first = Assert.Throws<CerasSchemaException>(() => serializer.Save(model, 1));
            int liveAfterFirst = liveRentals();

            var second = Assert.Throws<CerasSchemaException>(() => serializer.Save(model, 1));
            Assert.Equal(first.Message, second.Message);
            Assert.Equal(liveAfterFirst, liveRentals());

            // And the load on the same serializer refuses too, rather than reading the file this change
            // would otherwise have let it write.
            var bytes = NewRobust(AWriterWhoseMemberListIsDescribed).Save(model, 1);
            Assert.Throws<CerasSchemaException>(() => serializer.Load<Root>(bytes, 1));

            // A context that configures nothing for the late type is untouched by any of it. It drops the
            // late type's member like the planted file above, because that is the only shape this context
            // can write for a type whose manifest entry is a member-less one.
            var clean = NewRobust(AWriterWhoseMemberListIsDescribed);
            var round = clean.Load<Root>(clean.Save(model, 1), 1);
            Assert.Equal(model.Sentinel, round.Sentinel);
            Assert.Equal(0, round.Late.Id);
        }

        // ── what must keep working ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The context's own formatter is not a user payload, however late it is installed. The identity
        /// rule is asked at the moment the formatter is chosen exactly as it is at the reconciliation, and
        /// a formatter holding the manifest's own <c>Schema</c> instance is the context's own: nothing is
        /// framed, nothing is refused, and the round trip works.
        /// </summary>
        [Fact]
        public void ALateInstallThatIsTheContextsOwnFormatterIsNeitherFramedNorRefused()
        {
            var formatter = new LateOwnSchemaFormatter();

            // The very instance *this* context's manifest holds, read out of this context's own registry
            // after its generated manifest is registered and before the callback is installed. Read from
            // another context it would be a different instance — the generator's minimal entry for a type
            // it writes no formatter for is built per call — and the identity check would be comparing
            // two descriptors that merely agree, which is exactly what it is there to refuse.
            var serializer = NewRobust(c =>
            {
                Assert.True(c.SchemaRegistry.TryGetInfo(typeof(Late), out var registered),
                    "the context's manifest holds no entry for the late type, so the identity check has nothing to compare against");
                LateOwnSchemaFormatter.Schema = registered;
                c.OnConfigNewType = tc =>
                {
                    if (tc.Type == typeof(Late)) tc.CustomFormatter = formatter;
                };
            });

            var bytes = serializer.Save(Model(), 1);

            // Not an opaque entry, so nothing is framed: the payload is whatever the context's own
            // formatter wrote, which is what the (member-less) entry describes.
            Assert.DoesNotContain("OPAQUE", CerasRobustSerializer.DumpSchema(bytes), StringComparison.Ordinal);
            Assert.Equal(1, formatter.SerializeCalls);

            var clone = serializer.Load<Root>(bytes, 1);
            Assert.Equal(Model().Sentinel, clone.Sentinel);
            Assert.NotNull(clone.Late);
        }

        /// <summary>
        /// The raw context describes nothing, so a custom formatter's bytes stay exactly what they always
        /// were there — and it has no manifest, no bake and no reconciliation, so the late install is not
        /// even a window. This is the door a value that is only ever serialized transiently uses.
        /// </summary>
        [Fact]
        public void ARawContextStillHonoursTheSameLateInstallUnframed()
        {
            var config = new CerasRawConfig { AllowReflectionFallback = true };
            var formatter = new LateFormatterA();
            int callbacks = 0;
            config.OnConfigNewType = tc =>
            {
                if (tc.Type != typeof(Late)) return;
                callbacks++;
                tc.CustomFormatter = formatter;
            };

            var serializer = new CerasFastSerializer(config);
            var bytes = serializer.Serialize(Model());

            Assert.Equal(1, callbacks);
            Assert.Equal(1, formatter.SerializeCalls);
            Assert.Equal(PayloadId, serializer.Deserialize<Root>(bytes).Late.Id);
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A robust context over this file's context, configured by <paramref name="configure"/> after the
        /// generated manifest is registered.
        /// </summary>
        static CerasRobustSerializer NewRobust(Action<CerasRobustConfig> configure = null)
            => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(ContextName);
                configure?.Invoke(c);
            });

        /// <summary>
        /// Selects <paramref name="formatter"/> for the late type from the <c>OnConfigNewType</c> callback
        /// — i.e. from the middle of the first save or load that reaches it, after the reconciliation at
        /// the top of the operation and after the bake.
        /// </summary>
        static Action<CerasRobustConfig> Selecting(IFormatter<Late> formatter)
            => c => c.OnConfigNewType = tc =>
            {
                if (tc.Type == typeof(Late)) tc.CustomFormatter = formatter;
            };

        /// <summary>
        /// Drops the late type's one member, which is the only way this context can write a value of it:
        /// the generator registers a <em>member-less</em> entry for a type it emits no formatter for, and a
        /// robust context refuses the reflection fallback when the member list it would write is not the one
        /// that entry describes (Format/RobustReflectionFallbackPolicy). An empty member list is what a
        /// member-less entry describes, so this is the file the refusals above are planted in.
        /// </summary>
        static Action<CerasRobustConfig> AWriterWhoseMemberListIsDescribed
            => c => c.ConfigType<Late>().ConfigField(nameof(Late.Id)).Exclude();

        static int liveRentals() => CerasBufferPool.LiveRentals;

        static ulong Fingerprint(byte[] save) => BitConverter.ToUInt64(save, RobustHeader.FingerprintOffset);

        static int DataStartOf(byte[] save) => BitConverter.ToInt32(save, RobustHeader.DataSectionOffsetOffset);
        static int StringTableOf(byte[] save) => BitConverter.ToInt32(save, RobustHeader.StringTableOffsetOffset);

        /// <summary>Where the formatter's identity is written in the schema section.</summary>
        static string FormatterName(IFormatter<Late> formatter) => OpaquePayload.FormatterToken(formatter);

        /// <summary>One payload: the formatter's tag, then its own encoding of the id.</summary>
        static byte[] PayloadOf(byte tag, int id) => new[] { tag }.Concat(BitConverter.GetBytes(id)).ToArray();

        /// <summary>Whether the save's data section holds these bytes. Confined to the data section, so a
        /// pattern cannot be found in the schema section or the string table by accident.</summary>
        static int IndexOf(byte[] save, byte[] needle)
        {
            int start = DataStartOf(save), end = StringTableOf(save);
            for (int i = start; i <= end - needle.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                    if (save[i + j] != needle[j]) { hit = false; break; }
                if (hit) return i;
            }
            return -1;
        }

        /// <summary>The declared length in front of a payload, read as the format's own fixed int32.</summary>
        static int DeclaredLengthInFrontOf(byte[] save, byte[] payload)
        {
            int at = IndexOf(save, payload);
            Assert.True(at >= DataStartOf(save) + 4, "the payload is at the very start of the data section, so nothing can precede it");
            return BitConverter.ToInt32(save, at - 4);
        }

        /// <summary>The one dump line for the late type's opaque entry, matched on the stable name the
        /// schema section records (its canonical name), which is a prefix of no other entry here.</summary>
        static string OpaqueLine(byte[] save)
        {
            string stableName = TypeIdentity.GetCanonicalName(typeof(Late));
            string dump = CerasRobustSerializer.DumpSchema(save);
            var line = dump.Split('\n').FirstOrDefault(l => l.StartsWith("OPAQUE", StringComparison.Ordinal)
                                                          && l.Contains($"  {stableName}  ", StringComparison.Ordinal));
            Assert.True(line != null, $"the save has no OPAQUE entry for '{stableName}':\n{dump}");
            return line;
        }
    }
}
