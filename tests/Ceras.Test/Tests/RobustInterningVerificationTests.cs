using System;
using System.Linq;
using Ceras;
using Ceras.Format;
using Xunit;

namespace Ceras.Test
{
    [CerasContext("Tests")]
    [CerasSerializable("SharedString")]
    public class InterningTestPotion
    {
        public string Label;
        public string Label2;
    }

    public class RobustInterningVerificationTests : TestBase
    {
        /// <summary>
        /// The two-region format (v4): schema strings live in their own region (between the schema
        /// bytes and the data section) and are NOT shared with the payload string table. Payload
        /// strings still intern within the payload table.
        /// </summary>
        [Fact]
        public void SchemaStrings_And_PayloadStrings_AreSeparateRegions()
        {
            var robust = new CerasRobustSerializer(config =>
            {
                config.AllowReflectionFallback = true;
                config.StringTableCompression = StringTableCompression.Disabled;

                var info = new SchemaTypeInfo(
                    stableName: "SharedString",
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.Class,
                    fields: new[]
                    {
                        new SchemaFieldInfo("Label", Array.Empty<string>(), "System.String", true),
                        new SchemaFieldInfo("Label2", Array.Empty<string>(), "System.String", true),
                    },
                    fixedByteSize: 0);
                config.SchemaRegistry.Register(typeof(InterningTestPotion), info);
            });

            var p = new InterningTestPotion { Label = "PayloadValueX", Label2 = "PayloadValueX" };
            byte[] data = robust.Save(p, 1);

            long schemaStrOff = BitConverter.ToInt64(data, RobustHeader.SchemaStringOffsetOffset);
            long dataOff = BitConverter.ToInt64(data, RobustHeader.DataSectionOffsetOffset);
            long strOff = BitConverter.ToInt64(data, RobustHeader.StringTableOffsetOffset);

            // Region ordering: header | schema bytes | schema strings | data | payload strings.
            Assert.True(schemaStrOff >= RobustHeader.Size);
            Assert.True(schemaStrOff <= dataOff);
            Assert.True(dataOff <= strOff);
            Assert.True(strOff <= data.Length);

            // The schema stable name appears only inside the schema string region.
            Assert.Equal(1, CountOccurrencesInRange(data, "SharedString", (int)schemaStrOff, (int)dataOff));
            Assert.Equal(0, CountOccurrencesInRange(data, "SharedString", (int)strOff, data.Length));

            // The payload string is interned once (two fields, same value) and only in the payload table.
            Assert.Equal(0, CountOccurrencesInRange(data, "PayloadValueX", RobustHeader.Size, (int)dataOff));
            Assert.Equal(1, CountOccurrencesInRange(data, "PayloadValueX", (int)strOff, data.Length));
        }

        static int CountOccurrencesInRange(byte[] data, string target, int start, int endExclusive)
        {
            byte[] targetBytes = System.Text.Encoding.UTF8.GetBytes(target);
            int count = 0;
            for (int i = start; i <= endExclusive - targetBytes.Length; i++)
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
