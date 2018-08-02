namespace Ceras.Resolvers
{
	// Spells, Npcs, ScriptObjects, ... all belong into their own file.
	// because they are "root objects", they are serialized as an ID
	
	// If you have a field like "Spell mySpell;" in an Npc, it will write it as its id (from GetReferenceId)
	// 

	public interface IExternalRootObject
	{
		// What ID to write as reference
		int GetReferenceId();
	}

	public interface IExternalObjectResolver
	{
		void Resolve<T>(int id, out T value);
	}
}
