using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using static Ceras.SourceGenerator.TypeHelpers;

namespace Ceras.SourceGenerator
{
    /// <summary>
    /// One name a generated manifest answers for, and the CLR type that answers it.
    /// <para>
    /// The pair is what <c>Ceras.Format.SchemaRegistry.ClaimNames</c> is asked about: an entry claims its
    /// stable name and every <c>[PreviousTypeName]</c> alias, and a name may be claimed by exactly one
    /// entry. This is the generator's model of those claims — see
    /// <see cref="ManifestNames.ReportDuplicateClaims"/>, which states which names the model contains and
    /// why that is the set the runtime will ask about.
    /// </para>
    /// </summary>
    internal sealed class NameClaim
    {
        /// <summary>The type whose entry holds the name. The owner of the claim, not its text.</summary>
        public readonly INamedTypeSymbol Type;

        /// <summary>Whether the name was claimed as the entry's stable name or as a <c>[PreviousTypeName]</c> alias.</summary>
        public readonly bool IsStableName;

        /// <summary>
        /// <b>What a claim names</b>: a generic's definition, because a name that carries no arguments
        /// reaches the family and every closure of it answers for it (<c>SchemaRegistry.ClaimTargetOf</c>).
        /// Two claims are the same claim when this answers the same type for both, which is the one legal
        /// overlap the runtime allows.
        /// </summary>
        public readonly INamedTypeSymbol ClaimTarget;

        public NameClaim(INamedTypeSymbol type, bool isStableName)
        {
            Type = type;
            IsStableName = isStableName;
            ClaimTarget = ClaimTargetOf(type);
        }

        /// <summary>
        /// What a claim by <paramref name="type"/> names: a generic's definition, because a name that
        /// carries no arguments reaches the family and every closure of it answers for it
        /// (<c>SchemaRegistry.ClaimTargetOf</c>). Two claims are the same claim when this answers the same
        /// type for both — the one exception <c>SchemaRegistry.ClaimNames</c> documents.
        /// </summary>
        public static INamedTypeSymbol ClaimTargetOf(INamedTypeSymbol type)
            => type.IsGenericType ? type.OriginalDefinition : type;

        /// <summary>The role wording of <c>SchemaRegistry.NameRole</c>, so a compile error and a bake error read alike.</summary>
        public string Role => IsStableName ? "stable name" : "[PreviousTypeName] alias";
    }

    /// <summary>
    /// The manifest's name invariant, asked at compile time: <b>within one context, a name belongs to
    /// exactly one entry</b> (<c>Ceras.Format.SchemaRegistry.ClaimNames</c>).
    /// <para>
    /// The runtime enforces it when the manifest is registered, which is a save/load-time error: the
    /// context builds, the first <c>Save</c> throws <c>CerasSchemaException</c>, and the message names two
    /// CLR types the author then has to go and find. Every name in those claims is written in this
    /// compilation's own attributes, so the same two authors are known here — the check belongs before
    /// any code is generated, which is where a compile error is still about the declaration rather than
    /// about a save.
    /// </para>
    /// <para>
    /// <b>Which names this asks about.</b> Exactly the names the generated <c>UseRobustFormatters</c>
    /// hands to one context's registry, in the order it registers them, because that is what the runtime
    /// will be asked about:
    /// </para>
    /// <list type="number">
    /// <item>every type with a generated formatter, under the stable name its schema carries
    /// (<c>[CerasSerializable("…")]</c>, else the type's canonical name) and each of its
    /// <c>[PreviousTypeName]</c> aliases;</item>
    /// <item>every discovered interface/abstract type that gets the field-less placeholder schema, under
    /// its canonical name;</item>
    /// <item>every <c>contextMinimalTypes</c> entry — a type a member references but discovery
    /// deliberately leaves out — under its canonical name;</item>
    /// <item>every declared structural root (<c>DeclareRoot</c>), under its canonical name, with the
    /// aliases the runtime's derived enum descriptor reads.</item>
    /// </list>
    /// <para>
    /// That is deliberately a list of <em>registration paths</em> rather than a filter over the discovered
    /// types. The previous check asked "is this type a well-known type, a collection, or open?" and skipped
    /// it, which both missed the types that are registered under those very predicates and had to guess
    /// at the names they claim. Asking what the manifest registers has no exemption to get wrong: a type
    /// the manifest does not register claims nothing, so it is not in the model, and one it does is in it
    /// whatever its <c>SpecialType</c>, whatever namespace it lives in, and whether or not it is a
    /// collection. A <c>SpecialType</c> type in particular is registered by none of these paths, so it
    /// claims nothing here either — that is now a fact about the emitted manifest rather than a skipped
    /// case, and a user type that merely lives in a <c>System.*</c> namespace is registered under its own
    /// canonical name and is checked like any other.
    /// </para>
    /// <para>
    /// <b>What the model does not contain, stated rather than papered over.</b> A context's manifest also
    /// grows entries the bake registers while it walks members: <c>SchemaSection.ResolveId</c> registers
    /// the structural shape a member reaches (<c>List&lt;int&gt;</c>, <c>int[]</c>, an enum nothing
    /// declares, a <c>KeyValuePair</c>) under the canonical name it derives from the CLR type. Those names
    /// are not in the model, because they are reachable only through a member's type and not through a
    /// declared root. A name colliding with one of them is not a second claimant in a manifest either — it
    /// is the runtime's <em>own</em> name rule instead, <c>SchemaRegistry.RequireOwnDerivedName</c>, which
    /// recognizes the <em>form</em> of a derived name and refuses it without needing a manifest entry at
    /// all. So this check is a strict subset of what the runtime refuses, and the two rules together
    /// cover what the check does not.
    /// </para>
    /// <para>
    /// The whole thing is linear in the number of claims: one dictionary per context keyed by the claimed
    /// name, and each type's canonical name and aliases computed once for the whole build rather than once
    /// per context that claims it. Measured against a build of a sizeable synthetic graph (8 contexts,
    /// ~1400 types, ~1500 claims) by disabling this call, the generator's total time was unchanged within
    /// run-to-run noise at every size tried, so the check is not a new cost worth trading anything for.
    /// </para>
    /// </summary>
    internal static class ManifestNames
    {
        /// <summary>
        /// Reports <c>CERAS019</c> for every name two entries of one context claim, on the later claim —
        /// which is the one the runtime refuses, since the earlier one is already in the index. A conflict
        /// is reported in every context that has one, because each of those contexts is separately refused
        /// at registration.
        /// </summary>
        public static void ReportDuplicateClaims(
            SourceProductionContext context,
            IReadOnlyDictionary<string, List<INamedTypeSymbol>> contextTypes,
            IReadOnlyDictionary<string, List<INamedTypeSymbol>> contextMinimalTypes,
            IReadOnlyDictionary<string, List<INamedTypeSymbol>> declaredStructuralRoots,
            HashSet<INamedTypeSymbol> generatedFormatterTypes,
            AttributeBag attrBag)
        {
            var model = new ClaimModel(attrBag);

            foreach (string contextName in OrderedContextNames(contextTypes, contextMinimalTypes, declaredStructuralRoots))
            {
                // One name index per context, exactly as each context has its own registry: a name two
                // contexts both use is two different manifests, and the runtime never sees them together,
                // so a conflict has to be decided inside one of them.
                var owners = new Dictionary<string, NameClaim>(StringComparer.Ordinal);

                // Register is idempotent per type, so a type two registration paths both name registers
                // once and claims its names once. Mirrored so the model cannot report a type against
                // itself if those paths ever overlap.
                var registered = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

                if (contextTypes.TryGetValue(contextName, out var discovered))
                {
                    // The order the manifest registers in, which is also what makes the assigned type ids
                    // deterministic — and so the order the two claimants are reported in.
                    foreach (var type in discovered.OrderBy(model.CanonicalName, StringComparer.Ordinal))
                    {
                        if (generatedFormatterTypes.Contains(type))
                        {
                            if (!Enter(type)) continue;
                            Claim(model.StableName(type), type, isStableName: true, location: null);
                            foreach (var alias in model.PreviousTypeNames(type))
                                Claim(alias.Name, type, isStableName: false, location: alias.Location);
                        }
                        else if (EmitsMinimalPlaceholderSchema(type, attrBag))
                        {
                            // The placeholder carries no aliases: the emitted registration passes
                            // Array.Empty<string>(), so there is no alias to collide with.
                            if (!Enter(type)) continue;
                            Claim(model.CanonicalName(type), type, isStableName: true, location: null);
                        }
                    }
                }

                if (contextMinimalTypes.TryGetValue(contextName, out var minimalTypes))
                {
                    foreach (var type in minimalTypes)
                    {
                        if (!Enter(type)) continue;
                        Claim(model.CanonicalName(type), type, isStableName: true, location: null);
                    }
                }

                if (declaredStructuralRoots.TryGetValue(contextName, out var structuralRoots))
                {
                    foreach (var type in StructuralRootRegistrations(structuralRoots, generatedFormatterTypes))
                    {
                        if (!Enter(type)) continue;

                        // DeclareRoot derives the descriptor from the CLR type, and only its enum branch
                        // reads [PreviousTypeName]: every other shape gets Array.Empty<string>().
                        Claim(model.CanonicalName(type), type, isStableName: true, location: null);
                        if (type.TypeKind == TypeKind.Enum)
                            foreach (var alias in model.PreviousTypeNames(type))
                                Claim(alias.Name, type, isStableName: false, location: alias.Location);
                    }
                }

                bool Enter(INamedTypeSymbol type) => registered.Add(type);

                void Claim(string name, INamedTypeSymbol type, bool isStableName, Location? location)
                {
                    // An empty alias names nothing — the runtime skips it rather than indexing it, and
                    // making it collide with the next entry's empty alias would be a rule of its own. An
                    // empty stable name cannot reach here: GetStableTypeName falls back to the canonical
                    // name, which is never empty.
                    if (string.IsNullOrEmpty(name)) return;

                    // First claim wins, exactly as the runtime's TryAdd does, so the owner a message names
                    // is the entry the conflict would really be found against.
                    if (!owners.TryGetValue(name, out var owner))
                    {
                        owners[name] = new NameClaim(type, isStableName);
                        return;
                    }

                    // The one claim that is not a conflict: the same entry, or another closure of the same
                    // family sharing one open alias — see SchemaRegistry.ClaimNames' own exception.
                    if (SymbolEqualityComparer.Default.Equals(owner.ClaimTarget, NameClaim.ClaimTargetOf(type)))
                        return;

                    context.ReportDiagnostic(Diagnostic.Create(
                        Diag.DuplicateManifestName,
                        location ?? type.Locations.FirstOrDefault() ?? Location.None,
                        name,
                        contextName,
                        Display(owner.Type),
                        owner.Role,
                        Display(type),
                        isStableName ? "stable name" : "[PreviousTypeName] alias"));
                }
            }
        }

        /// <summary>
        /// The discovered types a context's manifest registers with the field-less placeholder schema: an
        /// interface or abstract class no formatter can be generated for. Shared with the emitter so the
        /// check can never disagree with what is written out about it.
        /// </summary>
        public static bool EmitsMinimalPlaceholderSchema(INamedTypeSymbol type, AttributeBag attrBag)
        {
            if (type.TypeKind != TypeKind.Interface && !type.IsAbstract) return false;
            if (type.IsSealed) return false;                          // static classes have no instances
            if (IsWellKnownType(type, attrBag)) return false;        // a built-in shape owns its own entry
            if (type.TypeParameters.Length > 0) return false;        // open generics: closed ones are handled at runtime
            return true;
        }

        /// <summary>
        /// The declared structural roots one context registers, in canonical-name order (so the assigned
        /// ids do not depend on the order the declarations arrived in) and without the ones a generated
        /// formatter already registers. Shared with the emitter for the same reason as
        /// <see cref="EmitsMinimalPlaceholderSchema"/>.
        /// </summary>
        public static List<INamedTypeSymbol> StructuralRootRegistrations(
            List<INamedTypeSymbol> declaredStructuralRoots,
            HashSet<INamedTypeSymbol> generatedFormatterTypes)
            => declaredStructuralRoots
                .Where(t => !generatedFormatterTypes.Contains(t))
                .GroupBy(t => t, SymbolEqualityComparer.Default)
                .Select(g => g.First())
                .OrderBy(t => GetRobustName(t), StringComparer.Ordinal)
                .ToList();

        static string Display(INamedTypeSymbol type)
            => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        /// <summary>
        /// Every context name, in ordinal order, so the diagnostics of one build come out in a stable order
        /// rather than in dictionary order.
        /// </summary>
        static IEnumerable<string> OrderedContextNames(
            IReadOnlyDictionary<string, List<INamedTypeSymbol>> contextTypes,
            IReadOnlyDictionary<string, List<INamedTypeSymbol>> contextMinimalTypes,
            IReadOnlyDictionary<string, List<INamedTypeSymbol>> declaredStructuralRoots)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var name in contextTypes.Keys) names.Add(name);
            foreach (var name in contextMinimalTypes.Keys) names.Add(name);
            foreach (var name in declaredStructuralRoots.Keys) names.Add(name);
            return names;
        }

        /// <summary>
        /// A type's canonical name and its rename aliases, computed once for the whole build. Both are
        /// pure functions of the symbol, and a type can be claimed by several contexts, so asking the
        /// symbol again per context would make the check quadratic in the number of contexts.
        /// </summary>
        sealed class ClaimModel
        {
            readonly AttributeBag _attrBag;
            readonly Dictionary<INamedTypeSymbol, string> _canonical = new(SymbolEqualityComparer.Default);
            readonly Dictionary<INamedTypeSymbol, string> _stable = new(SymbolEqualityComparer.Default);
            readonly Dictionary<INamedTypeSymbol, PreviousName[]> _aliases = new(SymbolEqualityComparer.Default);

            public ClaimModel(AttributeBag attrBag) => _attrBag = attrBag;

            /// <summary>
            /// The name Ceras writes for the type itself, which is what every registration path that takes
            /// no name from the author records — and which must match
            /// <c>TypeIdentity.GetCanonicalName</c> exactly (see CanonicalIdentityTests).
            /// </summary>
            public string CanonicalName(INamedTypeSymbol type)
            {
                if (!_canonical.TryGetValue(type, out var name))
                    _canonical[type] = name = GetRobustName(type);
                return name;
            }

            /// <summary>
            /// The stable name the type's generated schema carries: the author's
            /// <c>[CerasSerializable("…")]</c> if there is one, else the canonical name. Asked of
            /// <see cref="RobustCodeEmitter.GetStableTypeName"/> rather than restated, so the name this
            /// checks is the name the emitter writes.
            /// </summary>
            public string StableName(INamedTypeSymbol type)
            {
                if (!_stable.TryGetValue(type, out var name))
                    _stable[type] = name = RobustCodeEmitter.GetStableTypeName(type, _attrBag.CerasSerializable);
                return name;
            }

            /// <summary>
            /// The <c>[PreviousTypeName]</c> aliases on the type, read the way
            /// <c>RobustCodeEmitter.BuildSchemaExpression</c> reads them (the type's own attributes, in
            /// source order) and with each attribute's own location, so a conflict on an alias is reported
            /// where the alias was written rather than on the type.
            /// </summary>
            public PreviousName[] PreviousTypeNames(INamedTypeSymbol type)
            {
                if (_aliases.TryGetValue(type, out var cached)) return cached;

                var found = new List<PreviousName>();
                if (_attrBag.PreviousTypeName != null)
                {
                    foreach (var attr in type.GetAttributes())
                    {
                        if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, _attrBag.PreviousTypeName)) continue;
                        if (attr.ConstructorArguments.Length == 0) continue;
                        if (!(attr.ConstructorArguments[0].Value is string alias)) continue;

                        found.Add(new PreviousName(
                            alias,
                            attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? type.Locations.FirstOrDefault() ?? Location.None));
                    }
                }

                _aliases[type] = found.ToArray();
                return _aliases[type];
            }
        }

        /// <summary>A <c>[PreviousTypeName]</c> alias, and where it was written.</summary>
        public readonly struct PreviousName
        {
            public readonly string Name;
            public readonly Location Location;

            public PreviousName(string name, Location location)
            {
                Name = name;
                Location = location;
            }
        }
    }
}
