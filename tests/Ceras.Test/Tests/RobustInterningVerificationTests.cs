using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ceras;
using Ceras.Format;
using Xunit;

namespace Ceras.Test
{
    [CerasSerializable("SharedString")]
    public class InterningTestPotion
    {
        public string Label;
    }

    public class RobustInterningVerificationTests : TestBase
    {
        [Fact]
        public void SharedStringTable_InternsAcrossSections()
        {
            var config = new SerializerConfig();
            CerasRobust.Configure(config);
			config.Advanced.AllowReflectionFallback = true;
			// Must disable compression for this obviously
			config.Robust.StringTableCompression = StringTableCompression.Disabled;
			config.ConfigType<InterningTestPotion>().TargetMembers = TargetMember.AllFields;

			// Force schema generation and registration
			var info = new SchemaTypeInfo(
                stableName: "SharedString",
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.Class,
                fields: new[] { new SchemaFieldInfo("Label", Array.Empty<string>(), "System.String", true) },
                fixedByteSize: 0);
            config.Robust.SchemaRegistry.Register(typeof(InterningTestPotion), info);

            var ceras = new CerasSerializer(config);

			var p = new InterningTestPotion { Label = "SharedString" };

			byte[] data = CerasRobust.Save(p, 1, ceras);

			// "SharedString" should appear exactly ONCE in the entire binary
			// because it's shared between the Schema (StableName) and the Data (Label field).
			int count = CountOccurrences(data, "SharedString");
            Assert.Equal(1, count);
        }

        static int CountOccurrences(byte[] data, string target)
        {
            byte[] targetBytes = System.Text.Encoding.UTF8.GetBytes(target);
            int count = 0;
            for (int i = 0; i <= data.Length - targetBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < targetBytes.Length; j++)
                {
                    if (data[i + j] != targetBytes[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) count++;
            }
            return count;
        }
    }
}
