using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Exceptions
{
    public class CerasException : Exception
    {
        public CerasException(string message) : base(message)
        {
        }

        public CerasException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    public class InvalidConfigException : CerasException
    {
        public InvalidConfigException(string message) : base(message)
        {

        }
    }

    public class ConfigurationSealedException : CerasException
    {
        public ConfigurationSealedException(string message) : base(message)
        {
        }
    }

    public class WarningException : CerasException
    {
        public WarningException(string message) : base(message)
        {

        }
    }

    /// <summary>
    /// Thrown when the data format version does not match the library's version.
    /// </summary>
    public class CerasVersionMismatchException : CerasException
    {
        public uint FileVersion { get; }
        public uint LibraryVersion { get; }

        public CerasVersionMismatchException(string message, uint fileVersion, uint libraryVersion) : base(message)
        {
            FileVersion = fileVersion;
            LibraryVersion = libraryVersion;
        }
    }

    /// <summary>
    /// Thrown when the user-provided robust version in the file is newer than the current application version.
    /// </summary>
    public class CerasRobustVersionException : CerasException
    {
        public uint FileRobustVersion { get; }
        public uint CurrentRobustVersion { get; }

        public CerasRobustVersionException(string message, uint fileRobustVersion, uint currentRobustVersion) : base(message)
        {
            FileRobustVersion = fileRobustVersion;
            CurrentRobustVersion = currentRobustVersion;
        }
    }

    /// <summary>
    /// Thrown when data corruption is detected (e.g., CRC mismatch or invalid magic).
    /// </summary>
    public class CerasDataCorruptionException : CerasException
    {
        public CerasDataCorruptionException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown when there is an error resolving types or members during schema migration.
    /// </summary>
    public class CerasSchemaException : CerasException
    {
        public CerasSchemaException(string message) : base(message) { }
        public CerasSchemaException(string message, Exception inner) : base(message, inner) { }
    }
}
