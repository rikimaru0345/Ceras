using System;
using Xunit;
using Ceras;
using Ceras.Format;

namespace Ceras.Test
{
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

    [CerasSerializable]
    public class Dummy2 { public int X; }

    public class RobustSlowPathTests
    {
        [Fact]
        public void SlowPath_Fires_Callbacks_Even_Without_Changes()
        {
            var robust1 = new CerasRobustSerializer(config => config.AllowReflectionFallback = true);

            var original = new CallbackType { Value = 42 };
            var data = robust1.Save(original, 1);

            // Trigger mismatch by loading with a different config (extra type → different fingerprint)
            var robust2 = new CerasRobustSerializer(config =>
            {
                config.AllowReflectionFallback = true;
                config.ConfigType<Dummy2>().ConstructByUninitialized();
            });

            var result = robust2.Load<CallbackType>(data, 1);
            
            Assert.Equal(42, result.Value);
            Assert.True(result.CallbackCalled, "Callback should be called in slow path even if no changes to the type occurred");
        }
    }
}
