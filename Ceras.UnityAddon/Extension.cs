using System;

namespace Ceras
{
	using Formatters;
	using System.Collections.Generic;
	using UnityEngine;
	using static SerializerBinary;

	public static class UnityExtensions
	{
		static Dictionary<Type, Type> _typeToFormatterType = new Dictionary<Type, Type>
		{
				{ typeof(Vector2), typeof(Vector2Formatter) },
				{ typeof(Vector3), typeof(Vector3Formatter) },
		};


		public static void AddUnityFormatters(this SerializerConfig config) => config.OnResolveFormatter.Add(GetFormatter);

		static IFormatter GetFormatter(CerasSerializer ceras, Type typeToBeFormatted)
		{
			if (_typeToFormatterType.TryGetValue(typeToBeFormatted, out var formatterType))
				return (IFormatter)Activator.CreateInstance(formatterType);

			return null;
		}
	}
	
	class Vector2Formatter : IFormatter<Vector2>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector2 value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.x);
			WriteFloat32Fixed(ref buffer, ref offset, value.y);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector2 value)
		{
			value.x = ReadFloat32Fixed(buffer, ref offset);
			value.y = ReadFloat32Fixed(buffer, ref offset);
		}
	}

	class Vector3Formatter : IFormatter<Vector3>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.x);
			WriteFloat32Fixed(ref buffer, ref offset, value.y);
			WriteFloat32Fixed(ref buffer, ref offset, value.z);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			value.x = ReadFloat32Fixed(buffer, ref offset);
			value.y = ReadFloat32Fixed(buffer, ref offset);
			value.z = ReadFloat32Fixed(buffer, ref offset);
		}
	}

	class Vector4Formatter : IFormatter<Vector4>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector4 value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.x);
			WriteFloat32Fixed(ref buffer, ref offset, value.y);
			WriteFloat32Fixed(ref buffer, ref offset, value.z);
			WriteFloat32Fixed(ref buffer, ref offset, value.w);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector4 value)
		{
			value.x = ReadFloat32Fixed(buffer, ref offset);
			value.y = ReadFloat32Fixed(buffer, ref offset);
			value.z = ReadFloat32Fixed(buffer, ref offset);
			value.z = ReadFloat32Fixed(buffer, ref offset);
		}
	}
}
