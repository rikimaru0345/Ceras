
using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Formatters;
using Ceras.Helpers;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test
{
    public class AssemblyNameBug : TestBase
    {
        [CerasContext("Tests")]
        [CerasSerializable]
        public class Container
        {
            public object Any;
        }

        // A type that is NOT registered in SchemaRegistry but used in a polymorphic field
        public class UnregisteredType
        {
            public int X;
        }

        CerasRobustSerializer CreateSerializer() => new CerasRobustSerializer(config =>
        {
            config.UseGeneratedFormatters("Tests");
            config.AllowReflectionFallback = true;
        });

        /// <summary>
        /// This test used to assert that a boxed delegate in an <c>object</c> member was silently
        /// written as <c>null</c>: the save succeeded and the value came back missing. That is the
        /// behaviour the banned-type policy now forbids - a save that does not contain the value it
        /// was given is a silent data loss, not a skip - so the value is rejected by name instead.
        /// The member itself (<c>object Any</c>) is not banned and is still serialized normally; see
        /// BannedTypeFailLoudTests for the member-level, Type-slot and other-slot cases.
        /// </summary>
        [Fact]
        public void SerializingFunc_ThrowsInsteadOfWritingNull()
        {
            var ceras = CreateSerializer();

            var original = new Container
            {
                Any = (Func<int, int>)(x => x * 2)
            };

            var ex = Assert.ThrowsAny<Ceras.Exceptions.CerasException>(() => ceras.Save(original, 1));

            // The rejected value is named. (This root has a generated formatter, which reports the
            // value and the slot it went through, not the member - see BannedTypeFailLoudTests.)
            Assert.Contains("Func", ex.ToString(), StringComparison.Ordinal);
            Assert.Contains("System.Object", ex.ToString(), StringComparison.Ordinal);
        }
    }
}
