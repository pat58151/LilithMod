namespace LilithMod
{
    /// <summary>
    /// One sentence of a reply: synthesized speech and the text shown with it.
    /// </summary>
    public class Utterance
    {
        public string SpokenText { get; set; }
        public string ShownText { get; set; }
        public string Language { get; set; }
        public bool SuppressSubtitle { get; set; }
        public bool EndOfReply { get; set; }
        internal bool CompletionOnly { get; set; }
        internal NativeDialogueCue NativeDialogue { get; set; }
    }
}
