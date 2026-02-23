namespace Ceras.Formatters
{
    using Helpers;
    using System;
    using System.Buffers;
    using Ceras.Savegame;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using static System.Linq.Expressions.Expression;

    sealed class DynamicFormatter<T> : FormatterBase<T>, ISchemaTaintedFormatter
    {
        readonly CerasSerializer _ceras;
        SerializeDelegate<T> _serializer;
        readonly bool _isStatic;
        Schema _schema;

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

        public DynamicFormatter(CerasSerializer serializer, bool isStatic)
        {
            _ceras = serializer;
            var type = typeof(T);
            BannedTypes.ThrowIfNonspecific(type);
            var schema = isStatic ? _ceras.GetStaticTypeMetaData(type).PrimarySchema : _ceras.GetTypeMetaData(type).PrimarySchema;
            var typeConfig = _ceras.Config.GetTypeConfig(type, isStatic);
            typeConfig.VerifyConstructionMethod();
            _isStatic = isStatic;
            _schema = schema;
        }

        internal override void Initialize()
        {
            if (_serializer != null) return;
            if (_ceras.Config.Advanced.AotMode == AotMode.None)
                _serializer = GenerateSerializer(_ceras, _schema, false, _isStatic).Compile();
            else
                _serializer = GenerateSerializer_Aot(_ceras, _schema, _isStatic);
        }

        public override void Serialize(IBufferWriter<byte> writer, T value) => _serializer(writer, value);

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            var members = _schema.Members;
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (member.IsSkip) continue;

                var formatter = _ceras.GetReferenceFormatter(member.MemberType);
                formatter.DeserializeUntyped(ref reader, value, member.MemberInfo);
            }
        }

        public void OnSchemaChanged(TypeMetaData meta)
        {
            _schema = meta.CurrentSchema;
            // In AOT mode we don't need to re-generate anything because the interpreter uses _schema directly
            if (_ceras.Config.Advanced.AotMode == AotMode.None)
                _serializer = GenerateSerializer(_ceras, _schema, false, _isStatic).Compile();
            else
                _serializer = GenerateSerializer_Aot(_ceras, _schema, _isStatic);
        }

        internal static SerializeDelegate<T> GenerateSerializer_Aot(CerasSerializer ceras, Schema schema, bool isStatic)
        {
            var members = schema.Members;
            var typeToFormatter = new Dictionary<Type, IFormatter>();
            foreach (var m in members.Where(m => !m.IsSkip).DistinctBy(m => m.MemberType))
                typeToFormatter.Add(m.MemberType, ceras.GetReferenceFormatter(m.MemberType));

            return (writer, value) =>
            {
                foreach (var member in members)
                {
                    if (member.IsSkip) continue;
                    var formatter = typeToFormatter[member.MemberType];
                    object memberValue = null;
                    if (member.MemberInfo is FieldInfo f) memberValue = f.GetValue(value);
                    else if (member.MemberInfo is PropertyInfo p) memberValue = p.GetValue(value);
                    
                    try
                    {
                        formatter.SerializeUntyped(writer, memberValue);
                    }
                    catch (Exception e)
                    {
                        throw new Ceras.Exceptions.CerasException($"Error while serializing member '{member.MemberInfo.Name}' of type '{typeof(T).FullName}': {e.Message}", e);
                    }
                }
            };
        }

        internal static Expression<SerializeDelegate<T>> GenerateSerializer(CerasSerializer ceras, Schema schema, bool isSchemaFormatter, bool isStatic)
        {
            var members = schema.Members;
            var writerArg = Parameter(typeof(IBufferWriter<byte>), "writer");
            var valueArg = Parameter(typeof(T), "value");
            if (isStatic) valueArg = Parameter(typeof(T), "dummy");

            var body = new List<Expression>();
            Dictionary<Type, ConstantExpression> typeToFormatter = new Dictionary<Type, ConstantExpression>();
            foreach (var m in members.Where(m => !m.IsSkip).DistinctBy(m => m.MemberType))
                typeToFormatter.Add(m.MemberType, Constant(ceras.GetReferenceFormatter(m.MemberType)));

            foreach (var member in members)
            {
                if (member.IsSkip) continue;
                var formatterExp = typeToFormatter[member.MemberType];
                var serializeMethod = formatterExp.Value.GetType().ResolveSerializeMethod(member.MemberType);
                body.Add(Call(formatterExp, serializeMethod, writerArg, MakeMemberAccess(valueArg, member.MemberInfo)));
            }

            if (body.Count == 0) body.Add(Empty());
            return Lambda<SerializeDelegate<T>>(Block(body), writerArg, valueArg);
        }

        static MemberExpression MakeMemberAccess(Expression value, MemberInfo member)
        {
            if (member is FieldInfo f) return Field(value, f);
            if (member is PropertyInfo p) return Property(value, p);
            throw new ArgumentException();
        }

        static MethodInfo GetMethodByAttribute(Type objectType, Type attributeType)
        {
            return objectType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.GetCustomAttribute(attributeType) != null);
        }
    }
}
