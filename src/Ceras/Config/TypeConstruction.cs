using Ceras.Formatters;
using Ceras.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Ceras
{
	public abstract class TypeConstruction
	{
		internal TypeConfig TypeConfig; // Not as clean as I'd like, this can't be set from a protected base ctor, because users might eventually want to create their own

		internal abstract bool HasDataArguments { get; }
		internal abstract Func<object> GetRefFormatterConstructor();

		internal virtual void EmitConstruction(Schema schema, List<Expression> body, ParameterExpression refValueArg, HashSet<ParameterExpression> usedVariables, Formatters.MemberParameterPair[] memberParameters)
		{
			throw new NotImplementedException("This construction type can not be used in deferred mode.");
		}

		// todo: Sanity checks:
		// - does the given delegate/ctor/method even return an instance of the thing we need?
		// - can all the members be mapped correctly from FieldName/PropertyName to ParameterName? -> unfortunately this can't actually be done early. The schema we use can be different (reading with version tolerance)
		internal virtual void VerifyReturnType()
		{ }

		protected void VerifyMethodReturn(MethodBase methodBase)
		{
			if (methodBase.IsAbstract)
				throw new InvalidOperationException($"The given method '{methodBase.Name}' is abstract so it can not be used to construct anything.");

			// Example:
			// object -> Animal -> Cat
			// Ctor gives us a cat, we need an animal -> everything ok

			Type resultType;

			if (methodBase is MethodInfo m)
			{
				resultType = m.ReturnType;
			}
			else if (methodBase is ConstructorInfo c)
			{
				resultType = c.DeclaringType; // the "result" of a ctor is its declaring type
			}
			else
				throw new NotImplementedException("this helper method cannot handle a member info of type " + methodBase.GetType().FullName);

			// Can we use the result to assign it to the needed type?
			var neededType = TypeConfig.Type;

			if (!neededType.IsAssignableFrom(resultType))
				throw new InvalidOperationException($"The given method or constructor returns a '{resultType.FullName}' which is not compatible to the needed type '{neededType.FullName}'");
		}


		#region Factory Methods

		public static TypeConstruction Null() => new ConstructNull();

		public static TypeConstruction ByStaticMethod(MethodInfo methodInfo) => new ConstructByMethod(methodInfo);
		public static TypeConstruction ByStaticMethod(Expression<Func<object>> expression) => new ConstructByMethod(((MethodCallExpression)expression.Body).Method);

		public static TypeConstruction ByConstructor(ConstructorInfo constructorInfo) => new SpecificConstructor(constructorInfo);

		public static TypeConstruction ByUninitialized() => new UninitializedObject();

		// public static TypeConstruction ByUninitialized(ConstructorInfo directCtor) => ...;
		// public static TypeConstruction ByCombination() ... // allow to specify what should happen if there's an object (reset it? or just overwrite?) and how to create a new one


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
				var schemaMember = schema.Members.FirstOrDefault(m => parameterName.Equals(m.MemberName, StringComparison.OrdinalIgnoreCase) || parameterName.Equals(m.PersistentName, StringComparison.OrdinalIgnoreCase));

				// todo: try mapping by type as well (only works when there's exactly one type)
				// todo: remove prefixes like "_" or "m_" from member names

				if (schemaMember.MemberInfo == null || schemaMember.IsSkip) // Not found, or current schema does not contain this data member
				{
					throw new InvalidOperationException($"Can not construct type '{schema.Type.FullName}' using the selected constructor (or method) because the parameter '{parameterName}' can not be automatically mapped to any of the members in the current schema. Please provide a custom mapping for this constructor or method in the serializer config.");
				}

				// SerializedMember -> ParameterExpression
				var paramExp = memberParameters.First(m => m.Member == schemaMember.MemberInfo);

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

		internal override void VerifyReturnType()
		{
			VerifyMethodReturn(Constructor);
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
			if (Method.IsStatic)
				invocation = Expression.Assign(refValueArg, Expression.Call(Method, args));
			else
				invocation = Expression.Assign(refValueArg, Expression.Call(instance: Expression.Constant(TargetObject), method: Method, args));

			body.Add(invocation);
		}

		internal override void VerifyReturnType()
		{
			VerifyMethodReturn(Method);
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

		internal override bool HasDataArguments => _directConstructor != null && _directConstructor.GetParameters().Length > 0;

		internal override Func<object> GetRefFormatterConstructor()
		{
			// todo: There are a lot of hardcore tricks to improve performance here. But for now it would be wasted time since the feature will (probably) be used very rarely.
			var t = TypeConfig.Type;

			return Expression.Lambda<Func<object>>(Expression.Call(_getUninitialized, arg0: Expression.Constant(t))).Compile();
		}

		internal override void VerifyReturnType()
		{
			if (_directConstructor != null)
				if (_directConstructor.DeclaringType != base.TypeConfig.Type)
					throw new InvalidOperationException($"The given constructor is not part of the type '{base.TypeConfig.Type.FullName}'");
		}

		internal override void EmitConstruction(Schema schema, List<Expression> body, ParameterExpression refValueArg, HashSet<ParameterExpression> usedVariables, MemberParameterPair[] memberParameters)
		{
			throw new NotImplementedException("running a ctor or factory is not yet supported in this mode");
		}

	}

	// todo: a construction method that expects an object to be already present but then runs a given ctor anyway. Maybe also resetting all members and private vars to 'default(T)' ?
	abstract class ResetAndReuse : TypeConstruction
	{
		// option: load data from existing object into locals
		// option: reset all fields to default (including compiler generated), in definition order
		// option: call a specific ctor
		// option: write selected members again after calling the ctor
	}

	// todo: combines two construct methods. For example something that can somehow reuse an existing object, or fallback to somehow creating a new object when needed
	abstract class CoalesceConstruction : TypeConstruction
	{
		// Try reuse (overwrite, reset, direct-ctor, ...)
		// Or create custom (new(), factory, ...)
	}

}
