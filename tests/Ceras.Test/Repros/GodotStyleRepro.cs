using Ceras.Format;
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

    // A plain struct we DO want a generated formatter for.
    //
    // Declared, and that is the point of the declaration: the assembly blacklist above covers the whole
    // `ReproGodot` namespace, so without `[CerasSerializable]` this type would get only the member-less
    // placeholder entry and its member `RailSegment.Dummy` would be written by the reflection fallback —
    // which a robust context now refuses, because the payload's member list would not be the one the
    // manifest describes (Format/RobustReflectionFallbackPolicy). A `[CerasSerializable]` type is an
    // explicit root and wins over the blacklist ("If the user explicitly asks for Vector3, they should get
    // it, even if `Godot.*` is blacklisted"), so the engine *object* below keeps the member-less entry it is
    // meant to have and the plain struct gets a formatter and a matching schema.
    [CerasContext("Tests")]
    [CerasSerializable]
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
        // A custom formatter for a blacklisted engine type now works on BOTH paths. It used to work only
        // in a raw/transient one, because a robust context refused every user custom formatter outright.
        // The reason it works there is not that the bytes became describable on their own — they did not —
        // but that the generator registers a *minimal* manifest entry for a referenced type it emits no
        // formatter for (here: a `Class` with no members, plus the CERAS017 warning that says the member
        // needs a formatter of its own). That entry is where the selection is recorded: it becomes
        // `Opaque`, the payload becomes a declared length in front of the formatter's own bytes, and the
        // fingerprint covers both. A type with no manifest entry at all is still refused, because there
        // would be nowhere to record any of that — see Format/LAYOUT.md, "What is still refused".
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
        /// The robust half of the repro, re-classified twice. It first asserted that a robust save
        /// round-trips the engine-object member through the custom formatter, then that a robust context
        /// refuses the formatter outright; it asserts the round trip again, and the reason it can have it
        /// back is now a fact about the format rather than a gap in it.
        /// <para>
        /// The interpreter case is deliberately not forced here. <c>RailSegment</c> derives from a base
        /// class and holds a type the generator registers with a *minimal* (member-less) entry, so it is a
        /// same-version model, not a cross-version one: its own bytes do not survive a migration whatever
        /// the formatter does. The interpreter's handling of a framed payload — both reading it through the
        /// current formatter and skipping it by its declared length — is covered against a real old file in
        /// Tests/OpaqueCustomPayloadFixtureTests.cs.
        /// </para>
        /// </summary>
        [Fact]
        public void GodotStyleSerialization_Robust_FramesTheCustomFormatter()
        {
            var rail = new RailSegment { Curve = new Curve3D { Points = new[] { 1f, 2f, 3f } } };

            var bytes = new CerasRobustSerializer(RobustConfigWithCustomFormatter()).Save(rail, 1);

            // The manifest entry for the engine type is opaque: the formatter's identity, and no member
            // list — which is the whole difference from the generated reader it replaced.
            var dump = Ceras.CerasRobustSerializer.DumpSchema(bytes);
            Assert.Contains("OPAQUE", dump, StringComparison.Ordinal);
            Assert.Contains(nameof(Curve3DFormatter), dump, StringComparison.Ordinal);

            // A fresh serializer: the matching-layout path, which resolves the custom formatter itself.
            var fresh = new CerasRobustSerializer(RobustConfigWithCustomFormatter()).Load<RailSegment>(bytes, 1);
            Assert.NotNull(fresh.Curve);
            Assert.Equal(new[] { 1f, 2f, 3f }, fresh.Curve!.Points);

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
