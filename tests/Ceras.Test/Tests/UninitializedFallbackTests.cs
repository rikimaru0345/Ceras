using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using Ceras;
using Ceras.Exceptions;

namespace Ceras.Test
{
    public class UninitializedFallbackTests
    {
        public class TrulyNoCtorType
        {
            public int Value;
            public TrulyNoCtorType(int someOtherName) { Value = someOtherName; }
        }

        [Fact]
        public void NoCtorType_Throws_When_AllowUninitializedObjectFallback_Is_False()
        {
            var config = new SerializerConfig();
            config.Advanced.AllowUninitializedObjectFallback = false;
            
            var ceras = new CerasSerializer(config);
            
            var ex = Record.Exception(() => ceras.Serialize(new TrulyNoCtorType(5)));
            Assert.NotNull(ex);
            
            // It might be wrapped in TargetInvocationException because formatters are created via reflection
            if (ex is System.Reflection.TargetInvocationException tie)
                ex = tie.InnerException;
                
            Assert.IsType<CerasException>(ex);
            Assert.Contains("no parameterless constructor", ex.Message);
        }

        [Fact]
        public void NoCtorType_Works_When_AllowUninitializedObjectFallback_Is_True()
        {
            var config = new SerializerConfig();
            config.Advanced.AllowUninitializedObjectFallback = true;
            
            var ceras = new CerasSerializer(config);
            var original = new TrulyNoCtorType(5);
            
            var data = ceras.Serialize(original);
            var result = ceras.Deserialize<TrulyNoCtorType>(data);
            
            Assert.Equal(5, result.Value);
        }

        [Fact]
        public void NoCtorType_Works_When_Explicitly_Configured_Even_If_Global_Fallback_Is_False()
        {
            var config = new SerializerConfig();
            config.Advanced.AllowUninitializedObjectFallback = false;
            config.ConfigType<TrulyNoCtorType>().ConstructByUninitialized();
            
            var ceras = new CerasSerializer(config);
            var original = new TrulyNoCtorType(5);
            
            var data = ceras.Serialize(original);
            var result = ceras.Deserialize<TrulyNoCtorType>(data);
            
            Assert.Equal(5, result.Value);
        }

        [Fact]
        public void NoCtorType_Works_With_Attribute_Override()
        {
            var config = new SerializerConfig();
            config.Advanced.AllowUninitializedObjectFallback = false;
            
            var ceras = new CerasSerializer(config);
            
            // Should throw for TrulyNoCtorType
            var ex = Record.Exception(() => ceras.Serialize(new TrulyNoCtorType(5)));
            Assert.NotNull(ex);
            if (ex is System.Reflection.TargetInvocationException tie)
                ex = tie.InnerException;
            Assert.IsType<CerasException>(ex);
            
            // But should work for a type with the attribute
            var original = new AttributeType(10);
            var data = ceras.Serialize(original);
            var result = ceras.Deserialize<AttributeType>(data);
            Assert.Equal(10, result.Value);
        }

        [Fact]
        public void SchemaTraversalEngine_Throws_When_AllowUninitializedObjectFallback_Is_False()
        {
            var config = new SerializerConfig();
            config.Advanced.AllowUninitializedObjectFallback = false;
            var ceras = new CerasSerializer(config);
            
            // Create an empty engine
            var type = typeof(Ceras.Format.SchemaRegistry).Assembly.GetType("Ceras.Format.SchemaTraversalEngine");
            var engine = Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public, null, new object[] { 
                new Dictionary<string, Ceras.Format.OldTypeSchema>(), 
                new string[0], // stringTable
                1u,   // version
                ceras 
            }, null);
            
            var createInstanceMethod = type.GetMethod("CreateInstance", BindingFlags.NonPublic | BindingFlags.Instance);
            
            var ex = Record.Exception(() => createInstanceMethod.Invoke(engine, new object[] { typeof(TrulyNoCtorType) }));
            
            Assert.NotNull(ex);
            if (ex is System.Reflection.TargetInvocationException tie) ex = tie.InnerException;
            Assert.IsType<CerasException>(ex);
            Assert.Contains("Fallback to GetUninitializedObject is disabled", ex.Message);
        }

        [MemberConfig(AllowUninitializedObjectFallback = true)]
        public class AttributeType
        {
            public int Value;
            public AttributeType(int someOtherName) { Value = someOtherName; }
        }
    }
}
