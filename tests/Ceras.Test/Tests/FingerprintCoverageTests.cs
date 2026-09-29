using System;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.Fingerprint
{
    // A monomorphic-by-default model (the int field is monomorphic). The interpreter test below
    // deliberately overrides its schema so only the field's IsMonomorphic flag changes.
    [CerasContext("Tests")]
    [CerasSerializable]
    public class FingerprintMonoModel
    {
        public int X;
        public string S;
    }

    // Two types with distinct layouts plus a holder with a polymorphic (object) slot. The payload of
    // the holder embeds the concrete type's ID, so swapping the ID assignment must change the
    // fingerprint and force the interpreter (never a silent fast-path misread).
    [CerasContext("Tests")]
    [CerasSerializable]
    public class FpIdA { public int A; }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class FpIdB { public string B; }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class FpIdHolder { public object Item; }

    /// <summary>
    /// Defect: the layout fingerprint did not cover every layout-affecting property (field
    /// monomorphism, fixed byte size, generic definition/arguments, enum underlying type, format
    /// version), so two contexts that differed only in those produced the same fingerprint and the
    /// fast path silently misread the payload.
    /// </summary>
    public class FingerprintCoverageTests : TestBase
    {
        sealed class ProbeType { }

        sealed class ProbeType2 { }

        static SchemaTypeInfo MakeInfo(
            bool monomorphic,
            int fixedByteSize = 0,
            SchemaTypeKind kind = SchemaTypeKind.Struct,
            string underlyingTypeName = null,
            string[] argTypeNames = null,
            SchemaEnumValueInfo[] enumValues = null,
            string stableName = "Ceras.Test.Fingerprint.Probe",
            SchemaFieldInfo[] fields = null,
            string opaqueFormatterName = null,
            bool opaqueInline = false)
        {
            return new SchemaTypeInfo(
                stableName: stableName,
                previousTypeNames: Array.Empty<string>(),
                kind: kind,
                fields: fields ?? new[]
                {
                    new SchemaFieldInfo("Value", Array.Empty<string>(), "System.Int32", monomorphic)
                },
                fixedByteSize: fixedByteSize,
                enumValues: enumValues,
                underlyingTypeName: underlyingTypeName,
                argTypeNames: argTypeNames,
                opaqueFormatterName: opaqueFormatterName,
                opaqueValueWrittenInline: opaqueInline);
        }

        static ulong Fingerprint(SchemaTypeInfo info)
        {
            var registry = new SchemaRegistry();
            registry.Register(typeof(ProbeType), info);
            return registry.ComputeFingerprint();
        }

        /// <summary>Fingerprint of a single enum entry given as raw bits (the schema's representation).</summary>
        static ulong EnumFingerprint(string name, ulong value) =>
            Fingerprint(MakeInfo(false, kind: SchemaTypeKind.Enum, underlyingTypeName: "System.Int64",
                enumValues: new[] { new SchemaEnumValueInfo(name, value) }));

        /// <summary>Two-entry fingerprint, registered in the given order (IDs follow that order).</summary>
        static ulong OrderedFingerprint(bool reversed)
        {
            var a = MakeInfo(false, stableName: "Ceras.Test.Fingerprint.A");
            var b = MakeInfo(false, stableName: "Ceras.Test.Fingerprint.B");

            var registry = new SchemaRegistry();
            if (reversed)
            {
                registry.Register(typeof(ProbeType2), b);
                registry.Register(typeof(ProbeType), a);
            }
            else
            {
                registry.Register(typeof(ProbeType), a);
                registry.Register(typeof(ProbeType2), b);
            }
            return registry.ComputeFingerprint();
        }

        [Fact]
        public void Fingerprint_Changes_When_Field_Monomorphism_Changes()
        {
            Assert.NotEqual(Fingerprint(MakeInfo(monomorphic: false)), Fingerprint(MakeInfo(monomorphic: true)));
        }

        [Fact]
        public void Fingerprint_Changes_When_FixedByteSize_Changes()
        {
            Assert.NotEqual(Fingerprint(MakeInfo(false, fixedByteSize: 0)), Fingerprint(MakeInfo(false, fixedByteSize: 8)));
        }

        [Fact]
        public void Fingerprint_Changes_When_Generic_Definition_Changes()
        {
            var a = MakeInfo(false, kind: SchemaTypeKind.GenericInstantiation, underlyingTypeName: "System.Collections.Generic.List`1", argTypeNames: new[] { "System.Int32" });
            var b = MakeInfo(false, kind: SchemaTypeKind.GenericInstantiation, underlyingTypeName: "System.Collections.Generic.HashSet`1", argTypeNames: new[] { "System.Int32" });
            Assert.NotEqual(Fingerprint(a), Fingerprint(b));
        }

        [Fact]
        public void Fingerprint_Changes_When_Generic_Arguments_Change()
        {
            var a = MakeInfo(false, kind: SchemaTypeKind.GenericInstantiation, underlyingTypeName: "System.Collections.Generic.List`1", argTypeNames: new[] { "System.Int32" });
            var b = MakeInfo(false, kind: SchemaTypeKind.GenericInstantiation, underlyingTypeName: "System.Collections.Generic.List`1", argTypeNames: new[] { "System.String" });
            Assert.NotEqual(Fingerprint(a), Fingerprint(b));
        }

        /// <summary>
        /// Formatter selection is part of the layout, so it is part of the fingerprint. Two entries that
        /// differ only in <em>which</em> user formatter wrote the value - the same kind, the same name, no
        /// members either way - must not fingerprint the same, or a build that swapped a formatter would
        /// see a matching fingerprint and take the generated reader over a payload it does not read.
        /// </summary>
        [Fact]
        public void Fingerprint_Changes_When_TheCustomFormatter_Changes()
        {
            var first = MakeInfo(false, kind: SchemaTypeKind.Opaque, stableName: "Ceras.Test.Fingerprint.Opaque",
                opaqueFormatterName: "Game.RecipeFormatter", opaqueInline: false);
            var second = MakeInfo(false, kind: SchemaTypeKind.Opaque, stableName: "Ceras.Test.Fingerprint.Opaque",
                opaqueFormatterName: "Game.OtherRecipeFormatter", opaqueInline: false);
            var moved = MakeInfo(false, kind: SchemaTypeKind.Opaque, stableName: "Ceras.Test.Fingerprint.Opaque",
                opaqueFormatterName: "Game.RecipeFormatter", opaqueInline: true);

            Assert.NotEqual(Fingerprint(first), Fingerprint(second));   // a different formatter
            Assert.NotEqual(Fingerprint(first), Fingerprint(moved));     // the same formatter, a different framing
            Assert.Equal(Fingerprint(first), Fingerprint(first));       // and the same one twice
        }

        /// <summary>
        /// The control for the one above: an opaque entry and a *described* one for the same type are
        /// different layouts too, which is what a build that added or removed a custom formatter does.
        /// </summary>
        [Fact]
        public void Fingerprint_Changes_When_A_Type_Becomes_Opaque()
        {
            var described = MakeInfo(false, stableName: "Ceras.Test.Fingerprint.BecomesOpaque",
                fields: new[] { new SchemaFieldInfo("Value", Array.Empty<string>(), "System.Int32", true) });
            var opaque = MakeInfo(false, kind: SchemaTypeKind.Opaque, stableName: "Ceras.Test.Fingerprint.BecomesOpaque",
                fields: Array.Empty<SchemaFieldInfo>(), opaqueFormatterName: "Game.Formatter");

            Assert.NotEqual(Fingerprint(described), Fingerprint(opaque));
        }

        [Fact]
        public void Fingerprint_Changes_When_Enum_UnderlyingType_Changes()
        {
            var intEnum = MakeInfo(false, kind: SchemaTypeKind.Enum, underlyingTypeName: "System.Int32",
                enumValues: new[] { new SchemaEnumValueInfo("A", 0) });
            var byteEnum = MakeInfo(false, kind: SchemaTypeKind.Enum, underlyingTypeName: "System.Byte",
                enumValues: new[] { new SchemaEnumValueInfo("A", 0) });
            Assert.NotEqual(Fingerprint(intEnum), Fingerprint(byteEnum));
        }

        [Fact]
        public void Fingerprint_Is_Stable_For_Identical_Schemas()
        {
            Assert.Equal(Fingerprint(MakeInfo(false, fixedByteSize: 8)), Fingerprint(MakeInfo(false, fixedByteSize: 8)));
        }

        /// <summary>
        /// Both halves of the original coverage, expressed without an inconsistent context: a schema
        /// difference changes the fingerprint, and the interpreter reads a generated file correctly once
        /// it is the selected reader.
        /// <para>
        /// The schema difference is a field's monomorphism, and it is asserted on the fingerprint
        /// directly. Forcing the reader used to be done by registering a schema with the flag flipped for
        /// a type that has a generated formatter — which a robust context now refuses, because that
        /// context would write a file whose schema section disagrees with the bytes its own generated
        /// formatter produced (see <see cref="Doctored_Schema_ForAGeneratedType_IsRejected"/>). The stored
        /// fingerprint in a copy of an otherwise matching file selects the reader instead, which leaves
        /// the schema the generated one throughout.
        /// </para>
        /// </summary>
        [Fact]
        public void Monomorphic_Schema_Change_Forces_Interpreter_And_Still_Loads()
        {
            // A monomorphism change is a real layout difference, so it must move the fingerprint.
            Assert.NotEqual(
                Fingerprint(MakeInfo(monomorphic: true, stableName: "Ceras.Test.Fingerprint.MonoSwitch")),
                Fingerprint(MakeInfo(false, stableName: "Ceras.Test.Fingerprint.MonoSwitch")));

            // And with the interpreter selected, it reads a generated file correctly.
            var model = new FingerprintMonoModel { X = 123, S = "value" };
            var bytes = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests")).Save(model, 1);

            var reader = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
            var clone = reader.Load<FingerprintMonoModel>(SlowPath(bytes), 1);

            Assert.Equal(123, clone.X);
            Assert.Equal("value", clone.S);
        }

        /// <summary>
        /// A robust context now requires the schema registered for a type to be the very instance the
        /// installed generated formatter declares (<c>RobustCustomFormatterPolicy</c>). Registering a
        /// doctored schema for a type that has a generated formatter therefore fails the context: that
        /// context would write a file whose schema section disagrees with the bytes its own generated
        /// formatter produced, which is the desync the policy exists to prevent. Pinned here so the
        /// refusal is deliberate rather than incidental.
        /// </summary>
        [Fact]
        public void Doctored_Schema_ForAGeneratedType_IsRejected()
        {
            var probe = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
            Assert.True(probe.Config.SchemaRegistry.TryGetInfo(typeof(FingerprintMonoModel), out var normalInfo));

            var ex = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(() =>
                new CerasRobustSerializer(config =>
                {
                    var fields = normalInfo.Fields
                        .Select(f => new SchemaFieldInfo(f.StableName, f.PreviousNames, f.TypeName, !f.IsMonomorphic))
                        .ToArray();
                    config.SchemaRegistry.Register(typeof(FingerprintMonoModel), new SchemaTypeInfo(
                        normalInfo.StableName, normalInfo.PreviousTypeNames, normalInfo.Kind,
                        fields, normalInfo.FixedByteSize, normalInfo.EnumValues,
                        normalInfo.UnderlyingTypeName, normalInfo.ArgTypeNames));
                    config.UseGeneratedFormatters("Tests");
                }));

            Assert.Contains("FingerprintMonoModel", ex.Message, StringComparison.Ordinal);
        }

        // ── Type-ID assignment is part of the fingerprint ────────────────────────────

        [Fact]
        public void Fingerprint_Changes_When_TypeId_Assignment_Changes()
        {
            // Same logical types and schemas, but a different registration order assigns the IDs
            // in the opposite order. The payload type tags would not be interchangeable, so the
            // fingerprints must differ and the load must take the interpreter path.
            ulong normal = OrderedFingerprint(reversed: false);
            ulong reversed = OrderedFingerprint(reversed: true);
            Assert.NotEqual(normal, reversed);
        }

        [Fact]
        public void Reversed_TypeId_Assignment_With_Polymorphic_Payload_Loads_Via_Interpreter()
        {
            // The reader pre-registers the two payload types in the opposite order, so they get the
            // same stable names/schemas but swapped IDs. That is a materially different on-wire
            // contract: the fingerprint must differ and the load must not use the fast path.
            var probe = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
            Assert.True(probe.Config.SchemaRegistry.TryGetInfo(typeof(FpIdA), out var aInfo));
            Assert.True(probe.Config.SchemaRegistry.TryGetInfo(typeof(FpIdB), out var bInfo));

            var writer = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
            byte[] bytes = writer.Save(new FpIdHolder { Item = new FpIdA { A = 123 } }, 1);
            ulong writerFingerprint = writer.Config.SchemaRegistry.ComputeFingerprint();

            var reader = new CerasRobustSerializer(config =>
            {
                config.SchemaRegistry.Register(typeof(FpIdB), bInfo);
                config.SchemaRegistry.Register(typeof(FpIdA), aInfo);
                config.UseGeneratedFormatters("Tests");
            });
            ulong readerFingerprint = reader.Config.SchemaRegistry.ComputeFingerprint();

            // IDs really were assigned differently.
            Assert.True(reader.Config.SchemaRegistry.TryGetId(typeof(FpIdA), out int readerAId));
            Assert.True(writer.Config.SchemaRegistry.TryGetId(typeof(FpIdA), out int writerAId));
            Assert.NotEqual(writerAId, readerAId);

            Assert.NotEqual(writerFingerprint, readerFingerprint);

            // Because the fingerprints differ this goes through the identity-preserving interpreter,
            // which resolves the payload by stable name and still returns the right object.
            var clone = reader.Load<FpIdHolder>(bytes, 1);
            var item = Assert.IsType<FpIdA>(clone.Item);
            Assert.Equal(123, item.A);
        }

        [Fact]
        public void Fingerprint_Changes_When_Field_Order_Changes()
        {
            var ab = MakeInfo(false, fields: new[]
            {
                new SchemaFieldInfo("A", Array.Empty<string>(), "System.Int32", true),
                new SchemaFieldInfo("B", Array.Empty<string>(), "System.Int32", true),
            });
            var ba = MakeInfo(false, fields: new[]
            {
                new SchemaFieldInfo("B", Array.Empty<string>(), "System.Int32", true),
                new SchemaFieldInfo("A", Array.Empty<string>(), "System.Int32", true),
            });
            Assert.NotEqual(Fingerprint(ab), Fingerprint(ba));
        }

        [Fact]
        public void Fingerprint_Changes_When_Generic_Argument_Order_Changes()
        {
            var ab = MakeInfo(false, kind: SchemaTypeKind.GenericInstantiation,
                underlyingTypeName: "Ceras.Test.Fingerprint.Pair`2",
                argTypeNames: new[] { "System.Int32", "System.String" });
            var ba = MakeInfo(false, kind: SchemaTypeKind.GenericInstantiation,
                underlyingTypeName: "Ceras.Test.Fingerprint.Pair`2",
                argTypeNames: new[] { "System.String", "System.Int32" });
            Assert.NotEqual(Fingerprint(ab), Fingerprint(ba));
        }

        [Fact]
        public void Fingerprint_Changes_When_Value_Reference_Representation_Changes()
        {
            var structInfo = MakeInfo(false, kind: SchemaTypeKind.Struct);
            var classInfo = MakeInfo(false, kind: SchemaTypeKind.Class);
            Assert.NotEqual(Fingerprint(structInfo), Fingerprint(classInfo));
        }

        // ── Enum values: all 64 bits, names, and remapping ───────────────────────────

        [Fact]
        public void Fingerprint_Hashes_All_Enum_Value_Bits()
        {
            // The schema stores raw bits, so a signed value and the same bits as an unsigned one are the
            // same entry; every *distinct* bit pattern must still hash differently.
            static ulong Bits(long signed) => unchecked((ulong)signed);

            // (uint)1 == (uint)4294967297; the old truncation made these collide.
            Assert.NotEqual(EnumFingerprint("X", 1), EnumFingerprint("X", 4294967297UL));
            Assert.NotEqual(EnumFingerprint("X", 1), EnumFingerprint("X", Bits(-4294967295L)));
            Assert.NotEqual(EnumFingerprint("X", 0xFFFFFFFFUL), EnumFingerprint("X", Bits(long.MaxValue)));
            Assert.NotEqual(EnumFingerprint("X", Bits(long.MinValue)), EnumFingerprint("X", 0));

            // The unsigned half of the range is representable at all now.
            Assert.NotEqual(EnumFingerprint("X", ulong.MaxValue), EnumFingerprint("X", Bits(long.MaxValue)));
            Assert.NotEqual(EnumFingerprint("X", 0x8000000000000000UL), EnumFingerprint("X", 0));
        }

        [Fact]
        public void Fingerprint_Changes_When_Enum_Value_Is_Renamed_Or_Remapped()
        {
            Assert.NotEqual(EnumFingerprint("X", 1), EnumFingerprint("Y", 1));
            Assert.NotEqual(EnumFingerprint("X", 1), EnumFingerprint("X", 2));
        }

        // ── Caching / staleness ──────────────────────────────────────────────────────

        [Fact]
        public void Fingerprint_Is_Invalidated_By_New_Registration()
        {
            var registry = new SchemaRegistry();
            registry.Register(typeof(ProbeType), MakeInfo(false, stableName: "Ceras.Test.Fingerprint.A"));
            ulong first = registry.ComputeFingerprint();

            registry.Register(typeof(ProbeType2), MakeInfo(false, stableName: "Ceras.Test.Fingerprint.B"));
            ulong second = registry.ComputeFingerprint();

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void Fingerprint_Matches_Baked_Fingerprint_After_Seal()
        {
            var registry = new SchemaRegistry();
            registry.Register(typeof(ProbeType), MakeInfo(false, stableName: "Ceras.Test.Fingerprint.A"));

            Assert.False(registry.HasBakedSchema);
            registry.Seal();

            Assert.True(registry.TryGetBakedSchema(out _, out _, out _, out _, out ulong baked));
            Assert.Equal(baked, registry.ComputeFingerprint());
            Assert.Equal(baked, registry.ComputeFingerprint());
        }

        [Fact]
        public void Register_After_Seal_Throws_So_No_Stale_Baked_Schema_Can_Result()
        {
            var registry = new SchemaRegistry();
            registry.Register(typeof(ProbeType), MakeInfo(false, stableName: "Ceras.Test.Fingerprint.A"));
            registry.Seal();

            Assert.Throws<Ceras.Exceptions.CerasSchemaException>(() =>
                registry.Register(typeof(ProbeType2), MakeInfo(false, stableName: "Ceras.Test.Fingerprint.B")));

            // The baked schema/fingerprint are untouched by the rejected registration.
            Assert.True(registry.TryGetBakedSchema(out _, out _, out _, out _, out ulong baked));
            Assert.Equal(baked, registry.ComputeFingerprint());
        }

        [Fact]
        public void Fingerprint_Ignores_Migration_Aliases()
        {
            // Previous type names are migration metadata; they never appear in a new save's payload,
            // so a context that differs only in aliases is still byte-compatible and keeps the fast path.
            var plain = new SchemaTypeInfo("Ceras.Test.Fingerprint.A", Array.Empty<string>(),
                SchemaTypeKind.Struct, Array.Empty<SchemaFieldInfo>(), 0);
            var aliased = new SchemaTypeInfo("Ceras.Test.Fingerprint.A", new[] { "Ceras.Test.OldName" },
                SchemaTypeKind.Struct, Array.Empty<SchemaFieldInfo>(), 0);

            Assert.Equal(Fingerprint(plain), Fingerprint(aliased));
        }
    }
}
