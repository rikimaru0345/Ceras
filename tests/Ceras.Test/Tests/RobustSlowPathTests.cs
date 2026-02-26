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
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            config.Advanced.AllowReflectionFallback = true;
            var ceras = new CerasSerializer(config);

            var original = new CallbackType { Value = 42 };
            var data = CerasRobust.Save(original, 1, ceras);

            // Trigger mismatch
            var config2 = new SerializerConfig();
            CerasRobust.Configure(config2);
            config2.Advanced.AllowReflectionFallback = true;
            config2.ConfigType<Dummy2>().ConstructByUninitialized();
            var ceras2 = new CerasSerializer(config2);

            var result = CerasRobust.Load<CallbackType>(data, 1, ceras2);
            
            Assert.Equal(42, result.Value);
            Assert.True(result.CallbackCalled, "Callback should be called in slow path even if no changes to the type occurred");
        }
    }
}
