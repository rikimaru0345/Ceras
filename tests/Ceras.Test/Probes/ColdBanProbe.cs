// Cold-path probe for the ban verdict cache. NOT compiled into Ceras.Test.
//
// Read at test time and compiled by BanVerdictCacheTests into a console program that is then run as
// a fresh child process. It has to be a separate process: the cache is only empty at the start of one,
// so "the first lookup a type ever gets" is not observable from the shared xUnit process, and a test
// that claimed otherwise would assert nothing. A separate file also keeps this readable instead of
// escaped inside a string literal.
//
// Scenarios (argv[0]): verdicts | save | collectible   (argv[1] = the plugin assembly path)
// Exit code 0 when every check passed. Each check is printed, so a failure names itself.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Ceras;
using Ceras.Formatters;

public class Holder { public int Before; public object Value; public int After; }

[Exclude]
public class ExcludedThing { public int Secret; }

public class CustomEnumerator : System.Collections.IEnumerator
{
    public object Current => null;
    public bool MoveNext() => false;
    public void Reset() { }
}

public static class ColdProbe
{
    static int _failures;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + (detail.Length == 0 ? "" : "   [" + detail + "]"));
        if (!ok) _failures++;
    }

    // Reflection only, so nothing here reaches BannedTypes and runs its static constructor before the
    // scenario's first lookup does.
    static MethodInfo _tryGetBanned, _isBanned;

    static void BindOracle()
    {
        var banned = typeof(BannedTypeException).Assembly.GetType("Ceras.Formatters.BannedTypes", true);
        _tryGetBanned = banned.GetMethod("TryGetBanned", BindingFlags.NonPublic | BindingFlags.Static);
        _isBanned = banned.GetMethod("IsBanned", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Type) }, null);
        if (_tryGetBanned == null || _isBanned == null) throw new Exception("BannedTypes oracle not found");
    }

    static bool Cached(Type type, out string reason)
    {
        var args = new object[] { type, null };
        bool result = (bool)_tryGetBanned.Invoke(null, args);
        reason = args[1] as string;
        return result;
    }

    /// <summary>The pre-cache implementation: the full ban scan, which is what a cached verdict must equal.</summary>
    static bool Uncached(Type type) => (bool)_isBanned.Invoke(null, new object[] { type });

    public static int Main(string[] args)
    {
        var scenario = args.Length > 0 ? args[0] : "verdicts";
        BindOracle();

        if (scenario == "verdicts") Verdicts();
        else if (scenario == "save") Save();
        else if (scenario == "collectible") Collectible(args.Length > 1 ? args[1] : null);
        else { Console.WriteLine("unknown scenario " + scenario); return 2; }

        Console.WriteLine($"({scenario}: {_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    // ── the decisive one: the first lookup in this process is a banned shape ───────────────────

    static void Verdicts()
    {
        bool first = Cached(typeof(Action), out string firstReason);
        Check("first lookup was the banned shape 'System.Action'", first);
        Check("first lookup produced a reason naming the shape",
            firstReason != null && firstReason.IndexOf("delegate", StringComparison.OrdinalIgnoreCase) >= 0,
            firstReason ?? "<null>");

        // The same type again, now served from the table: the verdict must not change, and because the
        // cache keeps the verdict and re-derives the message, neither must the text.
        Check("warm verdict matches cold", Cached(typeof(Action), out _) == first);
        Check("warm reason is byte-identical", Cached(typeof(Action), out string warm) && warm == firstReason);

        // The same cold-then-warm pair for one type per ban rule, since each rule reads the type
        // differently: inheritance from a banned base, a user type that satisfies a custom check, and
        // an [Exclude] on the type itself.
        ColdThenWarm("a user IEnumerator (inheritance rule)", typeof(CustomEnumerator), true, "Enumerator");
        ColdThenWarm("a PropertyInfo (custom check)", typeof(PropertyInfo), true, "reflection");
        ColdThenWarm("an [Exclude]d type (attribute rule)", typeof(ExcludedThing), true, "Exclude");

        // And the control the whole file exists for: types that must NOT be banned, asked cold then warm.
        var allowed = new[] { typeof(int), typeof(string), typeof(List<int>), typeof(Holder), typeof(Type),
                              typeof(int[]), typeof(List<Func<int>>), typeof(Dictionary<string, int>) };
        Check("ordinary types are not banned (cold)", allowed.All(t => !Cached(t, out _)));
        Check("ordinary types are not banned (warm)", allowed.All(t => !Cached(t, out _)));
        Check("a closed generic delegate is banned", Cached(typeof(Func<int, int>), out _));

        // null must be refused, not answered "false" - which would be a silent "null is not banned".
        bool nullThrew = false;
        try { Cached(null, out _); }
        catch (TargetInvocationException e) { nullThrew = e.InnerException != null; }
        Check("a null Type is refused rather than answered", nullThrew);

        // Differential over a working set larger than the table, starting from an empty one, so the
        // first answer for every type in it is the uncached one.
        var all = new List<Type>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
            try { all.AddRange(assembly.GetTypes()); } catch { }
        }
        var distinct = all.Where(t => t != null && !t.IsGenericTypeDefinition).Distinct().ToList();
        int mismatches = distinct.Count(t => Cached(t, out var reason) != Uncached(t) || Uncached(t) != (reason != null));
        Check($"every verdict in a {distinct.Count}-type working set matches the uncached scan, from a cold table",
            mismatches == 0, mismatches + " mismatches");
    }

    static void ColdThenWarm(string label, Type type, bool banned, string reasonContains)
    {
        bool cold = Cached(type, out string coldReason);
        bool warm = Cached(type, out string warmReason);
        Check($"{label} is banned", cold && warm &&
            coldReason != null && coldReason.IndexOf(reasonContains, StringComparison.OrdinalIgnoreCase) >= 0 &&
            warmReason == coldReason, coldReason);
    }

    // ── the same, through the public serializer, as the process's very first operation ─────────

    static void Save()
    {
        var ceras = new CerasFastSerializer(c => c.AllowReflectionFallback = true);

        bool refused = false;
        string detail = "";
        try { ceras.Serialize(new Holder { Before = 1, Value = (Action)(() => { }), After = 2 }); }
        catch (Exception e)
        {
            // ReferenceFormatter adds object context by rethrowing, so the diagnosis the user acts on
            // is the innermost one.
            var banned = Unwrap(e);
            refused = banned != null;
            detail = banned != null ? banned.Message : e.GetType().Name;
        }
        Check("a delegate in an object slot is refused on the first save", refused, Truncate(detail));
        Check("the refusal names the type and the slot it went through",
            detail.IndexOf("Action", StringComparison.Ordinal) >= 0 && detail.IndexOf("Object", StringComparison.Ordinal) >= 0);

        // The robust serializer has its own Type formatter and its own call into the ban check, so it
        // gets the same treatment on the same cold process. object[] is the root because a probe
        // compiled at test time has no generated manifest; the ban check runs before the registry.
        var robust = new CerasRobustSerializer(c => { c.AllowReflectionFallback = true; c.DeclareRoot<object[]>(); });
        Check("the robust serializer refuses it too", Refused(() => robust.Save(new object[] { (Action)(() => { }) }, 1)));

        // The one control that matters here: without it, a probe that refused everything would pass the
        // three checks above. The rest of this behaviour - banned Type values, nulls, declared
        // omissions - is pinned in-process by BannedTypeFailLoudTests, and needs no second copy here.
        var clone = ceras.Deserialize<Holder>(ceras.Serialize(new Holder { Before = 1, Value = new List<int> { 4, 5 }, After = 2 }));
        Check("an ordinary value still round-trips", clone.Before == 1 && clone.After == 2 && ((List<int>)clone.Value).SequenceEqual(new[] { 4, 5 }));
    }

    static bool Refused(Action action)
    {
        try { action(); return false; }
        catch (Exception e) { return Unwrap(e) != null; }
    }

    static BannedTypeException Unwrap(Exception e)
    {
        for (var current = e; current != null; current = current.InnerException)
            if (current is BannedTypeException banned) return banned;
        return null;
    }

    static string Truncate(string s) => s.Length > 160 ? s.Substring(0, 160) + "..." : s;

    // ── composite collectible AssemblyLoadContexts ────────────────────────────────────────────

    // A static field holding a Type is what keeps a collectible AssemblyLoadContext alive, and the
    // verdict cache is exactly such a field. So nothing that reaches the cache from a collectible
    // context may be parked in it.
    //
    // The composites matter more than the direct case, and the reason is the shape of the CLR's type
    // model rather than anything Ceras does. Type.Assembly is the assembly of the *generic type
    // definition*, so List<Plugin> and Dictionary<Plugin,int> report a core assembly even though the
    // Type object holds a reference to Plugin. It is the assembly of the *element* for an array, so a
    // bare Plugin[] is caught but List<Plugin>[] is not. A guard reading only Type.Assembly therefore
    // parks most of these and pins the context anyway - which is what these cases are here to catch.

    static readonly (string Label, Func<Type, Type> Build)[] Composites =
    {
        ("List<Plugin>",           t => typeof(List<>).MakeGenericType(t)),
        ("List<List<Plugin>>",     t => typeof(List<>).MakeGenericType(typeof(List<>).MakeGenericType(t))),
        ("Dictionary<Plugin,int>", t => typeof(Dictionary<,>).MakeGenericType(t, typeof(int))),
        ("Plugin[]",               t => t.MakeArrayType()),
        ("Plugin[][]",             t => t.MakeArrayType().MakeArrayType()),
        ("List<Plugin>[]",         t => typeof(List<>).MakeGenericType(t).MakeArrayType()),
        ("Plugin&",                t => t.MakeByRefType()),
        ("Outer<Plugin>.Inner",    NestedInGeneric),
    };

    static string PluginDll;

    static void Collectible(string pluginDll)
    {
        PluginDll = pluginDll;
        Console.WriteLine("  (plugin assembly: " + pluginDll + ")");

        // The mechanism itself, so a failure below says which part moved instead of "nothing unloaded".
        Check("control: a context is collected when nothing references it",
            Collected(t => t, park: false, ask: false, out _));
        Check("control: a context is NOT collected when the type is parked in a static field",
            !Collected(t => t, park: true, ask: false, out _));

        Check("a context is still collected after the ban check saw the plugin type itself",
            Collected(t => t, park: false, ask: true, out _));

        foreach (var (label, build) in Composites)
        {
            bool collected = Collected(build, park: false, ask: true, out string parked);

            // Whether Type.Assembly alone would have seen the plugin. Reported on the check itself, so
            // a failure says which shape regressed and what it was built from.
            bool hidden = parked.IndexOf("PluginNs", StringComparison.Ordinal) >= 0 && !parked.StartsWith("PluginNs", StringComparison.Ordinal);
            Check("a context is still collected after the ban check saw " + label + " over the plugin type",
                collected, parked + (hidden ? "   [Type.Assembly alone would not see the plugin]" : ""));
        }

        // A banned shape that happens to come from a collectible context: the guard must not have
        // bought its un-parkedness by weakening the verdict.
        var context = new AssemblyLoadContext("banned-plugin", isCollectible: true);
        var enumerable = context.LoadFromAssemblyPath(pluginDll).GetType("PluginNs.Enumerable", true);
        Check("a banned plugin shape is reported as banned, cold and warm",
            Cached(enumerable, out string reason) && reason != null && Cached(enumerable, out _), reason);
        context.Unload();
    }

    static Type NestedInGeneric(Type plugin)
    {
        // Only the *shape* is needed here, never the instance, so it is read through a non-collectible
        // context - a collectible one would be a second context to keep alive.
        var assembly = new AssemblyLoadContext("shape", isCollectible: false).LoadFromAssemblyPath(PluginDll);
        var definition = assembly.GetType("PluginNs.Outer`1", throwOnError: true);
        var nested = assembly.GetType(definition.GetNestedType("Inner", BindingFlags.Public).FullName, throwOnError: true);
        return nested.MakeGenericType(plugin);
    }

    // The GC loop has to run in a frame that no longer holds the type or the context, otherwise it
    // measures the JIT's live locals instead of the CLR. Hence the split.
    static bool Collected(Func<Type, Type> build, bool park, bool ask, out string parked)
    {
        var weak = CreateContext(build, park, ask, out parked);
        for (int i = 0; i < 25; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        return !weak.IsAlive;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference CreateContext(Func<Type, Type> build, bool park, bool ask, out string parked)
    {
        var context = new AssemblyLoadContext("coldprobe-" + _failures + Guid.NewGuid(), isCollectible: true);
        var type = build(context.LoadFromAssemblyPath(PluginDll).GetType("PluginNs.Plain", throwOnError: true));
        parked = type.FullName;

        if (park) _parked = type;                     // the mechanism control
        if (ask) Cached(type, out _);                 // exactly what the write path does for such a value

        var weak = new WeakReference(context);
        context.Unload();
        return weak;
    }

    static object _parked;
}
