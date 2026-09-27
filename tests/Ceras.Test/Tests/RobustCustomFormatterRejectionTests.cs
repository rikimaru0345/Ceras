// Fail-loud rejection of user custom formatters in robust contexts.
//
// A robust save describes its values in the schema section it carries, and the layout fingerprint that
// chooses the generated reader is computed from that schema. Neither records which formatter produced
// the bytes, so a custom formatter's opaque payload is invisible to both: a later build that removed,
// reordered or retyped a member of the overridden type cannot skip the value and silently returns wrong
// data, while a build that kept the member still sees a *matching* fingerprint and takes the generated
// reader. The rejection is temporary — it is lifted when the format carries explicit opaque-schema and
// formatter-selection metadata (see RobustCustomFormatterPolicy).
//
// The decisive shapes: an overridden class and struct, as a root and as a member; a formatter installed
// after the serializer exists; and a v3 file whose fingerprint matches the current context exactly.
// Raw/transient is covered here too, because it is the supported home for the same formatters.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Formatters;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.Tests.RobustCustomReject
{
    // ── Models ─────────────────────────────────────────────────────────────────────────────────────

    [CerasContext("Tests")]
    [CerasSerializable]
    public class HoldsOverriddenClass { public int Id; public RejectTargetClass Target; }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class HoldsOverriddenStruct { public int Id; public RejectTargetStruct Target; }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class RejectRootClass { public int Number; public string Text; }

    [CerasContext("Tests")]
    [CerasSerializable]
    public struct RejectRootStruct { public int Number; public string Text; }

    /// <summary>Has a generated formatter, and is also the type a user formatter overrides.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public class RejectTargetClass { public int Magic; }

    /// <summary>A value type, so an opaque payload there desyncs the parent itself, not just the fields
    /// after it.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public struct RejectTargetStruct { public int Magic; }

    /// <summary>Served by the library's own robust string/Type formatters, which a robust context must
    /// still allow — they have no manifest entry to be identified against.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public class RobustStringTypeHolder { public string Text; public Type Kind; }

    // ── Custom formatters ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One hand-written formatter for every shape, so each case is a closed instantiation rather than
    /// four near-identical classes. It writes a marker int and, on read, leaves the value at its default
    /// — so a load that returns the marker proves the *custom* bytes round-tripped, and one that returns
    /// the generated field's original value proves the override was bypassed.
    /// </summary>
    public class OpaqueFormatter<T> : FormatterBase<T>
    {
        public const int Marker = unchecked((int)0x0BADF00D);

        public override void Serialize(IBufferWriter<byte> writer, T value)
            => SerializerBinary.WriteInt32Fixed(writer, Marker);

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            SerializerBinary.ReadInt32Fixed(ref reader);

            // A reference type has to come back as an object, not null: the reference machinery would
            // otherwise hand the caller a null instance. The point is that its *fields* keep their
            // defaults — a value the schema does not describe.
            value = typeof(T).IsValueType
                ? default
                : (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
        }
    }

    /// <summary>
    /// A hand-written formatter that also declares a static <c>Schema</c> field of the generator's shape,
    /// and a hand-written schema that does not describe its bytes. Proves the policy matches schema
    /// *identity*, not the presence of such a field.
    /// </summary>
    public class SelfDescribingUserFormatter : FormatterBase<RejectTargetClass>
    {
        public static readonly SchemaTypeInfo DeclaredSchema = new SchemaTypeInfo(
            stableName: "Ceras.Test.Tests.RobustCustomReject.RejectTargetClass",
            previousTypeNames: Array.Empty<string>(),
            kind: SchemaTypeKind.Class,
            fields: new[] { new SchemaFieldInfo("Magic", Array.Empty<string>(), "System.Int32", true) },
            fixedByteSize: 0);

        internal static readonly SchemaTypeInfo Schema = DeclaredSchema;   // the generator's member name

        public override void Serialize(IBufferWriter<byte> writer, RejectTargetClass value)
            => SerializerBinary.WriteInt32Fixed(writer, OpaqueFormatter<RejectTargetClass>.Marker);

        public override void Deserialize(ref SequenceReader<byte> reader, ref RejectTargetClass value)
        {
            SerializerBinary.ReadInt32Fixed(ref reader);
            value = new RejectTargetClass();
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────────────────────

    public class RobustCustomFormatterRejectionTests : TestBase
    {
        static CerasRobustSerializer Robust(Action<CerasRobustConfig> f) => new CerasRobustSerializer(f);
        static CerasRobustSerializer Generated() => Robust(c => c.UseGeneratedFormatters("Tests"));
        static CerasFastSerializer Raw(Action<CerasConfig> f) => new CerasFastSerializer(f);

        /// <summary>The four shapes an override can take, with the type the rejection must name.</summary>
        static Action<CerasRobustConfig> OverrideOf(string shape) => shape switch
        {
            "root class" => c => c.ConfigType<RejectRootClass>().CustomFormatter = new OpaqueFormatter<RejectRootClass>(),
            "root struct" => c => c.ConfigType<RejectRootStruct>().CustomFormatter = new OpaqueFormatter<RejectRootStruct>(),
            "member class" => c => c.ConfigType<RejectTargetClass>().CustomFormatter = new OpaqueFormatter<RejectTargetClass>(),
            "member struct" => c => c.ConfigType<RejectTargetStruct>().CustomFormatter = new OpaqueFormatter<RejectTargetStruct>(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };

        static readonly string[] AllShapes = { "root class", "root struct", "member class", "member struct" };

        // ── Every shape of override is refused, and the message says why ─────────────────────────

        [Theory]
        [InlineData("root class", "RejectRootClass")]
        [InlineData("root struct", "RejectRootStruct")]
        [InlineData("member class", "RejectTargetClass")]
        [InlineData("member struct", "RejectTargetStruct")]
        public void AUserCustomFormatter_IsRejected_ForEveryShape(string shape, string expectedType)
        {
            // The generated registration runs first for the member shapes, so this also covers replacing
            // a generated formatter with a user one: the schema still advertises the generated field
            // while the payload holds the custom bytes, which is the original defect.
            var ex = Assert.Throws<CerasSchemaException>(() => Robust(c =>
            {
                c.UseGeneratedFormatters("Tests");
                OverrideOf(shape)(c);
            }));

            Assert.Contains(expectedType, ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(OpaqueFormatter<int>).Split('<')[0], ex.Message, StringComparison.Ordinal);
            // names the limitation ...
            Assert.Contains("temporary", ex.Message, StringComparison.OrdinalIgnoreCase);
            // ... and the way out.
            Assert.Contains("CerasFastSerializer", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>Every offender is listed at once: a context usually has more than one, and reporting
        /// them one error at a time is a slow way to find out.</summary>
        [Fact]
        public void AllOffendingTypes_AreListed_InOneError()
        {
            var ex = Assert.Throws<CerasSchemaException>(() => Robust(c =>
            {
                c.UseGeneratedFormatters("Tests");
                OverrideOf("member class")(c);
                OverrideOf("member struct")(c);
            }));

            Assert.Contains("RejectTargetClass", ex.Message, StringComparison.Ordinal);
            Assert.Contains("RejectTargetStruct", ex.Message, StringComparison.Ordinal);
        }

        // ── Nothing is written or read: the guard covers construction, Save and Load ──────────────

        /// <summary>
        /// A type config is sealed lazily (on the type's first use), so a formatter can be installed
        /// *after* the serializer was constructed. The setup-time check alone would miss that and write a
        /// file, so Save and Load re-check. The check is memoised between calls, so this also pins that a
        /// caught context keeps failing and that clearing the override makes it usable again.
        /// </summary>
        [Fact]
        public void AFormatterInstalledAfterConstruction_IsRejected_OnSaveAndOnLoad_AndTheScanStaysEffective()
        {
            var good = Generated().Save(new HoldsOverriddenClass { Id = 1, Target = new RejectTargetClass { Magic = 9 } }, 1);

            var robust = Generated();
            robust.Config.ConfigType<RejectTargetClass>().CustomFormatter = new OpaqueFormatter<RejectTargetClass>();

            foreach (var attempt in new Action[]
            {
                () => robust.Save(new HoldsOverriddenClass { Id = 1, Target = new RejectTargetClass() }, 1),
                () => robust.Save(new HoldsOverriddenClass { Id = 1, Target = new RejectTargetClass() }, 1),   // again: not remembered as a pass
                () => robust.Load<HoldsOverriddenClass>(good, 1),
            })
            {
                var ex = Assert.Throws<CerasSchemaException>(attempt);
                Assert.Contains("RejectTargetClass", ex.Message, StringComparison.Ordinal);
            }

            robust.Config.ConfigType<RejectTargetClass>().CustomFormatter = null!;   // an assignment too
            var saved = robust.Save(new HoldsOverriddenClass { Id = 1, Target = new RejectTargetClass { Magic = 5 } }, 1);
            Assert.Equal(5, Generated().Load<HoldsOverriddenClass>(saved, 1).Target.Magic);
        }

        // ── Raw/transient is unaffected: the same formatters are still honoured ───────────────────

        [Fact]
        public void RawContext_StillHonorsEveryOverrideShape()
        {
            // Root and member, class and struct.
            var root = Raw(c =>
            {
                c.AllowReflectionFallback = true;
                c.UseGeneratedFormatters("Tests");
                c.ConfigType<RejectRootClass>().CustomFormatter = new OpaqueFormatter<RejectRootClass>();
            });
            var rootClone = root.Deserialize<RejectRootClass>(root.Serialize(new RejectRootClass { Number = 3, Text = "x" }));
            Assert.Equal(0, rootClone.Number);                                  // not the generated field
            Assert.Null(rootClone.Text);

            var members = Raw(c =>
            {
                c.AllowReflectionFallback = true;
                c.UseGeneratedFormatters("Tests");
                c.ConfigType<RejectTargetClass>().CustomFormatter = new OpaqueFormatter<RejectTargetClass>();
                c.ConfigType<RejectTargetStruct>().CustomFormatter = new OpaqueFormatter<RejectTargetStruct>();
            });
            var clone = members.Deserialize<HoldsOverriddenClass>(
                members.Serialize(new HoldsOverriddenClass { Id = 7, Target = new RejectTargetClass { Magic = 5 } }));
            Assert.Equal(7, clone.Id);
            Assert.Equal(0, clone.Target.Magic);
        }

        /// <summary>
        /// string and System.Type are served by the library's own internal robust formatters. They are
        /// identified by type, not by a manifest entry (they are well-known fixed ids), so this is what
        /// would break if the allowlist were wrong.
        /// </summary>
        [Fact]
        public void RobustStringAndSystemTypeMembers_StillRoundTrip()
        {
            var model = new RobustStringTypeHolder { Text = "interned", Kind = typeof(RejectTargetClass) };
            var bytes = Generated().Save(model, 1);
            var clone = Generated().Load<RobustStringTypeHolder>(bytes, 1);

            Assert.Equal("interned", clone.Text);
            Assert.Equal(typeof(RejectTargetClass), clone.Kind);
        }

        // ── Generated formatters are recognised by identity, not by name, namespace or a claim ───

        /// <summary>
        /// The invariant the classification rests on: the schema the context registered for a type is the
        /// very instance that type's generated formatter class declares. Pinned for the whole manifest
        /// rather than a sample, because the failure mode is silent — if the generator ever stopped
        /// emitting that field on some formatter class, the policy would start rejecting ordinary
        /// generated formatters and only a check across the manifest would say so.
        /// </summary>
        [Fact]
        public void EveryGeneratedFormatter_DeclaresTheSchemaTheManifestHolds()
        {
            var s = Generated();
            s.Save(new HoldsOverriddenClass { Id = 1, Target = new RejectTargetClass() }, 1);   // seals + bakes

            int withFormatter = 0;
            var mismatched = new System.Collections.Generic.List<string>();

            foreach (var (type, _, info) in s.Config.SchemaRegistry.All())
            {
                var tc = s.Config.GetExistingTypeConfig(type, false);
                if (tc?.CustomFormatter == null) continue;   // abstract/interface minimal entries

                withFormatter++;
                if (!ReferenceEquals(DeclaredSchemaOf(tc.CustomFormatter.GetType()), info))
                    mismatched.Add(TypeIdentity.GetCanonicalName(type));
            }

            Assert.True(withFormatter > 100, $"expected a populated manifest, found {withFormatter} formatters");
            Assert.Empty(mismatched);
        }

        [Fact]
        public void AGeneratedFormatter_IsAccepted()
        {
            var model = new HoldsOverriddenStruct { Id = 2, Target = new RejectTargetStruct { Magic = 8 } };
            var clone = Generated().Load<HoldsOverriddenStruct>(Generated().Save(model, 1), 1);

            Assert.Equal(2, clone.Id);
            Assert.Equal(8, clone.Target.Magic);
        }

        /// <summary>
        /// A user formatter declared in <c>Ceras.Generated</c>, with a generated-looking name, is still
        /// rejected: the pre-existing interpreter check used exactly that namespace string.
        /// </summary>
        [Fact]
        public void AUserFormatterInTheGeneratedNamespace_IsStillRejected()
        {
            var ex = Assert.Throws<CerasSchemaException>(() => Robust(c =>
            {
                c.UseGeneratedFormatters("Tests");
                c.ConfigType<RejectTargetClass>().CustomFormatter = new Ceras.Generated.OpaqueFormatter<RejectTargetClass>();
            }));

            Assert.Contains("RejectTargetClass", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A hand-written formatter that declares a schema of its own is still rejected. A public
        /// self-attestation would accept this; matching identity does not.
        /// </summary>
        [Fact]
        public void AUserFormatterDeclaringItsOwnSchema_IsStillRejected()
        {
            var ex = Assert.Throws<CerasSchemaException>(() => Robust(c =>
            {
                c.UseGeneratedFormatters("Tests");
                c.ConfigType<RejectTargetClass>().CustomFormatter = new SelfDescribingUserFormatter();
            }));

            Assert.Contains("RejectTargetClass", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The generated registration is ordinary public code the user can read and copy, so copying it
        /// must not be a way in. The generated line is
        /// <c>if (tc.CustomFormatter == null) tc.CustomFormatter = new XFormatter();</c> — reproduced here
        /// verbatim, before and after the context is registered.
        /// </summary>
        [Fact]
        public void CopyingTheGeneratedRegistrationCode_DoesNotMakeACustomFormatterAcceptable()
        {
            foreach (var generatedFirst in new[] { false, true })
            {
                var ex = Assert.Throws<CerasSchemaException>(() => Robust(c =>
                {
                    if (generatedFirst) c.UseGeneratedFormatters("Tests");
                    var tc = c.ConfigType<RejectTargetClass>();
                    if (tc.CustomFormatter == null) tc.CustomFormatter = new OpaqueFormatter<RejectTargetClass>();
                    else tc.CustomFormatter = new OpaqueFormatter<RejectTargetClass>();
                    if (!generatedFirst) c.UseGeneratedFormatters("Tests");
                }));

                Assert.Contains("RejectTargetClass", ex.Message, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// <b>Known residual, pinned deliberately.</b> <see cref="SchemaRegistry.Register"/> is public, so
        /// a caller can register a schema of its own and then satisfy the identity check with a
        /// hand-written formatter. This is not a regression and not something the policy can close: on an
        /// unguarded build the same registration already lets a file's schema disagree with its payload.
        /// The test exists so the limit stays visible, so closing it later is a deliberate change here
        /// rather than a silent surprise.
        /// </summary>
        [Fact]
        public void Documented_Residual_AHandRegisteredSchemaSatisfiesTheIdentityCheck()
        {
            var s = Robust(c =>
            {
                c.SchemaRegistry.Register(typeof(RejectTargetClass), SelfDescribingUserFormatter.DeclaredSchema);
                c.UseGeneratedFormatters("Tests");
                c.ConfigType<RejectTargetClass>().CustomFormatter = new SelfDescribingUserFormatter();
            });

            Assert.NotNull(s.Save(new HoldsOverriddenClass { Id = 1, Target = new RejectTargetClass() }, 1));
        }

        // ── Format version: v3 is refused even on a matching fingerprint ─────────────────────────

        /// <summary>
        /// A v3 file cannot be told apart from a good one by its fingerprint — the reason for the version
        /// bump. This rewinds only the version byte of a *current* save, so every other byte, the layout
        /// fingerprint included, is exactly what the current context writes, and the file stays internally
        /// consistent. The rejection has to explain itself, or an experimental version reads like a
        /// version skew the user can wait out.
        /// </summary>
        [Fact]
        public void AV3Save_IsRejected_EvenWhenTheFingerprintMatches()
        {
            var bytes = Generated().Save(new HoldsOverriddenClass { Id = 3, Target = new RejectTargetClass { Magic = 6 } }, 1);

            // Sanity: this really is a matching-layout file for the current context. The reader has to
            // load once first, because the fingerprint is only final once the registry is sealed.
            var reader = Generated();
            reader.Load<HoldsOverriddenClass>(bytes, 1);
            Assert.Equal(reader.Config.SchemaRegistry.ComputeFingerprint(), FingerprintOf(bytes));

            var asV3 = RewindToV3(bytes);
            Assert.Equal(3, asV3[RobustHeader.FormatVersionOffset]);
            // The fingerprint is deliberately unchanged — that is the whole problem.
            Assert.Equal(FingerprintOf(bytes), FingerprintOf(asV3));
            Assert.Equal(CrcOf(asV3), Ceras.Helpers.HashHelpers.CalculateCrc32WithZeroedRange(
                asV3, RobustHeader.ChecksumOffset, RobustHeader.ChecksumSize));

            var ex = Assert.Throws<CerasVersionMismatchException>(
                () => Generated().Load<HoldsOverriddenClass>(asV3, 1));

            Assert.Equal(3u, ex.FileVersion);
            Assert.Equal((uint)CerasRobustSerializer.FormatVersion, ex.LibraryVersion);
            Assert.Contains("no longer accepted", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("custom formatter", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The version written is 4, the two-region envelope the v3 layout introduced is untouched, and
        /// the fingerprint still covers the format version — so a v3 and a v4 context with identical
        /// declared layouts cannot be confused on a fast-path match either. Checked against a recorded v3
        /// fixture, which is a *historical* value: comparing with it is what proves the version
        /// participates in the fingerprint rather than being decoration in the header.
        /// </summary>
        [Fact]
        public void TheCurrentFormatVersion_IsFour_AndTheEnvelopeIsUnchanged()
        {
            Assert.Equal(4, CerasRobustSerializer.FormatVersion);

            var bytes = Generated().Save(new HoldsOverriddenClass { Id = 1, Target = new RejectTargetClass() }, 1);
            Assert.Equal(CerasRobustSerializer.FormatVersion, bytes[RobustHeader.FormatVersionOffset]);

            long schemaStrings = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(RobustHeader.SchemaStringOffsetOffset));
            long dataSection = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(RobustHeader.DataSectionOffsetOffset));
            long stringTable = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(RobustHeader.StringTableOffsetOffset));
            Assert.InRange(schemaStrings, (long)RobustHeader.Size, dataSection);
            Assert.InRange(dataSection, schemaStrings, stringTable);

            Assert.NotEqual(FingerprintOf(bytes), FingerprintOf(File.ReadAllBytes(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "HistoricalV1", "SaveGame.bin"))));
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        static SchemaTypeInfo DeclaredSchemaOf(Type formatterType)
            => formatterType
                .GetField("Schema", System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public)
                ?.GetValue(null) as SchemaTypeInfo;

        static ulong FingerprintOf(byte[] bytes)
            => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(RobustHeader.FingerprintOffset));

        static uint CrcOf(byte[] bytes)
            => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(RobustHeader.ChecksumOffset));

        /// <summary>Rewrites a current save as a v3 file: the version byte only, CRC recomputed so the
        /// file stays internally consistent. Schema, payload and fingerprint are byte-for-byte what the
        /// current build produced.</summary>
        static byte[] RewindToV3(byte[] bytes)
        {
            var copy = (byte[])bytes.Clone();
            copy[RobustHeader.FormatVersionOffset] = 3;
            FixRobustChecksum(copy);
            return copy;
        }
    }
}

// A user formatter that lives in the generator's namespace, used to prove the robust policy identifies
// formatters by schema identity rather than by a namespace or type-name string.
namespace Ceras.Generated
{
    public class OpaqueFormatter<T> : global::Ceras.Formatters.FormatterBase<T>
    {
        public override void Serialize(global::System.Buffers.IBufferWriter<byte> writer, T value)
            => global::Ceras.SerializerBinary.WriteInt32Fixed(writer, 1);

        public override void Deserialize(ref global::System.Buffers.SequenceReader<byte> reader, ref T value)
            => global::Ceras.SerializerBinary.ReadInt32Fixed(ref reader);
    }
}
