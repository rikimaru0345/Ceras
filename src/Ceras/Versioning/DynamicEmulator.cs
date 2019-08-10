using Ceras.Formatters;
using Ceras.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Versioning
{
	//
	// Scenario:
	// - We're running with AotMode.Enabled and VersionTolerance at the same time
	// - The user is giving us some data to deserialize, but the embedded Schema is different.
	//   Usually we'd be screwed now since we can't generate a formatter to match the Schema!
	//
	// This formatter solves this by emulating the needed reads.
	// While this isn't very fast, it is the only thing that can be done when forced to
	// react to a new Schema while at the same time not being allowed to generated code.
	//

	internal sealed class DynamicEmulator<T> : IFormatter<T> where T : class
	{
		readonly MemberReader[] _readers;
		readonly CerasSerializer _ceras;

		int _deserializationDepth;

		public DynamicEmulator(CerasSerializer ceras, Schema schema)
		{
			_ceras = ceras;
			var members = schema.Members;

			_readers = new MemberReader[members.Count];

			for (int i = 0; i < members.Count; i++)
			{
				var m = members[i];

				if (m.IsSkip)
				{
					_readers[i] = new SkipReader();
				}
				else if (m.MemberInfo is FieldInfo fieldInfo)
				{
					var formatter = ceras.GetReferenceFormatter(m.MemberType);
					var readerType = typeof(FieldReader<>).MakeGenericType(m.MemberType);
					_readers[i] = (MemberReader)Activator.CreateInstance(readerType, formatter, fieldInfo);
				}
				else if (m.MemberInfo is PropertyInfo propInfo)
				{
					var formatter = ceras.GetReferenceFormatter(m.MemberType);
					var readerType = typeof(PropertyReader<>).MakeGenericType(m.MemberType);
					_readers[i] = (MemberReader)Activator.CreateInstance(readerType, formatter, propInfo);
				}
			}
		}

		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			throw new NotImplementedException("Call to DynamicEmulator.Serialize (this is a bug, Serialization must always use primary schema, please report this on GitHub!)");
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			// If this is the first time we're reading this type,
			// then we have to read the schema
			var type = typeof(T);
			if (!_ceras.InstanceData.EncounteredSchemaTypes.Contains(type))
			{
				_ceras.InstanceData.EncounteredSchemaTypes.Add(type);

				// Read the schema in which the data was written
				var schema = _ceras.ReadSchema(buffer, ref offset, type, false);

				_ceras.ActivateSchemaOverride(type, schema);
			}

			try
			{
				_deserializationDepth++;
				DeserializeEmulated(buffer, ref offset, ref value);
			}
			finally
			{
				_deserializationDepth--;
			}
		}

		void DeserializeEmulated(byte[] buffer, ref int offset, ref T value)
		{
			object target = value;

			for (int i = 0; i < _readers.Length; i++)
				_readers[i].Execute(target, buffer, ref offset);
		}
	}

	abstract class MemberReader
	{
		public abstract void Execute(object target, byte[] buffer, ref int offset);
	}

	sealed class FieldReader<TMember> : MemberReader
	{
		readonly IFormatter<TMember> _formatter;
		readonly FieldInfo _field;

		public FieldReader(IFormatter<TMember> formatter, FieldInfo field)
		{
			_formatter = formatter;
			_field = field;
		}

		public override void Execute(object target, byte[] buffer, ref int offset)
		{
			var size = SerializerBinary.ReadUInt32Fixed(buffer, ref offset);

			TMember value = default;
			_formatter.Deserialize(buffer, ref offset, ref value);
			_field.SetValue(target, value);
		}
	}

	sealed class PropertyReader<TMember> : MemberReader
	{
		readonly IFormatter<TMember> _formatter;
		readonly Action<object, TMember> _propSetter;

		public PropertyReader(IFormatter<TMember> formatter, PropertyInfo prop)
		{
			_formatter = formatter;
			_propSetter = (Action<object, TMember>)Delegate.CreateDelegate(typeof(Action<object, TMember>), prop.GetSetMethod(true));
		}

		public override void Execute(object target, byte[] buffer, ref int offset)
		{
			var size = SerializerBinary.ReadUInt32Fixed(buffer, ref offset);

			TMember value = default;
			_formatter.Deserialize(buffer, ref offset, ref value);
			_propSetter(target, value);
		}
	}

	sealed class SkipReader : MemberReader
	{
		public override void Execute(object target, byte[] buffer, ref int offset)
		{
			var size = SerializerBinary.ReadUInt32Fixed(buffer, ref offset);
			offset += (int)size;
		}
	}
}
