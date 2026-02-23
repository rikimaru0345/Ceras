namespace Ceras.Formatters
{
    using Helpers;
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    sealed class DynamicFormatter<T> : FormatterBase<T>
    {
        readonly CerasSerializer _ceras;
        readonly bool _isStatic;
        readonly (MemberInfo member, IFormatter formatter, bool isMonomorphic)[] _members;
        readonly MethodInfo _onBeforeSerialize, _onAfterSerialize, _onBeforeDeserialize, _onAfterDeserialize;
        
        readonly Func<T> _factory;

        public DynamicFormatter(CerasSerializer serializer, bool isStatic)
        {
            _ceras = serializer;
            _isStatic = isStatic;

            var type = typeof(T);
            BannedTypes.ThrowIfNonspecific(type);
            
            var typeConfig = _ceras.Config.GetTypeConfig(type, isStatic);
            typeConfig.VerifyConstructionMethod();

            var meta = isStatic ? _ceras.GetStaticTypeMetaData(type) : _ceras.GetTypeMetaData(type);
            var schema = meta.PrimarySchema;

            _members = new (MemberInfo member, IFormatter formatter, bool isMonomorphic)[schema.Members.Count];
            for (int i = 0; i < schema.Members.Count; i++)
            {
                var m = schema.Members[i];
                
                // Verify static/instance match
                bool memberIsStatic = m.Member is FieldInfo fi ? fi.IsStatic : ((PropertyInfo)m.Member).GetAccessors(true)[0].IsStatic;
                if (memberIsStatic != isStatic)
                    throw new InvalidOperationException($"Member '{m.Member.Name}' static state does not match formatter's static mode.");

                // Monomorphic members (value types, sealed classes) skip ReferenceFormatter markers in generated code.
                // We must match this behavior for byte identity.
                var formatter = m.IsMonomorphic ? _ceras.GetSpecificFormatter(m.MemberType) : _ceras.GetReferenceFormatter(m.MemberType);
                _members[i] = (m.Member, formatter, m.IsMonomorphic);
            }

            _onBeforeSerialize = GetMethodByAttribute(type, typeof(OnBeforeSerializeAttribute));
            _onAfterSerialize = GetMethodByAttribute(type, typeof(OnAfterSerializeAttribute));
            _onBeforeDeserialize = GetMethodByAttribute(type, typeof(OnBeforeDeserializeAttribute));
            _onAfterDeserialize = GetMethodByAttribute(type, typeof(OnAfterDeserializeAttribute));

            if (!isStatic && !type.IsValueType)
            {
                var tc = typeConfig.TypeConstruction;
                if (tc != null && !tc.HasDataArguments && !(tc is ConstructNull))
                {
                    var factoryDel = tc.GetRefFormatterConstructor();
                    _factory = () => (T)factoryDel();
                }
            }
        }

        public override void Serialize(IBufferWriter<byte> writer, T value)
        {
            object target = _isStatic ? null : value;

            InvokeCallback(_onBeforeSerialize, target);

            for (int i = 0; i < _members.Length; i++)
            {
                var m = _members[i];
                object memberValue = null;

                if (m.member is FieldInfo f)
                    memberValue = f.GetValue(target);
                else if (m.member is PropertyInfo p)
                    memberValue = p.GetValue(target);

                try
                {
                    if (m.isMonomorphic)
                    {
                        // Direct call to specific formatter to skip ReferenceFormatter overhead.
                        // We must use reflection to call typed Serialize<TMember> if we want to be 100% sure it skips markers.
                        // But wait! SpecificFormatter.Serialize UNTYPED might still be called.
                        // Let's use SerializeUntyped, but ENSURE the formatter is NOT a ReferenceFormatter.
                        m.formatter.SerializeUntyped(writer, memberValue);
                    }
                    else
                    {
                        m.formatter.SerializeUntyped(writer, memberValue);
                    }
                }
                catch (Exception e)
                {
                    throw new Ceras.Exceptions.CerasException($"Error while serializing member '{m.member.Name}' of type '{typeof(T).FullName}'", e);
                }
            }

            InvokeCallback(_onAfterSerialize, target);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            if (_isStatic)
            {
                InvokeCallback(_onBeforeDeserialize, null);
                for (int i = 0; i < _members.Length; i++)
                {
                    var m = _members[i];
                    m.formatter.DeserializeUntyped(ref reader, null, m.member);
                }
                InvokeCallback(_onAfterDeserialize, null);
                return;
            }

            if (typeof(T).IsValueType)
            {
                object boxed = value;
                InvokeCallback(_onBeforeDeserialize, boxed);
                for (int i = 0; i < _members.Length; i++)
                {
                    var m = _members[i];
                    m.formatter.DeserializeUntyped(ref reader, boxed, m.member);
                }
                value = (T)boxed;
                InvokeCallback(_onAfterDeserialize, value);
                return;
            }

            // Reference type
            if (value == null)
            {
                if (_factory != null)
                    value = _factory();
                else
                    value = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            }
            else if (_factory != null)
            {
                // Re-run factory for pooling/recycling to match legacy behavior
                _factory();
            }

            InvokeCallback(_onBeforeDeserialize, value);

            for (int i = 0; i < _members.Length; i++)
            {
                var m = _members[i];
                m.formatter.DeserializeUntyped(ref reader, value, m.member);
            }

            InvokeCallback(_onAfterDeserialize, value);
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

        void InvokeCallback(MethodInfo method, object target)
        {
            if (method == null) return;

            var args = method.GetParameters().Length == 0 ? null : new object[] { _ceras };
            method.Invoke(target, args);
        }

        static MethodInfo GetMethodByAttribute(Type objectType, Type attributeType)
        {
            return objectType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(m => m.GetCustomAttribute(attributeType) != null);
        }
    }
}
