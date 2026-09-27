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

    [CerasSerializable]
    public abstract class Building
    {
        public float Health = 100;

        public Building() { }
    }

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
                SerializerBinary.WriteInt32(writer, -1);
                return;
            }
            SerializerBinary.WriteInt32(writer, value.Points.Length);
            for (int i = 0; i < value.Points.Length; i++)
                SerializerBinary.WriteFloat32Fixed(writer, value.Points[i]);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref Curve3D value)
        {
            int count = SerializerBinary.ReadInt32(ref reader);
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
        static CerasRobustSerializer MakeSerializer() => new CerasRobustSerializer(config =>
        {
            config.AllowReflectionFallback = true;
            config.UseGeneratedFormatters();
            config.ConfigType<Curve3D>().CustomFormatter = new Curve3DFormatter();
        });

        [Fact]
        public void TestGodotSerialization()
        {
            var robust = MakeSerializer();
            var rail = new RailSegment { Curve = new Curve3D { Points = new[] { 1f, 2f, 3f } } };

            // Standard Save/Load (raw path through robust serializer)
            var data = robust.Save(rail, 1);
            var clone = robust.Load<RailSegment>(data, 1);

            Assert.NotNull(clone.Curve);
            Assert.Equal(3, clone.Curve.Points.Length);

            // Robust Save/Load (fast path — same fingerprint)
            var robustData = robust.Save(rail, 1);
            var robustClone = robust.Load<RailSegment>(robustData, 1);

            Assert.NotNull(robustClone.Curve);
            Assert.Equal(3, robustClone.Curve.Points.Length);

            // Robust Load with a fresh serializer (same schema → fast path again).
            // This is the most realistic scenario: save in one session, load in the next.
            var robust3 = MakeSerializer();
            var robustCloneFresh = robust3.Load<RailSegment>(robustData, 1);
            Assert.NotNull(robustCloneFresh.Curve);
            Assert.Equal(3, robustCloneFresh.Curve.Points.Length);

            // Robust Slow Path (force fingerprint mismatch on a copy)
            var robust2 = new CerasRobustSerializer(config =>
            {
                config.AllowReflectionFallback = true;
                config.UseGeneratedFormatters();
                config.ConfigType<Curve3D>().CustomFormatter = new Curve3DFormatter();
            });

            var slowData = (byte[])robustData.Clone();
            slowData[13] ^= 0xFF;
            FixRobustChecksum(slowData);

            var robustCloneSlow = robust2.Load<RailSegment>(slowData, 1);
            Assert.NotNull(robustCloneSlow.Curve);
            Assert.Equal(3, robustCloneSlow.Curve.Points.Length);
        }

        /// <summary>
        /// Regression for the original bug: Curve3D inherits from Godot.GodotObject so the
        /// source generator used to hard-ban the backing field and silently skip it.
        /// After deserialization Curve was null → NullReferenceException in
        /// VisualUtils.PopulateSplineMultiMesh → VisualRailSegment._Ready.
        /// Fix: engine-object fields are included (CERAS017 warning emitted) rather than
        /// dropped — no [Include] needed.
        /// </summary>
        [Fact]
        public void CurveIsNotNullAfterLoad()
        {
            var robust = MakeSerializer();
            var rail = new RailSegment { Curve = new Curve3D { Points = new[] { 10f, 20f } } };
            var data = robust.Save(rail, 1);

            // Fresh serializer — simulates game restart / new session load
            var loaded = MakeSerializer().Load<RailSegment>(data, 1);

            // Without the [Include] fix this would be null → NullReferenceException in VisualRailSegment._Ready
            Assert.NotNull(loaded.Curve);
            Assert.Equal(2, loaded.Curve.Points.Length);
            Assert.Equal(10f, loaded.Curve.Points[0]);
            Assert.Equal(20f, loaded.Curve.Points[1]);
        }
    }

    [CerasSerializable]
    public class DummyType { public int X; }
}
