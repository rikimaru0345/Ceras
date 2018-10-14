using System;

namespace Ceras.Helpers
{
	using System.Reflection;
	using static System.Linq.Expressions.Expression;

	public struct SerializedMember
	{
		public readonly string Name;

		public readonly MemberInfo MemberInfo;
		public readonly Type MemberType;

		public bool IsField => MemberInfo is FieldInfo;
		public bool IsProperty => MemberInfo is PropertyInfo;

		public SerializedMember(MemberInfo memberInfo, Type memberType)
		{
			MemberInfo = memberInfo;
			MemberType = memberType;

			Name = memberInfo.Name;
		}
	}


	static class FieldOrProp
	{
		public static SerializedMember Create(MemberInfo memberInfo)
		{
			Type memberType;


			if (memberInfo is FieldInfo f)
			{
				memberType = f.FieldType;

				if (f.IsInitOnly)
					throw new Exception("field is readonly");
			}
			else if (memberInfo is PropertyInfo p)
			{
				memberType = p.PropertyType;
				if (!p.CanRead || !p.CanWrite)
					throw new Exception("property must be readable and writable");
			}
			else
				throw new ArgumentException("argument must be field or property");

			var declaringType = memberInfo.DeclaringType;
			if (declaringType == null)
				throw new Exception("declaring type is null");

			return new SerializedMember(memberInfo, memberType);
		}

	}
}
