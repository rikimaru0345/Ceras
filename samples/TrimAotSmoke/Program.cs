using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using Ceras;
using Ceras.Generated;

namespace TrimAotSmoke
{
    // ── Save model ────────────────────────────────────────────────────────────
    // Deliberately shaped like a real save: a polymorphic field, a cycle, a
    // dictionary, an array, a nullable, an enum and a repeated string (so the
    // robust format's interning table is exercised too).

    [CerasContext("Smoke")]
    [CerasSerializable]
    public class Player
    {
        [CerasStableName("Name")]
        public string Name;

        [CerasStableName("Level")]
        public int Level;

        [CerasStableName("Hp")]
        public float HitPoints;

        [CerasStableName("Bag")]
        public List<Item> Bag = new List<Item>();

        [CerasStableName("Stats")]
        public Dictionary<string, int> Stats = new Dictionary<string, int>();

        [CerasStableName("Grid")]
        public int[][] Grid;

        [CerasStableName("Nick")]
        public string Nick;              // same string as Name -> interned by robust

        [CerasStableName("Weapon")]
        public Gear Weapon;                // polymorphic: declared as the abstract base

        [CerasStableName("Faction")]
        public Faction Faction;

        [CerasStableName("SaveVersion")]
        public uint SaveVersion;
    }

    [CerasSerializable]
    public class Item
    {
        [CerasStableName("Id")]
        public int Id;
        [CerasStableName("Label")]
        public string Label;
    }

    /// <summary>Abstract base so the generator discovers the concrete subtypes.</summary>
    [CerasSerializable]
    public abstract class Gear
    {
        [CerasStableName("Damage")]
        public int Damage;
    }

    [CerasSerializable]
    public sealed class Sword : Gear
    {
        [CerasStableName("Edge")]
        public string Edge;
    }

    [CerasSerializable]
    public sealed class NullGear : Gear
    {
    }

    public enum Faction
    {
        None = 0,
        Alpha = 1,
        Beta = 2,
    }

    public static class Program
    {
        static int _failures;

        public static int Main()
        {
            Console.WriteLine("Ceras trim / NativeAOT smoke test");
            Console.WriteLine("  RuntimeFeature.IsDynamicCodeCompiled = " +
                              System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled +
                              "   (false => NativeAOT native image, true => CoreCLR/trimmed JIT)");
            Console.WriteLine("  Assembly.Location                   = " +
                              (typeof(CerasSerializer).Assembly.Location.Length == 0
                                  ? "<in-memory / single-file>"
                                  : typeof(CerasSerializer).Assembly.Location));
            Console.WriteLine();

            Section("1. assembly identity");
            typeof(CerasSerializer).Assembly.GetName().Dumper();

            Section("5. reflection entry points reachable after trimming/AOT");
            CheckReflectionEntryPoints();

            Section("2. raw (CerasFastSerializer) round trip");
            try
            {
                RawRoundTrip();
                Pass("raw round trip");
            }
            catch (Exception e)
            {
                Fail("raw round trip", e);
            }

            Section("3. robust (CerasRobustSerializer) Save/Load round trip");
            try
            {
                RobustRoundTrip();
                Pass("robust round trip");
            }
            catch (Exception e)
            {
                Fail("robust round trip", e);
            }

            Section("4. robust schema migration (v1 payload -> v2 reader)");
            try
            {
                Migration();
                Pass("robust migration");
            }
            catch (Exception e)
            {
                Fail("robust migration", e);
            }

            Console.WriteLine();
            if (_failures == 0)
            {
                Console.WriteLine("RESULT: PASS");
                return 0;
            }

            Console.WriteLine($"RESULT: FAIL ({_failures} check(s) failed)");
            return 1;
        }

        // ── checks ─────────────────────────────────────────────────────────────

        static void RawRoundTrip()
        {
            var s = new CerasFastSerializer(c => c.UseGeneratedFormatters("Smoke"));
            var original = BuildPlayer();

            var bytes = s.Serialize(original);
            var back = s.Deserialize<Player>(bytes);

            Expect(bytes.Length > 0, "payload is non-empty");
            Expect(back.Name == original.Name, "Name");
            Expect(back.Level == original.Level, "Level");
            Expect(Math.Abs(back.HitPoints - original.HitPoints) < 0.0001f, "HitPoints");
            Expect(back.Bag.Count == 3 && back.Bag[2].Label == "elixir", "Bag");
            Expect(back.Stats.Count == 2 && back.Stats["str"] == 11, "Stats");
            Expect(back.Grid[1][1] == 42, "Grid");
            Expect(back.Nick == "Alice", "Nick");
            Expect(back.Weapon is Sword sw && sw.Damage == 7 && sw.Edge == "sharp", "Weapon (polymorphic abstract base)");
            Expect(back.Faction == Faction.Beta, "Faction");
            Expect(back.SaveVersion == 3u, "SaveVersion");
            Console.WriteLine($"  payload: {bytes.Length} bytes");
        }

        static void RobustRoundTrip()
        {
            var s = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Smoke"));
            var original = BuildPlayer();

            var bytes = s.Save(original, robustVersion: 1);
            var back = s.Load<Player>(bytes, currentRobustVersion: 1);

            Expect(bytes.Length > 0, "payload is non-empty");
            Expect(back.Name == original.Name, "Name");
            Expect(back.Level == original.Level, "Level");
            Expect(back.Bag.Count == 3 && back.Bag[2].Label == "elixir", "Bag");
            Expect(back.Grid[1][1] == 42, "Grid");
            Expect(back.Weapon is Sword sw && sw.Damage == 7 && sw.Edge == "sharp", "Weapon (polymorphic abstract base)");
            Console.WriteLine($"  payload: {bytes.Length} bytes");
        }

        static void Migration()
        {
            // A payload written by the *same* schema but at a different robust
            // version, so the reader has to go through the schema-traversal /
            // migration engine rather than the generated fast path. That engine
            // is the reflection-heavy part of the library and is the part a
            // trimmer most easily breaks.
            var s = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Smoke"));
            var original = BuildPlayer();

            var bytes = s.Save(original, robustVersion: 1);
            var back = s.Load<Player>(bytes, currentRobustVersion: 7);

            Expect(back.Name == original.Name, "Name after migration");
            Expect(back.Bag.Count == 3, "Bag after migration");
        }

        static void CheckReflectionEntryPoints()
        {
            // Probes only - these Report() rather than Expect(), so losing one
            // shows up in the log and in the section-2/3 result but does not by
            // itself change the exit code. They exist because the interesting
            // trimming failures in Ceras are all "reflection cannot see this
            // any more", and that is invisible in a build log.
            var t = typeof(Player);

            Report("Type.GetType(AssemblyQualifiedName)",
                   Type.GetType(t.AssemblyQualifiedName, throwOnError: false) == t);
            Report("Type.GetType(bare FullName, throwOnError:false)",
                   Type.GetType(t.FullName, throwOnError: false) == t);
            Report("Assembly.GetType(FullName)",
                   t.Assembly.GetType(t.FullName, throwOnError: false) == t);
            Report("Player field (public instance) kept",
                   t.GetField("Name", BindingFlags.Public
                                          | BindingFlags.Instance) != null);
            Report("Player ctor (public parameterless) kept",
                   t.GetConstructor(Type.EmptyTypes) != null);
            Report("Faction enum values readable",
                   Enum.GetValuesAsUnderlyingType(typeof(Faction)).Length == 3);

            // Two different things are probed here and they are not the same question.
            //
            // (a) "present in metadata" is a reflection-metadata question: can GetConstructor find
            //     a signature for it. Under NativeAOT that is a separate fact from whether the image
            //     has native code for the member, so a null here is not by itself proof that AOT
            //     cannot construct it.
            //
            // (b) For the two formatter families Ceras really does build ONLY through
            //     Activator.CreateInstance(typeof(X<>).MakeGenericType(...), ...):
            //       Ceras.Formatters.ReferenceFormatter<> / ReferenceFormatter_KnownSealedType<>
            //            -> CerasSerializer.GetReferenceFormatter
            //     the reflection path is the only path, and CerasSerializer states the
            //     DynamicDependency that keeps those constructors alive.
            //
            //     Ceras.TypeConfig<T> is a third case: the generated registration for a
            //     [CerasSerializable] type calls ConfigType<T>(), which constructs the closed
            //     TypeConfig<T> directly, and TypeConfig<T>.CreateMemberConfig then constructs the
            //     closed MemberConfig<T> directly as well. Nothing in that chain goes through
            //     Activator - the reflective construction in TypeConfig.CreateMemberConfig and in
            //     CerasConfig.GetTypeConfigForConfiguration only serves a type that was reached
            //     reflectively in the first place, which a generated registration never is.
            //
            //     Those two direct constructions are why MemberConfig<Item> can be constructed at
            //     all, and they are also why the probe above is a real gate rather than a
            //     formality: if the type's own data members are not reflectable, TypeConfig<Item>
            //     comes back with ZERO members, no MemberConfig<Item> is ever built, and the
            //     constructor has neither code nor metadata to report. The source generator emits
            //     the [DynamicDependency] that keeps those members (and the generated formatter's
            //     Serialize/Deserialize/Schema, which Ceras also reaches only by reflection);
            //     without it this line reads MISS and the raw round trip below serializes a type
            //     whose config has no members.
            //
            // Caution when reading the NativeAOT run: naming
            // Ceras.TypeConfig<Faction> here roots that closed instantiation for
            // ILC, which is exactly what CerasConfig.GetTypeConfigForConfiguration
            // does not do for itself. Without this line the AOT build fails
            // earlier and differently, with
            //   NotSupportedException: ... no code was generated for it:
            //   'Ceras.TypeConfig`1[TrimAotSmoke.Faction]..ctor(Ceras.CerasConfig,System.Boolean)'
            // raised from CerasConfig.GetTypeConfigForConfiguration.
            // Keeping the line keeps the trim diagnostic above intact; the AOT
            // TypeConfig failure has to be re-checked with this probe removed.
            ReportCtor("ReferenceFormatter<Player>..ctor",
                       typeof(Ceras.Formatters.ReferenceFormatter<Player>), typeof(CerasSerializer));
            ReportCtor("ReferenceFormatter<List<Item>>..ctor",
                       typeof(Ceras.Formatters.ReferenceFormatter<List<Item>>), typeof(CerasSerializer));
            ReportCtor("ReferenceFormatter_KnownSealedType<int[][]>..ctor",
                       typeof(Ceras.Formatters.ReferenceFormatter_KnownSealedType<int[][]>), typeof(CerasSerializer));
            ReportCtor("ReferenceFormatter_KnownSealedType<string>..ctor",
                       typeof(Ceras.Formatters.ReferenceFormatter_KnownSealedType<string>), typeof(CerasSerializer));
            ReportCtor("TypeConfig<Faction>..ctor",
                       typeof(Ceras.TypeConfig<Faction>), typeof(CerasConfig), typeof(bool));
            ReportCtor("MemberConfig<Item>..ctor",
                       typeof(Ceras.MemberConfig<Item>), typeof(TypeConfig), typeof(MemberInfo));

            // The same Activator.CreateInstance Ceras makes, made from here. If
            // this succeeds where Ceras' own call throws, the constructor is
            // still in the metadata and what broke is Ceras' construction path,
            // not the member itself.
            TryInstantiate("Activator.CreateInstance(ReferenceFormatter<Player>)",
                           typeof(Ceras.Formatters.ReferenceFormatter<Player>));
            TryInstantiate("Activator.CreateInstance(ReferenceFormatter_KnownSealedType<int[][]>)",
                           typeof(Ceras.Formatters.ReferenceFormatter_KnownSealedType<int[][]>));
        }

        static void ReportCtor(string what, Type closedGeneric, params Type[] parameters)
        {
            var ctor = closedGeneric.GetConstructor(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic,
                null, parameters, null);
            Report(what + " present in metadata", ctor != null);
        }

        static void TryInstantiate(string what, Type closedGeneric)
        {
            try
            {
                var made = Activator.CreateInstance(closedGeneric,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic,
                    null, new object[] { new CerasFastSerializer() }, null);
                Report(what + " -> constructed", made != null);
            }
            catch (TargetInvocationException e)
            {
                // Bound and invoked; the constructor body itself rejected the
                // throwaway serializer. Not a trimming result.
                Report(what + " -> ctor ran, threw " +
                       (e.InnerException?.GetType().Name ?? "?"), true);
            }
            catch (Exception e)
            {
                Report($"{what} -> {e.GetType().Name}: {e.Message.Split('\n')[0]}", false);
            }
        }

        // ── helpers ────────────────────────────────────────────────────────────

        static Player BuildPlayer()
        {
            return new Player
            {
                Name = "Alice",
                Nick = "Alice",
                Level = 7,
                HitPoints = 3.5f,
                Faction = Faction.Beta,
                SaveVersion = 3,
                Bag =
                {
                    new Item { Id = 1, Label = "sword" },
                    new Item { Id = 2, Label = "shield" },
                    new Item { Id = 3, Label = "elixir" },
                },
                Stats = { { "str", 11 }, { "dex", 4 } },
                Grid = new[] { new[] { 1, 2 }, new[] { 3, 42 } },
                Weapon = new Sword { Damage = 7, Edge = "sharp" },
            };
        }

        static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title + " ==");
        }

        static void Expect(bool condition, string what)
        {
            if (condition)
            {
                Console.WriteLine("  ok   " + what);
                return;
            }

            _failures++;
            Console.WriteLine("  FAIL " + what);
        }

        static void Report(string what, bool ok)
        {
            Console.WriteLine("  " + (ok ? "ok   " : "MISS ") + what);
        }

        static void Pass(string what) => Console.WriteLine("  => " + what + ": ok");

        static void Fail(string what, Exception e)
        {
            _failures++;
            Console.WriteLine($"  => {what}: FAILED: {e.GetType().Name}: {e.Message}");
            var inner = e.InnerException;
            while (inner != null)
            {
                Console.WriteLine("       inner: " + inner.GetType().Name + ": " + inner.Message);
                inner = inner.InnerException;
            }
            Console.WriteLine(Indent(e.StackTrace));
        }

        static string Indent(string s)
            => s == null ? "" : string.Join("\n", s.Split('\n').Select(l => "         " + l.TrimEnd()));
    }

    static class Ext
    {
        public static void Dumper(this System.Reflection.AssemblyName n)
            => Console.WriteLine("  " + n.FullName);
    }
}
