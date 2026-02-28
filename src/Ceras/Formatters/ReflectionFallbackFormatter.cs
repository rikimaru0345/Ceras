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
                    var formatter = m.IsMonomorphic ? _ceras.GetSpecificFormatter(m.MemberType) : _ceras.GetReferenceFormatter(m.MemberType);
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

            // When using a [CerasConstructor], getter-only members are serialized last to
            // match the Deserialize order (they are read after ctor construction).
            if (_ctorOrFactory != null)
            {
                SerializeMembers(writer, target, getterOnly: false);
                SerializeMembers(writer, target, getterOnly: true);
            }
            else
            {
                SerializeMembers(writer, target, getterOnly: null);
            }

            InvokeCallback(_onAfterSerialize, target);
        }

        void SerializeMembers(IBufferWriter<byte> writer, object target, bool? getterOnly)
        {
            for (int i = 0; i < _members.Length; i++)
            {
                var m = _members[i];
                bool isGetterOnly = m.member is PropertyInfo p && p.GetSetMethod(true) == null;
                if (getterOnly.HasValue && isGetterOnly != getterOnly.Value) continue;

                object memberValue = null;
                if (m.member is FieldInfo f)
                    memberValue = f.GetValue(target);
                else if (m.member is PropertyInfo prop)
                    memberValue = prop.GetValue(target);

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
                if (_ctorOrFactory != null)
                {
                    // Phase 1: read non-getter-only members into a buffer (these include ctor args).
                    // Getter-only members are read after construction so they can be populated in-place.
                    object[] memberValues = new object[_members.Length];
                    FakeContainer fake = new FakeContainer();
                    for (int i = 0; i < _members.Length; i++)
                    {
                        bool isGetterOnly = _members[i].member is PropertyInfo gop && gop.GetSetMethod(true) == null;
                        if (isGetterOnly) continue;

                        _ceras.InstanceData.CurrentFixupTarget = memberValues;
                        _ceras.InstanceData.CurrentFixupIndex = i;
                        _ceras.InstanceData.CurrentFixupMember = _members[i].member;
                        _members[i].formatter.DeserializeUntyped(ref reader, fake, FakeContainer.ValueField);
                        memberValues[i] = fake.Value;
                        fake.Value = null;
                        _ceras.InstanceData.CurrentFixupTarget = null;
                        _ceras.InstanceData.CurrentFixupMember = null;
                    }

                    // Phase 2: construct.
                    object[] args = new object[_parameterToMemberIndex.Length];
                    for (int i = 0; i < args.Length; i++)
                        args[i] = memberValues[_parameterToMemberIndex[i]];

                    if (_ctorOrFactory is ConstructorInfo ctor)
                        value = (T)ctor.Invoke(args);
                    else
                        value = (T)_ctorOrFactory.Invoke(_ctorTarget, args);

                    InvokeCallback(_onBeforeDeserialize, value);

                    // Phase 3: assign writable non-ctor-param members.
                    for (int i = 0; i < _members.Length; i++)
                    {
                        if (Array.IndexOf(_parameterToMemberIndex, i) != -1)
                            continue;

                        var m = _members[i];
                        if (m.member is FieldInfo f)
                            f.SetValue(value, memberValues[i]);
                        else if (m.member is PropertyInfo p && p.GetSetMethod(true) != null)
                            p.SetValue(value, memberValues[i]);
                        // getter-only properties are handled in phase 4
                    }

                    // Phase 4: read getter-only members directly into the new object's instances (in-place).
                    for (int i = 0; i < _members.Length; i++)
                    {
                        if (!(_members[i].member is PropertyInfo gop) || gop.GetSetMethod(true) != null)
                            continue;

                        var existing = gop.GetValue(value);
                        if (existing == null)
                            throw new InvalidOperationException($"Getter-only property '{gop.Name}' on '{typeof(T).FullName}' returned null after construction. It must be initialized by the constructor.");

                        _ceras.InstanceData.CurrentFixupTarget = value;
                        _ceras.InstanceData.CurrentFixupMember = gop;
                        _members[i].formatter.DeserializeUntyped(ref reader, value, gop);
                        _ceras.InstanceData.CurrentFixupTarget = null;
                        _ceras.InstanceData.CurrentFixupMember = null;
                    }

                    InvokeCallback(_onAfterDeserialize, value);
                    return;
                }
                else if (_factory != null)
                {
                    value = _factory();
                }

                if (value == null)
                {
                    var tc = _ceras.Config.GetTypeConfig(typeof(T), _isStatic).TypeConstruction;
                    if (!(tc is ConstructNull))
                    {
                        string reason = "";
                        if (CerasSerializer.IsFormatterConstructed(typeof(T)))
                            reason = " (The type is marked as 'Formatter Constructed' globally, likely by a Source Generator test run, which disabled Reflection construction defaults)";

                        throw new InvalidOperationException($"The instance of type '{typeof(T).FullName}' was null, and no constructor or factory was able to create it. This should have been handled by ReferenceFormatter.{reason}");
                    }
                }
            }

            InvokeCallback(_onBeforeDeserialize, value);

            for (int i = 0; i < _members.Length; i++)
            {
                var m = _members[i];
                _ceras.InstanceData.CurrentFixupTarget = value;
                _ceras.InstanceData.CurrentFixupMember = m.member;
                m.formatter.DeserializeUntyped(ref reader, value, m.member);
                _ceras.InstanceData.CurrentFixupTarget = null;
            }

            InvokeCallback(_onAfterDeserialize, value);
        }

        void Initialize()
        {
            // This method is called by CerasSerializer via reflection after all dependencies are injected.
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

        object[] _callbackArgs;
        void InvokeCallback(MethodInfo method, object target)
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
