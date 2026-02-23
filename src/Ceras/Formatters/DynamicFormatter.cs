namespace Ceras.Formatters
{
    using Helpers;
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;

    sealed class DynamicFormatter<T> : FormatterBase<T>
    {
        readonly CerasSerializer _ceras;
        readonly bool _isStatic;
        readonly (MemberInfo member, IFormatter formatter)[] _members;

        public DynamicFormatter(CerasSerializer serializer, bool isStatic)
        {
            _ceras = serializer;
            _isStatic = isStatic;

            var type = typeof(T);
            BannedTypes.ThrowIfNonspecific(type);
            
            var typeConfig = _ceras.Config.GetTypeConfig(type, isStatic);
            typeConfig.VerifyConstructionMethod();

            var members = typeConfig.Members.Where(m => m.ComputeFinalInclusionFast()).ToList();
            _members = new (MemberInfo member, IFormatter formatter)[members.Count];
            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                _members[i] = (m.Member, _ceras.GetReferenceFormatter(m.MemberType));
            }
        }

        public override void Serialize(IBufferWriter<byte> writer, T value)
        {
            for (int i = 0; i < _members.Length; i++)
            {
                var m = _members[i];
                object memberValue = null;

                if (m.member is FieldInfo f)
                    memberValue = f.GetValue(value);
                else if (m.member is PropertyInfo p)
                    memberValue = p.GetValue(value);

                try
                {
                    m.formatter.SerializeUntyped(writer, memberValue);
                }
                catch (Exception e)
                {
                    throw new Ceras.Exceptions.CerasException($"Error while serializing member '{m.member.Name}' of type '{typeof(T).FullName}'", e);
                }
            }
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            object boxed = value;
            for (int i = 0; i < _members.Length; i++)
            {
                var m = _members[i];
                m.formatter.DeserializeUntyped(ref reader, boxed, m.member);
            }
            value = (T)boxed;
        }

        internal override void DeserializeToMember(ref SequenceReader<byte> reader, object container, MemberInfo member)
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
