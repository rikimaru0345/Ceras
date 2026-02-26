using System;
using Microsoft.CodeAnalysis;

namespace Ceras.SourceGenerator
{
    internal sealed class AttributeBag
    {
        public readonly INamedTypeSymbol? Include;
        public readonly INamedTypeSymbol? Exclude;
        public readonly INamedTypeSymbol? MemberConfig;
        public readonly INamedTypeSymbol? PreviousName;
        public readonly INamedTypeSymbol? PreviousTypeName;
        public readonly INamedTypeSymbol? SerializedAs;
        public readonly INamedTypeSymbol? Monomorphic;
        public readonly INamedTypeSymbol? Polymorphic;
        public readonly INamedTypeSymbol? CerasSerializable;
        public readonly INamedTypeSymbol? CerasConstructor;

        public AttributeBag(
            INamedTypeSymbol? include, INamedTypeSymbol? exclude, INamedTypeSymbol? memberConfig,
            INamedTypeSymbol? previousName, INamedTypeSymbol? previousType, INamedTypeSymbol? serializedAs,
            INamedTypeSymbol? monomorphic, INamedTypeSymbol? polymorphic, INamedTypeSymbol? cerasSerializable,
            INamedTypeSymbol? cerasConstructor)
        {
            Include = include;
            Exclude = exclude;
            MemberConfig = memberConfig;
            PreviousName = previousName;
            PreviousTypeName = previousType;
            SerializedAs = serializedAs;
            Monomorphic = monomorphic;
            Polymorphic = polymorphic;
            CerasSerializable = cerasSerializable;
            CerasConstructor = cerasConstructor;
        }
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
        public bool IsGetterOnly { get; }
        public bool IsCtorParam { get; }
        public string? CtorParamName { get; }
        public bool IsAccessible { get; }
        public bool IsReadAccessible { get; }
        public bool IsWriteAccessible { get; }
        public bool IsReadOnly { get; }

        public SerializedMember(
            string name, string stableName, ITypeSymbol type, INamedTypeSymbol declaringType, bool isField,
            string[] previousNames, bool isMonomorphic, bool isAccessible,
            bool isReadOnly, bool isGetterOnly, bool isCtorParam = false, string? ctorParamName = null,
            bool? isReadAccessible = null, bool? isWriteAccessible = null)
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
            IsGetterOnly = isGetterOnly;
            IsCtorParam = isCtorParam;
            CtorParamName = ctorParamName;
        }
    }

    internal sealed class TypeDiscoveryResult
    {
        public INamedTypeSymbol Type { get; }
        public bool IsVersionTolerant { get; }
        public string? DiscoveryPath { get; }

        public TypeDiscoveryResult(INamedTypeSymbol type, bool isVersionTolerant, string? discoveryPath)
        {
            Type = type;
            IsVersionTolerant = isVersionTolerant;
            DiscoveryPath = discoveryPath;
        }
    }
}
