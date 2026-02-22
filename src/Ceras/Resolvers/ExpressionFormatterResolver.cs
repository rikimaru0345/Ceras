namespace Ceras.Resolvers
{
    using System;
    using Formatters;
    using System.Linq.Expressions;

    public class ExpressionFormatterResolver : IFormatterResolver
    {
        readonly CerasSerializer _ceras;

        public ExpressionFormatterResolver(CerasSerializer ceras)
        {
            _ceras = ceras;
        }

        public IFormatter GetFormatter(Type type)
        {
            if (type == typeof(LabelTarget))
                return new LabelTargetFormatter(_ceras);

            if (type == typeof(LabelExpression))
                return new LabelFormatter(_ceras);

            if (type == typeof(MemberAssignment))
                return new MemberAssignmentFormatter(_ceras);

            if (type == typeof(MemberListBinding))
                return new MemberListBindingFormatter(_ceras);

            if (type == typeof(MemberMemberBinding))
                return new MemberMemberBindingFormatter(_ceras);

            return null;
        }
    }
}
