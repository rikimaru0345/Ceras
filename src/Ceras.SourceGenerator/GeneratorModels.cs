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

        public AttributeBag(
            INamedTypeSymbol? include, INamedTypeSymbol? exclude, 
            INamedTypeSymbol? previousName, INamedTypeSymbol? previousTypeName, INamedTypeSymbol? serializedAs,
            INamedTypeSymbol? cerasSerializable,
            INamedTypeSymbol? cerasConstructor)
        {
            Include = include;
            Exclude = exclude;
            PreviousName = previousName;
            PreviousTypeName = previousTypeName;
            SerializedAs = serializedAs;
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
