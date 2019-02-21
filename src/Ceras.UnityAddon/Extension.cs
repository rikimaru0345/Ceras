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
		static Type[] _blittableTypes = new Type[]
		{
			typeof(Vector2),
			typeof(Vector3),
			typeof(Vector4),
			typeof(Quaternion),
			typeof(Matrix4x4),
			typeof(Color),
			typeof(Color32),
			typeof(Bounds),
			typeof(Rect),

#if UNITY_2017_2_OR_NEWER
			typeof(Vector2Int),
			typeof(Vector3Int),
			typeof(RangeInt),
			typeof(RectInt),
			typeof(BoundsInt),
#endif
		};

		static Dictionary<Type, Type> _typeToFormatterType = new Dictionary<Type, Type>
		{
			// New
			{ typeof(Keyframe), typeof(KeyframeFormatter) },
			{ typeof(AnimationCurve), typeof(AnimationCurveFormatter) },
			{ typeof(RectOffset), typeof(RectOffsetFormatter) },
			{ typeof(GradientAlphaKey), typeof(GradientAlphaKeyFormatter) },
			{ typeof(GradientColorKey), typeof(GradientColorKeyFormatter) },
			{ typeof(Gradient), typeof(GradientFormatter) },
			{ typeof(LayerMask), typeof(LayerMaskFormatter) },
		};


		public static void AddUnityFormatters(this SerializerConfig config)
		{
			// Force usage of the reinterpret formatter for simple types
			foreach (var t in _blittableTypes)
				config.ConfigType(t).CustomResolver = ForceReinterpret;

			// Same for arrays of those types
			foreach (var t in _blittableTypes)
			{
				var arType = t.MakeArrayType();
				config.ConfigType(arType).CustomResolver = ForceReinterpretArray;
			}

			// Add our resolver method for all types that need a custom formatter
			config.OnResolveFormatter.Add(GetFormatter);
		}

		static IFormatter ForceReinterpret(CerasSerializer ceras, Type typeToBeFormatted)
		{
			var formatterType = typeof(ReinterpretFormatter<>).MakeGenericType(typeToBeFormatted);
			return (IFormatter) Activator.CreateInstance(formatterType);
		}

		static IFormatter ForceReinterpretArray(CerasSerializer ceras, Type typeToBeFormatted)
		{
			var itemType = typeToBeFormatted.GetElementType();

			var maxCount = itemType == typeof(byte)
					? ceras.GetConfig().Advanced.SizeLimits.MaxByteArraySize
					: ceras.GetConfig().Advanced.SizeLimits.MaxArraySize;

			var formatterType = typeof(ReinterpretArrayFormatter<>).MakeGenericType(itemType);
			return (IFormatter)Activator.CreateInstance(formatterType, maxCount);
		}
		
		static IFormatter GetFormatter(CerasSerializer ceras, Type typeToBeFormatted)
		{
			if (_typeToFormatterType.TryGetValue(typeToBeFormatted, out var formatterType))
				return (IFormatter)Activator.CreateInstance(formatterType);

			return null;
		}
	}


	#region "New" Types

	class KeyframeFormatter : IFormatter<Keyframe>
	{
		// Auto-injected by Ceras
		EnumFormatter<WeightedMode> _weightedModeFormatter;

		public void Serialize(ref byte[] buffer, ref int offset, Keyframe value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.value);
			WriteFloat32Fixed(ref buffer, ref offset, value.time);

			WriteFloat32Fixed(ref buffer, ref offset, value.inTangent);
			WriteFloat32Fixed(ref buffer, ref offset, value.inWeight);

			WriteFloat32Fixed(ref buffer, ref offset, value.outTangent);
			WriteFloat32Fixed(ref buffer, ref offset, value.outWeight);

			_weightedModeFormatter.Serialize(ref buffer, ref offset, value.weightedMode);
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
			_weightedModeFormatter.Deserialize(buffer, ref offset, ref mode);
			value.weightedMode = mode;
		}
	}

	class AnimationCurveFormatter : IFormatter<AnimationCurve>
	{
		// Auto-injected by Ceras
		KeyframeFormatter _keyframeFormatter;
		EnumFormatter<WrapMode> _wrapModeFormatter;

		public void Serialize(ref byte[] buffer, ref int offset, AnimationCurve value)
		{
			var keys = value.keys;

			// Length
			WriteInt32(ref buffer, ref offset, keys.Length);
			// Keyframes
			for (int i = 0; i < keys.Length; i++)
				_keyframeFormatter.Serialize(ref buffer, ref offset, keys[i]);

			_wrapModeFormatter.Serialize(ref buffer, ref offset, value.preWrapMode);
			_wrapModeFormatter.Serialize(ref buffer, ref offset, value.postWrapMode);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref AnimationCurve value)
		{
			// Read length
			var length = ReadInt32(buffer, ref offset);

			// Deserialize individual keyframes again
			var keys = new Keyframe[length];
			for (int i = 0; i < length; i++)
				_keyframeFormatter.Deserialize(buffer, ref offset, ref keys[i]);

			// Create or update the given AnimationCurve
			if (value == null)
				value = new AnimationCurve(keys);
			else
				value.keys = keys;

			// Set the wrap modes
			var wrap = default(WrapMode);

			_wrapModeFormatter.Deserialize(buffer, ref offset, ref wrap);
			value.preWrapMode = wrap;

			_wrapModeFormatter.Deserialize(buffer, ref offset, ref wrap);
			value.postWrapMode = wrap;
		}
	}

	// RectOffset is a class, it can't be blitted.
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
		// Auto-injected by Ceras
		IFormatter<Color> _colorFormatter;

		public void Serialize(ref byte[] buffer, ref int offset, GradientColorKey value)
		{
			WriteFloat32Fixed(ref buffer, ref offset, value.time);
			_colorFormatter.Serialize(ref buffer, ref offset, value.color);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref GradientColorKey value)
		{
			value.time = ReadFloat32Fixed(buffer, ref offset);
			_colorFormatter.Deserialize(buffer, ref offset, ref value.color);
		}
	}

	class GradientFormatter : IFormatter<Gradient>
	{
		// Auto-injected by Ceras
		IFormatter<GradientAlphaKey[]> _alphaKeysFormatter;
		IFormatter<GradientColorKey[]> _colorKeysFormatter;
		EnumFormatter<GradientMode> _gradientModeFormatter;

		public void Serialize(ref byte[] buffer, ref int offset, Gradient value)
		{
			_alphaKeysFormatter.Serialize(ref buffer, ref offset, value.alphaKeys);
			_colorKeysFormatter.Serialize(ref buffer, ref offset, value.colorKeys);
			_gradientModeFormatter.Serialize(ref buffer, ref offset, value.mode);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Gradient value)
		{
			GradientAlphaKey[] alphaKeys = value.alphaKeys;
			_alphaKeysFormatter.Deserialize(buffer, ref offset, ref alphaKeys);
			value.alphaKeys = alphaKeys;

			GradientColorKey[] colorKeys = value.colorKeys;
			_colorKeysFormatter.Deserialize(buffer, ref offset, ref colorKeys);
			value.colorKeys = colorKeys;

			var mode = value.mode;
			_gradientModeFormatter.Deserialize(buffer, ref offset, ref mode);
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

}
