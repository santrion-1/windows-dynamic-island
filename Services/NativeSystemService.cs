using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Services
{
    public static class NativeSystemService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public static (int Percent, bool IsCharging) GetBatteryInfo()
        {
            try
            {
                if (GetSystemPowerStatus(out var status))
                {
                    int pct = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 100;
                    bool charging = status.ACLineStatus == 1 || (status.BatteryFlag & 8) != 0;
                    return (Math.Clamp(pct, 5, 100), charging);
                }
            }
            catch { }
            return (85, true);
        }

        public static void SendMediaKeyPlayPause()
        {
            try
            {
                keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        public static void SendMediaKeyNext()
        {
            try
            {
                keybd_event(VK_MEDIA_NEXT_TRACK, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MEDIA_NEXT_TRACK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        public static void SendMediaKeyPrev()
        {
            try
            {
                keybd_event(VK_MEDIA_PREV_TRACK, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MEDIA_PREV_TRACK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        // --- CoreAudio Peak Meter COM Interop ---
        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int NotImpl1();
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig]
            int GetPeakValue(out float pfPeak);
        }

        private static IAudioMeterInformation? _audioMeter;
        private static bool _audioMeterInitTried;

        public static float GetSystemAudioPeak()
        {
            try
            {
                if (_audioMeter == null && !_audioMeterInitTried)
                {
                    _audioMeterInitTried = true;
                    var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                    if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) == 0 && device != null)
                    {
                        Guid iid = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
                        if (device.Activate(ref iid, 23, IntPtr.Zero, out var obj) == 0)
                        {
                            _audioMeter = obj as IAudioMeterInformation;
                        }
                    }
                }

                if (_audioMeter != null && _audioMeter.GetPeakValue(out float peak) == 0)
                {
                    return Math.Clamp(peak, 0f, 1f);
                }
            }
            catch { }
            return 0f;
        }

        public static System.Windows.Media.Color ExtractDominantColor(BitmapSource? bitmap)
        {
            if (bitmap == null)
                return System.Windows.Media.Color.FromRgb(150, 24, 38); // Rich crimson fallback matching reference

            try
            {
                var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int w = formatted.PixelWidth;
                int h = formatted.PixelHeight;
                if (w <= 0 || h <= 0)
                    return System.Windows.Media.Color.FromRgb(150, 24, 38);

                byte[] pixels = new byte[w * h * 4];
                formatted.CopyPixels(pixels, w * 4, 0);

                long rSum = 0, gSum = 0, bSum = 0, weightSum = 0;
                int step = Math.Max(1, (w * h) / 200);
                for (int i = 0; i < pixels.Length - 4; i += 4 * step)
                {
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    int sat = max - min;
                    // Favor saturated, mid-bright colors so the glow is vibrant and rich
                    int weight = 1 + (sat / 8);
                    if (max > 30 && max < 245)
                    {
                        rSum += r * weight;
                        gSum += g * weight;
                        bSum += b * weight;
                        weightSum += weight;
                    }
                }

                if (weightSum > 0)
                {
                    byte rFinal = (byte)Math.Clamp((rSum / weightSum) * 1.15, 45, 210);
                    byte gFinal = (byte)Math.Clamp((gSum / weightSum) * 0.95, 20, 180);
                    byte bFinal = (byte)Math.Clamp((bSum / weightSum) * 0.95, 25, 190);
                    return System.Windows.Media.Color.FromRgb(rFinal, gFinal, bFinal);
                }
            }
            catch { }

            return System.Windows.Media.Color.FromRgb(150, 24, 38);
        }

        public static BitmapSource CreateDefaultCoverArt()
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var rect = new Rect(0, 0, 120, 120);
                var grad = new LinearGradientBrush(
                    System.Windows.Media.Color.FromRgb(220, 28, 56),
                    System.Windows.Media.Color.FromRgb(95, 10, 24),
                    new System.Windows.Point(0, 0),
                    new System.Windows.Point(1, 1));
                dc.DrawRoundedRectangle(grad, null, rect, 16, 16);

                // Subtle artistic circles / silhouettes resembling the red album cover in reference
                var accent1 = new RadialGradientBrush(
                    System.Windows.Media.Color.FromArgb(180, 255, 95, 120),
                    System.Windows.Media.Color.FromArgb(0, 255, 95, 120));
                dc.DrawEllipse(accent1, null, new System.Windows.Point(38, 42), 38, 38);
                dc.DrawEllipse(accent1, null, new System.Windows.Point(82, 48), 34, 34);

                // Music note icon in center
                var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 3.2);
                dc.DrawEllipse(System.Windows.Media.Brushes.White, null, new System.Windows.Point(44, 76), 8, 6);
                dc.DrawEllipse(System.Windows.Media.Brushes.White, null, new System.Windows.Point(74, 70), 8, 6);
                dc.DrawLine(pen, new System.Windows.Point(50, 75), new System.Windows.Point(50, 38));
                dc.DrawLine(pen, new System.Windows.Point(80, 69), new System.Windows.Point(80, 32));
                dc.DrawLine(new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 5), new System.Windows.Point(49, 38), new System.Windows.Point(81, 32));
            }

            var bmp = new RenderTargetBitmap(120, 120, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
