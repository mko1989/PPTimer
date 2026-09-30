using System;
using System.IO;
using System.Media;
using PPTimer.Core;

namespace PPTimer.Windows
{
    /// <summary>
    /// Plays the zero alert: a .wav from settings, or a generated three-beep file.
    /// Always plays from a file path: SoundPlayer's in-memory playback isn't GC-safe when async.
    /// </summary>
    internal static class SoundAlert
    {
        static readonly object Gate = new object();
        static string beepPath;
        static SoundPlayer current;

        public static void Init(string dataDirectory)
        {
            beepPath = Path.Combine(dataDirectory, "beep.wav");
            try
            {
                if (!File.Exists(beepPath)) File.WriteAllBytes(beepPath, MakeBeep());
            }
            catch (Exception ex)
            {
                Log.Error("Could not write beep.wav", ex);
            }
        }

        public static void Play(string soundFile)
        {
            var file = beepPath;
            if (!string.IsNullOrWhiteSpace(soundFile))
            {
                if (File.Exists(soundFile)) file = soundFile;
                else Log.Warn($"Sound file not found: {soundFile}; playing the built-in beep");
            }

            try
            {
                lock (Gate)
                {
                    current?.Stop();
                    current = new SoundPlayer(file);
                    current.Play(); // async
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Could not play {file}", ex);
            }
        }

        /// <summary>Three short 880 Hz beeps, 16-bit mono PCM.</summary>
        static byte[] MakeBeep()
        {
            const int rate = 44100;
            const double beep = 0.18, gap = 0.12;
            var total = (int)(rate * (3 * beep + 2 * gap));
            var samples = new short[total];
            var fade = (int)(rate * 0.006);
            for (var n = 0; n < 3; n++)
            {
                var start = (int)(rate * n * (beep + gap));
                var length = (int)(rate * beep);
                for (var i = 0; i < length && start + i < total; i++)
                {
                    var envelope = Math.Min(1.0, Math.Min(i, length - i) / (double)fade);
                    samples[start + i] = (short)(Math.Sin(2 * Math.PI * 880 * i / rate) * envelope * 0.6 * short.MaxValue);
                }
            }

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                var dataBytes = samples.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16);            // fmt chunk size
                w.Write((short)1);      // PCM
                w.Write((short)1);      // mono
                w.Write(rate);
                w.Write(rate * 2);      // byte rate
                w.Write((short)2);      // block align
                w.Write((short)16);     // bits per sample
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                foreach (var s in samples) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
