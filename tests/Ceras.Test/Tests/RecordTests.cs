using System;
using Xunit;
using Ceras;

namespace Ceras.Test.Tests {
    [CerasContext("Tests")]
    [CerasSerializable]
    public partial record SimpleRecord(int Id, string Name);

    [CerasContext("Tests")]
    [CerasSerializable]
    public partial record struct StructRecord(double X, double Y);

    public class RecordTests : TestBase {
        [Fact]
        public void TestRecordSerialization() {
            var ceras = new CerasFastSerializer();
            
            var r1 = new SimpleRecord(42, "Test");
            var r2 = ceras.Deserialize<SimpleRecord>(ceras.Serialize(r1));
            Assert.Equal(r1, r2);

            var s1 = new StructRecord(1.5, 2.5);
            var s2 = ceras.Deserialize<StructRecord>(ceras.Serialize(s1));
            Assert.Equal(s1, s2);
        }
    }
}
