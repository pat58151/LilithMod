namespace LilithMod
{
    /// <summary>
    /// Thread-safe result carrier for the concurrent queue.
    /// Holds only managed types – never used with Il2Cpp objects.
    /// </summary>
    public sealed class ChatResult
    {
        public bool Ok { get; set; }
        /// <summary>Valid reply when Ok == true.</summary>
        public string Text { get; set; }
        /// <summary>Error description when Ok == false.</summary>
        public string Error { get; set; }
    }
}
