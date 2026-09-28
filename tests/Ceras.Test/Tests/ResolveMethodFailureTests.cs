using System;
using System.Buffers;
using System.Reflection;
using Ceras.Exceptions;
using Ceras.Formatters;
using Xunit;

namespace Ceras.Test
{
    /// <summary>
    /// Pins what <c>ReflectionHelper.ResolveMethod</c> does when a formatter declares no method of the
    /// name and shape a dispatcher needs, and — just as importantly — where a caller may ask for one.
    /// <para>
    /// The lookup is by name and parameter types, which is what lets one dispatcher bind a delegate to
    /// every formatter. A formatter whose methods implement the interface <em>explicitly</em> is the one
    /// way to be a valid <c>IFormatter&lt;T&gt;</c> and still be invisible to it: reflection names such
    /// a method after the interface (<c>IFormatter`1.Serialize</c>), never <c>Serialize</c>.
    /// </para>
    /// <para>
    /// There are two dispatchers and they differ in what they need. The one that binds a delegate
    /// cannot work without the method, so a miss there is a real error and is reported as one — naming
    /// the formatter, the method and the parameter types, instead of leaving
    /// <see cref="Delegate.CreateDelegate(Type, object, MethodInfo)"/> to throw a bare
    /// <see cref="ArgumentNullException"/> that names neither. The other calls the formatter through
    /// <c>IFormatter&lt;T&gt;</c> and never uses a <see cref="MethodInfo"/> at all, so a formatter that
    /// only implements the interface explicitly is usable there. Asking that second dispatcher for a
    /// method it would not have used would refuse a dispatch that works, so the ask stays inside the
    /// branch that binds one. The two tests below are the same formatter written the same way, and the
    /// only difference between them is which dispatcher runs.
    /// </para>
    /// </summary>
    public class ResolveMethodFailureTests
    {
        /// <summary>The declared type of a slot whose runtime value is the <see cref="DerivedTarget"/>.</summary>
        public class Target
        {
            public int Value;
        }

        /// <summary>A runtime type that only ever appears in a <see cref="Target"/>-typed slot.</summary>
        public class DerivedTarget : Target
        {
            public int Extra;
        }

        /// <summary>
        /// Implements the interface explicitly, so both methods exist and satisfy the interface while
        /// reflection names them <c>IFormatter&lt;…&gt;.Serialize</c> / <c>…Deserialize</c>. Writes the
        /// whole payload, so a round trip through it is a real check and not a no-op.
        /// </summary>
        public class ExplicitTargetFormatter : IFormatter<Target>
        {
            void IFormatter<Target>.Serialize(IBufferWriter<byte> writer, Target value) => SerializerBinary.WriteInt32(writer, value.Value);
            void IFormatter<Target>.Deserialize(ref SequenceReader<byte> reader, ref Target value)
                => value = new Target { Value = SerializerBinary.ReadInt32(ref reader) };
            public void SerializeUntyped(IBufferWriter<byte> writer, object value) { }
            public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member) { }
        }

        /// <summary>The same, for the derived runtime type and its extra member.</summary>
        public class ExplicitDerivedFormatter : IFormatter<DerivedTarget>
        {
            void IFormatter<DerivedTarget>.Serialize(IBufferWriter<byte> writer, DerivedTarget value)
            {
                SerializerBinary.WriteInt32(writer, value.Value);
                SerializerBinary.WriteInt32(writer, value.Extra);
            }
            void IFormatter<DerivedTarget>.Deserialize(ref SequenceReader<byte> reader, ref DerivedTarget value)
                => value = new DerivedTarget
                {
                    Value = SerializerBinary.ReadInt32(ref reader),
                    Extra = SerializerBinary.ReadInt32(ref reader),
                };
            public void SerializeUntyped(IBufferWriter<byte> writer, object value) { }
            public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member) { }
        }

        /// <summary>
        /// The same formatter written the ordinary way, for the control: a method declared under its
        /// own name is found, so the exact-type dispatch works. Without it the failure test could pass
        /// for a reason that has nothing to do with explicit implementations.
        /// </summary>
        public class ImplicitTargetFormatter : IFormatter<Target>
        {
            public void Serialize(IBufferWriter<byte> writer, Target value) => SerializerBinary.WriteInt32(writer, value.Value);
            public void Deserialize(ref SequenceReader<byte> reader, ref Target value)
                => value = new Target { Value = SerializerBinary.ReadInt32(ref reader) };
            public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Target)value);
            public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member) { }
        }

        /// <summary>
        /// (b) The exact-type path binds a delegate to the formatter's own <c>Serialize</c>, so the
        /// method has to exist and a miss is an error: reported by name, not as a null nobody can call.
        /// </summary>
        [Fact]
        public void The_Exact_Type_Path_Reports_A_Missing_Method_By_Name()
        {
            var ceras = new CerasFastSerializer(c => c.ConfigType<Target>().SetFormatter(new ExplicitTargetFormatter()));

            // It is accepted as a formatter for the type (a closed IFormatter<T> for exactly it), so the
            // failure has to come from the dispatch, and it has to come from the save.
            Assert.IsType<ExplicitTargetFormatter>(ceras.GetSpecificFormatter(typeof(Target)));

            var failure = Assert.Throws<CerasException>(() => ceras.Serialize(new Target { Value = 42 }));

            // Names the formatter, the method and the parameter types it asked for ...
            Assert.Contains(nameof(ExplicitTargetFormatter), failure.Message);
            Assert.Contains("Serialize(", failure.Message);
            Assert.Contains("IBufferWriter", failure.Message);
            Assert.Contains(nameof(Target), failure.Message);
            // ... and says what to do about it, because "no such method" alone is not actionable.
            Assert.Contains("explicitly", failure.Message);
            Assert.Contains(nameof(FormatterBase<Target>), failure.Message);
        }

        /// <summary>
        /// (a) A derived runtime type in a <see cref="Target"/>-typed slot is dispatched through
        /// <c>IFormatter&lt;DerivedTarget&gt;</c> rather than through a bound delegate, so it never
        /// needs the by-name lookup — and must keep round-tripping with a formatter that only implements
        /// the interface explicitly. The lookup used to be asked for anyway, and refusing a dispatch
        /// that works is what this pins shut.
        /// </summary>
        [Fact]
        public void A_Derived_Runtime_Type_Through_A_Base_Typed_Slot_Still_Round_Trips()
        {
            var ceras = new CerasFastSerializer(c => c.ConfigType<DerivedTarget>().SetFormatter(new ExplicitDerivedFormatter()));

            // The declared type is the base, so the dispatcher resolves the derived runtime type and
            // takes the interface-based branch - the one that has no MethodInfo to resolve.
            var clone = ceras.Deserialize<Target>(ceras.Serialize<Target>(new DerivedTarget { Value = 42, Extra = 7 }));

            var derived = Assert.IsType<DerivedTarget>(clone);
            Assert.Equal(42, derived.Value);
            Assert.Equal(7, derived.Extra);
        }

        /// <summary>The control: a method declared under its own name is found, and used.</summary>
        [Fact]
        public void The_Control_A_Formatter_That_Declares_The_Method_Is_Still_Found_And_Used()
        {
            var ceras = new CerasFastSerializer(c => c.ConfigType<Target>().SetFormatter(new ImplicitTargetFormatter()));

            var clone = ceras.Deserialize<Target>(ceras.Serialize(new Target { Value = 42 }));

            Assert.Equal(42, clone.Value);
        }
    }
}
