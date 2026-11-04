using System;
using System.Collections.Generic;

namespace Contracts.Core.Sim
{
    /// <summary>
    /// Work spread over frames, in order: a step at a time (one a frame), or the rest at once (before
    /// a save, or for tests). A step may queue more steps; they run after those already queued. A
    /// step that throws is dropped, not run again, and the work goes on with the next.
    /// </summary>
    public sealed class StagedWork
    {
        private readonly Queue<Action> steps = new Queue<Action>();

        public bool Pending => steps.Count > 0;

        /// <summary>The steps run so far, those that threw included.</summary>
        public int Run { get; private set; }

        public void Then(Action step) => steps.Enqueue(step ?? throw new ArgumentNullException(nameof(step)));

        /// <summary>Runs the next step; false if none was left. A step's exception goes to <paramref name="failed"/>, or to the caller without one.</summary>
        public bool Step(Action<Exception> failed = null)
        {
            if (steps.Count == 0)
                return false;
            var step = steps.Dequeue();
            Run++;
            try
            {
                step();
            }
            catch (Exception e) when (failed != null)
            {
                failed(e);
            }
            return true;
        }

        /// <summary>Runs every step left, those queued along the way included; returns how many ran.</summary>
        public int Finish(Action<Exception> failed = null)
        {
            var n = 0;
            while (Step(failed))
                n++;
            return n;
        }
    }
}
