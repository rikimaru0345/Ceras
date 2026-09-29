// The [CerasConstructor] contract for the *member shapes* whose write is not an ordinary public field
// assignment: a readonly field, an auto-property with no setter, an init-only property, and a private one
// behind a private constructor.
//
// Both readers write a `FieldInfo` and not a property. The generated reader reaches one through an
// `UnsafeAccessor(Field, Name)` — `AccessorEmitter.WriteMemberSet` emits exactly that for a member the
// generator discovered as an auto-property, whose member *is* its `<Name>k__BackingField`
// (`MemberExplorer`: "Properties are no longer serialized directly") — and the interpreter resolves one
// through `MigrationPlanBuilder`'s member map, which enumerates instance fields and peels the same
// backing-field name. So `readonly`, `get`-only, `init`-only and `private set` are the *same* shape for
// both readers, and there is no "the setter is inaccessible" case to handle: a member with no writable
// storage at all is not a member on either side. `FieldInfo.SetValue` writes a readonly instance field on
// .NET 10, so nothing here needs the generated reader's ref-accessor.
//
// Same-layout cross-check, and only that: the fingerprint flip in `TestBase.SlowPath` is what selects the
// reader, and the bytes describe exactly the current schema. The cross-version version of the same rule —
// a stored value the current model's constructor would not have produced, and a *reference* argument the
// constructor drops — is `HistoricalCtorFixtureTests`, which reads a real v1 file.
//
// xunit runs one test class at a time, so a static switch the author flips between saving and loading is
// enough; only this file touches it.
using System;
using Xunit;
using Ceras;
using Ceras.Generated;

namespace Ceras.Test.ConstructorRewriteMembers
{
    /// <summary>
    /// The switch. Off while the graph is built, so the *stored* value is the plain one; on while it is
    /// loaded, so the constructor rewrites it. Both readers see the same bytes and run the same bodies —
    /// only the order of the assignment relative to the constructor can differ between them, and a rewrite
    /// that survives it is what these tests are looking for.
    /// </summary>
    public static class RewriteSwitch
    {
        public static bool On;
        public static void Reset() => On = false;
    }

    /// <summary>A readonly field, which a constructor body can write and only a ref access can overwrite.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public class ReadonlyFedModel
    {
        public readonly int Payload;
        public int Tail;

        [CerasConstructor]
        public ReadonlyFedModel(int payload)
        {
            Payload = RewriteSwitch.On ? -999 : payload;
            Tail = RewriteSwitch.On ? -999 : 0;
        }
    }

    /// <summary>A get-only auto-property: there is no setter to call at all, on either reader.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public class GetOnlyFedModel
    {
        public int Payload { get; }
        public int Tail { get; set; }

        [CerasConstructor]
        public GetOnlyFedModel(int payload)
        {
            Payload = RewriteSwitch.On ? -999 : payload;
            Tail = RewriteSwitch.On ? -999 : 0;
        }
    }

    /// <summary>An init-only auto-property: a setter exists, but only for an object initializer.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public class InitOnlyFedModel
    {
        public int Payload { get; init; }
        public int Tail { get; set; }

        [CerasConstructor]
        public InitOnlyFedModel(int payload)
        {
            Payload = RewriteSwitch.On ? -999 : payload;
            Tail = RewriteSwitch.On ? -999 : 0;
        }
    }

    /// <summary>A private field behind a private constructor, the shape the interpreter reaches
    /// reflectively and the generated reader through its constructor accessor.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public sealed class PrivateFedModel
    {
        [CerasConstructor]
        private PrivateFedModel(int payload)
        {
            Payload = RewriteSwitch.On ? -999 : payload;
            Tail = RewriteSwitch.On ? -999 : 0;
        }

        public static PrivateFedModel Create(int payload) => new PrivateFedModel(payload);

#pragma warning disable IDE1006
        private int Payload;
        private int Tail;
#pragma warning restore IDE1006

        public int ReadPayload => Payload;
        public int ReadTail => Tail;
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class RewriteRoot
    {
        public ReadonlyFedModel Readonly;
        public GetOnlyFedModel GetOnly;
        public InitOnlyFedModel InitOnly;
        public PrivateFedModel Private;
        public int Tail;
    }

    public class ConstructorRewriteMemberTests : TestBase
    {
        static CerasRobustSerializer Generated() => new(c => c.UseGeneratedFormatters("Tests"));

        /// <summary>The stored value, and the constructor's rewrite of it, for every shape above.</summary>
        const int StoredValue = 111;
        const int ConstructorRewrite = -999;

        static byte[] Save(RewriteRoot graph)
        {
            RewriteSwitch.Reset();
            return Generated().Save(graph, 1);
        }

        static void AssertStoredValuesAreRestored(RewriteRoot loaded)
        {
            Assert.Equal(StoredValue, loaded.Readonly.Payload);
            Assert.Equal(0, loaded.Readonly.Tail);
            Assert.Equal(StoredValue, loaded.GetOnly.Payload);
            Assert.Equal(0, loaded.GetOnly.Tail);
            Assert.Equal(StoredValue, loaded.InitOnly.Payload);
            Assert.Equal(0, loaded.InitOnly.Tail);
            Assert.Equal(StoredValue, loaded.Private.ReadPayload);
            Assert.Equal(0, loaded.Private.ReadTail);
            Assert.Equal(7, loaded.Tail);      // the stream stayed aligned
        }

        /// <summary>
        /// One set of bytes, both readers, the same expectations. Every constructor is one that rewrites
        /// both the value it was handed and the member it was not, so the stored <c>111</c> and the
        /// constructor's <c>-999</c> cannot both be present — the two are read together because either
        /// alone would pass on a reader that only got half the rule.
        /// </summary>
        [Fact]
        public void A_Constructor_That_Rewrites_Its_Argument_Loses_To_The_Payload_On_Both_Readers()
        {
            var bytes = Save(new RewriteRoot
            {
                Readonly = new ReadonlyFedModel(StoredValue),
                GetOnly = new GetOnlyFedModel(StoredValue),
                InitOnly = new InitOnlyFedModel(StoredValue),
                Private = PrivateFedModel.Create(StoredValue),
                Tail = 7,
            });

            RewriteSwitch.On = true;
            try
            {
                AssertStoredValuesAreRestored(Generated().Load<RewriteRoot>(bytes, 1));
                AssertStoredValuesAreRestored(Generated().Load<RewriteRoot>(SlowPath(bytes), 1));
            }
            finally
            {
                RewriteSwitch.Reset();
            }
        }

        /// <summary>
        /// A reused instance is never re-constructed, and the payload is still assigned into it on both
        /// readers — so the rule above is about the constructor's <em>rewrite</em>, not about a member the
        /// constructor was fed being left alone. Without this, a reader that passed the first test by
        /// skipping the constructor entirely would pass both.
        /// </summary>
        [Fact]
        public void A_Reused_Instance_Still_Gets_The_Payload_On_Both_Readers()
        {
            var bytes = Save(new RewriteRoot { Readonly = new ReadonlyFedModel(StoredValue), Tail = 7 });

            RewriteSwitch.On = true;
            try
            {
                foreach (var data in new[] { bytes, SlowPath(bytes) })
                {
                    var existingModel = new ReadonlyFedModel(0);
                    var existing = new RewriteRoot { Readonly = existingModel };
                    Generated().Load(ref existing, data, 1);

                    Assert.Same(existingModel, existing.Readonly);
                    Assert.Equal(StoredValue, existingModel.Payload);
                    Assert.Equal(7, existing.Tail);
                }
            }
            finally
            {
                RewriteSwitch.Reset();
            }
        }

        /// <summary>
        /// The rewrite is observable at all: with the switch on, the constructors really do produce
        /// <c>-999</c>, so the numbers the first test asserts are not what an ordinary construction would
        /// have left behind. Stated here because it is a property of the models, not of either reader.
        /// </summary>
        [Fact]
        public void The_Models_Really_Do_Rewrite_What_They_Are_Handed()
        {
            RewriteSwitch.On = true;
            try
            {
                Assert.Equal(ConstructorRewrite, new ReadonlyFedModel(StoredValue).Payload);
                Assert.Equal(ConstructorRewrite, new GetOnlyFedModel(StoredValue).Payload);
                Assert.Equal(ConstructorRewrite, new InitOnlyFedModel(StoredValue).Payload);
                Assert.Equal(ConstructorRewrite, PrivateFedModel.Create(StoredValue).ReadPayload);
            }
            finally
            {
                RewriteSwitch.Reset();
            }
        }
    }
}
