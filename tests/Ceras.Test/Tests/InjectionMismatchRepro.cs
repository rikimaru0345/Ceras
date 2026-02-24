using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;

namespace Ceras.Test
{
    public interface IReproState { }

    [CerasSerializable]
    public class ReproStateImpl : IReproState { public int Value; }

    [CerasSerializable]
    [MemberConfig(TargetMember.PublicFields)]
    public class InjectionMismatchReproModel
    {
        // This specific type (Dictionary<int, interface>) 
        // triggered the "Injection mismatch" error.
        public Dictionary<int, IReproState> States;
    }

    public class InjectionMismatchRepro : TestBase
    {
        [Fact]
        public void Dictionary_InjectionMismatch_Reproduction()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            Ceras.Generated.GeneratedFormatters.UseFormatters(config);
            
            // This should NOT throw "Injection mismatch for Dictionary`2: expected CollectionFormatter`2 but got CollectionFormatter`2"
            var ceras = new CerasSerializer(config);

            var model = new InjectionMismatchReproModel
            {
                States = new Dictionary<int, IReproState>
                {
                    { 1, new ReproStateImpl { Value = 42 } }
                }
            };

            var blob = ceras.Serialize(model);
            var cloned = ceras.Deserialize<InjectionMismatchReproModel>(blob);

            Assert.NotNull(cloned.States);
            Assert.Single(cloned.States);
            Assert.Equal(42, ((ReproStateImpl)cloned.States[1]).Value);
        }
    }
}
