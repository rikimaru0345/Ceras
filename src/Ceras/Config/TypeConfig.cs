namespace Ceras
{
	using Exceptions;
	using Formatters;
	using Helpers;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Runtime.CompilerServices;
	using System.Runtime.Serialization;
	using IFormatter = Formatters.IFormatter;


	/// <summary>
	/// <para>For performance reasons <see cref="CerasSerializer"/>, <see cref="CerasConfig"/>, and <see cref="TypeConfig"/> are not thread-safe. Do not share a config instance between serializers.</para>
	/// <para>
	/// A type's configuration is open until one of two things closes it, and which one it was is
	/// something the caller is told rather than left to guess: the schema for this type was built
	/// (emitted code and a formatter now depend on it), or the owning <see cref="CerasConfig"/> was
	/// sealed because a serializer was constructed. See <see cref="ThrowIfSealed()"/>.
	/// </para>
	/// </summary>
	public abstract class TypeConfig : ISealedConfiguration
	{
		public Type Type { get; }
		public CerasConfig Config { get; }
		public bool IsStatic { get; }

		bool _isSealed = false;

		// The two windows in which this type's configuration still accepts a change the seal rules
		// would otherwise refuse. Both are the library's own work: the constructor, which applies the
		// defaults and the attributes, and the OnConfigNewType callback for this type, which exists
		// because Ceras discovered the type now and could not have been told about it earlier. Neither
		// is a capability - the first is over before the constructor returns, the second is opened and
		// closed by CerasConfig around that one call, on this one instance (BeginNewTypeCallback).
		bool _isBeingBuilt;
		bool _isNewTypeCallbackOpen;


		//
		// Settings
		bool _isConstructionExplicit;

		/// <summary>
		/// The construction for this type, or null while none is selected. Both states are real:
		/// it is null until <see cref="TypeConfigDefaults"/> has applied a default (the
		/// <see cref="TypeConfig"/> constructor always ends with one), and a deliberate assignment
		/// of null puts it back — which <see cref="VerifyConstructionMethod"/> then reports as
		/// "no construction mode configured". The public property keeps the non-null contract every
		/// configured type satisfies.
		/// </summary>
		protected TypeConstruction? _typeConstruction;

		/// <summary>
		/// The construction Ceras uses to build instances of this type. A configured
		/// <see cref="TypeConfig"/> always has one: the constructor applies a library default, and
		/// <see cref="VerifyConstructionMethod"/> is the check that reports the one case where that
		/// is not true.
		/// </summary>
		public TypeConstruction TypeConstruction
		{
			get => _typeConstruction!;
			set
			{
				AssignConstruction(value);
				_isConstructionExplicit = true;
			}
		}

		/// <summary>
		/// True when the application selected the construction for this type (<c>ConstructBy(...)</c>,
		/// <c>ConstructByUninitialized()</c>, a <c>TypeConstruction</c> instance, or an assignment from an
		/// <c>OnConfigNewType</c> callback) rather than Ceras having picked the library default.
		/// <para>
		/// The readers need the difference. A generated (or custom) formatter builds the instance of a
		/// generated type itself, so the reference wrapper normally ignores the construction — otherwise
		/// Ceras' own default would silently take that job away from the generated code. An explicit
		/// selection is the opposite: it exists precisely to replace that allocation (a pool, a factory
		/// that hands out recycled instances), so it is honored even then. See
		/// <c>Formatters.ReferenceFormatter&lt;T&gt;</c> and <c>Format.CurrentObjectFactory</c>.
		/// </para>
		/// </summary>
		internal bool IsConstructionExplicit => _isConstructionExplicit;

		/// <summary>
		/// The construction Ceras picked for this type by itself (see <see cref="TypeConfigDefaults"/>).
		/// Assigned without marking the selection explicit, so "nobody configured anything" stays
		/// distinguishable from "somebody configured exactly this".
		/// </summary>
		internal void SetDefaultTypeConstruction(TypeConstruction construction) => AssignConstruction(construction);

		void AssignConstruction(TypeConstruction? value)
		{
			ThrowIfSealed();
			_typeConstruction = value;
			if (value != null)
			{
				value.TypeConfig = this;
				value.VerifyReturnType();
			}
		}

		/// <summary>
		/// The user's explicit constructor-to-member mapping, or null while none is set (and before
		/// <see cref="TypeConstruction.VerifyParameterMapping(MethodBase)"/> has built its automatic one).
		/// </summary>
		internal Dictionary<ParameterInfo, MemberInfo>? ParameterMap;


		/// <summary>The application's formatter callback for this type, or null when it set none.</summary>
		internal FormatterResolverCallback? _customResolver;

		/// <summary>
		/// A callback that supplies the formatter for this type, or null when there is none - which is
		/// the ordinary state, and the one Ceras' own resolution chain starts from. Setting it is
		/// mutually exclusive with <see cref="CustomFormatter"/>.
		/// </summary>
		public FormatterResolverCallback? CustomResolver
		{
			get => _customResolver;
			set
			{
				ThrowIfSealed();
				if (_overrideFormatter != null)
					ThrowCantSetResolverAndFormatter();
				if (value != null)
					ThrowIfTypeIsNotConfigurable();
				_customResolver = value;
			}
		}

		/// <summary>The formatter the application registered for this type, or null when it registered none.</summary>
		protected IFormatter? _overrideFormatter;

		/// <summary>
		/// The formatter the application registered for this type, or null when it registered none.
		/// Assigning null removes a previously registered formatter (and is counted as an assignment,
		/// so a robust context's re-check sees it). Setting it is mutually exclusive with
		/// <see cref="CustomResolver"/>.
		/// </summary>
		public IFormatter? CustomFormatter
		{
			get => _overrideFormatter;
			set
			{
				ThrowIfSealed();
				if (_customResolver != null)
					ThrowCantSetResolverAndFormatter();

				Config.NoteFormatterAssignment();

				if (value != null)
				{
					ThrowIfTypeIsNotConfigurable();
					FormatterHelper.ThrowOnMismatch(value, Type);
					_overrideFormatter = value;
				}
				else
				{
					_overrideFormatter = null;
				}
			}
		}

		/// <summary>
		/// Refuses a custom formatter for a type whose formatter Ceras would ignore anyway.
		/// <para>
		/// Resolution short-circuits every user hook for anything
		/// <see cref="Ceras.CerasSerializer.IsPrimitiveType"/> reports as primitive: the built-in
		/// formatter is chosen first, so a formatter configured here for, say, <see cref="int"/> was
		/// silently discarded and the save still used the library's own encoding. Silently ignoring
		/// configuration is the worst of the three outcomes - the code reads as though it took
		/// effect, the payload says otherwise, and the only symptom is a file that does not decode
		/// the way its writer expected - so this is refused up front instead.
		/// </para>
		/// <para>
		/// The escape hatch is to give the value a type of its own: a struct or a class with the
		/// shape you want and a formatter for that. That is not a workaround imposed for the sake of
		/// this check, it is how every other custom encoding in Ceras is already expressed, and it
		/// works with the source generator, the schema and the migration interpreter - none of which
		/// have a special case for a redefined <see cref="int"/>.
		/// </para>
		/// <para>
		/// <see cref="string"/> and <see cref="Type"/> are the deliberate exceptions, and they are
		/// not a loophole: their encodings are not fixed by the platform. A string's length limit is
		/// enforced by wrapping the formatter (<c>MaxSizeStringFormatter</c>,
		/// <c>RobustStringFormatter</c>), and a <see cref="Type"/> has to be written as a schema id
		/// rather than a name in the robust format. Both are configured through this same property,
		/// and both genuinely take effect.
		/// </para>
		/// </summary>
		void ThrowIfTypeIsNotConfigurable()
		{
			if (Type == typeof(string) || Type == typeof(Type))
				return;

			if (!Ceras.CerasSerializer.IsPrimitiveType(Type))
				return;

			throw new CerasException(
				$"Ceras cannot use a custom formatter for '{Type.FriendlyName(true)}', because the built-in " +
				$"encoding for this type is chosen before any user configuration is consulted, so the formatter " +
				$"would be silently ignored. Give the value a type of its own instead - a struct or class with " +
				$"the shape you want, and a custom formatter for that - and Ceras will use it everywhere, " +
				$"including in the schema and when migrating older saves. ('string' and 'Type' are the exceptions: " +
				$"their encodings are not fixed, so a formatter for them does take effect.)");
		}

		void ThrowCantSetResolverAndFormatter() => throw new InvalidOperationException("You can only set a custom resolver or a custom formatter instance, not both.");


		internal readonly ConfigList<MemberConfig> _allMembers;
		public IEnumerable<MemberConfig> Members => _allMembers.Where(m => !m.IsCompilerGenerated);


		protected TypeConfig(CerasConfig config, Type type, bool isStatic)
		{
			Config = config;
			Type = type;
			IsStatic = isStatic;

			// A type can be built after its config was sealed - that is what discovery does, and it is
			// the normal case for every type the application did not configure up front. Applying the
			// defaults and the attributes below is the library building the configuration, not the
			// application changing a closed one, so it is the one window the seal does not apply to.
			// The window is opened and closed inside this constructor, so it is not a capability: by the
			// time any derived constructor body - the only other code that can run here - is reached, it
			// is already shut again.
			_isBeingBuilt = true;

			// CreateMemberConfig, not Activator inline: TypeConfig<T> overrides it and constructs
			// MemberConfig<T> directly, and that is the only place in the library where the closed
			// instantiation is visible to a compiler. Calling it from the constructor is safe here
			// because the override cannot observe anything of TypeConfig<T> that is not set yet: the
			// only derived type is TypeConfig<T> itself (its constructor is internal, so nothing
			// outside this assembly can extend it) and its constructor body is empty. The defaults are
			// applied below, in the same order as before.
			var members = isStatic ? type.GetAllStaticDataMembers() : type.GetAllDataMembers();
			_allMembers = new ConfigList<MemberConfig>(this, $"the member list of type '{type.FriendlyName(true)}'");
			foreach (var m in members)
				_allMembers.Add(CreateMemberConfig(m));

			TypeConfigDefaults.ApplySpecializedDefaults(this); // apply special handling first

			TypeConfigDefaults.ApplyTypeAttributes(this); // then attributes on the type and members
			foreach (var m in _allMembers)
				TypeConfigDefaults.ApplyMemberAttributes(m);

			_isBeingBuilt = false;
		}

		/// <summary>
		/// Builds the config for one member this type exposes.
		/// <para>
		/// Called from the constructor, once per member, before any other construction runs: an
		/// override must not read state its own constructor has not assigned yet.
		/// </para>
		/// <para>
		/// The implementation here constructs the closed <c>MemberConfig&lt;T&gt;</c> reflectively, which
		/// a NativeAOT compiler can only satisfy once that closed type has been named statically
		/// somewhere - see <see cref="TypeConfig{T}"/>, which is the override that names it. A type
		/// reached only reflectively (through <see cref="CerasConfig.ConfigType(Type)"/>) otherwise
		/// gets <c>NotSupportedException: ... no code was generated for it</c> for a value-type
		/// argument.
		/// </para>
		/// </summary>
		protected virtual MemberConfig CreateMemberConfig(MemberInfo member)
		{
			// Activator returns object?; the constructed type is a MemberConfig<> over the very type
			// reflected over here, and its arguments are two real instances, so a null would be a
			// broken deployment rather than a runtime condition.
			return (MemberConfig)Activator.CreateInstance(
				typeof(MemberConfig<>).MakeGenericType(Type),
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
				null,
				new object[] { this, member },
				null)!;
		}

		internal MemberConfig GetMemberConfig(MemberInfo memberInfo)
		{
			for (int i = 0; i < _allMembers.Count; i++)
			{
				var mc = _allMembers[i];
				if (mc.Member == memberInfo)
					return mc;
			}

			// DeclaringType is null for a dynamic member (one with no declaring type at all, see
			// MemberConfig's constructor), which is the one case where it can be missing here.
			throw new InvalidOperationException($"Cannot find member '{Format.TypeIdentity.GetCanonicalNameOrUnknown(memberInfo.DeclaringType)}.{memberInfo.Name}' in type '{Type.FriendlyName(true)}'");
		}

		/// <summary>
		/// Don't use this.
		/// <para>
		/// This is the live list, not a copy - a change made through it is a change to this type - but
		/// it is a <see cref="ConfigList{T}"/> and not a <see cref="List{T}"/>. That is the whole
		/// reason it can be handed out at all: a plain <see cref="List{T}"/> would keep accepting
		/// changes after the type's configuration was sealed, and adding or removing a member then is
		/// the one edit that defeats every other check (it changes what the type serializes without
		/// going through any of them). <see cref="List{T}"/> cannot be intercepted - its methods are
		/// not virtual - so the guard has to be a different type, and casting the result back to
		/// <see cref="List{T}"/> now fails instead of producing an unguarded list. Before the seal the
		/// list still changes like any other, through <see cref="IList{T}"/>.
		/// </para>
		/// </summary>
		public IEnumerable<MemberConfig> UnsafeGetAllMembersIncludingCompilerGenerated()
		{
			return _allMembers;
		}


		/// <summary>
		/// This will include/exclude all members according to the same rules 'DataContractSerializer' would use.
		/// If the type has the <see cref="System.Runtime.Serialization.DataContractAttribute"/>, then the method succeeds and will include all members that have the <see cref="System.Runtime.Serialization.DataMemberAttribute"/> (doesn't matter if public or non-public).
		/// If the type does not have the DataContractAttribute the method fails and does nothing.
		/// If the <paramref name="force"/> parameter is true, the check for <see cref="System.Runtime.Serialization.DataContractAttribute"/> is skipped.
		/// </summary>
		public bool TryConfigureLikeDataContractSerializer(bool force = false)
		{
			var dataContract = Type.GetCustomAttribute<DataContractAttribute>();
			if (dataContract == null && !force)
				return false;

			foreach (var member in Members)
			{
				var ignore = member.Member.GetCustomAttribute<IgnoreDataMemberAttribute>(true);
				if (ignore != null)
				{
					member.ExcludeWithReason("User has called 'TryConfigureLikeDataContractSerializer', and the member has the [IgnoreDataMember] attribute.");
					continue;
				}

				var dataMember = member.Member.GetCustomAttribute<DataMemberAttribute>(true);
				if (dataMember != null)
				{
					if (!string.IsNullOrWhiteSpace(dataMember.Name))
						member.PersistentName = dataMember.Name;

					member.SetIncludeWithReason(SerializationOverride.ForceInclude, "User has called 'TryConfigureLikeDataContractSerializer', and the member has the [DataMember] attribute.");
				}
			}

			return true;
		}


		internal void Seal()
		{
			if (_isSealed)
				return;

			_isSealed = true;
		}

		internal void VerifyConstructionMethod()
		{
			TypeConfigDefaults.CheckStructWithAutoProps(this);

			if (TypeConstruction == null)
				throw new CerasException($"You have not configured a construction mode for the type '{Type.FullName}' and it has no parameterless constructor. There are many ways Ceras can handle this, select one of the methods in the TypeConfig ('config.ConfigType<YourType>().ConstructBy(...)')");

			TypeConstruction.VerifyReturnType();
			TypeConstruction.VerifyParameterMapping();
			VerifyNoUnsupportedImmutableCycle();
		}

		/// <summary>
		/// Runtime counterpart of the generator's CERAS031 diagnosis, for types that have no
		/// generated formatter (reflection fallback / external types).
		/// <para>
		/// A member whose declared type is a banned <em>shape</em> (a delegate, an enumerator,
		/// reflection metadata, a native engine object, ...) cannot be written: the member used to
		/// be dropped from the schema, so saving succeeded and loading produced a
		/// <c>null</c>/default where real data had been. Now that it is not in the schema, a
		/// formatter is not even built for it, so this is reported here - at the point where Ceras
		/// takes responsibility for the members of the type - instead of silently producing a lossy
		/// save.
		/// </para>
		/// <para>
		/// Only shape bans are diagnosed. A member of a type that carries <c>[Exclude]</c> is still
		/// simply absent from the schema, because that is what the attribute means, and a member the
		/// user excluded with <c>[Exclude]</c>/<see cref="SerializationOverride.ForceSkip"/> (or
		/// dropped via <c>[NonSerialized]</c>) is the documented way to keep such a member out of
		/// the payload. Registering a custom formatter for the member's type does <em>not</em>
		/// count: the member never reaches a formatter, and Ceras cannot tell whether an arbitrary
		/// formatter would restore the value.
		/// </para>
		/// </summary>
		internal void VerifyNoBannedMemberShapes(bool isStatic)
		{
			foreach (var member in _allMembers)
			{
				if (MemberIsStatic(member.Member) != isStatic) continue;
				if (member.IsExcludedByUser) continue;
				if (!BannedTypes.IsBannedShape(member.MemberType, out var reason)) continue;

				throw new BannedTypeException(
					$"Member '{member.DisplayName}' of type '{Type.FriendlyName(true)}' has the type '{member.MemberType.FriendlyName(true)}', " +
					$"which Ceras cannot serialize. Leaving the member out of the payload would save fine and load it back as null, " +
					$"so Ceras refuses to do it. Add [Exclude] to the member (or call ConfigType<{Type.FriendlyName()}>.ConfigField/ConfigProperty(\"{member.DisplayName}\").Exclude()) " +
					$"if it is intentionally not serialized, or replace it with a representation Ceras can write " +
					$"(for a delegate: store the target and the method identity yourself and re-attach it in an [OnAfterDeserialize] callback). " +
					$"Reason this type is banned: \"{reason}\".");
			}
		}

		static bool MemberIsStatic(MemberInfo member)
			=> member is FieldInfo f ? f.IsStatic : ((PropertyInfo)member).GetAccessors(true)[0].IsStatic;

		/// <summary>
		/// Runtime counterpart of the generator's CERAS026 diagnosis, for types that have no
		/// generated formatter (reflection fallback / external types). A constructor-dependent
		/// immutable cycle cannot be constructed: a readonly member participates in a cycle but the
		/// selected construction mode does not initialize it and there is no parameterless path, so the
		/// member would silently deserialize as its default value. Fails with an actionable message
		/// instead of producing a broken graph.
		/// <para>
		/// The rule is intentionally conservative and no network/live cycle validation is performed:
		/// it only looks at direct readonly members whose declared type is the same type (self-cycle),
		/// which is exactly the case a constructor-based strategy can never satisfy.
		/// </para>
		/// </summary>
		void VerifyNoUnsupportedImmutableCycle()
		{
			if (!Type.IsClass) return;

			// A null factory (ConstructNull, e.g. a custom formatter) or a parameterless constructor
			// that actually runs lets Ceras allocate+register before reading members, so cycles are fine.
			if (TypeConstruction is ConstructNull) return;
			if (Type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null) != null)
				return;

			foreach (var member in _allMembers)
			{
				if (member.IsCompilerGenerated) continue;
				if (!member.ComputeFinalInclusionFast()) continue;

				var memberType = member.Member.FieldOrPropType();

				// Only a direct self/mutual reference through a readonly member is provably
				// unconstructable at this level; anything else is left to the generator/manifest.
				if (memberType != Type) continue;

				bool isReadOnly = member.Member is FieldInfo f
					? f.IsInitOnly
					: member.Member is PropertyInfo p && (p.SetMethod == null || IsInitOnlySetter(p.SetMethod));
				if (!isReadOnly) continue;

				throw new CerasException(
					$"Type '{Type.FriendlyName(true)}' has an immutable (readonly/getter-only) member '{member.Member.Name}' " +
					$"of type '{memberType.FriendlyName()}' that participates in a reference cycle, but the selected construction " +
					$"strategy ({TypeConstruction.GetType().Name}) does not initialize it and there is no parameterless construction " +
					$"path. Ceras cannot construct this immutable cycle. Supply an explicit construction strategy: mark a constructor " +
					$"[CerasConstructor] that takes the cycled member as a parameter, add an accessible parameterless constructor, or " +
					$"supply a custom IFormatter<{Type.FriendlyName(true)}>.");
			}
		}

		/// <summary>True when the setter is an init-only setter (carries the IsExternalInit modreq).</summary>
		static bool IsInitOnlySetter(MethodInfo setter)
		{
			if (setter == null) return false;
			foreach (var modreq in setter.ReturnParameter.GetRequiredCustomModifiers())
				if (modreq.FullName == "System.Runtime.CompilerServices.IsExternalInit") return true;
			return false;
		}


		/// <summary>
		/// Why this type's configuration can no longer be changed, or null while it still can.
		/// <para>
		/// Two reasons, and they are genuinely different situations, so they get their own message
		/// rather than one that covers both: the schema for this type has been built (the original
		/// rule - dynamically emitted code and a resolved formatter now depend on this), or the owning
		/// <see cref="CerasConfig"/> was sealed because a serializer was constructed. The second used
		/// to be accepted with two different outcomes - ignored for a type the serializer had already
		/// resolved, applied for one it had not - which is why it is refused now; see
		/// <see cref="CerasConfig.ConfigType(Type)"/>.
		/// </para>
		/// </summary>
		string? WhyConfigurationIsClosed()
		{
			if (_isSealed)
				return "The configuration for this Type or Member is already sealed because the SerializationSchema has been instantiated (which means dynamically emitted code relies on it not changing anymore). All changes to the type or member configuration must be made before the the configuration is used in a 'CerasSerializer' instance, except for config callbacks like OnConfigNewType)";

			// The two windows, and nothing else: the library building this configuration, or the
			// OnConfigNewType callback running for this one type. A type's configuration is otherwise
			// immutable the moment a serializer exists, whether or not this type was ever reached.
			if (Config.IsSealed && !_isBeingBuilt && !_isNewTypeCallbackOpen)
				return $"The configuration for type '{Type.FriendlyName(true)}' can no longer be changed: the CerasConfig that owns it was sealed when the CerasSerializer was constructed, and a serializer does not read type configuration again after that. " +
					   "A change made now would be ignored for a type the serializer has already resolved and applied for one it has not reached yet, so the same call would mean different things for different types and the code that made it could not tell which. " +
					   "Configure the type in the 'configure' callback of the serializer's constructor, or - for a type Ceras only discovers at runtime - from OnConfigNewType, which is still called for a type met after the seal, or with [CerasSerializable], whose generated registration configures it up front.";

			return null;
		}

		internal void ThrowIfSealed()
		{
			var why = WhyConfigurationIsClosed();
			if (why != null)
				throw new ConfigurationSealedException(why);
		}

		/// <summary>
		/// The named form, for the member list (<see cref="ConfigList{T}"/>), so a refusal there points
		/// at the list rather than at the type's configuration in general.
		/// </summary>
		void ISealedConfiguration.ThrowIfSealed(string what)
		{
			var why = WhyConfigurationIsClosed();
			if (why != null)
				throw new ConfigurationSealedException(why + " (refused: " + what + ")");
		}

		/// <summary>
		/// Opens the one post-seal window this type has: the <see cref="CerasConfig.OnConfigNewType"/>
		/// callback, for the type the callback is handed. Called by <see cref="CerasConfig"/> around
		/// that one invocation and closed again straight after, on this instance - so it lets the
		/// application configure <em>this</em> type and nothing else: a type configured before the seal
		/// stays closed even from inside the callback, and a reference kept to this type stops working
		/// when the callback returns.
		/// </summary>
		internal void BeginNewTypeCallback() => _isNewTypeCallbackOpen = true;
		internal void EndNewTypeCallback() => _isNewTypeCallbackOpen = false;
	}


	/// <inheritdoc/>
	public class TypeConfig<T> : TypeConfig
	{
		internal TypeConfig(CerasConfig config, bool isStatic) : base(config, typeof(T), isStatic) { }

		/// <summary>
		/// Constructs <c>MemberConfig&lt;T&gt;</c> directly instead of going through the reflective
		/// <see cref="TypeConfig.CreateMemberConfig"/>. This single line is what makes a closed
		/// <c>MemberConfig&lt;T&gt;</c> exist under NativeAOT: ILC emits native code for a closed generic
		/// only when some code in the compilation names it, and this is the only place Ceras does.
		/// </summary>
		protected override MemberConfig CreateMemberConfig(MemberInfo member) => new MemberConfig<T>(this, member);

		#region Construction

		/// <summary>
		/// Don't construct an instance for this object, let the formatter handle it (must be used together with a custom formatter to work)
		/// </summary>
		public TypeConfig<T> ConstructByFormatter()
		{
			TypeConstruction = TypeConstruction.Null();
			return this;
		}

		/// <summary>
		/// Call a given static method to get an object
		/// </summary>
		public TypeConfig<T> ConstructBy(MethodInfo methodInfo)
		{
			TypeConstruction = new ConstructByMethod(methodInfo);
			return this;
		}

		/// <summary>
		/// Call a given instance-method on the given object instance to create a object
		/// </summary>
		public TypeConfig<T> ConstructBy(object instance, MethodInfo methodInfo)
		{
			TypeConstruction = new ConstructByMethod(instance, methodInfo);
			return this;
		}

		/// <summary>
		/// Use the given constructor to create a new object
		/// </summary>
		public TypeConfig<T> ConstructBy(ConstructorInfo constructorInfo)
		{
			if (constructorInfo.IsAbstract || constructorInfo.DeclaringType != Type)
				throw new InvalidOperationException("This constructor does not belong to the type " + Type.FullName);

			TypeConstruction = new SpecificConstructor(constructorInfo);
			return this;
		}

		/// <summary>
		/// Call the given delegate to produce an object (this is the only method that currently does not support arguments, support for that will be added later)
		/// </summary>
		public TypeConfig<T> ConstructByDelegate(Func<T> factory)
		{
			// Delegates get deconstructed into target+method automatically. A static method has no
			// target and a lambda that closes over nothing is such a method; the ConstructByMethod
			// constructor is what rejects the impossible combinations (a static method handed a
			// target, or an instance method handed none), so the possibly-null Target goes to it
			// unexamined rather than being silently replaced here.
			var instance = factory.Target;
			var method = factory.Method;

			TypeConstruction = new ConstructByMethod(instance, method);
			return this;
		}

		/// <summary>
		/// Use the given static method (inferred from the given expression). This works exactly the same as <see cref="ConstructBy(MethodInfo)"/> but since it takes an Expression selecting a method is much easier (no need to fiddle around with reflection manually). The given expression is not compiled or called in any way.
		/// </summary>
		public TypeConfig<T> ConstructBy(Expression<Func<T>> methodSelectExpression)
		{
			return ConstructBy(instance: null!, methodSelectExpression);
		}

		/// <summary>
		/// Use the given static method (inferred from the given expression). This works exactly the same as <see cref="ConstructBy(object, MethodInfo)"/> but since it takes an Expression selecting a method is much easier (no need to fiddle around with reflection manually). The given expression is not compiled or called in any way.
		/// </summary>
		public TypeConfig<T> ConstructBy(object instance, Expression<Func<T>> methodSelectExpression)
		{
			var body = methodSelectExpression.Body;

			if (body is NewExpression newExpression)
			{
				if (instance != null)
					throw new InvalidOperationException("You can't specify a constructor and an instance at the same time");

				var ctor = newExpression.Constructor;
				TypeConstruction = new SpecificConstructor(ctor);
			}
			else if (body is MethodCallExpression methodCall)
			{
				if (instance == null)
					TypeConstruction = new ConstructByMethod(methodCall.Method);
				else
					TypeConstruction = new ConstructByMethod(instance, methodCall.Method);
			}
			else
			{
				throw new InvalidOperationException("The given expression must be a 'method-call' or 'new-expression'");
			}

			return this;
		}

		/// <summary>
		/// Use this to tell Ceras how it is supposed to construct new objects when deserializing. By default it will use the parameterless constructor (doesn't matter if public or private)
		/// </summary>
		public TypeConfig<T> ConstructBy(TypeConstruction manualConstructConfig)
		{
			TypeConstruction = manualConstructConfig;
			return this;
		}

		/// <summary>
		/// Create an object without running any of its constructors
		/// </summary>
		public TypeConfig<T> ConstructByUninitialized()
		{
			TypeConstruction = new UninitializedObject();
			return this;
		}

		/// <summary>
		/// <para>
		/// Ceras can automatically map what members to put into the parameters of the constructor or factory method, but sometimes (when the names don't match up) you have to manually specify it
		/// </para>
		/// Only use this overload if you really have to (for example a parameter that will go into a private field)
		/// <para>
		/// The mapping is part of this type's configuration and is refused once that is closed, like
		/// every other change to it. It used to be accepted, and then applied or dropped depending on
		/// when the reflection formatter happened to be built: the only reader of the map is
		/// <c>TypeConstruction.VerifyParameterMapping</c>, reached from the fallback formatter, so a
		/// mapping written after the serializer had built that formatter was never consulted at all.
		/// </para>
		/// </summary>
		public TypeConfig<T> MapParameters(Dictionary<ParameterInfo, MemberInfo> mapping)
		{
			// Ahead of the two state checks below: those report a mistake in the call, this one reports
			// that it is too late, and once it is too late the rest is uninteresting.
			ThrowIfSealed();
			if (ParameterMap != null)
				throw new InvalidOperationException("ParameterMapping is already set");
			if (_typeConstruction == null)
				throw new InvalidOperationException("You must set a type construction method before mapping parameters");

			// Copy
			ParameterMap = mapping.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
			return this;
		}

		#endregion

		#region Formatter

		/// <summary>
		/// The formatter registered for <typeparamref name="T"/>, or null when none is. Assigning
		/// null removes a previously registered one.
		/// </summary>
		public new IFormatter<T>? CustomFormatter
		{
			// A cast, not 'as': a formatter registered for a different closed type is a
			// configuration error, and InvalidCastException is what has always reported it. Only the
			// genuine 'no formatter registered' case becomes null.
			get => (IFormatter<T>?)base.CustomFormatter;
			set => base.CustomFormatter = value;
		}

		/// <summary>
		/// Set a custom formatter that will get used to serialize this type
		/// </summary>
		public TypeConfig<T> SetFormatter(IFormatter<T> formatterInstance)
		{
			CustomFormatter = formatterInstance;
			return this;
		}

		#endregion

		#region Member Config

		public MemberConfig<T>? ConfigMember<TMember>(Expression<Func<T, TMember>> selectMemberExpression)
		{
			var memberInfo = ((MemberExpression)selectMemberExpression.Body).Member;
			var memberConfig = Members.FirstOrDefault(m => m.Member == memberInfo);
			// Cast, not 'as', for the same reason as CustomFormatter: a member config of another
			// closed type is a configuration error. Null here only means 'no such member'.
			return (MemberConfig<T>?)memberConfig;
		}

		public MemberConfig<T>? ConfigField(string fieldName)
		{
			var memberConfig = Members.FirstOrDefault(m => m.Member is FieldInfo && m.Member.Name == fieldName);
			return (MemberConfig<T>?)memberConfig;
		}

		public MemberConfig<T>? ConfigProperty(string propName)
		{
			var memberConfig = Members.FirstOrDefault(m => m.Member is PropertyInfo && m.Member.Name == propName);
			return (MemberConfig<T>?)memberConfig;
		}

		#endregion
	}


	public abstract class MemberConfig
	{
		public TypeConfig TypeConfig { get; }
		public Type DeclaringType => TypeConfig.Type;
		public MemberInfo Member { get; }
		public Type MemberType => Member is FieldInfo f ? f.FieldType : ((PropertyInfo)Member).PropertyType;


		/// <summary>
		/// The name a previous save file recorded for this member, when one was configured. Null is
		/// the ordinary state - a member has no old spelling until something says it does.
		/// </summary>
		string? _persistentNameOverride;

		/// <summary>
		/// The name this member is written under: the configured override, else the auto-property's
		/// name, else the member's C# name. Never null.
		/// <para>
		/// Assigning null clears the override, so the member falls back to the derived name above. That
		/// is the same state as never having assigned one, and it is what a null <c>PreviousName</c>
		/// resolves to (see <c>TypeConfigDefaults.ApplyMemberAttributes</c>).
		/// </para>
		/// </summary>
		[AllowNull]
		public string PersistentName
		{
			get
			{
				if (_persistentNameOverride != null) return _persistentNameOverride;
				if (AssociatedProperty != null) return AssociatedProperty.Name;
				return Member.Name;
			}
			set
			{
				TypeConfig.ThrowIfSealed();
				_persistentNameOverride = value;
			}
		}

		/// <summary>
		/// True for everything the compiler automatically generates: hidden async-state-machines, automatic enumerator implementations, cached dynamic method dispatchers, ...
		/// <para>In short, all things you never want to serialize anyway.</para>
		/// <para>Ceras ignores compiler generated members by default.</para>
		/// </summary>
		public bool IsCompilerGenerated { get; }
		/// <summary>
		/// True if it's a field and it is declared as 'readonly'
		/// </summary>
		public bool IsReadonlyField { get; }
		/// <summary>
		/// A property that does not have a set-method is called a "computed property", which is different from properties where the setter is just hidden in a base type, or private, ...
		/// <para>Example 1: int Time => Environment.TickCount;</para>
		/// <para>Example 2: string Name { get; }</para>
		/// </summary>
		public bool IsGetterOnly { get; }

		internal bool HasNonSerializedAttribute;

		/// <summary>
		/// The auto-property a compiler-generated backing field belongs to, or null when this member
		/// is not one (a real field or property) or its declaring type has no such property.
		/// </summary>
		internal readonly PropertyInfo? AssociatedProperty;

		/// <summary>
		/// Controls when Ceras will write this member to the class/struct after deserialization.
		/// By default (<see cref="WriteBackOrder"/> == 0) writes are optimized for performance.
		/// But sometimes you might have a property that needs to be written last (maybe after some fields are written already or something...).
		///
		/// Members are set in ascending order.
		/// <para>Default: Value from [DataMember.Order] or 0 if the member has no attribute</para>
		/// </summary>
		int _writeBackOrder;
		public int WriteBackOrder { 
			get => _writeBackOrder; 
			set { TypeConfig.ThrowIfSealed(); _writeBackOrder = value; } 
		}


		string _explicitInclusionReason = "Error: no inclusion/exclusion override is set for this member";

		/// <summary>
		/// Tells you why (or why not!) this member got included in the serialization.
		/// </summary>
		public string IncludeExcludeReason
		{
			get
			{
				var r = ComputeFinalInclusionResult(Member, true);
				return (r.IsIncluded ? "Included: " : "Excluded: ") + r.Reason;
			}
		}


		SerializationOverride _serializationOverride = SerializationOverride.NoOverride;
		/// <summary>
		/// Contains the serialization override for this member. It is initially set from any member attributes (like Include/Exclude) or the [MemberConfig] attribute.
		/// If none of those attributes exist the override is unspecified (in which case the DefaultTargets setting in the serializer config is used)
		/// </summary>
		public SerializationOverride SerializationOverride
		{
			set => SetIncludeWithReason(value, "User has explicitly set member.SerializationOverride");
			get => _serializationOverride;
		}



		protected MemberConfig(TypeConfig typeConfig, MemberInfo member)
		{
			TypeConfig = typeConfig;
			Member = member;

			if (member is FieldInfo fi && fi.Name.EndsWith("k__BackingField"))
			{
				var match = System.Text.RegularExpressions.Regex.Match(fi.Name, @"^<(.*)>k__BackingField$");
				if (match.Success)
				{
					string propName = match.Groups[1].Value;
					// A dynamic member has no declaring type, and then no auto-property to find either.
				AssociatedProperty = fi.DeclaringType?.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
				}
			}

			IsCompilerGenerated = member.GetCustomAttribute<CompilerGeneratedAttribute>() != null;
			if (AssociatedProperty != null)
				IsCompilerGenerated = false;

			IsReadonlyField = member is FieldInfo f && f.IsInitOnly;
			IsGetterOnly = member is PropertyInfo p && p.GetSetMethod(true) == null;
		}



		internal void ExcludeWithReason(string reason) => SetIncludeWithReason(SerializationOverride.ForceSkip, reason);

		internal void SetIncludeWithReason(SerializationOverride serializationOverride, string reason)
		{
			TypeConfig.ThrowIfSealed();
			if (serializationOverride == SerializationOverride.NoOverride)
				throw new InvalidOperationException("Explicitly setting 'NoOverride', this must be a bug, please report it on GitHub!");
			if (string.IsNullOrWhiteSpace(reason))
				throw new InvalidOperationException("Missing reason in " + nameof(SetIncludeWithReason) + ", this must be a bug, please report it on GitHub!");

			_serializationOverride = serializationOverride;
			_explicitInclusionReason = reason;
		}


		/// <summary>
		/// True when the user has explicitly taken this member out of serialization
		/// (<c>[Exclude]</c>, <c>Exclude()</c>, <see cref="SerializationOverride.ForceSkip"/>, or a
		/// callback that skipped it). This is the one way a member whose type cannot be serialized
		/// may still be absent from the payload - see <see cref="TypeConfig.VerifyNoBannedMemberShapes"/>.
		/// <para>
		/// Deliberately independent of the ban itself: a banned type would also make
		/// <see cref="ComputeFinalInclusionFast"/> return false, and that must not count as the
		/// user's decision.
		/// </para>
		/// </summary>
		internal bool IsExcludedByUser
			=> _serializationOverride == SerializationOverride.ForceSkip
			   || (HasNonSerializedAttribute && TypeConfig.Config.RespectNonSerializedAttribute);

		/// <summary>
		/// How to refer to this member in a message: the property name for an auto-property's
		/// backing field, the field name otherwise.
		/// </summary>
		internal string DisplayName => AssociatedProperty?.Name ?? Member.Name;

		/// <summary>
		/// Determine whether or not this member is included when serializing/deserializing.
		/// </summary>
		/// <returns>true when the member will be serialized/deserialized</returns>
		public InclusionExclusionResult ComputeFinalInclusion() => ComputeFinalInclusionResult(Member, true);

		internal bool ComputeFinalInclusionFast() => ComputeFinalInclusionResult(Member, false).IsIncluded;


		InclusionExclusionResult ComputeFinalInclusionResult(MemberInfo memberInfo, bool needReason)
		{
			// [Include] / [Exclude]
			if (_serializationOverride != SerializationOverride.NoOverride)
				return new InclusionExclusionResult(_serializationOverride == SerializationOverride.ForceInclude, _explicitInclusionReason);

			// Banned types
			if (BannedTypes.IsBannedShape(MemberType, out var banReason))
				return new InclusionExclusionResult(false, $"The type '{MemberType.FriendlyName(true)}' cannot be serialized at all ({banReason}), so the member is left out. That is an error unless the member is marked [Exclude]: see TypeConfig.VerifyNoBannedMemberShapes.");

			// A type the user excluded on purpose: its members are simply not part of the schema.
			if (BannedTypes.IsExcludedType(MemberType))
				return new InclusionExclusionResult(false, $"The type '{MemberType.FriendlyName(true)}' is marked with [Exclude], so it is not serialized and neither are its members.");

			// [NonSerialized]
			if (HasNonSerializedAttribute && TypeConfig.Config.RespectNonSerializedAttribute)
				return new InclusionExclusionResult(false, $"Member has [NonSerializedAttribute] and 'RespectNonSerializedAttribute' is set in the config.");

			// The reason is only built when the caller asked for one: every branch above builds its
			// string unconditionally because it is the only way to reach the verdict, but this last
			// branch is the default-included case, whose reason would be a constant string built on a
			// hot path that never displays it.
			return new InclusionExclusionResult(true, needReason ? "Member is a field and included by default." : null);
		}

		public override string ToString() => $"{TypeConfig.Type.FriendlyName()}.{Member.Name} ({(ComputeFinalInclusionFast() ? "Included" : "Excluded")})";
	}

	/// <summary>
	/// The verdict for one member: whether it is serialized, and why.
	/// </summary>
	public struct InclusionExclusionResult
	{
		public readonly bool IsIncluded;

		/// <summary>
		/// Why the member is included or excluded, or null when the caller did not ask for it.
		/// <para>
		/// Computing a reason allocates the string that explains it, and the verdict itself is
		/// consulted on a hot path (<c>ComputeFinalInclusionFast</c>), which does not need one -
		/// so the two are requested separately and only <see cref="MemberConfig.ComputeFinalInclusion"/>
		/// pays for the explanation. Use <see cref="MemberConfig.IncludeExcludeReason"/> for a reason
		/// that is never null.
		/// </para>
		/// </summary>
		public readonly string? Reason;

		public InclusionExclusionResult(bool isIncluded, string? reason)
		{
			IsIncluded = isIncluded;
			Reason = reason;
		}
	}

	public class MemberConfig<TDeclaring> : MemberConfig
	{
		public new TypeConfig<TDeclaring> TypeConfig => (TypeConfig<TDeclaring>)base.TypeConfig;

		public MemberConfig(TypeConfig typeConfig, MemberInfo member) : base(typeConfig, member) { }


		public TypeConfig<TDeclaring> Include()
		{
			SetIncludeWithReason(SerializationOverride.ForceInclude, "User called Include()");
			return TypeConfig;
		}

		public TypeConfig<TDeclaring> Exclude() => Exclude("User called Exclude()");

		public TypeConfig<TDeclaring> Exclude(string customReason)
		{
			SetIncludeWithReason(SerializationOverride.ForceSkip, customReason);
			return TypeConfig;
		}
	}

}
