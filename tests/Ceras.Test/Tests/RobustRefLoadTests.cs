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
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            var ceras = new CerasSerializer(config);

            var original = new RefLoadData { Name = "Original", Value = 123 };
            var data = CerasRobust.Save(original, 1, ceras);

            var existing = new RefLoadData { Name = "Existing", Value = 0 };
            CerasRobust.Load(ref existing, data, 1, ceras);

            Assert.Equal("Original", existing.Name);
            Assert.Equal(123, existing.Value);
        }

        [Fact]
        public void LoadRef_SlowPath()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            var ceras = new CerasSerializer(config);

            var original = new RefLoadData { Name = "Original", Value = 123 };
            var data = CerasRobust.Save(original, 1, ceras);

            // Force slow path by changing fingerprint
            data[CerasRobust.OffsetFingerprint] ^= 0xFF;

            var existing = new RefLoadData { Name = "Existing", Value = 0 };
            CerasRobust.Load(ref existing, data, 1, ceras);

            Assert.Equal("Original", existing.Name);
            Assert.Equal(123, existing.Value);
        }

        [Fact]
        public void LoadRef_List_SlowPath()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            var ceras = new CerasSerializer(config);

            var original = new List<int> { 1, 2, 3 };
            var data = CerasRobust.Save(original, 1, ceras);

            // Force slow path by changing fingerprint
            data[CerasRobust.OffsetFingerprint] ^= 0xFF;

            var existing = new List<int> { 9, 8, 7, 6 };
            CerasRobust.Load(ref existing, data, 1, ceras);

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
