namespace Ceras.Format
{
    /// <summary>
    /// Shared on-the-wire protocol primitives, specified in Format/LAYOUT.md. The
    /// reference/polymorphism markers and biases are defined once here so the formatters and the
    /// migration interpreter cannot drift apart. The byte encoding itself is little-endian and
    /// fixed-width by default.
    /// </summary>
    internal static class Protocol
    {
        // Reference / polymorphism markers (written with a +5 bias so that -1..-5 are distinguishable
        // from non-negative object-cache IDs).
        public const int RefBias = 5;
        public const int RefNull = -1;
        public const int RefNewValue = -2;
        public const int RefNewValueSameType = -3;
        public const int RefInlineType = -5;

        // Collection / array element counts use bias 1; -1 marks a null collection.
        //
        // Queue<>/Stack<> are the documented exception: QueueFormatter/StackFormatter write their count
        // through the injected IFormatter<int>, i.e. the plain fixed 4-byte little-endian int, so they are
        // unbiased and have no null marker. The two rules are encoded per shape in
        // SchemaTraversalEngine.ReadCollectionCount, which decides from the file's definition id.
        public const int CollectionBias = 1;
        public const int CollectionNull = -1;
    }
}
