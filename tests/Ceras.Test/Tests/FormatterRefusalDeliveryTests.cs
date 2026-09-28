// Ceras's own refusals must reach the caller as themselves.
//
// Every formatter in this library is built by reflection, through `Helpers.Instantiator.Create`, and
// `Activator.CreateInstance` wraps whatever a constructor throws in a `TargetInvocationException`. A
// formatter constructor is where Ceras raises its deliberate, typed refusals — `BannedTypeException`
// from `TypeConfig.VerifyNoBannedMemberShapes`, `CerasSchemaException` from `FixedBufferGuard.Reject`
// — so every one of them used to reach the caller as
//
//     System.Reflection.TargetInvocationException: Exception has been thrown by the target of an invocation.
//     ---> Ceras.Exceptions.CerasSchemaException: Member 'Buffer' (fixed System.Byte[25], 25 bytes) ...
//
// i.e. an actionable diagnosis demoted to one line of a stack-trace dump, and `catch (CerasSchemaException)`
// — the entire reason that type exists — unable to reach it. The repository had already paid for this
// twice: `BannedTypeFailLoudTests` and `FixedBufferStructTests` each unwrapped the chain by hand, which
// hid the wrapping from the very tests that exist to pin the refusal.
//
// What is pinned here, at the choke point rather than through a serializer:
//   * a Ceras exception raised by a constructed type arrives as itself, with nothing wrapped around it;
//   * with the throw site still on the stack, so the frames that say *which* member of which type led
//     here survive — `throw inner` would restart the trace at the `Activator` frame and lose them;
//   * an exception that is *not* a Ceras refusal keeps the wrapper it arrived in. The unwrapping is
//     not "unwrap everything Activator caught": that would reinterpret a third party's wrapper, and
//     would also dismantle a wrapper somebody else built on purpose;
//   * a wrapper around somebody else's wrapper is left alone, even when a Ceras refusal is visible
//     underneath it — one level is the level the runtime put there, and digging further would mean
//     re-deciding a report that is not ours;
//   * a type with no constructor this call can bind still fails as `MissingMethodException`, which is
//     what the trimming story in `CerasSerializer.GetReferenceFormatter` depends on;
//   * and end to end, a refusal buried several formatters deep arrives as one typed exception rather
//     than a stack of wrappers.
using System;
using System.Linq;
using System.Reflection;
using Ceras;
using Ceras.Exceptions;
using Ceras.Helpers;
using Ceras.Test.Tests.FixedBuffer;
using Xunit;

namespace Ceras.Test.Tests
{
    public class FormatterRefusalDeliveryTests : TestBase
    {
        // ── the models: what a constructed type can fail with ──────────────────────────────────

        /// <summary>
        /// Stands in for a formatter constructor that refuses, which is what
        /// <c>TypeConfig.VerifyNoBannedMemberShapes</c> and <c>FixedBufferGuard.Reject</c> do from
        /// inside <c>ReflectionFallbackFormatter&lt;T&gt;</c>'s constructor. The argument is a
        /// <see cref="CerasSerializer"/> because that is what the real constructors are handed; it is
        /// ignored, because the point is the exception and not what would have been built.
        /// </summary>
        sealed class RefusingFormatter
        {
            internal const string Refusal = "Member 'Buffer' is not describable, and here is what to do about it.";

            internal RefusingFormatter(CerasSerializer serializer)
            {
                throw new CerasSchemaException(Refusal);
            }
        }

        /// <summary>
        /// The control: the same thing failing for a reason that is not Ceras's to classify — a
        /// user's own code refusing, or a library of theirs. This wrapper stays.
        /// </summary>
        sealed class ForeignRefusingFormatter
        {
            internal const string Refusal = "the user's own construction, which Ceras only relays";

            internal ForeignRefusingFormatter(CerasSerializer serializer)
            {
                throw new NotSupportedException(Refusal);
            }
        }

        /// <summary>
        /// The one constructor takes two arguments, so handing this method one leaves it with nothing
        /// to bind — the situation a trimmed build produces for a formatter whose constructor was
        /// removed, and the one the <c>DynamicDependency</c> attributes on
        /// <c>CerasSerializer.GetReferenceFormatter</c> exist to prevent.
        /// </summary>
        sealed class NoUsableConstructor
        {
            internal NoUsableConstructor(CerasSerializer serializer, int count)
            {
            }
        }

        /// <summary>
        /// Refuses through its <em>own</em> reflection, which is what user code reached from inside a
        /// construction callback does, and lets that wrapper escape. What
        /// <see cref="Instantiator.Create"/> then catches is a wrapper around a wrapper, and the
        /// exception under both of them is a Ceras refusal.
        /// </summary>
        sealed class RefusingThroughItsOwnReflection
        {
            internal const string Refusal = "a refusal raised by a callback that used reflection itself";

            internal RefusingThroughItsOwnReflection(CerasSerializer serializer)
            {
                typeof(RefusingThroughItsOwnReflection)
                    .GetMethod(nameof(Raise), BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, null);
            }

            static void Raise() => throw new CerasSchemaException(Refusal);
        }

        // ── the fix ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ACerasRefusal_FromAConstructor_ReachesTheCallerAsItself()
        {
            var refusal = Assert.Throws<CerasSchemaException>(
                () => Instantiator.Create(typeof(RefusingFormatter), Fast()));

            Assert.Equal(RefusingFormatter.Refusal, refusal.Message);

            // And nothing is wrapped around it, which is the whole point: a caller catching
            // CerasSchemaException - or CerasException - gets the refusal, not a reflection wrapper
            // whose InnerException happens to be one.
            Assert.Null(refusal.InnerException);
        }

        [Fact]
        public void TheRefusal_StillCarriesTheStackTraceFromWhereItWasThrown()
        {
            // `throw inner` would discard this: the trace would restart at Instantiator.Create and
            // the frames naming the type and member that led here would be gone, which is the part of
            // a diagnosis that says *where* the problem is. ExceptionDispatchInfo keeps them, and
            // appends the rethrow site below them.
            var refusal = Assert.Throws<CerasSchemaException>(
                () => Instantiator.Create(typeof(RefusingFormatter), Fast()));

            var top = (refusal.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";

            Assert.Contains(nameof(RefusingFormatter), top, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(Instantiator), top, StringComparison.Ordinal);
        }

        // ── the line: what is deliberately not unwrapped ────────────────────────────────────────

        [Fact]
        public void AnExceptionThatIsNotACerasRefusal_KeepsItsWrapper()
        {
            // Not Ceras's own typed refusal, so the reflection wrapper stays. The user is told what
            // their own code threw, wrapped the way reflection always wraps it - which is what they
            // would see from any other reflection call they make themselves.
            var wrapper = Assert.Throws<TargetInvocationException>(
                () => Instantiator.Create(typeof(ForeignRefusingFormatter), Fast()));

            var inner = Assert.IsType<NotSupportedException>(wrapper.InnerException);
            Assert.Equal(ForeignRefusingFormatter.Refusal, inner.Message);
        }

        [Fact]
        public void AWrapperAroundSomebodyElsesWrapper_IsNotDugThrough()
        {
            // One level, and only the level the runtime put there. Here the inner
            // TargetInvocationException is one the constructor's own code created, and a Ceras
            // exception does sit under it - but deciding whether to unwrap that would mean
            // re-deciding a report somebody else already shaped, so the outer wrapper stays as it is.
            var wrapper = Assert.Throws<TargetInvocationException>(
                () => Instantiator.Create(typeof(RefusingThroughItsOwnReflection), Fast()));

            var inner = Assert.IsType<TargetInvocationException>(wrapper.InnerException);
            Assert.Equal(RefusingThroughItsOwnReflection.Refusal, Assert.IsType<CerasSchemaException>(inner.InnerException).Message);
        }

        [Fact]
        public void ATypeWithNoConstructorThisCallCanBind_StillFailsAsMissingMethod()
        {
            // The trimming story depends on this one: a formatter whose constructor was removed has to
            // surface as MissingMethodException (see the DynamicDependency notes on
            // CerasSerializer.GetReferenceFormatter), and adding the unwrapping must not have turned
            // it into something a caller would mistake for a refusal. Assert.Throws is an exact-type
            // match, so this also pins that nothing reclassified it on the way out.
            Assert.Throws<MissingMethodException>(
                () => Instantiator.Create(typeof(NoUsableConstructor), Fast()));
        }

        // ── end to end: the nesting a real serializer produces ────────────────────────────────

        [Fact]
        public void ARefusalBuriedUnderOtherFormatters_ArrivesAsOneTypedException()
        {
            // The inner struct is resolved while the outer's formatter is still being built, and each
            // of those is its own Activator call - so the refusal is two constructions deep, and used
            // to arrive as two stacked TargetInvocationExceptions. One level of unwrapping per
            // construction is enough, and the caller gets the refusal once.
            var refusal = Assert.Throws<CerasSchemaException>(
                () => Fast().GetSpecificFormatter(typeof(ContainsFixedBufferStruct)));

            Assert.Contains("Buffer", refusal.Message, StringComparison.Ordinal);
            Assert.Null(refusal.InnerException);
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────

        static CerasFastSerializer Fast() => new CerasFastSerializer(new CerasRawConfig { AllowReflectionFallback = true });
    }
}
