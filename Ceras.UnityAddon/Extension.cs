using System;

namespace Ceras
{
	using Formatters;
	using Resolvers;
	using System.Collections.Generic;
	using UnityEngine;
	using static SerializerBinary;

	public static class UnityExtensions
	{
		static Dictionary<Type, Type> _typeToFormatterType = new Dictionary<Type, Type>
		{
				// Classic
				{ typeof(Vector2), typeof(Vector2Formatter) },
				{ typeof(Vector3), typeof(Vector3Formatter) },
				{ typeof(Vector4), typeof(Vector4Formatter) },

				{ typeof(Quaternion), typeof(QuaternionFormatter) },
				{ typeof(Matrix4x4), typeof(Matrix4x4Formatter) },

				{ typeof(Color), typeof(ColorFormatter) },
				{ typeof(Color32), typeof(Color32Formatter) },

				{ typeof(Bounds), typeof(BoundsFormatter) },

				{ typeof(Rect), typeof(RectFormatter) },


				// New
				{ typeof(Keyframe), typeof(KeyframeFormatter) },
				{ typeof(AnimationCurve), typeof(AnimationCurveFormatter) },
				{ typeof(RectOffset), typeof(RectOffsetFormatter) },
				{ typeof(GradientAlphaKey), typeof(GradientAlphaKeyFormatter) },
				{ typeof(GradientColorKey), typeof(GradientColorKeyFormatter) },
				{ typeof(Gradient), typeof(GradientFormatter) },
				{ typeof(LayerMask), typeof(LayerMaskFormatter) },


				// 2017.2
				{ typeof(Vector2Int), typeof(Vector2IntFormatter) },
				{ typeof(Vector3Int), typeof(Vector3IntFormatter) },
				{ typeof(RangeInt), typeof(RangeIntFormatter) },
				{ typeof(RectInt), typeof(RectIntFormatter) },
				{ typeof(BoundsInt), typeof(BoundsIntFormatter) },

		};


		public static void AddUnityFormatters(this SerializerConfig config) => config.OnResolveFormatter.Add(GetFormatter);

		static IFormatter GetFormatter(CerasSerializer ceras, Type typeToBeFormatted)
		{
			if (_typeToFormatterType.TryGetValue(typeToBeFormatted, out var formatterType))
				return (IFormatter)Activator.CreateInstance(formatterType);

			return null;
		}
	}


	#region Classic Types

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


	class QuaternionFormatter : IFormatter<Quaternion>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Quaternion value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.w);
			WriteFloat32Fixed(ref buffer, ref offset, value.x);
			WriteFloat32Fixed(ref buffer, ref offset, value.y);
			WriteFloat32Fixed(ref buffer, ref offset, value.z);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Quaternion value)
		{
			value.w = ReadFloat32Fixed(buffer, ref offset);
			value.x = ReadFloat32Fixed(buffer, ref offset);
			value.y = ReadFloat32Fixed(buffer, ref offset);
			value.z = ReadFloat32Fixed(buffer, ref offset);
		}
	}

	class Matrix4x4Formatter : IFormatter<Matrix4x4>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Matrix4x4 value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.m00);
			WriteFloat32Fixed(ref buffer, ref offset, value.m01);
			WriteFloat32Fixed(ref buffer, ref offset, value.m02);
			WriteFloat32Fixed(ref buffer, ref offset, value.m03);

			WriteFloat32Fixed(ref buffer, ref offset, value.m10);
			WriteFloat32Fixed(ref buffer, ref offset, value.m11);
			WriteFloat32Fixed(ref buffer, ref offset, value.m12);
			WriteFloat32Fixed(ref buffer, ref offset, value.m13);

			WriteFloat32Fixed(ref buffer, ref offset, value.m20);
			WriteFloat32Fixed(ref buffer, ref offset, value.m21);
			WriteFloat32Fixed(ref buffer, ref offset, value.m22);
			WriteFloat32Fixed(ref buffer, ref offset, value.m23);

			WriteFloat32Fixed(ref buffer, ref offset, value.m30);
			WriteFloat32Fixed(ref buffer, ref offset, value.m31);
			WriteFloat32Fixed(ref buffer, ref offset, value.m32);
			WriteFloat32Fixed(ref buffer, ref offset, value.m33);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Matrix4x4 value)
		{
			value.m00 = ReadFloat32Fixed(buffer, ref offset);
			value.m01 = ReadFloat32Fixed(buffer, ref offset);
			value.m02 = ReadFloat32Fixed(buffer, ref offset);
			value.m03 = ReadFloat32Fixed(buffer, ref offset);

			value.m10 = ReadFloat32Fixed(buffer, ref offset);
			value.m11 = ReadFloat32Fixed(buffer, ref offset);
			value.m12 = ReadFloat32Fixed(buffer, ref offset);
			value.m13 = ReadFloat32Fixed(buffer, ref offset);

			value.m20 = ReadFloat32Fixed(buffer, ref offset);
			value.m21 = ReadFloat32Fixed(buffer, ref offset);
			value.m22 = ReadFloat32Fixed(buffer, ref offset);
			value.m23 = ReadFloat32Fixed(buffer, ref offset);

			value.m30 = ReadFloat32Fixed(buffer, ref offset);
			value.m31 = ReadFloat32Fixed(buffer, ref offset);
			value.m32 = ReadFloat32Fixed(buffer, ref offset);
			value.m33 = ReadFloat32Fixed(buffer, ref offset);
		}
	}



	class ColorFormatter : IFormatter<Color>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Color value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.r);
			WriteFloat32Fixed(ref buffer, ref offset, value.g);
			WriteFloat32Fixed(ref buffer, ref offset, value.b);
			WriteFloat32Fixed(ref buffer, ref offset, value.a);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Color value)
		{
			value.r = ReadFloat32Fixed(buffer, ref offset);
			value.g = ReadFloat32Fixed(buffer, ref offset);
			value.b = ReadFloat32Fixed(buffer, ref offset);
			value.a = ReadFloat32Fixed(buffer, ref offset);
		}
	}

	class Color32Formatter : IFormatter<Color32>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Color32 value)
		{
			EnsureCapacity(ref buffer, offset, 4);

			buffer[offset + 0] = value.r;
			buffer[offset + 1] = value.g;
			buffer[offset + 2] = value.b;
			buffer[offset + 3] = value.a;

			offset += 4;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Color32 value)
		{
			value.r = buffer[offset + 0];
			value.g = buffer[offset + 1];
			value.b = buffer[offset + 2];
			value.a = buffer[offset + 3];

			offset += 4;
		}
	}


	class BoundsFormatter : IFormatter<Bounds>
	{
		public Vector3Formatter Vector3Formatter;

		public void Serialize(ref byte[] buffer, ref int offset, Bounds value)
		{
			Vector3Formatter.Serialize(ref buffer, ref offset, value.center);
			Vector3Formatter.Serialize(ref buffer, ref offset, value.size);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Bounds value)
		{
			Vector3 center = default(Vector3), size = default(Vector3);

			Vector3Formatter.Deserialize(buffer, ref offset, ref center);
			Vector3Formatter.Deserialize(buffer, ref offset, ref size);

			value = new Bounds(center, size);
		}
	}

	class RectFormatter : IFormatter<Rect>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Rect value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.xMin);
			WriteFloat32Fixed(ref buffer, ref offset, value.yMin);
			WriteFloat32Fixed(ref buffer, ref offset, value.xMax);
			WriteFloat32Fixed(ref buffer, ref offset, value.yMax);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Rect value)
		{
			value.xMin = ReadFloat32Fixed(buffer, ref offset);
			value.yMin = ReadFloat32Fixed(buffer, ref offset);
			value.xMax = ReadFloat32Fixed(buffer, ref offset);
			value.yMax = ReadFloat32Fixed(buffer, ref offset);
		}
	}

	#endregion

	#region "New" Types

	class KeyframeFormatter : IFormatter<Keyframe>
	{
		public EnumFormatter<WeightedMode> WeightedModeFormatter;

		public void Serialize(ref byte[] buffer, ref int offset, Keyframe value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.value);
			WriteFloat32Fixed(ref buffer, ref offset, value.time);

			WriteFloat32Fixed(ref buffer, ref offset, value.inTangent);
			WriteFloat32Fixed(ref buffer, ref offset, value.inWeight);

			WriteFloat32Fixed(ref buffer, ref offset, value.outTangent);
			WriteFloat32Fixed(ref buffer, ref offset, value.outWeight);

			WeightedModeFormatter.Serialize(ref buffer, ref offset, value.weightedMode);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Keyframe value)
		{
			value.value = ReadFloat32Fixed(buffer, ref offset);
			value.time = ReadFloat32Fixed(buffer, ref offset);

			value.inTangent = ReadFloat32Fixed(buffer, ref offset);
			value.inWeight = ReadFloat32Fixed(buffer, ref offset);

			value.outTangent = ReadFloat32Fixed(buffer, ref offset);
			value.outWeight = ReadFloat32Fixed(buffer, ref offset);

			var mode = value.weightedMode;
			WeightedModeFormatter.Deserialize(buffer, ref offset, ref mode);
			value.weightedMode = mode;
		}
	}

	class AnimationCurveFormatter : IFormatter<AnimationCurve>
	{
		public KeyframeFormatter KeyframeFormatter;
		public EnumFormatter<WrapMode> WrapModeFormatter;

		public void Serialize(ref byte[] buffer, ref int offset, AnimationCurve value)
		{
			var keys = value.keys;

			// Length
			WriteInt32(ref buffer, ref offset, keys.Length);
			// Keyframes
			for (int i = 0; i < keys.Length; i++)
				KeyframeFormatter.Serialize(ref buffer, ref offset, keys[i]);

			WrapModeFormatter.Serialize(ref buffer, ref offset, value.preWrapMode);
			WrapModeFormatter.Serialize(ref buffer, ref offset, value.postWrapMode);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref AnimationCurve value)
		{
			// Read length
			var length = ReadInt32(buffer, ref offset);

			// Deserialize individual keyframes again
			var keys = new Keyframe[length];
			for (int i = 0; i < length; i++)
				KeyframeFormatter.Deserialize(buffer, ref offset, ref keys[i]);

			// Create or update the given AnimationCurve
			if (value == null)
				value = new AnimationCurve(keys);
			else
				value.keys = keys;

			// Set the wrap modes
			var wrap = default(WrapMode);

			WrapModeFormatter.Deserialize(buffer, ref offset, ref wrap);
			value.preWrapMode = wrap;

			WrapModeFormatter.Deserialize(buffer, ref offset, ref wrap);
			value.postWrapMode = wrap;
		}
	}

	class RectOffsetFormatter : IFormatter<RectOffset>
	{
		public void Serialize(ref byte[] buffer, ref int offset, RectOffset value)
		{
			WriteInt32Fixed(ref buffer, ref offset, value.left);
			WriteInt32Fixed(ref buffer, ref offset, value.right);
			WriteInt32Fixed(ref buffer, ref offset, value.top);
			WriteInt32Fixed(ref buffer, ref offset, value.bottom);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref RectOffset value)
		{
			value.left = ReadInt32Fixed(buffer, ref offset);
			value.right = ReadInt32Fixed(buffer, ref offset);
			value.top = ReadInt32Fixed(buffer, ref offset);
			value.bottom = ReadInt32Fixed(buffer, ref offset);
		}
	}

	class GradientAlphaKeyFormatter : IFormatter<GradientAlphaKey>
	{
		public void Serialize(ref byte[] buffer, ref int offset, GradientAlphaKey value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.time);
			WriteFloat32Fixed(ref buffer, ref offset, value.alpha);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref GradientAlphaKey value)
		{
			value.time = ReadFloat32Fixed(buffer, ref offset);
			value.alpha = ReadFloat32Fixed(buffer, ref offset);
		}
	}

	class GradientColorKeyFormatter : IFormatter<GradientColorKey>
	{
		public ColorFormatter ColorFormatter;

		public void Serialize(ref byte[] buffer, ref int offset, GradientColorKey value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.time);
			ColorFormatter.Serialize(ref buffer, ref offset, value.color);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref GradientColorKey value)
		{
			value.time = ReadFloat32Fixed(buffer, ref offset);
			ColorFormatter.Deserialize(buffer, ref offset, ref value.color);
		}
	}

	class GradientFormatter : IFormatter<Gradient>
	{
		public IFormatter<GradientAlphaKey[]> AlphaKeysFormatter;
		public IFormatter<GradientColorKey[]> ColorKeysFormatter;
		public EnumFormatter<GradientMode> GradientModeFormatter;

		public void Serialize(ref byte[] buffer, ref int offset, Gradient value)
		{
			AlphaKeysFormatter.Serialize(ref buffer, ref offset, value.alphaKeys);
			ColorKeysFormatter.Serialize(ref buffer, ref offset, value.colorKeys);
			GradientModeFormatter.Serialize(ref buffer, ref offset, value.mode);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Gradient value)
		{
			GradientAlphaKey[] alphaKeys = value.alphaKeys;
			AlphaKeysFormatter.Deserialize(buffer, ref offset, ref alphaKeys);
			value.alphaKeys = alphaKeys;

			GradientColorKey[] colorKeys = value.colorKeys;
			ColorKeysFormatter.Deserialize(buffer, ref offset, ref colorKeys);
			value.colorKeys = colorKeys;

			var mode = value.mode;
			GradientModeFormatter.Deserialize(buffer, ref offset, ref mode);
			value.mode = mode;
		}
	}

	class LayerMaskFormatter : IFormatter<LayerMask>
	{
		public void Serialize(ref byte[] buffer, ref int offset, LayerMask value)
		{
			WriteInt32Fixed(ref buffer, ref offset, value.value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref LayerMask value)
		{
			value.value = ReadInt32Fixed(buffer, ref offset);
		}
	}

	#endregion

	#region 2017.2

	class Vector2IntFormatter : IFormatter<Vector2Int>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector2Int value)
		{
			WriteInt32Fixed(ref buffer, ref offset, value.x);
			WriteInt32Fixed(ref buffer, ref offset, value.y);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector2Int value)
		{
			value.x = ReadInt32Fixed(buffer, ref offset);
			value.y = ReadInt32Fixed(buffer, ref offset);
		}
	}

	class Vector3IntFormatter : IFormatter<Vector3Int>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector3Int value)
		{
			WriteInt32Fixed(ref buffer, ref offset, value.x);
			WriteInt32Fixed(ref buffer, ref offset, value.y);
			WriteInt32Fixed(ref buffer, ref offset, value.z);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3Int value)
		{
			value.x = ReadInt32Fixed(buffer, ref offset);
			value.y = ReadInt32Fixed(buffer, ref offset);
			value.z = ReadInt32Fixed(buffer, ref offset);
		}
	}

	class RangeIntFormatter : IFormatter<RangeInt>
	{
		public void Serialize(ref byte[] buffer, ref int offset, RangeInt value)
		{
			WriteInt32(ref buffer, ref offset, value.start);
			WriteInt32(ref buffer, ref offset, value.length);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref RangeInt value)
		{
			value.start = ReadInt32(buffer, ref offset);
			value.length = ReadInt32(buffer, ref offset);
		}
	}

	class RectIntFormatter : IFormatter<RectInt>
	{
		public void Serialize(ref byte[] buffer, ref int offset, RectInt value)
		{
			WriteInt32Fixed(ref buffer, ref offset, value.xMin);
			WriteInt32Fixed(ref buffer, ref offset, value.xMax);
			WriteInt32Fixed(ref buffer, ref offset, value.yMin);
			WriteInt32Fixed(ref buffer, ref offset, value.yMax);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref RectInt value)
		{
			value.xMin = ReadInt32Fixed(buffer, ref offset);
			value.xMax = ReadInt32Fixed(buffer, ref offset);
			value.yMin = ReadInt32Fixed(buffer, ref offset);
			value.yMax = ReadInt32Fixed(buffer, ref offset);
		}
	}

	class BoundsIntFormatter : IFormatter<BoundsInt>
	{
		public void Serialize(ref byte[] buffer, ref int offset, BoundsInt value)
		{
			WriteInt32Fixed(ref buffer, ref offset, value.xMin);
			WriteInt32Fixed(ref buffer, ref offset, value.yMin);
			WriteInt32Fixed(ref buffer, ref offset, value.xMax);
			WriteInt32Fixed(ref buffer, ref offset, value.yMax);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref BoundsInt value)
		{
			value.xMin = ReadInt32Fixed(buffer, ref offset);
			value.yMin = ReadInt32Fixed(buffer, ref offset);
			value.xMax = ReadInt32Fixed(buffer, ref offset);
			value.yMax = ReadInt32Fixed(buffer, ref offset);
		}
	}
	
	#endregion
}
