namespace Ceras.Formatters
{

	/*
	 * Situation:
	 * There's an object that has no parameterless constructor. Either it always needs parameters, or there's a static 'Create' method.
	 * 
	 * Problem:
	 * Currently we instantiate a new object and then we populate the members.
	 * 
	 * Approach:
	 * Read all values into local variables,
	 * then construct the object using whatever method there is (serialization ctor or static factory method).
	 * Once we have the object we simply assign the remaining members.
	 * 
	 * Things to keep in mind:
	 * - Ensure all remaining members can be written
	 * - Ensure we read all members into locals for two reasons:
	 *		- most likely the order of the members will not exactly match the constructor
	 *		- performance is better with multiple consecutive read steps, then all assignment steps
	 *		- code is much simpler
	 * 
	 * 
	 * !! Big problem:
	 * What if the object in question has a sub-object, which contains a reference to the parent?
	 * At that point we do not have that object yet since we're still "collecting" the individual members...
	 * Pre-creating a deserialization proxy like we already do won't help! The proxy must have a value at the time the sub-object is created (which is of course not the case then)
	 * And we can't just collect a few members and then call the ctor, because we have to read stuff in the order its given to us, we have barely any control over the serialization schema,
	 * as it is optimized for other things and has to stay that way (robustness against renamings and base-class-switches, ...)
	 * 
	 * It seems like a pretty hard problem.
	 * - Maybe I can think of something when I have more time, or someone else comes up with an idea
	 * - Maybe we can cheat? Like we'd leave the reference be null, and then have some sort of 'post deserialization' callback or something where the user can fix whatever mess was created?
	 *   DeserializationCallbacks are on the todo list anyway!
	 * - Maybe treat it as an explicitly not-supported edge case?
	 * 
	 *
	 */

	// Not sure if the name really fits, maybe an alternative would be DelayedFormatter or something...

	class ConstructorFormatter
	{
	}
}
