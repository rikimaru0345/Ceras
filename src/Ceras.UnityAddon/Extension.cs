using System;

namespace Ceras
{
	using Formatters;
	using Resolvers;
	using System.Collections.Generic;
	using UnityEngine;

	public static class CerasUnityFormatters
	{
		static Type[] _blittableTypes = new Type[]
		{
			// Primitives
			typeof(Vector2),
			typeof(Vector3),
			typeof(Vector4),
			typeof(Quaternion),
			typeof(Matrix4x4),
			typeof(Color),
			typeof(Color32),

			// Extended primitives
			typeof(Bounds),
			typeof(Rect),
			typeof(RangeInt),
			typeof(Plane),
			typeof(Ray),
			typeof(Ray2D),
			typeof(BoundingSphere),
			
			// Gradient data
			typeof(GradientAlphaKey),
			typeof(GradientColorKey),

			// Misc
			typeof(Keyframe), // Warning: Keyframe has changed from 2017.x to 2018.1. It has gained 3 items describing "weighted mode"
			typeof(Hash128),
			

#if UNITY_2017_2_OR_NEWER
			typeof(Vector2Int),
			typeof(Vector3Int),
			typeof(RectInt),
			typeof(BoundsInt),
#endif


		};

		static Dictionary<Type, Type> _typeToFormatterType = new Dictionary<Type, Type>
		{
			// New
			{ typeof(AnimationCurve), typeof(AnimationCurveFormatter) },
			{ typeof(RectOffset), typeof(RectOffsetFormatter) },
			{ typeof(Gradient), typeof(GradientFormatter) },
			{ typeof(LayerMask), typeof(LayerMaskFormatter) },
		};

		
		public static void ApplyToConfig(SerializerConfig config)
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

		static IFormatter GetFormatter(CerasSerializer ceras, Type typeToBeFormatted)
		{
			Type formatterType;
			if (_typeToFormatterType.TryGetValue(typeToBeFormatted, out formatterType))
				return (IFormatter)Activator.CreateInstance(formatterType);

			return null;
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
	}


	#region "New" Types
	
	class AnimationCurveFormatter : IFormatter<AnimationCurve>
	{
		// Auto-injected by Ceras
		IFormatter<Keyframe> _keyframeFormatter = default;
		ReinterpretFormatter<WrapMode> _wrapModeFormatter = default;

		public void Serialize(ref byte[] buffer, ref int offset, AnimationCurve value)
		{
			var keys = value.keys;

			// Length
			SerializerBinary.WriteInt32(ref buffer, ref offset, keys.Length);
			// Keyframes
			for (int i = 0; i < keys.Length; i++)
				_keyframeFormatter.Serialize(ref buffer, ref offset, keys[i]);

			_wrapModeFormatter.Serialize(ref buffer, ref offset, value.preWrapMode);
			_wrapModeFormatter.Serialize(ref buffer, ref offset, value.postWrapMode);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref AnimationCurve value)
		{
			// Read length
			var length = SerializerBinary.ReadInt32(buffer, ref offset);

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
			SerializerBinary.WriteInt32Fixed(ref buffer, ref offset, value.left);
			SerializerBinary.WriteInt32Fixed(ref buffer, ref offset, value.right);
			SerializerBinary.WriteInt32Fixed(ref buffer, ref offset, value.top);
			SerializerBinary.WriteInt32Fixed(ref buffer, ref offset, value.bottom);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref RectOffset value)
		{
			value.left = SerializerBinary.ReadInt32Fixed(buffer, ref offset);
			value.right = SerializerBinary.ReadInt32Fixed(buffer, ref offset);
			value.top = SerializerBinary.ReadInt32Fixed(buffer, ref offset);
			value.bottom = SerializerBinary.ReadInt32Fixed(buffer, ref offset);
		}
	}
	
	class GradientFormatter : IFormatter<Gradient>
	{
		// Auto-injected by Ceras
		IFormatter<GradientAlphaKey[]> _alphaKeysFormatter = default;
		IFormatter<GradientColorKey[]> _colorKeysFormatter = default;
		ReinterpretFormatter<GradientMode> _gradientModeFormatter = default;

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
			SerializerBinary.WriteInt32Fixed(ref buffer, ref offset, value.value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref LayerMask value)
		{
			value.value = SerializerBinary.ReadInt32Fixed(buffer, ref offset);
		}
	}

	#endregion

}
