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
			// Constructor
			ApplyConstructorDefaults(typeConfig);

			//
			// Set default values from attributes
			var memberConfigAttrib = type.GetCustomAttribute<MemberConfigAttribute>();
			if (memberConfigAttrib != null)
			{
				typeConfig.ReadonlyFieldOverride = memberConfigAttrib.ReadonlyFieldHandling;
				typeConfig.TargetMembers = memberConfigAttrib.TargetMembers;
			}
		}

		static void ApplyConstructorDefaults(TypeConfig typeConfig)
		{
			if (typeConfig.TypeConstruction != null)
				return;


			if(typeConfig.TypeConstruction == null)
				if (CerasSerializer.IsFormatterConstructed(typeConfig.Type) || (typeConfig.Type.IsStatic()))
				{
					typeConfig.TypeConstruction = TypeConstruction.Null();
					return;
				}


			var type = typeConfig.Type;
			MethodBase ctor;

			// Try hint by attribute
			IEnumerable<MethodBase> methods = type.GetMethods(BindingFlagsStatic).Cast<MethodBase>().Concat(type.GetConstructors(BindingFlagsCtor));
			var cerasCtors = methods.Where(m => m.GetCustomAttribute<CerasConstructorAttribute>() != null).ToArray();

			if (cerasCtors.Length > 1)
			{
				// Multiple matches
				throw new InvalidConfigException($"There are multiple constructors on your type '{typeConfig.Type.FriendlyName()}' that have the '[CerasConstructor]' attribute, so its unclear which one to use. Only one constructor in this type can have the attribute.");
			}
			else if (cerasCtors.Length == 1)
			{
				// Single match
				ctor = cerasCtors[0];
			}
			else
			{
				// No match, try default ctor
				ctor = type.GetConstructors(BindingFlagsCtor).FirstOrDefault(c => c.GetParameters().Length == 0);
			}
			
			// Apply this ctor or factory
			if (ctor != null)
			{
				if (ctor is ConstructorInfo constructorInfo)
					typeConfig.TypeConstruction = TypeConstruction.ByConstructor(constructorInfo);
				else if (ctor is MethodInfo methodInfo)
					typeConfig.TypeConstruction = TypeConstruction.ByStaticMethod(methodInfo);
			}


			if (type.IsValueType && typeConfig.TypeConstruction == null)
				typeConfig.TypeConstruction = ConstructNull.Instance;
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
			var prevName = memberInfo.GetCustomAttribute<PreviousNameAttribute>();
			if (prevName != null)
			{
				memberConfig.PersistentName = prevName.Name;
				//VerifyName(attrib.Name);
				//foreach (var n in attrib.AlternativeNames)
				//	VerifyName(n);
			}
		}

		static bool ShouldSkipProperty(MemberConfig m)
		{
			var p = (PropertyInfo)m.Member;

			// There's no way we can serialize a prop that we can't write.
			// If it would have a { private set; } that would work, but it doesn't.
			var accessors = p.GetAccessors(true);

			if (accessors.Length <= 1)
			{
				// It's a "computed" property, which has no concept of writing.
				m.ExcludeWithReason("Computed Property (has no 'set' function, not even a private one)");
				return true;
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

				typeConfig.TargetMembers = TargetMember.AllFields;
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
			var r = ((ICerasAdvanced)ceras).GetFormatterResolver<DynamicObjectFormatterResolver>();
			return r.GetFormatter(type);
		}
	}
}
