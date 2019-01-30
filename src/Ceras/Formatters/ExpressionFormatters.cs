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
		readonly CerasSerializer      _ceras;
		readonly ExpressionFormatter  _expressionFormatter;
		readonly LabelTargetFormatter _labelTargetFormatter;

		public ExpressionFormatterResolver(CerasSerializer ceras)
		{
			_ceras = ceras;

			_expressionFormatter = new ExpressionFormatter(ceras);

			_labelTargetFormatter = new LabelTargetFormatter();
			ceras.InjectDependencies(_labelTargetFormatter);
		}

		public IFormatter GetFormatter(Type type)
		{
			if (type.IsSubclassOf(typeof(Expression)))
			{
				// No type derived from Expression can be instantiated normally
				CerasSerializer.AddFormatterConstructedType(type);

				return _expressionFormatter;
			}

			if (type == typeof(LabelTarget))
			{
				return _labelTargetFormatter;
			}


			return null;
		}
	}


	// What about 'Constant'? For simple values it should be np, but for references we might pull in all sorts of wild nonsense.

	// Idea:
	// We take all the node names in the "ExpressionType" enum, then lookup the identically named static methods to construct them.
	// We always take the method with the most parameters so we can be sure no information gets lost.
	// From there we check out all the parameters of those methods and map them to the actual properties.
	// 
	class ExpressionFormatters
	{
		readonly HashSet<ExpressionType> _ignoredTypes = new HashSet<ExpressionType>
		{
			ExpressionType.Extension,
			ExpressionType.ListInit,
			ExpressionType.RuntimeVariables, // dynamic/eval... nope!
		};

		readonly Dictionary<string, string> _nameCorrections = new Dictionary<string, string>
		{
			{"Conditional", "Condition"},
			{"MemberAccess", "MakeMemberAccess"},
			{"Index", "MakeIndex"},
			{"Try", "MakeTry"},
		};

		readonly Dictionary<ExpressionType, Expression<Func<object>>> _selectedMethods;


		public ExpressionFormatters()
		{
			var allNodeTypes        = Enum.GetValues(typeof(ExpressionType)).Cast<ExpressionType>().ToArray();
			var constructionMethods = typeof(Expression).GetMethods(BindingFlags.Static | BindingFlags.Public);


			Type                             tType        = null;
			IEnumerable<Expression>          tExpressions = null;
			IEnumerable<MemberInfo>          tMembers     = null;
			IEnumerable<ParameterExpression> tParams      = null;

			_selectedMethods = new Dictionary<ExpressionType, Expression<Func<object>>>
			{
				{
					ExpressionType.Block,
					() => Expression.Block(tType, tParams, tExpressions),
				},
			};


			new Dictionary<ExpressionType, MethodInfo>
			{
				{
					ExpressionType.Block,
					typeof(Expression).GetMethod("Block", new[] {typeof(IEnumerable<ParameterExpression>), typeof(IEnumerable<Expression>)})
				},

				{ExpressionType.ArrayIndex, typeof(Expression).GetMethod("ArrayIndex", types: new[] {typeof(IEnumerable<Expression>)})},
				{ExpressionType.Invoke, typeof(Expression).GetMethod("Invoke", types: new[] {typeof(IEnumerable<Expression>)})},
				{ExpressionType.MemberInit, typeof(Expression).GetMethod("MemberInit", types: new[] {typeof(IEnumerable<MemberBinding>)})}, // Must use Expression.Bind() internally (?)
				{ExpressionType.New, typeof(Expression).GetMethod("New", types: new[] {typeof(ConstructorInfo), typeof(IEnumerable<Expression>), typeof(IEnumerable<MemberInfo>)})},
				{ExpressionType.NewArrayInit, typeof(Expression).GetMethod("NewArrayInit", types: new[] {typeof(IEnumerable<Expression>)})},
				{ExpressionType.NewArrayBounds, typeof(Expression).GetMethod("NewArrayBounds", types: new[] {typeof(IEnumerable<Expression>)})},
			};


			GenerateMatchingMethods(allNodeTypes, _ignoredTypes, _nameCorrections, constructionMethods);
		}

		static void GenerateMatchingMethods(ExpressionType[] allNodeTypes, HashSet<ExpressionType> ignoredTypes, Dictionary<string, string> nameCorrections, MethodInfo[] methods)
		{
			var matchingMethods = allNodeTypes
								 .Select(nodeType =>
								  {
									  if (ignoredTypes.Contains(nodeType))
										  return null;

									  var name = nodeType.ToString();
									  if (nameCorrections.TryGetValue(name, out var correctedName))
										  name = correctedName;

									  var matches = methods.Where(m => m.Name == name).OrderByDescending(m => m.GetParameters().Length).ToArray();

									  if (matches.Length >= 2)
										  if (matches[0].GetParameters().Length == matches[1].GetParameters().Length)
											  throw new Exception("oh no, there are at least two methods with the same amount of parameters, we need some manual guidance");

									  return matches.Last();
								  })
								 .Where(x => x != null)
								 .ToArray();

			var friendlyDisplay = matchingMethods.Select(m =>
												  {
													  var args          = m.GetParameters();
													  var argsFormatted = args.Select(a => $"{a.ParameterType.Name} {a.Name}");
													  var argsStr       = string.Join(", ", argsFormatted);
													  return $"{m.Name}({argsStr})";
												  })
												 .ToArray();
		}
	}

	static class LookupDictionaryExtensions
	{
		public static TCollection Append<TCollection, TItem>(this TCollection collection, TItem item)
				where TCollection : ICollection<TItem>
		{
			collection.Add(item);
			return collection;
		}
	}


	class ExpressionFormatter : IFormatter<Expression>
	{
		readonly CerasSerializer _ceras;

		public ExpressionFormatter(CerasSerializer ceras)
		{
			_ceras = ceras;
		}

		public void Serialize(ref byte[] buffer, ref int offset, Expression exp)
		{
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Expression exp)
		{
		}
	}


	class LabelTargetFormatter : IFormatter<LabelTarget>
	{
		public IFormatter<string> _stringFormatter;
		public IFormatter<Type>   _typeFormatter;

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
}