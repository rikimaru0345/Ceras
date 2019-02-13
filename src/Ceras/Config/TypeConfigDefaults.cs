using System;
using System.Linq;

namespace Ceras.Config
{
	using System.Collections.ObjectModel;
	using System.Linq.Expressions;
	using System.Reflection;

	static class TypeConfigDefaults
	{
		const BindingFlags BindingFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;


		internal static void SetDefaultConfiguration(Type type, TypeConfig typeConfig)
		{
			//
			// Set default constructor
			var methods = type.GetMethods(BindingFlags).Cast<MemberInfo>().Concat(type.GetConstructors(BindingFlags));
			MemberInfo ctor = methods.FirstOrDefault(m => m.GetCustomAttribute<CerasConstructorAttribute>() != null);

			if (ctor == null)
				// No hint found, try default ctor
				ctor = type.GetConstructors(BindingFlags).FirstOrDefault(c => c.GetParameters().Length == 0);

			// Default is null to throw an exception unless configured otherwise by the user later on
			typeConfig.TypeConstruction = null;

			// Apply this ctor or factory
			if (ctor != null)
			{
				if (ctor is ConstructorInfo constructorInfo)
					typeConfig.TypeConstruction = TypeConstruction.ByConstructor(constructorInfo);
				else if (ctor is MethodInfo methodInfo)
					typeConfig.TypeConstruction = TypeConstruction.ByStaticMethod(methodInfo);
			}


			//
			// Set default values from attributes
			var memberConfig = type.GetCustomAttribute<MemberConfigAttribute>();
			if (memberConfig != null)
			{
				typeConfig.SetReadonlyHandling(memberConfig.ReadonlyFieldHandling);
				typeConfig.SetTargetMembers(memberConfig.TargetMembers);
			}


			// todo: per-member attributes like: include, ignore, readonly


			ApplySpecializedDefaults(type, typeConfig);
		}

		static void ApplySpecializedDefaults(Type type, TypeConfig typeConfig)
		{
			if (type.Assembly == typeof(Expression).Assembly)
			{
				if (!type.IsAbstract && type.IsSubclassOf(typeof(Expression)))
				{
					typeConfig.ConstructByUninitialized();
					typeConfig.SetReadonlyHandling(ReadonlyFieldHandling.ForcedOverwrite);
					typeConfig.SetTargetMembers(TargetMember.PrivateFields);
				}

				if (type.FullName.StartsWith("System.Runtime.CompilerServices.TrueReadOnlyCollection"))
				{
					typeConfig.ConstructByUninitialized();
					typeConfig.SetReadonlyHandling(ReadonlyFieldHandling.ForcedOverwrite);
					typeConfig.SetTargetMembers(TargetMember.PrivateFields);
				}
			}
				
			if (type.Assembly == typeof(ReadOnlyCollection<>).Assembly)
				if (type.FullName.StartsWith("System.Collections.ObjectModel.ReadOnlyCollection"))
				{
					typeConfig.ConstructByUninitialized();
					typeConfig.SetReadonlyHandling(ReadonlyFieldHandling.ForcedOverwrite);
					typeConfig.SetTargetMembers(TargetMember.PrivateFields);
				}
		}
	}
}
