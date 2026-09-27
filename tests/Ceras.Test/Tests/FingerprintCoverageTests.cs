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

    /// <summary>
    /// Defect: the layout fingerprint did not cover every layout-affecting property (field
    /// monomorphism, fixed byte size, generic definition/arguments, enum underlying type, format
    /// version), so two contexts that differed only in those produced the same fingerprint and the
    /// fast path silently misread the payload.
    /// </summary>
    public class FingerprintCoverageTests
    {
        sealed class ProbeType { }

        static SchemaTypeInfo MakeInfo(
            bool monomorphic,
            int fixedByteSize = 0,
            SchemaTypeKind kind = SchemaTypeKind.Struct,
            string underlyingTypeName = null,
            string[] argTypeNames = null,
            SchemaEnumValueInfo[] enumValues = null)
        {
            return new SchemaTypeInfo(
                stableName: "Ceras.Test.Fingerprint.Probe",
                previousTypeNames: Array.Empty<string>(),
                kind: kind,
                fields: new[]
                {
                    new SchemaFieldInfo("Value", Array.Empty<string>(), "System.Int32", monomorphic)
                },
                fixedByteSize: fixedByteSize,
                enumValues: enumValues,
                underlyingTypeName: underlyingTypeName,
                argTypeNames: argTypeNames);
        }

        static ulong Fingerprint(SchemaTypeInfo info)
        {
            var registry = new SchemaRegistry();
            registry.Register(typeof(ProbeType), info);
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

        [Fact]
        public void Monomorphic_Schema_Change_Forces_Interpreter_And_Still_Loads()
        {
            var model = new FingerprintMonoModel { X = 123, S = "value" };
            var writer = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
            byte[] bytes = writer.Save(model, 1);

            // Read the normally generated schema for the model so we can build a context that is
            // identical except for the field's monomorphism.
            var probe = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));
            Assert.True(probe.Config.SchemaRegistry.TryGetInfo(typeof(FingerprintMonoModel), out var normalInfo));

            var reader = new CerasRobustSerializer(config =>
            {
                // Pre-register the modified schema; the generated registration is idempotent and
                // keeps our override (same stable name, flipped IsMonomorphic).
                var fields = normalInfo.Fields
                    .Select(f => new SchemaFieldInfo(f.StableName, f.PreviousNames, f.TypeName, !f.IsMonomorphic))
                    .ToArray();
                var modified = new SchemaTypeInfo(
                    normalInfo.StableName, normalInfo.PreviousTypeNames, normalInfo.Kind,
                    fields, normalInfo.FixedByteSize, normalInfo.EnumValues,
                    normalInfo.UnderlyingTypeName, normalInfo.ArgTypeNames);
                config.SchemaRegistry.Register(typeof(FingerprintMonoModel), modified);
                config.UseGeneratedFormatters("Tests");
            });

            ulong writerFingerprint = writer.Config.SchemaRegistry.ComputeFingerprint();
            ulong readerFingerprint = reader.Config.SchemaRegistry.ComputeFingerprint();
            Assert.NotEqual(writerFingerprint, readerFingerprint);

            // Fingerprints differ, so this load genuinely takes the interpreter path.
            var clone = reader.Load<FingerprintMonoModel>(bytes, 1);

            Assert.Equal(123, clone.X);
            Assert.Equal("value", clone.S);
        }
    }
}
