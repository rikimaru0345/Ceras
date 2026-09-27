using System;
using Xunit;
using Ceras;
using Ceras.Generated;
using System.Linq;

namespace Ceras.Test.Accessors
{
    // ------------------------------------------------------------------
    // Non-generic: private fields + private parameterized constructor.
    // ------------------------------------------------------------------
    [CerasSerializable]
    public class PrivateCtorFieldModel
    {
        private int _number = 5;
        private readonly string _text;

        private PrivateCtorFieldModel(string text)
        {
            _text = text;
        }

        public static PrivateCtorFieldModel Create(string text) => new PrivateCtorFieldModel(text);

        public int Number => _number;
        public string Text => _text;
        public void SetNumber(int n) => _number = n;
    }

    // ------------------------------------------------------------------
    // Generic type with private fields and a parameterized constructor,
    // reached through closed instantiations (generic accessor helper).
    // ------------------------------------------------------------------
    [CerasSerializable]
    public class GenericBox<T>
    {
        private T _value;
        private int _count;

        public GenericBox(T value, int count)
        {
            _value = value;
            _count = count + 1; // proves the constructor ran
        }

        public T Value => _value;
        public int Count => _count;
    }

    // ------------------------------------------------------------------
    // Constrained generic (where T : struct).
    // ------------------------------------------------------------------
    [CerasSerializable]
    public class StructBox<T> where T : struct
    {
        private T _value;

        public StructBox(T value) => _value = value;

        public T Value => _value;
    }

    // ------------------------------------------------------------------
    // Private field inherited from a generic base class.
    // ------------------------------------------------------------------
    public class GenericHolderBase<T>
    {
        private T _held;
        public void SetHeld(T value) => _held = value;
        public T GetHeld() => _held;
    }

    [CerasSerializable]
    public class GenericHolderDerived : GenericHolderBase<string>
    {
        public int Extra;
    }

    // ------------------------------------------------------------------
    // [CerasConstructor] with a ref parameter (signature must preserve ref).
    // ------------------------------------------------------------------
    [CerasSerializable]
    public class RefCtorModel
    {
        public int Value;

        [CerasConstructor]
        public RefCtorModel(ref int value)
        {
            Value = value;
        }
    }

    // ------------------------------------------------------------------
    // Root that makes the closed generic instantiations reachable.
    // ------------------------------------------------------------------
    [CerasSerializable]
    public class AccessorRoot
    {
        public GenericBox<int> IntBox;
        public GenericBox<string> StrBox;
        public StructBox<double> DblBox;
    }

    // ------------------------------------------------------------------
    // Value-type receiver with private members.
    // ------------------------------------------------------------------
    [CerasSerializable]
    public struct PrivateStruct
    {
        private int _x;
        private string _y;

        public PrivateStruct(int x, string y)
        {
            _x = x;
            _y = y;
        }

        public int GetX() => _x;
        public string GetY() => _y;
    }

    // ------------------------------------------------------------------
    // Nested generic type (helper must declare the full parameter list).
    // ------------------------------------------------------------------
    public class OuterHost
    {
        public class Nested<T>
        {
            private T _item;

            public Nested(T item) => _item = item;

            public T Item => _item;
        }
    }

    [CerasSerializable]
    public class NestedRoot
    {
        public OuterHost.Nested<int> N;
        public OuterHost.Nested<string> S;
    }

    public class AccessorConsolidationTests
    {
        static CerasRobustSerializer Create() => new CerasRobustSerializer(c => c.UseGeneratedFormatters());

        [Fact]
        public void PrivateFields_And_PrivateCtor_RoundTrip()
        {
            var original = PrivateCtorFieldModel.Create("secret");
            original.SetNumber(41);

            var bytes = Create().Save(original, 1);
            var clone = Create().Load<PrivateCtorFieldModel>(bytes, 1);

            Assert.Equal(41, clone.Number);
            Assert.Equal("secret", clone.Text);
        }

        [Fact]
        public void GenericType_MultipleInstantiations_RoundTrip_And_CtorRuns()
        {
            var root = new AccessorRoot
            {
                IntBox = new GenericBox<int>(42, 7),
                StrBox = new GenericBox<string>("hello", 1),
                DblBox = new StructBox<double>(3.5)
            };

            var bytes = Create().Save(root, 1);
            var clone = Create().Load<AccessorRoot>(bytes, 1);

            Assert.Equal(42, clone.IntBox.Value);
            Assert.Equal(8, clone.IntBox.Count); // ctor added 1
            Assert.Equal("hello", clone.StrBox.Value);
            Assert.Equal(2, clone.StrBox.Count);
            Assert.Equal(3.5, clone.DblBox.Value);
        }

        [Fact]
        public void InheritedGenericBase_PrivateField_RoundTrip()
        {
            var original = new GenericHolderDerived { Extra = 99 };
            original.SetHeld("held-value");

            var bytes = Create().Save(original, 1);
            var clone = Create().Load<GenericHolderDerived>(bytes, 1);

            Assert.Equal("held-value", clone.GetHeld());
            Assert.Equal(99, clone.Extra);
        }

        [Fact]
        public void RefParameterConstructor_RoundTrip()
        {
            int seed = 123;
            var original = new RefCtorModel(ref seed);

            var bytes = Create().Save(original, 1);
            var clone = Create().Load<RefCtorModel>(bytes, 1);

            Assert.Equal(123, clone.Value);
        }

        [Fact]
        public void ValueType_PrivateMembers_RoundTrip()
        {
            var original = new PrivateStruct(7, "struct");

            var bytes = Create().Save(original, 1);
            var clone = Create().Load<PrivateStruct>(bytes, 1);

            Assert.Equal(7, clone.GetX());
            Assert.Equal("struct", clone.GetY());
        }

        [Fact]
        public void NestedGenericType_RoundTrip()
        {
            var root = new NestedRoot
            {
                N = new OuterHost.Nested<int>(5),
                S = new OuterHost.Nested<string>("nested")
            };

            var bytes = Create().Save(root, 1);
            var clone = Create().Load<NestedRoot>(bytes, 1);

            Assert.Equal(5, clone.N.Item);
            Assert.Equal("nested", clone.S.Item);
        }

        [Fact]
        public void GenericInstantiations_HaveDistinctCanonicalStableNames()
        {
            var robust = Create();

            var names = robust.Config.SchemaRegistry.All()
                .ToDictionary(e => e.type, e => e.info.StableName);

            string intName = names[typeof(GenericBox<int>)];
            string strName = names[typeof(GenericBox<string>)];
            string nestedInt = names[typeof(OuterHost.Nested<int>)];
            string nestedStr = names[typeof(OuterHost.Nested<string>)];

            Assert.NotEqual(intName, strName);
            Assert.NotEqual(nestedInt, nestedStr);
            Assert.Contains("GenericBox`1<", intName);
            Assert.Contains("System.Int32", intName);
            Assert.Contains("System.String", strName);
            Assert.Contains("Nested`1<", nestedInt);
            Assert.Contains("Nested`1<", nestedStr);
        }

        [Fact]
        public void GenericInstantiations_HaveDistinctIdentity_ThroughInterpreter()
        {
            var root = new AccessorRoot
            {
                IntBox = new GenericBox<int>(42, 7),
                StrBox = new GenericBox<string>("hello", 1),
                DblBox = new StructBox<double>(3.5)
            };

            var bytes = Create().Save(root, 1);

            var interp = Create();
            interp.Config.SchemaRegistry.InternalClear();
            interp.Config.UseGeneratedFormatters();
            var clone = interp.Load<AccessorRoot>(bytes, 1);

            Assert.Equal(42, clone.IntBox.Value);
            Assert.Equal("hello", clone.StrBox.Value);
            Assert.Equal(3.5, clone.DblBox.Value);
        }
    }
}
