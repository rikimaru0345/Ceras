// Shared plumbing for the cross-version fixture tests: the golden files live in one directory, they are
// always read the same way, and each file is always checked for the same three things (an intact checksum, a
// schema section that really holds the *old* names, and a layout fingerprint that cannot match this build).
//
// These helpers exist so a test file states its own cases and nothing else. They do not decide anything
// about a load; a test that wants a different expectation for one of these three checks does it inline, and
// the reason is then visible next to the case.
using System;
using System.IO;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;
using Ceras.Helpers;

namespace Ceras.Test
{
    static class HistoricalFixtureFiles
    {
        /// <summary>The directory every golden file is copied into, for the contexts that use them.</summary>
        public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HistoricalV1");

        public static byte[] Fixture(string name) => Read(name);

        public static byte[] Read(string name)
        {
            string path = Path.Combine(Directory, name);
            Assert.True(File.Exists(path), $"missing golden fixture '{name}' at '{path}'. " +
                "Run fixtures/Ceras.HistoricalWriter to regenerate it (see its README.md).");
            return File.ReadAllBytes(path);
        }

        /// <summary>
        /// The loader verifies the CRC before it interprets anything, so a green load already implies an
        /// intact checksum — but a test whose point is a *refusal* proves nothing about the payload, so this
        /// is what establishes that the bytes really are what an old build wrote.
        /// </summary>
        public static void AssertIntactChecksum(byte[] bytes)
            => Assert.Equal(HashHelpers.CalculateCrc32WithZeroedRange(bytes, RobustHeader.ChecksumOffset, RobustHeader.ChecksumSize),
                BitConverter.ToUInt32(bytes, RobustHeader.ChecksumOffset));

        /// <summary>
        /// The file's schema section holds the *old* stable names, so a load really has to map them through
        /// <c>[PreviousTypeName]</c> rather than finding the current types by name — and the reader
        /// assembly must not contain the v1 types at all, or the test would be a second copy of this build's
        /// model rather than a cross-version read.
        /// </summary>
        public static void AssertSchemaNamesTheOldModel(byte[] bytes, string oldNamespace, string currentNamespace)
        {
            var dump = CerasRobustSerializer.DumpSchema(bytes);
            Assert.Contains(oldNamespace, dump);
            Assert.DoesNotContain(currentNamespace, dump);
        }

        /// <summary>
        /// A file whose fingerprint matched this context would take the generated fast path, and then none of
        /// the interpreter behaviour under test would run. The registry is sealed directly rather than
        /// through a load, so this also works for the files a load is <em>refused</em> — which is exactly
        /// the set where "the fast path was never an option" is the point.
        /// </summary>
        public static void AssertDoesNotMatchCurrentLayout(byte[] bytes, string context)
        {
            var registry = MigrationReader(context).Config.SchemaRegistry;
            registry.Seal();
            Assert.NotEqual(registry.ComputeFingerprint(), BitConverter.ToUInt64(bytes, RobustHeader.FingerprintOffset));
        }

        /// <summary>The migration reader: the only one a cross-version case uses unless it says otherwise.</summary>
        public static CerasRobustSerializer MigrationReader(string context)
            => new(c => c.UseGeneratedFormatters(context));

        /// <summary>A serializer over the <em>current</em> model, for the matching-layout controls. The
        /// unknown build timestamp is what makes a save reproducible byte for byte.</summary>
        public static CerasRobustSerializer CurrentWriter(string context)
            => new(c =>
            {
                c.UseGeneratedFormatters(context);
                c.SetUnknownBuildTimestamp();
            });

        /// <summary>The data section of a save: the root type id and the root's payload, nothing else.</summary>
        public static byte[] Payload(byte[] bytes) => DataSection(bytes);

        public static byte[] DataSection(byte[] bytes)
        {
            RobustHeader.Read(bytes, out _, out _, out _, out long dataStart, out long stringTable, out _);
            return bytes.Skip((int)dataStart).Take((int)(stringTable - dataStart)).ToArray();
        }

        /// <summary>A reference marker: a biased varint, exactly as <c>SerializerBinary.ReadUInt32Bias</c>
        /// reads it.</summary>
        public static int ReadMarker(byte[] payload, ref int offset)
            => SerializerBinary.ReadUInt32Bias(payload, ref offset, Protocol.RefBias);

        /// <summary>
        /// Every fixture this reader test file reads, so each one is pinned by a checked-in file: the two
        /// loops a reader test always wants, in one call. A file whose checksum does not verify, or whose
        /// schema section does not hold the old model's names, is a finding rather than a test failure to
        /// work around — and a load refused by a schema check proves nothing about the payload, so the
        /// checksum is what establishes the bytes at all.
        /// </summary>
        public static void AssertEveryFixture(string[] names, string oldNamespace, string currentNamespace, string context)
        {
            foreach (var name in names)
            {
                var bytes = Fixture(name);
                AssertIntactChecksum(bytes);
                AssertSchemaNamesTheOldModel(bytes, oldNamespace, currentNamespace);

                // The registry is sealed directly rather than through a load, so this also works for the
                // files a load is *refused* — which is exactly the set where "the fast path was never an
                // option" is the point. A changed member type is what changes the fingerprint, so the
                // interpreter is the only reader those files could ever have reached.
                var registry = MigrationReader(context).Config.SchemaRegistry;
                registry.Seal();
                Assert.NotEqual(registry.ComputeFingerprint(), BitConverter.ToUInt64(bytes, RobustHeader.FingerprintOffset));
            }
        }
    }
}
