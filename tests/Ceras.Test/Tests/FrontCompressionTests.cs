using System;
using System.Buffers;
using System.Linq;
using Ceras.Format;
using Xunit;

namespace Ceras.Test
{
	public class FrontCompressionTests
	{
		static string[] RoundTrip(string[] input)
		{
			var intern = new StringInternTable();
			foreach (var s in input)
				intern.Intern(s);

			var buf = new byte[1024 * 64];
			int offset = 0;
			int start = intern.Flush(ref buf, ref offset);

			return StringTable.Load(buf, start);
		}

		        static byte[] GetCompressedBytes(string[] input)
		        {
		            var intern = new StringInternTable();
		            foreach (var s in input)
		                intern.Intern(s);
		
		            var buf = new byte[1024 * 64];
		            int offset = 0;
		            intern.Flush(ref buf, ref offset);
		            
		            var result = new byte[offset];
		            Array.Copy(buf, 0, result, 0, offset);
		            return result;
		        }
		
		        [Fact]
		        public void CompressionEfficiency_Test()
		        {
		            var input = new[]
		            {
		                "System.Collections.Generic.List`1",
		                "System.Collections.Generic.Dictionary`2",
		                "System.Collections.Generic.HashSet`1"
		            };
		
		            var bytes = GetCompressedBytes(input);
		            var fullString = string.Join("", input);
		            var utf8 = System.Text.Encoding.UTF8.GetBytes("System.Collections.Generic.");
		            
		            // The prefix "System.Collections.Generic." should only appear ONCE in the binary
		            int count = CountOccurrences(bytes, utf8);
		            Assert.Equal(1, count);
		
		            // Total size should be significantly less than just concatenating them
		            int rawSize = input.Sum(s => System.Text.Encoding.UTF8.GetByteCount(s));
		            Assert.True(bytes.Length < rawSize, $"Compressed size ({bytes.Length}) should be less than raw size ({rawSize})");
		        }
		
		        static int CountOccurrences(byte[] data, byte[] pattern)
		        {
		            int count = 0;
		            for (int i = 0; i <= data.Length - pattern.Length; i++)
		            {
		                bool match = true;
		                for (int j = 0; j < pattern.Length; j++)
		                {
		                    if (data[i + j] != pattern[j])
		                    {
		                        match = false;
		                        break;
		                    }
		                }
		                if (match) count++;
		            }
		            return count;
		        }
		
		        [Fact]
		        public void CommonPrefixes_RoundTrip()		{
			var input = new[]
			{
				"Ceras.Format.SchemaRegistry",
				"Ceras.Format.SchemaSection",
				"Ceras.Format.SchemaTypeInfo",
				"Ceras.Format.StringInternTable"
			};

			var output = RoundTrip(input);

			Assert.Null(output[0]);
			for (int i = 0; i < input.Length; i++)
				Assert.Equal(input[i], output[i + 1]);
		}

		[Fact]
		public void NoCommonPrefixes_RoundTrip()
		{
			var input = new[] { "apple", "banana", "cherry", "date" };
			var output = RoundTrip(input);

			Assert.Null(output[0]);
			for (int i = 0; i < input.Length; i++)
				Assert.Equal(input[i], output[i + 1]);
		}

		[Fact]
		public void DecreasingLength_RoundTrip()
		{
			var input = new[] { "alphabet", "alpha", "alp", "a" };
			var output = RoundTrip(input);

			Assert.Null(output[0]);
			for (int i = 0; i < input.Length; i++)
				Assert.Equal(input[i], output[i + 1]);
		}

		[Fact]
		public void IdenticalStrings_RoundTrip()
		{
			// InternTable handles duplicates, but let's test what happens if we have them anyway
			// (InternTable.GetIndex will return the same index, so Flush only writes it once)
			var intern = new StringInternTable();
			intern.Intern(new String("test"));
			intern.Intern(new String("test"));

			var buf = new byte[256];
			int offset = 0;
			int start = intern.Flush(ref buf, ref offset);
			var output = StringTable.Load(buf, start);

			Assert.Equal(2, output.Length); // null + "test"
			Assert.Equal("test", output[1]);
		}

		[Fact]
		public void EmptyStrings_RoundTrip()
		{
			var input = new[] { "", "a", "b" }; // Removed duplicates as InternTable collapses them
			var output = RoundTrip(input);

			Assert.Null(output[0]);
			for (int i = 0; i < input.Length; i++)
				Assert.Equal(input[i], output[i + 1]);
		}

		[Fact]
		public void MixedData_RoundTrip()
		{
			var input = new[]
			{
				"prefix_abc",
				"prefix_def",
				"different",
				"prefix_ghi",
				"",
				"another_prefix_1",
				"another_prefix_2"
			};

			var output = RoundTrip(input);

			Assert.Null(output[0]);
			for (int i = 0; i < input.Length; i++)
				Assert.Equal(input[i], output[i + 1]);
		}

		[Fact]
		public void InvalidData_Throws()
		{
			// Manual construction of invalid front compression data
			// commonLength > last.Length

			var writer = new ArrayBufferWriter<byte>();
			SchemaSection.WriteLeb128(writer, 2); // 2 strings

			// String 1: "abc"
			SchemaSection.WriteLeb128(writer, 0); // prefix 0
			SchemaSection.WriteInlineString(writer, "abc");

			// String 2: invalid prefix 5 (longer than "abc")
			SchemaSection.WriteLeb128(writer, 5);
			SchemaSection.WriteInlineString(writer, "suffix");

			var data = writer.WrittenSpan.ToArray();

			Assert.Throws<InvalidOperationException>(() => StringTable.Load(data, 0));
		}
	}
}
