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
        public static int ContractCtors;
        public static int ContractFieldInits;
        public static int HeuristicCtors;
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

    // ── The construction contract on the migration path ───────────────────────────────────
    //
    // The generated reader invokes a parameterized [CerasConstructor] with values taken from the
    // payload, then assigns every member. The migration interpreter used to allocate uninitialized
    // and only read fields, so the same save produced a different object depending on which reader
    // ran: the constructor never ran (no side effects, no field initializers, no validation) and an
    // [Exclude]d initialized field came back as default. These pin that the two readers now agree.

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CtorContractModel
    {
        public int Value;
        public int Rewritten;                    // the constructor doubles it; the payload must still win
        [Exclude]
        public int NotStored = InitNotStored();

        static int InitNotStored()
        {
            CtorCounters.ContractFieldInits++;
            return 99;
        }

        [CerasConstructor]
        public CtorContractModel(int value)
        {
            CtorCounters.ContractCtors++;
            Value = value;
            Rewritten = value * 2;
        }
    }

    /// <summary>No [CerasConstructor]: the generator's heuristic picks the constructor with the most
    /// parameters the serialized members can satisfy, so the interpreter has to pick the same one.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public class CtorHeuristicModel
    {
        public int A;
        public string B;

        public CtorHeuristicModel(int a, string b)
        {
            CtorCounters.HeuristicCtors++;
            A = a;
            B = b;
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CtorContractRoot
    {
        public CtorContractModel Model;
        public CtorHeuristicModel Heuristic;
        public CtorCountBox<int> Box;
        public int Tail;
    }

    /// <summary>A constructor that validates: a value it rejects must fail the load on both readers.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public class CtorStrictModel
    {
        public int Value;

        [CerasConstructor]
        public CtorStrictModel(int value)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "value must not be negative");
            Value = value;
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class CtorStrictRoot
    {
        public CtorStrictModel Model;
        public int Tail;
    }

    public class ConstructorReuseTests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        static void ResetCounters()
        {
            CtorCounters.ModelCtors = 0;
            CtorCounters.ModelFieldInits = 0;
            CtorCounters.PrivateCtors = 0;
            CtorCounters.ContractCtors = 0;
            CtorCounters.ContractFieldInits = 0;
            CtorCounters.HeuristicCtors = 0;
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

        // ── The construction contract has to hold on the migration path too ────────────────

        /// <summary>
        /// The same save, loaded through the generated reader and through the migration interpreter,
        /// must produce the same object *and* run the same construction: constructor exactly once,
        /// field initializers, and the payload winning over anything the constructor did.
        /// </summary>
        [Fact]
        public void ParameterizedCtor_Behaves_The_Same_On_Both_Paths()
        {
            var bytes = Generated().Save(new CtorContractRoot
            {
                Model = new CtorContractModel(5),
                Heuristic = new CtorHeuristicModel(7, "h"),
                Box = new CtorCountBox<int>(8),
                Tail = 4,
            }, 1);

            ResetCounters();
            CtorCountBox<int>.Ctors = 0;
            AssertSameConstruction(Generated().Load<CtorContractRoot>(bytes, 1));

            ResetCounters();
            CtorCountBox<int>.Ctors = 0;
            AssertSameConstruction(Generated().Load<CtorContractRoot>(SlowPath(bytes), 1));
        }

        static void AssertSameConstruction(CtorContractRoot loaded)
        {
            const int ctors = 1, fieldInits = 1, heuristicCtors = 1, boxCtors = 1;
            int contractCtors = CtorCounters.ContractCtors;
            int contractFieldInits = CtorCounters.ContractFieldInits;
            int heuristicCounter = CtorCounters.HeuristicCtors;
            int boxCounter = CtorCountBox<int>.Ctors;
            ResetCounters();
            CtorCountBox<int>.Ctors = 0;

            Assert.Equal(5, loaded.Model.Value);
            Assert.Equal(10, loaded.Model.Rewritten);       // the stored value, not the constructor's rewrite
            Assert.Equal(99, loaded.Model.NotStored);      // the [Exclude]d field initializer ran
            Assert.Equal(7, loaded.Heuristic.A);
            Assert.Equal("h", loaded.Heuristic.B);
            Assert.Equal(8, loaded.Box.Value);
            Assert.Equal(4, loaded.Tail);                  // the stream stayed aligned

            Assert.Equal(ctors, contractCtors);
            Assert.Equal(fieldInits, contractFieldInits);
            Assert.Equal(heuristicCtors, heuristicCounter);
            Assert.Equal(boxCtors, boxCounter);
        }

        /// <summary>A reused instance is never re-constructed: no constructor, no field initializers.</summary>
        [Fact]
        public void ReusedInstance_Runs_No_Ctor_On_The_Migration_Path()
        {
            var bytes = Generated().Save(new CtorContractRoot { Model = new CtorContractModel(5), Tail = 4 }, 1);

            var existingModel = new CtorContractModel(1);
            var existing = new CtorContractRoot { Model = existingModel, Tail = 0 };
            ResetCounters();
            CtorCountBox<int>.Ctors = 0;

            Generated().Load(ref existing, SlowPath(bytes), 1);

            Assert.Same(existingModel, existing.Model);
            Assert.Equal(5, existing.Model.Value);
            Assert.Equal(4, existing.Tail);
            Assert.Equal(0, CtorCounters.ContractCtors);
            Assert.Equal(0, CtorCounters.ContractFieldInits);
        }

        /// <summary>
        /// A constructor that rejects a value must reject the load on both readers: the interpreter
        /// used to skip the constructor entirely and accept a value the generated path refuses.
        /// </summary>
        [Fact]
        public void Ctor_That_Throws_Rejects_The_Load_On_Both_Paths()
        {
            var serializer = Generated();
            var bytes = serializer.Save(new CtorStrictRoot { Model = new CtorStrictModel(3), Tail = 1 }, 1);

            // Corrupt the stored constructor argument (the int 3) and fix the CRC, so only the reader
            // can notice. Located by value so the test does not depend on the field order.
            var corrupt = (byte[])bytes.Clone();
            RobustHeader.Read(corrupt, out _, out _, out _, out long dataStart, out long stringTable, out _);
            var stored = BitConverter.GetBytes(3);
            int at = -1;
            for (int i = (int)dataStart; i + 4 <= (int)stringTable; i++)
            {
                if (corrupt[i] == stored[0] && corrupt[i + 1] == stored[1] && corrupt[i + 2] == stored[2] && corrupt[i + 3] == stored[3])
                {
                    at = i;
                    break;
                }
            }
            Assert.True(at >= 0, "the stored constructor argument should be findable in the payload");
            corrupt[at] = 0xFF; corrupt[at + 1] = 0xFF; corrupt[at + 2] = 0xFF; corrupt[at + 3] = 0xFB; // -5
            FixRobustChecksum(corrupt);

            Assert.ThrowsAny<Ceras.Exceptions.CerasException>(() => serializer.Load<CtorStrictRoot>(corrupt, 1));

            // The interpreter names the type, the payload field and the constructor's own failure.
            var slow = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(
                () => serializer.Load<CtorStrictRoot>(SlowPath(corrupt), 1));
            Assert.Contains("CtorStrictModel", slow.Message, StringComparison.Ordinal);
            Assert.Contains("Value", slow.Message, StringComparison.Ordinal);
            Assert.Contains("ArgumentOutOfRangeException", slow.Message, StringComparison.Ordinal);
        }
    }
}
