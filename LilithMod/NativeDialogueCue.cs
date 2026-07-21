using System.Threading;

namespace LilithMod
{
    internal sealed class NativeDialogueCue
    {
        private readonly ManualResetEventSlim _displayed = new ManualResetEventSlim(false);

        internal NativeDialogueCue(DialogueBubbleUI bubble, DialogueNode node, long key)
        {
            Bubble = bubble;
            Node = node;
            Key = key;
        }

        internal DialogueBubbleUI Bubble { get; }
        internal DialogueNode Node { get; }
        internal long Key { get; }

        /// <summary>
        /// A cancelled cue is neither re-shown nor voiced: the coordinator still
        /// clears its pending entry, and the voice thread skips its audio. Set when
        /// a newer line superseded this one, or a chat reply abandoned it.
        /// </summary>
        internal bool Cancelled => _cancelled;
        private volatile bool _cancelled;

        internal void Cancel() => _cancelled = true;

        internal void MarkDisplayed() => _displayed.Set();
        internal void WaitUntilDisplayed(CancellationToken token) => _displayed.Wait(token);
    }
}
