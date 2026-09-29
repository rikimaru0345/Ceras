using System.Collections.Generic;
using System.Reflection;

namespace Ceras.Format
{
    /// <summary>
    /// The values a type's <c>[CerasConstructor]</c> is invoked with, filled from the payload while it
    /// is read, plus every member the payload carried, which is assigned to the instance afterwards.
    /// <para>
    /// The plan, the buffer and the pending assignments travel as one object because they are one
    /// decision: either the instance is constructed out of the payload or it is not. A reader that
    /// could be handed the plan without the buffer it fills would have to re-decide that per member,
    /// and a null-forgiving operator would stand in for the answer.
    /// </para>
    /// <para>
    /// It sits next to <see cref="CtorArgumentPlan"/> rather than inside the reader that fills it,
    /// because the routing is a property of the plan — which member feeds which parameter, and which
    /// parameters are transient — and not of any one reader.
    /// </para>
    /// <para>
    /// <b>Every member the payload carried is assigned after the constructor has run, the ones a
    /// parameter was fed from included.</b> That is the generated reader's order: it reads every member
    /// into a local, invokes the constructor, and then writes <em>all</em> of the locals back — "if it
    /// was a constructor parameter, it's already set (for classes). But for consistency and to handle
    /// readonly fields… we write it anyway" (<c>RobustCodeEmitter</c>). So a <c>[CerasConstructor]</c> is
    /// a <em>construction</em> input and not an assignment: a constructor that normalizes, clamps,
    /// drops or reorders what it is handed cannot change the result, on either reader.
    /// </para>
    /// <para>
    /// This used to hold back the members a parameter fed, on the reading that a member the
    /// constructor was handed is the constructor's own business. That produced a different object from
    /// one save depending on which reader read it, which is a parity defect rather than a decision, and
    /// it was worst for the members a value comparison cannot check: a constructor parameter that is a
    /// <em>reference</em> and drops its argument left the graph's shared instance and cycles broken
    /// while the load reported success.
    /// </para>
    /// <para>
    /// Two boundaries are unchanged and are not this class's to move. A member the payload did not carry
    /// is not assigned at all — it keeps whatever the construction gave it, which for a
    /// field initializer is the value the constructor wrote and for a member the constructor never
    /// touched is <c>default</c>. And a <b>reused</b> instance never gets here: the engine resolves a
    /// constructor plan only for a fresh one, so a <c>Load(ref existing)</c> neither runs a constructor
    /// nor routes through this class.
    /// </para>
    /// </summary>
    internal sealed class CtorArgumentValues
    {
        public CtorArgumentPlan Plan { get; }

        // One entry per parameter. Null until the member that feeds it has been read, and null after
        // that too when the file legitimately stored a null for a parameter that has one.
        readonly object?[] _values;

        // Every member the payload carried, applied after the constructor has run so a constructor that
        // rewrites a value cannot change the result — the generated reader's order, which writes back
        // every local it read, the ones a parameter was fed from included. Created on the first member,
        // which is every one of them: this class is only built for a type that has a parameterized
        // [CerasConstructor], so there is no "every member fed a parameter and none is left" case left
        // to keep the list away from.
        List<(FieldInfo Field, object? Value)>? _pendingAssignments;

        public CtorArgumentValues(CtorArgumentPlan plan)
        {
            Plan = plan;
            _values = new object?[plan.Parameters.Length];
        }

        /// <summary>
        /// Routes one member's value: to the parameter it feeds, and — for every member, that one
        /// included — to the post-construction assignments. A value that no parameter can take is
        /// refused here, by <see cref="CtorArgumentPlan.AcceptMemberValue"/>, rather than at the
        /// reflection call.
        /// </summary>
        public void Set(FieldInfo field, OldFieldSchema storedField, object? value)
        {
            int slot = Plan.SlotForField(field);
            if (slot >= 0)
                _values[slot] = Plan.AcceptMemberValue(slot, value, storedField);

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

        /// <summary>
        /// Assigns every member the payload carried, the ones the constructor was fed from included, in
        /// the order the file recorded them.
        /// </summary>
        public void ApplyPendingAssignments(object target)
        {
            if (_pendingAssignments == null) return;

            foreach (var (field, value) in _pendingAssignments)
                field.SetValue(target, value);
        }
    }
}
