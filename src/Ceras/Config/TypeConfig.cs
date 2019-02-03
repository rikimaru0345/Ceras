using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras
{
	using System.Linq.Expressions;
	using System.Reflection;

	class TypeConfiguration
	{
		Dictionary<Type, TypeConfigEntry> _configEntries = new Dictionary<Type, TypeConfigEntry>();
		
		internal TypeConfigEntry GetOrCreate(Type type)
		{
			if (_configEntries.TryGetValue(type, out var typeConfig))
				return typeConfig;

			typeConfig = new TypeConfigEntry(type);
			_configEntries.Add(type, typeConfig);

			return typeConfig;
		}
	}

	// todo: Methods for: BeforeReadingMember, BeforeWritingMember, AfterReadingMember, ... BeforeReadingObject, AfterReadingObject, ...
	// todo: DiscardObject method
	// todo: SetReadonlyHandling
	// todo: CustomSchema (with a method to obtain a default schema given some settings)
	// todo: setting "obj has no default ctor; you can activate an automatic fallback to 'GetUninitializedObject'"
	// todo: ensure that any given methods actually return the right object type, and ctors actually match the object type
	// todo: allow the worst case scenario (self ref with ctor) by using GetUninitializedObject, then reading and assigning, and then running the ctor after that!
	public class TypeConfigEntry
	{
		internal Type Type;

		internal ObjectConstructionMode ConstructionMode = ObjectConstructionMode.Normal_ParameterlessConstructor;
		internal Delegate UserDelegate;
		internal MethodInfo ObjectConstructionMethod;

		internal TypeConfigEntry(Type type)
		{
			Type = type;
		}

		public TypeConfigEntry ConstructByFormatter()
		{
			ConstructionMode = ObjectConstructionMode.None_DeferToFormatter;
			return this;
		}

		public TypeConfigEntry ConstructBy(MethodInfo staticMethod)
		{
			ConstructionMode = ObjectConstructionMode.User_StaticMethod;
			return this;
		}

		public TypeConfigEntry ConstructBy(object instance, MethodInfo instanceMethod)
		{
			ConstructionMode = ObjectConstructionMode.User_InstanceMethod;
			return this;
		}

		public TypeConfigEntry ConstructBy(ConstructorInfo constructor)
		{
			ConstructionMode = ObjectConstructionMode.SpecificConstructor;
			return this;
		}

		public TypeConfigEntry ConstructBy(Delegate @delegate)
		{
			ConstructionMode = ObjectConstructionMode.User_Delegate;
			return this;
		}
	}

	public static class Extensions
	{
		public static TypeConfigEntry ConstructBy<T>(this TypeConfigEntry entry, Expression<Func<T>> methodSelectExpression)
		{
			var method = ((MethodCallExpression) methodSelectExpression.Body).Method;

			if (!method.IsStatic)
				throw new InvalidOperationException("The given method is an instance-method, but you did not provide an instance.");

			entry.ConstructionMode = ObjectConstructionMode.User_StaticMethod;
			entry.ObjectConstructionMethod = method;

			return entry;
		}
	}

	public enum ObjectConstructionMode
	{
		None_DeferToFormatter, // Don't construct an instance, let the formatter handle it
		Normal_ParameterlessConstructor, // Use parameterless constructor 'new()'
		SpecificConstructor, // Some constructor that is not the default constructor
		NoConstructor, // Use 'GetUninitializedObject', should only be used as a last resort because it's slow and will not call a constructor

		User_InstanceMethod, // The user has given us a specific method and a matching object on which we will call the method
		User_StaticMethod, // The user has given us some static method which will take care of everything
		User_Delegate, // A user provided delegate will instantiate any objects
	}
}
