using System;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Ceras.Helpers
{
    internal static class Instantiator
    {
        /// <summary>
        /// Creates an instance of <paramref name="t"/>. Never returns null: <see cref="Activator"/> either
        /// produces one or throws (a missing constructor, a throwing constructor, a null result from a
        /// value-type default). Every caller casts the result, so a nullable return only spread noise.
        /// <para>
        /// <paramref name="t"/> is deliberately not annotated
        /// <see cref="System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute"/>, so ILLink
        /// reports IL2067 against this line and keeps reporting it. The annotation would be a true
        /// statement of what
        /// <see cref="Activator.CreateInstance(Type, BindingFlags, Binder, object[], System.Globalization.CultureInfo)"/>
        /// needs, but it is not one anything can meet: every caller builds <paramref name="t"/> with
        /// <c>MakeGenericType</c> over a runtime <see cref="Type"/>, so the requirement would have to be
        /// pushed onto that runtime type and then out through the whole internal call chain, ending at
        /// the public <c>GetFormatter&lt;T&gt;</c>/<c>Serialize&lt;T&gt;</c> surface - where it would be an
        /// obligation on the caller to preserve the constructors of their own serialized types, which
        /// is not what <c>Activator</c> is about here. Measured on the trim/AOT smoke, adding it roots
        /// nothing the two dependencies do not already root, and moves IL2067 off this line and onto
        /// the callers that cannot meet it - four more analysis warnings in
        /// <c>Config/TypeConfig.cs</c>, <c>Formatters/MemberInfoFormatter.cs</c>,
        /// <c>Formatters/ReflectionFallbackFormatter.cs</c> and
        /// <c>Resolvers/StandardFormatterResolver.cs</c>, none of which has anything to do with the
        /// formatter this library reflects over. The members that do have to survive are rooted where
        /// the type that needs them is built: <c>CerasSerializer.GetReferenceFormatter</c> states
        /// <c>DynamicDependency</c> for <c>ReferenceFormatter&lt;&gt;</c> and
        /// <c>ReferenceFormatter_KnownSealedType&lt;&gt;</c>, the only two formatter families constructed
        /// through this method whose constructors no compilation names.
        /// </para>
        /// <para>
        /// <b>A Ceras refusal arrives as itself, not wrapped.</b> <see cref="Activator"/> wraps
        /// anything a constructor throws in a <see cref="TargetInvocationException"/>, and this is
        /// the one place the library constructs a formatter, so every refusal Ceras raises from a
        /// formatter constructor used to reach the caller inside that wrapper. The refusals are
        /// deliberate, typed and actionable — <c>TypeConfig.VerifyNoBannedMemberShapes</c> throws
        /// <see cref="Formatters.BannedTypeException"/>, <c>FixedBufferGuard.Reject</c> throws
        /// <see cref="Exceptions.CerasSchemaException"/>, and
        /// <c>ReflectionFallbackFormatter&lt;T&gt;</c>'s constructor runs both plus the construction
        /// checks — and the wrapper undoes all of that. The caller was shown
        /// <c>System.Reflection.TargetInvocationException: Exception has been thrown by the target
        /// of an invocation.</c> with the whole diagnosis collapsed onto one <c>---&gt;</c> line of a
        /// stack-trace dump, and <c>catch (CerasSchemaException)</c> could not reach the refusal at
        /// all — which is the one thing those exception types exist to offer. So a wrapped Ceras
        /// exception is rethrown here as itself.
        /// </para>
        /// <para>
        /// <see cref="ExceptionDispatchInfo.Capture(Exception)"/> and not <c>throw inner</c>: the
        /// diagnosis is raised deep inside a resolver, several frames from this method, and rethrowing
        /// an exception object with a plain <c>throw</c> discards its stack trace and restarts it at
        /// the <c>Activator</c> frame. Capturing first keeps the throw site, which is where the
        /// member, the type and the member's own resolution are on the stack — the part that says
        /// <em>which</em> member of which type led here.
        /// </para>
        /// <para>
        /// One level is all that is unwrapped, and one level is all that is needed. A formatter that
        /// resolves another formatter goes through a <c>Create</c> of its own, so by the time an
        /// outer constructor's wrapper is produced the inner one has already been removed: a refusal
        /// buried N formatters deep arrives as one typed exception rather than N nested wrappers.
        /// </para>
        /// <para>
        /// <b>What is deliberately not unwrapped.</b> The line is drawn on the <em>type</em> of the
        /// exception that was wrapped, and on nothing else. Everything else keeps the wrapper it
        /// arrives in today: a <see cref="MissingMethodException"/> (the type has no constructor this
        /// call can bind), a <see cref="TargetException"/> or
        /// <see cref="TargetParameterCountException"/> from the binder, and any exception a
        /// constructor of <em>user</em> code threw that is not a Ceras exception. That last one is
        /// also why the test is the type rather than the provenance: the wrapper is sometimes the
        /// only signal an exception has. <c>TypeConfigDefaults.CheckStructWithAutoProps</c> raises its
        /// auto-property warning as a <see cref="TargetInvocationException"/> around a plain
        /// <see cref="Exception"/>, and a user who wraps something in a reflection exception has said
        /// something about how it should be reported, which this method does not get to overrule.
        /// Conversely, a <see cref="Exceptions.CerasException"/> reaching this point is a refusal by
        /// contract, whoever raised it, and a caller who catches
        /// <see cref="Exceptions.CerasException"/> wants exactly that object rather than a wrapper
        /// around it.
        /// </para>
        /// <para>
        /// One level means one level, too: a <see cref="TargetInvocationException"/> whose inner
        /// exception is <em>another</em> <see cref="TargetInvocationException"/> is not dug through,
        /// even when a Ceras exception is visible further down. The inner wrapper was put there by the
        /// constructor's own code, which is exactly the code whose decision about reporting this
        /// exception is not this method's to overrule.
        /// </para>
        /// <para>
        /// <see cref="MethodAccessException"/> and <see cref="TargetException"/> are not treated as
        /// wrappers either, and that is a statement about the runtime rather than a preference: the
        /// CLR wraps a constructor's own exception in <see cref="TargetInvocationException"/> and in
        /// nothing else. Those two report that the constructor could not be <em>reached</em> — access
        /// denied, or a signature that does not bind — so there is no exception the constructor threw
        /// for them to be carrying, and treating them as wrappers would be a guess.
        /// </para>
        /// </summary>
        public static object Create(Type t, params object[] args)
        {
            try
            {
                return Activator.CreateInstance(t, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)
                    ?? throw new MissingMethodException(
                        $"Activator returned null for '{t}', which has no usable instance constructor for the arguments given.");
            }
            // The handler is reachable for one case only, and that is the whole job of the filter:
            // nothing else can get into the body, so no unrelated failure can be reclassified or
            // swallowed here — every other exception propagates exactly as it did before.
            catch (TargetInvocationException wrapped) when (wrapped.InnerException is Exceptions.CerasException refusal)
            {
                ExceptionDispatchInfo.Capture(refusal).Throw();
                throw;   // unreachable: Capture(..).Throw() always throws. Here to satisfy the compiler.
            }
        }
    }
}
