using System;
using System.Buffers;
using System.Reflection;
using System.Linq;

namespace Ceras.Formatters
{
    public class MemberInfoFormatter<T> : FormatterBase<T> where T : MemberInfo
    {
        readonly IFormatter<Type> _typeFormatter;

        internal MemberInfoFormatter(CerasSerializer ceras)
        {
            _typeFormatter = ceras.GetFormatter<Type>();
        }

        public override void Serialize(IBufferWriter<byte> writer, T member)
        {
            if (member == null)
            {
                _typeFormatter.Serialize(writer, null);
                return;
            }

            _typeFormatter.Serialize(writer, member.DeclaringType);
            SerializerBinary.WriteByte(writer, (byte)member.MemberType);
            SerializerBinary.WriteString(writer, member.Name);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            Type declaringType = null;
            _typeFormatter.Deserialize(ref reader, ref declaringType);
            if (declaringType == null) { value = null; return; }

            var memberType = (MemberTypes)SerializerBinary.ReadByte(ref reader);
            var name = SerializerBinary.ReadString(ref reader);

            if (memberType == MemberTypes.Field)
            {
                value = (T)(object)declaringType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }
            else if (memberType == MemberTypes.Property)
            {
                value = (T)(object)declaringType.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }
            else
            {
                throw new NotSupportedException($"Member type '{memberType}' is no longer supported for serialization.");
            }
        }
    }
}
