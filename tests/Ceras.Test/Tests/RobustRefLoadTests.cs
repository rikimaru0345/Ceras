using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Helpers;
using Ceras.Format;

namespace Ceras.Test
{
    public class RobustRefLoadTests : TestBase
    {
        [Fact]
        public void LoadRef_FastPath()
        {
            var robust = new CerasRobustSerializer();

            var original = new RefLoadData { Name = "Original", Value = 123 };
            var data = robust.Save(original, 1);

            var existing = new RefLoadData { Name = "Existing", Value = 0 };
            robust.Load(ref existing, data, 1);

            Assert.Equal("Original", existing.Name);
            Assert.Equal(123, existing.Value);
        }

        [Fact]
        public void LoadRef_SlowPath()
        {
            var robust = new CerasRobustSerializer();

            var original = new RefLoadData { Name = "Original", Value = 123 };
            var data = robust.Save(original, 1);

            // Force slow path by changing fingerprint
            data[13] ^= 0xFF;
            FixRobustChecksum(data);

            var existing = new RefLoadData { Name = "Existing", Value = 0 };
            robust.Load(ref existing, data, 1);

            Assert.Equal("Original", existing.Name);
            Assert.Equal(123, existing.Value);
        }

        [Fact]
        public void LoadRef_List_SlowPath()
        {
            var robust = new CerasRobustSerializer();

            var original = new List<int> { 1, 2, 3 };
            var data = robust.Save(original, 1);

            // Force slow path by changing fingerprint
            data[13] ^= 0xFF;
            FixRobustChecksum(data);

            var existing = new List<int> { 9, 8, 7, 6 };
            robust.Load(ref existing, data, 1);

            Assert.Equal(3, existing.Count);
            Assert.Equal(1, existing[0]);
            Assert.Equal(2, existing[1]);
            Assert.Equal(3, existing[2]);
        }

        [CerasSerializable]
        public class RefLoadData
        {
            public string Name;
            public int Value;
        }
    }
}
