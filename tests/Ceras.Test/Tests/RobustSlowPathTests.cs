using System;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test
{
    [CerasContext("Tests")]
    [CerasSerializable]
    public class CallbackType
    {
        public int Value;
        public bool CallbackCalled;

        [OnAfterDeserialize]
        void OnAfterDeserialize()
        {
            CallbackCalled = true;
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class Dummy2 { public int X; }

    public class RobustSlowPathTests : TestBase
    {
        [Fact]
        public void SlowPath_Fires_Callbacks_Even_Without_Changes()
        {
            var robust = new CerasRobustSerializer(config =>
            {
                config.AllowReflectionFallback = true;
                config.UseGeneratedFormatters("Tests");
            });

            var original = new CallbackType { Value = 42 };
            var data = robust.Save(original, 1);

            // Force the interpreter/slow path via a fingerprint mismatch.
            data[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(data);

            var result = robust.Load<CallbackType>(data, 1);

            Assert.Equal(42, result.Value);
            Assert.True(result.CallbackCalled, "Callback should be called in slow path even if no changes to the type occurred");
        }
    }
}
