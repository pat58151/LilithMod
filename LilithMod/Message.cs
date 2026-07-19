namespace LilithMod
{
    /// <summary>
    /// A plain C# class (non-IL2CPP) representing a conversation turn.
    /// Used only on the managed side for LLM chat history.
    /// </summary>
    public class Message
    {
        public string Role { get; set; }     // "system", "user", "assistant"
        public string Content { get; set; }
    }
}
