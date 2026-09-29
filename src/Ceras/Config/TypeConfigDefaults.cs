using System;
using System.Linq;

namespace Ceras
{
    using Exceptions;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Runtime.Serialization;
    using Helpers;
    using Resolvers;
    using IFormatter = Formatters.IFormatter;

    static class TypeConfigDefaults
    {
        const BindingFlags BindingFlagsStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        const BindingFlags BindingFlagsCtor = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.CreateInstance;


        // Apply default settings to the newly created TypeConfig based on the attributes on the Type
        internal static void ApplyTypeAttributes(TypeConfig typeConfig)
        {
            var type = typeConfig.Type;

            //
            // Set default values from attributes
            //
            // Constructor
            ApplyConstructorDefaults(typeConfig);
        }

        static void ApplyConstructorDefaults(TypeConfig typeConfig)
        {
            if (typeConfig.TypeConstruction != null)
                return;

            var type = typeConfig.Type;

            // 1. Arrays, statics, and manually handled types
            if (CerasSerializer.IsFormatterConstructed(type) || type.IsStatic() || type.IsArray || (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string)))
            {
                // Every concrete IEnumerable reference type (not just collection subclasses such as
                // MyList<T> : List<T>) is "formatter constructed" in the sense that its formatter builds
                // the instance, so no CLR constructor is run for a fresh object. But a reference type
                // still needs *some* non-null allocation before its members/elements are read: the
                // built-in collection formatter constructs the actual instance, and the reference
                // wrapper only needs a placeholder for object-cache registration. ConstructNull returns
                // null, which breaks that, so such a type is allocated uninitialized instead.
                typeConfig.SetDefaultTypeConstruction(type.IsValueType || type.IsAbstract || type.IsInterface || type.IsArray
                    ? TypeConstruction.Null()
                    : TypeConstruction.ByUninitialized());
                return;
            }

            // 2. Robust mode: always use uninitialized objects
            if (typeConfig.Config is CerasRobustConfig)
            {
                typeConfig.SetDefaultTypeConstruction(TypeConstruction.ByUninitialized());
                return;
            }

            // 3. Try parameterless constructor
            var ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (ctor != null)
            {
                typeConfig.SetDefaultTypeConstruction(TypeConstruction.ByConstructor(ctor));
                return;
            }

            // 4. Value types
            if (type.IsValueType)
            {
                typeConfig.SetDefaultTypeConstruction(TypeConstruction.Null());
                return;
            }

            // 5. Fallback: uninitialized object
            typeConfig.SetDefaultTypeConstruction(TypeConstruction.ByUninitialized());
        }

        internal static void CheckStructWithAutoProps(TypeConfig typeConfig)
        {
            var type = typeConfig.Type;
            if (type.IsValueType && typeConfig.Config.WarnOnStructWithAutoProperties)
            {
                var allDataMembers = type.GetAllDataMembers().ToArray();
                
                // Ceras serializes structs by their fields. 
                // Auto-properties use compiler-generated backing-fields which are excluded by default.
                // We want to detect if the user has ONLY auto-properties and NO other "real" fields, 
                // as this would result in an empty (or unexpectedly small) serialized object.
                var normalFields = allDataMembers.OfType<FieldInfo>()
                                                .Where(f => f.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() == null)
                                                .ToArray();

                var autoProps = allDataMembers.OfType<PropertyInfo>().Where(p =>
                {
                    var get = p.GetGetMethod(true);
                    return get != null && get.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() != null;
                }).ToArray();

                // If we found auto-props but NO normal fields, give the warning.
                if (autoProps.Length > 0 && normalFields.Length == 0)
                {
                    var msg = $"The struct '{type.FriendlyName()}' cannot be serialized because it only consists of auto-properties. " +
                              "Ceras serializes structs by their fields, but auto-properties use hidden backing-fields that are excluded by default. " +
                              "To fix this, either: 1. Use normal fields, 2. Add [CerasSerializable] and use the source generator, or 3. Disable this warning in CerasConfig.WarnOnStructWithAutoProperties.";

                    throw new TargetInvocationException(new Exception(msg));
                }
            }
        }

        // Use attributes on the members to further specialize the settings.
        // We're only setting some *defaults* here, that can later be overriden some more.
        internal static void ApplyMemberAttributes(MemberConfig memberConfig)
        {
            var type = memberConfig.TypeConfig.Type;

            var memberInfo = memberConfig.Member;

            // Apply WriteBackOrder
            var dataMemberAttribute = memberInfo.GetCustomAttribute<DataMemberAttribute>();
            if (dataMemberAttribute != null)
            {
                memberConfig.WriteBackOrder = dataMemberAttribute.Order;
            }


            // Skip compiler generated (backing fields for auto-properties are NOT compiler-generated per IsCompilerGenerated, so they are included)
            if (memberConfig.IsCompilerGenerated)
            {
                memberConfig.ExcludeWithReason("Compiler generated field");
                return;
            }

            if (memberInfo is PropertyInfo)
            {
                // Properties are never serialized directly; backing fields are handled as fields above.
                // A property can only be included via an explicit [Include] attribute.
                if (ShouldSkipProperty(memberConfig))
                    return;
            }


            // [Include] / [Exclude] / [NonSerialized]
            var hasExclude = memberInfo.GetCustomAttribute<ExcludeAttribute>(true) != null || (memberConfig.AssociatedProperty != null && memberConfig.AssociatedProperty.GetCustomAttribute<ExcludeAttribute>(true) != null);
            var hasInclude = memberInfo.GetCustomAttribute<IncludeAttribute>(true) != null || (memberConfig.AssociatedProperty != null && memberConfig.AssociatedProperty.GetCustomAttribute<IncludeAttribute>(true) != null);
            var hasNonSerialized = memberInfo.GetCustomAttribute<NonSerializedAttribute>(true) != null || (memberConfig.AssociatedProperty != null && memberConfig.AssociatedProperty.GetCustomAttribute<NonSerializedAttribute>(true) != null);
            
            if (hasInclude && (hasExclude || hasNonSerialized))
                throw new Exception($"Member '{memberInfo.Name}' on type '{type.FriendlyName()}' has both [Include] and [Exclude] (or [NonSerialized]) !");

            if (hasExclude)
                memberConfig.ExcludeWithReason("[Exclude] attribute");
            
            if(hasNonSerialized)
                memberConfig.HasNonSerializedAttribute = true;

            if (hasInclude)
                memberConfig.SetIncludeWithReason(SerializationOverride.ForceInclude, "[Include] attribute on member");

            // Success: persistent name (from attribute or normal member name)
            var serializedAs = memberInfo.GetCustomAttribute<CerasStableNameAttribute>() ?? memberConfig.AssociatedProperty?.GetCustomAttribute<CerasStableNameAttribute>();
            if (serializedAs != null)
            {
                memberConfig.PersistentName = serializedAs.Name;
                CerasHelpers.VerifyName(serializedAs.Name);
            }

            // Type check: is the type of this member marked with [Exclude]?
            var memberType = memberConfig.MemberType;
            if (memberType.GetCustomAttribute<ExcludeAttribute>(true) != null)
            {
                memberConfig.ExcludeWithReason($"Type '{memberType.FriendlyName()}' is marked with [Exclude]");
                return;
            }

            var prevName = memberInfo.GetCustomAttribute<PreviousNameAttribute>();
            if (prevName != null)
            {
                // The old name becomes the wire name only when nothing else already picked one:
                // an explicit [CerasStableName] wins, because it is the spelling a file written by
                // this build uses. PersistentName never returns null or empty (it falls back to the
                // member's own name), so this is really the "no stable name was configured" test.
                if (string.IsNullOrEmpty(memberConfig.PersistentName) || memberConfig.PersistentName == memberInfo.Name)
                {
                    // A null Name means the attribute records alternative names only, with no
                    // primary old name to adopt. Assigning it clears the override, so PersistentName
                    // falls back to the member's own name again - the intended "no old name" state,
                    // and the same value the getter produced a line above.
                    memberConfig.PersistentName = prevName.Name;
                }

                if (prevName.Name != null)
                    CerasHelpers.VerifyName(prevName.Name);

                // Every alternative is an old wire name and has to satisfy the same rules, so a
                // malformed one is reported here rather than as a corrupt-file error on load. The
                // array is only null-annotated because the attribute's params overload stores
                // whatever it was handed, including a null array.
                foreach (var n in prevName.AlternativeNames ?? Array.Empty<string>())
                    CerasHelpers.VerifyName(n);
            }
        }

        static bool ShouldSkipProperty(MemberConfig m)
        {
            if (m.IsGetterOnly)
            {
                if (m.MemberType.IsValueType)
                {
                    m.ExcludeWithReason("Getter-only property of value type (cannot be populated in-place)");
                    return true;
                }

                // Getter-only reference-type properties can be populated in-place (we always use ForcedOverwrite behavior)
            }

            return false;
        }


        // Built-in support for some specific types
        internal static void ApplySpecializedDefaults(TypeConfig typeConfig)
        {
            var type = typeConfig.Type;

            // All structs
            if (!type.IsPrimitive && type.IsValueType)
            {
                if(typeConfig.TypeConstruction == null)
                    typeConfig.SetDefaultTypeConstruction(ConstructNull.Instance);

                return;
            }

            // Version
            if(type == typeof(Version))
            {
                ForceSerialization(typeConfig);
                return;
            }

            // Expression & TrueReadOnlyCollection
            if (type.Assembly == typeof(Expression).Assembly)
            {
                if (!type.IsAbstract && type.IsSubclassOf(typeof(Expression)))
                {
                    ForceSerialization(typeConfig);
                    return;
                }

                if (type.FullName?.StartsWith("System.Runtime.CompilerServices.TrueReadOnlyCollection", StringComparison.Ordinal) == true)
                {
                    ForceSerialization(typeConfig);
                    return;
                }
            }

            // ReadOnlyCollection<T> is deliberately NOT handled here, and it is no longer *serializable*
            // either. The history: it has no public parameterless constructor and no Add, so the
            // reflection fallback dumped it field-by-field — a payload the robust schema does not
            // describe, so such a file could not be read back. A dedicated formatter then fixed the
            // bytes and made the base type round-trip, but it was typed for the base
            // IFormatter<ReadOnlyCollection<T>>, so a *subclass* could not be serialized at all: the
            // reference wrapper could not bind a serializer for the derived type and Save failed with a
            // bare ArgumentNullException("method"). The owner has since decided the whole family is out of
            // scope (Format/UnsupportedCollectionPolicy: ReadOnlyCollection<T> /
            // ReadOnlyDictionary<K,V> and their subclasses, plus every List<T>/Dictionary<K,V> subclass),
            // so there is no formatter to install here and the refusal is reported from the layers that
            // own each position instead.
        }

        static void ForceSerialization(TypeConfig typeConfig)
        {
            typeConfig.SetDefaultTypeConstruction(TypeConstruction.ByUninitialized());
            // The cached instance, not the method group: a robust context recognises this resolver by
            // reference (RobustResolverPolicy), and a method group allocates a fresh delegate every time
            // it is converted, so handing the group out here would install an instance nothing holds a
            // reference to and the approval would never match.
            typeConfig.CustomResolver = ForcedDynamicResolver;
        }


        static IFormatter ForceDynamicResolver(CerasSerializer ceras, Type type)
        {
#pragma warning disable CS0618
            // Only called once the fallback has been allowed, so the resolver is installed; saying so
            // beats a null reference if that ever stops being true.
            var r = ceras.GetFormatterResolver<ReflectionFallbackResolver>()
                ?? throw new InvalidOperationException(
                    "The reflection fallback resolver is not installed, so the type cannot be serialized by reflection. " +
                    "Enable CerasConfig.AllowReflectionFallback (or declare the type with [CerasSerializable]).");
            return r.GetFormatter(type)!;
#pragma warning restore CS0618
        }

        /// <summary>
        /// The one delegate instance <see cref="ForceSerialization"/> installs as a
        /// <see cref="TypeConfig.CustomResolver"/> for the types it forces into reflection-based
        /// serialization, and the one <c>RobustResolverPolicy</c> compares against — so what a robust
        /// context approves is this exact delegate, installed by this library, and not any delegate an
        /// application builds over the same method.
        /// <para>
        /// The approval is scoped to this resolver rather than to the formatter it answers with
        /// (<c>ReflectionFallbackFormatter&lt;T&gt;</c>), because that formatter is closed over an
        /// arbitrary type and is constructible by reflection despite being <c>internal sealed</c>: a
        /// formatter-type exemption would approve a caller's instance of it just as readily. See
        /// <c>Format/RobustResolverPolicy</c>.
        /// </para>
        /// </summary>
        internal static readonly FormatterResolverCallback ForcedDynamicResolver = ForceDynamicResolver;
    }
}
