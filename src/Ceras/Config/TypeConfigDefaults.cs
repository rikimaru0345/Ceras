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

            if (CerasSerializer.IsFormatterConstructed(typeConfig.Type) || (typeConfig.Type.IsStatic()) || typeConfig.Type.IsArray)
            {
                typeConfig.TypeConstruction = TypeConstruction.Null();
                return;
            }

            var type = typeConfig.Type;
            MethodBase selectedCtor = null;

            // 1. Try hint by attribute
            IEnumerable<MethodBase> methods = type.GetMethods(BindingFlagsStatic).Cast<MethodBase>().Concat(type.GetConstructors(BindingFlagsCtor));
            var cerasCtors = methods.Where(m => m.GetCustomAttribute<CerasConstructorAttribute>() != null).ToArray();

            if (cerasCtors.Length > 1)
            {
                throw new InvalidConfigException($"There are multiple constructors on your type '{typeConfig.Type.FriendlyName()}' that have the '[CerasConstructor]' attribute, so its unclear which one to use. Only one constructor in this type can have the attribute.");
            }
            else if (cerasCtors.Length == 1)
            {
                selectedCtor = cerasCtors[0];
            }
            else
            {
                // 2. Try default (parameterless) ctor
                selectedCtor = type.GetConstructors(BindingFlagsCtor).FirstOrDefault(c => c.GetParameters().Length == 0);

                if (selectedCtor == null)
                {
                    // 3. Heuristic: Find the ctor with most parameters that can be fully satisfied by serializable members
                    // We must use all members here because some might be excluded by default but COULD be included if they match a ctor param
                    var allCtors = type.GetConstructors(BindingFlagsCtor).OrderByDescending(c => c.GetParameters().Length).ToList();
                    var members = typeConfig._allMembers;

                    foreach (var c in allCtors)
                    {
                        var parameters = c.GetParameters();
                        if (parameters.Length == 0) continue; // already handled

                        bool allMatch = true;
                        foreach (var p in parameters)
                        {
                            bool hasMatch = members.Any(m => string.Equals(MiscHelpers.CleanMemberName(m.Member.Name), p.Name, StringComparison.OrdinalIgnoreCase) ||
                                                            string.Equals(m.Member.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                            if (!hasMatch)
                            {
                                allMatch = false;
                                break;
                            }
                        }

                        if (allMatch)
                        {
                            selectedCtor = c;
                            break;
                        }
                    }
                }
            }
            
            // Apply this ctor or factory
            if (selectedCtor != null)
            {
                if (selectedCtor is ConstructorInfo constructorInfo)
                    typeConfig.TypeConstruction = TypeConstruction.ByConstructor(constructorInfo);
                else if (selectedCtor is MethodInfo methodInfo)
                    typeConfig.TypeConstruction = TypeConstruction.ByStaticMethod(methodInfo);
            }

            if (type.IsValueType && typeConfig.TypeConstruction == null)
                typeConfig.TypeConstruction = ConstructNull.Instance;

            // 4. Last resort: GetUninitializedObject (if allowed in config)
            bool allowFallback = typeConfig.AllowUninitializedObjectFallback ?? typeConfig.Config.Advanced.AllowUninitializedObjectFallback;
            if (typeConfig.TypeConstruction == null && allowFallback)
                typeConfig.TypeConstruction = TypeConstruction.ByUninitialized();
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
            var hasExclude = memberInfo.GetCustomAttribute<ExcludeAttribute>(true) != null;
            var hasInclude = memberInfo.GetCustomAttribute<IncludeAttribute>(true) != null;
            var hasNonSerialized = memberInfo.GetCustomAttribute<NonSerializedAttribute>(true) != null;
            
            if (hasInclude && (hasExclude || hasNonSerialized))
                throw new Exception($"Member '{memberInfo.Name}' on type '{type.FriendlyName()}' has both [Include] and [Exclude] (or [NonSerialized]) !");

            if (hasExclude)
                memberConfig.ExcludeWithReason("[Exclude] attribute");
            
            if(hasNonSerialized)
                memberConfig.HasNonSerializedAttribute = true;

            if (hasInclude)
                memberConfig.SetIncludeWithReason(SerializationOverride.ForceInclude, "[Include] attribute on member");

            // Success: persistent name (from attribute or normal member name)
            var serializedAs = memberInfo.GetCustomAttribute<CerasStableNameAttribute>();
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
