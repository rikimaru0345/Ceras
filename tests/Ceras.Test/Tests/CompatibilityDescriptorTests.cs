// The context compatibility descriptors, and the two contracts that make them usable: they are
// obtainable without producing a save, and they never report a provisional layout fingerprint as if it
// were final.
//
// ComputeFingerprint() is provisional until the manifest is sealed and baked (it excludes the root
// shapes still pending in DeclareRoot), so the descriptor reports IsFinal=false and LayoutFingerprint=0
// until the bake and never a number that would compare equal to another context's provisional one. The
// transient descriptor deliberately has no fingerprint member at all: the transient layout is not
// hashed, and that is the honest answer rather than a zero standing in for one.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.CompatibilityDescriptors
{
    // Two self-contained contexts, so "the same context twice" and "two different contexts" are both
    // expressible. CompatB deliberately has a second manifest entry and a different layout, so the two
    // descriptors cannot be accidentally equal.
    [CerasContext("CompatDescriptorA")]
    [CerasSerializable]
    public class CompatHolderA
    {
        public int Value;
        public string Name;
    }

    [CerasContext("CompatDescriptorB")]
    [CerasSerializable]
    public class CompatHolderB
    {
        public int Value;
        public CompatLeafB Leaf;
    }

    [CerasContext("CompatDescriptorB")]
    [CerasSerializable]
    public class CompatLeafB
    {
        public double Weight;
    }

    /// <summary>A root shape no generated manifest can describe, so it has to be declared up front.</summary>
    public enum CompatDeclaredRoot
    {
        None,
        Some,
    }

    public class CompatibilityDescriptorTests : TestBase
    {
        const string ContextA = "CompatDescriptorA";
        const string ContextB = "CompatDescriptorB";

        // The test assembly declares [assembly: CerasBuildTimestamp(639028224000000000L)] (see
        // BuildTimestampTests), so an ordinary serializer here is a *known* build.
        const long DeclaredBuildTimestamp = 639028224000000000L;

        static CerasRobustSerializer Robust(string context, string contextName = null,
            Action<CerasRobustConfig> extra = null)
            => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(context);
                if (contextName != null) c.SetContextName(contextName);
                extra?.Invoke(c);
            });

        static CerasRobustSerializer Baked(string context, string contextName = null)
        {
            var serializer = Robust(context, contextName);
            serializer.Save(context == ContextB
                ? (object)new CompatHolderB { Value = 1, Leaf = new CompatLeafB { Weight = 2 } }
                : new CompatHolderA { Value = 1, Name = "a" }, 1);
            return serializer;
        }

        // ── Identity ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Two_Serializers_Of_The_Same_Context_Produce_Identical_Descriptors()
        {
            var a = Robust(ContextA, ContextA);
            var b = Robust(ContextA, ContextA);

            // Before the bake as well as after: both snapshots have to be reproducible, not just the
            // final one.
            AssertSameDescriptor(a.RobustCompatibility, b.RobustCompatibility);

            a.Save(new CompatHolderA { Value = 1, Name = "x" }, 1);
            b.Save(new CompatHolderA { Value = 2, Name = "y" }, 1);   // a different graph, same layout

            AssertSameDescriptor(a.RobustCompatibility, b.RobustCompatibility);
        }

        [Fact]
        public void Two_Contexts_Produce_Different_Descriptors()
        {
            var a = Baked(ContextA, ContextA);
            var b = Baked(ContextB, ContextB);

            var da = a.RobustCompatibility;
            var db = b.RobustCompatibility;

            Assert.NotEqual(da.ContextName, db.ContextName);
            // The whole point of the fingerprint: two contexts whose layouts are not the same must not
            // compare equal, or a save of one would be taken by the other as its own.
            Assert.NotEqual(da.LayoutFingerprint, db.LayoutFingerprint);
            Assert.NotEqual(da.ManifestTypeCount, db.ManifestTypeCount);
        }

        // ── The provisional-fingerprint contract ───────────────────────────────────────────────

        [Fact]
        public void No_Fingerprint_Is_Reported_Until_The_Manifest_Is_Baked_And_Both_Fields_Flip_On_Save()
        {
            var serializer = Robust(ContextA, ContextA);

            var before = serializer.RobustCompatibility;
            Assert.False(before.IsFinal);
            Assert.Equal(0UL, before.LayoutFingerprint);

            var bytes = serializer.Save(new CompatHolderA { Value = 7, Name = "flip" }, 1);

            var after = serializer.RobustCompatibility;
            Assert.True(after.IsFinal);
            Assert.NotEqual(0UL, after.LayoutFingerprint);
            Assert.NotEqual(before.LayoutFingerprint, after.LayoutFingerprint);

            // And the final answer is stable, not recomputed per read.
            Assert.Equal(after.LayoutFingerprint, serializer.RobustCompatibility.LayoutFingerprint);

            // The save that flipped it is a real one.
            Assert.Equal(7, serializer.Load<CompatHolderA>(bytes, 1).Value);
        }

        [Fact]
        public void Reading_The_Descriptor_Seals_Nothing_And_Costs_No_Save_Or_Load()
        {
            var serializer = Robust(ContextA, ContextA);

            // The very first thing done to this serializer, with no Save/Load before it.
            var descriptor = serializer.RobustCompatibility;
            Assert.NotNull(descriptor);
            Assert.False(descriptor.IsFinal);
            Assert.True(descriptor.ManifestTypeCount > 0);

            // Reading it must not have sealed or baked anything: that is the side effect a "just tell me
            // the fingerprint" call is most likely to have, and it would fix the manifest before the
            // application had declared all of its roots.
            Assert.False(serializer.Config.SchemaRegistry.IsSealed);
            Assert.False(serializer.Config.SchemaRegistry.HasBakedSchema);

            // Repeated reads, and a descriptor that is not a reference to live state.
            Assert.Same(descriptor, serializer.RobustCompatibility);
        }

        [Fact]
        public void The_Reported_Fingerprint_Is_The_One_In_The_Saves_Bytes()
        {
            var serializer = Robust(ContextA, ContextA);
            var bytes = serializer.Save(new CompatHolderA { Value = 3, Name = "bytes" }, 1);

            ulong inHeader = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(RobustHeader.FingerprintOffset));
            Assert.Equal(inHeader, serializer.RobustCompatibility.LayoutFingerprint);
            Assert.Equal(inHeader, serializer.Config.SchemaRegistry.ComputeFingerprint());
        }

        [Fact]
        public void A_Pending_DeclaredRoot_Is_Not_Counted_Until_The_Bake_Which_Is_Why_The_Fingerprint_Is_Gated()
        {
            // The reason IsFinal exists at all. A declared root is not in the manifest yet, so the
            // fingerprint computed before the bake genuinely does not describe the context that the
            // first save will produce - and a descriptor that reported it would hand a number that
            // disagrees with the save's own header.
            var plain = Robust(ContextA, ContextA);
            var declared = Robust(ContextA, ContextA, c => c.DeclareRoot<CompatDeclaredRoot>());

            Assert.Equal(plain.RobustCompatibility.ManifestTypeCount,
                declared.RobustCompatibility.ManifestTypeCount);

            plain.Save(new CompatHolderA { Value = 1, Name = "a" }, 1);
            declared.Save(new CompatHolderA { Value = 1, Name = "a" }, 1);

            Assert.Equal(plain.RobustCompatibility.ManifestTypeCount + 1,
                declared.RobustCompatibility.ManifestTypeCount);
            Assert.NotEqual(plain.RobustCompatibility.LayoutFingerprint,
                declared.RobustCompatibility.LayoutFingerprint);
        }

        // ── What the descriptor reports ─────────────────────────────────────────────────────────

        [Fact]
        public void The_Descriptor_Reports_The_Format_Version_And_The_Producer_Build()
        {
            var descriptor = Baked(ContextA, ContextA).RobustCompatibility;

            Assert.Equal(CerasCompatibilityMode.RobustPersistent, descriptor.Mode);
            Assert.Equal(CerasRobustSerializer.FormatVersion, descriptor.FormatVersion);
            Assert.Equal(4, descriptor.FormatVersion);   // the version the loader enforces
            Assert.NotEmpty(descriptor.CerasAssemblyVersion);

            Assert.True(descriptor.ProducerBuildKnown);
            Assert.Equal(DeclaredBuildTimestamp, descriptor.ProducerBuildTimestamp);
        }

        [Fact]
        public void A_Serializer_Without_A_Build_Timestamp_Says_So_Instead_Of_Guessing()
        {
            var unknown = Robust(ContextA, ContextA, c => c.SetUnknownBuildTimestamp());
            var descriptor = unknown.RobustCompatibility;

            Assert.False(descriptor.ProducerBuildKnown);
            Assert.Equal(0L, descriptor.ProducerBuildTimestamp);

            // ... and the header agrees with the descriptor, so the two cannot drift apart.
            var bytes = unknown.Save(new CompatHolderA { Value = 1, Name = "a" }, 1);
            Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(RobustHeader.BuildTimestampOffset)));
        }

        [Fact]
        public void The_String_Table_Compression_Is_Reported_Because_A_Save_Encodes_It()
        {
            var deflate = Robust(ContextA, ContextA).RobustCompatibility;
            var plain = Robust(ContextA, ContextA, c => c.StringTableCompression = StringTableCompression.Disabled)
                .RobustCompatibility;

            Assert.Equal(StringTableCompression.Deflate, deflate.StringTableCompression);
            Assert.Equal(StringTableCompression.Disabled, plain.StringTableCompression);

            // ... and it is a header flag, not a layout decision: the two contexts still share a layout.
            var a = Baked(ContextA, ContextA);
            var b = Robust(ContextA, ContextA, c => c.StringTableCompression = StringTableCompression.Disabled);
            b.Save(new CompatHolderA { Value = 1, Name = "a" }, 1);
            Assert.Equal(a.RobustCompatibility.LayoutFingerprint, b.RobustCompatibility.LayoutFingerprint);
        }

        // ── The context name ───────────────────────────────────────────────────────────────────

        [Fact]
        public void The_Context_Name_Is_Null_Until_It_Is_Declared()
        {
            // The runtime never learns the generated name, so a serializer that was not told reports
            // none. Null, and not the name guessed out of the manifest: a handshake that compared a
            // guess would be comparing something the application never said.
            var undeclared = Robust(ContextA).RobustCompatibility;
            Assert.Null(undeclared.ContextName);

            var declared = Robust(ContextA, ContextA).RobustCompatibility;
            Assert.Equal(ContextA, declared.ContextName);
        }

        [Fact]
        public void A_Name_That_Cannot_Identify_A_Context_Is_Not_Declared_Rather_Than_Empty()
        {
            foreach (string name in new[] { "", "   ", "\t" })
            {
                var descriptor = Robust(ContextA, name).RobustCompatibility;
                Assert.Null(descriptor.ContextName);
            }

            // An explicit null is the same answer as never calling the setter at all, and must not be
            // distinguishable from it: a name that cannot identify a context is not a name.
            var nullName = Robust(ContextA, extra: c => c.SetContextName(null)).RobustCompatibility;
            var notCalled = Robust(ContextA).RobustCompatibility;
            Assert.Null(nullName.ContextName);
            Assert.Equal(notCalled.ContextName, nullName.ContextName);
        }

        [Fact]
        public void A_Context_Name_Cannot_Be_Changed_After_The_Config_Is_Sealed()
        {
            // The same convention as every other setting: the seal check runs first, so even a same-value
            // no-op throws, and one serializer instance cannot start claiming another context.
            var serializer = Robust(ContextA, ContextA);
            Assert.Throws<InvalidOperationException>(() => serializer.Config.SetContextName("Something Else"));

            var raw = new CerasFastSerializer(c => c.SetContextName("Transient"));
            Assert.Throws<InvalidOperationException>(() => raw.Config.SetContextName("Something Else"));
        }

        // ── The transient descriptor ────────────────────────────────────────────────────────────

        [Fact]
        public void The_Transient_Descriptor_Reports_Its_Mode_And_Context_Name()
        {
            var undeclared = new CerasFastSerializer().TransientCompatibility;
            Assert.Equal(CerasCompatibilityMode.TransientNetwork, undeclared.Mode);
            Assert.Null(undeclared.ContextName);
            Assert.NotEmpty(undeclared.CerasAssemblyVersion);

            var declared = new CerasFastSerializer(c => c.SetContextName("Net")).TransientCompatibility;
            Assert.Equal("Net", declared.ContextName);
        }

        [Fact]
        public void The_Transient_Descriptor_Reflects_The_Reinterpret_Formatter()
        {
            // Not a performance knob: it changes the bytes for blittable structs and there is no version
            // tolerance at all, so a connection setup has to be able to see which one is in use.
            var withReinterpret = new CerasFastSerializer(c => c.UseReinterpretFormatter = true).TransientCompatibility;
            var withoutReinterpret = new CerasFastSerializer(c => c.UseReinterpretFormatter = false).TransientCompatibility;

            Assert.True(withReinterpret.UseReinterpretFormatter);
            Assert.False(withoutReinterpret.UseReinterpretFormatter);
        }

        [Fact]
        public void The_Transient_Descriptor_Has_No_Fingerprint_Member_At_All()
        {
            // The robust one has a fingerprint precisely because its layout is hashed. The transient
            // layout is not, is not stable across compiles of the application's own types, and cannot be
            // compared against anything - so a member here would invite exactly the check the mode
            // cannot support, and a zero would look like an answer.
            Assert.Empty(typeof(TransientCompatibilityDescriptor)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.Contains("Fingerprint")));
            Assert.Empty(typeof(TransientCompatibilityDescriptor)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(ulong)));

            // The contrast that makes the absence deliberate rather than an oversight.
            Assert.NotEmpty(typeof(RobustCompatibilityDescriptor)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name == "LayoutFingerprint"));
        }

        // ── The descriptors are snapshots ───────────────────────────────────────────────────────

        [Theory]
        [InlineData(typeof(RobustCompatibilityDescriptor))]
        [InlineData(typeof(TransientCompatibilityDescriptor))]
        public void Every_Descriptor_Property_Is_Read_Only(Type descriptorType)
        {
            var properties = descriptorType.GetProperties(BindingFlags.Public | BindingFlags.Instance).ToArray();
            Assert.NotEmpty(properties);

            foreach (var property in properties)
            {
                Assert.True(property.CanRead, $"{descriptorType.Name}.{property.Name} is not readable.");
                Assert.False(property.CanWrite, $"{descriptorType.Name}.{property.Name} has a setter.");
                Assert.Null(property.SetMethod);
            }
        }

        [Theory]
        [InlineData(typeof(RobustCompatibilityDescriptor))]
        [InlineData(typeof(TransientCompatibilityDescriptor))]
        public void No_Descriptor_Exposes_A_Collection(Type descriptorType)
        {
            // A collection member would be a live view of mutable state behind an "immutable snapshot",
            // and it would make the descriptor's own comparison (two contexts, two descriptors) an
            // exercise in walking collections instead of a value compare. `string` is enumerable in the
            // BCL sense (IEnumerable<char>) and is a value here, so it is named as the one exception.
            foreach (var property in descriptorType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var type = property.PropertyType;
                bool isCollection = type != typeof(string)
                                    && (type.IsArray
                                        || typeof(IEnumerable).IsAssignableFrom(type)
                                        || typeof(IDictionary).IsAssignableFrom(type)
                                        || (type.IsGenericType && IsCollectionInterface(type.GetGenericTypeDefinition())));

                Assert.False(isCollection,
                    $"{descriptorType.Name}.{property.Name} exposes {type.Name}, which is enumerable; a descriptor is a value.");
            }
        }

        [Fact]
        public void The_Descriptors_Are_Sealed_And_Have_No_Public_Constructor()
        {
            // Sealed, so nobody can add a settable property or an IEnumerable later and still call it one
            // of these; and no public constructor, so IsFinal/Mode can only be produced by the library.
            foreach (var type in new[] { typeof(RobustCompatibilityDescriptor), typeof(TransientCompatibilityDescriptor) })
            {
                Assert.True(type.IsSealed, $"{type.Name} is not sealed.");
                Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            }

            // The mode is a closed set, not a free integer.
            Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(CerasCompatibilityMode)));
            Assert.Equal(new[] { "RobustPersistent", "TransientNetwork" }, Enum.GetNames(typeof(CerasCompatibilityMode)));
        }

        static bool IsCollectionInterface(Type genericTypeDefinition)
            => genericTypeDefinition == typeof(IReadOnlyCollection<>) || genericTypeDefinition == typeof(IReadOnlyList<>);

        static void AssertSameDescriptor(RobustCompatibilityDescriptor a, RobustCompatibilityDescriptor b)
        {
            Assert.Equal(a.Mode, b.Mode);
            Assert.Equal(a.ContextName, b.ContextName);
            Assert.Equal(a.FormatVersion, b.FormatVersion);
            Assert.Equal(a.IsFinal, b.IsFinal);
            Assert.Equal(a.LayoutFingerprint, b.LayoutFingerprint);
            Assert.Equal(a.ManifestTypeCount, b.ManifestTypeCount);
            Assert.Equal(a.ProducerBuildTimestamp, b.ProducerBuildTimestamp);
            Assert.Equal(a.ProducerBuildKnown, b.ProducerBuildKnown);
            Assert.Equal(a.StringTableCompression, b.StringTableCompression);
            Assert.Equal(a.CerasAssemblyVersion, b.CerasAssemblyVersion);
        }
    }
}
