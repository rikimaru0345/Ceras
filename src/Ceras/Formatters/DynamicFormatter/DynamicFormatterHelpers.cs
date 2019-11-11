namespace Ceras.Formatters
{
	using Exceptions;
	using System;
	using System.Collections.Generic;
	using System.Linq.Expressions;
	using System.Reflection;
	using Helpers;
	using static System.Linq.Expressions.Expression;

	static class DynamicFormatterHelpers
	{
		static readonly MethodInfo _setValue = typeof(FieldInfo).GetMethod(
																		   name: "SetValue",
																		   bindingAttr: BindingFlags.Instance | BindingFlags.Public,
																		   binder: null,
																		   types: new Type[] { typeof(object), typeof(object) },
																		   modifiers: new ParameterModifier[2]);

		// A member has been deserialized into a local variable, and now it has to be written back to its actual field (which is readonly)
		// Depending on the setting we'll do different things here.
		internal static void EmitReadonlyWriteBack(Type type, ReadonlyFieldHandling readonlyFieldHandling, FieldInfo fieldInfo, ParameterExpression refValueArg, ParameterExpression tempStore, List<Expression> block, List<ParameterExpression> locals)
		{
			if (readonlyFieldHandling == ReadonlyFieldHandling.ExcludeFromSerialization)
				throw new InvalidOperationException($"Error while trying to generate a deserializer for the field '{fieldInfo.DeclaringType.FriendlyName(true)}.{fieldInfo.Name}': the field is readonly, but ReadonlyFieldHandling is turned off in the configuration.");

			// 4. ReferenceTypes and ValueTypes are handled a bit differently (Boxing, Equal-vs-ReferenceEqual, text in exception, ...)
			if (type.IsValueType)
			{
				// Value types are simple.
				// Either they match perfectly -> do nothing
				// Or the values are not the same -> throw exception or forced overwrite

				Expression onMismatch;
				if (readonlyFieldHandling == ReadonlyFieldHandling.ForcedOverwrite)
				{
					// field.SetValue(valueArg, tempStore)

					if (refValueArg.Type.IsValueType)
					{
						// Worst possible case...
						// Pretty big performance hit, but there's no other way
						var boxedRefVal = Variable(typeof(object), "boxedRef");
						locals.Add(boxedRefVal);

						var setValCall = Call(
							Constant(fieldInfo), _setValue,
							arg0: boxedRefVal,
							arg1: Convert(tempStore, typeof(object))); // Explicit boxing needed

						// Explicit unbox
						onMismatch = Block(
							Assign(boxedRefVal, Convert(refValueArg, typeof(object))),
							setValCall,
							Assign(refValueArg, Convert(boxedRefVal, refValueArg.Type)));
					}
					else
					{
						onMismatch = Call(
							Constant(fieldInfo), _setValue,
							arg0: refValueArg,
							arg1: Convert(tempStore, typeof(object))); // Explicit boxing needed
					}
				}
				else
				{
					onMismatch = Throw(Constant(new CerasException($"The value-type in field '{fieldInfo.Name}' does not match the expected value, but the field is readonly and overwriting is not allowed in the configuration. Make the field writeable or enable 'ForcedOverwrite' in the serializer settings to allow Ceras to overwrite the readonly-field.")));
				}

				block.Add(IfThenElse(
									 test: StructEquality.IsStructEqual(tempStore, Field(refValueArg, fieldInfo)),
									 ifTrue: Empty(),
									 ifFalse: onMismatch
									));
			}
			else
			{
				// Either we already give the deserializer the existing object as target where it should write to, in which case its fine.
				// Or the deserializer somehow gets its own object instance from somewhere else, in which case we can only proceed with overwriting the field anyway.

				// So the most elegant way to handle this is to first let the deserializer do what it normally does,
				// and then check if it has changed the reference.
				// If it did not, everything is fine; meaning it must have accepted 'null' or whatever object is in there, or fixed its content.
				// If the reference was changed there is potentially some trouble.
				// If we're allowed to change it we use reflection, if not we throw an exception


				Expression onReassignment;
				if (readonlyFieldHandling == ReadonlyFieldHandling.ForcedOverwrite)
				{
					// field.SetValue(valueArg, tempStore)

					if (refValueArg.Type.IsValueType)
					{
						// Worst possible case...
						// Pretty big performance hit, but there's no other way
						var boxedRefVal = Variable(typeof(object), "boxedRef");
						locals.Add(boxedRefVal);

						var setValCall = Call(
							Constant(fieldInfo), _setValue,
							arg0: boxedRefVal,
							arg1: Convert(tempStore, typeof(object))); // Explicit boxing needed

						// Explicit unbox
						onReassignment = Block(
							Assign(boxedRefVal, Convert(refValueArg, typeof(object))),
							setValCall,
							Assign(refValueArg, Convert(boxedRefVal, refValueArg.Type)));
					}
					else
					{
						onReassignment = Call(Constant(fieldInfo), _setValue, arg0: refValueArg, arg1: tempStore);
					}
				}
				else
				{
					onReassignment = Throw(Constant(new CerasException("The reference in the readonly-field '" + fieldInfo.Name + "' would have to be overwritten, but forced overwriting is not enabled in the serializer settings. Either make the field writeable or enable ForcedOverwrite in the ReadonlyFieldHandling-setting.")));
				}
				// Did the reference change?
				block.Add(IfThenElse(
									 test: ReferenceEqual(tempStore, MakeMemberAccess(refValueArg, fieldInfo)),

									 // Still the same. Whatever has happened (and there are a LOT of cases), it seems to be ok.
									 // Maybe the existing object's content was overwritten, or the instance reference was already as expected, or...
									 ifTrue: Empty(),

									 // Reference changed. Handle it depending on if its allowed or not
									 ifFalse: onReassignment
									));
			}
		}


		public static MethodInfo ResolveSerializeMethod(this Type formatterType, Type exactTypeGettingFormatted)
			=> ResolveSerializeMethod(formatterType, exactTypeGettingFormatted, true);

		public static MethodInfo ResolveDeserializeMethod(this Type formatterType, Type exactTypeGettingFormatted)
			=> ResolveSerializeMethod(formatterType, exactTypeGettingFormatted, false);

		static MethodInfo ResolveSerializeMethod(Type formatterType, Type exactTypeGettingFormatted, bool serialize)
		{
			var methods = formatterType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

			var name = serialize ? nameof(IFormatter<int>.Serialize) : nameof(IFormatter<int>.Deserialize);

			for (int i = 0; i < methods.Length; i++)
			{
				var method = methods[i];

				if (method.Name != name)
					continue;

				var args = method.GetParameters();
				if (args.Length != 3)
					continue;

				var paramType = args[2].ParameterType;
				if (paramType.IsByRef)
					paramType = paramType.GetElementType();

				if (paramType == exactTypeGettingFormatted)
				{
					return method;
				}
			}

			throw new CerasException($"Can't find Serialize/Deserialize for ''{exactTypeGettingFormatted?.FriendlyName(true)}'' on formatter type '{formatterType?.FriendlyName(true)}'");
		}
	}


	struct MemberParameterPair
	{
		public MemberInfo Member;
		public ParameterExpression LocalVar;
	}
}