using System.Buffers;
using System.Reflection;

namespace Ceras.Formatters
{
    public abstract class FormatterBase<T> : IFormatter<T>
    {
        public abstract void Serialize(IBufferWriter<byte> writer, T value);
        public abstract void Deserialize(ref SequenceReader<byte> reader, ref T value);

        protected virtual void Initialize() { }
        internal virtual void DeserializeToMember(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            DeserializeUntyped(ref reader, container, member);
        }

        public virtual void SerializeUntyped(IBufferWriter<byte> writer, object value)
        {
            Serialize(writer, (T)value);
        }

        public virtual void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            T value = default;
            if (member is FieldInfo f)
            {
                var existing = f.GetValue(container);
                if (existing is T t) value = t;
                Deserialize(ref reader, ref value);
                f.SetValue(container, value);
            }
            else if (member is PropertyInfo p)
            {
                var existing = p.GetValue(container);
                if (existing is T t) value = t;
                Deserialize(ref reader, ref value);
                p.SetValue(container, value);
            }
        }
    }
}
