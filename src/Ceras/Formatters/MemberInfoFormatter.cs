using System;
using System.Buffers;
using System.Reflection;

namespace Ceras.Formatters
{
    public class MemberInfoFormatter<T> : IFormatter<T> where T : MemberInfo
    {
        readonly IFormatter<Type> _typeFormatter;

        public MemberInfoFormatter(CerasSerializer ceras)
        {
            _typeFormatter = ceras.GetFormatter<Type>();
        }

        public void Serialize(IBufferWriter<byte> writer, T member)
        {
            if (member == null)
            {
                _typeFormatter.Serialize(writer, null);
                return;
            }

            _typeFormatter.Serialize(writer, member.DeclaringType);
            SerializerBinary.WriteByte(writer, (byte)member.MemberType);
            SerializerBinary.WriteString(writer, member.Name);

            if (member is MethodBase method)
            {
                var parameters = method.GetParameters();
                SerializerBinary.WriteByte(writer, (byte)parameters.Length);
                foreach (var p in parameters) _typeFormatter.Serialize(writer, p.ParameterType);
            }
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            Type declaringType = null;
            _typeFormatter.Deserialize(ref reader, ref declaringType);
            if (declaringType == null) { value = null; return; }

            var memberType = (MemberTypes)SerializerBinary.ReadByte(ref reader);
            var name = SerializerBinary.ReadString(ref reader);

            if (memberType == MemberTypes.Method || memberType == MemberTypes.Constructor)
            {
                var paramCount = SerializerBinary.ReadByte(ref reader);
                var paramTypes = new Type[paramCount];
                for (int i = 0; i < paramCount; i++) _typeFormatter.Deserialize(ref reader, ref paramTypes[i]);

                if (memberType == MemberTypes.Method) value = (T)(object)declaringType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, paramTypes, null);
                else value = (T)(object)declaringType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, paramTypes, null);
            }
            else if (memberType == MemberTypes.Field) value = (T)(object)declaringType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            else if (memberType == MemberTypes.Property) value = (T)(object)declaringType.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        }
    }
}
