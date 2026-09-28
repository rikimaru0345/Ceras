// The emitted code's own statement of the member-order contract.
//
// One emitted class writes every closure of a generic definition, so the *only* thing that can describe
// those bytes to a later build is the per-closure schema - and it has to list its fields in the order
// that one class emits them. The runtime side of this is in GenericMemberOrderTests (a real
// version-separated file, and the fast path); here the invariant is asserted on the emitted source
// itself, for every closure of a model built to make the two orders differ:
//
//   * a member declared as a type parameter, closed over a type whose name sorts before `System.Int32`
//     (`Wrap<T> { T Value; int Tag; }` over `OrderProbe.Alpha.Thing`): written `Tag, Value`, closed sort
//     `Value, Tag`;
//   * a generic base, so the members the closure *inherits* have to be matched to the definition that
//     declares them rather than to the closed derived type - `Derived<T> : Base<T>`, whose two inherited
//     members land in the middle of the written order;
//   * a single-member generic, whose two orders agree, so the fix must leave it alone.
//
// The generator is run through the shared harness (GeneratorTestHarness), and the assertions below are
// deliberately textual and independent of how classes are named: the member order is read out of the
// shared writer's `Serialize` body, the field order out of the per-closed schema expression, and the two
// are paired by the base class each closed schema names. A closure is identified by its canonical stable
// name, which is the string that ends up in the file.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Ceras.Test.Generator
{
    public class GeneratedGenericSchemaOrderTests
    {
        /// <summary>
        /// A model where the closed member order differs from the written one for every generic, and an
        /// inheritance edge to make the member matching do real work. <c>OrderProbe.Alpha.Thing</c> sorts
        /// before <c>System.Int32</c>, which is what moves a <c>T</c> member ahead of an <c>int</c> one
        /// in the closed order but not in the written one.
        /// </summary>
        const string Model = @"
using System;
using Ceras;

namespace OrderProbe.Alpha
{
    [CerasSerializable]
    public class Thing { public int V; }
}

namespace OrderProbe
{
    [CerasSerializable]
    public class Wrap<T> { public T Value; public int Tag; }

    [CerasSerializable]
    public class Base<T> { public T BValue; public int BTag; }

    /// <summary>Inherits two members from a generic base.</summary>
    [CerasSerializable]
    public class Derived<T> : Base<T> { public T DValue; public int DTag; }

    /// <summary>One member: the two orders cannot differ.</summary>
    [CerasSerializable]
    public class Single<T> { public T Only; }

    [CerasSerializable]
    [CerasContext(""OrderProbe"")]
    public class Root
    {
        public Wrap<Alpha.Thing> Wrapped;
        public Derived<Alpha.Thing> Derived;
        public Single<Alpha.Thing> Single;
    }
}";

        [Fact]
        public void Every_Closed_Schema_Lists_Its_Fields_In_The_Order_The_Shared_Writer_Emits_Them()
        {
            var (diagnostics, generated) = GeneratorTestHarness.Run(Model);

            // A model that stopped generating would pass the comparisons below vacuously.
            Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Contains(generated, g => g.Hint.Contains("OrderProbe_Root"));

            // The member order each shared writer emits, keyed by the class name a per-closed schema
            // derives from: the members its `Serialize` body reads, in order.
            var written = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var (_, text) in generated)
            {
                var declared = Regex.Match(text, @"internal class (\w+)<");
                if (declared.Success)
                    written[declared.Groups[1].Value] = MembersWrittenBy(text);
            }

            Assert.Equal(3, written.Count);      // Wrap, Base and Derived share one writer each

            // Every schema that belongs to a shared writer has to list its fields in that writer's order.
            // A non-generic type emits its writer next to its own schema, so it is consistent by
            // construction and is not part of this comparison.
            var compared = 0;
            foreach (var (hintName, text) in generated)
            {
                if (!text.Contains("SchemaFieldInfo(")) continue;

                var baseName = Regex.Match(text, @"internal sealed class \w+ : (\w+)").Groups[1].Value;
                if (!written.TryGetValue(baseName, out var members)) continue;

                compared++;
                Assert.True(members.SequenceEqual(SchemaFields(text)),
                    $"{hintName}: the schema lists [{string.Join(", ", SchemaFields(text))}] but the shared " +
                    $"writer emits [{string.Join(", ", members)}]");
            }

            Assert.Equal(3, compared);           // Wrap, Derived and Single
        }

        [Fact]
        public void The_Order_Is_The_Definitions_Not_The_Closed_One()
        {
            var (diagnostics, generated) = GeneratorTestHarness.Run(Model);
            Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

            // `Wrap<OrderProbe.Alpha.Thing>`: the writer emits Tag (System.Int32) before Value (the
            // parameter "T"), because 'S' < 'T'. The closed order would be Value, Tag, because
            // "OrderProbe.Alpha.Thing" sorts before "System.Int32".
            Assert.Equal(new[] { "Tag", "Value" }, SchemaFieldsOf(generated, "OrderProbe.Wrap`1<OrderProbe.Alpha.Thing>"));

            // `Derived<...> : Base<...>`: the sort key is member type + member name, so every `int` member
            // comes before every `T` one regardless of which type declares it - the two inherited ones
            // included, and they land ahead of the derived `T` member. The closed order would be
            // BValue, DValue, BTag, DTag, i.e. exactly reversed.
            Assert.Equal(new[] { "BTag", "DTag", "BValue", "DValue" },
                SchemaFieldsOf(generated, "OrderProbe.Derived`1<OrderProbe.Alpha.Thing>"));

            // A single-member generic: nothing to reorder, and the fix must not invent an order.
            Assert.Equal(new[] { "Only" },
                SchemaFieldsOf(generated, "OrderProbe.Single`1<OrderProbe.Alpha.Thing>"));
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The field order of the schema emitted for a closed type, found by its canonical stable name -
        /// the string that ends up in the file, rather than by the generated class name, so the assertion
        /// is about the format and not about how classes are named.
        /// </summary>
        static string[] SchemaFieldsOf(IEnumerable<(string Hint, string Text)> sources, string stableName)
        {
            foreach (var (_, text) in sources)
                if (text.Contains("stableName: \"" + stableName + "\""))
                    return SchemaFields(text);

            throw new Xunit.Sdk.XunitException($"no generated schema found for the closed type '{stableName}'");
        }

        /// <summary>
        /// The field names a per-closed schema expression lists, in order. The pattern deliberately stops
        /// at the name: every entry starts the same way, so matching the prefix is enough and it keeps the
        /// pattern free of the argument list that follows.
        /// </summary>
        static string[] SchemaFields(string text)
            => Regex.Matches(text, @"SchemaFieldInfo\(""([^""]+)""")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToArray();

        /// <summary>
        /// The members a shared writer's <c>Serialize</c> reads, in order. The body is taken by brace
        /// matching from the method signature, so the <c>Deserialize</c> half and the class preamble
        /// cannot contribute.
        /// </summary>
        static string[] MembersWrittenBy(string text)
        {
            var signature = text.IndexOf("public override void Serialize", StringComparison.Ordinal);
            if (signature < 0) throw new Xunit.Sdk.XunitException("no Serialize in the shared writer");

            int open = text.IndexOf('{', signature);
            int depth = 0, close = open;
            for (; close < text.Length; close++)
            {
                if (text[close] == '{') depth++;
                else if (text[close] == '}' && --depth == 0) break;
            }

            return Regex.Matches(text.Substring(open, close - open), @"value\.(\w+)")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToArray();
        }
    }
}
