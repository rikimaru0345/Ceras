using System;
using Microsoft.CodeAnalysis;

namespace Ceras.SourceGenerator
{
    internal sealed class AttributeBag
    {
        public readonly INamedTypeSymbol? Include;
        public readonly INamedTypeSymbol? Exclude;
        public readonly INamedTypeSymbol? PreviousName;
        public readonly INamedTypeSymbol? PreviousTypeName;
        public readonly INamedTypeSymbol? SerializedAs;
        public readonly INamedTypeSymbol? CerasSerializable;
        public readonly INamedTypeSymbol? CerasConstructor;
        public readonly INamedTypeSymbol? OnBeforeSerialize;
        public readonly INamedTypeSymbol? OnAfterSerialize;
        public readonly INamedTypeSymbol? OnBeforeDeserialize;
        public readonly INamedTypeSymbol? OnAfterDeserialize;
        public readonly INamedTypeSymbol? CerasSerializerType;
        public readonly INamedTypeSymbol? MigrationContextType;

        public AttributeBag(
            INamedTypeSymbol? include, INamedTypeSymbol? exclude, 
            INamedTypeSymbol? previousName, INamedTypeSymbol? previousTypeName, INamedTypeSymbol? serializedAs,
            INamedTypeSymbol? cerasSerializable,
            INamedTypeSymbol? cerasConstructor,
            INamedTypeSymbol? onBeforeDeserialize = null,
            INamedTypeSymbol? onAfterDeserialize = null,
            INamedTypeSymbol? cerasSerializerType = null,
            INamedTypeSymbol? migrationContextType = null,
            INamedTypeSymbol? onBeforeSerialize = null,
            INamedTypeSymbol? onAfterSerialize = null)
        {
            Include = include;
            Exclude = exclude;
            PreviousName = previousName;
            PreviousTypeName = previousTypeName;
            SerializedAs = serializedAs;
            CerasSerializable = cerasSerializable;
            CerasConstructor = cerasConstructor;
            OnBeforeDeserialize = onBeforeDeserialize;
            OnAfterDeserialize = onAfterDeserialize;
            CerasSerializerType = cerasSerializerType;
            MigrationContextType = migrationContextType;
            OnBeforeSerialize = onBeforeSerialize;
            OnAfterSerialize = onAfterSerialize;
        }
    }

    /// <summary>
    /// Which half of the round trip a callback belongs to. Serialize callbacks run around the writer's
    /// member writes, deserialize callbacks around the reader's member reads, and the two halves accept
    /// different arguments (there is no migration while saving).
    /// </summary>
    internal enum CallbackKind
    {
        Serialize = 0,
        Deserialize = 1,
    }

    /// <summary>The supported parameter shape of a callback.</summary>
    internal enum CallbackParameterKind
    {
        /// <summary><c>void M()</c></summary>
        None = 0,
        /// <summary><c>void M(CerasSerializer)</c> — access to the serializer / user context.</summary>
        Serializer = 1,
        /// <summary><c>void M(MigrationContext)</c> — describes what the migration skipped/added.</summary>
        MigrationContext = 2,
    }

    /// <summary>
    /// A discovered <c>[OnBeforeSerialize]</c> / <c>[OnAfterSerialize]</c> / <c>[OnBeforeDeserialize]</c> /
    /// <c>[OnAfterDeserialize]</c> method together with the argument shape the generated writer/reader
    /// must pass it.
    /// </summary>
    internal sealed class CallbackInfo
    {
        public readonly IMethodSymbol Method;
        public readonly CallbackKind Kind;
        public readonly bool IsBefore;
        public readonly CallbackParameterKind ParameterKind;

        public CallbackInfo(IMethodSymbol method, CallbackKind kind, bool isBefore, CallbackParameterKind parameterKind)
        {
            Method = method;
            Kind = kind;
            IsBefore = isBefore;
            ParameterKind = parameterKind;
        }

        public bool HasArgument => ParameterKind != CallbackParameterKind.None;

        /// <summary>
        /// The fully-qualified parameter type when the callback takes one, or null when it is
        /// parameterless. <see cref="HasArgument"/> is the guard: a parameterless callback is the only
        /// shape with no parameter to name, and the two agree by construction — <c>None</c> is only ever
        /// chosen for arity zero, and the two argument-taking shapes only for arity one.
        /// </summary>
        public string? ArgumentTypeFqn => Method.Parameters.Length == 1
            ? Method.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;
    }

    [Flags]
    internal enum AccessHelperRequirement
    {
        None = 0,
        Read = 1 << 0,
        Write = 1 << 1,
        Both = Read | Write
    }

    internal sealed class SerializedMember
    {
        // ... (existing properties)
        public string Name { get; } 
        public string StableName { get; }
        public ITypeSymbol Type { get; }
        public INamedTypeSymbol DeclaringType { get; }
        public bool IsField { get; }
        public string[] PreviousNames { get; }
        public bool IsMonomorphic { get; }
        public bool IsAccessible { get; }
        public bool IsReadAccessible { get; }
        public bool IsWriteAccessible { get; }
        public bool IsReadOnly { get; }
        /// <summary>
        /// The constructed member symbol (field/property) this member was discovered from.
        /// Needed to obtain the unsubstituted (open) member type when generating generic accessors.
        /// </summary>
        public ISymbol? MemberSymbol { get; }

        public SerializedMember(
            string name, string stableName, ITypeSymbol type, INamedTypeSymbol declaringType, bool isField,
            string[] previousNames, bool isMonomorphic, bool isAccessible,
            bool isReadOnly,
            bool? isReadAccessible = null, bool? isWriteAccessible = null, ISymbol? memberSymbol = null)
        {
            Name = name;
            StableName = stableName;
            Type = type;
            DeclaringType = declaringType;
            IsField = isField;
            PreviousNames = previousNames;
            IsMonomorphic = isMonomorphic;
            IsAccessible = isAccessible;
            IsReadAccessible = isReadAccessible ?? isAccessible;
            IsWriteAccessible = isWriteAccessible ?? isAccessible;
            IsReadOnly = isReadOnly;
            MemberSymbol = memberSymbol;
        }
    }

    internal sealed class TypeDiscoveryResult
    {
        public INamedTypeSymbol Type { get; }
        public string? DiscoveryPath { get; }

        public TypeDiscoveryResult(INamedTypeSymbol type, string? discoveryPath)
        {
            Type = type;
            DiscoveryPath = discoveryPath;
        }
    }
}
