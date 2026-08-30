using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The dirty-flag contract <see cref="PlannerState"/> and <see cref="SessionTabState"/> share via
    /// <see cref="PersistableState"/>. Both used to carry their own copy of it, and had drifted in what
    /// they let a caller do with the flag.
    /// </summary>
    public class PersistableStateTests
    {
        private static (T State, SignalBus Bus) Wire<T>(T state) where T : PersistableState
        {
            var bus = new SignalBus();
            state.Bus = bus;
            return (state, bus);
        }

        /// <summary>
        /// The base holds the signal as <see cref="object"/>, so the thing that could silently break is
        /// DISPATCH: a concrete signal posted through a base-typed reference must still reach a handler
        /// subscribed to its own type. It does because <see cref="SignalBus.ProcessPending"/> keys on
        /// the runtime type -- but nothing in the type system says so, hence this test.
        /// </summary>
        [Fact]
        public void EachStatePostsItsOwnConcreteSignal()
        {
            var (planner, plannerBus) = Wire(new PlannerState());
            var (session, sessionBus) = Wire(new SessionTabState());

            var plannerSaves = 0;
            var sessionSaves = 0;
            plannerBus.Subscribe<SavePlannerSessionSignal>(_ => plannerSaves++);
            sessionBus.Subscribe<SaveSessionConfigSignal>(_ => sessionSaves++);

            planner.MarkDirty();
            session.MarkDirty();
            plannerBus.ProcessPending();
            sessionBus.ProcessPending();

            plannerSaves.ShouldBe(1, "the planner's save signal must reach a handler subscribed to its own type");
            sessionSaves.ShouldBe(1, "and so must the session tab's");
        }

        [Fact]
        public void MarkDirtySetsTheFlag()
        {
            var state = new PlannerState();
            state.IsDirty.ShouldBeFalse("a fresh state has nothing to save");

            state.MarkDirty();

            state.IsDirty.ShouldBeTrue();
        }

        /// <summary>
        /// The half that was MISSING on <see cref="SessionTabState"/>: with an internal setter and no
        /// public clear, a host outside this assembly could dirty the flag and never get it back to
        /// false.
        /// </summary>
        [Fact]
        public void MarkSavedClearsTheFlagWithoutAskingForAnotherSave()
        {
            var (state, bus) = Wire(new SessionTabState());
            var saves = 0;
            bus.Subscribe<SaveSessionConfigSignal>(_ => saves++);

            state.MarkDirty();
            bus.ProcessPending();
            saves.ShouldBe(1);

            state.MarkSaved();

            state.IsDirty.ShouldBeFalse();
            bus.ProcessPending();
            saves.ShouldBe(1, "a save that re-requested a save would never settle");
        }

        /// <summary>
        /// Posting on every set-true rather than on the false-to-true edge is the behaviour both copies
        /// had, and it is what keeps a host that never clears the flag from going silent after its
        /// first edit.
        /// </summary>
        [Fact]
        public void EveryDirtyingAsksForASaveEvenWithoutAnInterveningClear()
        {
            var (state, bus) = Wire(new PlannerState());
            var saves = 0;
            bus.Subscribe<SavePlannerSessionSignal>(_ => saves++);

            state.MarkDirty();
            state.MarkDirty();
            bus.ProcessPending();

            saves.ShouldBe(2);
        }

        [Fact]
        public void AStateWithNoBusDoesNotThrow()
        {
            var state = new SessionTabState();

            Should.NotThrow(() => state.MarkDirty());

            state.IsDirty.ShouldBeTrue("the flag is the state's own, and does not depend on a host listening");
        }
    }
}
