using Microsoft.CodeAnalysis;

namespace Ceras.SourceGenerator
{
    internal static class Diag
    {
        public static readonly DiagnosticDescriptor NoSerializableMembers = new(
            id: "CERAS001",
            title: "No serializable members found",
            messageFormat: "Type '{0}' has [CerasSerializable] but no serializable members were found. Check [Include]/[Exclude] attributes and TargetMembers config.",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InterfaceOrAbstractWithoutDiscoverSubtypes = new(
            id: "CERAS002",
            title: "Interface or abstract class without DiscoverSubtypes",
            messageFormat: "The {1} '{0}' is marked with [CerasSerializable] but 'DiscoverSubtypes' is set to false. Since interfaces/abstract classes cannot be instantiated directly, this attribute has no effect.",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor NotConstructable = new(
            id: "CERAS003",
            title: "Type not constructable by Ceras",
            messageFormat: "Type '{0}' is included in serialization but Ceras cannot construct it: there is no accessible (public or internal) parameterless constructor and no constructor marked [CerasConstructor]. Add a parameterless constructor or mark one constructor with [CerasConstructor].",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor CerasConstructorMultiple = new(
            id: "CERAS004",
            title: "Multiple [CerasConstructor] attributes",
            messageFormat: "Type '{0}' has multiple constructors marked with [CerasConstructor]. Only one is allowed.",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor CerasConstructorNotAccessible = new(
            id: "CERAS005",
            title: "[CerasConstructor] not accessible",
            messageFormat: "The constructor marked with [CerasConstructor] on type '{0}' must be public or internal.",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor CerasConstructorParamMismatch = new(
            id: "CERAS006",
            title: "[CerasConstructor] parameter mismatch",
            messageFormat: "The parameter '{0}' in the [CerasConstructor] of type '{1}' does not match any serializable member (by name and type).",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor CerasConstructorParamNotProvided = new(
            id: "CERAS007",
            title: "[CerasConstructor] parameter not serializable",
            messageFormat: "The parameter '{0}' in the [CerasConstructor] of type '{1}' matches a member that is excluded from serialization.",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor IncludeExcludeConflict = new(
            id: "CERAS008",
            title: "Conflicting [Include] and [Exclude] attributes",
            messageFormat: "Member '{0}' in type '{1}' has both [Include] and [Exclude] attributes.",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateWireName = new(
            id: "CERAS009",
            title: "Duplicate stable wire name",
            messageFormat: "Type '{0}' has multiple members that would be serialized with the name '{1}'. Use [CerasStableName] to resolve the conflict.",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PartialRequired = new(
            id: "CERAS010",
            title: "Partial class required for private members",
            messageFormat: "Type '{0}' contains private or protected members ({1}) that are included in serialization. The class must be declared as 'partial' to allow the Ceras source generator to inject access helpers.",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ReadonlyMemberMustBeInCtor = new(
            id: "CERAS011",
            title: "Readonly member not in [CerasConstructor]",
            messageFormat: "Member '{0}' in type '{1}' is readonly (or a getter-only property) but is not provided by a [CerasConstructor].",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MemberConfigConflict = new(
            id: "CERAS012",
            title: "Conflicting MemberConfig attributes",
            messageFormat: "Conflicting MemberConfig settings inherited from '{0}'. This setting must match the one from '{1}' at {2}.",
            category: "Ceras.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PrivateMemberFromAnotherAssembly = new(
            id: "CERAS013",
            title: "Cannot serialize private member from another assembly",
            messageFormat: "Member '{0}' on type '{1}' is private or protected and is defined in another assembly. " +
                           "The Ceras source generator cannot inject access helpers into another assembly. " +
                           "Make the member internal (and use [InternalsVisibleTo(\"ProjectB\")]) or public.",
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
    }
}
