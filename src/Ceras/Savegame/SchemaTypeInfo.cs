using System;

namespace Ceras.Savegame
{
    public enum SchemaTypeKind : byte
    {
        Enum                 = 0,
        Struct               = 1,
        Class                = 2,
        GenericInstantiation = 3,
    }

    /// <summary>
    /// Compile-time description of a single serializable type, emitted by the source generator
    /// for every <c>[CerasSerializable]</c> type and registered with
    /// <see cref="SchemaRegistry"/> at startup.
    /// </summary>
    public sealed class SchemaTypeInfo
    {
        /// <summary>Stable wire name (from <c>[CerasSerializable("Name")]</c> or simple class name).</summary>
        public string StableName { get; }

        /// <summary>Old wire names from <c>[PreviousTypeName]</c>. May be empty.</summary>
        public string[] PreviousTypeNames { get; }

        public SchemaTypeKind Kind { get; }

        /// <summary>Fields in SchemaMemberComparer order. Null / empty for enums.</summary>
        public SchemaFieldInfo[] Fields { get; }

        /// <summary>Enum name→value pairs. Null / empty for structs and classes.</summary>
        public SchemaEnumValueInfo[] EnumValues { get; }

        /// <summary>
        /// For enums: the CLR full name of the underlying integer type (e.g. "System.Int32").
        /// For generic instantiations: the CLR full name of the open generic definition (e.g. "System.Collections.Generic.List`1").
        /// Empty otherwise.
        /// </summary>
        public string UnderlyingTypeName { get; }

        /// <summary>Type-ID arguments for generic instantiations. Null / empty otherwise.</summary>
        public string[] ArgTypeNames { get; }

        /// <summary>
        /// Exact byte size of one instance in the data section, or 0 if variable.
        /// A type is fixed-size iff all fields (recursively) are primitives, enums,
        /// fixed-size structs, or strings (which are always a 4-byte table index).
        /// Classes are always 0 (variable) due to the reference-marker overhead.
        /// </summary>
        public int FixedByteSize { get; }

        public SchemaTypeInfo(
            string                stableName,
            string[]              previousTypeNames,
            SchemaTypeKind        kind,
            SchemaFieldInfo[]     fields,
            int                   fixedByteSize,
            SchemaEnumValueInfo[] enumValues         = null,
            string                underlyingTypeName = null,
            string[]              argTypeNames       = null)
        {
            StableName         = stableName         ?? throw new ArgumentNullException(nameof(stableName));
            PreviousTypeNames  = previousTypeNames   ?? Array.Empty<string>();
            Kind               = kind;
            Fields             = fields              ?? Array.Empty<SchemaFieldInfo>();
            EnumValues         = enumValues          ?? Array.Empty<SchemaEnumValueInfo>();
            UnderlyingTypeName = underlyingTypeName  ?? string.Empty;
            ArgTypeNames       = argTypeNames        ?? Array.Empty<string>();
            FixedByteSize      = fixedByteSize;
        }
    }

    /// <summary>Describes a single serialized field within a <see cref="SchemaTypeInfo"/>.</summary>
    public sealed class SchemaFieldInfo
    {
        /// <summary>Stable wire name (from <c>[CerasStableName]</c> or C# member name).</summary>
        public string StableName { get; }

        /// <summary>Old wire names from <c>[PreviousName]</c>. May be empty.</summary>
        public string[] PreviousNames { get; }

        /// <summary>
        /// CLR full name of the field's type as written by the source generator
        /// (e.g. "System.Int32", "System.String", "MyGame.Inventory").
        /// For well-known types this matches <see cref="WellKnownTypes"/> constants.
        /// </summary>
        public string TypeName { get; }

        /// <summary>
        /// True when no per-element type ID is written for this field's elements.
        /// Auto-set for value-type and sealed-class element types; overridable via
        /// <c>[Monomorphic]</c> / <c>[Polymorphic]</c>.
        /// </summary>
        public bool IsMonomorphic { get; }

        public SchemaFieldInfo(
            string   stableName,
            string[] previousNames,
            string   typeName,
            bool     isMonomorphic)
        {
            StableName    = stableName    ?? throw new ArgumentNullException(nameof(stableName));
            PreviousNames = previousNames ?? Array.Empty<string>();
            TypeName      = typeName      ?? throw new ArgumentNullException(nameof(typeName));
            IsMonomorphic = isMonomorphic;
        }
    }

    /// <summary>One name→value entry from an enum definition.</summary>
    public sealed class SchemaEnumValueInfo
    {
        public string Name  { get; }
        public long   Value { get; }

        public SchemaEnumValueInfo(string name, long value)
        {
            Name  = name  ?? throw new ArgumentNullException(nameof(name));
            Value = value;
        }
    }
}
