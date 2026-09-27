using System;
using Xunit;
using Ceras;
using Ceras.Generated;

// Priority 0.4.1 — a reused object must not run its constructor (or field initializers) again.
// The generated reader is handed an instance by the reference wrapper even for a fresh load
// (robust construction is uninitialized), so it is told whether that instance is new via
// CerasSerializer.ConsumeCurrentObjectFresh(). A fresh load must construct exactly once; a
// Load(ref existing) must construct zero times.
namespace Ceras.Test.ConstructorReuse
{
    public static class CtorCounters
    {
        public static int ModelCtors;
        public static int ModelFieldInits;
        public static int PrivateCtors;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CtorCountModel
    {
        public int Value;
        public int InitializedByField = BumpField();

        static int BumpField()
        {
            CtorCounters.ModelFieldInits++;
            return 5;
        }

        [CerasConstructor]
        public CtorCountModel(int value)
        {
            CtorCounters.ModelCtors++;
            Value = value;
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CtorCountRoot
    {
        public CtorCountModel Child;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class PrivateParamCtorModel
    {
        public int Value;

        [CerasConstructor]
        private PrivateParamCtorModel(int value)
        {
            CtorCounters.PrivateCtors++;
            Value = value;
        }

        public static PrivateParamCtorModel Create(int value) => new PrivateParamCtorModel(value);
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CtorCountBox<T>
    {
        public static int Ctors;

        public T Value;

        [CerasConstructor]
        public CtorCountBox(T value)
        {
            Ctors++;
            Value = value;
        }
    }

    // Declares the closed instantiations so the generator emits their formatters.
    [CerasContext("Tests")]
    [CerasSerializable]
    public class CtorCountGenericRoot
    {
        public CtorCountBox<int> IntBox;
        public CtorCountBox<CtorCountModel> RefBox;
    }

    public class ConstructorReuseTests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        static void ResetCounters()
        {
            CtorCounters.ModelCtors = 0;
            CtorCounters.ModelFieldInits = 0;
            CtorCounters.PrivateCtors = 0;
        }

        [Fact]
        public void FreshLoad_ConstructsExactlyOnce()
        {
            var bytes = Generated().Save(new CtorCountModel(11), 1);
            ResetCounters();

            var loaded = Generated().Load<CtorCountModel>(bytes, 1);

            Assert.Equal(11, loaded.Value);
            Assert.Equal(1, CtorCounters.ModelCtors);
            Assert.Equal(1, CtorCounters.ModelFieldInits);
        }

        [Fact]
        public void ReusedLoad_DoesNotConstructAgain()
        {
            var bytes = Generated().Save(new CtorCountModel(11), 1);

            var existing = new CtorCountModel(99);
            ResetCounters();

            Generated().Load(ref existing, bytes, 1);

            Assert.Equal(11, existing.Value);
            Assert.Equal(0, CtorCounters.ModelCtors);
            Assert.Equal(0, CtorCounters.ModelFieldInits);
        }

        [Fact]
        public void ReusedMember_DoesNotConstructAgain()
        {
            var bytes = Generated().Save(new CtorCountRoot { Child = new CtorCountModel(11) }, 1);

            var existingChild = new CtorCountModel(99);
            var existingRoot = new CtorCountRoot { Child = existingChild };
            ResetCounters();

            Generated().Load(ref existingRoot, bytes, 1);

            Assert.Same(existingChild, existingRoot.Child);
            Assert.Equal(11, existingRoot.Child.Value);
            Assert.Equal(0, CtorCounters.ModelCtors);
            Assert.Equal(0, CtorCounters.ModelFieldInits);
        }

        [Fact]
        public void PrivateParameterizedConstructor_FreshLoadsOnce_ReusesWithoutCalling()
        {
            var bytes = Generated().Save(PrivateParamCtorModel.Create(7), 1);

            ResetCounters();
            var loaded = Generated().Load<PrivateParamCtorModel>(bytes, 1);
            Assert.Equal(7, loaded.Value);
            Assert.Equal(1, CtorCounters.PrivateCtors);

            var existing = PrivateParamCtorModel.Create(0);
            ResetCounters();
            Generated().Load(ref existing, bytes, 1);
            Assert.Equal(7, existing.Value);
            Assert.Equal(0, CtorCounters.PrivateCtors);
        }

        [Fact]
        public void GenericValueTypeArgument_FreshLoadsOnce_ReusesWithoutCalling()
        {
            var bytes = Generated().Save(new CtorCountBox<int>(3), 1);

            CtorCountBox<int>.Ctors = 0;
            var loaded = Generated().Load<CtorCountBox<int>>(bytes, 1);
            Assert.Equal(3, loaded.Value);
            Assert.Equal(1, CtorCountBox<int>.Ctors);

            var existing = new CtorCountBox<int>(9);
            CtorCountBox<int>.Ctors = 0;
            Generated().Load(ref existing, bytes, 1);
            Assert.Equal(3, existing.Value);
            Assert.Equal(0, CtorCountBox<int>.Ctors);
        }

        [Fact]
        public void GenericReferenceTypeArgument_FreshLoadsOnce_ReusesWithoutCalling()
        {
            var source = new CtorCountBox<CtorCountModel>(new CtorCountModel(21));
            var bytes = Generated().Save(source, 1);

            CtorCountBox<CtorCountModel>.Ctors = 0;
            ResetCounters();
            var loaded = Generated().Load<CtorCountBox<CtorCountModel>>(bytes, 1);
            Assert.Equal(21, loaded.Value.Value);
            Assert.Equal(1, CtorCountBox<CtorCountModel>.Ctors);
            Assert.Equal(1, CtorCounters.ModelCtors);

            var existing = new CtorCountBox<CtorCountModel>(new CtorCountModel(5));
            CtorCountBox<CtorCountModel>.Ctors = 0;
            ResetCounters();
            Generated().Load(ref existing, bytes, 1);
            Assert.Equal(21, existing.Value.Value);
            Assert.Equal(0, CtorCountBox<CtorCountModel>.Ctors);
            Assert.Equal(0, CtorCounters.ModelCtors);
        }
    }
}
