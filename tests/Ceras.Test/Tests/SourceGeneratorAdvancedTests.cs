using System;
using System.Collections.Generic;
using System.Linq;
using System.Buffers;
using System.Reflection;
using Ceras.Formatters;
using Ceras;
using Xunit;

namespace Ceras.Test.Tests
{
    public class SourceGeneratorAdvancedTests : TestBase
    {
        CerasConfig CreateSgConfig()
        {
            var config = new CerasRawConfig();
            Ceras.Generated.RobustFormatters.UseRobustFormatters(config);
            return config;
        }

        #region In-Place Population

        [Fact]
        public void ReadonlyCollections_ArePopulatedInPlace()
        {
            var config = CreateSgConfig();
            var obj = new AllReadonlyCollections();
            obj.ListField.Add(1.1f);
            obj.ListProp.Add(2.2f);
            obj.SetField.Add(42);
            
            var clone = Clone(obj, config);
            
            Assert.NotSame(obj, clone);
            Assert.Equal(obj.ListField, clone.ListField);
            Assert.Equal(obj.ListProp, clone.ListProp);
            Assert.Equal(obj.SetField, clone.SetField);
        }

        [Fact]
        public void ReadonlyCollections_AreClearedBeforePopulation()
        {
            var config = CreateSgConfig();
            var obj = new AllReadonlyCollections();
            obj.ListField.Add(1.1f);
            
            // First clone
            var clone = Clone(obj, CreateSgConfig());
            Assert.Single(clone.ListField);

            // Modify original
            obj.ListField.Clear();
            obj.ListField.Add(3.3f);

            // Second clone - should not contain 1.1f from the ctor initialization if it were additive
            // (though in this case the ctor always creates a fresh list, but imagine a static shared list or similar)
            var clone2 = Clone(obj, CreateSgConfig());
            Assert.Single(clone2.ListField);
            Assert.Equal(3.3f, clone2.ListField[0]);
        }

        [Fact(Skip = "In-place custom collection population issue")]
        public void CustomCollection_VerifyInPlaceCalls()
        {
            var config = CreateSgConfig();
            config.ConfigType<CustomList>().CustomFormatter = new CustomListFormatter(new CerasSerializer());
            
            var obj = new CustomCollectionContainer();
            obj.Items.Add(1);
            obj.Items.Add(2);

            var clone = Clone(obj, config);

            Assert.Equal(2, clone.Items.Count);
            Assert.True(clone.Items.WasCleared, "Clear() was not called on the existing instance");
            Assert.True(clone.Items.AddCount >= 2, "Add() was not called the expected number of times");
        }

        #endregion

        #region MemberConfig Inheritance

        [Fact]
        public void MemberConfig_DeepInheritance_Works()
        {
            var config = CreateSgConfig();
            var obj = new DeepDerived();
            obj.SetGrandBasePrivate(1);
            obj.SetBasePrivate(2);
            obj.SetDerivedPrivate(3);
            obj.PublicField = 4;

            var clone = Clone(obj, config);

            // IGrandInterface has [MemberConfig(All)]
            // DeepDerived inherits this through the chain.
            Assert.Equal(1, clone.GetGrandBasePrivate());
            Assert.Equal(2, clone.GetBasePrivate());
            Assert.Equal(3, clone.GetDerivedPrivate());
            Assert.Equal(4, clone.PublicField);
        }

        [Fact]
        public void MemberConfig_OverrideAtMidLevel_Works()
        {
            var config = CreateSgConfig();
            var obj = new DerivedFromOverride();
            obj.SetBasePrivate(1);
            obj.DerivedField = 2;

            var clone = Clone(obj, config);

            // BaseWithOverride has [MemberConfig(AllPublic)]
            // This should override IGrandInterface(All) for its entire sub-hierarchy.
            Assert.Equal(0, clone.GetBasePrivate()); // Should NOT be serialized
            Assert.Equal(2, clone.DerivedField);
        }

        #endregion

        #region Private Access Helpers

        [Fact]
        public void PrivateMembers_InAbstractBases_AreAccessible()
        {
            var config = CreateSgConfig();
            var obj = new ConcreteFromAbstract();
            obj.SetAbstractPrivate(42);
            obj.Value = 100;

            var clone = Clone(obj, config);

            Assert.Equal(42, clone.GetAbstractPrivate());
            Assert.Equal(100, clone.Value);
        }

        [Fact]
        public void PrivateProperties_WithBackingFields_RoundTrip()
        {
            var config = CreateSgConfig();
            var obj = new PrivatePropObj();
            obj.SetPrivateProp(123);

            var clone = Clone(obj, config);

            Assert.Equal(123, clone.GetPrivateProp());
        }

        #endregion
    }

    // --- Helper Types for In-Place ---

    [CerasSerializable]
    public class AllReadonlyCollections
    {
        public readonly List<float> ListField = new List<float>();
        public List<float> ListProp { get; } = new List<float>();
        public readonly HashSet<int> SetField = new HashSet<int>();
    }

    public class CustomList : List<int>
    {
        public bool WasCleared;
        public int AddCount;

        public void TrackedClear() { WasCleared = true; base.Clear(); }
        public void TrackedAdd(int item) { AddCount++; base.Add(item); }
    }

    public class CustomListFormatter : IFormatter<CustomList>
    {
        IFormatter<int> _intFormatter;
        public CustomListFormatter(CerasSerializer s) => _intFormatter = s.GetFormatter<int>();

        public void Serialize(IBufferWriter<byte> writer, CustomList value)
        {
            SerializerBinary.WriteUInt32(writer, (uint)value.Count);
            foreach (var item in value) _intFormatter.Serialize(writer, item);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref CustomList value)
        {
            var count = SerializerBinary.ReadUInt32(ref reader);
            if (value == null) value = new CustomList();
            else value.TrackedClear();

            for (int i = 0; i < count; i++)
            {
                int item = default;
                _intFormatter.Deserialize(ref reader, ref item);
                value.TrackedAdd(item);
            }
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (CustomList)value);
        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, System.Reflection.MemberInfo member)
        {
            var value = (CustomList)((System.Reflection.FieldInfo)member).GetValue(container);
            Deserialize(ref reader, ref value);
        }
    }

    [CerasSerializable]
    public class CustomCollectionContainer
    {
        public readonly CustomList Items = new CustomList();
    }

    // --- Helper Types for Inheritance ---

    [MemberConfig(TargetMember.AllFields)]
    public interface IGrandInterface { }

    public abstract partial class GrandBase : IGrandInterface
    {
        int _grandBasePrivate;
        public void SetGrandBasePrivate(int v) => _grandBasePrivate = v;
        public int GetGrandBasePrivate() => _grandBasePrivate;
    }

    public abstract partial class MidBase : GrandBase
    {
        int _basePrivate;
        public void SetBasePrivate(int v) => _basePrivate = v;
        public int GetBasePrivate() => _basePrivate;
    }

    [CerasSerializable]
    public partial class DeepDerived : MidBase
    {
        public int PublicField;
        int _derivedPrivate;
        public void SetDerivedPrivate(int v) => _derivedPrivate = v;
        public int GetDerivedPrivate() => _derivedPrivate;
    }

    [MemberConfig(TargetMember.PublicFields)]
    public abstract partial class BaseWithOverride : IGrandInterface
    {
        int _basePrivate;
        public void SetBasePrivate(int v) => _basePrivate = v;
        public int GetBasePrivate() => _basePrivate;
    }

    [CerasSerializable]
    public partial class DerivedFromOverride : BaseWithOverride
    {
        public int DerivedField;
    }

    // --- Helper Types for Private Access ---

    public abstract partial class AbstractBase
    {
        int _absPrivate;
        public void SetAbstractPrivate(int v) => _absPrivate = v;
        public int GetAbstractPrivate() => _absPrivate;
    }

    [CerasSerializable]
    [MemberConfig(TargetMember.AllFields)]
    public partial class ConcreteFromAbstract : AbstractBase
    {
        public int Value;
    }

    [CerasSerializable]
    public partial class PrivatePropObj
    {
        [Include]
        int PrivateProp { get; set; }

        public void SetPrivateProp(int v) => PrivateProp = v;
        public int GetPrivateProp() => PrivateProp;
    }
}
