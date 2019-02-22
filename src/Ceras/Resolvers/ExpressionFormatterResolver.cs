namespace Ceras.Formatters
{
	using System;
	using System.Collections.ObjectModel;
	using System.Linq.Expressions;
	using Resolvers;

	// todo: maybe a delegate to hook into TypeBinder; shorten expression type names to unique IDs or something. Maybe a 3 byte thing: special "ceras internal Id" symbol, then "expression tree type", then "actual type index"

	/// <summary>
	/// A special resolver that produces formatters for some special types in the 'System.Linq.Expressions.*' namespace (like <see cref="LabelTarget"/> or <see cref="MemberListBinding"/> ...)
	/// Ceras contains some TypeConfig defaults specifically for the actual Expressions themselves (<see cref="MethodCallExpression"/>, <see cref="LambdaExpression"/>, <see cref="LoopExpression"/>, ...).
	/// That enables <see cref="DynamicFormatter{T}"/> to handle them!
	/// </summary>
	public class ExpressionFormatterResolver : IFormatterResolver
	{
		readonly LabelTargetFormatter _labelTargetFormatter;
		readonly LabelFormatter _labelFormatter;

		readonly MemberAssignmentFormatter _memberAssignmentFormatter;
		readonly MemberListBindingFormatter _memberListBindingFormatter;
		readonly MemberMemberBindingFormatter _memberMemberBindingFormatter;

		public ExpressionFormatterResolver()
		{
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

			return null;
		}
	}
}