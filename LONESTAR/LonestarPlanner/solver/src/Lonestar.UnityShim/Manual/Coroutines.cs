using System;

namespace UnityEngine
{
    public partial class YieldInstruction
    {
    }

    public sealed partial class Coroutine : YieldInstruction
    {
        internal CoroutineState state;
    }

    public sealed partial class WaitForSeconds : YieldInstruction
    {
        internal readonly float seconds;

        public WaitForSeconds(float seconds)
        {
            this.seconds = seconds;
        }
    }

    public sealed partial class WaitForEndOfFrame : YieldInstruction
    {
        public WaitForEndOfFrame()
        {
        }
    }

    public abstract partial class CustomYieldInstruction
    {
        public abstract bool keepWaiting { get; }
    }

    public sealed partial class WaitUntil : CustomYieldInstruction
    {
        private readonly Func<bool> predicate;

        public WaitUntil(Func<bool> predicate)
        {
            this.predicate = predicate;
        }

        public override bool keepWaiting => !predicate();
    }

    public sealed partial class WaitWhile : CustomYieldInstruction
    {
        private readonly Func<bool> predicate;

        public WaitWhile(Func<bool> predicate)
        {
            this.predicate = predicate;
        }

        public override bool keepWaiting => predicate();
    }
}
