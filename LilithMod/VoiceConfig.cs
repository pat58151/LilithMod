using System.IO;

namespace LilithMod
{
    /// <summary>
    /// Voice configuration values, read once from the BepInEx config file at startup.
    /// All fields are populated by <see cref="LilithModPlugin"/> during Load().
    /// </summary>
    public static class VoiceConfig
    {
        public static bool Enabled;
        public static string Endpoint;
        public static string RefAudioPath;
        public static string PromptText;
        public static string TextLang;
        public static string PromptLang;
        public static int TimeoutSeconds;
        public static int WarmUpTimeoutSeconds;
        public static float FragmentInterval;
        public static string TextSplitMethod;
        public static string SubtitleLang;
        public static string CacheIdentity;
        public static string GptWeights;
        public static string SovitsWeights;
        public static string WarmUpText;

        /// <summary>
        /// Resolves a relative path (from the mod root) to an absolute path on disk.
        /// Falls back to the raw configured value when the mod directory is unknown.
        /// </summary>
        public static string ResolveRefAudioPath(string modDirectory)
        {
            if (string.IsNullOrEmpty(RefAudioPath))
                return null;

            if (Path.IsPathRooted(RefAudioPath))
                return RefAudioPath;

            if (!string.IsNullOrEmpty(modDirectory))
                return Path.GetFullPath(Path.Combine(modDirectory, RefAudioPath));

            return RefAudioPath;
        }
    }
}
