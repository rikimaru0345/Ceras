using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Generated;

namespace Ceras.Test.RobustV3
{
    [CerasContext("Tests")]
    [CerasSerializable]
    public class V3Model
    {
        public int Id;
        public string Name;
        public List<int> Values = new();
    }

    /// <summary>
    /// Phase 3.3: two-region format and frozen schema.
    /// </summary>
    public class RobustFormatV3Tests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        [Fact]
        public void FastPath_SkipsSchemaParsing_OnFingerprintMatch()
        {
            var model = new V3Model { Id = 7, Name = "keep", Values = { 1, 2, 3 } };
            var bytes = Generated().Save(model, 1);

            long schemaStrOff = BitConverter.ToInt64(bytes, RobustHeader.SchemaStringOffsetOffset);
            Assert.True(schemaStrOff > RobustHeader.Size);

            // Destroy the schema bytes but keep the fingerprint matching. The happy path must not
            // parse the schema at all, so the load still succeeds.
            for (int i = RobustHeader.Size; i < schemaStrOff; i++)
                bytes[i] = 0;
            FixRobustChecksum(bytes);

            var clone = Generated().Load<V3Model>(bytes, 1);
            Assert.Equal(7, clone.Id);
            Assert.Equal("keep", clone.Name);
            Assert.Equal(new[] { 1, 2, 3 }, clone.Values);
        }

        [Fact]
        public void Save_DoesNotGrowRegistry_AfterSeal()
        {
            var robust = Generated();
            var model = new V3Model { Id = 1, Name = "x", Values = { 1 } };

            robust.Save(model, 1);
            Assert.True(robust.Config.SchemaRegistry.HasBakedSchema);

            int countAfterFirst = robust.Config.SchemaRegistry.All().Count();
            robust.Save(model, 1);
            int countAfterSecond = robust.Config.SchemaRegistry.All().Count();

            Assert.Equal(countAfterFirst, countAfterSecond);
        }

        [Fact]
        public void NewerBuildTimestamp_IsRejected_BeforePayloadDecode()
        {
            var writer = new CerasRobustSerializer(c => { c.UseGeneratedFormatters("Tests"); c.BuildTimestamp = 200; });
            var bytes = writer.Save(new V3Model { Id = 3, Name = "ts", Values = { 1 } }, 1);

            var older = new CerasRobustSerializer(c => { c.UseGeneratedFormatters("Tests"); c.BuildTimestamp = 100; });
            Assert.Throws<Ceras.Exceptions.CerasException>(() => older.Load<V3Model>(bytes, 1));

            var sameBuild = new CerasRobustSerializer(c => { c.UseGeneratedFormatters("Tests"); c.BuildTimestamp = 200; });
            Assert.NotNull(sameBuild.Load<V3Model>(bytes, 1));

            var newerBuild = new CerasRobustSerializer(c => { c.UseGeneratedFormatters("Tests"); c.BuildTimestamp = 300; });
            Assert.NotNull(newerBuild.Load<V3Model>(bytes, 1));

            // Explicitly unknown current build timestamp -> no gate.
            var unknown = new CerasRobustSerializer(c => { c.UseGeneratedFormatters("Tests"); c.SetUnknownBuildTimestamp(); });
            Assert.False(unknown.Config.BuildTimestampKnown);
            Assert.NotNull(unknown.Load<V3Model>(bytes, 1));
        }

        [Fact]
        public void Header_CarriesSchemaStringOffset_AndSurvivesRoundTrip()
        {
            var model = new V3Model { Id = 2, Name = "y", Values = { 9 } };
            var bytes = Generated().Save(model, 1);

            long schemaStrOff = BitConverter.ToInt64(bytes, RobustHeader.SchemaStringOffsetOffset);
            long dataOff = BitConverter.ToInt64(bytes, RobustHeader.DataSectionOffsetOffset);
            long strOff = BitConverter.ToInt64(bytes, RobustHeader.StringTableOffsetOffset);

            Assert.InRange(schemaStrOff, (long)RobustHeader.Size, dataOff);
            Assert.True(dataOff <= strOff);
            Assert.Equal(3, bytes[4]); // FormatVersion

            var clone = Generated().Load<V3Model>(bytes, 1);
            Assert.Equal(9, clone.Values[0]);
        }
    }
}
