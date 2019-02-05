using System;
using System.Collections.Generic;

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

	// Serialization Constructors:
	// todo: exception: "your Type ... has no default ctor; you can activate an automatic fallback to 'GetUninitializedObject' if you want"
	// todo: ensure that any given methods actually return the right object type, and ctors actually match the object type
	// todo: allow the worst case scenario (self ref with ctor) by using GetUninitializedObject, then reading and assigning, and then running the ctor after that!
	// todo: "You have a configuration error on type 'bla': You selected 'mode' to create new objects, but the formatter used by ceras to handle this type is not 'DynamicObjectFormatter' which is required for this mode to work. If this formatter is a custom (user-provided) formatter then you probably want to set the construction mode to either 'Null' so Ceras does not create an object and the formatter can handle it; or you can set it to 'Normal' so Ceras creates a new object instance using 'new()'. Generally: passing any arguments to functions can only work with the DynamicObjectFormatter."
	// todo: Compile some sort of 'EnsureInstance()' method that we can call for each specific type and can use directly in the ref-formatter-dispatcher.

	// Extra Features:
	// todo: Methods for: BeforeReadingMember, BeforeWritingMember, AfterReadingMember, ... BeforeReadingObject, AfterReadingObject, ...
	// todo: DiscardObject method
	// todo: SetReadonlyHandling
	// todo: CustomSchema (with a method to obtain a default schema given some settings)

	public class TypeConfigEntry
	{
		internal Type Type;

		internal ObjectConstructionMode ConstructionMode = ObjectConstructionMode.Normal_ParameterlessConstructor;
		internal Delegate UserDelegate;
		internal MethodInfo ObjectConstructionMethod;

		internal bool CtorHasParameters; // If it wants arguments we must defer construction to the DynamicObjectFormatter.


		internal TypeConfigEntry(Type type)
		{
			Type = type;
		}

		/// <summary>
		/// Create no instances when deserializing this type, the user-formatter will take care of somehow creating an object instance (or maybe re-using a potentially already existing instance)
		/// </summary>
		public TypeConfigEntry ConstructByFormatter()
		{
			// todo: can not be used with DynamicObjectFormatter; only with custom / user provided types.
			ConstructionMode = ObjectConstructionMode.Null_DeferToFormatter;
			return this;
		}

		/// <summary>
		/// Use '<see cref="System.Runtime.Serialization.FormatterServices.GetUninitializedObject(Type)"/>' to create instances of this type.
		/// This will make it possible to deal with stubborn objects which have no parameterless constructor!
		/// <para>!! Warning !!</para>
		/// Don't use this as an easy way to side-step any problems, there are two things to keep in mind with this:
		/// <list type="bullet">
		/// <item>
		/// <description>
		/// GetUninitializedObject is by its nature not very fast. Expect a ~6x slowdown when creating objects (note that creating objects is a very small part of the whole deserialization, but in some rare cases it might still impact you. So test it for yourself!)
		/// </description>
		/// </item>
		/// <item>
		/// <description>
		/// No constructor is executed, not even the default one. If the normal constructor of the object has some important side effects, then you'll run into trouble. Obviously this completely depends on the situation. And if you're looking into this option you probably already know what you're getting yourself into...
		/// </description>
		/// </item>
		/// </list>
		/// </summary>
		public TypeConfigEntry ConstructByUninitialized()
		{
			ConstructionMode = ObjectConstructionMode.None_GetUninitializedObject;
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

		/// <summary>
		/// Helper method that redirects to <see cref="ConstructBy(MethodInfo)"/> by extracting the <see cref="MethodInfo"/> from the given expression
		/// </summary>
		public TypeConfigEntry ConstructBy<T>(Expression<Func<T>> methodSelectExpression)
		{
			var method = ((MethodCallExpression)methodSelectExpression.Body).Method;

			if (!method.IsStatic)
				throw new InvalidOperationException("The given method is an instance-method, but you did not provide an instance.");

			ConstructionMode = ObjectConstructionMode.User_StaticMethod;
			ObjectConstructionMethod = method;

			return this;
		}

		/// <summary>
		/// Helper method that redirects to <see cref="ConstructBy(object, MethodInfo)"/> by extracting the <see cref="MethodInfo"/> from the given expression
		/// </summary>
		public TypeConfigEntry ConstructBy<T>(object instance, Expression<Func<T>> methodSelectExpression)
		{
			var method = ((MethodCallExpression)methodSelectExpression.Body).Method;

			if (method.IsStatic)
				throw new InvalidOperationException("The given method is a static method, but you have provided an instance that is not needed.");

			ConstructionMode = ObjectConstructionMode.User_StaticMethod;
			ObjectConstructionMethod = method;

			return this;
		}
	}


	// New, alternative approach
	class TypeConfigEntry2
	{
	}

	public abstract class Construct
	{
		// bool that tells whether or not to use in ref or dyn.
		// generate expression trees for ref and dyn formatters.

		
		public static Construct Null()                                         => new NullConstructionMode();
		public static Construct ByStaticMethod(MethodInfo     methodInfo)      => new StaticMethodConstructionMode { StaticMethod = methodInfo };
		public static Construct ByConstructor(ConstructorInfo constructorInfo) => new ConstructorConstructionMode { Constructor   = constructorInfo };
	}

	class StaticMethodConstructionMode : Construct
	{
		public MethodInfo StaticMethod;
	}
	
	class ConstructorConstructionMode : Construct
	{
		public ConstructorInfo Constructor;
	}

	class NullConstructionMode : Construct
	{
	}

	enum ObjectConstructionMode
	{
		Null_DeferToFormatter, // Don't construct an instance, let the formatter handle it
		None_GetUninitializedObject, // Use 'GetUninitializedObject', should only be used as a last resort because it's slow and will not call a constructor
		Normal_ParameterlessConstructor, // Use parameterless constructor 'new()'
		SpecificConstructor, // Some constructor that is not the default constructor

		User_InstanceMethod, // The user has given us a specific method and a matching object on which we will call the method
		User_StaticMethod, // The user has given us some static method which will take care of everything
		User_Delegate, // A user provided delegate will instantiate any objects
	}
}
