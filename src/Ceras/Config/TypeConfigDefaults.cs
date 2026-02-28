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
            var memberConfigAttrib = type.GetCustomAttribute<MemberConfigAttribute>();
            if (memberConfigAttrib != null)
            {
                typeConfig.ReadonlyFieldOverride = memberConfigAttrib.ReadonlyFieldHandling;
                typeConfig.TargetMembers = memberConfigAttrib.TargetMembers;
                typeConfig.AllowUninitializedObjectFallback = memberConfigAttrib.InternalAllowUninitializedObjectFallback;
            }

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
            if (typeConfig.Config.Robust.Enabled)
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

            // 5. Fallback: uninitialized object (if allowed)
            bool allowFallback = typeConfig.AllowUninitializedObjectFallback ?? typeConfig.Config.Advanced.AllowUninitializedObjectFallback;
            if (allowFallback)
            {
                typeConfig.TypeConstruction = TypeConstruction.ByUninitialized();
            }
        }

        internal static void CheckStructWithAutoProps(TypeConfig typeConfig)
        {
            var type = typeConfig.Type;
            if (type.IsValueType && typeConfig.Config.Warnings.ExceptionOnStructWithAutoProperties)
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
                              "To fix this, either: 1. Use normal fields, 2. Add [CerasSerializable] and use the source generator, or 3. Disable this warning in SerializerConfig.Warnings.";

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


            // Skip compiler generated
            if (memberConfig.IsCompilerGenerated)
                if (memberConfig.TypeConfig.Config.Advanced.SkipCompilerGeneratedFields)
                {
                    memberConfig.ExcludeWithReason("Compiler generated field");
                    return;
                }

            if (memberInfo is FieldInfo f)
            {
                var readOnlyConfig = f.GetCustomAttribute<ReadonlyConfigAttribute>(true);
                if(readOnlyConfig != null)
                memberConfig.ReadonlyFieldHandling = readOnlyConfig.ReadonlyFieldHandling;

                // Getter-only fields (readonly) are allowed if they are reference types
                // or if we are using ForcedOverwrite
                if (memberConfig.IsReadonlyField && memberConfig.MemberType.IsValueType)
                {
                    if (memberConfig.ComputeReadonlyHandling() != ReadonlyFieldHandling.ForcedOverwrite)
                    {
                        memberConfig.ExcludeWithReason("Readonly field of value type (cannot be populated in-place)");
                        return;
                    }
                }
            }
            else if (memberInfo is PropertyInfo p)
            {
                // prop: skip computed
                if (ShouldSkipProperty(memberConfig))
                    return;
            }
            else
            {
                throw new InvalidOperationException(memberInfo.Name + " must be a field or a property");
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

                // If it's a reference type, we can populate it in-place IF we are in the right mode
                var handling = m.ComputeReadonlyHandling();
                if (handling != ReadonlyFieldHandling.Members && handling != ReadonlyFieldHandling.ForcedOverwrite)
                {
                    m.ExcludeWithReason("Getter-only property of reference type (can only be populated if ReadonlyFieldHandling is set to 'Members')");
                    return true;
                }
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

                typeConfig.TargetMembers = TargetMember.AllFields | TargetMember.PublicProperties;
                typeConfig.ReadonlyFieldOverride = ReadonlyFieldHandling.ForcedOverwrite;
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
            typeConfig.ReadonlyFieldOverride = ReadonlyFieldHandling.ForcedOverwrite;
            typeConfig.TargetMembers = TargetMember.PrivateFields;
            typeConfig.CustomResolver = ForceDynamicResolver;
        }


        static IFormatter ForceDynamicResolver(CerasSerializer ceras, Type type)
        {
            var r = ((ICerasAdvanced)ceras).GetFormatterResolver<ReflectionFallbackResolver>();
            return r.GetFormatter(type);
        }
    }
}
