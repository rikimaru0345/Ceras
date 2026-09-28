using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

// The producer build timestamp is declared once per owning assembly, not at serializer construction or
// save time. Tests use a fixed value so build-ordering behavior is deterministic.
[assembly: CerasBuildTimestamp(639028224000000000L)]

namespace Ceras.Test.BuiltinShapes
{
    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsBuildHolder { public int Value; }

    /// <summary>
    /// The producer build timestamp is supplied once per owning build via an
    /// assembly-level <see cref="CerasBuildTimestampAttribute"/> (MSBuild-friendly), recorded in the
    /// header, kept out of the layout fingerprint, and used to reject a newer save before payload
    /// decoding. Unknown is an explicit, visible mode.
    /// </summary>
    public class BuildTimestampTests : TestBase
    {
        const string Ctx = "BuiltinShapes";

        static CerasRobustSerializer New() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        [Fact]
        public void Assembly_Attribute_Supplies_The_Default_BuildTimestamp()
        {
            var s = New();
            Assert.True(s.Config.BuildTimestampKnown);
            Assert.Equal(639028224000000000L, s.Config.BuildTimestamp);
        }

        [Fact]
        public void BuildInfo_Reads_The_Assembly_Attribute()
        {
            Assert.Equal(639028224000000000L, BuildInfo.GetProducerBuildTimestamp(typeof(BuildTimestampTests).Assembly));
            // An assembly without the attribute reports 0 (unknown).
            Assert.Equal(0, BuildInfo.GetProducerBuildTimestamp(typeof(CerasSerializer).Assembly));
        }

        [Fact]
        public void Timestamp_Is_Not_Part_Of_The_Layout_Fingerprint()
        {
            var a = new CerasRobustSerializer(c => { c.UseGeneratedFormatters(Ctx); c.BuildTimestamp = 100; });
            var b = new CerasRobustSerializer(c => { c.UseGeneratedFormatters(Ctx); c.BuildTimestamp = 999999; });
            a.Save(new BsBuildHolder { Value = 1 }, 1);
            b.Save(new BsBuildHolder { Value = 1 }, 1);
            Assert.Equal(a.Config.SchemaRegistry.ComputeFingerprint(), b.Config.SchemaRegistry.ComputeFingerprint());
        }

        [Fact]
        public void Newer_Build_Is_Rejected_Before_Payload_Decoding_With_Identical_Layouts()
        {
            // Same layout/fingerprint, different producer build timestamps.
            var newer = new CerasRobustSerializer(c => { c.UseGeneratedFormatters(Ctx); c.BuildTimestamp = 500; });
            var bytes = newer.Save(new BsBuildHolder { Value = 7 }, 1);

            // The rejection must happen before the payload is decoded. Corrupt the payload region (keeping
            // the CRC valid) so a decode attempt would throw a *different*, data-shaped exception.
            CorruptPayloadKeepingValidChecksum(bytes);

            var older = new CerasRobustSerializer(c => { c.UseGeneratedFormatters(Ctx); c.BuildTimestamp = 400; });
            var ex = Assert.Throws<Ceras.Exceptions.CerasException>(() => older.Load<BsBuildHolder>(bytes, 1));
            Assert.Contains("newer build", ex.Message);

            // Same build timestamp passes even with identical layout.
            var same = new CerasRobustSerializer(c => { c.UseGeneratedFormatters(Ctx); c.BuildTimestamp = 500; });
            Assert.NotNull(same.Load<BsBuildHolder>(RebuildValid(bytes), 1));

            // Older producer build also passes.
            var newerReader = new CerasRobustSerializer(c => { c.UseGeneratedFormatters(Ctx); c.BuildTimestamp = 600; });
            Assert.NotNull(newerReader.Load<BsBuildHolder>(RebuildValid(bytes), 1));
        }

        [Fact]
        public void Explicit_Unknown_Mode_Disables_The_Gate_Visibly()
        {
            var writer = new CerasRobustSerializer(c => { c.UseGeneratedFormatters(Ctx); c.BuildTimestamp = 5000; });
            var bytes = writer.Save(new BsBuildHolder { Value = 3 }, 1);

            var unknown = new CerasRobustSerializer(c => { c.UseGeneratedFormatters(Ctx); c.SetUnknownBuildTimestamp(); });
            Assert.False(unknown.Config.BuildTimestampKnown);
            Assert.Equal(0, unknown.Config.BuildTimestamp);
            Assert.Equal(3, unknown.Load<BsBuildHolder>(bytes, 1).Value);

            // The header of a save written by an unknown build records 0.
            var unknownWriter = new CerasRobustSerializer(c => { c.UseGeneratedFormatters(Ctx); c.SetUnknownBuildTimestamp(); });
            var unknownBytes = unknownWriter.Save(new BsBuildHolder { Value = 4 }, 1);
            Assert.Equal(0, BitConverter.ToInt64(unknownBytes, RobustHeader.BuildTimestampOffset));
        }

        // ── Where the owning assembly comes from ────────────────────────────────────────
        //
        // The owning assembly is an explicit input, because the implicit way of finding it - a stack
        // walk through Assembly.GetCallingAssembly() - is a property of the runtime, not of Ceras:
        // NativeAOT compiles IL ahead of time, implements no stack to walk, and throws
        // PlatformNotSupportedException from the call, so a serializer that needs the answer
        // unconditionally cannot even be constructed there.
        //
        // What has to be true on every runtime that *can* walk a stack is that the parameterless
        // constructor still finds the calling assembly by itself, because that is what puts the
        // timestamp in a plain `new CerasRobustSerializer(c => ...)` today. The first test below is
        // written so that it asserts the honest answer rather than a fixed one on a runtime where the
        // walk is not available: it names the capability it depends on, so a run that fails it says
        // why instead of silently meaning "unknown build".

        static readonly Assembly ThisAssembly = typeof(BuildTimestampTests).Assembly;
        const long Declared = 639028224000000000L;

        [Fact]
        public void The_Test_Assembly_Declares_A_Known_Build()
        {
            // The precondition for every test in this block, and the reason the value below is fixed:
            // the attribute is declared once, at the top of this file.
            Assert.Equal(Declared, BuildInfo.GetProducerBuildTimestamp(ThisAssembly));
            Assert.Equal(0, BuildInfo.GetProducerBuildTimestamp(typeof(CerasSerializer).Assembly));
        }

        [Fact]
        public void Parameterless_Constructor_Still_Discovers_The_Calling_Assembly()
        {
            // The regression this whole change risks: on a runtime that can walk a stack, the
            // parameterless constructor must still find the caller, because a game that never passes an
            // assembly anywhere depends on the timestamp landing in the header without being asked for.
            var s = New();

            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                Assert.True(s.Config.BuildTimestampKnown,
                    "a runtime that supports dynamic code can walk a stack, so the calling assembly should have been found");
                Assert.Equal(Declared, s.Config.BuildTimestamp);
                Assert.Equal(Declared, BitConverter.ToInt64(s.Save(new BsBuildHolder { Value = 1 }, 1), RobustHeader.BuildTimestampOffset));
            }
            else
            {
                // NativeAOT and friends: nothing thrown, and no build invented either. This is the
                // behaviour the capability check exists to produce, and it is asserted rather than
                // skipped so a runtime that reports the capability wrongly fails here.
                Assert.False(s.Config.BuildTimestampKnown);
                Assert.Equal(0, s.Config.BuildTimestamp);
                Assert.Equal(0, BitConverter.ToInt64(s.Save(new BsBuildHolder { Value = 1 }, 1), RobustHeader.BuildTimestampOffset));
            }
        }

        [Fact]
        public void Explicit_Constructor_Assembly_Gives_The_Same_Answer_As_The_Discovered_One()
        {
            var discovered = New();
            var explicitAssembly = new CerasRobustSerializer(ThisAssembly, c => c.UseGeneratedFormatters(Ctx));

            Assert.Equal(Declared, explicitAssembly.Config.BuildTimestamp);
            Assert.Equal(discovered.Config.BuildTimestamp, explicitAssembly.Config.BuildTimestamp);

            // And it is the same on the wire, byte for byte: the two serializers differ only in how
            // they were handed the assembly.
            Assert.Equal(discovered.Save(new BsBuildHolder { Value = 9 }, 1),
                         explicitAssembly.Save(new BsBuildHolder { Value = 9 }, 1));
        }

        [Fact]
        public void OwningAssembly_Property_Gives_The_Same_Answer_As_The_Discovered_One()
        {
            // The same input one step later, for the shape a caller already uses: the configure
            // callback. Under NativeAOT this is the only way to get a timestamp at all.
            var discovered = New();
            var viaProperty = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.OwningAssembly = ThisAssembly;
            });

            Assert.Equal(Declared, viaProperty.Config.BuildTimestamp);
            Assert.Equal(discovered.Save(new BsBuildHolder { Value = 9 }, 1),
                         viaProperty.Save(new BsBuildHolder { Value = 9 }, 1));
        }

        [Fact]
        public void OwningAssembly_In_The_Callback_Overrides_The_Constructor_Argument()
        {
            // The two are the same input, so the last one stated wins - with the explicit
            // BuildTimestamp outranking both, and none of it depending on the order the caller wrote
            // the two lines in.
            var fromCallback = new CerasRobustSerializer(typeof(CerasSerializer).Assembly, c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.OwningAssembly = ThisAssembly;
            });
            Assert.Equal(Declared, fromCallback.Config.BuildTimestamp);

            var timestampLast = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.OwningAssembly = ThisAssembly;
                c.BuildTimestamp = 4242;
            });
            Assert.Equal(4242, timestampLast.Config.BuildTimestamp);

            var timestampFirst = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.BuildTimestamp = 4242;
                c.OwningAssembly = ThisAssembly;
            });
            Assert.Equal(4242, timestampFirst.Config.BuildTimestamp);
            // The assembly was still recorded - it just no longer decides the timestamp.
            Assert.Equal(ThisAssembly, timestampFirst.Config.OwningAssembly);
        }

        [Fact]
        public void An_Assembly_Without_The_Attribute_Reports_The_Build_As_Unknown()
        {
            // Ceras' own assembly declares no CerasBuildTimestamp, so naming it is a real answer - and
            // the answer is "unknown build", not a guess at Ceras' build time.
            var s = new CerasRobustSerializer(typeof(CerasSerializer).Assembly, c => c.UseGeneratedFormatters(Ctx));

            Assert.False(s.Config.BuildTimestampKnown);
            Assert.Equal(0, s.Config.BuildTimestamp);
            Assert.False(s.RobustCompatibility.ProducerBuildKnown);
            Assert.Equal(0, s.RobustCompatibility.ProducerBuildTimestamp);
            Assert.Equal(0, BitConverter.ToInt64(s.Save(new BsBuildHolder { Value = 2 }, 1), RobustHeader.BuildTimestampOffset));
        }

        [Fact]
        public void An_Unset_Or_Null_Owning_Assembly_Reports_The_Build_As_Unknown()
        {
            // Both spellings of "no owning assembly", and neither invents a build: the constructor
            // argument and the property. This is the state a NativeAOT serializer is in by default,
            // stated explicitly here so it is a pinned contract rather than a side effect.
            var nullArgument = new CerasRobustSerializer((Assembly)null, c => c.UseGeneratedFormatters(Ctx));
            Assert.Null(nullArgument.Config.OwningAssembly);
            Assert.False(nullArgument.Config.BuildTimestampKnown);
            Assert.Equal(0, nullArgument.Config.BuildTimestamp);

            var nullProperty = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.OwningAssembly = null;
            });
            Assert.Null(nullProperty.Config.OwningAssembly);
            Assert.False(nullProperty.Config.BuildTimestampKnown);
            Assert.Equal(0, nullProperty.Config.BuildTimestamp);
            Assert.False(nullProperty.RobustCompatibility.ProducerBuildKnown);
            Assert.Equal(0, BitConverter.ToInt64(nullProperty.Save(new BsBuildHolder { Value = 3 }, 1), RobustHeader.BuildTimestampOffset));
        }

        [Fact]
        public void Null_Owning_Assembly_Overrides_A_Known_One_And_Says_Unknown()
        {
            // "No owning assembly" is a value, not an absence of one: handing the serializer the
            // test assembly and then taking it away leaves the unknown-build mode, not the old value.
            var s = new CerasRobustSerializer(ThisAssembly, c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.OwningAssembly = null;
            });

            Assert.False(s.Config.BuildTimestampKnown);
            Assert.Equal(0, s.Config.BuildTimestamp);
        }

        [Fact]
        public void OwningAssembly_Is_Sealed_Once_The_Serializer_Exists()
        {
            // Every other config setting is sealed, and this one has to be too: which build a live
            // serializer claims to be must not change after the first save was written with it.
            var s = New();

            var ex = Assert.Throws<InvalidOperationException>(() => s.Config.OwningAssembly = ThisAssembly);
            Assert.Contains("sealed", ex.Message, StringComparison.OrdinalIgnoreCase);

            // A same-value no-op throws as well, matching every other sealed setter.
            Assert.Throws<InvalidOperationException>(() => s.Config.BuildTimestamp = s.Config.BuildTimestamp);
        }

        [Fact]
        public void The_Raw_Context_Has_No_Owning_Assembly_At_All()
        {
            // CerasFastSerializer is the control for "is this pattern elsewhere?": the transient
            // format has no header, records no producer build, and asks for no assembly. If it ever
            // grows one, this is the test that has to be re-read.
            var raw = new CerasFastSerializer();
            Assert.IsType<CerasRawConfig>(raw.Config);
            Assert.Null(typeof(CerasRawConfig).GetProperty("OwningAssembly"));
            Assert.DoesNotContain(typeof(CerasFastSerializer).GetConstructors(),
                c => c.GetParameters().Any(p => p.ParameterType == typeof(Assembly)));
        }

        static void CorruptPayloadKeepingValidChecksum(byte[] bytes)
        {
            RobustHeader.Read(bytes, out _, out _, out _, out long dataOffset, out long stringOffset, out _);
            for (long i = dataOffset; i < stringOffset && i < dataOffset + 4; i++)
                bytes[i] ^= 0xFF;
            FixRobustChecksum(bytes);
        }

        static byte[] RebuildValid(byte[] bytes)
        {
            // The corruption above only exists to prove ordering; rebuild a clean copy for the passing cases.
            var s = new CerasRobustSerializer(c => { c.UseGeneratedFormatters(Ctx); c.BuildTimestamp = 500; });
            return s.Save(new BsBuildHolder { Value = 7 }, 1);
        }
    }
}
