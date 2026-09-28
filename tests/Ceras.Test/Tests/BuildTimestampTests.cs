using System;
using System.Reflection;
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
