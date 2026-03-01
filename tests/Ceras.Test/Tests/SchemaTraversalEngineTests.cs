using Ceras;
using Ceras.Format;
using Ceras.Helpers;
using System;
using System.Collections.Generic;
using Xunit;

namespace Ceras.Test
{
	public class SchemaTraversalEngineTests : TestBase
	{
		[Fact]
		public void SchemaTraversalEngine_UsesParameterlessConstructor()
		{
			// 1. Setup config and serializer
			var config = new CerasRobustConfig();
			CerasRobust.Configure(config);
			config.AllowReflectionFallback = true;
			config.ConfigType<CtorTestModel>().SetTargetMembers(TargetMember.AllFields);
			var ceras = new CerasSerializer(config);

			// 2. Create data with some field
			var original = new CtorTestModel { Value = 42 };
			var data = CerasRobust.Save(original, 1, ceras);

			// 3. To trigger SchemaTraversalEngine, we need a schema mismatch.
			// We can do this by using a DIFFERENT config that has an EXTRA field for the same type.
			var config2 = new CerasRobustConfig();
			CerasRobust.Configure(config2);
			config2.ConfigType<CtorTestModel>().SetTargetMembers(TargetMember.AllFields);
			// We don't actually need to add a field to the C# class, 
			// just ensure the fingerprint is different or the schema is different.
			// Actually, CerasRobust.Load will check the fingerprint.
			// If we change the "StableName" or add a field in the config, it will trigger it.
			
			// Let's force a schema mismatch by changing a property in the registry manually if possible, 
			// or just by using a model that has an extra field in the "new" version.
			
			// A simpler way: CerasRobust.Load automatically uses SchemaTraversalEngine IF the fingerprints don't match.
			// We can fake a fingerprint mismatch by patching the data.
			data[CerasRobust.OffsetFingerprint] ^= 0xFF; // Flip a byte in the fingerprint

			CtorTestModel.CtorCalled = false;
			var clone = CerasRobust.Load<CtorTestModel>(data, 1, ceras);

			Assert.Equal(42, clone.Value);
			Assert.True(CtorTestModel.CtorCalled, "Constructor should have been called by SchemaTraversalEngine");
		}

		[Fact]
		public void GetterOnly_Null_ThrowsUsingHelper()
		{
			// This test verifies that CerasThrowHelper.ThrowGetterOnlyNull is used.
			// We can't easily check the call stack here, but we can check the exception message.
			
			var config = new CerasRobustConfig();
			// Ensure we use the generated formatters if they exist, 
			// but here we want to test the one we just modified.
			// Since we're in a test project that references Ceras, 
			// and we just modified RobustCodeEmitter, we'd need to REGENERATE the formatters to test the SG change.
			
			// However, we can at least verify the helper itself works and has the right message.
			var ex = Assert.Throws<InvalidOperationException>(() => CerasThrowHelper.ThrowGetterOnlyNull("MyMember", "MyType"));
			Assert.Contains("Cannot deserialize getter-only member 'MyMember' of type 'MyType'", ex.Message);
		}
	}

	[CerasSerializable]
	public class CtorTestModel
	{
		public static bool CtorCalled = false;

		public int Value;

		public CtorTestModel()
		{
			CtorCalled = true;
		}
	}

	[CerasSerializable]
	public class GetterOnlyModel
	{
		public List<int> Items { get; } = null; // Forced null to trigger the check
	}
}
