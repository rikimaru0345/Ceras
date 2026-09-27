using System;
using System.Buffers;
using System.Reflection;
using System.Linq;

namespace Ceras.Formatters
{
    public class MemberInfoFormatter<T> : FormatterBase<T> where T : MemberInfo
    {
        const BindingFlags MemberLookup = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        readonly IFormatter<Type> _typeFormatter;

        internal MemberInfoFormatter(CerasSerializer ceras)
        {
            _typeFormatter = ceras.GetFormatter<Type>();
        }

        public override void Serialize(IBufferWriter<byte> writer, T member)
        {
            if (member == null)
            {
                _typeFormatter.Serialize(writer, null!);
                return;
            }

            // A member with no declaring type (a global or dynamic method) has no type to name, so it
            // is written as a null type marker and comes back as a null member - the same null that
            // Serialize writes for a null member in the first place.
            _typeFormatter.Serialize(writer, member.DeclaringType!);
            SerializerBinary.WriteByte(writer, (byte)member.MemberType);
            SerializerBinary.WriteString(writer, member.Name);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            // The out parameter of the type formatter is not nullable; a null marker in this slot is
            // how a null member is encoded (see Serialize) and is handled right after the read.
            Type declaringType = null!;
            _typeFormatter.Deserialize(ref reader, ref declaringType);
            if (declaringType == null) { value = null!; return; }

            var memberType = (MemberTypes)SerializerBinary.ReadByte(ref reader);
            var name = SerializerBinary.ReadString(ref reader);

            // Serialize writes MemberInfo.Name, which is never null, so a null marker here is a payload
            // that does not describe a member. GetField/GetProperty would report it as a bare
            // ArgumentNullException about "name", which says nothing about the file.
            if (name == null)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt data: a member of '{declaringType}' was written without a name.");

            if (memberType == MemberTypes.Field)
            {
                // A member that cannot be found - renamed or removed since the payload was written, or
                // declared on a base class the lookup flags do not reach - has always deserialized to
                // null here, and that is kept: a missing member is not a reason to lose the whole object.
                var field = declaringType.GetField(name, MemberLookup);
                value = field == null ? null! : (T)(object)field;
                return;
            }
            else if (memberType == MemberTypes.Property)
            {
                var property = declaringType.GetProperty(name, MemberLookup);
                value = property == null ? null! : (T)(object)property;
                return;
            }
            else
            {
                throw new NotSupportedException($"Member type '{memberType}' is no longer supported for serialization.");
            }
        }
    }
}
