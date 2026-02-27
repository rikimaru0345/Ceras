using System;
using System.Collections.Generic;
using System.Buffers;
using Xunit;
using Ceras;
using Ceras.Format;
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
            var config = new SerializerConfig();
            Ceras.Generated.RobustFormatters.UseRobustFormatters(config);
            CerasRobust.Configure(config);
            config.Advanced.SizeLimits.MaxCollectionSize = 100; // Small limit
            var ceras = new CerasSerializer(config);

            // Verify that a legitimate large save is blocked if it exceeds limits.
            var oversized = new LargeCollectionModel { Items = Enumerable.Range(0, 200).ToList() };
            var oversizedBlob = CerasRobust.Save(oversized, 1, ceras);

            // CerasRobust wraps InvalidOperationException in a CerasException
            var ex = Assert.Throws<Ceras.Exceptions.CerasException>(() => 
                CerasRobust.Load<LargeCollectionModel>(oversizedBlob, 1, ceras));
            
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
            // 1. RAW Path (standard config)
            var configRaw = new SerializerConfig();
            Ceras.Generated.RobustFormatters.UseRobustFormatters(configRaw);
            var cerasRaw = new CerasSerializer(configRaw);

            // 2. ROBUST Path (configured for robust format)
            var configRobust = new SerializerConfig();
            CerasRobust.Configure(configRobust);
            Ceras.Generated.RobustFormatters.UseRobustFormatters(configRobust);
            var cerasRobust = new CerasSerializer(configRobust);

            // Create two distinct string instances with the same content
            var content = "ThisIsAUniqueString_12345";
            string s1 = new string(content.ToCharArray());
            string s2 = new string(content.ToCharArray());
            Assert.False(ReferenceEquals(s1, s2));

            var model = new StringModel { A = s1, B = s2 };

            // 1. RAW Path execution
            var rawBlob = CerasRaw.Serialize(model, cerasRaw);
            byte[] stringBytes = System.Text.Encoding.UTF8.GetBytes(content);
            int rawOccurrences = CountOccurrences(rawBlob, stringBytes);
            // In Raw mode, strings are inline (often twice if not interned by object cache)
            Assert.True(rawOccurrences >= 1);
            
            // 2. ROBUST Path execution
            var robustBlob = CerasRobust.Save(model, 1, cerasRobust);
            int robustOccurrences = CountOccurrences(robustBlob, stringBytes);

            // Robust MUST have it exactly once (in the table at the end)
            Assert.Equal(1, robustOccurrences);

            // 3. Position check: String data should be at the very end of Robust blob
            long stringTableOff = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(robustBlob.AsSpan(CerasRobust.OffsetStringTable));
            int firstMatchIdx = IndexOf(robustBlob, stringBytes);
            Assert.True(firstMatchIdx >= stringTableOff, $"String data should be in the table at the end. Match at {firstMatchIdx}, Table starts at {stringTableOff}");
        }

        [Fact]
        public void Robust_Interning_PreservesReferences()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
            Ceras.Generated.RobustFormatters.UseRobustFormatters(config);
            var ceras = new CerasSerializer(config);

            var content = "InternMe";
            var model = new StringModel { A = new string(content), B = new string(content) };
            
            var blob = CerasRobust.Save(model, 1, ceras);
            var cloned = CerasRobust.Load<StringModel>(blob, 1, ceras);

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
