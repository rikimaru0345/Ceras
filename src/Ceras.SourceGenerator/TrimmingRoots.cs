using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using static Ceras.SourceGenerator.TypeHelpers;

namespace Ceras.SourceGenerator
{
    /// <summary>
    /// Emits the trimming / NativeAOT roots this compilation needs, as
    /// <c>[DynamicDependency]</c> attributes on the generated registration entry point.
    /// <para>
    /// <b>Why any of this is needed.</b> Ceras reads three things at runtime that no compiler can
    /// see, because each of them is reached by reflection over a runtime value rather than by a call
    /// a root could follow:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>The data members of a serialized type.</b> <c>TypeConfig..ctor</c> builds a
    /// <c>MemberConfig</c> per member by walking <c>Type.GetFields</c> / <c>Type.GetProperties</c>
    /// over a runtime <see cref="Type"/> (<c>ReflectionHelper.EnumerateMembers</c>), and
    /// <c>MemberConfig</c> then reads each member's name, declaring type, <c>IsInitOnly</c>, accessors
    /// and attributes. Nothing in the compilation names those members, so a trimmer and the AOT
    /// compiler are both free to drop their metadata - and when they do, the type config comes back
    /// with <i>zero</i> members rather than with an error. A trimmed build then writes a schema that
    /// disagrees with the payload the generated formatter actually wrote.
    /// </description></item>
    /// <item><description>
    /// <b>The generated formatter's own <c>Serialize</c> / <c>Deserialize</c> methods.</b>
    /// <c>ReferenceFormatter&lt;T&gt;</c> binds its dispatch delegate by looking those two methods up
    /// by name (<c>ReflectionHelper.ResolveMethod</c>), because the formatter reaches it as a bare
    /// <c>IFormatter</c>. A generated formatter is only ever called through that interface, so its
    /// method metadata is not otherwise required.
    /// </description></item>
    /// <item><description>
    /// <b>The generated formatter's static <c>Schema</c> field.</b>
    /// <c>RobustCustomFormatterPolicy</c> recognises the context's own formatters by identity, by
    /// reading that field off the installed formatter's runtime type. Without the field's metadata the
    /// check cannot see it, and every generated formatter is then reported as a user custom formatter
    /// that a robust context must refuse.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Why the generator, and not the library.</b> The direct construction these roots support is
    /// already emitted: the registration calls <c>ConfigType&lt;T&gt;()</c> for every type in the
    /// context, which constructs the closed <c>TypeConfig&lt;T&gt;</c> directly, and
    /// <c>TypeConfig&lt;T&gt;.CreateMemberConfig</c> constructs the closed <c>MemberConfig&lt;T&gt;</c>
    /// directly. So the closed generics have native code; what is missing is <i>reflection
    /// metadata</i> about members that only reflection names. Only the generator knows the emitted
    /// formatter types and the full member set (including the base types that contribute inherited
    /// members), so only the generator can state these dependencies - and stating them here keeps
    /// them next to the manifest that implies them.
    /// </para>
    /// <para>
    /// The dependencies go on <c>UseRobustFormatters</c> because that is the one emitted member every
    /// generator-enabled application calls directly, in a raw context and a robust one alike, so the
    /// attributes are honoured no matter which of the two the application builds. They are emitted in
    /// canonical-name order, so two builds of one compilation produce byte-identical generated code.
    /// </para>
    /// </summary>
    internal static class TrimmingRoots
    {
        const string Ns = "global::System.Diagnostics.CodeAnalysis.";

        /// <summary>Everything Ceras reads off a member's declaring type (see item 1 above).</summary>
        static readonly string[] DataMembers =
        {
            "PublicFields", "NonPublicFields", "PublicProperties", "NonPublicProperties",
        };

        /// <summary>
        /// What is additionally needed of the type being formatted itself: the parameterless
        /// constructor, which is what <c>TypeConfigDefaults.ApplyConstructorDefaults</c> looks for and
        /// what <c>SpecificConstructor.GetRefFormatterConstructor</c> invokes. Only that one type is
        /// constructed, so only that one type asks for constructors.
        /// </summary>
        static readonly string[] PlusParameterlessCtors =
        {
            "PublicParameterlessConstructor", "NonPublicConstructors",
        };

        /// <summary>
        /// What is read off a generated formatter class (items 2 and 3): the two dispatch methods and
        /// the <c>Schema</c> field, which is <c>internal</c>.
        /// </summary>
        static readonly string[] FormatterSurface =
        {
            "PublicMethods", "NonPublicMethods", "PublicFields", "NonPublicFields",
        };

        /// <summary>
        /// Writes one <c>[DynamicDependency]</c> per line, indented to <paramref name="indent"/>.
        /// </summary>
        /// <param name="sb">The generated file's builder.</param>
        /// <param name="indent">Leading whitespace for each attribute line.</param>
        /// <param name="compilation">The compilation the generated code is compiled into.</param>
        /// <param name="formatterTypes">Types that got a generated formatter, with their members.</param>
        /// <param name="minimalTypes">
        /// Types that are in the manifest but got no formatter. Their bytes come from a custom or a
        /// reflection formatter, and both of those go through a <c>TypeConfig</c> that reads the
        /// type's members, so the type is rooted the same way. Only its own members are asked for
        /// and not its base types': nothing here knows which of those contribute an inherited member,
        /// and a type reached this way is by definition one the application supplies a formatter for.
        /// </param>
        public static void WriteDependencies(
            StringBuilder sb,
            string indent,
            Compilation compilation,
            List<INamedTypeSymbol> formatterTypes,
            List<INamedTypeSymbol> minimalTypes,
            Func<INamedTypeSymbol, List<SerializedMember>> discoverMembers)
        {
            // One entry per (type, flags) pair, so the same type reached twice - as a formatted type
            // and as the declaring type of an inherited member, say - is stated once. Sorted by the
            // emitted text, which is what makes the generated file reproducible.
            var lines = new SortedSet<string>(StringComparer.Ordinal);

            void Add(INamedTypeSymbol type, string[] memberTypes, Func<INamedTypeSymbol, string> typeName)
            {
                // `typeof(X)` has to compile in the consumer's own compilation. A type that is not
                // accessible from the assembly being compiled cannot be named there at all (CS0122),
                // so it is left alone: an inaccessible type is not one the application can configure,
                // and rooting it from generated code is not possible by any spelling.
                if (!compilation.IsSymbolAccessibleWithin(type, compilation.Assembly))
                    return;

                // Every flag is spelled out in full, not only the first: the attribute argument is one
                // flag expression, and an unqualified name after the first would not resolve.
                var flags = string.Join(" | ", memberTypes.Select(m => Ns + "DynamicallyAccessedMemberTypes." + m));
                lines.Add($"{indent}[{Ns}DynamicDependency({flags}, typeof({typeName(type)}))]");
            }

            foreach (var type in formatterTypes)
            {
                Add(type, FormatterSurface, MakeFormatterClassName);

                var declaringTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default) { type };
                foreach (var m in discoverMembers(type))
                    declaringTypes.Add(m.DeclaringType);

                // The formatted type itself also needs its parameterless constructor; a base type that
                // only contributes fields never has an instance built, so it asks for members alone.
                foreach (var declaring in declaringTypes)
                {
                    bool isFormattedType = SymbolEqualityComparer.Default.Equals(declaring, type);
                    Add(declaring,
                        isFormattedType ? DataMembers.Concat(PlusParameterlessCtors).ToArray() : DataMembers,
                        Name);
                }
            }

            foreach (var type in minimalTypes)
                Add(type, DataMembers, Name);

            foreach (var line in lines)
                sb.AppendLine(line);
        }

        /// <summary>
        /// The spelling of a type inside the generated file: fully qualified and <c>global::</c>-
        /// prefixed, like everything else the generator emits, so it never depends on a using
        /// directive or on the namespace the application's own types live in.
        /// </summary>
        static string Name(INamedTypeSymbol type)
            => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}
