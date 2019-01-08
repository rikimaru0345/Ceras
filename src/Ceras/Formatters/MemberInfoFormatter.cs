namespace Ceras.Formatters
{
	using System;
	using System.Reflection;

	class MemberInfoFormatter<T> : IFormatter<T> where T : MemberInfo
	{
		IFormatter<string> _stringFormatter;
		IFormatter<Type> _typeFormatter;
		
		const BindingFlags BindingAllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
		const BindingFlags BindingAllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		public MemberInfoFormatter(CerasSerializer serializer)
		{
			_stringFormatter = serializer.GetFormatter<string>();
			_typeFormatter = (IFormatter<Type>)serializer.GetSpecificFormatter(typeof(Type));
		}

		public void Serialize(ref byte[] buffer, ref int offset, T member)
		{
			// Declaring type
			_typeFormatter.Serialize(ref buffer, ref offset, member.DeclaringType);

			byte bindingData = 0;
			
			switch (member.MemberType)
			{
			// Write all the data we need to resolve overloads
			case MemberTypes.Constructor:
			case MemberTypes.Method:
				var method = (MethodBase)(MemberInfo)member;

				bindingData = PackBindingData(method.IsStatic, ReflectionTypeToCeras(member.MemberType));

				// 1. Binding data
				SerializerBinary.WriteByte(ref buffer, ref offset, bindingData);

				// 2. Method Name
				_stringFormatter.Serialize(ref buffer, ref offset, method.Name);

				// todo: parameter count can be merged into the unused bits of bindingData, but so much bit-packing makes things more complicated than they need to be;
				// it's extremely unlikely that anyone would notice the savings, even if they'd serialize tons of MemberInfos

				// 3. Parameters
				var args = method.GetParameters();
				SerializerBinary.WriteInt32(ref buffer, ref offset, args.Length);
				for (int i = 0; i < args.Length; i++)
					_typeFormatter.Serialize(ref buffer, ref offset, args[i].ParameterType);

				break;

			case MemberTypes.Property:
				PropertyInfo prop = (PropertyInfo)(MemberInfo)member;
				
				bindingData = PackBindingData(prop.GetAccessors(true)[0].IsStatic, ReflectionTypeToCeras(member.MemberType));
				
				// 1. Binding data
				SerializerBinary.WriteByte(ref buffer, ref offset, bindingData);

				// 2. Property Name
				_stringFormatter.Serialize(ref buffer, ref offset, prop.Name);

				// 3. Property Type
				_typeFormatter.Serialize(ref buffer, ref offset, prop.PropertyType);
				break;

			case MemberTypes.Field:
				FieldInfo field = (FieldInfo)(MemberInfo)member;

				bindingData = PackBindingData(field.IsStatic, ReflectionTypeToCeras(member.MemberType));

				// 1. Binding data
				SerializerBinary.WriteByte(ref buffer, ref offset, bindingData);

				// 2. Field Name
				_stringFormatter.Serialize(ref buffer, ref offset, field.Name);

				// 3. Field Type
				_typeFormatter.Serialize(ref buffer, ref offset, field.FieldType);

				break;

			case MemberTypes.TypeInfo:
			case MemberTypes.NestedType:
				// This should never happen, because root types as well as nested types are simply "Type",
				// so they should be handled by the TypeFormatter!
				goto default;

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
			var bindingData = SerializerBinary.ReadByte(buffer, ref offset);
			UnpackBindingData(bindingData, out bool isStatic, out MemberType memberType);

			var bindingFlags = isStatic ? BindingAllStatic : BindingAllInstance;

			string name = null;

			switch (memberType)
			{
			case MemberType.Constructor:
			case MemberType.Method:
				_stringFormatter.Deserialize(buffer, ref offset, ref name);
				var numArgs = SerializerBinary.ReadInt32(buffer, ref offset);

				Type[] args = new Type[numArgs];

				for (int i = 0; i < numArgs; i++)
					_typeFormatter.Deserialize(buffer, ref offset, ref args[i]);

				if (memberType == MemberType.Constructor)
					member = (T)(MemberInfo)type.GetConstructor(bindingFlags, null, args, null);
				else
					member = (T)(MemberInfo)type.GetMethod(name, bindingFlags, binder: null, types: args, modifiers: null);

				break;

			case MemberType.Field:
			case MemberType.Property:
				_stringFormatter.Deserialize(buffer, ref offset, ref name);
				Type fieldOrPropType = null;
				_typeFormatter.Deserialize(buffer, ref offset, ref fieldOrPropType);

				if (memberType == MemberType.Field)
					member = (T)(MemberInfo)type.GetField(name, bindingFlags);
				else
					member = (T)(MemberInfo)type.GetProperty(name, bindingFlags, null, fieldOrPropType, types: new Type[0], null);

				break;

			default:
				throw new ArgumentOutOfRangeException("Cannot deserialize member type '" + memberType + "'");
			}
		}


		static MemberType ReflectionTypeToCeras(MemberTypes memberTypes)
		{
			if ((memberTypes & MemberTypes.Constructor) != 0)
				return MemberType.Constructor;
			if ((memberTypes & MemberTypes.Method) != 0)
				return MemberType.Method;
			if ((memberTypes & MemberTypes.Field) != 0)
				return MemberType.Field;
			if ((memberTypes & MemberTypes.Property) != 0)
				return MemberType.Property;

			throw new InvalidOperationException("MemberTypes enum is out of range");
		}

		static byte PackBindingData(bool isStatic, MemberType memberType)
		{
			byte b = (byte)memberType;

			if (isStatic)
				b |= 1 << 7; // most significant bit is used for 'isStatic'

			return b;
		}

		static void UnpackBindingData(byte b, out bool isStatic, out MemberType memberType)
		{
			const int msbMask = 1 << 7;

			isStatic = (b & msbMask) != 0;

			memberType = (MemberType) (b & ~msbMask);
		}
	}

	enum MemberType : byte
	{
		Constructor = 0,
		Method = 1,
		Field = 2,
		Property = 3,
	}
}