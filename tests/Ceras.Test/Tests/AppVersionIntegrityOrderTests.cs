// The application version is a *protected* header field, so it is checked after the CRC32, like every
// other field the checksum covers — and the four files here are four genuine small saves, not
// hand-built headers.
//
// The distinction the file rests on (Format/LAYOUT.md, "Load order and what is rejected"):
//
//   - the magic, the format version and the section-offset envelope are the *envelope*: they say how
//     the rest of the header is to be read at all, so they are asked first and their refusal is a
//     version refusal whatever the checksum says. That is what keeps a v4 file's message the
//     clear "older experimental version" one rather than a checksum complaint about a file this build
//     does not read in the first place;
//   - the app version, the compression flag, the producer build timestamp and the layout fingerprint
//     are *interpreted values*. Their verdict is a comparison against this build — "newer than the
//     current application", "newer than the current build", "this build's own layout" — so a
//     corrupted byte in one of them must be reported as corruption. Answering such a comparison from
//     bytes the checksum has not vouched for tells the caller to update the application, or to
//     re-save, over a file that does not exist.
//
// Before the ordering was fixed, `Load` read the app version and refused a "newer application
// version" *before* verifying the CRC, so a single flipped byte at offset 8 produced exactly that
// advice. It also contradicted the "integrity first" comment three lines below it, the load order
// the document states, and `OldFileGraph.Read`, which checks the same checksum before interpreting
// any of the same fields.
using System;
using System.Buffers.Binary;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.AppVersionOrder
{
    [CerasContext("AppVersionOrder")]
    [CerasSerializable]
    public class AvHolder
    {
        public int Value;
        public string Name;
    }

    public class AppVersionIntegrityOrderTests : TestBase
    {
        const string Ctx = "AppVersionOrder";

        /// <summary>The version the sample is written and read at; a refusal has to be about something else.</summary>
        const uint CurrentVersion = 1;

        /// <summary>Above <see cref="CurrentVersion"/>, so an app-version comparison would refuse it.</summary>
        const uint NewerVersion = 7;

        static CerasRobustSerializer New() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        /// <summary>A real save of a two-member root: no hand-built header, no giant fixture.</summary>
        static byte[] Sample() => New().Save(new AvHolder { Value = 42, Name = "n" }, CurrentVersion);

        static void SetAppVersion(byte[] bytes, uint version)
            => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RobustHeader.GameVersionOffset), version);

        [Fact]
        public void A_Corrupted_App_Version_Byte_Is_Corruption_And_Not_A_Newer_Application()
        {
            var bytes = Sample();

            // The premise, so a failure below cannot be the sample's fault: the file is good, the app
            // version is what the save claims, and it loads.
            Assert.Equal(CurrentVersion, BitConverter.ToUInt32(bytes, RobustHeader.GameVersionOffset));
            Assert.Equal(42, New().Load<AvHolder>(bytes, CurrentVersion).Value);

            // One field of the header changed, the checksum left to disagree - which is what corruption
            // of that field actually looks like. Reported as "newer application version" it sends the
            // caller off to update an application for a file that was never written by a newer one.
            SetAppVersion(bytes, NewerVersion);

            var corruption = Assert.IsType<CerasDataCorruptionException>(
                Record.Exception(() => New().Load<AvHolder>(bytes, CurrentVersion)));
            Assert.Contains("CRC mismatch", corruption.Message, StringComparison.Ordinal);

            // And the other direction, so the check above cannot be satisfied by rejecting only a
            // version that is too high: a byte corrupted downwards is the same corruption, and it
            // reached the same answer before this ordering was fixed.
            var downwards = Sample();
            SetAppVersion(downwards, 0);

            var alsoCorruption = Assert.IsType<CerasDataCorruptionException>(
                Record.Exception(() => New().Load<AvHolder>(downwards, CurrentVersion)));
            Assert.Contains("CRC mismatch", alsoCorruption.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Newer_App_Version_On_A_File_Whose_Checksum_Matches_Is_Still_Refused()
        {
            // The other half of the contract, and the one that must not move: a file that *is* from a
            // newer application is refused for that reason, with both versions in the exception, even
            // though its checksum is now perfectly valid.
            var bytes = Sample();
            SetAppVersion(bytes, NewerVersion);
            FixRobustChecksum(bytes);

            var ex = Assert.Throws<CerasRobustVersionException>(() => New().Load<AvHolder>(bytes, CurrentVersion));
            Assert.Equal(NewerVersion, ex.FileRobustVersion);
            Assert.Equal(CurrentVersion, ex.CurrentRobustVersion);
        }

        [Fact]
        public void A_V4_File_Is_Refused_As_An_Older_Format_Version()
        {
            var bytes = Sample();
            bytes[RobustHeader.FormatVersionOffset] = 4;
            FixRobustChecksum(bytes);

            var ex = Assert.Throws<CerasVersionMismatchException>(() => New().Load<AvHolder>(bytes, CurrentVersion));
            Assert.Equal(4u, ex.FileVersion);
            Assert.Equal((uint)CerasRobustSerializer.FormatVersion, ex.LibraryVersion);
            Assert.Contains("older experimental version", ex.Message, StringComparison.Ordinal);

            // The same file with the checksum left disagreeing, which is what flipping that one byte
            // produces. The format version is the envelope, so it is asked before the checksum and the
            // refusal stays the clear version one - a build that does not read this format has no
            // opinion about what its checksum would have said.
            var staleChecksum = Sample();
            staleChecksum[RobustHeader.FormatVersionOffset] = 4;

            Assert.IsType<CerasVersionMismatchException>(
                Record.Exception(() => New().Load<AvHolder>(staleChecksum, CurrentVersion)));
        }

        [Fact]
        public void A_Refused_Load_Leaves_The_Serializer_Usable()
        {
            // The same pattern ArrayShapeChangeMigrationTests pins for a schema refusal: a refusal is
            // not a poisoned state, and the reader that refused one file still reads the next one.
            var reader = New();
            var bytes = Sample();

            var corrupted = (byte[])bytes.Clone();
            SetAppVersion(corrupted, NewerVersion);
            Assert.IsType<CerasDataCorruptionException>(
                Record.Exception(() => reader.Load<AvHolder>(corrupted, CurrentVersion)));

            var newer = (byte[])bytes.Clone();
            SetAppVersion(newer, NewerVersion);
            FixRobustChecksum(newer);
            Assert.IsType<CerasRobustVersionException>(
                Record.Exception(() => reader.Load<AvHolder>(newer, CurrentVersion)));

            var loaded = reader.Load<AvHolder>(bytes, CurrentVersion);
            Assert.Equal(42, loaded.Value);
            Assert.Equal("n", loaded.Name);
        }
    }
}
