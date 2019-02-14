using System;
using System.Linq;

namespace Ceras
{
	using Ceras.Exceptions;
	using System.Collections.Generic;
	using System.Collections.ObjectModel;
	using System.Linq.Expressions;
	using System.Reflection;

	static class TypeConfigDefaults
	{
		const BindingFlags BindingFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;


		// Apply default settings to the newly created TypeConfig based on the attributes on the Type
		internal static void ApplyTypeAttributes(TypeConfig typeConfig)
		{
			var type = typeConfig.Type;

			//
			// Constructor
			MethodBase ctor = null;

			// Try hint by attribute
			IEnumerable<MethodBase> methods = type.GetMethods(BindingFlags).Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags));
			var cerasCtors = methods.Where(m => m.GetCustomAttribute<CerasConstructorAttribute>() != null).ToArray();
			if (cerasCtors.Length > 1)
				throw new InvalidConfigException($"There are multiple constructors on your type '{typeConfig.Type.Name}' that have the '[CerasConstructor]' attribute, so its unclear which one to use. Only one constructor in this type can have the attribute.");
			else if (cerasCtors.Length == 1)
				ctor = cerasCtors[0];

			// Try default ctor
			if (ctor == null)
				ctor = type.GetConstructors(BindingFlags).FirstOrDefault(c => c.GetParameters().Length == 0);

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
			var memberConfigAttrib = type.GetCustomAttribute<MemberConfigAttribute>();
			if (memberConfigAttrib != null)
			{
				typeConfig.ReadonlyFieldHandling = memberConfigAttrib.ReadonlyFieldHandling;
				typeConfig.TargetMembers = memberConfigAttrib.TargetMembers;
			}
		}

		// Use attributes on the members to further specialize the settings.
		// We're only setting some *defaults* here, that can later be overriden some more.
		internal static void ApplyMemberAttributes(MemberConfig memberConfig)
		{
			var type = memberConfig.TypeConfig.Type;

			var memberInfo = memberConfig.Member;

			// Skip compiler generated
			if (memberConfig.IsCompilerGenerated)
				if (memberConfig.TypeConfig.Config.Advanced.SkipCompilerGeneratedFields)
				{
					memberConfig.IncludeExcludeReason = "Compiler generated field";
					return;
				}

			if (memberInfo is FieldInfo f)
			{
				// field: skip readonly
				if (ShouldSkipField(memberConfig))
					return;
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


			// [Include] / [Ignore] / [NonSerialized]
			var hasIgnore = memberInfo.GetCustomAttribute<IgnoreAttribute>(true) != null;
			var hasInclude = memberInfo.GetCustomAttribute<IncludeAttribute>(true) != null;
			var hasNonSerialized = memberInfo.GetCustomAttribute<NonSerializedAttribute>(true) != null;

			if (hasInclude && (hasIgnore || hasNonSerialized))
				throw new Exception($"Member '{memberInfo.Name}' on type '{type.Name}' has both [Include] and [Ignore] (or [NonSerialized]) !");

			if (hasIgnore)
			{
				memberConfig
				return;
			}




			// Use [MemberConfig] on type

			// Use serializer config global default. todo: ensure the user can't change the base-settings of the config after once it is locked; lock config on the first call to ConfigType<>?

			// Success: persistent name (from attribute or normal member name)



			//
			// 1.) Use member-attribute




			//var attrib = memberInfo.GetCustomAttribute<PreviousNameAttribute>();
			//if (attrib != null)
			//{
			//	VerifyName(attrib.Name);
			//	foreach (var n in attrib.AlternativeNames)
			//		VerifyName(n);
			//}

			//var schemaMember = new SchemaMember(attrib?.Name ?? memberInfo.Name, serializedMember);


			////
			//// 2.) Use "targets" to determine whether or not to include something (type-level attribute -> global config default)
			//if (IsMatch(isField, isProp, isPublic, typeConfig.TargetMembers))
			//{
			//	schema.Members.Add(schemaMember);
			//	continue;
			//}
		}

		static bool ShouldSkipField(MemberConfig m)
		{
			// Skip readonly
			if (m.IsReadonlyField)
			{
				// Check attribute on member
				var handling = m.Member.GetCustomAttribute<ReadonlyConfigAttribute>(true);
				if (handling != null)
				{
					if (handling.ReadonlyFieldHandling == ReadonlyFieldHandling.ExcludeFromSerialization)
					{
						m.ExcludeWithReason("Field is readonly and has [ReadonlyConfig] set to exclude.");
						return true;
					}

					return false;
				}

				// Check attribute on type
				handling = m.Member.DeclaringType.GetCustomAttribute<ReadonlyConfigAttribute>(true);
				if (handling != null)
				{
					if (handling.ReadonlyFieldHandling == ReadonlyFieldHandling.ExcludeFromSerialization)
					{
						m.ExcludeWithReason("Field is readonly and the declaring type has [ReadonlyConfig] set to exclude.");
						return true;
					}

					return false;
				}

				// Check global default
				if (m.TypeConfig.Config.Advanced.ReadonlyFieldHandling == ReadonlyFieldHandling.ExcludeFromSerialization)
				{
					m.ExcludeWithReason("Field is readonly and the global default in the SerializerConfig is set to exclude.");
					return true;
				}

				return false;
			}

			return false;
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

			if (type.Assembly == typeof(Expression).Assembly)
			{
				if (!type.IsAbstract && type.IsSubclassOf(typeof(Expression)))
				{
					typeConfig.TypeConstruction = new UninitializedObject();
					typeConfig.ReadonlyFieldHandling = ReadonlyFieldHandling.ForcedOverwrite;
					typeConfig.TargetMembers = TargetMember.PrivateFields;
					return;
				}

				if (type.FullName.StartsWith("System.Runtime.CompilerServices.TrueReadOnlyCollection"))
				{
					typeConfig.TypeConstruction = new UninitializedObject();
					typeConfig.ReadonlyFieldHandling = ReadonlyFieldHandling.ForcedOverwrite;
					typeConfig.TargetMembers = TargetMember.PrivateFields;
					return;
				}
			}

			if (type.Assembly == typeof(ReadOnlyCollection<>).Assembly)
				if (type.FullName.StartsWith("System.Collections.ObjectModel.ReadOnlyCollection"))
				{
					typeConfig.TypeConstruction = new UninitializedObject();
					typeConfig.ReadonlyFieldHandling = ReadonlyFieldHandling.ForcedOverwrite;
					typeConfig.TargetMembers = TargetMember.PrivateFields;
					return;
				}
		}



	}
}
