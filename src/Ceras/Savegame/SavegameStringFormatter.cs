using Ceras.Formatters;

namespace Ceras.Savegame
{
    /// <summary>
    /// Replaces the default Ceras string formatter for savegame serialization.
    ///
    /// On write: interns the string into the active <see cref="StringInternTable"/>
    /// (via <see cref="SavegameContext"/>) and writes a 4-byte LE index inline.
    ///
    /// On read: reads a 4-byte LE index and resolves it against the loaded
    /// <c>string[]</c> table (via <see cref="SavegameContext"/>).
    ///
    /// Register via <see cref="CerasSave.ConfigureSavegame"/> before building
    /// the <see cref="CerasSerializer"/>.
    /// </summary>
    public sealed class SavegameStringFormatter : IFormatter<string>
    {
        public void Serialize(ref byte[] buffer, ref int offset, string value)
        {
            SavegameContext.WriteTable.WriteIndex(ref buffer, ref offset, value);
        }

        public void Deserialize(byte[] buffer, ref int offset, ref string value)
        {
            value = StringTable.ReadString(buffer, ref offset, SavegameContext.ReadTable);
        }
    }
}
