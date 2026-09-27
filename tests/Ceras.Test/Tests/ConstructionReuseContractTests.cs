using System;
using System.Buffers;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

// Priority 0.4.3 / 0.4.4 — the construction and reuse contract must match between the generated
// reader and the migration interpreter. Every test here runs both readers over identical bytes.
namespace Ceras.Test.ConstructionContract
{
    [CerasContext("Tests")]
    [CerasSerializable]
    public class ContractChild
    {
        public int Id;
        public string Name;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public abstract class ContractBase
    {
        public int BaseValue;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ContractDerivedA : ContractBase
    {
        public string A;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ContractDerivedB : ContractBase
    {
        public string B;
    }

    public struct ContractValue
    {
        public int X;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ContractNode
    {
        public int Id;
        public ContractNode Next;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ContractCycleRoot
    {
        public ContractNode A;
        public ContractNode B;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ContractRoot
    {
        public ContractChild Child;
        public ContractChild AliasA;
        public ContractChild AliasB;
        public ContractBase Poly;
        public List<int> Items = new List<int>();
        public int[] Numbers = Array.Empty<int>();
        public ContractValue Value;
        public int Number;
        public ContractChild Nullable;
    }

    public static class ContractCtorCounter
    {
        public static int Calls;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class ContractCtorNode
    {
        public int Value;

        [CerasConstructor]
        public ContractCtorNode(int value)
        {
            ContractCtorCounter.Calls++;
            Value = value;
        }
    }

    // Used for the "added field keeps its declared default" migration test. `Added`/`AddedRef` are
    // absent from the hand-built legacy schema and must therefore keep their field-initializer values.
    [CerasContext("Tests")]
    [CerasSerializable]
    public class AddedFieldModel
    {
        public int Kept;
        public int Added = 123;
        public string AddedRef = "default";
    }

    /// <summary>
    /// A collection that has a usable Add but no public Clear, so CollectionFormatter must not
    /// silently append to an existing instance.
    /// </summary>
    public class NoClearCollection : System.Collections.ICollection
    {
        public readonly List<int> Items = new List<int>();

        public void Add(int item) => Items.Add(item);

        public int Count => Items.Count;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public void CopyTo(Array array, int index) => ((System.Collections.ICollection)Items).CopyTo(array, index);
        public System.Collections.IEnumerator GetEnumerator() => Items.GetEnumerator();
    }

    public class ConstructionReuseContractTests : TestBase
    {
        static CerasRobustSerializer Generated() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Tests"));

        static byte[] ForceSlowPath(byte[] data)
        {
            var clone = (byte[])data.Clone();
            clone[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(clone);
            return clone;
        }

        /// <summary>Runs the same assertion against a fresh instance on both readers.</summary>
        static void LoadBoth<T>(byte[] bytes, Func<T> makeExisting, Action<T> assert) where T : class
        {
            var generated = makeExisting();
            Generated().Load(ref generated, bytes, 1);
            assert(generated);

            var interpreter = makeExisting();
            Generated().Load(ref interpreter, ForceSlowPath(bytes), 1);
            assert(interpreter);
        }

        [Fact]
        public void ReuseContract_MatchesOnBothReaders()
        {
            var child = new ContractChild { Id = 1, Name = "child" };
            var saved = new ContractRoot
            {
                Child = child,
                AliasA = child,
                AliasB = child,
                Poly = new ContractDerivedB { BaseValue = 2, B = "b" },
                Items = new List<int> { 1, 2, 3 },
                Numbers = new[] { 4, 5, 6 },
                Value = new ContractValue { X = 7 },
                Number = 42,
                Nullable = null,
            };
            var bytes = Generated().Save(saved, 1);

            LoadBoth(bytes,
                () =>
                {
                    var existingChild = new ContractChild { Id = 99, Name = "existing" };
                    var existing = new ContractRoot
                    {
                        Child = existingChild,
                        AliasA = existingChild,
                        AliasB = existingChild,
                        Poly = new ContractDerivedA { BaseValue = 9, A = "stale" },
                        Items = new List<int> { 9, 9, 9, 9, 9 },
                        Numbers = new[] { 9, 9, 9, 9, 9 },
                        Value = new ContractValue { X = 99 },
                        Number = 99,
                        Nullable = new ContractChild { Id = 5, Name = "stale" },
                    };
                    return existing;
                },
                existing =>
                {
                    // Exact-type reuse: the child instance is reused in place, aliases keep identity.
                    Assert.NotNull(existing.Child);
                    Assert.Same(existing.Child, existing.AliasA);
                    Assert.Same(existing.Child, existing.AliasB);
                    Assert.Equal(1, existing.Child.Id);
                    Assert.Equal("child", existing.Child.Name);

                    // Subtype mismatch: a new DerivedB is allocated, not the old DerivedA.
                    var poly = Assert.IsType<ContractDerivedB>(existing.Poly);
                    Assert.Equal(2, poly.BaseValue);
                    Assert.Equal("b", poly.B);

                    // Collections clear + refill in place.
                    Assert.Equal(new[] { 1, 2, 3 }, existing.Items);

                    // Arrays reuse only at equal length (saved 3 vs existing 5 -> fresh instance).
                    Assert.Equal(new[] { 4, 5, 6 }, existing.Numbers);

                    // Value types overwrite.
                    Assert.Equal(7, existing.Value.X);

                    // Scalars overwrite.
                    Assert.Equal(42, existing.Number);

                    // Null replaces the destination.
                    Assert.Null(existing.Nullable);
                });
        }

        [Fact]
        public void ArrayReuse_EqualLength_SameInstance()
        {
            var bytes = Generated().Save(new ContractRoot { Numbers = new[] { 1, 2, 3 } }, 1);

            var existingArray = new[] { 9, 9, 9 };
            var existing = new ContractRoot { Numbers = existingArray };
            Generated().Load(ref existing, bytes, 1);
            Assert.Same(existingArray, existing.Numbers);

            var interpArray = new[] { 9, 9, 9 };
            var interp = new ContractRoot { Numbers = interpArray };
            Generated().Load(ref interp, ForceSlowPath(bytes), 1);
            Assert.Same(interpArray, interp.Numbers);
        }

        [Fact]
        public void MutableCycle_IdentityIsPreservedOnBothReaders()
        {
            var a = new ContractNode { Id = 1 };
            var b = new ContractNode { Id = 2 };
            a.Next = b;
            b.Next = a;
            var bytes = Generated().Save(new ContractCycleRoot { A = a, B = b }, 1);

            LoadBoth(bytes,
                () => new ContractCycleRoot(),
                root =>
                {
                    Assert.NotNull(root.A);
                    Assert.NotNull(root.B);
                    Assert.Same(root.B, root.A.Next);
                    Assert.Same(root.A, root.B.Next);
                });
        }

        [Fact]
        public void Constructors_StayOffReusedInstances_OnBothReaders()
        {
            var bytes = Generated().Save(new ContractCtorNode(11), 1);

            var existing = new ContractCtorNode(0);
            ContractCtorCounter.Calls = 0;
            Generated().Load(ref existing, bytes, 1);
            Assert.Equal(11, existing.Value);
            Assert.Equal(0, ContractCtorCounter.Calls);

            var interp = new ContractCtorNode(0);
            ContractCtorCounter.Calls = 0;
            Generated().Load(ref interp, ForceSlowPath(bytes), 1);
            Assert.Equal(11, interp.Value);
            Assert.Equal(0, ContractCtorCounter.Calls);
        }

        [Fact]
        public void GeneratedCollection_NoClear_FallsBackToFreshConstruction()
        {
            var serializer = Generated();
            var formatter = new Ceras.Formatters.CollectionFormatter<NoClearCollection, int>(serializer);

            var existing = new NoClearCollection();
            existing.Add(9); // must not survive: no Clear means fresh construction, never append

            var writer = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(writer, 2, 1);
            SerializerBinary.WriteInt32(writer, 1);
            SerializerBinary.WriteInt32(writer, 2);

            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(writer.WrittenMemory));
            formatter.Deserialize(ref reader, ref existing);

            Assert.Equal(new[] { 1, 2 }, existing.Items);
        }

        [Fact]
        public void AddedFields_KeepDeclaredDefaults_Interpreter()
        {
            // A hand-built legacy file whose schema contains only 'Kept'. No historical CLR type is
            // needed: the file schema is encoded from an independent registry.
            var bytes = BuildLegacyFile(
                typeof(AddedFieldModel),
                "Ceras.Test.ConstructionContract.AddedFieldModel",
                new[]
                {
                    new SchemaFieldInfo("Kept", Array.Empty<string>(), "System.Int32", true),
                },
                payload => SerializerBinary.WriteInt32(payload, 7));

            var loaded = Generated().Load<AddedFieldModel>(bytes, 1);

            Assert.Equal(7, loaded.Kept);
            Assert.Equal(123, loaded.Added);          // field-initializer default preserved
            Assert.Equal("default", loaded.AddedRef); // reference default preserved
        }

        /// <summary>
        /// Encodes a complete robust file (header + schema + one class payload) for a schema that
        /// differs from the current generated manifest. The deliberately wrong fingerprint forces the
        /// migration interpreter, and the stable name resolves the old schema to the current CLR type.
        /// </summary>
        static byte[] BuildLegacyFile(Type clrType, string stableName, SchemaFieldInfo[] oldFields,
            Action<ArrayBufferWriter<byte>> writePayload)
        {
            var oldRegistry = new CerasRobustConfig().SchemaRegistry;
            oldRegistry.Register(clrType, new SchemaTypeInfo(stableName, Array.Empty<string>(),
                SchemaTypeKind.Class, oldFields, 0));
            oldRegistry.TryGetId(clrType, out int oldTypeId);

            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);

            var intern = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, oldRegistry, intern);
            int schemaStringOffset = offset;
            intern.Flush(ref buffer, ref offset);
            int dataStart = offset;

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)oldTypeId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            writePayload(payload);

            var data = payload.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;

            int stringTableStart = offset;
            var payloadIntern = new StringInternTable();
            payloadIntern.Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(buffer, offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return buffer[..offset];
        }
    }
}
