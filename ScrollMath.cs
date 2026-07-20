using System;
using System.Diagnostics;

namespace MusicBeePlugin
{
    /// <summary>
    /// Maps playback position to vertical scroll pixels (not karaoke).
    /// Start delay holds at the top, then uses full track duration as the rate
    /// (does not shrink the denominator by the delay).
    /// </summary>
    public static class ScrollMath
    {
        public static float ScrollY(int positionMs, int durationMs, int startDelayMs, float maxScrollPixels)
        {
            if (maxScrollPixels <= 0f)
                return 0f;

            int delay = Math.Max(0, startDelayMs);
            if (positionMs <= delay)
                return 0f;

            int duration = Math.Max(durationMs, 1);
            // Same rate as zero-delay scroll: pixels per ms = maxScroll/duration.
            // Delay only postpones the start; it is not subtracted from duration.
            float progress = (positionMs - delay) / (float)duration;
            if (progress > 1f) progress = 1f;
            return progress * maxScrollPixels;
        }

#if DEBUG
        static ScrollMath()
        {
            Debug.Assert(ScrollY(0, 100000, 0, 200f) == 0f);
            Debug.Assert(Math.Abs(ScrollY(50000, 100000, 0, 200f) - 100f) < 0.01f);
            Debug.Assert(ScrollY(100000, 100000, 0, 200f) == 200f);
            Debug.Assert(ScrollY(4000, 100000, 5000, 200f) == 0f); // still in delay
            Debug.Assert(ScrollY(5000, 100000, 5000, 200f) == 0f);
            // 15s after a 5s delay → 10s of motion over full 100s duration → 20px of 200
            Debug.Assert(Math.Abs(ScrollY(15000, 100000, 5000, 200f) - 20f) < 0.01f);
            Debug.Assert(ScrollY(0, 100000, 0, 0f) == 0f);
        }
#endif
    }
}
