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
    /// <item><description>Third-Party Types: It can handle types from external libraries that cannot be marked with [GenerateFormatter].</description></item>
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
        readonly MethodInfo _onBeforeSerialize, _onAfterSerialize, _onBeforeDeserialize, _onAfterDeserialize;
        
        readonly Func<T> _factory;
        readonly MethodBase _ctorOrFactory;
        readonly object _ctorTarget;
        readonly int[] _parameterToMemberIndex;

        public ReflectionFallbackFormatter(CerasSerializer serializer, bool isStatic)
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
                if (tc != null && !(tc is ConstructNull))
                {
                    if (!tc.HasDataArguments)
                    {
                        var factoryDel = tc.GetRefFormatterConstructor();
                        _factory = () => (T)factoryDel();
                    }
                    else
                    {
                        if (tc is SpecificConstructor sc)
                        {
                            _ctorOrFactory = sc.Constructor;
                        }
                        else if (tc is ConstructByMethod cbm)
                        {
                            _ctorOrFactory = cbm.Method;
                            _ctorTarget = cbm.TargetObject;
                        }

                        if (_ctorOrFactory != null)
                        {
                            var parameters = _ctorOrFactory.GetParameters();
                            _parameterToMemberIndex = new int[parameters.Length];
                            for (int i = 0; i < parameters.Length; i++)
                            {
                                var p = parameters[i];
                                var member = typeConfig.ParameterMap[p];
                                _parameterToMemberIndex[i] = Array.FindIndex(_members, m => m.member == member);
                            }
                        }
                    }
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
                {
                    value = _factory();
                }
                else if (_ctorOrFactory != null)
                {
                    // Buffered read
                    object[] memberValues = new object[_members.Length];
                    FakeContainer fake = new FakeContainer();
                    for (int i = 0; i < _members.Length; i++)
                    {
                        _members[i].formatter.DeserializeUntyped(ref reader, fake, FakeContainer.ValueField);
                        memberValues[i] = fake.Value;
                        fake.Value = null;
                    }

                    object[] args = new object[_parameterToMemberIndex.Length];
                    for (int i = 0; i < args.Length; i++)
                        args[i] = memberValues[_parameterToMemberIndex[i]];

                    if (_ctorOrFactory is ConstructorInfo ctor)
                        value = (T)ctor.Invoke(args);
                    else
                        value = (T)_ctorOrFactory.Invoke(_ctorTarget, args);

                    InvokeCallback(_onBeforeDeserialize, value);

                    // Assign rest of members
                    for (int i = 0; i < _members.Length; i++)
                    {
                        // Don't overwrite members that were passed to the constructor
                        if (Array.IndexOf(_parameterToMemberIndex, i) != -1)
                            continue;

                        var m = _members[i];
                        if (m.member is FieldInfo f)
                            f.SetValue(value, memberValues[i]);
                        else if (m.member is PropertyInfo p && p.GetSetMethod(true) != null)
                            p.SetValue(value, memberValues[i]);
                    }

                    InvokeCallback(_onAfterDeserialize, value);
                    return;
                }
                else
                {
                    value = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
                }
            }
            else if (_factory != null)
            {
                // We already have an instance, so we don't need to call the factory again.
                // Re-running the factory here was intended to match some legacy behavior, but it causes
                // side-effects (like double-allocations or incrementing counters) that are undesirable.
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

        class FakeContainer
        {
            public object Value;
            public static readonly FieldInfo ValueField = typeof(FakeContainer).GetField(nameof(Value));
        }
    }
}
