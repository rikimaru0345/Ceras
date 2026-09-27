namespace Ceras.Formatters
{
    using Helpers;
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// <para>ReflectionFallbackFormatter is the "escape hatch" of Ceras.</para>
    /// <para>While the project's vision is to move towards 100% Source Generated formatters for performance and AOT compatibility, 
    /// this class remains essential for several reasons:</para>
    /// <list type="bullet">
    /// <item><description>Third-Party Types: It can handle types from external libraries that cannot be marked with [CerasSerializable].</description></item>
    /// <item><description>Private Access: Unlike generated code, it can use reflection to access private/protected state in legacy objects.</description></item>
    /// <item><description>Runtime Types: It supports types created at runtime (e.g., via Reflection.Emit or dynamic proxies) that are invisible to compile-time generators.</description></item>
    /// <item><description>Test Baseline: It serves as the "ground truth" for byte-identity verification in the test suite.</description></item>
    /// </list>
    /// <para>In performance-critical paths (like high-speed networking), users should prioritize Source Generators. 
    /// This formatter provides a slower, reflection-based fallback that "just works" for everything else.</para>
    /// </summary>
    sealed class ReflectionFallbackFormatter<T> : FormatterBase<T>
    {
        readonly CerasSerializer _ceras;
        readonly bool _isStatic;
        readonly (MemberInfo member, IFormatter formatter, bool isMonomorphic)[] _members;
        // A type that declares none of the four callback attributes has no method to invoke, so all
        // four stay null; InvokeCallback checks for that before doing anything.
        readonly MethodInfo? _onBeforeSerialize, _onAfterSerialize, _onBeforeDeserialize, _onAfterDeserialize;
        readonly Func<object> _factory;

        internal ReflectionFallbackFormatter(CerasSerializer serializer, bool isStatic)
        {
            _ceras = serializer;
            _isStatic = isStatic;

            var type = typeof(T);
            BannedTypes.ThrowIfNonspecific(type);
            
            var typeConfig = _ceras.Config.GetTypeConfig(type, isStatic);
            typeConfig!.VerifyConstructionMethod();
            _factory = typeConfig.TypeConstruction.GetRefFormatterConstructor();

            // Structs with only auto-properties cannot be serialized using reflection
            // because their backing-fields are hidden and excluded by default.
            TypeConfigDefaults.CheckStructWithAutoProps(typeConfig);

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
                _ceras.PushContext(type, m.Member);
                try
                {
                    var formatter = m.IsMonomorphic ? _ceras.GetSpecificFormatter(m.MemberType) : _ceras.GetReferenceFormatterRequired(m.MemberType);
                    _members[i] = (m.Member, formatter, m.IsMonomorphic);
                }
                finally
                {
                    _ceras.PopContext();
                }
            }

            _onBeforeSerialize = GetMethodByAttribute(type, typeof(OnBeforeSerializeAttribute));
            _onAfterSerialize = GetMethodByAttribute(type, typeof(OnAfterSerializeAttribute));
            _onBeforeDeserialize = GetMethodByAttribute(type, typeof(OnBeforeDeserializeAttribute));
            _onAfterDeserialize = GetMethodByAttribute(type, typeof(OnAfterDeserializeAttribute));
        }

        public override void Serialize(IBufferWriter<byte> writer, T value)
        {
            object? target = _isStatic ? null : value;

            InvokeCallback(_onBeforeSerialize, target);

            SerializeMembers(writer, target);

            InvokeCallback(_onAfterSerialize, target);
        }

        // target is null for a static type formatter: its members are static, and reading a static
        // member is exactly what FieldInfo.GetValue(null)/PropertyInfo.GetValue(null) are for.
        void SerializeMembers(IBufferWriter<byte> writer, object? target)
        {
            for (int i = 0; i < _members.Length; i++)
            {
                var m = _members[i];

                object? memberValue = null;
                if (m.member is FieldInfo f)
                    memberValue = f.GetValue(target);
                else if (m.member is PropertyInfo prop)
                    memberValue = prop.GetValue(target);

                try
                {
                    // A member that holds null is an ordinary value: the member's own formatter writes
                    // the null marker for it, which is why the untyped contract takes a non-null object
                    // here even though reflection hands us null.
                    m.formatter.SerializeUntyped(writer, memberValue!);
                }
                catch (Exception e)
                {
                    throw new Ceras.Exceptions.CerasException($"Error while serializing member '{m.member.Name}' of type '{typeof(T).FullName}'", e);
                }
            }
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            if (_isStatic)
            {
                InvokeCallback(_onBeforeDeserialize, null!);
                for (int i = 0; i < _members.Length; i++)
                {
                    var m = _members[i];
                    m.formatter.DeserializeUntyped(ref reader, null!, m.member);
                }
                InvokeCallback(_onAfterDeserialize, null!);
                return;
            }

            if (typeof(T).IsValueType)
            {
                // T is a value type in this branch, so boxing it always produces an instance and the
                // unbox below can never see null. The compiler cannot see that, hence the
                // null-forgiving operator on the value.
                object boxed = value!;
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
                value = (T)_factory();
            }

            InvokeCallback(_onBeforeDeserialize, value);

            for (int i = 0; i < _members.Length; i++)
            {
                var m = _members[i];
                m.formatter.DeserializeUntyped(ref reader, value, m.member);
            }

            InvokeCallback(_onAfterDeserialize, value);
        }

        protected override void Initialize()
        {
            // This method is called by CerasSerializer via reflection after all dependencies are injected.
        }

        internal override void DeserializeToMember(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            T value = default!;

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

        // Created on first use, hence the null until then.
        object[]? _callbackArgs;

        // The target is the instance the callback runs against, or null for a static type formatter
        // (where the only thing InvokeCallback can be called with is the static method itself).
        void InvokeCallback(MethodInfo? method, object? target)
        {
            if (method == null) return;

            if (method.GetParameters().Length == 0)
            {
                method.Invoke(target, null);
            }
            else
            {
                var args = _callbackArgs ??= new object[] { _ceras };
                method.Invoke(target, args);
            }
        }

        static MethodInfo? GetMethodByAttribute(Type objectType, Type attributeType)
        {
            return objectType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(m => m.GetCustomAttribute(attributeType) != null);
        }
    }
}
