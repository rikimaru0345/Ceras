using Ceras.Formatters;
using Ceras.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Ceras
{
	using Exceptions;

	public abstract class TypeConstruction
	{
		internal TypeConfig TypeConfig; // Not as clean as I'd like, this can't be set from a protected base ctor, because users might eventually want to create their own

		internal abstract bool HasDataArguments { get; }
		internal abstract Func<object> GetRefFormatterConstructor(bool allowDynamicCodeGen);

		internal virtual void EmitConstruction(Schema schema, List<Expression> body, ParameterExpression refValueArg, HashSet<ParameterExpression> usedVariables, MemberParameterPair[] memberParameters)
		{
			throw new NotImplementedException("This construction type can not be used in deferred mode.");
		}

		internal virtual void VerifyReturnType() { }

		internal virtual void VerifyParameterMapping() { }


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

		protected void VerifyParameterMapping(MethodBase methodBase)
		{
			var parameters = methodBase.GetParameters();
			if (parameters.Length == 0)
				return;

			var map = TypeConfig.ParameterMap ?? (TypeConfig.ParameterMap = new Dictionary<ParameterInfo, MemberInfo>());

			var memberConfigs = TypeConfig.Members.Where(mc => mc.ComputeFinalInclusionFast()).ToArray();

			foreach (var parameterInfo in parameters)
			{
				// Are we still missing a mapping for this member?
				if (!map.TryGetValue(parameterInfo, out MemberInfo sourceMember))
				{
					var caseInsensitiveMatches = memberConfigs.Where(mc => parameterInfo.Name.Equals(mc.Member.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
					if (SetMatchOrThrow(parameterInfo, caseInsensitiveMatches))
						continue;
					
					var tolerantMatches = memberConfigs.Where(mc => parameterInfo.Name.Equals(MiscHelpers.CleanMemberName(mc.Member.Name), StringComparison.OrdinalIgnoreCase)).ToArray();
					if (SetMatchOrThrow(parameterInfo, tolerantMatches))
						continue;

					var ctorParamStrings = string.Join(", ", parameters.Select(t => t.ParameterType.FriendlyName() + " " + t.Name));
					throw new CerasException($"There is no mapping specified from the members of '{TypeConfig.Type.FriendlyName()}' to the constructor '({ctorParamStrings})'. Ceras has tried to automatically detect a mapping by matching the names of the fields/properties to the method parameters, but no source field or property could be found to populate the parameter '{parameterInfo.ParameterType.FriendlyName()} {parameterInfo.Name}'");
				}
				else
				{
					// We already have a user-provided match, but is it part of the serialization?
					var sourceMemberConfig = TypeConfig.Members.First(mc => mc.Member == sourceMember);
					if (!sourceMemberConfig.ComputeFinalInclusionFast())
						throw new CerasException($"The construction mode for the type '{TypeConfig.Type.FriendlyName()}' is invalid because the parameter '{parameterInfo.ParameterType.FriendlyName()} {parameterInfo.Name}' is supposed to be initialized from the member '{sourceMember.FieldOrPropType().FriendlyName()} {sourceMember.Name}', but that member is not part of the serialization, so it will not be available at deserialization-time.");
				}
			}

			bool SetMatchOrThrow(ParameterInfo p, MemberConfig[] configs)
			{
				if (configs.Length > 1)
					throw new AmbiguousMatchException($"There are multiple members that match the parameter '{p.ParameterType.FriendlyName()} {p.Name}': {string.Join(", ", configs.Select(c => c.Member.Name))}");

				if(configs.Length == 0)
					return false;

				map.Add(p, configs[0].Member);
				return true;
			}
		}



		#region Factory Methods

		public static TypeConstruction Null() => ConstructNull.Instance;

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
		public static ConstructNull Instance { get; } = new ConstructNull();

		ConstructNull() { }

		internal override bool HasDataArguments => false;
		internal override Func<object> GetRefFormatterConstructor(bool allowDynamicCodeGen) => () => null;
	}

	abstract class MethodBaseConstruction : TypeConstruction
	{
		protected Expression[] GenerateArgumentExpressions(ParameterInfo[] targetMethodParameters, Schema schema, HashSet<ParameterExpression> usedVariables, MemberParameterPair[] memberParameters)
		{
			Expression[] finalArgExpressions = new Expression[targetMethodParameters.Length];

			// Parameter -> ParameterMap -> SchemaMembers -> MemberExpression
			for (int i = 0; i < targetMethodParameters.Length; i++)
			{
				var parameter = targetMethodParameters[i];
				var sourceMember = TypeConfig.ParameterMap[parameter];
				var schemaMember = schema.Members.First(m => m.MemberInfo == sourceMember);
				
				if (schemaMember.IsSkip) // Not found, or current schema does not contain this data member
					throw new InvalidOperationException($"Can not generate the constructor-call or call to the factory method for type '{schema.Type.FullName}'. The parameter '{parameter.Name}' is not part of the serialization / serialized data.");
				
				// SerializedMember -> ParameterExpression
				var paramExp = memberParameters.First(m => m.Member == schemaMember.MemberInfo);

				// Use as source in call
				finalArgExpressions[i] = paramExp.LocalVar;

				// And mark as consumed
				usedVariables.Add(paramExp.LocalVar);
			}

			return finalArgExpressions;
		}
	}

	class SpecificConstructor : MethodBaseConstruction
	{
		internal ConstructorInfo Constructor;

		public SpecificConstructor(ConstructorInfo constructor)
		{
			Constructor = constructor;
		}

		internal override bool HasDataArguments => Constructor.GetParameters().Length > 0;
		internal override Func<object> GetRefFormatterConstructor(bool allowDynamicCodeGen)
		{
			if(allowDynamicCodeGen)
				return Expression.Lambda<Func<object>>(Expression.New(Constructor)).Compile();

			return () => Constructor.Invoke(null);
		}

		internal override void EmitConstruction(Schema schema, List<Expression> body, ParameterExpression refValueArg, HashSet<ParameterExpression> usedVariables, Formatters.MemberParameterPair[] memberParameters)
		{
			var parameters = Constructor.GetParameters();
			var args = GenerateArgumentExpressions(parameters, schema, usedVariables, memberParameters);

			var invocation = Expression.Assign(refValueArg, Expression.New(Constructor, args));
			body.Add(invocation);
		}

		internal override void VerifyReturnType() => VerifyMethodReturn(Constructor);
		internal override void VerifyParameterMapping() => VerifyParameterMapping(Constructor);
	}

	class ConstructByMethod : MethodBaseConstruction
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
		internal override Func<object> GetRefFormatterConstructor(bool allowDynamicCodeGen)
		{
			if (Method.IsStatic)
				return (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), Method);
			else
				return (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), TargetObject, Method);
		}

		internal override void EmitConstruction(Schema schema, List<Expression> body, ParameterExpression refValueArg, HashSet<ParameterExpression> usedVariables, Formatters.MemberParameterPair[] memberParameters)
		{
			var parameters = Method.GetParameters();
			var args = GenerateArgumentExpressions(parameters, schema, usedVariables, memberParameters);

			Expression invocation;
			if (Method.IsStatic)
				invocation = Expression.Assign(refValueArg, Expression.Call(Method, args));
			else
				invocation = Expression.Assign(refValueArg, Expression.Call(instance: Expression.Constant(TargetObject), method: Method, args));

			body.Add(invocation);
		}

		internal override void VerifyReturnType() => VerifyMethodReturn(Method);
		internal override void VerifyParameterMapping() => VerifyParameterMapping(Method);
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

		internal override Func<object> GetRefFormatterConstructor(bool allowDynamicCodeGen)
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
