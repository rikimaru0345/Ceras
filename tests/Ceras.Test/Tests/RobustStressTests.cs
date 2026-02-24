using System;
using System.Collections.Generic;
using System.Buffers;
using Xunit;
using Ceras;
using Ceras.Format;
using System.Linq;

namespace Ceras.Test
{
    public class RobustStressTests : TestBase
    {
        // -------------------------------------------------------------------
        // Scenario #5: Size Limit Stress (Security)
        // Confirm that the Robust path respects SizeLimits to prevent 
        // OOM attacks from corrupted/malicious saves.
        // -------------------------------------------------------------------

        [CerasSerializable]
        public class LargeCollectionModel
        {
            public List<int> Items;
        }

        [Fact]
        public void Robust_SizeLimits_PreventsOOM()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            config.Advanced.SizeLimits.MaxCollectionSize = 100; // Small limit
            var ceras = new CerasSerializer(config);

            // Verify that a legitimate large save is blocked if it exceeds limits.
            var oversized = new LargeCollectionModel { Items = Enumerable.Range(0, 200).ToList() };
            var oversizedBlob = CerasRobust.Save(oversized, 1, ceras);

            // CerasRobust wraps InvalidOperationException in a CerasException
            var ex = Assert.Throws<Ceras.Exceptions.CerasException>(() => 
                CerasRobust.Load<LargeCollectionModel>(oversizedBlob, 1, ceras));
            
            Assert.Contains("Max size exceeded", ex.ToString());
        }
    }
}
