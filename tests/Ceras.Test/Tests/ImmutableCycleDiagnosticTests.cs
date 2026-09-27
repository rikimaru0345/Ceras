using System;
using System.Reflection;
using Xunit;
using Ceras;
using Ceras.Formatters;

namespace Ceras.Test
{
    /// <summary>
    /// Unsupported constructor-dependent immutable cycles are diagnosed instead of silently
    /// producing a broken graph. The generator-side check (CERAS026) is covered by
    /// <c>GeneratorDiagnosticsTests</c>; this covers the runtime guard used for types that have no
    /// generated formatter (reflection fallback).
    /// </summary>
    public class ImmutableCycleDiagnosticTests
    {
        // Deliberately not [CerasSerializable], so this type is served by the reflection fallback and
        // the generator never sees it (the runtime guard is the only line of defence).
        public class SelfCycleNoParameterless
        {
            public readonly SelfCycleNoParameterless Next;
            public readonly int Value;

            public SelfCycleNoParameterless(int value)
            {
                Value = value;
            }
        }

        [Fact]
        public void ReflectionFallback_UnsupportedImmutableSelfCycle_ThrowsActionableError()
        {
            var config = new CerasRawConfig { AllowReflectionFallback = true };
            var ceras = new CerasFastSerializer(config);

            // Building the reflection formatter runs VerifyConstructionMethod, which performs the check.
            var ex = Assert.ThrowsAny<Exception>(() => ceras.Serialize(new SelfCycleNoParameterless(1)));

            var message = ex.ToString();
            Assert.Contains("immutable", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Next", message);
            Assert.Contains("construction strategy", message);
            Assert.Contains("parameterless", message, StringComparison.OrdinalIgnoreCase);
        }

        // Same shape but with a parameterless construction path: the cycle is supported and must not
        // be rejected.
        public class SelfCycleWithParameterless
        {
            public readonly SelfCycleWithParameterless Next;
            public readonly int Value;

            public SelfCycleWithParameterless() { }
            public SelfCycleWithParameterless(int value) { Value = value; }
        }

        [Fact]
        public void ReflectionFallback_ImmutableCycle_WithParameterlessCtor_IsSupported()
        {
            var config = new CerasRawConfig { AllowReflectionFallback = true };
            var ceras = new CerasFastSerializer(config);

            var value = new SelfCycleWithParameterless(5);
            typeof(SelfCycleWithParameterless)
                .GetField(nameof(SelfCycleWithParameterless.Next))!
                .SetValue(value, value);

            var data = ceras.Serialize(value);
            var clone = ceras.Deserialize<SelfCycleWithParameterless>(data);

            Assert.NotNull(clone);
            Assert.Same(clone, clone.Next);
            Assert.Equal(5, clone.Value);
        }

        // A mutable self-cycle (no readonly member) is supported and must not be rejected.
        public class MutableSelfCycle
        {
            public MutableSelfCycle Next;
            public int Value;

            public MutableSelfCycle(int value) { Value = value; }
        }

        [Fact]
        public void ReflectionFallback_MutableCycle_IsSupported()
        {
            var config = new CerasRawConfig { AllowReflectionFallback = true };
            var ceras = new CerasFastSerializer(config);

            var value = new MutableSelfCycle(7) { Next = null };
            value.Next = value;

            var data = ceras.Serialize(value);
            var clone = ceras.Deserialize<MutableSelfCycle>(data);

            Assert.NotNull(clone);
            Assert.Same(clone, clone.Next);
            Assert.Equal(7, clone.Value);
        }
    }
}
