using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// UI state that the host persists, and that asks to be persisted by posting a signal the moment
    /// it is dirtied. <see cref="PlannerState"/> and <see cref="SessionTabState"/> had a byte-identical
    /// copy of this each, differing only in which signal they posted -- and had already drifted apart
    /// in what they let a caller DO with the flag, which is what this exists to stop.
    /// <para>
    /// Derive from <see cref="PersistableState{TSignal}"/>, which is where the signal is named. This
    /// non-generic base exists so the states remain a single type a caller can hold, constrain on or
    /// enumerate, which a bare generic would have cost.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The setter is internal on purpose:</b> mutations belong in the action helpers
    /// (<see cref="PlannerActions"/>, the session tab), not in arbitrary callers, so that every edit
    /// goes through one place that knows what it dirtied. That is exactly why
    /// <see cref="MarkDirty"/> and <see cref="MarkSaved"/> are public: an out-of-assembly host (the
    /// browser build's localStorage store) has no other way to take part, and the half of this pair
    /// that was missing on one of the two states was a host that could dirty the flag and never clear
    /// it.
    /// </para>
    /// <para>
    /// <b>Posting on every set-true, not on the false-to-true edge</b>, is deliberate and is the
    /// behaviour both copies had. A host that never clears the flag still gets a save signal for each
    /// subsequent edit; on the edge it would get one for the first edit and silence after that.
    /// </para>
    /// </remarks>
    public abstract class PersistableState
    {
        /// <summary>Signal bus the save request is posted to. Set by the host during initialization.</summary>
        public SignalBus? Bus { get; set; }

        /// <summary>Whether this state has changes the host has not persisted yet.</summary>
        public bool IsDirty
        {
            get => _isDirty;
            internal set
            {
                _isDirty = value;
                if (value)
                {
                    PostSaveSignal();
                }
            }
        }
        private bool _isDirty;

        /// <summary>Asks the host to persist this state. Implemented once, in the generic subclass.</summary>
        private protected abstract void PostSaveSignal();

        /// <summary>Marks the state dirty, which asks the host to save it.</summary>
        public void MarkDirty() => IsDirty = true;

        /// <summary>
        /// Clears the dirty flag once the host has persisted the state, WITHOUT posting -- a save
        /// that re-requested a save would never settle. Hosts outside this assembly call it from their
        /// own subscriber to the save signal, mirroring what the desktop handler does.
        /// </summary>
        public void MarkSaved() => _isDirty = false;
    }

    /// <summary>
    /// A <see cref="PersistableState"/> that names its save signal as a type argument, so a derived
    /// state declares no members at all for this: <c>class PlannerState :
    /// PersistableState&lt;SavePlannerSessionSignal&gt;</c> is the whole declaration.
    /// </summary>
    /// <typeparam name="TSignal">
    /// The signal asking the host to persist this state. Constructed per post rather than cached,
    /// which is what the two hand-written copies did: <see cref="SignalBus"/> queues into a
    /// <c>ConcurrentQueue&lt;object&gt;</c>, so one of these value-type markers boxes on the way in
    /// either way, and reusing a cached box would be a change in behaviour bought for nothing.
    /// </typeparam>
    public abstract class PersistableState<TSignal> : PersistableState
        where TSignal : notnull, new()
    {
        /// <summary>
        /// Posts a <typeparamref name="TSignal"/>. Sealed: the signal is chosen by the type argument,
        /// and a state that overrode this could post something its own type parameter denies.
        /// </summary>
        private protected sealed override void PostSaveSignal() => Bus?.Post(new TSignal());
    }
}
