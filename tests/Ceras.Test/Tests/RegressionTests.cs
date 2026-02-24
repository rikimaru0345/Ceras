using System;
using System.Collections.Generic;
using System.Reflection;
using System.Buffers;
using Xunit;
using Ceras;
using Ceras.Helpers;
using Ceras.Formatters;
using Ceras.Format;

namespace Ceras.Test
{
    public class RegressionTests : TestBase
    {
        // -------------------------------------------------------------------
        // Ambiguous Match Repro (Generic vs Non-Generic)
        // -------------------------------------------------------------------

        internal sealed class AmbigFormatter : IFormatter<Weapon>
        {
            public void Serialize(IBufferWriter<byte> writer, Weapon value) {}
            
            // This generic method, when closed with IBufferWriter<byte>, matches the signature above exactly.
            public void Serialize<TWriter>(TWriter writer, Weapon value) where TWriter : IBufferWriter<byte> {}

            public void Deserialize(ref SequenceReader<byte> reader, ref Weapon value) {}
            public void SerializeUntyped(IBufferWriter<byte> writer, object value) {}
            public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member) {}
        }

        [Fact]
        public void Fix_AmbiguousMatch_PreferredNonGeneric()
        {
            var type = typeof(AmbigFormatter);
            var method = type.ResolveSerializeMethod(typeof(Weapon));
            Assert.NotNull(method);
            Assert.False(method.IsGenericMethod, "Should have preferred the non-generic overload");
        }

        // -------------------------------------------------------------------
        // Dictionary Injection Mismatch Repro
        // -------------------------------------------------------------------

        public interface IReproState { }

        [CerasSerializable]
        public class ReproStateImpl : IReproState { public int Value; }

        [CerasSerializable]
        [MemberConfig(TargetMember.PublicFields)]
        public class InjectionMismatchReproModel
        {
            public Dictionary<int, IReproState> States;
        }

        [Fact]
        public void Fix_Dictionary_InjectionMismatch()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            Ceras.Generated.GeneratedFormatters.UseFormatters(config);
            
            var ceras = new CerasSerializer(config);

            var model = new InjectionMismatchReproModel
            {
                States = new Dictionary<int, IReproState>
                {
                    { 1, new ReproStateImpl { Value = 42 } }
                }
            };

            var blob = CerasRobust.Save(model, 1, ceras);
            var cloned = CerasRobust.Load<InjectionMismatchReproModel>(blob, 1, ceras);

            Assert.NotNull(cloned.States);
            Assert.Single(cloned.States);
            Assert.Equal(42, ((ReproStateImpl)cloned.States[1]).Value);
        }

        // -------------------------------------------------------------------
        // Array Injection Mismatch Repro
        // -------------------------------------------------------------------

        [Fact]
        public void Fix_Array_InjectionMismatch()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            Ceras.Generated.GeneratedFormatters.UseFormatters(config);
            
            var ceras = new CerasSerializer(config);

            var model = new ArrayMemberModel
            {
                Weapons = new[] { new Weapon { Name = "Sword" } }
            };

            var blob = CerasRobust.Save(model, 1, ceras);
            var cloned = CerasRobust.Load<ArrayMemberModel>(blob, 1, ceras);

            Assert.NotNull(cloned.Weapons);
            Assert.Single(cloned.Weapons);
            Assert.Equal("Sword", cloned.Weapons[0].Name);
        }
    }

    [CerasSerializable]
    public class ArrayMemberModel
    {
        public Weapon[] Weapons;
    }
}
