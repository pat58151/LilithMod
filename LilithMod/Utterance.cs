namespace LilithMod
{
    /// <summary>
    /// One sentence of a reply: the Japanese Lilith speaks aloud, and the English
    /// shown in the bubble while that audio plays.
    /// </summary>
    public class Utterance
    {
        public string JaText { get; set; }
        public string EnText { get; set; }
        public string Language { get; set; }
        public bool SuppressSubtitle { get; set; }
        public bool EndOfReply { get; set; }
        internal bool CompletionOnly { get; set; }
        internal NativeDialogueCue NativeDialogue { get; set; }
    }
}
