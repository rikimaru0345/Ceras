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

    abstract class DynamicFormatter
    {
        internal abstract void Initialize();
    }

    sealed class DynamicFormatter<T> : DynamicFormatter, IFormatter<T>
    {
        readonly CerasSerializer _ceras;
        SerializeDelegate<T> _serializer;
        DeserializeDelegate<T> _deserializer;
        readonly bool _isStatic;
        readonly Schema _schema;

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
            _serializer = GenerateSerializer(_ceras, _schema, false, _isStatic).Compile();
            _deserializer = (ref SequenceReader<byte> reader, ref T value) => 
            {
                // SequenceReader is a ref struct, so we can't box it or use it in reflection easily.
                // For the JIT path, we'd need a specialized interpreter.
                throw new NotSupportedException("DynamicFormatter JIT path requires Source Generator for SequenceReader.");
            };
        }

        public void Serialize(IBufferWriter<byte> writer, T value) => _serializer(writer, value);
        public void Deserialize(ref SequenceReader<byte> reader, ref T value) => _deserializer(ref reader, ref value);

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
