using System;

namespace Ceras.Helpers
{
	using System.Reflection;

	// Just a helper struct to make it a little easier to deal with MemberInfo
	public struct SerializedMember
	{
		const BindingFlags _bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		public string Name => MemberInfo.Name;

		public readonly MemberInfo MemberInfo;
		public readonly Type MemberType;

		public bool IsField => MemberInfo is FieldInfo;
		public bool IsProperty => MemberInfo is PropertyInfo;

		SerializedMember(MemberInfo memberInfo)
		{
			MemberInfo = memberInfo;

			if (memberInfo is PropertyInfo p)
				MemberType = p.PropertyType;
			else if (MemberInfo is FieldInfo f)
				MemberType = f.FieldType;
			else
				throw new Exception("type " + memberInfo.GetType().Name + " can not be used as serializedType");
		}

		internal static SerializedMember Create(MemberInfo memberInfo, bool allowReadonly = false)
		{
			if (memberInfo is FieldInfo f)
			{
				if (!allowReadonly)
					if (f.IsInitOnly)
						throw new Exception("field is readonly");
			}
			else if (memberInfo is PropertyInfo p)
			{
				p = p.DeclaringType.GetProperty(p.Name, _bindingFlags);
				if (!p.CanRead || !p.CanWrite)
					throw new Exception("property must be readable and writable");
			}
			else if (memberInfo == null)
				throw new ArgumentNullException("memberInfo cannot be null");
			else
				throw new ArgumentException("argument must be field or property");

			var declaringType = memberInfo.DeclaringType;
			if (declaringType == null)
				throw new Exception("declaring type is null");

			return new SerializedMember(memberInfo);
		}
	}

}
