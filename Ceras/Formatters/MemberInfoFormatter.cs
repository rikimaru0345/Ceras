namespace Ceras.Formatters
{
	using System;
	using System.Reflection;

	class MemberInfoFormatter<T> : IFormatter<T> where T : MemberInfo
	{
		IFormatter<string> _stringFormatter;
		IFormatter<Type> _typeFormatter;

		public MemberInfoFormatter(CerasSerializer serializer)
		{
			_stringFormatter = (IFormatter<string>)serializer.GetFormatter(typeof(string));
			_typeFormatter = (IFormatter<Type>)serializer.GetFormatter(typeof(Type));
		}

		public void Serialize(ref byte[] buffer, ref int offset, T member)
		{
			// Declaring type
			_typeFormatter.Serialize(ref buffer, ref offset, member.DeclaringType);

			SerializerBinary.WriteInt32(ref buffer, ref offset, (int)member.MemberType);

			switch (member.MemberType)
			{
			// Write all the data we need to resolve overloads
			case MemberTypes.Constructor:
			case MemberTypes.Method:
				var method = (MethodBase)(MemberInfo)member;

				_stringFormatter.Serialize(ref buffer, ref offset, method.Name);

				var args = method.GetParameters();
				SerializerBinary.WriteInt32(ref buffer, ref offset, args.Length);
				for (int i = 0; i < args.Length; i++)
					_typeFormatter.Serialize(ref buffer, ref offset, args[i].ParameterType);

				break;

			case MemberTypes.Property:
				PropertyInfo prop = (PropertyInfo)(MemberInfo)member;
				_stringFormatter.Serialize(ref buffer, ref offset, prop.Name);
				_typeFormatter.Serialize(ref buffer, ref offset, prop.PropertyType);
				break;

			case MemberTypes.Field:
				FieldInfo field = (FieldInfo)(MemberInfo)member;
				_stringFormatter.Serialize(ref buffer, ref offset, field.Name);
				_typeFormatter.Serialize(ref buffer, ref offset, field.FieldType);

				break;

			default:
				throw new ArgumentOutOfRangeException("Cannot serialize member type '" + member.MemberType + "'");
			}

		}

		public void Deserialize(byte[] buffer, ref int offset, ref T member)
		{
			// What type?
			Type type = null;
			_typeFormatter.Deserialize(buffer, ref offset, ref type);

			// What kind of member?
			var memberType = (MemberTypes)SerializerBinary.ReadInt32(buffer, ref offset);

			string name = null;

			switch (memberType)
			{
			case MemberTypes.Constructor:
			case MemberTypes.Method:
				_stringFormatter.Deserialize(buffer, ref offset, ref name);
				var numArgs = SerializerBinary.ReadInt32(buffer, ref offset);

				Type[] args = new Type[numArgs];

				for (int i = 0; i < numArgs; i++)
					_typeFormatter.Deserialize(buffer, ref offset, ref args[i]);

				if (memberType == MemberTypes.Constructor)
					member = (T)(MemberInfo)type.GetConstructor(args);
				else
					member = (T)(MemberInfo)type.GetMethod(name, args);

				break;

			case MemberTypes.Field:
			case MemberTypes.Property:
				_stringFormatter.Deserialize(buffer, ref offset, ref name);
				Type fieldOrPropType = null;
				_typeFormatter.Deserialize(buffer, ref offset, ref fieldOrPropType);

				if (memberType == MemberTypes.Field)
					member = (T)(MemberInfo)type.GetField(name);
				else
					member = (T)(MemberInfo)type.GetProperty(name, fieldOrPropType);

				break;

			default:
				throw new ArgumentOutOfRangeException("Cannot deserialize member type '" + memberType + "'");
			}
		}
	}
}