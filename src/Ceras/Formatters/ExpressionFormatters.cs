using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Ceras.Formatters
{
    static class ExpressionPayload
    {
        /// <summary>
        /// A value a nested formatter produced is null where the writers below never write a null. That
        /// means the payload does not describe the expression tree it claims to, so it is reported as
        /// data corruption instead of the <see cref="ArgumentNullException"/> the <see cref="Expression"/>
        /// factory methods would throw about a parameter name that says nothing about the file.
        /// </summary>
        public static T Required<T>(T? value, string what) where T : class
            => value ?? throw new Ceras.Exceptions.CerasDataCorruptionException($"Corrupt data: {what} is missing from the serialized expression.");

        /// <summary>
        /// <see cref="LabelTarget.Name"/> is null for a label that has no name (a parameter created
        /// without one), and <see cref="SerializerBinary"/> writes that as a null marker.
        /// <see cref="Expression.Label(Type, string)"/> rejects a null name, so a nameless label is
        /// rebuilt the way the BCL spells one: with an empty name.
        /// </summary>
        public static string LabelName(string? name) => name ?? "";
    }

    public class LabelTargetFormatter : FormatterBase<LabelTarget>
    {
        IFormatter<Type> _typeFormatter;
        internal LabelTargetFormatter(CerasSerializer ceras) { _typeFormatter = ceras.GetFormatter<Type>(); }
        public override void Serialize(IBufferWriter<byte> writer, LabelTarget value) { _typeFormatter.Serialize(writer, value.Type!); SerializerBinary.WriteString(writer, value.Name!); }
        public override void Deserialize(ref SequenceReader<byte> reader, ref LabelTarget value) { Type t = null!; _typeFormatter.Deserialize(ref reader, ref t); var name = SerializerBinary.ReadString(ref reader); value = Expression.Label(ExpressionPayload.Required(t, "the type of a label target"), ExpressionPayload.LabelName(name)); }
    }

    public class LabelFormatter : FormatterBase<LabelExpression>
    {
        IFormatter<LabelTarget> _targetFormatter; IFormatter<Expression> _defaultValueFormatter;
        internal LabelFormatter(CerasSerializer ceras) { _targetFormatter = ceras.GetFormatter<LabelTarget>(); _defaultValueFormatter = ceras.GetFormatter<Expression>(); }
        // A void label has a null DefaultValue, which is a legal value of the property and is written as
        // the null marker by the reference formatter, so the formatter is handed it on purpose.
        public override void Serialize(IBufferWriter<byte> writer, LabelExpression value) { _targetFormatter.Serialize(writer, value.Target); _defaultValueFormatter.Serialize(writer, value.DefaultValue!); }
        public override void Deserialize(ref SequenceReader<byte> reader, ref LabelExpression value) { LabelTarget target = null!; _targetFormatter.Deserialize(ref reader, ref target); Expression defaultValue = null!; _defaultValueFormatter.Deserialize(ref reader, ref defaultValue); value = Expression.Label(ExpressionPayload.Required(target, "the target of a label"), defaultValue); }
    }

    public class MemberAssignmentFormatter : FormatterBase<MemberAssignment>
    {
        IFormatter<System.Reflection.MemberInfo> _memberFormatter; IFormatter<Expression> _expressionFormatter;
        internal MemberAssignmentFormatter(CerasSerializer ceras) { _memberFormatter = ceras.GetFormatter<System.Reflection.MemberInfo>(); _expressionFormatter = ceras.GetFormatter<Expression>(); }
        public override void Serialize(IBufferWriter<byte> writer, MemberAssignment value) { _memberFormatter.Serialize(writer, value.Member); _expressionFormatter.Serialize(writer, value.Expression); }
        public override void Deserialize(ref SequenceReader<byte> reader, ref MemberAssignment value) { System.Reflection.MemberInfo member = null!; _memberFormatter.Deserialize(ref reader, ref member); Expression expression = null!; _expressionFormatter.Deserialize(ref reader, ref expression); value = Expression.Bind(ExpressionPayload.Required(member, "the member of an assignment"), ExpressionPayload.Required(expression, "the expression of an assignment")); }
    }

    public class MemberListBindingFormatter : FormatterBase<MemberListBinding>
    {
        IFormatter<System.Reflection.MemberInfo> _memberFormatter; IFormatter<IEnumerable<ElementInit>> _elementsFormatter;
        internal MemberListBindingFormatter(CerasSerializer ceras) { _memberFormatter = ceras.GetFormatter<System.Reflection.MemberInfo>(); _elementsFormatter = ceras.GetFormatter<IEnumerable<ElementInit>>(); }
        public override void Serialize(IBufferWriter<byte> writer, MemberListBinding value) { _memberFormatter.Serialize(writer, value.Member); _elementsFormatter.Serialize(writer, value.Initializers); }
        public override void Deserialize(ref SequenceReader<byte> reader, ref MemberListBinding value) { System.Reflection.MemberInfo member = null!; _memberFormatter.Deserialize(ref reader, ref member); IEnumerable<ElementInit> initializers = null!; _elementsFormatter.Deserialize(ref reader, ref initializers); value = Expression.ListBind(ExpressionPayload.Required(member, "the member of a list binding"), ExpressionPayload.Required(initializers, "the initializers of a list binding")); }
    }

    public class MemberMemberBindingFormatter : FormatterBase<MemberMemberBinding>
    {
        IFormatter<System.Reflection.MemberInfo> _memberFormatter; IFormatter<IEnumerable<MemberBinding>> _bindingsFormatter;
        internal MemberMemberBindingFormatter(CerasSerializer ceras) { _memberFormatter = ceras.GetFormatter<System.Reflection.MemberInfo>(); _bindingsFormatter = ceras.GetFormatter<IEnumerable<MemberBinding>>(); }
        public override void Serialize(IBufferWriter<byte> writer, MemberMemberBinding value) { _memberFormatter.Serialize(writer, value.Member); _bindingsFormatter.Serialize(writer, value.Bindings); }
        public override void Deserialize(ref SequenceReader<byte> reader, ref MemberMemberBinding value) { System.Reflection.MemberInfo member = null!; _memberFormatter.Deserialize(ref reader, ref member); IEnumerable<MemberBinding> bindings = null!; _bindingsFormatter.Deserialize(ref reader, ref bindings); value = Expression.MemberBind(ExpressionPayload.Required(member, "the member of a member binding"), ExpressionPayload.Required(bindings, "the bindings of a member binding")); }
    }
}
