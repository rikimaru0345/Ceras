
#pragma warning disable 0649 // Field is never assigned to


using System.Buffers;

// ---------------------------------------------------------------------------
// Robust scenario tests for the Ceras source generator.
//
// These tests answer two key questions:
//   1. Does the source generator handle realistic, complex object graphs?
//   2. How do generated formatters interact with the 'CerasRobust' system?
//
// The short answer to (2):
//   Generated formatters do NOT embed field names in the binary — they write
//   fields in sorted order with no schema header. This makes them fast and
//   compact.
//
//   Robust versioning (adding/removing/renaming fields) is handled by the
//   'CerasRobust' system, which uses the migration engine and SchemaTypeInfo
//   emitted by the source generator when 'VersionTolerant = true' is used.
// ---------------------------------------------------------------------------


using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Formatters;
using Ceras.Helpers;
using Ceras.Format;
using Ceras.Generated;
using System.Linq;

namespace Ceras.Test {

	struct SomeStruct {
		public int x, y;
	}

	[CerasContext("Tests")]
	[CerasSerializable]
	partial class ThingWithBackingFields {
		public bool SomeBool { get; set; }
		public int SomeInteger { get; set; }
		public SomeStruct SomeXY { get; set; }
		public string Str { get; set; }
	}

	[CerasContext("Tests")]
	[CerasSerializable]
	partial class ThingWithSerializedAs {
		[CerasStableName("NewName")]
		public int OldName { get; set; }
	}

	public class AutoPropBackingFieldTests : TestBase {
		CerasRawConfig GeneratedConfig() {
			var c = new CerasRawConfig();
			RobustFormatters.UseRobustFormatters(c, "Tests");
			return c;
		}

		static ThingWithBackingFields GenerateData() {
			return new ThingWithBackingFields {
				SomeBool = true,
				SomeInteger = 20,
				SomeXY = new SomeStruct { x = 5, y = -3 },
				Str = "abc123"
			};
		}

		[Fact]
		public void Robust_FullRoundTrip_GeneratedFormatter() {
			var ceras = new CerasFastSerializer(GeneratedConfig());
			var original = GenerateData();

			var clone = ceras.Deserialize<ThingWithBackingFields>(ceras.Serialize(original));

			Assert.Equal(original.SomeBool, clone.SomeBool);
			Assert.Equal(original.SomeInteger, clone.SomeInteger);
			Assert.Equal(original.SomeXY, clone.SomeXY);
			Assert.Equal(original.Str, clone.Str);
		}

		[Fact]
		public void Robust_SchemaNames_UsePropertyName_ForBackingFields() {
			var config = new CerasRobustConfig();
            RobustFormatters.UseRobustFormatters(config, "Tests");
            
            Assert.True(((CerasRobustConfig)config).SchemaRegistry.TryGetInfo(typeof(ThingWithBackingFields), out var schema));
            Assert.Contains(schema.Fields, f => f.StableName == "SomeBool");
            Assert.Contains(schema.Fields, f => f.StableName == "SomeInteger");
            Assert.Contains(schema.Fields, f => f.StableName == "SomeXY");
            Assert.Contains(schema.Fields, f => f.StableName == "Str");
		}

		[Fact]
		public void Robust_SchemaNames_HonorsSerializedAs_OnProperties() {
			var config = new CerasRobustConfig();
            RobustFormatters.UseRobustFormatters(config, "Tests");
            
            Assert.True(((CerasRobustConfig)config).SchemaRegistry.TryGetInfo(typeof(ThingWithSerializedAs), out var schema));
            Assert.Contains(schema.Fields, f => f.StableName == "NewName");
            Assert.DoesNotContain(schema.Fields, f => f.StableName == "OldName");
		}

	}
}
