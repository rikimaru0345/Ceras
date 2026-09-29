// Writes the checked-in golden fixtures for the renamed-open-generic cross-version tests.
//
// This is deliberately NOT a test project and is not referenced by tests/Ceras.Test: the old CLR types
// (Legacy.*) must be absent from the reader build, so a reader test can only ever see the bytes and
// the current model.
//
// Run it only when intentionally updating the fixtures:
//
//     dotnet run --project fixtures/Ceras.GenericAliasWriter -c Release -- --out tests/Ceras.Test/Fixtures/GenericAliasV1
//
// Then review the diff: every byte change is a change to what old saves look like.
using System;
using System.Collections.Generic;
using System.IO;
using Ceras;
using Ceras.Generated;

namespace Ceras.GenericAliasWriter
{
    static class Program
    {
        const string Context = "GenericAliasV1";

        static int Main(string[] args)
        {
            string outDir = null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--out") outDir = args[i + 1];

            if (outDir == null)
            {
                Console.Error.WriteLine("usage: --out <directory>");
                return 2;
            }

            Directory.CreateDirectory(outDir);
            foreach (var (name, bytes) in Fixtures())
            {
                string path = Path.Combine(outDir, name);
                File.WriteAllBytes(path, bytes);
                Console.WriteLine($"wrote {path} ({bytes.Length} bytes)");
            }
            return 0;
        }

        /// <summary>
        /// The build timestamp is pinned to "unknown" so regenerating with unchanged inputs produces
        /// identical bytes: a fixture diff then always means a real change.
        /// </summary>
        static CerasRobustSerializer NewSerializer() => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Context);
            c.SetUnknownBuildTimestamp();
        });

        static IEnumerable<(string Name, byte[] Bytes)> Fixtures()
        {
            var serializer = NewSerializer();

            var save = new Legacy.Save
            {
                Version = 3,
                Ints = new List<Legacy.Box<int>>
                {
                    new Legacy.Box<int> { Value = 11 },
                    new Legacy.Box<int> { Value = 22 },
                },
                Text = new Legacy.Box<string> { Value = "three" },
                Point = new Legacy.Box<Legacy.Coord> { Value = new Legacy.Coord { X = 5, Y = 6, Z = 7 } },
                Pair = new Legacy.Crate<int, string> { First = 8, Second = "eight" },
                Cell = new Legacy.Box<int>.Cell<Legacy.Coord>
                {
                    Payload = new Legacy.Coord { X = 9, Y = 10, Z = 11 },
                },
                Slot = new Legacy.Box<int> { Value = 1000 },
                Marker = new Legacy.Coord { X = 11, Y = 12, Z = 13 },
                Tail = "TAIL-SENTINEL",
            };

            yield return ("Save.bin", serializer.Save(save, 1));

            // A renamed closed generic as a root of its own, so the resolution of its type identity is
            // pinned where it is identity-critical: a wrong root has to be rejected, not misread.
            yield return ("TextBox.bin", serializer.Save(save.Text, 1));

            // The collection of closed generics as a root of its own: the element type's identity is
            // the only thing that has to be rebuilt.
            yield return ("IntBoxes.bin", serializer.Save(save.Ints, 1));

            // The generic whose member order is fixed by its definition. `Wrap<Coord>` is closed over a
            // type whose name sorts before System.Int32, so it is also the closure whose *substituted*
            // order happens to agree with the written one; the writer still emits Value before Tag,
            // because a declared type parameter sorts under its position rather than under its name. It
            // is carried by a renamed type with a trailing sentinel, so a read that lost its alignment is
            // visible in a member written after the generic rather than looking like a plausible value.
            yield return ("Wrap.bin", serializer.Save(new Legacy.Sleeve
            {
                Wrapped = new Legacy.Wrap<Legacy.Coord> { Value = new Legacy.Coord { X = 1, Y = 2, Z = 3 }, Tag = 42 },
                Tail = "TAIL-SENTINEL",
            }, 1));
        }
    }
}
