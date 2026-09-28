// The ban verdict cache must be transparent, including on the very first call in a process.
//
// BannedTypes.TryGetBanned used to run the whole ban scan - the shape rules plus a materialization of
// the type's complete attribute list - once per *value written*. It is now served from a bounded
// direct-mapped table keyed by the Type. Two things have to stay true for that to be a pure
// optimization, and the two halves of this file test them from opposite ends:
//
//   * the verdict is the same function of the Type it was before - on a hit and on a miss, in any
//     order, from any thread, and after as many evictions as the table can produce. Every assertion
//     below is differential against that old implementation, which is still present and unchanged as
//     BannedTypes.IsBanned(Type): saying "the cache does not change the answer" any other way would
//     mean restating the answer.
//
//   * nothing about *caching* it is observable. In particular a static table of Type references keeps
//     a collectible AssemblyLoadContext alive for the life of the process, which would be a new,
//     permanent leak for a plugin host.
//
// The cold half cannot live here at all: this process is shared by the whole test run, so "the first
// invocation in the process" is not observable from it, and a test claiming otherwise asserts nothing.
// Those cases are compiled into a console program and run as a child process - see
// Probes/ColdBanProbe.cs, which also carries the detail on why a composite type can hide a plugin one
// from a Type.Assembly check.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Ceras;
using Ceras.Formatters;

namespace Ceras.Test.Banned
{
    public class BanVerdictCacheTests : TestBase
    {
        // ── eviction: a working set larger than the table ────────────────────────────────────

        [Fact]
        public void Collisions_OverAWorkingSetLargerThanTheTable_NeverChangeAVerdict()
        {
            var types = WorkingSet();

            // Two full passes. The first populates and evicts as it goes; the second runs against a
            // table the first pass has already left in a collided state, so most lookups miss.
            for (int pass = 0; pass < 2; pass++)
                foreach (var type in types)
                    AssertSameAsScan(type, $"pass {pass}: '{type}'");
        }

        // ── concurrency: one shared table, many writers and readers ──────────────────────────

        [Fact]
        public void ConcurrentLookups_NeverReturnAVerdictTheScanDisagreesWith()
        {
            // Every type in one flat list, so a racing writer publishing one type's verdict over
            // another's slot is as likely as anything else in this loop.
            var all = WorkingSet()
                .Concat(new[] { typeof(Action), typeof(Func<int>), typeof(System.Collections.IEnumerator), typeof(System.Reflection.PropertyInfo) })
                .Concat(new[] { typeof(int), typeof(string), typeof(List<int>), typeof(Holder) })
                .ToList();

            int errors = 0;
            string firstError = null;

            Parallel.For(0, 8, worker =>
            {
                var random = new Random(worker);
                for (int i = 0; i < 40000; i++)
                {
                    Type type = all[random.Next(all.Count)];
                    bool cached = BannedTypes.TryGetBanned(type, out string reason);
                    bool expected = BannedTypes.IsBanned(type);

                    if (expected != cached || expected != (reason != null))
                    {
                        Interlocked.Increment(ref errors);
                        if (firstError == null)
                            firstError = $"'{type}': cached={(cached ? "banned" : "allowed")}, scan={(expected ? "banned" : "allowed")}, reason={reason ?? "<null>"}";
                    }
                }
            });

            Assert.True(errors == 0, $"{errors} of 320000 lookups disagreed with the scan. First: {firstError}");
        }

        [Fact]
        public void ConcurrentSaves_BannedValueIsRejectedOnEveryThread()
        {
            // The same table, reached through the public API on eight threads with eight serializers.
            // A banned value has to be refused every single time: the table is process-wide, so a
            // "sometimes accepted" result here would be silent data loss.
            int accepted = 0;
            string firstAccept = null;

            Parallel.For(0, 8, _ =>
            {
                var ceras = Raw();
                for (int i = 0; i < 2000; i++)
                    try
                    {
                        ceras.Serialize(new Holder { Before = 1, Value = (Action)(() => { }), After = 2 });
                        Interlocked.Increment(ref accepted);
                        if (firstAccept == null) firstAccept = "a delegate was written instead of refused";
                    }
                    catch (Exception e) when (Unwrap(e) != null) { }
            });

            Assert.True(accepted == 0, $"{accepted} of 16000 saves wrote a banned value. First: {firstAccept}");
        }

        // ── the reason text, which the cache stores no part of ───────────────────────────────

        [Fact]
        public void Reason_IsTheSameTextOnEveryInvocation()
        {
            // The cache stores the verdict, not the message, and re-derives the message by asking
            // again - so the text has to be identical every time, or a user whose type is banned gets
            // one diagnosis the first time and a different one afterwards. One type per ban rule.
            var cases = new[]
            {
                typeof(Action), typeof(Func<int, int>), typeof(System.Collections.IEnumerator),
                typeof(System.Reflection.PropertyInfo), typeof(ExcludedThing), typeof(System.IO.FileSystemInfo),
            };

            foreach (var type in cases)
            {
                BannedTypes.TryGetBanned(type, out string first);
                BannedTypes.TryGetBanned(type, out string second);

                Assert.True(!string.IsNullOrEmpty(first), $"'{type}' was not reported as banned at all");
                Assert.Equal(first, second);
            }
        }

        // ── the rules the verdict is derived from, one case per rule ──────────────────────────

        [Fact]
        public void Inheritance_ExcludeAndGenericBans_AreDecidedPerConstructedType()
        {
            // Each rule reads the type differently, and the table is keyed by the Type, so a derived
            // type, an [Exclude]d type and a closed generic are each their own key. Both answers have to
            // come out of the table unchanged, and a type that merely sits next to one stays allowed.
            Assert.True(BannedTypes.TryGetBanned(typeof(CustomEnumerator), out string enumerator));
            Assert.Contains("Enumerator", enumerator, StringComparison.Ordinal);

            Assert.True(BannedTypes.TryGetBanned(typeof(ExcludedThing), out string excluded));
            Assert.Contains("Exclude", excluded, StringComparison.Ordinal);

            Assert.True(BannedTypes.TryGetBanned(typeof(Func<int>), out string closed));
            Assert.Contains("delegate", closed, StringComparison.OrdinalIgnoreCase);
            Assert.True(BannedTypes.TryGetBanned(typeof(Func<>), out _), "the open delegate definition is still a Delegate");

            Assert.False(BannedTypes.TryGetBanned(typeof(Holder), out _));
            Assert.False(BannedTypes.TryGetBanned(typeof(NotAnEnumerator), out _));
            Assert.False(BannedTypes.TryGetBanned(typeof(List<Func<int>>), out _), "a container of a banned type is the user's declaration");
            Assert.False(BannedTypes.TryGetBanned(typeof(List<>), out _));
        }

        // ── shapes and helpers ────────────────────────────────────────────────────────────────

        public class Holder { public int Before; public object Value; public int After; }

        [Exclude]
        public class ExcludedThing { public int Secret; }

        public class CustomEnumerator : System.Collections.IEnumerator
        {
            public object Current => null;
            public bool MoveNext() => false;
            public void Reset() { }
        }

        public class NotAnEnumerator { public int Value; }

        static CerasFastSerializer Raw() => new CerasFastSerializer(new CerasRawConfig { AllowReflectionFallback = true });

        // The refusal arrives wrapped: ReferenceFormatter adds object context by rethrowing, so the
        // diagnosis the user acts on is the innermost one.
        static BannedTypeException Unwrap(Exception e)
        {
            for (var current = e; current != null; current = current.InnerException)
                if (current is BannedTypeException match) return match;
            return null;
        }

        static void AssertSameAsScan(Type type, string where)
        {
            bool cached = BannedTypes.TryGetBanned(type, out string reason);
            bool expected = BannedTypes.IsBanned(type);

            Assert.True(expected == cached, $"{where}: cached as {(cached ? "banned" : "allowed")} but the scan says {(expected ? "banned" : "allowed")}");
            Assert.Equal(expected, reason != null);
        }

        // More distinct types than the table has slots, so the loops above genuinely evict. Taken from
        // the assemblies this process has already loaded, which is what an application looks like to the
        // table. Generic type definitions are dropped: they behave differently under the ban rules and
        // would make a failure harder to read.
        List<Type> WorkingSet()
        {
            var all = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
                try { all.AddRange(assembly.GetTypes()); }
                catch (ReflectionTypeLoadException e) { all.AddRange(e.Types.Where(t => t != null)); }
                catch { }
            }

            var distinct = all.Where(t => t != null && !t.IsGenericTypeDefinition).Distinct().ToList();

            // The table is a power-of-two mask; a test that does not know its size cannot tell whether
            // a working set is actually evicting anything.
            var table = typeof(BannedTypeException).Assembly
                .GetType("Ceras.Formatters.BannedTypes", throwOnError: true)
                .GetField("_verdictCache", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.True(table != null, "the verdict table is gone; this file is testing nothing");
            int slots = ((Array)table.GetValue(null)).Length;
            Assert.True(distinct.Count > slots, $"Only {distinct.Count} distinct types are available here, against {slots} cache slots.");
            return distinct;
        }

        // ── the cold half: a fresh process per scenario ──────────────────────────────────────

        [Fact]
        public void ColdProcess_TheFirstLookupIsABannedShape_AndItIsRefused()
        {
            var result = RunCold("verdicts");

            Assert.True(result.ExitCode == 0, $"Cold probe 'verdicts' failed:\n{result.Output}");
            // The scenario is worthless if its first lookup was not a banned shape, so the probe reports
            // what it started with and this pins that. If the order in the probe ever changes, this fails
            // before the checks that depend on it can pass for the wrong reason.
            Assert.Contains("first lookup was the banned shape 'System.Action'", result.Output);
        }

        [Fact]
        public void ColdProcess_ASaveThatMustBeRefusedIsRefusedOnTheVeryFirstOperation()
        {
            var result = RunCold("save");

            Assert.True(result.ExitCode == 0, $"Cold probe 'save' failed:\n{result.Output}");
        }

        [Fact]
        public void ColdProcess_TheCacheDoesNotKeepACollectibleLoadContextAlive()
        {
            var result = RunCold("collectible");

            Assert.True(result.ExitCode == 0, $"Cold probe 'collectible' failed:\n{result.Output}");
        }

        /// <summary>Runs one probe scenario in a child process, and hands back everything it printed.</summary>
        static (int ExitCode, string Output) RunCold(string scenario)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ceras-coldprobe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string probeDll = Path.Combine(dir, "ColdProbe.dll");
                string pluginDll = Path.Combine(dir, "ColdBanProbePlugin.dll");
                CompileProbes(probeDll, pluginDll);

                // The probe references only Ceras.dll, which itself depends on nothing outside the shared
                // framework, so these files are a runnable app.
                File.Copy(typeof(Ceras.CerasSerializer).Assembly.Location, Path.Combine(dir, "Ceras.dll"));
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Ceras.Test.runtimeconfig.json"),
                          Path.Combine(dir, "ColdProbe.runtimeconfig.json"));

                var psi = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add(probeDll);
                psi.ArgumentList.Add(scenario);
                // The collectible scenario loads this from a real file: a type that already exists in the
                // test process lives in the default context and proves nothing about unloading.
                psi.ArgumentList.Add(pluginDll);

                using Process process = Process.Start(psi);
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                string output = stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n--- stderr ---\n" + stderr);

                if (!process.WaitForExit(180_000))
                {
                    process.Kill(entireProcessTree: true);
                    return (-1, "the probe hung\n" + output);
                }

                return (process.ExitCode, output);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Compiles the two files in Probes/. The reference set is the whole shared framework rather than
        /// the assemblies this host happens to have loaded, because the probe uses Console and Parallel
        /// and those facades are not loaded here - the reference set the generator tests build would not
        /// compile it.
        /// </summary>
        static void CompileProbes(string probeDll, string pluginDll)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            var errors = new List<string>();
            foreach (var (name, output, kind) in new[]
                     {
                         ("ColdBanProbe.cs", probeDll, OutputKind.ConsoleApplication),
                         ("ColdBanProbePlugin.cs", pluginDll, OutputKind.DynamicallyLinkedLibrary),
                     })
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Probes", name);
                var compilation = CSharpCompilation.Create(
                    Path.GetFileNameWithoutExtension(name),
                    new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp14)) },
                    references,
                    new CSharpCompilationOptions(kind));

                using var stream = File.Create(output);
                errors.AddRange(compilation.Emit(stream).Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => name + d));
            }

            Assert.True(errors.Count == 0,
                "The cold probe no longer compiles, so the cold-path checks above are not running at all:\n"
                + string.Join("\n", errors));
        }
    }
}
