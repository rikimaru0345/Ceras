using System;


namespace Ceras
{
    using Formatters;
    using Helpers;


    /// <summary>
    /// Add this to a field or property to force Ceras to ignore it.
    /// Check out the tutorial to see in what order attributes, the ShouldSerialize callback and other settings are evaluated.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class ExcludeAttribute : Attribute { }

    /// <summary>
    /// Add this to a field or property to force Ceras to include it.
    /// Check out the tutorial to see in what order attributes, the ShouldSerialize callback and other settings are evaluated.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class IncludeAttribute : Attribute { }


    /// <summary>
    /// Read the descriptions of the individual entries.
    /// </summary>
    public enum SerializationOverride
    {
        /// <summary>
        /// When you return 'NoOverride' Ceras will continue normally, which is checking the member-attributes, class attributes, etc... check out the tutorial to see how Ceras decides what members are included in detail.
        /// </summary>
        NoOverride,
        /// <summary>
        /// ForceInclude will completely skip all other checks and include the member. Be careful that you don't accidentally include hidden/compiler generated fields if you have turned 'SkipCompilerGeneratedFields' off.
        /// </summary>
        ForceInclude,
        /// <summary>
        /// Forces Ceras to ignore the field or property completely.
        /// </summary>
        ForceSkip,
    }


    /// <summary>
    /// Configure what members to include by default in this type, you can also add [Exclude] and [Include] to individual members as well to override the member config.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class MemberConfigAttribute : Attribute
    {
        public TargetMember TargetMembers { get; set; }
        public ReadonlyFieldHandling ReadonlyFieldHandling { get; set; }

        public MemberConfigAttribute(TargetMember targetMembers = TargetMember.PublicFields, ReadonlyFieldHandling readonlyFieldHandling = ReadonlyFieldHandling.ExcludeFromSerialization)
        {
            TargetMembers = targetMembers;
            ReadonlyFieldHandling = readonlyFieldHandling;
        }
    }

    /// <summary>
    /// Use this to override global or class-level settings for a single field or property.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class ReadonlyConfigAttribute : Attribute
    {
        public ReadonlyFieldHandling ReadonlyFieldHandling { get; set; }

        public ReadonlyConfigAttribute(ReadonlyFieldHandling readonlyFieldHandling = ReadonlyFieldHandling.ExcludeFromSerialization)
        {
            ReadonlyFieldHandling = readonlyFieldHandling;
        }
    }

    /// <summary>
    /// What members should be serialized and deserialized
    /// </summary>
    [Flags]
    public enum TargetMember
    {
        None = 0,

        /// <summary>
        /// Include all fields with the "public" keyword, pretty obvious. As with all visibility checks, "public" just means "has the public keyword", and doesn't require complete outside visibility (like, the containing class or struct doesn't have to be public for the field to count as public)
        /// </summary>
        PublicFields = 1 << 0,
        /// <summary>
        /// Include all private fields. Hidden compiler-generated fields (like backing fields for properties, enumerator state-machines, ...) are not included.
        /// </summary>
        PrivateFields = 1 << 1,
        /// <summary>
        /// Only properties marked with the "public" keyword. So properties marked as internal/protected/private are not included
        /// </summary>
        PublicProperties = 1 << 2,
        /// <summary>
        /// Properties that are not public (so "internal", "private", or "protected")
        /// </summary>
        PrivateProperties = 1 << 3,

        AllPublic = PublicFields | PublicProperties,
        AllPrivate = PrivateFields | PrivateProperties,

        AllFields = PublicFields | PrivateFields,
        AllProperties = PublicProperties | PrivateProperties,

        All = PublicFields | PrivateFields | PublicProperties | PrivateProperties
    }

    /// <summary>
    /// Add this to a member if you have changed the type and you're using the VersionTolerance feature.
    /// Ceras will use this to map old field names to the new one.
    /// You can also use this to simply override what name is used to serialize the member, so as long as the attribute is around and does not change you can freely rename the member itself; this can be used to make the resulting serialized data smaller.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class PreviousNameAttribute : Attribute
    {
        public readonly string[] AlternativeNames = new string[0];
        public readonly string Name;

        public PreviousNameAttribute()
        {
        }

        public PreviousNameAttribute(string name)
        {
            Name = name;
        }

        public PreviousNameAttribute(string name, params string[] alternativeNames)
        {
            Name = name;
            AlternativeNames = alternativeNames;
        }
    }


    // todo: previous type / previous formatter would be nice to have. It's supposed to auto-convert old data to the new format (or let the user provide a formatter to read the old data)
    // at the moment the problem is that we never know in what format the data was written; we'd have to embed the data type (ewww! that would make the binary huge!), or add a version number that the user provides
    // so we always know in what format we can expect the data. version number would be simply added to the binary data. 

    class PreviousFormatter : PreviousNameAttribute
    {
        public Type FormatterType { get; } // formatter that can read this old version

        public PreviousFormatter(Type formatterType) : base(null)
        {
            CheckType(formatterType);
            FormatterType = formatterType;
        }
        public PreviousFormatter(string previousName, Type formatterType) : base(previousName)
        {
            CheckType(formatterType);
            FormatterType = formatterType;
        }

        static void CheckType(Type formatterType)
        {
            if (!typeof(IFormatter).IsAssignableFrom(formatterType))
                throw new Exception($"The provided type {formatterType.FriendlyName()} is not valid for 'PreviousFormatter', it needs to be a type that implements IFormatter<T>");
        }
    }

    class PreviousType : PreviousNameAttribute
    {
        public Type MemberType { get; } // the old type of the field/property

        public PreviousType(Type memberType) : base(null)
        {
            MemberType = memberType;
        }

        public PreviousType(string previousName, Type memberType) : base(previousName)
        {
            MemberType = memberType;
        }
    }


    /// <summary>
    /// Put this on any constructor or static method as a hint of what constructor/factory to use by default. (Can be overriden through <see cref="SerializerConfig.ConfigType{T}"/>)
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method)]
    public class CerasConstructorAttribute : Attribute
    {
    }

    
    /// <summary>
    /// Add this to a method of a class you serialize/deserialize. Ceras will call it during serialization.
    /// </summary>
    public class OnBeforeSerializeAttribute : Attribute
    {
    }

    /// <summary>
    /// Add this to a method of a class you serialize/deserialize. Ceras will call it during serialization.
    /// </summary>
    public class OnAfterSerializeAttribute : Attribute
    {
    }
    
    /// <summary>
    /// Add this to a method of a class you serialize/deserialize. Ceras will call it during serialization.
    /// </summary>
    public class OnBeforeDeserializeAttribute : Attribute
    {
    }

    /// <summary>
    /// Add this to a method of a class you serialize/deserialize. Ceras will call it during serialization.
    /// </summary>
    public class OnAfterDeserializeAttribute : Attribute
    {
    }
}

namespace Ceras.Formatters
{
    /// <summary>
    /// Add this attribute to your <see cref="IFormatter{T}"/> class to configure Ceras' dependency injection system. (Only needed to turn it off, it's enabled, even for private fields, by default)
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class CerasInjectAttribute : Attribute
    {
        internal static readonly CerasInjectAttribute Default = new CerasInjectAttribute();

        /// <summary>
        /// If true, Ceras will include all private fields when trying to inject dependencies
        /// <para>Default: true</para>
        /// </summary>
        public bool IncludePrivate { get; set; } = true;
    }

    /// <summary>
    /// Add this to a field inside an <see cref="IFormatter{T}"/> to let Ceras ignore it. Only useful on fields that could potentially have anything injected into them in the first place (field types like <see cref="CerasSerializer"/> or inheriting from <see cref="IFormatter"/>)
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Class)]
    public class CerasNoInjectAttribute : Attribute
    {
    }

    /// <summary>
    /// Add this to a field inside an <see cref="IFormatter{T}"/> to inject only the direct version of a formatter, instead of a wrapper for reference types. Only use this if you are fully aware of what ReferenceFormatter does and provides
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class CerasNoReference : Attribute
    {
    }
}

namespace Ceras.Formatters.AotGenerator
{
    /// <summary>
    /// When using the formatter generator you should have a static method somewhere that returns a SerializerConfig, the tool will find the method using this attribute and then execute it so it can use the same config that you are using when the program runs.
    /// That way the generator knows what types to generate formatters for, and what members to include or exclude.
    /// The tool inspects 'KnownTypes' as well as all types that you configured using the ConfigType methods. Types that are only handled in "OnConfigNewType" will not be handled in any way because that method is only called when any type is actually encountered (serialized or deserialized).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class AotSerializerConfigAttribute : Attribute
    {
    }
}

namespace Ceras
{
    /// <summary>
    /// Put this attribute on every class/struct you want Ceras to generate a formatter for at compile time.
    /// The source generator will emit an <c>IFormatter&lt;T&gt;</c> implementation and include it in
    /// <c>Ceras.Generated.GeneratedFormatters.UseFormatters(config)</c>.
    /// This replaces the old AotGenerator tool and is required for IL2CPP / AOT platforms.
    /// </summary>
    /// <param name="stableName">
    /// The stable wire name for this type — independent of the C# class name.
    /// If omitted, defaults to the simple class name (no namespace).
    /// Set this once and the C# class can be freely renamed without needing <see cref="PreviousTypeNameAttribute"/>.
    /// </param>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum)]
    public class GenerateFormatterAttribute : Attribute
    {
        /// <summary>The stable name used in the binary schema. Defaults to the simple class name.</summary>
        public string StableName { get; }

        /// <summary>
        /// When true, the source generator also emits a schema descriptor and registers it with
        /// <see cref="Ceras.Savegame.SavegameSchemaRegistry"/>. Required for the Ceras savegame
        /// format's version-tolerance (add/remove/rename fields across game updates).
        /// </summary>
        public bool VersionTolerant { get; set; }

        public GenerateFormatterAttribute(string stableName = null)
        {
            StableName = stableName;
        }
    }

    /// <summary>
    /// Declares a previous CLR type name for this class/struct so that the savegame migration
    /// engine can map old binary data (written under the old name) to the current type.
    /// Can be applied multiple times if the type has been renamed more than once.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    public class PreviousTypeNameAttribute : Attribute
    {
        public string Name { get; }
        public PreviousTypeNameAttribute(string name) { Name = name; }
    }

    /// <summary>
    /// Sets a stable wire name for this field or property — independent of the C# member name.
    /// Set this once and the C# member can be freely renamed without needing <see cref="PreviousNameAttribute"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class SerializedAsAttribute : Attribute
    {
        public string Name { get; }
        public SerializedAsAttribute(string name) { Name = name; }
    }

    /// <summary>
    /// Forces a collection field to be treated as monomorphic: all elements are guaranteed to be
    /// exactly the declared element type, so no per-element type ID is written.
    /// Auto-detected for value types and sealed classes; use this to override for non-sealed types
    /// you know will never be subclassed in saves.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class MonomorphicAttribute : Attribute { }

    /// <summary>
    /// Forces a collection field to be treated as polymorphic even when auto-detection would
    /// mark it as monomorphic (e.g. a sealed class that you plan to extend later).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class PolymorphicAttribute : Attribute { }
}
