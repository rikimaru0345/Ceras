namespace Ceras.Formatters
{
	using System;
	using System.Collections.Generic;
	using Helpers;
	using Resolvers;

	/*
	 This is a thing intended to be used with STRING or OBJECT for T.
	 It saves seen instances into dict/list while serializing
	 This enables dealing with any object graph, even cyclic references of any kind.
	 It also save binary size(and thus a lot of cpu cycles as well if the object is large)
	 todo: different CacheFormatters MUST share a common pool! Why?
	 todo: .. because what if we have an object of a known type(field type and actual type are exactly the same)
	 todo: .. and then we later find an object-field with a refernce to an encountered object!
	 todo: .. lets say the data tells us "it's the object with Id 5".
	 todo: .. now that's all the information we have, and there are multiple "Caches" with different types.
	 todo: .. that means we wouldn't know which object type it is even supposed to be. The data doesn't tell us since the ID is the ONLY thing there
	 todo: .. and the field-type itself doesn't tell us anything either (since it is 'object')
	 todo: .. Solution: One big common cache for all the objects!
	
	
	 The only alternative solution would be to do this in the serializer
	 - When an object is encountered again, we write "type+ cahedObjId"
	   which will then result in 2 things written (so not minimum amount of data)
	 - Could only be avoided when we know the object is of exactly the right type (not higher or lower in the type-hierarchy)
	   Maybe if the type is sealed??
	 - After writing type+ID, when we deserialize again, we can use the type to know what CacheFormatter<T> we're looking for, 
	   so we resolve the right one, and then look into what objects it has.


	 But what are the advantages/disadvantages of each approach??
	 SharedObjectCache
	 + less dictionary lookups (no lookup of type->cache, and then id->obj)
	 + less space used in the binary file
	 - different object types can not share IDs, so if there's an NPC with Id 5, then there can't be a spell with Id 5!
	 
	 Cache by type
	 - need to write type for every ID(so we know what specific type cache it is in)
	 + can define own IDs per object, so no gaps
	 - have to do two lookups at deserialization(typeId -> cache; cache+objId -> obj)
	
	
	 Variant 3:
	 Could we just completely skip over all root objects (except the one being currently serialized), and only write IDs that are given to the serializer by some interface of the object?
	 -> At read time, how do we resolve circular refernces?
	 	  - the object has to resolve references as they are encountered:   void Resolve<T>(ref T obj, int id)
		   -it can pass that to the serializer again if it wants to...

	 -> how do we handle fields like "object r;" which can contain a rootObject or normal one?
	      - at deserialization time there are a few cases:
		  			- 'null' -> write null to field
					- 'type:SpellRoot' + 'spellID' -> call Resolve<Spell>(ref obj, spellId);
					- 'type:SimpleExample' + 'existingId' -> look into normal object cache

	      - we're back to solution1, we need to know the type first, and then switch based on that. maybe resolve, maybe nomal object, ...

	- while serializing, we can put all root objects into a hash-set so the user knows what objects we encountered in the graph
	  maybe that would be helpful to know, so those objects can be serialized as well (maybe with dirty-checks or so...)

		todo: we went with the easiest method (just write ID, let the user resolve IDs at deserialization time)
		
		todo: can we improve this? Could we  instead just write a IFormatter<IRootObject>, so only those objects will get special treatment, and the CacheFormatter doesn't have to check every single object for IExternalRootObject

*/

	public class CacheFormatter<T> : IFormatter<T>
	{
		// -3: For external objects that the user has to resolve
		// -2: New Value/Object that we've never seen before
		// -1: Actually <null>
		//  0: The previously seen item with index 0
		//  1: The previously seen item with index 1
		//  2: The previously seen item with index 2
		// ...
		const int Bias = 3;
		const int ExternalObject = -3;
		const int NewValue = -2;
		const int Null = -1;

		readonly IFormatter<T> _innerFormatter;
		readonly CerasSerializer _serializer;
		readonly ObjectCache _objectCache;

		public bool IsSealed { get; private set; }

		public CacheFormatter(IFormatter<T> innerFormatter, CerasSerializer serializer, ObjectCache objectCache)
		{
			_innerFormatter = innerFormatter;
			_serializer = serializer;
			_objectCache = objectCache;
		}


		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			if (EqualityComparer<T>.Default.Equals(value, default(T)))
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, Null, Bias);
				return;
			}

			if (!object.ReferenceEquals(_serializer.CurrentRoot, value))
			{
				if (value is IExternalRootObject externalObj)
				{
					SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, ExternalObject, Bias);

					var refId = externalObj.GetReferenceId();
					SerializerBinary.WriteInt32(ref buffer, ref offset, refId);
					return;
				}
			}

			if (_objectCache.TryGetExistingObjectId(value, out int id))
			{
				// Existing value
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, id, Bias);
			}
			else
			{
				if (IsSealed)
				{
					throw new InvalidOperationException($"Trying to add '{value.ToString()}' (type '{typeof(T).FullName}') to a sealed cache formatter.");
				}

				// New value
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, NewValue, Bias);

				// Important: Insert the ID for this value into our dictionary BEFORE calling SerializeFirstTime, as that might recursively call us again (maybe with the same value!)
				_objectCache.RegisterObject(value);

				// Write the object normally
				_innerFormatter.Serialize(ref buffer, ref offset, value);
			}
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			var objId = SerializerBinary.ReadUInt32Bias(buffer, ref offset, Bias);

			if (objId == Null)
			{
				// Null
				value = default(T);
				return;
			}

			if (objId == NewValue)
			{
				// New object

				// !! Important !!
				// When we're deserializing any nested types (ex: Literal<float>)
				// then we're writing Literal`1 as 0, and System.Single as 1
				// but while reading, we're recursively descending, so System.Single would get read first (as 0)
				// which would throw off everything.
				// Solution: 
				// Reserve some space (inserting <null>) into the list before actually reading, so all the nested things will just append to the list as intended.
				// and then just overwrite the inserted placeholder with the actual value once we're done

				// Problem:
				// If there's a root object that has a field that simply references the root object
				// then we will write it correctly, but since we only update the reference after the object is FULLY deserialized, we end up with nulls.
				//
				// Solution:
				// Right when the object is created, it has to be immediately put into the _deserializationCache
				// So child fields (like '.Self') can properly resolve the reference.

				// Problem:
				// How to even do that?
				// So when the object is created (but its fields are not yet fully populated) we must get a reference to
				// it immediately, before any member-field is deserialized! (Because one of those fields could be a self-refernce)
				// How can we intercept this assignment/object creation?
				//
				// Solution:
				// We pass a reference to a proxy. If we'd just have a List<T> we would not be able to use "ref" (like: deserialize(... ref list[x]) )
				// So instead we'll pass the reference to a proxy object. 
				// The proxy objects are small, but spamming them for every deserialization is still not cool.
				// So we use a very simple pool for them.



				var objectProxy = _objectCache.CreateDeserializationProxy<T>();

				// Make sure that the deserializer can make use of an already existing object (if there is one)
				objectProxy.Value = value;

				_innerFormatter.Deserialize(buffer, ref offset, ref objectProxy.Value);

				// Write back the actual value
				value = objectProxy.Value;

				return;
			}

			if (objId == ExternalObject)
			{
				// External object, let the user resolve!
				int externalId = SerializerBinary.ReadInt32(buffer, ref offset);

				// Let the user resolve
				_serializer.Config.ExternalObjectResolver.Resolve(externalId, out value);
				return;
			}

			// Something we already know
			value = _objectCache.GetExistingObject<T>(objId);
		}

		/// <summary>
		/// Set IsSealed, and will throw an exception whenever a new value is encountered.
		/// Intended to be used when serializing Types (inner formatter is 'TypeFormatter') and using KnownTypes.
		/// The idea is that you populate KnownTypes in advance, then call Seal(), so you'll be notified *before* something goes wrong (a new, unintended, type getting serialized)
		/// </summary>
		public void Seal()
		{
			IsSealed = true;
		}

	}


}