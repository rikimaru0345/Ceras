namespace Ceras.Formatters
{
	using Ceras.Exceptions;
	using Ceras.Format;
	using System;
	using System.Buffers;
	using System.Reflection;
	using System.Runtime.CompilerServices;

	/// <summary>
	/// The half of <see cref="OpaqueFramedFormatter{T}"/> that does not need <c>T</c>: the wrapped
	/// formatter, the framed type's name, and the one place a framed block is read.
	/// <para>
	/// It is a base class rather than a helper because the frame has to be readable from two directions
	/// — the ordinary read path (<c>IFormatter&lt;T&gt;.Deserialize</c> and
	/// <c>DeserializeUntyped</c>) and the migration interpreter, which has a value it cannot type and
	/// drives the same read through a box and a field (<c>ReadFrame</c>). Both go through the method
	/// below, so "how many bytes is a block" has one answer in the library even on the path that only
	/// wants to skip.
	/// </para>
	/// <para>
	/// It is not generic because the interpreter holds one typed reference to it and asks a question
	/// that has no <c>T</c> in it: whether the formatter this build would use for a type is a framed
	/// one at all. That question is asked about a <see cref="Type"/> the interpreter already has, which
	/// is why the type arrives as a constructor argument rather than as a type argument.
	/// </para>
	/// </summary>
	abstract class OpaqueFramedFormatterBase
	{
		/// <summary>
		/// The user custom formatter whose bytes the frame wraps. Not injected and not resolved: it is
		/// the instance the serializer picked for the framed type when that type's specific formatter was
		/// resolved (<c>CerasSerializer.WrapOpaqueIfSelected</c>), and it is final for the life of the
		/// serializer. A formatter can only be installed through <see cref="TypeConfig.CustomFormatter"/>,
		/// a serializer seals its config when it is constructed, and the one door that survives that seal
		/// (<c>OnConfigNewType</c>) fires only for a type that has no configuration yet — which is a type
		/// whose specific formatter has not been resolved either. A formatter installed through that door is
		/// therefore picked up by the very resolution above rather than by some later save, and a frame that
		/// has been built is never left wrapped around a formatter that has since been replaced.
		/// </summary>
		protected readonly IFormatter Inner;

		/// <summary>The type this formatter is the specific formatter for, and the name refusals use.</summary>
		readonly Type _framedType;

		readonly string _framedTypeName;

		protected OpaqueFramedFormatterBase(IFormatter inner, CerasSerializer serializer, Type framedType)
		{
			if (inner == null) throw new ArgumentNullException(nameof(inner));
			if (serializer == null) throw new ArgumentNullException(nameof(serializer));
			if (framedType == null) throw new ArgumentNullException(nameof(framedType));

			// The wrapped formatter is an exact match for the framed type, refused here rather than by
			// the caller's own check, because this is the only place both halves are known: a frame
			// around a formatter for some other type produces a file that says "these bytes are a T" and
			// hands back something else, and the message would name a formatter the caller never chose
			// for T.
			FormatterHelper.ThrowOnMismatch(inner, framedType);

			// A frame is a declared length in front of a payload, and the only things in the format that
			// describe one are a robust context's schema section and layout fingerprint. A raw or
			// transient context describes nothing, so a frame written in one is bytes no reader could
			// account for. CerasSerializer.WrapOpaqueIfSelected only wraps for a robust config, so this
			// is the last line of defence rather than the first: it catches a hand-built framed
			// formatter handed to a raw context, and it asks only for the config's *type*, so the answer
			// cannot depend on when the wrap happened.
			if (!(serializer.Config is CerasRobustConfig))
				throw new CerasSchemaException(
					$"Cannot frame the custom formatter for '{TypeIdentity.GetCanonicalName(framedType)}' in a context that is not a robust one. " +
					"A frame is a declared length in front of a custom payload, and only a robust context's schema section and layout fingerprint " +
					"describe one; a raw or transient context describes nothing, so a frame written in one is bytes no reader could account for. " +
					"Use a raw context for a value that is only serialized transiently (CerasFastSerializer), where a custom formatter's bytes " +
					"are exactly what they always were.");

			_framedType = framedType;
			_framedTypeName = TypeIdentity.GetCanonicalName(framedType);
			Inner = inner;
		}

		/// <summary>The framed type, for a diagnostic that is reading a value rather than a type.</summary>
		internal Type FramedType => _framedType;

		/// <summary>The framed type's canonical name — the same identity the schema section records.</summary>
		internal string FramedTypeName => _framedTypeName;

		/// <summary>
		/// Reads one framed block and hands it to the wrapped formatter through the non-generic
		/// <see cref="IFormatter.DeserializeUntyped"/>, so a block is written and read in one place and
		/// the two directions cannot describe it differently.
		/// <para>
		/// The caller's reader is advanced past the block <em>before</em> the inner formatter runs, by
		/// the declared length. That is what makes an over- or under-reading formatter a diagnosed
		/// disagreement rather than a hole in the stream: the bytes after the block are found by the
		/// length the writer wrote, never by however much the reader happened to consume.
		/// </para>
		/// </summary>
		internal void ReadFrame(ref SequenceReader<byte> reader, object container, MemberInfo member)
		{
			var position = $"the value the save records as '{_framedTypeName}'";

			var block = OpaquePayload.ReadBlock(ref reader, position, out int declared);
			Inner.DeserializeUntyped(ref block, container, member);
			OpaquePayload.RequireBlockConsumed(ref block, position, declared);
		}
	}

	/// <summary>
	/// Wraps a user custom formatter so that its payload is a **declared length followed by the
	/// formatter's own bytes**, and the manifest entry for the type says so.
	/// <para>
	/// The wrap is the whole mechanism behind opaque custom payloads, and it is installed as the
	/// <em>specific</em> formatter for the type (<c>CerasSerializer.WrapOpaqueIfSelected</c>) because
	/// that is the one place a formatter is chosen for a type: the generated readers and writers, the
	/// collection and array formatters and the migration interpreter all ask for the specific
	/// formatter, so wrapping it once frames a custom payload on every path that writes the type — a
	/// root, a member, a collection element, a dictionary key or value, a tuple item, a polymorphic
	/// slot — with no generated code and no collection special case.
	/// </para>
	/// <para>
	/// <b>[CerasNoInject]</b> is load-bearing, not tidiness. Ceras injects a formatter into every
	/// <c>IFormatter&lt;T&gt;</c>-typed field it finds, and this class holds the wrapped formatter in
	/// exactly such a field: injection would hand it the <em>reference</em> formatter for the type and
	/// either nest a second frame around the same bytes or, because the field already holds the user's
	/// formatter, refuse with an "Injection conflict" that names neither the frame nor the custom
	/// formatter. This class is handed everything it needs — the formatter it wraps, and the type it
	/// wraps it for — and there is nothing left for injection to decide.
	/// </para>
	/// <para>
	/// A reference-typed value keeps its reference marker, because the marker belongs to the writer's
	/// own reference wrapper and stands <em>outside</em> the frame: a class value is
	/// <c>marker, length, payload</c>, exactly as a generated class value is <c>marker, members</c>, and
	/// the object-cache slot is reserved in the same place. A value type is <c>length, payload</c> with
	/// no marker. The file records which, because a reader that no longer has the type has nothing else
	/// to go on.
	/// </para>
	/// <para>
	/// The declared length costs a scratch buffer: the payload is produced first, into a writer of its
	/// own, and copied out behind its length. That is the price of a format that can skip a value it
	/// cannot read, and it is paid only by the values that <em>have</em> a custom formatter — every
	/// other value still goes straight into the save's buffer.
	/// </para>
	/// </summary>
	[CerasNoInject]
	sealed class OpaqueFramedFormatter<T> : OpaqueFramedFormatterBase, IFormatter<T>
	{
		/// <summary>
		/// The wrapped formatter, typed. Held separately from the base's non-generic slot because the
		/// ordinary read and write paths go through <c>IFormatter&lt;T&gt;</c> — the direct call a
		/// generated reader makes — and only the interpreter's untyped boundary needs the other one.
		/// </summary>
		readonly IFormatter<T> _typed;

		/// <summary>
		/// <c>StrongBox&lt;T&gt;.Value</c>, found once per framed type. The typed read hands its out
		/// parameter to the untyped read, which is the only read the frame has; a field the library does
		/// not itself write through is a boundary it cannot check, so this is resolved here, once, with a
		/// message that says what is wrong if it is ever absent.
		/// </summary>
		readonly FieldInfo _boxValueField;

		public OpaqueFramedFormatter(IFormatter inner, CerasSerializer serializer)
			: base(inner, serializer, typeof(T))
		{
			_typed = (IFormatter<T>)inner;
			_boxValueField = typeof(StrongBox<T>).GetField(nameof(StrongBox<object>.Value))
				?? throw new CerasSchemaException(
					$"Cannot read a value the save records as '{FramedTypeName}' through its custom formatter: " +
					$"'{typeof(StrongBox<T>)}' has no public 'Value' field for Ceras to hand the result through. This is a defect in Ceras, " +
					"not in the save.");
		}

		public void Serialize(IBufferWriter<byte> writer, T value)
		{
			// The payload is produced before its length is known, and an IBufferWriter cannot be
			// back-patched, so it goes into a writer of its own and is copied out behind the length. A
			// pooled scratch is deliberately not used: the block is written once and copied, and the
			// frame is what makes a value this build cannot read skippable, so its cost is paid only by
			// the values that have a custom formatter.
			var scratch = new ArrayBufferWriter<byte>();
			_typed.Serialize(scratch, value);

			OpaquePayload.WriteLength(writer, scratch.WrittenCount);
			SerializerBinary.Write(writer, scratch.WrittenSpan);
		}

		public void Deserialize(ref SequenceReader<byte> reader, ref T value)
		{
			// The frame has one read and it is untyped (see OpaqueFramedFormatterBase.ReadFrame), so the
			// out parameter is handed through a box and read back. The seed matters: a value type member
			// reuses the instance the reader already holds, and the formatter writes through the box
			// exactly as it would have written through the member.
			var box = new StrongBox<T>(value);
			ReadFrame(ref reader, box, _boxValueField);
			// A custom formatter may legitimately produce null (a null reference payload is a value), and
			// IFormatter<T>'s out parameter is declared non-nullable, so the null is assigned rather than
			// pretended not to happen — the same reading of the contract ReferenceFormatter.Deserialize
			// states for a null marker.
			value = box.Value!;
		}

		public void SerializeUntyped(IBufferWriter<byte> writer, object value)
			=> Serialize(writer, (T)value);

		public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
			=> ReadFrame(ref reader, container, member);
	}
}
