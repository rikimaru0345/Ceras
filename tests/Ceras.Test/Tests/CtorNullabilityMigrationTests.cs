// R4: what a [CerasConstructor] parameter may be given by a save file.
//
//   * A null is a value the format can genuinely record, so it is passed straight through for the two
//     shapes that have one: a reference type and a Nullable<T>. A model whose constructor takes such a
//     parameter must migrate, and the value the file stored must survive.
//   * A null for a parameter that is a *non-nullable value type* is not a value the format can produce
//     for it, and reflection does not stop it: `ConstructorInfo.Invoke` substitutes default(T), runs
//     the constructor with it and returns null without raising anything. The load therefore succeeds
//     with an argument the save never contained — silent data loss — and it does so after the user's
//     constructor body has already run. Ceras refuses it instead, at the first point the value exists:
//     before the constructor, before any member is assigned, and before the object can reach a deferred
//     [OnAfterDeserialize], naming the parameter, the member and the type.
//
// No generator-written model can ask for the second case — the generated reader would not compile the
// same constructor call — so it is driven through a hand-built old schema and a hand-built plan, which
// is the same lever the corruption tests use and the one the engine's own `plans` parameter exists for.
// The two cases are then compared on exactly that machinery, so the only difference between them is the
// declared type of the parameter.
using System;
using System.Buffers;
using System.Collections.Generic;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test
{
    [CerasContext("CtorNullability")]
    [CerasSerializable]
    public class NullableCtorArgModel
    {
        public string Name;
        public int? Count;
        public int Tail;

        [CerasConstructor]
        public NullableCtorArgModel(string name, int? count, int tail)
        {
            Name = name;
            Count = count;
            Tail = tail;
        }
    }

    [CerasContext("CtorNullability")]
    [CerasSerializable]
    public class ValueCtorParamModel
    {
        public int Value;

        public static bool CtorRan;
        public static bool AfterCallbackRan;

        [CerasConstructor]
        public ValueCtorParamModel(int value)
        {
            Value = value;
            CtorRan = true;
        }

        [OnAfterDeserialize]
        void After() => AfterCallbackRan = true;
    }

    public class CtorNullabilityMigrationTests : TestBase
    {
        const string Ctx = "CtorNullability";

        static CerasRobustSerializer New(Action<CerasRobustConfig> extra = null)
            => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                extra?.Invoke(c);
            });

        // ── A null the parameter *can* hold is passed through ──────────────────────────────────

        [Fact]
        public void A_Null_For_A_Reference_Or_Nullable_Ctor_Parameter_Is_Accepted_By_Both_Readers()
        {
            var ceras = New();
            var bytes = ceras.Save(new NullableCtorArgModel(null, null, 7), 1);

            // The generated reader and the migration interpreter construct the same type the same way,
            // so a null argument has to be equally acceptable on both; only the fingerprint differs.
            var generated = ceras.Load<NullableCtorArgModel>(bytes, 1);
            var interpreted = New().Load<NullableCtorArgModel>(SlowPath(bytes), 1);

            foreach (var loaded in new[] { generated, interpreted })
            {
                Assert.Null(loaded.Name);
                Assert.Null(loaded.Count);
                Assert.Equal(7, loaded.Tail);
            }
        }

        [Fact]
        public void A_Non_Null_Value_For_A_Reference_Or_Nullable_Ctor_Parameter_Is_Unaffected()
        {
            var ceras = New();
            var bytes = ceras.Save(new NullableCtorArgModel("n", 3, 7), 1);

            var generated = ceras.Load<NullableCtorArgModel>(bytes, 1);
            var interpreted = New().Load<NullableCtorArgModel>(SlowPath(bytes), 1);

            foreach (var loaded in new[] { generated, interpreted })
            {
                Assert.Equal("n", loaded.Name);
                Assert.Equal(3, loaded.Count);
                Assert.Equal(7, loaded.Tail);
            }
        }

        // ── The same null, the same plan, two parameter types ──────────────────────────────────

        [Fact]
        public void A_Null_Is_Passed_To_A_Reference_Ctor_Parameter_And_Refused_For_A_Value_Type_One()
        {
            // Half of the contract, on the machinery the other half needs: the file records the member
            // as a polymorphic slot and stores a null marker for it, and the [CerasConstructor]
            // parameter that member feeds is a `string` — so the null is a value the parameter has.
            var referenceCase = EngineOver(nameof(NullableCtorArgModel.Name), typeof(NullableCtorArgModel), New());
            var loaded = ReadRoot<NullableCtorArgModel>(referenceCase);
            Assert.Null(loaded.Name);

            // The other half: identical file, identical plan shape, and the parameter is an `int` — which
            // has no null, so the read is refused instead.
            var valueCase = EngineOver(nameof(ValueCtorParamModel.Value), typeof(ValueCtorParamModel), New());
            var ex = Assert.Throws<CerasSchemaException>(() => ReadRoot<ValueCtorParamModel>(valueCase));

            // Typed, and it names the parameter, the member the null was read for, and the type the
            // parameter is — which is the whole point, because without the guard nothing is named at
            // all: reflection hands the constructor a default and the load reports success.
            Assert.Contains("'value'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("'Value'", ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(Int32), ex.Message, StringComparison.Ordinal);
        }

        // ── The refusal is early: no constructor, no assignment, no deferred callback ───────────

        [Fact]
        public void A_Null_For_A_Value_Type_Ctor_Parameter_Is_Refused_Before_Anything_Is_Built()
        {
            ValueCtorParamModel built = null;
            var ceras = New(c => c.ConfigType<ValueCtorParamModel>().ConstructByDelegate(() =>
            {
                // The instance shape the interpreter's own fallback produces, so the flag that says "the
                // constructor ran" is about the read and not about this factory.
                built = (ValueCtorParamModel)System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(typeof(ValueCtorParamModel));
                return built;
            }));

            var engine = EngineOver(nameof(ValueCtorParamModel.Value), typeof(ValueCtorParamModel), ceras);

            ValueCtorParamModel.CtorRan = false;
            ValueCtorParamModel.AfterCallbackRan = false;

            Assert.Throws<CerasSchemaException>(() => ReadRoot<ValueCtorParamModel>(engine));

            // The configured construction has run (the interpreter allocates before it reads, so that a
            // cycle through a constructor parameter resolves) — but the constructor it exists to feed
            // must not have, and nothing may have been written to the instance.
            Assert.NotNull(built);
            Assert.False(ValueCtorParamModel.CtorRan, "the constructor must not run for a save that cannot supply its argument");
            Assert.Equal(0, built.Value);

            // ... and the object must not have reached a deferred [OnAfterDeserialize]. The callbacks of
            // the abandoned read are still queued, so flushing them is exactly what a successful load
            // would have done, and nothing was enqueued.
            ceras.FlushDeserializeCallbacks();
            Assert.False(ValueCtorParamModel.AfterCallbackRan, "a refused load must not run the post-deserialization callback");
        }

        // ── The hand-built "old" schema, plan and payload both cases above are read through ──────

        const int ModelTypeId = 200;

        static T ReadRoot<T>(SchemaTraversalEngine engine)
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(NullMarkerPayload()));
            return engine.ReadRoot<T>(ref reader, default);
        }

        /// <summary>
        /// An engine over a file whose schema records <paramref name="memberName"/> as a polymorphic
        /// slot — so its payload is a reference marker, and a marker can be a null — mapped onto the
        /// current model's member of that name.
        /// </summary>
        static SchemaTraversalEngine EngineOver(string memberName, Type currentType, CerasRobustSerializer ceras)
        {
            var newField = currentType.GetField(memberName);
            Assert.NotNull(newField);

            var oldField = new OldFieldSchema(memberName, Array.Empty<string>(), WellKnownTypes.Object, isMonomorphic: false);
            var plan = new TypeMigrationPlan
            {
                CurrentType = currentType,
                Mappings = new[] { new FieldMapping { OldField = oldField, NewField = newField, Coercion = CoercionKind.None } },
                AddedFieldNames = Array.Empty<string>(),
            };

            var stableName = TypeIdentity.GetCanonicalName(currentType);
            var schema = new OldTypeSchema(ModelTypeId, stableName, Array.Empty<string>(),
                SchemaTypeKind.Class, new[] { oldField }, 0);

            var schemas = new Dictionary<string, OldTypeSchema> { [stableName] = schema };
            var plans = new Dictionary<string, TypeMigrationPlan> { [stableName] = plan };

            return new SchemaTraversalEngine(schemas, new string[] { null }, 1, ceras, plans);
        }

        /// <summary>
        /// The root's reference marker and its type id, then one member written as a null reference: the
        /// payload a save carries for a value that is nothing.
        /// </summary>
        static byte[] NullMarkerPayload()
        {
            var writer = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(writer, Protocol.RefNewValue, Protocol.RefBias);
            SchemaSection.WriteLeb128(writer, ModelTypeId);
            SerializerBinary.WriteUInt32Bias(writer, Protocol.RefNull, Protocol.RefBias);
            return writer.WrittenSpan.ToArray();
        }
    }
}
