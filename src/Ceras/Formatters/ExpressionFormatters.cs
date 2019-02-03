namespace Ceras.Formatters
{
	using Resolvers;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;

	class ExpressionFormatterResolver : IFormatterResolver
	{
		readonly CerasSerializer _ceras;
		readonly ExpressionFormatter _expressionFormatter;

		readonly LabelTargetFormatter _labelTargetFormatter;
		readonly LabelFormatter _labelFormatter;

		readonly MemberAssignmentFormatter _memberAssignmentFormatter;
		readonly MemberListBindingFormatter _memberListBindingFormatter;
		readonly MemberMemberBindingFormatter _memberMemberBindingFormatter;

		readonly Dictionary<Type, IFormatter> _genericLambdaFormatters = new Dictionary<Type, IFormatter>();


		public ExpressionFormatterResolver(CerasSerializer ceras)
		{
			_ceras = ceras;

			_expressionFormatter = new ExpressionFormatter();

			_labelTargetFormatter = new LabelTargetFormatter();
			_labelFormatter = new LabelFormatter();

			_memberAssignmentFormatter = new MemberAssignmentFormatter();
			_memberListBindingFormatter = new MemberListBindingFormatter();
			_memberMemberBindingFormatter = new MemberMemberBindingFormatter();
		}

		public IFormatter GetFormatter(Type type)
		{
			if (type == typeof(LabelTarget))
				return _labelTargetFormatter;

			if (type == typeof(LabelFormatter))
				return _labelFormatter;

			if (type == typeof(MemberAssignment))
				return _memberAssignmentFormatter;

			if (type == typeof(MemberListBinding))
				return _memberListBindingFormatter;

			if (type == typeof(MemberMemberBinding))
				return _memberMemberBindingFormatter;

			if (type.IsGenericType)
			{
				// todo: handle generic lambda expression in specialized formatter
			}


			if (type.IsSubclassOf(typeof(Expression)))
			{
				// No type derived from Expression can be instantiated normally
				CerasSerializer.AddFormatterConstructedType(type);

				return _expressionFormatter;
			}

			return null;
		}
	}


	// What about 'Constant'? For simple values it should be np, but for references we might pull in all sorts of wild nonsense.

	// Idea:
	// We take all the node names in the "ExpressionType" enum, then lookup the identically named static methods to construct them.
	// We always take the method with the most parameters so we can be sure no information gets lost.
	// From there we check out all the parameters of those methods and map them to the actual properties.
	// -> That's what's being done in the ctor

	class ExpressionFormatter : IFormatter<Expression>
	{
		static readonly HashSet<ExpressionType> _ignoredTypes = new HashSet<ExpressionType>
		{
			ExpressionType.Label, // Handled directly in LabelFormatter
			ExpressionType.Extension, // We won't handle user-defined at the moment
			ExpressionType.RuntimeVariables, // dynamic/eval... nope!
			ExpressionType.Dynamic,
		};

		static readonly Dictionary<string, string> _nameCorrections = new Dictionary<string, string>
		{
			{"Conditional", "Condition"},
			{"MemberAccess", "MakeMemberAccess"},
			{"Index", "MakeIndex"},
			{"Try", "MakeTry"},
		};

		static readonly Dictionary<ExpressionType, Expression<Func<object>>> _selectedMethods;


		static ExpressionFormatter()
		{
			var allNodeTypes = Enum.GetValues(typeof(ExpressionType)).Cast<ExpressionType>().ToArray();
			var constructionMethods = typeof(Expression).GetMethods(BindingFlags.Static | BindingFlags.Public);

			Type tType = null;
			MethodInfo tMethod = null;
			ConstructorInfo tCtor = null;
			Expression tExpr = null;
			NewExpression tNewExpr = null;
			IEnumerable<Expression> tExprs = null;
			IEnumerable<MemberInfo> tMembers = null;
			IEnumerable<ParameterExpression> tParams = null;

			_selectedMethods = new Dictionary<ExpressionType, Expression<Func<object>>>
			{
				{
					ExpressionType.New,
					() => Expression.New(tCtor, tExprs, tMembers)
				},
				{
					ExpressionType.NewArrayInit,
					() => Expression.NewArrayInit(tType, tExprs)
				},
				{
					ExpressionType.NewArrayBounds,
					() => Expression.NewArrayBounds(tType, tExprs)
				},

				{
					ExpressionType.Block,
					() => Expression.Block(tType, tParams, tExprs)
				},
				{
					ExpressionType.Call,
					() => Expression.Call(tExpr, tMethod, tExprs)
				},
				{
					ExpressionType.Invoke,
					() => Expression.Invoke(tExpr, tExprs)
				},

				{
					ExpressionType.ArrayIndex,
					() => Expression.ArrayIndex(tExpr, tExprs)
				},
				{
					ExpressionType.ListInit,
					() => Expression.ListInit(tNewExpr, tMethod, tExprs)
				},
				{
					ExpressionType.MemberInit,
					() => Expression.MemberInit(tNewExpr, (IEnumerable<MemberBinding>)null)
				},
				{
					ExpressionType.Switch,
					() => Expression.NewArrayInit(tType, tExprs)
				},

				{
					ExpressionType.Lambda,
					() => Expression.Lambda(tType, tExpr, "", false, tParams)
				},
			};

			MethodInfo NodeTypeToMethod(ExpressionType nodeType)
			{
				// Manually banned
				if (_ignoredTypes.Contains(nodeType))
					return null;

				// Manually pre-selected
				if (_selectedMethods.TryGetValue(nodeType, out var expr))
					return ((MethodCallExpression)expr.Body).Method;

				// Heuristic based on name
				var name = nodeType.ToString();
				if (_nameCorrections.TryGetValue(name, out var correctedName))
					name = correctedName;

				var matches = constructionMethods
							 .Where(m => m.Name == name)
							 .OrderByDescending(m => m.GetParameters().Length)
							 .ToArray();

				// Sanity check heuristic results
				if (matches.Length >= 2)
				{
					// Two methods that have the same number of arguments? -> Need manual selection
					if (matches[0].GetParameters().Length == matches[1].GetParameters().Length)
						throw new Exception("same number of args");

					// If any of them have have IEnumerable we have to manually select, it's possible that there are
					// overloads for 1, 2, 3, and 4 elements but more will use an IEnumerable variant
					if (matches.Any(m => m.GetParameters().Any(p => p.ParameterType.Name.Contains("IEnumerable"))))
						throw new Exception("overload contains IEnumerable");
				}

				return matches.First();
			}


			var matchingMethods = allNodeTypes
								 .Select(NodeTypeToMethod)
								 .Where(x => x != null)
								 .ToArray();

			var friendlyDisplay = matchingMethods.Select(m =>
			                                      {
													  var args = m.GetParameters();
													  var argsFormatted = args.Select(a => $"{a.ParameterType.Name} {a.Name}");
													  var argsStr = string.Join(", ", argsFormatted);
													  return $"{m.Name}({argsStr})";
												  })
												 .ToArray();

		}

		//
		// Instance
		//
		IFormatter<byte> _byteFormatter;
		
		public void Serialize(ref byte[] buffer, ref int offset, Expression exp)
		{
			byte nt = (byte) exp.NodeType;
			_byteFormatter.Serialize(ref buffer, ref offset, nt);


		}

		public void Deserialize(byte[] buffer, ref int offset, ref Expression exp)
		{
		}
	}



	class LabelTargetFormatter : IFormatter<LabelTarget>
	{
		IFormatter<string> _stringFormatter;
		IFormatter<Type> _typeFormatter;

		public LabelTargetFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(LabelTarget));
		}

		public void Serialize(ref byte[] buffer, ref int offset, LabelTarget exp)
		{
			_stringFormatter.Serialize(ref buffer, ref offset, exp.Name);
			_typeFormatter.Serialize(ref buffer, ref offset, exp.Type);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref LabelTarget exp)
		{
			string name = null;
			_stringFormatter.Deserialize(buffer, ref offset, ref name);

			Type type = null;
			_typeFormatter.Deserialize(buffer, ref offset, ref type);

			if (exp != null)
				if (exp.Name == name && exp.Type == type)
					return; // Existing object already exactly matches what we want

			exp = Expression.Label(type, name);
		}
	}

	class LabelFormatter : IFormatter<LabelExpression>
	{
		IFormatter<LabelTarget> _labelTargetFormatter;
		IFormatter<Expression> _expressionFormatter;

		public LabelFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(LabelExpression));
		}

		public void Serialize(ref byte[] buffer, ref int offset, LabelExpression label)
		{
			_labelTargetFormatter.Serialize(ref buffer, ref offset, label.Target);
			_expressionFormatter.Serialize(ref buffer, ref offset, label.DefaultValue);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref LabelExpression label)
		{
			LabelTarget labelTarget = null;
			_labelTargetFormatter.Deserialize(buffer, ref offset, ref labelTarget);

			Expression defaultValue = null;
			_expressionFormatter.Deserialize(buffer, ref offset, ref defaultValue);

			label = Expression.Label(labelTarget, defaultValue);
		}
	}

	class MemberAssignmentFormatter : IFormatter<MemberAssignment>
	{
		IFormatter<MemberInfo> _memberInfoFormatter;
		IFormatter<Expression> _expressionFormatter;

		public MemberAssignmentFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(MemberAssignment));
		}

		public void Serialize(ref byte[] buffer, ref int offset, MemberAssignment binding)
		{
			_memberInfoFormatter.Serialize(ref buffer, ref offset, binding.Member);
			_expressionFormatter.Serialize(ref buffer, ref offset, binding.Expression);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref MemberAssignment binding)
		{
			MemberInfo memberInfo = null;
			_memberInfoFormatter.Deserialize(buffer, ref offset, ref memberInfo);

			Expression expression = null;
			_expressionFormatter.Deserialize(buffer, ref offset, ref expression);

			binding = Expression.Bind(memberInfo, expression);
		}
	}

	class MemberListBindingFormatter : IFormatter<MemberListBinding>
	{
		IFormatter<MemberInfo> _memberInfoFormatter;
		IFormatter<ElementInit[]> _initArFormatter;

		public MemberListBindingFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(MemberListBinding));
		}

		public void Serialize(ref byte[] buffer, ref int offset, MemberListBinding binding)
		{
			_memberInfoFormatter.Serialize(ref buffer, ref offset, binding.Member);

			var inits = binding.Initializers;
			var initAr = new ElementInit[inits.Count];
			inits.CopyTo(initAr, 0);

			_initArFormatter.Serialize(ref buffer, ref offset, initAr);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref MemberListBinding binding)
		{
			MemberInfo memberInfo = null;
			_memberInfoFormatter.Deserialize(buffer, ref offset, ref memberInfo);

			ElementInit[] initializers = null;
			_initArFormatter.Deserialize(buffer, ref offset, ref initializers);

			binding = Expression.ListBind(memberInfo, initializers);
		}
	}

	class MemberMemberBindingFormatter : IFormatter<MemberMemberBinding>
	{
		IFormatter<MemberInfo> _memberInfoFormatter;
		IFormatter<MemberBinding[]> _bindingsArFormatter;

		public MemberMemberBindingFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(MemberMemberBinding));
		}

		public void Serialize(ref byte[] buffer, ref int offset, MemberMemberBinding binding)
		{
			_memberInfoFormatter.Serialize(ref buffer, ref offset, binding.Member);

			var bindings = binding.Bindings;
			var bindingsAr = new MemberBinding[bindings.Count];
			bindings.CopyTo(bindingsAr, 0);

			_bindingsArFormatter.Serialize(ref buffer, ref offset, bindingsAr);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref MemberMemberBinding binding)
		{
			MemberInfo memberInfo = null;
			_memberInfoFormatter.Deserialize(buffer, ref offset, ref memberInfo);

			MemberBinding[] bindings = null;
			_bindingsArFormatter.Deserialize(buffer, ref offset, ref bindings);

			binding = Expression.MemberBind(memberInfo, bindings);
		}
	}

	/*
	class LambdaFormatter<TDelegate> : IFormatter<LambdaExpression>
	{
		IFormatter<MemberInfo>      _memberInfoFormatter;
		IFormatter<MemberBinding[]> _bindingsArFormatter;

		public LambdaFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(Expression<TDelegate>));
		}

		public void Serialize(ref byte[] buffer, ref int offset, LambdaExpression lambda)
		{
			// Actual type is 'Expression<TDelegate>'

			lambda
		}

		public void Deserialize(byte[] buffer, ref int offset, ref LambdaExpression lambda)
		{
			// Actual type is 'Expression<TDelegate>'

			MemberInfo memberInfo = null;
			_memberInfoFormatter.Deserialize(buffer, ref offset, ref memberInfo);

			MemberBinding[] bindings = null;
			_bindingsArFormatter.Deserialize(buffer, ref offset, ref bindings);

			binding = Expression.MemberBind(memberInfo, bindings);
		}
	}
	*/
}
