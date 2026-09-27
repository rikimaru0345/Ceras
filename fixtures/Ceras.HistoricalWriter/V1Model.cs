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
    /// <summary>Unsigned enum: v1 spelled its top member <c>Max</c>, and v1 had a member the current
    /// model has deleted.</summary>
    public enum Rank : ulong
    {
        Recruit = 0,
        Max = ulong.MaxValue,
    }

    /// <summary>A second enum, to pin a value whose member no longer exists in the current model.</summary>
    public enum Mode : int
    {
        Idle = 0,
        Legacy = 1,
        Removed = 2,
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
    /// A struct: fixed-width, and the current model drops one of its fields. Pinning the *skip* of a
    /// removed field inside a fixed-size value is a different code path from a class field.
    /// </summary>
    [CerasContext("HistoricalV1")]
    [CerasSerializable]
    public struct Coord
    {
        public int X;
        public int Y;
        public int Z;
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
    /// A polymorphic slot: <c>Owner</c> is declared as the base, so the file records the concrete type by
    /// id. The current model renames <see cref="Creature"/> to <c>Monster</c>, which is only reachable
    /// through the alias.
    /// </summary>
    [CerasContext("HistoricalV1")]
    [CerasSerializable]
    public class Creature
    {
        public int HitPoints;
    }

    [CerasContext("HistoricalV1")]
    [CerasSerializable]
    public class Monster : Creature
    {
        public string Species;
    }

    /// <summary>Nested collections of a fixed-size value type, plus a keyed one.</summary>
    [CerasContext("HistoricalV1")]
    [CerasSerializable]
    public class Inventory
    {
        public List<Coord> Slots;
        public Dictionary<string, Coord> Named;
        public List<List<int>> Groups;
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
        public Mode Difficulty;

        /// <summary>A field v1 wrote between <c>Version</c> and <c>Players</c>: the current model
        /// declares it *after* them, so the reader has to follow the file's order, not the current one.</summary>
        public string Chapter;

        public Inventory Bag;
        public Creature Owner;
    }
}
