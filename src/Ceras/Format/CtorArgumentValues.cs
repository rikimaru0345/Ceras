using System.Collections.Generic;
using System.Reflection;

namespace Ceras.Format
{
    /// <summary>
    /// The values a type's <c>[CerasConstructor]</c> is invoked with, filled from the payload while it
    /// is read, plus the members the constructor does not take, which are assigned to the instance
    /// afterwards.
    /// <para>
    /// The plan, the buffer and the pending assignments travel as one object because they are one
    /// decision: either the instance is constructed out of the payload or it is not. A reader that
    /// could be handed the plan without the buffer it fills would have to re-decide that per member,
    /// and a null-forgiving operator would stand in for the answer.
    /// </para>
    /// <para>
    /// It sits next to <see cref="CtorArgumentPlan"/> rather than inside the reader that fills it,
    /// because the routing is a property of the plan — which member feeds which parameter, and which
    /// parameters are transient — and not of any one reader. It holds the members no parameter feeds
    /// and applies them after the constructor has run, so for those members a constructor that
    /// rewrites a value cannot change the result. That is the whole reason they are held rather than
    /// written straight through.
    /// </para>
    /// <para>
    /// <b>What the constructor <em>is</em> handed is a different matter, and this paragraph is about
    /// the streaming reader's current behaviour rather than a rule worth keeping.</b> A member a
    /// parameter fed is the constructor's own business: this class hands it over and does not write
    /// it back. The generated reader does write it back — it writes <em>every</em> member from its
    /// local after the constructor returns, with the comment "if it was a constructor parameter, it's
    /// already set (for classes). But for consistency and to handle readonly fields… we write it
    /// anyway" (<c>RobustCodeEmitter</c>). So a constructor that rewrites a value it was handed
    /// produces a different object on the two readers from the same save, and a save of one round-trips
    /// differently depending on which reader read it.
    /// </para>
    /// <para>
    /// That is a parity defect between the two readers, not a decision recorded here, and it is not
    /// resolved by this class. It is spelled out so that resolving it later is not mistaken for
    /// breaking something: nothing here depends on the members a parameter fed being left alone, and
    /// nothing here should be read as saying the generated reader's behaviour is the one to match in
    /// preference to a fix here.
    /// </para>
    /// </summary>
    internal sealed class CtorArgumentValues
    {
        public CtorArgumentPlan Plan { get; }

        // One entry per parameter. Null until the member that feeds it has been read, and null after
        // that too when the file legitimately stored a null for a parameter that has one.
        readonly object?[] _values;

        // The members no parameter feeds, applied after the constructor has run so a constructor that
        // rewrites one of *these* cannot change the result. Created on the first one, because the
        // usual case is that every member feeds a parameter and none is left. A member a parameter
        // does feed is the constructor's own business and is not written back here — see the class
        // comment for how that differs from the generated reader.
        List<(FieldInfo Field, object? Value)>? _pendingAssignments;

        public CtorArgumentValues(CtorArgumentPlan plan)
        {
            Plan = plan;
            _values = new object?[plan.Parameters.Length];
        }

        /// <summary>
        /// Routes one member's value: to the parameter it feeds, or to the post-construction
        /// assignments. A value that no parameter can take is refused here, by
        /// <see cref="CtorArgumentPlan.AcceptMemberValue"/>, rather than at the reflection call.
        /// </summary>
        public void Set(FieldInfo field, OldFieldSchema storedField, object? value)
        {
            int slot = Plan.SlotForField(field);
            if (slot >= 0)
            {
                _values[slot] = Plan.AcceptMemberValue(slot, value, storedField);
                return;
            }

            (_pendingAssignments ??= new List<(FieldInfo Field, object? Value)>()).Add((field, value));
        }

        /// <summary>
        /// The arguments to invoke the constructor with: a parameter no serialized member feeds is
        /// transient construction input and gets its own declared default, exactly like the
        /// generated reader passes <c>default(T)</c>.
        /// </summary>
        public object?[] Arguments()
        {
            for (int i = 0; i < _values.Length; i++)
                if (!Plan.IsMapped(i)) _values[i] = Plan.DefaultFor(i);

            return _values;
        }

        /// <summary>Assigns the members the constructor did not take.</summary>
        public void ApplyPendingAssignments(object target)
        {
            if (_pendingAssignments == null) return;

            foreach (var (field, value) in _pendingAssignments)
                field.SetValue(target, value);
        }
    }
}
