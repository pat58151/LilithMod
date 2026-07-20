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

        internal void MarkDisplayed() => _displayed.Set();
        internal void WaitUntilDisplayed(CancellationToken token) => _displayed.Wait(token);
    }
}
