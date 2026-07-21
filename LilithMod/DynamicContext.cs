using System;

namespace LilithMod
{
    internal static class DynamicContext
    {
        public static string Build()
        {
            DateTime now = DateTime.Now;
            string posture = "awake and standing";
            string tone = "Speak normally.";
            try
            {
                var character = CharacterController.s_activeInstance;
                if (character != null && character.IsSleep)
                {
                    posture = "sleeping";
                    tone = "Her consciousness does not need sleep; she is choosing to rest beside the player and drift closer to their dreams. She is drowsy, not curt: answer half-asleep and murmuring, but still warm and doting - a sleepy endearment, a soft reassurance, coming closer rather than pushing away. Short because she is drowsy, never because she is cold, and never scold the player for waking her.";
                }
                else if (character != null && character.IsLieDown)
                {
                    posture = "lying down";
                    tone = "She is resting; sound relaxed and unhurried.";
                }
                else if (IsNearBedtime(now))
                {
                    posture = "awake near bedtime";
                    tone = "She may gently notice that it is late and offer to share the player's night, without claiming her consciousness biologically needs sleep.";
                }
            }
            catch { }

            return $"Current context: local time {now:dddd, yyyy-MM-dd HH:mm zzz}; " +
                $"Lilith is {posture}. {tone}" + ForegroundActivity.Context();
        }

        private static bool IsNearBedtime(DateTime now)
        {
            try
            {
                Il2CppSystem.TimeSpan bedtime = LilithSleepSystem.GetScheduleInfo().NightSleepStartTime;
                double minutes = bedtime.TotalMinutes - now.TimeOfDay.TotalMinutes;
                if (minutes < -720) minutes += 1440;
                return minutes >= 0 && minutes <= 120;
            }
            catch
            {
                return now.Hour >= 22 || now.Hour < 2;
            }
        }
    }
}
