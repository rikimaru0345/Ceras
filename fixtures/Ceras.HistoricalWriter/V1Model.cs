// The "old" model of a save file, as version 1 of a game wrote it. This assembly is the *historical
// writer*: it is never referenced by tests/Ceras.Test, so none of these CLR types exist in the reader
// build. The reader tests load the checked-in bytes this project produces and see only the current
// model in tests/Ceras.Test/Tests/HistoricalFixtureTests.cs.
//
// Keep these declarations frozen: the golden fixtures in
// tests/Ceras.Test/Fixtures/HistoricalV1/ are bytes of *this* model, and every rename/removal/retyping
// is pinned by a [PreviousName]/[PreviousTypeName] alias on the current side. To change a fixture,
// change this model *and* the current model *and* regenerate (see README.md in this folder).
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;

namespace Ceras.HistoricalWriter.V1
{
    /// <summary>Unsigned enum: v1 spelled its top member <c>Max</c>.</summary>
    public enum Rank : ulong
    {
        Recruit = 0,
        Max = ulong.MaxValue,
    }

    /// <summary>v1's settings type. Renamed to <c>GameSettings</c> in v2.</summary>
    [CerasContext("HistoricalV1")]
    [CerasSerializable]
    public class Settings
    {
        public bool Fullscreen;
        public int Volume;
        public string Profile;
    }

    /// <summary>
    /// v1's player type. In the current model this is <c>Actor</c>: the type was renamed,
    /// <c>Mana</c> became <c>long</c>, <c>Health</c> was removed, <c>Level</c> was added, and the rank
    /// member <c>Max</c> became <c>Highest</c>.
    /// </summary>
    [CerasContext("HistoricalV1")]
    [CerasSerializable]
    public class Player
    {
        public string Name;

        /// <summary>Removed in the current model; the reader has to skip its bytes.</summary>
        public int Health;

        /// <summary>Retyped to <c>long</c> in the current model; the reader has to widen it.</summary>
        public int Mana;

        public Rank Rank;

        /// <summary>Part of a reference cycle through <see cref="Friend"/>.</summary>
        public Player Friend;
    }

    /// <summary>
    /// v1's save root. <c>Main</c> is the *same instance* as <c>Players[0]</c> (a shared reference),
    /// <c>Stats</c> is a renamed field, and the collections carry the counts v1 wrote.
    /// </summary>
    [CerasContext("HistoricalV1")]
    [CerasSerializable]
    public class SaveGame
    {
        public int Version;
        public List<Player> Players;
        public Player Main;
        public Settings Stats;
        public int[] Slots;
        public Dictionary<string, int> Scores;
    }
}
