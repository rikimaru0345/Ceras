using Ceras;
using Ceras.Formatters;
using Ceras.Generated;
using System;
using System.Buffers;
using Xunit;

// Simulate the game's assembly-level configuration
[assembly: CerasGeneratorBlacklist("ReproGodot.*")]


// Minimal stub so the source generator sees global::Godot.GodotObject in this compilation
// and applies the GodotObject ban — exactly as it would in the real Godot game project.
namespace Godot
{
    public class GodotObject { }
    public class RefCounted : GodotObject { }
}

namespace ReproGodot
{
    // Simulate a Godot class (must be in the blacklisted namespace).
    // Inherits from Godot.GodotObject so the source-generator marks it as a banned type.
    // A custom formatter (Curve3DFormatter) is the only way to serialize it — [Include] is
    // required on any field/property that holds it to opt out of the ban.
    public class Curve3D : global::Godot.RefCounted
    {
        public float[] Points = Array.Empty<float>();
    }

    // A plain struct we DO want a generated formatter for
    public struct Vector3
    {
        public float X, Y, Z;
    }
}

namespace ReproGame
{
    using ReproGodot;

    [CerasContext("Tests")]
    [CerasSerializable]
    public abstract class Building
    {
        public float Health = 100;

        public Building() { }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class RailSegment : Building
    {
        // Curve3D inherits from Godot.GodotObject.  The source generator emits CERAS017 to
        // remind us that a custom IFormatter<Curve3D> must be registered at runtime — but it
        // still includes the field so that a user who says "serialize all fields" doesn't get
        // silent null on load (principle of least surprise).
        public Curve3D Curve { get; set; }

        public Vector3 Dummy;
    }

    public class Curve3DFormatter : IFormatter<Curve3D>
    {
        public void Serialize(IBufferWriter<byte> writer, Curve3D value)
        {
            if (value == null)
            {
                SerializerBinary.WriteInt32Fixed(writer, -1);
                return;
            }
            SerializerBinary.WriteInt32Fixed(writer, value.Points.Length);
            for (int i = 0; i < value.Points.Length; i++)
                SerializerBinary.WriteFloat32Fixed(writer, value.Points[i]);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref Curve3D value)
        {
            int count = SerializerBinary.ReadInt32Fixed(ref reader);
            if (count == -1)
            {
                value = null;
                return;
            }
            if (value == null) value = new Curve3D();
            value.Points = new float[count];
            for (int i = 0; i < count; i++)
                value.Points[i] = SerializerBinary.ReadFloat32Fixed(ref reader);
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value) => Serialize(writer, (Curve3D)value);
        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, System.Reflection.MemberInfo member)
        {
            var f = (System.Reflection.FieldInfo)member;
            var val = (Curve3D)f.GetValue(container);
            Deserialize(ref reader, ref val);
            f.SetValue(container, val);
        }
    }

    public class GodotStyleRepro : Ceras.Test.TestBase
    {
        // A custom formatter for a blacklisted engine type is supported in a raw/transient context, and
        // is refused in a robust one: Curve3DFormatter writes an opaque count + float array that the
        // file's schema section cannot describe, so a build that removed or retyped `Points` could not
        // skip the value and would silently return wrong data. See RobustCustomFormatterPolicy.
        static CerasFastSerializer MakeTransientSerializer() => new CerasFastSerializer(config =>
        {
            config.AllowReflectionFallback = true;
            config.UseGeneratedFormatters("Tests");
            config.ConfigType<Curve3D>().CustomFormatter = new Curve3DFormatter();
        });

        static Action<CerasRobustConfig> RobustConfigWithCustomFormatter() => config =>
        {
            config.AllowReflectionFallback = true;
            config.UseGeneratedFormatters("Tests");
            config.ConfigType<Curve3D>().CustomFormatter = new Curve3DFormatter();
        };

        // The same context *without* the custom formatter, so a formatter can be installed after the
        // serializer already exists.
        static CerasRobustSerializer MakeRobustSerializerWithoutCustomFormatter()
            => new CerasRobustSerializer(config =>
            {
                config.AllowReflectionFallback = true;
                config.UseGeneratedFormatters("Tests");
            });

        [Fact]
        public void GodotStyleSerialization_Raw()
        {
            var transient = MakeTransientSerializer();
            var rail = new RailSegment { Curve = new Curve3D { Points = new[] { 1f, 2f, 3f } } };

            var data = transient.Serialize(rail);
            var clone = transient.Deserialize<RailSegment>(data);

            Assert.NotNull(clone.Curve);
            Assert.Equal(3, clone.Curve.Points.Length);

            // A fresh serializer, i.e. the realistic "next session" shape.
            var cloneFresh = MakeTransientSerializer().Deserialize<RailSegment>(data);
            Assert.NotNull(cloneFresh.Curve);
            Assert.Equal(3, cloneFresh.Curve.Points.Length);
        }

        /// <summary>
        /// The robust half of the repro, re-classified. It used to assert that a robust save round-trips
        /// the engine-object member through the custom formatter; a robust context now refuses that
        /// formatter outright instead of writing a file whose schema does not describe its payload.
        /// </summary>
        [Fact]
        public void GodotStyleSerialization_Robust_RefusesTheCustomFormatter()
        {
            var ex = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(
                () => new CerasRobustSerializer(RobustConfigWithCustomFormatter()));

            Assert.Contains("Curve3D", ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(Curve3DFormatter), ex.Message, StringComparison.Ordinal);

            // Also refused when installed after the serializer exists: a TypeConfig is sealed lazily, so
            // the setup-time check alone is not enough to keep a file from being written.
            var robust = MakeRobustSerializerWithoutCustomFormatter();
            robust.Config.ConfigType<Curve3D>().CustomFormatter = new Curve3DFormatter();

            var rail = new RailSegment { Curve = new Curve3D { Points = new[] { 1f, 2f, 3f } } };
            var loadEx = Assert.Throws<Ceras.Exceptions.CerasSchemaException>(() => robust.Save(rail, 1));
            Assert.Contains("Curve3D", loadEx.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Regression for the original bug: Curve3D inherits from Godot.GodotObject so the
        /// source generator used to hard-ban the backing field and silently skip it.
        /// After deserialization Curve was null → NullReferenceException in
        /// VisualUtils.PopulateSplineMultiMesh → VisualRailSegment._Ready.
        /// Fix: engine-object fields are included (CERAS017 warning emitted) rather than
        /// dropped — no [Include] needed. Re-classified to the raw context, which is where a
        /// custom formatter for an engine type is supported.
        /// </summary>
        [Fact]
        public void CurveIsNotNullAfterLoad()
        {
            var transient = MakeTransientSerializer();
            var rail = new RailSegment { Curve = new Curve3D { Points = new[] { 10f, 20f } } };
            var data = transient.Serialize(rail);

            // Fresh serializer — simulates game restart / new session load
            var loaded = MakeTransientSerializer().Deserialize<RailSegment>(data);

            // Without the [Include] fix this would be null → NullReferenceException in VisualRailSegment._Ready
            Assert.NotNull(loaded.Curve);
            Assert.Equal(2, loaded.Curve.Points.Length);
            Assert.Equal(10f, loaded.Curve.Points[0]);
            Assert.Equal(20f, loaded.Curve.Points[1]);
        }
    }

    [CerasContext("Tests")]
    [CerasSerializable]
    public class DummyType { public int X; }
}
