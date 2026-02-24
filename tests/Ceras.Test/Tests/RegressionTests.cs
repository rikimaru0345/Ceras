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
            Ceras.Generated.GeneratedFormatters.UseFormatters(config);
            CerasRobust.Configure(config);
            
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
            Ceras.Generated.GeneratedFormatters.UseFormatters(config);
            CerasRobust.Configure(config);
            
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

        // -------------------------------------------------------------------
        // Struct with Auto-Properties (Enforcement)
        // -------------------------------------------------------------------

        [Fact]
        public void Fix_StructWithAutoProps_ThrowsEarlier_IfMissingAttribute()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config); // This sets AllowReflectionFallback = false
            var ceras = new CerasSerializer(config);

            var model = new LegacyModel { Item = new LegacyCostItem("Test", 10) };

            // This should throw InvalidOperationException because it's missing [CerasSerializable]
            // and reflection is disabled.
            var ex = Assert.Throws<InvalidOperationException>(() => ceras.Serialize(model));
            Assert.Contains("reflection-based fallback is disabled", ex.ToString());
        }

        [Fact]
        public void Fix_StructWithAutoProps_StillThrowsWarning_IfReflectionExplicitlyEnabled()
        {
            var config = new SerializerConfig();
            config.Advanced.AllowReflectionFallback = true;
            var ceras = new CerasSerializer(config);

            var model = new LegacyModel { Item = new LegacyCostItem("Test", 10) };

            // If user explicitly enables reflection, they still get the helpful warning about structs.
            var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => ceras.Serialize(model));
            Assert.Contains("backing-fields", ex.ToString());
        }
    }

    [CerasSerializable]
    public class ArrayMemberModel
    {
        public Weapon[] Weapons;
    }

    // A struct with only auto-properties and no [CerasSerializable]
    public struct LegacyCostItem
    {
        public string Name { get; set; }
        public int Amount { get; set; }

        public LegacyCostItem(string name, int amount)
        {
            Name = name;
            Amount = amount;
        }
    }

    [CerasSerializable]
    public class LegacyModel
    {
        public LegacyCostItem Item;
    }
}
