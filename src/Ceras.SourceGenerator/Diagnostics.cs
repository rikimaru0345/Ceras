using Microsoft.CodeAnalysis;

namespace Ceras.SourceGenerator
{
    internal static class Diag
    {
        public static readonly DiagnosticDescriptor NoSerializableMembers = new(
            id: "CERAS001",
            title: "No serializable members found",
            messageFormat: "Type '{0}' has [CerasSerializable] but no serializable members were found. Check [Include]/[Exclude] attributes and TargetMembers config",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor NotConstructable = new(
            id: "CERAS003",
            title: "Type not constructable by Ceras",
            messageFormat: "Type '{0}' is included in serialization but Ceras cannot construct it: there is no parameterless constructor, no constructor with parameters that can be fully mapped to serializable members, and no constructor marked [CerasConstructor]. Add a parameterless constructor, a constructor with mappable parameters, or mark one constructor with [CerasConstructor]",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor CerasConstructorMultiple = new(
            id: "CERAS004",
            title: "Multiple [CerasConstructor] attributes",
            messageFormat: "Type '{0}' has multiple constructors marked with [CerasConstructor]. Only one is allowed",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor CerasConstructorNotAccessible = new(
            id: "CERAS005",
            title: "[CerasConstructor] not accessible",
            messageFormat: "The constructor marked with [CerasConstructor] on type '{0}' is not accessible (Internal Error: this diagnostic should no longer be emitted as UnsafeAccessor is used)",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor CerasConstructorParamMismatch = new(
            id: "CERAS006",
            title: "[CerasConstructor] parameter mismatch",
            messageFormat: "The parameter '{0}' in the [CerasConstructor] of type '{1}' does not match any serializable member (by name and type)",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor CerasConstructorParamNotProvided = new(
            id: "CERAS007",
            title: "[CerasConstructor] parameter not serializable",
            messageFormat: "The parameter '{0}' in the [CerasConstructor] of type '{1}' matches a member that is excluded from serialization",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor IncludeExcludeConflict = new(
            id: "CERAS008",
            title: "Conflicting [Include] and [Exclude] attributes",
            messageFormat: "Member '{0}' in type '{1}' has both [Include] and [Exclude] attributes",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateWireName = new(
            id: "CERAS009",
            title: "Duplicate stable wire name",
            messageFormat: "Type '{0}' has multiple members that would be serialized with the name '{1}' ({2}). Use [CerasStableName] or [CerasExclude] on one of them to resolve the conflict",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PartialRequired = new(
            id: "CERAS010",
            title: "Partial type required for private members",
            messageFormat: "Type '{0}' contains private or protected members ({1}) that are included in serialization. The type must be declared as 'partial' to allow the Ceras source generator to inject access helpers",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ReadonlyMemberMustBeInCtor = new(
            id: "CERAS011",
            title: "Readonly member not in [CerasConstructor]",
            messageFormat: "Member '{0}' in type '{1}' is readonly (or a getter-only property) but is not provided by a [CerasConstructor]",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MemberConfigConflict = new(
            id: "CERAS012",
            title: "Conflicting MemberConfig attributes",
            messageFormat: "Conflicting MemberConfig settings inherited from '{0}'. This setting must match the one from '{1}' at {2}",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PrivateMemberFromAnotherAssembly = new(
            id: "CERAS013",
            title: "Cannot serialize private member from another assembly",
            messageFormat: "Member '{0}' on type '{1}' is private or protected and is defined in another assembly. The Ceras source generator cannot inject access helpers into another assembly. Make the member internal (and use [InternalsVisibleTo(\"ProjectB\")]) or public",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DiscoveredTypePropagation = new(
            id: "CERAS014",
            title: "Discovered type via propagation",
            messageFormat: "{0}",
            category: "Ceras.Info",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PolymorphicFieldTypeNotSerializable = new(
            id: "CERAS015",
            title: "Polymorphic field type not in serialization graph",
            messageFormat: "Member '{0}' in '{1}' has polymorphic type '{2}' (interface/abstract) not marked [CerasSerializable]. Concrete subtypes won't have formatters, causing a runtime crash. Add [CerasSerializable] to '{2}' so subtypes are auto-discovered, or add [Exclude] to the member",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PolymorphicFieldTypeImplicit = new(
            id: "CERAS016",
            title: "Polymorphic field type not explicitly marked [CerasSerializable]",
            messageFormat: "Member '{0}' in '{1}' uses polymorphic type '{2}' (interface/abstract) which is not directly marked [CerasSerializable]. Subtypes are auto-discovered via member propagation, but this is implicit and fragile. Add [CerasSerializable] to '{2}' to make the intent clear",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor EngineObjectNeedsCustomFormatter = new(
            id: "CERAS017",
            title: "Engine object type requires a custom formatter",
            messageFormat: "Member '{0}' in '{1}' has type '{2}' which inherits from a native engine object base (e.g. Godot.GodotObject). Ceras cannot serialize it with its default formatters. Register a custom IFormatter<{2}> before creating the CerasSerializer, or add [Exclude] to skip this member",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedAccessShape = new(
            id: "CERAS018",
            title: "Unsupported access or construction shape",
            messageFormat: "Cannot generate access for '{0}': {1}. Mark the member [Exclude], make the type/member accessible, or provide a custom formatter",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateStableTypeName = new(
            id: "CERAS019",
            title: "Duplicate stable type name",
            messageFormat: "Types '{0}' and '{1}' both use the stable type name '{2}'. Give them distinct stable names with [CerasStableName] or [CerasSerializable(\"name\")]",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor FormatterFieldNameCollision = new(
            id: "CERAS020",
            title: "Generated formatter field name collision",
            messageFormat: "Type '{0}' has members '{1}' (type '{2}') and '{3}' (type '{4}') whose generated formatter fields both map to '{5}'. Two distinct types are collapsing to the same identifier; rename one of them (change its namespace, nesting, or spelling) or exclude one member",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MemberTypeWithoutGeneratedFormatter = new(
            id: "CERAS021",
            title: "Member type has no generated formatter",
            messageFormat: "Member '{0}' in type '{1}' references type '{2}', but no formatter will be generated for it ({3}). Fix that type's construction or accessibility, provide a custom IFormatter<{2}>, or mark the member [Exclude]",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor GeneratedNameCollision = new(
            id: "CERAS022",
            title: "Generated formatter name collision",
            messageFormat: "Types '{0}' and '{1}' both produce the generated name '{2}', which is used for the formatter class, the generated-source hint, and/or the registration local. Rename one type (change its namespace, nesting, or spelling)",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvalidContextName = new(
            id: "CERAS023",
            title: "Invalid [CerasContext] name",
            messageFormat: "Type '{0}' has a [CerasContext] attribute with a null/empty/whitespace name. A serialization context needs a non-empty, case-sensitive name",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnknownContext = new(
            id: "CERAS024",
            title: "Unknown serialization context",
            messageFormat: "Assembly-level declaration for type '{0}' names the serialization context '{1}', but no type in this compilation declares it with [CerasContext(\"{1}\")]. Declared contexts: {2}",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor NoSerializationContext = new(
            id: "CERAS025",
            title: "No serialization context declared",
            messageFormat: "No Ceras serialization context is declared. Ceras has no default context: annotate at least one root type with [CerasContext(\"Name\")] (and optionally [CerasSerializable]) so the generator can emit a manifest, then call UseGeneratedFormatters(\"Name\").",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedImmutableCycleConstruction = new(
            id: "CERAS026",
            title: "Unsupported constructor-dependent immutable cycle",
            messageFormat: "Member '{0}' in type '{1}' is immutable (readonly or getter-only) and participates in a reference cycle with type '{2}', but the selected constructor ({3}) does not initialize it and there is no parameterless construction path. Ceras cannot construct this immutable cycle. Supply an explicit construction strategy: mark a constructor [CerasConstructor] that takes the cycled members as parameters, add an accessible parameterless constructor, or supply a custom IFormatter<{1}>.",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedMultidimensionalArray = new(
            id: "CERAS027",
            title: "Unsupported multidimensional array",
            messageFormat: "Member '{0}' in type '{1}' has multidimensional array type '{2}' (rank {3}). The robust format currently describes only one-dimensional arrays. Use a jagged array (T[][]) or flatten it into a one-dimensional array plus explicit dimension fields, or mark the member [Exclude]",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedCallbackSignature = new(
            id: "CERAS028",
            title: "Unsupported callback signature",
            messageFormat: "[{0}] method '{1}' on type '{2}' is declared as '{3}', but Ceras cannot invoke it: {4}. Ceras would silently not call it. {5}",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedFrameworkMemberType = new(
            id: "CERAS029",
            title: "Member type has no built-in representation in the robust format",
            messageFormat: "Member '{0}' in '{1}' has type '{2}', which the robust format has no built-in representation for. '{3}' is not declared in this compilation and carries no [CerasSerializable] attribute, so the generator treats it as a framework type from a System.* namespace and can give it neither a formatter nor a schema. No declaration ([CerasSerializable]/[CerasContext]/[CerasKnownType<...>]) and no custom IFormatter<T> can make it serializable in a robust context. If it is your own type from another assembly, mark it [CerasSerializable]; otherwise store a serializable form of the value instead (a string, a byte[], or another type the format describes), mark the member [Exclude], or keep the type out of every [CerasContext] if you only use the reflection-based CerasSerializer",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedFrameworkTypeDeclaration = new(
            id: "CERAS030",
            title: "Declaration names a type the robust format cannot describe",
            messageFormat: "The assembly-level declaration names '{0}', which the robust format has no built-in representation for. '{1}' is not declared in this compilation and carries no [CerasSerializable] attribute, so the generator treats it as a framework type from a System.* namespace; declaring it cannot give it a schema and no custom IFormatter<T> can write it in a robust context. If it is your own type from another assembly, mark it [CerasSerializable] and declare that instead; otherwise declare a root shape the format does describe (a one-dimensional array, a built-in collection, KeyValuePair, a tuple or an enum), or remove the declaration",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor BannedMemberType = new(
            id: "CERAS031",
            title: "Member has a banned type",
            messageFormat: "Member '{0}' in '{1}' has the type '{2}', which Ceras cannot serialize. Leaving it out of the payload would save fine and load it back as null, so this is an error. Add [Exclude] to the member if it is intentionally not serialized, or replace it with a representation Ceras can write (for a delegate: store the target and the method identity yourself and re-attach it in an [OnAfterDeserialize] callback). Note that a custom IFormatter for '{2}' does not help: the member never reaches a formatter, and Ceras cannot tell whether an arbitrary formatter would restore the value",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        // CERAS029/CERAS030 are the System-namespace and framework-shape diagnostics and CERAS031 is
        // the banned-member error, so this sits above them.
        public static readonly DiagnosticDescriptor MemberOrderNotWritten = new(
            id: "CERAS032",
            title: "Member order cannot be matched to the emitted writer",
            messageFormat: "{0}. Every closure of the generic definition '{1}' is written by one shared formatter, in the order that definition's members sort in, so a closed schema may only list its fields in that order. Ceras could not place {2} - and a schema whose fields are not in the order they are written describes a layout that does not exist, so a save would be read back with the wrong members. This is a Ceras bug: please report it with the model that triggers it",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}
