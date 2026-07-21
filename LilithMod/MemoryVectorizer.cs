using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LilithMod
{
    /// <summary>Local multilingual matching with word, character, and topic features.</summary>
    internal static class MemoryVectorizer
    {
        private const int Dimensions = 512;
        private static readonly Dictionary<string, string> TokenConcepts =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<KeyValuePair<string, string>> PhraseConcepts =
            new List<KeyValuePair<string, string>>();

        static MemoryVectorizer()
        {
            AddConcept("work", "work|job|career|office|workplace|employment|coworker|colleague|boss|仕事|職場|会社|同僚|上司|工作|职场|公司|上班|同事|老板");
            AddConcept("sadness", "sad|sadness|upset|depressed|depression|melancholy|悲しい|悲しみ|落ち込む|憂鬱|難過|难过|傷心|伤心|抑鬱|抑郁|低落");
            AddConcept("anger", "angry|anger|furious|annoyed|irritated|mad|怒り|怒る|腹が立つ|イライラ|生氣|生气|憤怒|愤怒|惱火|恼火");
            AddConcept("anxiety", "anxious|anxiety|nervous|worried|worry|stressed|stress|不安|緊張|心配|ストレス|焦慮|焦虑|緊張|紧张|擔心|担心|壓力|压力");
            AddConcept("happiness", "happy|happiness|excited|pleased|glad|joy|joyful|proud|嬉しい|楽しい|幸せ|興奮|誇り|開心|开心|高興|高兴|幸福|興奮|兴奋|自豪");
            AddConcept("fatigue", "tired|exhausted|sleepy|fatigue|fatigued|burnout|疲れ|疲れる|眠い|疲労|累|疲憊|疲惫|困|倦");
            AddConcept("sleep", "sleep|sleeping|insomnia|nightmare|dream|眠る|睡眠|不眠|悪夢|夢|睡覺|睡觉|睡眠|失眠|噩夢|噩梦|夢|梦");
            AddConcept("family", "family|mother|mom|father|dad|parent|parents|sibling|brother|sister|家族|母|お母さん|父|お父さん|兄|弟|姉|妹|家人|媽媽|妈妈|母親|母亲|爸爸|父親|父亲|哥哥|弟弟|姐姐|妹妹");
            AddConcept("relationship", "partner|boyfriend|girlfriend|spouse|husband|wife|dating|relationship|恋人|彼氏|彼女|夫|妻|伴侶|伴侣|男朋友|女朋友|丈夫|妻子|戀愛|恋爱");
            AddConcept("friendship", "friend|friends|friendship|buddy|友達|親友|朋友|友情");
            AddConcept("loneliness", "lonely|alone|isolated|isolation|寂しい|孤独|一人|寂寞|孤獨|孤独|一個人|一个人");
            AddConcept("education", "school|university|college|study|studying|exam|class|学校|大学|勉強|試験|授業|學校|学校|大學|大学|學習|学习|考試|考试|課程|课程");
            AddConcept("gaming", "game|games|gaming|steam|ゲーム|遊ぶ|遊戲|游戏|玩");
            AddConcept("health", "health|healthy|sick|illness|pain|doctor|hospital|medicine|健康|病気|痛み|医者|病院|薬|生病|疾病|疼痛|醫生|医生|醫院|医院|藥|药");
            AddConcept("money", "money|financial|finance|salary|rent|bill|bills|debt|お金|給料|家賃|請求|借金|錢|钱|工資|工资|房租|賬單|账单|債務|债务");
            AddConcept("grief", "grief|grieving|bereavement|loss|mourning|喪失|死別|悲嘆|哀悼|失去|喪親|丧亲");
        }

        public static float Similarity(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return 0f;
            float[] a = Embed(first);
            float[] b = Embed(second);
            double dot = 0;
            double aa = 0;
            double bb = 0;
            for (int i = 0; i < Dimensions; i++)
            {
                dot += a[i] * b[i];
                aa += a[i] * a[i];
                bb += b[i] * b[i];
            }
            return aa == 0 || bb == 0 ? 0f : (float)(dot / Math.Sqrt(aa * bb));
        }

        private static float[] Embed(string value)
        {
            var vector = new float[Dimensions];
            string normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            var concepts = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(normalized, @"[\p{L}\p{N}]+"))
            {
                string token = match.Value;
                Add(vector, "w:" + token, 2f);
                if (TokenConcepts.TryGetValue(token, out string concept)) concepts.Add(concept);
                string padded = "^" + token + "$";
                if (padded.Length < 3) Add(vector, "c:" + padded, 1f);
                else
                    for (int i = 0; i <= padded.Length - 3; i++)
                        Add(vector, "c:" + padded.Substring(i, 3), 1f);
            }
            foreach (KeyValuePair<string, string> phrase in PhraseConcepts)
                if (normalized.IndexOf(phrase.Key, StringComparison.Ordinal) >= 0)
                    concepts.Add(phrase.Value);
            foreach (string concept in concepts) Add(vector, "s:" + concept, 4f);
            return vector;
        }

        private static void AddConcept(string concept, string aliases)
        {
            foreach (string raw in aliases.Split('|'))
            {
                string alias = raw.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
                if (Regex.IsMatch(alias, @"^[a-z0-9]+$")) TokenConcepts[alias] = concept;
                else PhraseConcepts.Add(new KeyValuePair<string, string>(alias, concept));
            }
        }

        private static void Add(float[] vector, string feature, float weight)
        {
            uint hash = 2166136261;
            foreach (char c in feature)
            {
                hash ^= c;
                hash *= 16777619;
            }
            int index = (int)(hash % Dimensions);
            vector[index] += (hash & 0x80000000) == 0 ? weight : -weight;
        }
    }
}
