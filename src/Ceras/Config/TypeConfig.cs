using System;
using System.Collections.Generic;

namespace Ceras
{
	using Ceras.Formatters;
	using Ceras.Helpers;
	using System.Linq;
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
	// todo: when deferring to the dynamic object formatter, everything has to be done there: checking if an instance already exists, if it's the right type, and possibly discard it using the discard method

	// Extra Features:
	// todo: Methods for: BeforeReadingMember, BeforeWritingMember, AfterReadingMember, ... BeforeReadingObject, AfterReadingObject, ...
	// todo: DiscardObject method
	// todo: SetReadonlyHandling
	// todo: CustomSchema (with a method to obtain a default schema given some settings)
	// todo: If we create something from uninitialized; do we give an option to run some specific ctor? Do we write some props/fields again after calling the ctor??
	// todo: what about just calling Dispose() as an alternative to Discard!?

	public class TypeConfigEntry
	{
		internal Type Type;

		internal TypeConstruction TypeConstruction;


		internal TypeConfigEntry(Type type)
		{
			Type = type;

			var ctor = type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

			if (ctor != null)
				TypeConstruction = TypeConstruction.ByConstructor(ctor);
			else
				TypeConstruction = null;
		}


		public TypeConfigEntry ConstructBy(MethodInfo methodInfo)
		{
			TypeConstruction = new ConstructByMethod(methodInfo);

			TypeConstruction.TypeConfigEntry = this;
			TypeConstruction.VerifyReturnType();

			return this;
		}

		public TypeConfigEntry ConstructBy(object instance, MethodInfo methodInfo)
		{
			TypeConstruction = new ConstructByMethod(instance, methodInfo);

			TypeConstruction.TypeConfigEntry = this;
			TypeConstruction.VerifyReturnType();

			return this;
		}

		public TypeConfigEntry ConstructBy(ConstructorInfo constructorInfo)
		{
			TypeConstruction = new SpecificConstructor(constructorInfo);

			TypeConstruction.TypeConfigEntry = this;
			TypeConstruction.VerifyReturnType();

			return this;
		}

		public TypeConfigEntry ConstructByDelegate(Func<object> factory)
		{
			// Delegates get deconstructed into target+method automatically
			var instance = factory.Target;
			var method = factory.Method;

			TypeConstruction = new ConstructByMethod(instance, method);

			TypeConstruction.TypeConfigEntry = this;
			TypeConstruction.VerifyReturnType();

			return this;
		}


		public TypeConfigEntry ConstructBy(Expression<Func<object>> methodSelectExpression)
		{
			return ConstructBy(instance: null, methodSelectExpression);
		}

		/// <summary>
		/// Use this overload to select a static method or constructor to use to create instance
		/// </summary>
		public TypeConfigEntry ConstructBy(object instance, Expression<Func<object>> methodSelectExpression)
		{
			var body = methodSelectExpression.Body;

			if (body is NewExpression newExpression)
			{
				if (instance != null)
					throw new InvalidOperationException("You can't specify a constructor and an instance at the same time");

				var ctor = newExpression.Constructor;
				TypeConstruction = new SpecificConstructor(ctor);
			}
			else if (body is MethodCallExpression methodCall)
			{
				if (instance == null)
					TypeConstruction = new ConstructByMethod(methodCall.Method);
				else
					TypeConstruction = new ConstructByMethod(instance, methodCall.Method);
			}
			else
			{
				throw new InvalidOperationException("The given expression must be a 'method-call' or 'new-expression'");
			}

			TypeConstruction.TypeConfigEntry = this;
			TypeConstruction.VerifyReturnType();

			return this;
		}


		/// <summary>
		/// Use this to tell Ceras how it is supposed to construct new objects when deserializing. By default it will use the parameterless constructor (doesn't matter if public or private)
		/// </summary>
		public TypeConfigEntry ConstructBy(TypeConstruction manualConstructConfig)
		{
			TypeConstruction.TypeConfigEntry = this;
			TypeConstruction = manualConstructConfig;
			return this;
		}

		/*

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

		*/
	}

	/// <summary>
	/// Use the static factory methods like <see cref="ByConstructor(ConstructorInfo)"/> to create instances
	/// </summary>
	public abstract class TypeConstruction
	{
		internal TypeConfigEntry TypeConfigEntry; // Not as clean as I'd like, this can't be set from a protected base ctor, because users might eventually want to create their own

		internal abstract bool HasDataArguments { get; }
		internal abstract Func<object> GetRefFormatterConstructor();

		internal virtual void EmitConstruction(Schema schema, List<Expression> body, ParameterExpression refValueArg, HashSet<ParameterExpression> usedVariables, Formatters.MemberParameterPair[] memberParameters)
		{
			throw new NotImplementedException("This construction type can not be used in deferred mode.");
		}

		// todo: Sanity checks:
		// - does the given delegate/ctor/method even return an instance of the thing we need?
		// - can all the members be mapped correctly from FieldName/PropertyName to ParameterName?
		internal virtual void VerifyReturnType()
		{ }

		protected void VerifyMethodReturn(MethodBase methodBase, Type intendedReturnType)
		{
			if (methodBase is MethodInfo m)
			{
				var r = m.ReturnType;
				var actual = intendedReturnType;
				// todo: ...
			}
		}

		// What is needed for the dynamic object formatter?
		// - it needs to know which of its locals it needs to pass, and in what order
		// - it needs to know whether to emit a NewExpression or MethodCallExpression


		#region Factory Methods

		public static TypeConstruction Null() => new ConstructNull();

		public static TypeConstruction ByStaticMethod(MethodInfo methodInfo) => new ConstructByMethod(methodInfo);
		public static TypeConstruction ByStaticMethod(Expression<Func<object>> expression) => new ConstructByMethod(((MethodCallExpression)expression.Body).Method);

		public static TypeConstruction ByConstructor(ConstructorInfo constructorInfo) => new SpecificConstructor(constructorInfo);

		#endregion
	}


	// Aka "FormatterConstructed"
	class ConstructNull : TypeConstruction
	{
		internal override bool HasDataArguments => false;
		internal override Func<object> GetRefFormatterConstructor() => () => null;
	}
	
	class SpecificConstructor : TypeConstruction
	{
		internal ConstructorInfo Constructor;

		public SpecificConstructor(ConstructorInfo constructor)
		{
			Constructor = constructor;
		}

		internal override bool HasDataArguments => Constructor.GetParameters().Length > 0;
		internal override Func<object> GetRefFormatterConstructor()
		{
			return Expression.Lambda<Func<object>>(Expression.New(Constructor)).Compile();
		}

		internal static Expression[] ConfigureArguments(ParameterInfo[] targetMethodParamters, Schema schema, HashSet<ParameterExpression> usedVariables, Formatters.MemberParameterPair[] memberParameters)
		{
			Expression[] args = new Expression[targetMethodParamters.Length];

			// Figure out what local goes into what parameter slot of the given method
			for (int i = 0; i < args.Length; i++)
			{
				// Parameter -> SerializedMember (either by automatically mapping by name, or using a user provided lookup)
				var parameterName = targetMethodParamters[i].Name;
				var schemaMember = schema.Members.FirstOrDefault(m => parameterName.Equals(m.Member.Name, StringComparison.OrdinalIgnoreCase) || parameterName.Equals(m.PersistentName, StringComparison.OrdinalIgnoreCase));

				// todo: try mapping by type as well (only works when there's exactly one type)
				// todo: remove prefixes like "_" or "m_" from member names

				if (schemaMember.Member.MemberInfo == null || schemaMember.IsSkip) // Not found, or current schema does not contain this data member
				{
					throw new InvalidOperationException($"Can not construct type '{schema.Type.FullName}' using the selected constructor (or method) because the parameter '{parameterName}' can not be automatically mapped to any of the members in the current schema. Please provide a custom mapping for this constructor or method in the serializer config.");
				}

				// SerializedMember -> ParameterExpression
				var paramExp = memberParameters.First(m => m.Member == schemaMember.Member.MemberInfo);
				
				// Use as source in call
				args[i] = paramExp.LocalVar;

				// Mark as consumed
				usedVariables.Add(paramExp.LocalVar);
			}

			return args;
		}

		internal override void EmitConstruction(Schema schema, List<Expression> body, ParameterExpression refValueArg, HashSet<ParameterExpression> usedVariables, Formatters.MemberParameterPair[] memberParameters)
		{
			var parameters = Constructor.GetParameters();
			var args = ConfigureArguments(parameters, schema, usedVariables, memberParameters);

			var invocation = Expression.Assign(refValueArg, Expression.New(Constructor, args));
			body.Add(invocation);
		}
	}

	class ConstructByMethod : TypeConstruction
	{
		internal readonly MethodInfo Method;
		internal readonly object TargetObject;

		internal ConstructByMethod(MethodInfo staticMethod)
		{
			if (!staticMethod.IsStatic)
				throw new InvalidOperationException($"You have provided an instance method without a target object");

			Method = staticMethod;
		}

		internal ConstructByMethod(object targetObject, MethodInfo instanceMethod)
		{
			if (instanceMethod.IsStatic)
				throw new InvalidOperationException("You have provided target-instance but the given method is a static method");
			if (targetObject == null)
				throw new ArgumentNullException(nameof(targetObject), "The given method requires an instance (a targetObject), but you have given 'null'");

			Method = instanceMethod;
			TargetObject = targetObject;
		}


		internal override bool HasDataArguments => Method.GetParameters().Length > 0;
		internal override Func<object> GetRefFormatterConstructor()
		{
			if (Method.IsStatic)
				return (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), Method);
			else
				return (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), TargetObject, Method);
		}

		internal override void EmitConstruction(Schema schema, List<Expression> body, ParameterExpression refValueArg, HashSet<ParameterExpression> usedVariables, Formatters.MemberParameterPair[] memberParameters)
		{
			var parameters = Method.GetParameters();
			var args = SpecificConstructor.ConfigureArguments(parameters, schema, usedVariables, memberParameters);

			Expression invocation;
			if(Method.IsStatic)
				invocation = Expression.Assign(refValueArg, Expression.Call(Method, args));
			else
				invocation = Expression.Assign(refValueArg, Expression.Call(instance: Expression.Constant(TargetObject), method: Method, args));
			
			body.Add(invocation);
		}

	}


	class UninitializedObject : TypeConstruction
	{
		static MethodInfo _getUninitialized;

		// todo: cool ideas:
		// - after constructing an uninitialized object we can do some trickery to run any ctor on it afterwards
		// - and maybe after the ctor we'd overwrite some members again (if the ctor messed something up)
		ConstructorInfo _directConstructor;
		bool _writeMembersAgain;

		public UninitializedObject()
		{
			// We don't want exceptions in static ctors, that's why this is in the normal ctor
			if (_getUninitialized == null)
			{
				Expression<Func<object>> exp = () => System.Runtime.Serialization.FormatterServices.GetUninitializedObject(null);
				_getUninitialized = ((MethodCallExpression)exp.Body).Method;
			}
		}

		internal override bool HasDataArguments => false;// Constructor.GetParameters().Length > 0;
		internal override Func<object> GetRefFormatterConstructor()
		{
			var t = TypeConfigEntry.Type;

			// todo: can be optimized a lot by bypassing some clr checks... but it's very rarely used / needed
			return Expression.Lambda<Func<object>>(Expression.Call(_getUninitialized, arg0: Expression.Constant(t))).Compile();
		}
	}
}
