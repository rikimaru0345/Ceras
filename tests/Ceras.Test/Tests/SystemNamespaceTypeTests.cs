using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Ceras.Test.Generator;
using Xunit;

// A user type that merely lives in a System.* namespace — the shape the runtime probe
// (scratch-probes/SystemMembers, Root4) used. The generator used to answer "is this type in the System
// namespace?" for the *well-known* question, so this type was classified as a framework type, got no
// generated formatter and no schema, and the first robust Save failed with advice to declare the type
// that had just been declared.
namespace System.Games
{
    [Ceras.CerasSerializable]
    public class Hero
    {
        public int Hp;
        public string Name;
    }

    /// <summary>
    /// A type in a <c>System.*</c> namespace that the generator is given <em>through a reference</em>: the
    /// in-memory compilations the generator tests build reference this assembly, so a member declared as
    /// <c>System.Games.Sidekick</c> there is a type from another assembly, not one declared in the
    /// compilation being analyzed. It carries no <c>[CerasSerializable]</c>, which is exactly what a
    /// framework type looks like from the generator's side — the ambiguity the classification has to
    /// state rather than guess about.
    /// </summary>
    public class Sidekick
    {
        public int Loyalty;
    }
}

namespace Ceras.Test.SystemNamespace
{
    [CerasContext("SystemNamespace")]
    [CerasSerializable]
    public class SystemNamespaceRoot
    {
        public int Id;
        public global::System.Games.Hero Hero;
        public global::System.Games.Hero[] Squad;
        public string Tail;
    }

    /// <summary>
    /// The built-in controls, in the same context: the shapes the format really does describe and the
    /// generator must keep accepting (a System.* enum, arrays, the structural value types and the built-in
    /// collections). Living in a System.* namespace is not a reason to reject any of them.
    /// </summary>
    [CerasContext("SystemNamespace")]
    [CerasSerializable]
    public class SystemNamespaceControls
    {
        public int[] Sizes;
        public string[] Names;
        public int[][] Jagged;
        public DayOfWeek Day;
        public int? Maybe;
        public (int, string) Pair;
        public KeyValuePair<string, int> Kvp;
        public List<int> Items;
        public Dictionary<string, int> Scores;
        public HashSet<int> Unique;
        public Queue<int> Pending;
        public Stack<int> Done;
        public Guid Id2;
        public DateTime When;
        public int After;
    }

    /// <summary>
    /// Pins the System-namespace discovery fix at runtime: a <c>[CerasSerializable]</c> type declared in a
    /// <c>System.*</c> namespace is an ordinary user type for the generated manifest, and the supported
    /// structural/BCL shapes around it keep working on both readers.
    /// </summary>
    public class SystemNamespaceTypeTests : TestBase
    {
        const string Ctx = "SystemNamespace";

        static CerasRobustSerializer New() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        [Fact]
        public void UserTypeInSystemNamespace_RoundTrips_Both_Paths()
        {
            var s = New();
            var original = new SystemNamespaceRoot
            {
                Id = 7,
                Hero = new global::System.Games.Hero { Hp = 3, Name = "h" },
                Squad = new[]
                {
                    new global::System.Games.Hero { Hp = 1, Name = "a" },
                    new global::System.Games.Hero { Hp = 2, Name = "b" },
                },
                Tail = "t",
            };

            var bytes = s.Save(original, 1);

            // The generated (fingerprint-matching) reader and the interpreter must agree.
            foreach (var clone in new[] { s.Load<SystemNamespaceRoot>(bytes, 1), s.Load<SystemNamespaceRoot>(SlowPath(bytes), 1) })
            {
                Assert.NotNull(clone);
                Assert.Equal(7, clone.Id);
                Assert.NotNull(clone.Hero);
                Assert.Equal(3, clone.Hero.Hp);
                Assert.Equal("h", clone.Hero.Name);

                // The array form has to reach the same conclusion about the element type: a wrapper must
                // not turn a user type back into a framework type.
                Assert.Equal(2, clone.Squad.Length);
                Assert.Equal(1, clone.Squad[0].Hp);
                Assert.Equal("a", clone.Squad[0].Name);
                Assert.Equal(2, clone.Squad[1].Hp);
                Assert.Equal("b", clone.Squad[1].Name);

                Assert.Equal("t", clone.Tail);
            }
        }

        [Fact]
        public void UserTypeInSystemNamespace_HasAGeneratedSchemaInTheManifest()
        {
            // Not a placeholder: a real field-based schema and a stable name, which is what the
            // identity/migration machinery resolves a file's type ids against.
            var s = New();
            s.Save(new SystemNamespaceRoot { Id = 1, Hero = new global::System.Games.Hero(), Tail = "t" }, 1);

            Assert.True(s.Config.SchemaRegistry.TryGetInfo(typeof(global::System.Games.Hero), out var info));
            Assert.Equal(SchemaTypeKind.Class, info.Kind);
            Assert.Equal("System.Games.Hero", info.StableName);
            Assert.Equal(new[] { "Hp", "Name" }, info.Fields.Select(f => f.StableName).ToArray());
            Assert.Equal(new[] { "System.Int32", "System.String" }, info.Fields.Select(f => f.TypeName).ToArray());
            Assert.True(s.Config.SchemaRegistry.TryGetId(typeof(global::System.Games.Hero), out _));
        }

        [Fact]
        public void BuiltInShapes_StillRoundTrip_Both_Paths()
        {
            var s = New();
            var original = new SystemNamespaceControls
            {
                Sizes = new[] { 1, 2 },
                Names = new[] { "a" },
                Jagged = new[] { new[] { 1 }, new[] { 2, 3 } },
                Day = DayOfWeek.Wednesday,
                Maybe = 5,
                Pair = (7, "x"),
                Kvp = new KeyValuePair<string, int>("k", 9),
                Items = new List<int> { 1, 2, 3 },
                Scores = new Dictionary<string, int> { ["a"] = 4 },
                Unique = new HashSet<int> { 5 },
                Pending = new Queue<int>(new[] { 6 }),
                Done = new Stack<int>(new[] { 7 }),
                Id2 = new Guid("11111111-2222-3333-4444-555555555555"),
                When = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Unspecified),
                After = 4242,
            };

            var bytes = s.Save(original, 1);

            foreach (var clone in new[] { s.Load<SystemNamespaceControls>(bytes, 1), s.Load<SystemNamespaceControls>(SlowPath(bytes), 1) })
            {
                Assert.NotNull(clone);
                Assert.Equal(new[] { 1, 2 }, clone.Sizes);
                Assert.Equal(new[] { "a" }, clone.Names);
                Assert.Equal(new[] { new[] { 1 }, new[] { 2, 3 } }, clone.Jagged);
                Assert.Equal(DayOfWeek.Wednesday, clone.Day);
                Assert.Equal(5, clone.Maybe);
                Assert.Equal((7, "x"), clone.Pair);
                Assert.Equal("k", clone.Kvp.Key);
                Assert.Equal(9, clone.Kvp.Value);
                Assert.Equal(new[] { 1, 2, 3 }, clone.Items);
                Assert.Equal(4, clone.Scores["a"]);
                Assert.Equal(new[] { 5 }, clone.Unique);
                Assert.Equal(new[] { 6 }, clone.Pending);
                Assert.Equal(new[] { 7 }, clone.Done);
                Assert.Equal(original.Id2, clone.Id2);
                Assert.Equal(original.When, clone.When);
                Assert.Equal(4242, clone.After);
            }
        }

        [Fact]
        public void EnumMemberInSystemNamespace_UsesTheRuntimeDerivedSchema()
        {
            // A System.* enum is a supported shape, not a framework type to reject: its schema comes from
            // CLR metadata at declaration time.
            var s = New();
            s.Save(new SystemNamespaceControls { Day = DayOfWeek.Friday, After = 1 }, 1);

            Assert.True(s.Config.SchemaRegistry.TryGetInfo(typeof(DayOfWeek), out var info));
            Assert.Equal(SchemaTypeKind.Enum, info.Kind);
            Assert.Equal("System.Int32", info.UnderlyingTypeName);
            Assert.Contains(info.EnumValues, v => v.Name == nameof(DayOfWeek.Wednesday));
        }
    }

    /// <summary>
    /// Pins the generator's supported/unsupported classification against the runtime's. The generator
    /// cannot reference the runtime assembly — it re-implements <c>WellKnownTypes</c> and
    /// <c>SchemaRegistry.TryBuildStructuralSchema</c> over symbols — so each row states what the format
    /// really does with a type and both sides are checked against it. A change to the runtime's
    /// well-known tables, or to the generator's mirror of them, then fails here instead of turning into a
    /// build that compiles and a first <c>Save</c> that throws.
    /// </summary>
    public class SystemNamespaceClassificationTests
    {
        /// <summary>
        /// (member type as written, the runtime type, can the format describe it, does the generator
        /// report the member). The last two columns are the contract each side has to match.
        /// </summary>
        public static IEnumerable<object[]> BuiltinShapes => new[]
        {
            // --- the reserved primitive/string/object ids, and the fixed-width BCL value types -------
            new object[] { "int", typeof(int), true, false },
            new object[] { "bool", typeof(bool), true, false },
            new object[] { "string", typeof(string), true, false },
            new object[] { "object", typeof(object), true, false },
            new object[] { "decimal", typeof(decimal), true, false },
            new object[] { "System.Guid", typeof(Guid), true, false },
            new object[] { "System.DateTime", typeof(DateTime), true, false },
            new object[] { "System.DateTimeOffset", typeof(DateTimeOffset), true, false },
            new object[] { "System.TimeSpan", typeof(TimeSpan), true, false },
            new object[] { "System.Type", typeof(Type), true, false },

            // --- enums: the value table is derived from CLR metadata ---------------------------------
            new object[] { "System.DayOfWeek", typeof(DayOfWeek), true, false },

            // --- structural shapes -------------------------------------------------------------------
            new object[] { "int[]", typeof(int[]), true, false },
            new object[] { "string[]", typeof(string[]), true, false },
            new object[] { "int[][]", typeof(int[][]), true, false },
            new object[] { "int?", typeof(int?), true, false },
            new object[] { "(int, string)", typeof((int, string)), true, false },
            new object[] { "System.Tuple<int, string>", typeof(Tuple<int, string>), true, false },
            // Eight arguments: there is no reserved id beyond seven, so this is a framework type the
            // format cannot describe.
            new object[] { "(int, int, int, int, int, int, int, string)", typeof((int, int, int, int, int, int, int, string)), false, true },
            new object[] { "System.Collections.Generic.KeyValuePair<string, int>", typeof(KeyValuePair<string, int>), true, false },
            new object[] { "System.Collections.Generic.List<int>", typeof(List<int>), true, false },
            new object[] { "System.Collections.Generic.Dictionary<string, int>", typeof(Dictionary<string, int>), true, false },
            new object[] { "System.Collections.Generic.HashSet<int>", typeof(HashSet<int>), true, false },
            new object[] { "System.Collections.Generic.Queue<int>", typeof(Queue<int>), true, false },
            new object[] { "System.Collections.Generic.Stack<int>", typeof(Stack<int>), true, false },
            new object[] { "System.Collections.Generic.ICollection<int>", typeof(ICollection<int>), true, false },
            new object[] { "System.Collections.Generic.IList<int>", typeof(IList<int>), true, false },

            // --- framework types with no reserved id and no structural shape --------------------------
            new object[] { "System.Uri", typeof(Uri), false, true },
            new object[] { "System.Version", typeof(Version), false, true },
            new object[] { "System.Numerics.BigInteger", typeof(System.Numerics.BigInteger), false, true },
            new object[] { "System.Text.StringBuilder", typeof(System.Text.StringBuilder), false, true },
            new object[] { "System.Attribute", typeof(Attribute), false, true },
            new object[] { "IntPtr", typeof(IntPtr), false, true },
            new object[] { "System.Collections.ArrayList", typeof(ArrayList), false, true },
            new object[] { "System.Collections.ICollection", typeof(ICollection), false, true },
            new object[] { "System.Collections.Generic.IEnumerable<int>", typeof(IEnumerable<int>), false, true },

            // A type from another assembly but outside the System.* namespaces keeps the existing
            // minimal-registration/custom-formatter path, so it is not reported here either.
            new object[] { "Ceras.CerasSerializer", typeof(CerasSerializer), false, false },
        };

        [Theory]
        [MemberData(nameof(BuiltinShapes))]
        public void GeneratorAndRuntimeAgree_OnWhatTheFormatCanDescribe(
            string memberType, Type runtimeType, bool describable, bool generatorReportsMember)
        {
            // The rule the generator implements, restated so a row that contradicts it fails loudly.
            bool inSystemNamespace = runtimeType.Namespace == "System"
                || (runtimeType.Namespace?.StartsWith("System.", StringComparison.Ordinal) ?? false);
            Assert.Equal(inSystemNamespace && !describable, generatorReportsMember);

            // The runtime side: the question SchemaSection.TryGetAutoRegistration asks at bake time.
            Assert.Equal(describable, RuntimeCanDescribe(runtimeType));

            // The generator side: does a member of this type get reported?
            var source = @"
using Ceras;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Root
    {
        public " + memberType + @" Member;
        public int After;
    }
}";

            Assert.Equal(generatorReportsMember, GeneratorTestHarness.DiagnosticIds(source).Contains("CERAS029"));
        }

        /// <summary>
        /// Mirrors <c>Ceras.Format.SchemaSection.TryGetAutoRegistration</c>: the shapes the bake
        /// registers structurally on its own, so a member of such a type needs no generated schema. Never
        /// called with a shape that raises (a multidimensional array, a <c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c>
        /// subclass); those report their own dedicated error.
        /// </summary>
        static bool RuntimeCanDescribe(Type type)
        {
            if (WellKnownTypes.IsWellKnown(type)) return true;

            WellKnownTypes.TryResolveCollectionShape(type, out _, out _, out var rejection);
            if (rejection != CollectionShapeRejection.None) return false;

            if (SchemaRegistry.TryBuildStructuralSchema(type, out _)) return true;

            if (type.IsGenericType && !type.IsGenericTypeDefinition &&
                WellKnownTypes.TryGetId(type.GetGenericTypeDefinition(), out _))
                return true;

            return type.IsEnum;
        }

        // The runtime bake is the last line of defence — it is reached when the compile-time report is
        // suppressed, and for a manifest assembled by hand — so it must not send the user back to a fix
        // that cannot work. The registry below is exactly the manifest the generator used to emit for a
        // member of type System.Uri: the declaring type is registered with its field, and the field's
        // framework type is not, because the generator believed it had a built-in formatter. The probe
        // showed that failing with advice which cannot help, and that neither declaring System.Uri nor
        // registering a custom IFormatter<Uri> changed that.
        [Fact]
        public void BakeError_DoesNotTellTheUserToDeclareAFrameworkType()
        {
            var registry = new SchemaRegistry();
            registry.Register(typeof(BakeProbeRoot), new SchemaTypeInfo(
                stableName: "SystemNamespace.BakeProbeRoot",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Class,
                fields: new[] { new SchemaFieldInfo("Site", Array.Empty<string>(), "System.Uri", true) },
                fixedByteSize: 0));

            // Sealing is what bakes the schema, so the failure surfaces here — the same place it did for a
            // user of the pre-fix generator.
            var ex = Assert.Throws<CerasSchemaException>(() => registry.Seal());

            Assert.Contains("System.Uri", ex.Message, StringComparison.Ordinal);
            Assert.Contains("no built-in representation", ex.Message, StringComparison.Ordinal);
            Assert.Contains("no custom IFormatter<T> in a robust context can make it serializable", ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Only ever used through a hand-built manifest, so the generator has nothing to say about it — a real
    /// <c>[CerasContext]</c> member like this would be CERAS029 at compile time.
    /// </summary>
    public class BakeProbeRoot
    {
        public Uri Site;
    }
}
