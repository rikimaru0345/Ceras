// Item 5: the reentrancy contract for the per-operation robust context.
//
// RobustContext used to be a single [ThreadStatic] slot, so a Save/Load started from inside a callback
// overwrote the outer operation's string table and type-id map. The outer read then carried on with the
// inner operation's tables - silently, and only for the rest of the graph, which is why the nesting point
// matters: [OnBeforeDeserialize] runs *before* the object's fields are read, so everything after it is
// read against the wrong tables. The context is now a scoped stack; these tests pin that.
using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Generated;

namespace Ceras.Test.Reentrancy
{
    /// <summary>What a nested operation saw, so the two nesting levels can be compared.</summary>
    public static class NestLog
    {
        public static readonly List<string> Entries = new();
        public static void Reset() => Entries.Clear();
    }

    /// <summary>Shared state for the callbacks: the bytes of the "other" context's save, and its serializer.</summary>
    public static class NestState
    {
        public static byte[] OtherBytes;
        public static CerasRobustSerializer OtherSerializer;

        public static void Reset()
        {
            OtherBytes = null;
            OtherSerializer = null;
            NestLog.Reset();
        }
    }

    [CerasContext("ReentrancyOther")]
    [CerasSerializable]
    public class ReOther
    {
        public string OtherName;
        public ReOtherInner OtherInner;
    }

    [CerasContext("ReentrancyOther")]
    [CerasSerializable]
    public class ReOtherInner
    {
        public int OtherNumber;
    }

    [CerasContext("Reentrancy")]
    [CerasSerializable]
    public class ReOuter
    {
        public string OuterName;
        public ReOtherShared Shared;
        public int After;

        /// <summary>
        /// Runs before the fields above are read, so the outer read continues afterwards - exactly where a
        /// clobbered context used to do damage.
        /// </summary>
        [OnBeforeDeserialize]
        private void Before()
        {
            if (NestState.OtherBytes == null) return;

            var nested = NestState.OtherSerializer.Load<ReOther>(NestState.OtherBytes, 1);
            NestLog.Entries.Add("outer-before:" + OuterName);
            NestLog.Entries.Add("nested:" + nested.OtherName);
        }
    }

    [CerasContext("Reentrancy")]
    [CerasSerializable]
    public class ReOtherShared
    {
        public string SharedName;
    }

    /// <summary>
    /// A load whose callback performs another *save*, through a second serializer instance. That covers the
    /// write half of the scoped context: the outer read has to keep its own string table and type map while
    /// a write context is pushed on top of it.
    /// <para>
    /// The nesting point has to be a deserialize callback because the robust generated path only emits
    /// those; [OnBeforeSerialize] is honoured by the raw serializer but silently ignored here (see the
    /// open P0 item in TODO.md, "Serialize callbacks in robust generated format"). Load-inside-load
    /// above and load-inside-save here exercise the same [ThreadStatic] stack for both context kinds.
    /// </para>
    /// </summary>
    [CerasContext("Reentrancy")]
    [CerasSerializable]
    public class ReSaveInCallback
    {
        public string Name;
        public ReOtherShared Shared;
        public int Number;

        [OnBeforeDeserialize]
        private void Before()
        {
            if (NestState.OtherBytes == null) return;

            var nested = NestState.OtherSerializer.Save(
                new ReOther { OtherName = "saved-inside", OtherInner = new ReOtherInner { OtherNumber = 7 } }, 1);
            NestLog.Entries.Add("outer-before:" + Name);
            NestLog.Entries.Add("nested-save:" + nested.Length);
        }
    }

    public class NestedOperationTests : TestBase
    {
        static CerasRobustSerializer Outer() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("Reentrancy"));
        static CerasRobustSerializer Other() => new CerasRobustSerializer(c => c.UseGeneratedFormatters("ReentrancyOther"));

        static ReOuter SampleOuter() => new ReOuter
        {
            OuterName = "outer-string",
            Shared = new ReOtherShared { SharedName = "shared-string" },
            After = 11,
        };

        static byte[] OtherBytes() => Other().Save(
            new ReOther { OtherName = "other-string", OtherInner = new ReOtherInner { OtherNumber = 22 } }, 1);

        [Fact]
        public void A_Load_Started_Inside_A_Callback_Does_Not_Disturb_The_Outer_Load()
        {
            NestState.Reset();
            NestState.OtherSerializer = Other();
            NestState.OtherBytes = OtherBytes();

            var outer = Outer();
            var loaded = outer.Load<ReOuter>(outer.Save(SampleOuter(), 1), 1);

            // The nested load really happened, through a different context with its own string table.
            Assert.Equal(2, NestLog.Entries.Count);
            Assert.Equal("outer-before:", NestLog.Entries[0]);   // before the fields are read, so still null
            Assert.Equal("nested:other-string", NestLog.Entries[1]);

            // Everything the outer read consumed *after* the callback came from its own tables. With one
            // shared slot these would be the inner context's, and the strings below would be wrong or the
            // read would desynchronise.
            Assert.Equal("outer-string", loaded.OuterName);
            Assert.Equal("shared-string", loaded.Shared.SharedName);
            Assert.Equal(11, loaded.After);
        }

        [Fact]
        public void A_Save_Started_Inside_A_Callback_Does_Not_Disturb_The_Outer_Load()
        {
            NestState.Reset();
            NestState.OtherSerializer = Other();
            NestState.OtherBytes = OtherBytes();

            var outer = Outer();
            var value = new ReSaveInCallback
            {
                Name = "outer-string",
                Shared = new ReOtherShared { SharedName = "shared-string" },
                Number = 33,
            };
            var bytes = outer.Save(value, 1);
            NestLog.Reset();

            var loaded = outer.Load<ReSaveInCallback>(bytes, 1);

            Assert.Equal(2, NestLog.Entries.Count);
            Assert.Equal("outer-before:", NestLog.Entries[0]);   // still before the fields are read
            Assert.StartsWith("nested-save:", NestLog.Entries[1], StringComparison.Ordinal);

            // Everything read after the callback came from the outer read's own tables.
            Assert.Equal("outer-string", loaded.Name);
            Assert.Equal("shared-string", loaded.Shared.SharedName);
            Assert.Equal(33, loaded.Number);
        }

        [Fact]
        public void The_Outer_Load_Also_Works_Through_The_Migration_Reader()
        {
            NestState.Reset();
            NestState.OtherSerializer = Other();
            NestState.OtherBytes = OtherBytes();

            var outer = Outer();
            var bytes = outer.Save(SampleOuter(), 1);

            // The interpreter defers callbacks to the end of the graph, so the nested load happens after
            // every byte is read. It must still be scoped, and must not disturb the result.
            var loaded = outer.Load<ReOuter>(SlowPath(bytes), 1);

            Assert.Equal("outer-string", loaded.OuterName);
            Assert.Equal("shared-string", loaded.Shared.SharedName);
            Assert.Equal(11, loaded.After);
            Assert.Equal(2, NestLog.Entries.Count);
        }

        [Fact]
        public void Repeated_Operations_Through_One_Instance_Keep_Working()
        {
            // The scope has to restore cleanly every time, not just once: a leak would show up as the
            // second operation reading the first one's table.
            NestState.Reset();
            NestState.OtherSerializer = Other();
            NestState.OtherBytes = OtherBytes();

            var outer = Outer();
            var outerBytes = outer.Save(SampleOuter(), 1);
            var otherBytes = OtherBytes();

            for (int i = 0; i < 3; i++)
            {
                Assert.Equal("outer-string", outer.Load<ReOuter>(outerBytes, 1).OuterName);
                Assert.Equal("other-string", Other().Load<ReOther>(otherBytes, 1).OtherName);
                Assert.Equal("outer-string", outer.Load<ReOuter>(SlowPath(outerBytes), 1).OuterName);
                Assert.Equal("shared-string", outer.Load<ReOuter>(outerBytes, 1).Shared.SharedName);
            }
        }

        [Fact]
        public void Nesting_Does_Not_Leak_A_Context_When_The_Inner_Operation_Fails()
        {
            NestState.Reset();
            NestState.OtherSerializer = Other();
            NestState.OtherBytes = new byte[] { 1, 2, 3 };   // not a save at all

            var outer = Outer();
            var bytes = outer.Save(SampleOuter(), 1);

            // The callback's load fails; the outer load must still fail for its own reason and must not
            // leave a half-populated context behind for the next call.
            Assert.ThrowsAny<Exception>(() => outer.Load<ReOuter>(bytes, 1));
            Assert.ThrowsAny<Exception>(() => outer.Load<ReOuter>(bytes, 1));

            // With valid bytes again, both readers work, so nothing was left behind.
            NestState.OtherBytes = OtherBytes();
            Assert.Equal("outer-string", outer.Load<ReOuter>(bytes, 1).OuterName);
            Assert.Equal("outer-string", outer.Load<ReOuter>(SlowPath(bytes), 1).OuterName);
        }
    }
}
