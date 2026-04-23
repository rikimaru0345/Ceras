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
                typeConfig.TypeConstruction = TypeConstruction.Null();
                return;
            }

            // 2. Robust mode: always use uninitialized objects
            if (typeConfig.Config is CerasRobustConfig)
            {
                typeConfig.TypeConstruction = TypeConstruction.ByUninitialized();
                return;
            }

            // 3. Try parameterless constructor
            var ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (ctor != null)
            {
                typeConfig.TypeConstruction = TypeConstruction.ByConstructor(ctor);
                return;
            }

            // 4. Value types
            if (type.IsValueType)
            {
                typeConfig.TypeConstruction = TypeConstruction.Null();
                return;
            }

            // 5. Fallback: uninitialized object
            typeConfig.TypeConstruction = TypeConstruction.ByUninitialized();
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
                if (string.IsNullOrEmpty(memberConfig.PersistentName) || memberConfig.PersistentName == memberInfo.Name)
                    memberConfig.PersistentName = prevName.Name;

                if (prevName.Name != null)
                    CerasHelpers.VerifyName(prevName.Name);

                foreach (var n in prevName.AlternativeNames)
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
                    typeConfig.TypeConstruction = ConstructNull.Instance;

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

                if (type.FullName.StartsWith("System.Runtime.CompilerServices.TrueReadOnlyCollection"))
                {
                    ForceSerialization(typeConfig);
                    return;
                }
            }

            // ReadOnlyCollection
            if (type.Assembly == typeof(ReadOnlyCollection<>).Assembly)
                if (type.FullName.StartsWith("System.Collections.ObjectModel.ReadOnlyCollection"))
                {
                    ForceSerialization(typeConfig);
                    return;
                }
        }

        static void ForceSerialization(TypeConfig typeConfig)
        {
            typeConfig.TypeConstruction = TypeConstruction.ByUninitialized();
            typeConfig.CustomResolver = ForceDynamicResolver;
        }


        static IFormatter ForceDynamicResolver(CerasSerializer ceras, Type type)
        {
#pragma warning disable CS0618
            var r = ceras.GetFormatterResolver<ReflectionFallbackResolver>();
            return r.GetFormatter(type);
#pragma warning restore CS0618
        }
    }
}
