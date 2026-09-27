using Ceras.Formatters;
using Ceras.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ceras
{
#pragma warning disable 0649 // Field is never assigned to (reflection/internal use)

    using Exceptions;

    public abstract class TypeConstruction
    {
        internal TypeConfig TypeConfig = null!; // Not as clean as I'd like, this can't be set from a protected base ctor, because users might eventually want to create their own

        internal abstract bool HasDataArguments { get; }
        internal abstract Func<object> GetRefFormatterConstructor();

        internal virtual void VerifyReturnType() { }

        internal virtual void VerifyParameterMapping() { }


        protected void VerifyMethodReturn(MethodBase methodBase)
        {
            if (methodBase.IsAbstract)
                throw new InvalidOperationException($"The given method '{methodBase.Name}' is abstract so it can not be used to construct anything.");

            // Example:
            // object -> Animal -> Cat
            // Ctor gives us a cat, we need an animal -> everything ok

            Type resultType;

            if (methodBase is MethodInfo m)
            {
                resultType = m.ReturnType;
            }
            else if (methodBase is ConstructorInfo c)
            {
                resultType = c.DeclaringType; // the "result" of a ctor is its declaring type
            }
            else
                throw new NotImplementedException("this helper method cannot handle a member info of type " + methodBase.GetType().FullName);

            // Can we use the result to assign it to the needed type?
            var neededType = TypeConfig.Type;

            if (!neededType.IsAssignableFrom(resultType))
                throw new InvalidOperationException($"The given method or constructor returns a '{Format.TypeIdentity.GetCanonicalNameOrUnknown(resultType)}' which is not compatible to the needed type '{neededType.FullName}'");
        }

        protected void VerifyParameterMapping(MethodBase methodBase)
        {
            var parameters = methodBase.GetParameters();
            if (parameters.Length == 0)
                return;

            var map = TypeConfig.ParameterMap ?? (TypeConfig.ParameterMap = new Dictionary<ParameterInfo, MemberInfo>());

            var memberConfigs = TypeConfig.Members.Where(mc => mc.ComputeFinalInclusionFast()).ToArray();

            foreach (var parameterInfo in parameters)
            {
                // Are we still missing a mapping for this member?
                if (!map.TryGetValue(parameterInfo, out MemberInfo sourceMember))
                {
                    var caseInsensitiveMatches = memberConfigs.Where(mc => string.Equals(parameterInfo.Name, mc.Member.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (SetMatchOrThrow(parameterInfo, caseInsensitiveMatches))
                        continue;
                    
                    var tolerantMatches = memberConfigs.Where(mc => string.Equals(parameterInfo.Name, MiscHelpers.CleanMemberName(mc.Member.Name), StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (SetMatchOrThrow(parameterInfo, tolerantMatches))
                        continue;

                    var ctorParamStrings = string.Join(", ", parameters.Select(t => t.ParameterType.FriendlyName() + " " + t.Name));

                    // Check if the member exists but is excluded — gives a much more actionable error than "not found"
                    var excludedMatch = TypeConfig._allMembers.FirstOrDefault(mc =>
                        (string.Equals(parameterInfo.Name, mc.Member.Name, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(parameterInfo.Name, MiscHelpers.CleanMemberName(mc.Member.Name), StringComparison.OrdinalIgnoreCase))
                        && !mc.ComputeFinalInclusionFast());

                    if (excludedMatch != null)
                        throw new CerasException($"Ceras automatically selected the constructor '({ctorParamStrings})' for type '{Format.TypeIdentity.GetCanonicalNameOrUnknown(TypeConfig.Type)}', but the parameter '{parameterInfo.ParameterType.FriendlyName()} {parameterInfo.Name}' matches member '{excludedMatch.Member.Name}' which is excluded from serialization (reason: {excludedMatch.IncludeExcludeReason ?? "unknown"}). To fix this: either include the member with [Include], change ReadonlyFieldHandling, or configure a different constructor via ConfigType<T>().ConstructBy(...).");

                    throw new CerasException($"There is no mapping specified from the members of '{Format.TypeIdentity.GetCanonicalNameOrUnknown(TypeConfig.Type)}' to the constructor '({ctorParamStrings})'. Ceras has tried to automatically detect a mapping by matching the names of the fields/properties to the method parameters, but no source field or property could be found to populate the parameter '{parameterInfo.ParameterType.FriendlyName()} {parameterInfo.Name}'");
                }
                else
                {
                    // We already have a user-provided match, but is it part of the serialization?
                    var sourceMemberConfig = TypeConfig.Members.First(mc => mc.Member == sourceMember);
                    if (!sourceMemberConfig.ComputeFinalInclusionFast())
                        throw new CerasException($"The construction mode for the type '{Format.TypeIdentity.GetCanonicalNameOrUnknown(TypeConfig.Type)}' is invalid because the parameter '{parameterInfo.ParameterType.FriendlyName()} {parameterInfo.Name}' is supposed to be initialized from the member '{sourceMember.FieldOrPropType().FriendlyName()} {sourceMember.Name}', but that member is not part of the serialization, so it will not be available at deserialization-time.");
                }
            }

            bool SetMatchOrThrow(ParameterInfo p, MemberConfig[] configs)
            {
                if (configs.Length > 1)
                    throw new AmbiguousMatchException($"There are multiple members that match the parameter '{p.ParameterType.FriendlyName()} {p.Name}': {string.Join(", ", configs.Select(c => c.Member.Name))}");

                if(configs.Length == 0)
                    return false;

                map.Add(p, configs[0].Member);
                return true;
            }
        }



        #region Factory Methods

        public static TypeConstruction Null() => ConstructNull.Instance;

        public static TypeConstruction ByStaticMethod(MethodInfo methodInfo) => new ConstructByMethod(methodInfo);
        public static TypeConstruction ByStaticMethod(Expression<Func<object>> expression) => new ConstructByMethod(((MethodCallExpression)expression.Body).Method);

        public static TypeConstruction ByConstructor(ConstructorInfo constructorInfo) => new SpecificConstructor(constructorInfo);

        public static TypeConstruction ByUninitialized() => new UninitializedObject();

        // public static TypeConstruction ByUninitialized(ConstructorInfo directCtor) => ...;
        // public static TypeConstruction ByCombination() ... // allow to specify what should happen if there's an object (reset it? or just overwrite?) and how to create a new one


        #endregion
    }


    // Aka "FormatterConstructed"
    class ConstructNull : TypeConstruction
    {
        public static ConstructNull Instance { get; } = new ConstructNull();

        ConstructNull() { }

        internal override bool HasDataArguments => false;
        internal override Func<object>? GetRefFormatterConstructor() => () => null;
    }

    class SpecificConstructor : TypeConstruction
    {
        internal ConstructorInfo Constructor;

        public SpecificConstructor(ConstructorInfo constructor)
        {
            Constructor = constructor;
        }

        internal override bool HasDataArguments => Constructor.GetParameters().Length > 0;
        internal override Func<object>? GetRefFormatterConstructor()
        {
            var ctor = Constructor;
            if (ctor.GetParameters().Length == 0)
                return () => ctor.Invoke(null);

            throw new NotSupportedException($"Parameterized constructors (Type: {Format.TypeIdentity.GetCanonicalNameOrUnknown(ctor.DeclaringType)}) are no longer supported because the fixup system was removed. Use a parameterless constructor or 'ConstructByUninitialized()'.");
        }

        internal override void VerifyReturnType() => VerifyMethodReturn(Constructor);
        internal override void VerifyParameterMapping() => VerifyParameterMapping(Constructor);
    }

    class ConstructByMethod : TypeConstruction
    {
        internal readonly MethodInfo Method;
        internal readonly object TargetObject;

        internal ConstructByMethod(MethodInfo staticMethod)
        {
            if (!staticMethod.IsStatic)
                throw new InvalidOperationException($"You have provided an instance method without a target object");

            Method = staticMethod;
        }

        internal ConstructByMethod(object targetObject, MethodInfo instanceMethod)
        {
            if (instanceMethod.IsStatic)
                throw new InvalidOperationException("You have provided target-instance but the given method is a static method");
            if (targetObject == null)
                throw new ArgumentNullException(nameof(targetObject), "The given method requires an instance (a targetObject), but you have given 'null'");

            Method = instanceMethod;
            TargetObject = targetObject;
        }


        internal override bool HasDataArguments => Method.GetParameters().Length > 0;
        internal override Func<object>? GetRefFormatterConstructor()
        {
            if (Method.GetParameters().Length > 0)
                throw new NotSupportedException($"Factory methods with parameters (Method: {Format.TypeIdentity.GetCanonicalNameOrUnknown(Method.DeclaringType)}.{Method.Name}) are no longer supported because the fixup system was removed. Use a parameterless method or 'ConstructByUninitialized()'.");

            if (Method.IsStatic)
                return () => Method.Invoke(null, null);
            else
                return () => Method.Invoke(TargetObject, null);
        }

        internal override void VerifyReturnType() => VerifyMethodReturn(Method);
        internal override void VerifyParameterMapping() => VerifyParameterMapping(Method);
    }

    class UninitializedObject : TypeConstruction
    {
        ConstructorInfo _directConstructor = null!;

        internal override bool HasDataArguments => _directConstructor != null && _directConstructor.GetParameters().Length > 0;

        internal override Func<object>? GetRefFormatterConstructor()
        {
            var t = TypeConfig.Type;
            return () => RuntimeHelpers.GetUninitializedObject(t);
        }

        internal override void VerifyReturnType()
        {
            if (_directConstructor != null)
                if (_directConstructor.DeclaringType != base.TypeConfig.Type)
                    throw new InvalidOperationException($"The given constructor is not part of the type '{base.TypeConfig.Type.FullName}'");
        }
    }
}
