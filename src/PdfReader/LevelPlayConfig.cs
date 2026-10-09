using System;
using System.Collections.Generic;
using System.Text;

namespace PdfReader
{
    public static class LevelPlayConfig
    {
        public const string AppKey = "28917c775";
        public const string BannerAdUnitId = "w83s1w62mhxkd3om";
        public const string InterstitialAdUnitId = "geri7xih9uhw31sv";

        /// <summary>Turns on LevelPlay's own adapter debug logging (<c>LevelPlay.SetAdaptersDebug</c>). Debug builds
        /// only, same split as <see cref="AdsConfig.UseTestAds"/> -- does not affect which ad unit IDs are used,
        /// only how chatty the SDK's own logcat output is.</summary>
#if DEBUG
        public const bool TestMode = true;
#else
    public const bool TestMode = false;
#endif
    }
}
