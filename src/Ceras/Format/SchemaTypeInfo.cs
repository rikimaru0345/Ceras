using System;

namespace Ceras.Format
{
    public enum SchemaTypeKind : byte
    {
        Enum                 = 0,
        Struct               = 1,
        Class                = 2,
        GenericInstantiation = 3,

        /// <summary>
        /// A value whose bytes a <b>user custom formatter</b> owns: an opaque block, declared as a
        /// 4-byte length followed by exactly that many bytes.
        /// <para>
        /// An entry of this kind describes <b>no member list</b> — that is the whole point of it. The
        /// generated schema of a type the custom formatter overrides is not what the payload holds, so
        /// recording those members is exactly the lie this kind replaces: with them recorded, a
        /// migration reader would walk fields the bytes do not contain. The formatter's identity is
        /// recorded instead (<see cref="SchemaTypeInfo.OpaqueFormatterName"/>), because which formatter
        /// produced the bytes is part of the layout and belongs in the fingerprint.
        /// </para>
        /// <para>
        /// Written by <c>SchemaSection.Write</c>, read by <c>SchemaSection.Read</c>, skipped and read by
        /// the migration interpreter through <c>OpaquePayload</c> (the length) and the current build's
        /// formatter for the type (the bytes). See <c>Format/LAYOUT.md</c>, "A user custom formatter's
        /// payload is an opaque block".
        /// </para>
        /// </summary>
        Opaque               = 4,
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

        /// <summary>
        /// For <see cref="SchemaTypeKind.Opaque"/>: the stable name of the user custom formatter that
        /// wrote this type's values — the canonical name of the formatter's CLR type
        /// (<c>OpaquePayload.FormatterToken</c>). Empty for every other kind.
        /// <para>
        /// This is the one field of a manifest entry that describes <b>which formatter</b> produced the
        /// bytes rather than what they contain, and it exists because formatter selection is part of the
        /// layout: it is written into the file's schema section, and
        /// <see cref="SchemaRegistry.ComputeFingerprint"/> hashes it, so a build that swaps a type's
        /// custom formatter sees a different fingerprint and takes the migration path instead of the
        /// generated one. Two builds using the <em>same</em> formatter class with different behaviour
        /// are indistinguishable here — see <c>Format/LAYOUT.md</c>, "What the formatter name does not
        /// distinguish".
        /// </para>
        /// </summary>
        public string OpaqueFormatterName { get; }

        /// <summary>
        /// For <see cref="SchemaTypeKind.Opaque"/>: whether the value is written <b>inline</b> (a value
        /// type: no reference marker in front of the frame) rather than behind a reference marker (a
        /// reference type). False for every other kind.
        /// <para>
        /// Recorded rather than re-derived from the current CLR type, because the skip path has to be
        /// able to account for a value whose type this build no longer has: whether a marker stands in
        /// front of the frame is a fact about the writer, and the file is the only place that fact
        /// survives. It is a property of the <em>type</em> and not of the position, because every
        /// writer that emits a value of a reference type emits a marker for it — the collection and
        /// array formatters inject <c>GetReferenceFormatterRequired(TItem)</c>, so a class element is
        /// marked and registered like any other class value.
        /// </para>
        /// </summary>
        public bool OpaqueValueWrittenInline { get; }

        public SchemaTypeInfo(
            string                stableName,
            string[]              previousTypeNames,
            SchemaTypeKind        kind,
            SchemaFieldInfo[]     fields,
            int                   fixedByteSize,
            SchemaEnumValueInfo[] enumValues         = null!,
            string                underlyingTypeName = null!,
            string[]              argTypeNames       = null!,
            string                opaqueFormatterName = null!,
            bool                  opaqueValueWrittenInline = false)
        {
            StableName         = stableName         ?? throw new ArgumentNullException(nameof(stableName));
            PreviousTypeNames  = previousTypeNames   ?? Array.Empty<string>();
            Kind               = kind;
            Fields             = fields              ?? Array.Empty<SchemaFieldInfo>();
            EnumValues         = enumValues          ?? Array.Empty<SchemaEnumValueInfo>();
            UnderlyingTypeName = underlyingTypeName  ?? string.Empty;
            ArgTypeNames       = argTypeNames        ?? Array.Empty<string>();
            FixedByteSize      = fixedByteSize;
            OpaqueFormatterName = opaqueFormatterName ?? string.Empty;
            OpaqueValueWrittenInline = opaqueValueWrittenInline;
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
        /// Derived from the element type, not configured: a value type or a sealed class is
        /// monomorphic, anything else is not. There is no attribute to override it — the former
        /// <c>[Monomorphic]</c>/<c>[Polymorphic]</c> pair no longer exists, and
        /// <c>TypeHelpers.IsMonomorphicByDefault</c> (generator) and <c>SchemaMember</c>
        /// (reflection fallback) are the two places the value is computed.
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

    /// <summary>
    /// One name→value entry from an enum definition. <see cref="Value"/> holds the enum value's
    /// <b>raw bits</b>, so a <c>ulong</c> value above <see cref="long.MaxValue"/> survives. Whether
    /// those bits read as a signed or an unsigned number is decided by the enum's underlying type id
    /// (<see cref="SchemaTypeInfo.UnderlyingTypeName"/>), so this needs no extra flag or second word.
    /// </summary>
    public sealed class SchemaEnumValueInfo
    {
        public string Name { get; }
        public ulong Value { get; }

        public SchemaEnumValueInfo(string name, ulong value)
        {
            Name  = name  ?? throw new ArgumentNullException(nameof(name));
            Value = value;
        }
    }
}
