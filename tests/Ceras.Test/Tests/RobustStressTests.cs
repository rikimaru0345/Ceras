using System;
using System.Collections.Generic;
using System.Buffers;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;
using System.Linq;

namespace Ceras.Test
{
    public class RobustStressTests : TestBase
    {
		// -------------------------------------------------------------------
		// Scenario #5: Size Limit Stress (Security)
		// Confirm that the Robust path respects SizeLimits to prevent
		// OOM attacks from corrupted/malicious saves.
		// -------------------------------------------------------------------

		[CerasSerializable]
        public class LargeCollectionModel
        {
            public List<int> Items;
        }

        [Fact]
        public void Robust_SizeLimits_PreventsOOM()
        {
            var robust = new CerasRobustSerializer(config =>
            {
                config.UseGeneratedFormatters();
                config.SizeLimits.MaxCollectionSize = 100; // Small limit
            });

            // Verify that a legitimate large save is blocked if it exceeds limits.
            var oversized = new LargeCollectionModel { Items = Enumerable.Range(0, 200).ToList() };
            var oversizedBlob = robust.Save(oversized, 1);

			var ex = Assert.Throws<Ceras.Exceptions.CerasException>(() =>
				robust.Load<LargeCollectionModel>(oversizedBlob, 1));

			Assert.Contains("Max size exceeded", ex.ToString());
        }

        // -------------------------------------------------------------------
        // String Table Placement & Overhead Comparison
        // -------------------------------------------------------------------

        [CerasSerializable]
        public class StringModel
        {
            public string A;
            public string B;
        }

        [Fact]
        public void Raw_vs_Robust_StringStorage_Comparison()
        {
            // 1. RAW Path: use a plain CerasSerializer (no robust string table)
            var rawConfig = new CerasRawConfig();
            rawConfig.UseGeneratedFormatters();
            var cerasRaw = new CerasSerializer(rawConfig);

            // 2. ROBUST Path
            var robust = new CerasRobustSerializer(config =>
            {
                config.UseGeneratedFormatters();
                // cant test anything string related if compressed
                config.StringTableCompression = StringTableCompression.Disabled;
            });

            // Create two distinct string instances with the same content
            var content = "ThisIsAUniqueString_12345";
            string s1 = new string(content.ToCharArray());
            string s2 = new string(content.ToCharArray());
            Assert.False(ReferenceEquals(s1, s2));

            var model = new StringModel { A = s1, B = s2 };

            // 1. RAW Path execution
            var rawBlob = cerasRaw.Serialize(model);
            byte[] stringBytes = System.Text.Encoding.UTF8.GetBytes(content);
            int rawOccurrences = CountOccurrences(rawBlob, stringBytes);
            // In Raw mode, strings are inline (often twice if not interned by object cache)
            Assert.True(rawOccurrences >= 1);

            // 2. ROBUST Path execution
            var robustBlob = robust.Save(model, 1);
            int robustOccurrences = CountOccurrences(robustBlob, stringBytes);

            // Robust MUST have it exactly once (in the table at the end)
            Assert.Equal(1, robustOccurrences);

            // 3. Position check: String data should be at the very end of Robust blob
            long stringTableOff = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(robustBlob.AsSpan(CerasRobustSerializer.OffsetStringTable));
            int firstMatchIdx = IndexOf(robustBlob, stringBytes);
            Assert.True(firstMatchIdx >= stringTableOff, $"String data should be in the table at the end. Match at {firstMatchIdx}, Table starts at {stringTableOff}");
        }

        [Fact]
        public void Robust_Interning_PreservesReferences()
        {
            var robust = new CerasRobustSerializer(config => config.UseGeneratedFormatters());

            var content = "InternMe";
            var model = new StringModel { A = new string(content), B = new string(content) };

            var blob = robust.Save(model, 1);
            var cloned = robust.Load<StringModel>(blob, 1);

            // After loading from robust format, both strings MUST be the same instance
            Assert.True(ReferenceEquals(cloned.A, cloned.B), "Robust path should automatically intern all strings");
        }

        static int CountOccurrences(byte[] haystack, byte[] needle)
        {
            int count = 0;
            int pos = 0;
            while ((pos = IndexOf(haystack, needle, pos)) != -1)
            {
                count++;
                pos += needle.Length;
            }
            return count;
        }

        static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
        {
            for (int i = start; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}
