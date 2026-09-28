using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

// A migration maps a derived type's members back from the names an older file recorded, and the map
// decides which stored values are read and which are dropped. It used to be built from
// Type.GetFields(Public|NonPublic|Instance), which reaches a base type's public and protected fields
// but never its private ones -- and an auto-property's backing field is private by definition. So
// every *inherited* auto-property was missing from the map: the file's value was skipped, the object
// kept its declared default, and the load reported success. Both writers (the source generator and
// the reflection schema) walk the base chain, so those members are in the payload of every file they
// wrote. The tests here pin the writer's member rules, not the reflection call that lost them.
namespace Ceras.Test.InheritedMigration
{
    // ── The shape the loss needs: a base type whose state lives in auto-properties ────────────────
    [CerasContext("Tests")]
    [CerasSerializable]
    public abstract class InheritedBase
    {
        // Private backing fields on a base type: the members the map could not see.
        public int Position { get; set; }
        public int EntityId { get; set; }

        // A base *public* field, which the same reflection call did reach. Kept so these tests show
        // the loss is about auto-properties rather than about inheritance as such.
        public int PublicBaseField;

        // A field-like event: its compiler-generated backing field is another private base field, and
        // no auto-property stands in for it, so it is not a member of anything. The writer excludes it
        // (an event's backing field is compiler-generated and not an auto-property), so the map must
        // not offer it either -- a base-chain walk that skipped no fields would start offering it.
        public event EventHandler BaseEvent;
        public void RaiseBaseEvent() => BaseEvent?.Invoke(this, EventArgs.Empty);
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class InheritedLeaf : InheritedBase
    {
        public int LeafValue;
        public int LeafProperty { get; set; }
    }

    // ── Property-level attributes: the property is what the user writes, the field is what exists ─
    [CerasContext("Tests")]
    [CerasSerializable]
    public class PropertyAttributeModel
    {
        public int Plain;

        // A wire name declared on the property. The writers honour it (the generator reads the
        // associated property, TypeConfigDefaults reads memberConfig.AssociatedProperty), so a file
        // records "pos" and never "Position". A map built from the field's own attributes sees no
        // attribute at all and offers the name "Position" instead.
        [CerasStableName("pos")]
        public int Position { get; set; }

        // An old wire name declared on the property, same argument.
        [PreviousName("OldRenamed")]
        public int Renamed { get; set; }

        // Removed from the payload by the writers; a file written by this build cannot contain it, so
        // the map must not offer a destination for it.
        [Exclude]
        public int Secret { get; set; }

        // A [NonSerialized] field, which the map already skipped before this fix and must keep
        // skipping: the walk this test is about is a new *source* of members, not a new rule.
        [NonSerialized]
        public int Transient;

        [OnAfterDeserialize]
        private void After(MigrationContext context) => InheritedMigrationProbe.LastContext = context;
    }

    // The same three attributes on a *base* auto-property, which is where the two defects compound:
    // the member is invisible, and the name it would be offered under is the property name.
    [CerasContext("Tests")]
    [CerasSerializable]
    public abstract class NamedPropertyBase
    {
        [CerasStableName("pos")]
        public int Position { get; set; }

        [PreviousName("OldRenamed")]
        public int Renamed { get; set; }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class NamedPropertyLeaf : NamedPropertyBase
    {
        public int LeafValue;
    }

    public static class InheritedMigrationProbe
    {
        // Left null by Reset() until an [OnAfterDeserialize] hands one over, so the reads below say
        // so with `!` at the point of use. The test project builds with nullable annotations off --
        // the same reason SystemArraySlotMigrationTests.LabelFor's parameter carries no annotation --
        // so the field is a plain reference field and a `?` here is CS8632. Runtime behaviour is
        // either way: MigrationContext is a class, so `T?` would only have been an annotation.
        public static MigrationContext LastContext;
        public static void Reset() => LastContext = null;
    }

    // Carries an [OnAfterDeserialize] callback so a test can read what the migration told the
    // application about the members it did and did not read.
    [CerasContext("Tests")]
    [CerasSerializable]
    public class AddedFieldsProbe
    {
        public int PublicField;

        // Mapped from the file, so it must NOT be reported as an added field even though it is an
        // auto-property: the plan recorded the backing field's compiler-generated name for it.
        public int MappedProperty { get; set; }

        // Absent from the file, so it must be reported.
        public int TrulyAdded { get; set; }

        [OnAfterDeserialize]
        private void After(MigrationContext context) => InheritedMigrationProbe.LastContext = context;
    }

    public class InheritedMemberMigrationTests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        // ── The reproduction: inherited auto-properties ───────────────────────────────────────────

        [Fact]
        public void InheritedAutoProperties_AreRead_NotLeftAtTheirDefaults()
        {
            var bytes = BuildLegacyFile(
                typeof(InheritedLeaf),
                "Ceras.Test.InheritedMigration.InheritedLeaf",
                new[]
                {
                    new SchemaFieldInfo("Position", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("EntityId", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("PublicBaseField", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("LeafValue", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("LeafProperty", Array.Empty<string>(), "System.Int32", true),
                },
                payload =>
                {
                    SerializerBinary.WriteInt32Fixed(payload, 42);   // Position
                    SerializerBinary.WriteInt32Fixed(payload, 7);    // EntityId
                    SerializerBinary.WriteInt32Fixed(payload, 1);    // PublicBaseField
                    SerializerBinary.WriteInt32Fixed(payload, 2);    // LeafValue
                    SerializerBinary.WriteInt32Fixed(payload, 3);    // LeafProperty
                });

            var loaded = Generated().Load<InheritedLeaf>(bytes, 1);

            // The two inherited auto-properties are the whole point: 0 here means the stored value was
            // dropped and the field initializer stood in for it.
            Assert.Equal(42, loaded.Position);
            Assert.Equal(7, loaded.EntityId);

            // The members the old reflection call could already reach keep working, so this test
            // isolates the loss rather than reporting a general breakage.
            Assert.Equal(1, loaded.PublicBaseField);
            Assert.Equal(2, loaded.LeafValue);
            Assert.Equal(3, loaded.LeafProperty);
        }

        // ── The map itself, which is where the fix lives ──────────────────────────────────────────

        static Dictionary<string, FieldInfo> BuildCurrentFieldMap(Type type)
        {
            // Same reflection probe MigrationPlanBuilderTests uses: the method is internal, and the
            // test is about the map's contents, so it asks for the map rather than a load.
            var method = typeof(SchemaRegistry).Assembly.GetType("Ceras.Format.MigrationPlanBuilder")
                .GetMethod("BuildCurrentFieldMap", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (Dictionary<string, FieldInfo>)method.Invoke(null, new object[] { type })!;
        }

        [Fact]
        public void BuildCurrentFieldMap_ReachesABaseTypesPrivateAutoPropertyFields()
        {
            var map = BuildCurrentFieldMap(typeof(InheritedLeaf));

            // Declared on the base, which is what makes the fields private and therefore unreachable
            // for a call that does not walk the chain itself.
            Assert.Equal(typeof(InheritedBase), map["Position"].DeclaringType);
            Assert.Equal(typeof(InheritedBase), map["EntityId"].DeclaringType);
            Assert.Equal(typeof(InheritedBase), map["PublicBaseField"].DeclaringType);
            Assert.Equal(typeof(InheritedLeaf), map["LeafProperty"].DeclaringType);
        }

        [Fact]
        public void BuildCurrentFieldMap_DoesNotOfferACompilerGeneratedFieldThatIsNotAMember()
        {
            // The base chain brings in fields the writers never serialized. An event's backing field
            // is compiler-generated and no property stands in for it, so it is not a member -- and a
            // file can never contain a field by that name. Offering it would be harmless today and
            // wrong the moment someone names a real member "BaseEvent".
            var map = BuildCurrentFieldMap(typeof(InheritedLeaf));

            Assert.False(map.ContainsKey("BaseEvent"),
                "an event's backing field is not a serialized member and must not be offered as a migration target");
        }

        [Fact]
        public void BuildCurrentFieldMap_GivesADerivedMemberPriorityOverTheBaseOneItShadows()
        {
            var map = BuildCurrentFieldMap(typeof(ShadowingLeaf));

            // `new` on both sides: the generator drops the base version of a shadowed auto-property, and
            // a map built derived-first with first-wins insertion is what makes that happen here too.
            Assert.Equal(typeof(ShadowingLeaf), map["Shadows"].DeclaringType);
        }

        [Fact]
        public void BuildCurrentFieldMap_StillHonoursTheExclusionsItHonouredBefore()
        {
            var map = BuildCurrentFieldMap(typeof(PropertyAttributeModel));

            // [Exclude] on the property removes the member, exactly as it does for the writers.
            Assert.False(map.ContainsKey("Secret"),
                "[Exclude] on the auto-property's property must keep the member out of the map");
            Assert.False(map.ContainsKey("<Secret>k__BackingField"),
                "the excluded member must not be reachable under its compiler-generated name either");

            // A [NonSerialized] field is still skipped, as before.
            Assert.False(map.ContainsKey("Transient"),
                "[NonSerialized] members are still not migration targets");
        }

        [Fact]
        public void BuildCurrentFieldMap_TakesTheWireNameFromThePropertyNotOnlyTheField()
        {
            var map = BuildCurrentFieldMap(typeof(PropertyAttributeModel));

            // What a file actually recorded. The writers read [CerasStableName] off the property, so
            // this map has to offer the same name for the member to be found at all.
            Assert.Equal(typeof(PropertyAttributeModel), map["pos"].DeclaringType);

            // [PreviousName] on the property is an old wire name for the same member.
            Assert.Equal(typeof(PropertyAttributeModel), map["OldRenamed"].DeclaringType);

            // The C# name of a property whose wire name was pinned is not a name any file used, so it
            // must not answer for it: mapping "Position" onto the member would let a name the writers
            // never emitted claim this member's value.
            Assert.False(map.ContainsKey("Position"),
                "a [CerasStableName] auto-property is only reachable under the pinned wire name");
        }

        // ── Property-level attributes, through a load ─────────────────────────────────────────────

        [Fact]
        public void PropertyLevelStableNameAndPreviousName_AreMigrated()
        {
            var bytes = BuildLegacyFile(
                typeof(PropertyAttributeModel),
                "Ceras.Test.InheritedMigration.PropertyAttributeModel",
                new[]
                {
                    new SchemaFieldInfo("Plain", Array.Empty<string>(), "System.Int32", true),
                    // The name this build's writers put on the wire, not the C# property name.
                    new SchemaFieldInfo("pos", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("OldRenamed", Array.Empty<string>(), "System.Int32", true),
                },
                payload =>
                {
                    SerializerBinary.WriteInt32Fixed(payload, 10);   // Plain
                    SerializerBinary.WriteInt32Fixed(payload, 20);   // pos      -> Position
                    SerializerBinary.WriteInt32Fixed(payload, 30);   // OldRenamed -> Renamed
                });

            var loaded = Generated().Load<PropertyAttributeModel>(bytes, 1);

            Assert.Equal(10, loaded.Plain);
            Assert.Equal(20, loaded.Position);
            Assert.Equal(30, loaded.Renamed);
        }

        [Fact]
        public void PropertyLevelExclude_IsNotAMigrationDestination()
        {
            var bytes = BuildLegacyFile(
                typeof(PropertyAttributeModel),
                "Ceras.Test.InheritedMigration.PropertyAttributeModel",
                new[]
                {
                    new SchemaFieldInfo("Plain", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("pos", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("OldRenamed", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("Secret", Array.Empty<string>(), "System.Int32", true),
                },
                payload =>
                {
                    SerializerBinary.WriteInt32Fixed(payload, 10);
                    SerializerBinary.WriteInt32Fixed(payload, 20);
                    SerializerBinary.WriteInt32Fixed(payload, 30);
                    SerializerBinary.WriteInt32Fixed(payload, 99);   // Secret, a member this build does not write
                });

            InheritedMigrationProbe.Reset();
            var loaded = Generated().Load<PropertyAttributeModel>(bytes, 1);

            // The stored 99 must not survive into a member this build does not serialize, and the
            // migration has to say it dropped the value rather than quietly reassigning it.
            Assert.Equal(0, loaded.Secret);
            Assert.Contains("Secret", InheritedMigrationProbe.LastContext!.SkippedFields);
        }

        [Fact]
        public void InheritedPropertyLevelStableNameAndPreviousName_AreMigrated()
        {
            var bytes = BuildLegacyFile(
                typeof(NamedPropertyLeaf),
                "Ceras.Test.InheritedMigration.NamedPropertyLeaf",
                new[]
                {
                    new SchemaFieldInfo("pos", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("OldRenamed", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("LeafValue", Array.Empty<string>(), "System.Int32", true),
                },
                payload =>
                {
                    SerializerBinary.WriteInt32Fixed(payload, 20);
                    SerializerBinary.WriteInt32Fixed(payload, 30);
                    SerializerBinary.WriteInt32Fixed(payload, 40);
                });

            var loaded = Generated().Load<NamedPropertyLeaf>(bytes, 1);

            Assert.Equal(20, loaded.Position);
            Assert.Equal(30, loaded.Renamed);
            Assert.Equal(40, loaded.LeafValue);
        }

        // ── MigrationContext.AddedFields ───────────────────────────────────────────────────────────

        [Fact]
        public void AddedFields_DoesNotReportAnAutoPropertyTheFileActuallyMapped()
        {
            var bytes = BuildLegacyFile(
                typeof(AddedFieldsProbe),
                "Ceras.Test.InheritedMigration.AddedFieldsProbe",
                new[]
                {
                    new SchemaFieldInfo("PublicField", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("MappedProperty", Array.Empty<string>(), "System.Int32", true),
                },
                payload =>
                {
                    SerializerBinary.WriteInt32Fixed(payload, 5);
                    SerializerBinary.WriteInt32Fixed(payload, 6);
                });

            InheritedMigrationProbe.Reset();
            var loaded = Generated().Load<AddedFieldsProbe>(bytes, 1);

            Assert.Equal(5, loaded.PublicField);
            Assert.Equal(6, loaded.MappedProperty);

            var context = InheritedMigrationProbe.LastContext!;
            Assert.True(context.WasMigrated);
            Assert.DoesNotContain("MappedProperty", context.AddedFields);
            Assert.Contains("TrulyAdded", context.AddedFields);
        }

        // ── The legacy fixture, in the shape ConstructionReuseContractTests.BuildLegacyFile writes ──
        // A hand-built robust file (header + schema + one class payload) whose schema records
        // something other than the current manifest, plus a fingerprint of 0 so the load is forced
        // through the migration interpreter rather than the generated fast path. Every member is an
        // int, so the payload is exactly the schema's field order in fixed-width values.

        static byte[] BuildLegacyFile(Type clrType, string stableName, SchemaFieldInfo[] oldFields,
            Action<ArrayBufferWriter<byte>> writePayload)
        {
            var oldRegistry = new CerasRobustConfig().SchemaRegistry;
            oldRegistry.Register(clrType, new SchemaTypeInfo(stableName, Array.Empty<string>(),
                SchemaTypeKind.Class, oldFields, 0));
            oldRegistry.TryGetId(clrType, out int oldTypeId);

            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);

            var intern = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, oldRegistry, intern);
            int schemaStringOffset = offset;
            intern.Flush(ref buffer, ref offset);
            int dataStart = offset;

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)oldTypeId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            writePayload(payload);

            var data = payload.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;

            int stringTableStart = offset;
            var payloadIntern = new StringInternTable();
            payloadIntern.Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(buffer, offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return buffer[..offset];
        }
    }

    // A derived-first collision: the same member name declared on both sides of the inheritance
    // boundary. Auto-properties, because that is the only shadowing a [CerasSerializable] type may
    // actually contain — two plain fields under one name is CERAS009, an error. For an auto-property
    // the generator keeps the derived one and drops the base one (`claimedBackingFieldNames`), and a
    // map built derived-first with first-wins insertion is what makes the migration agree.
    [CerasContext("Tests")]
    [CerasSerializable]
    public abstract class ShadowingBase
    {
        public int Shadows { get; set; }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ShadowingLeaf : ShadowingBase
    {
        public new int Shadows { get; set; }
    }
}
