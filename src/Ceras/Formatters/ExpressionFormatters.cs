namespace Ceras.Formatters
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;

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
}
