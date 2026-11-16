using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Services
{
    public class MediaSessionInfo
    {
        public string Title { get; set; } = "No Media Playing";
        public string Artist { get; set; } = "Play music on Spotify or Browser";
        public string Album { get; set; } = "";
        public bool IsPlaying { get; set; } = false;
        public double PositionSeconds { get; set; } = 0;
        public double DurationSeconds { get; set; } = 0;
        public BitmapSource? AlbumArt { get; set; }
        public System.Windows.Media.Color DominantColor { get; set; } = System.Windows.Media.Color.FromRgb(150, 24, 38);
        public bool IsExternalSession { get; set; } = false;
    }

    public class MediaAndBluetoothService : IDisposable
    {
        private Process? _bridgeProcess;
        private StreamWriter? _bridgeInput;
        private bool _disposed;

        private string _lastArtSignature = "";
        private BitmapSource? _cachedArt;
        private System.Windows.Media.Color _cachedColor = System.Windows.Media.Color.FromRgb(150, 24, 38);
        private readonly BitmapSource _defaultArt;

        public MediaSessionInfo CurrentMedia { get; private set; }
        public event Action<MediaSessionInfo>? MediaUpdated;
        public event Action<string, int>? BluetoothConnected;

        public MediaAndBluetoothService()
        {
            _defaultArt = NativeSystemService.CreateDefaultCoverArt();
            CurrentMedia = CreateIdleMediaInfo();
        }

        private MediaSessionInfo CreateIdleMediaInfo()
        {
            return new MediaSessionInfo
            {
                Title = "No Media Playing",
                Artist = "Play music on Spotify or Browser",
                Album = "",
                IsPlaying = false,
                PositionSeconds = 0,
                DurationSeconds = 0,
                AlbumArt = _defaultArt,
                DominantColor = System.Windows.Media.Color.FromRgb(150, 24, 38),
                IsExternalSession = false
            };
        }

        public void Start()
        {
            Task.Run(() =>
            {
                try
                {
                    string exePath = EnsureBridgeExecutable();
                    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                        return;

                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };

                    _bridgeProcess = Process.Start(psi);
                    if (_bridgeProcess == null) return;

                    _bridgeInput = _bridgeProcess.StandardInput;

                    while (!_disposed && !_bridgeProcess.HasExited)
                    {
                        string? line = _bridgeProcess.StandardOutput.ReadLine();
                        if (line == null) break;
                        ProcessBridgeLine(line);
                    }
                }
                catch { }
            });
        }

        private void ProcessBridgeLine(string line)
        {
            if (line.StartsWith("MEDIA|"))
            {
                var parts = line.Split('|');
                if (parts.Length < 8) return;

                string title = parts[1].Trim();
                string artist = parts[2].Trim();
                string album = parts[3].Trim();
                bool isPlaying = parts[4] == "1";
                double.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out double pos);
                double.TryParse(parts[6], NumberStyles.Any, CultureInfo.InvariantCulture, out double dur);
                string thumbPath = parts[7].Trim();

                if (string.IsNullOrEmpty(title))
                {
                    // If we were previously showing an external session and it closed, revert to clean idle state
                    if (CurrentMedia.IsExternalSession)
                    {
                        CurrentMedia = CreateIdleMediaInfo();
                        MediaUpdated?.Invoke(CurrentMedia);
                    }
                    return;
                }

                string sig = $"{title}|{artist}|{thumbPath}";
                if (sig != _lastArtSignature)
                {
                    _lastArtSignature = sig;
                    _cachedArt = LoadThumbnailSafe(thumbPath) ?? _defaultArt;
                    _cachedColor = NativeSystemService.ExtractDominantColor(_cachedArt);
                }

                if (dur <= 1) dur = Math.Max(pos + 60, 180);

                CurrentMedia = new MediaSessionInfo
                {
                    Title = title,
                    Artist = string.IsNullOrEmpty(artist) ? "Unknown Artist" : artist,
                    Album = album,
                    IsPlaying = isPlaying,
                    PositionSeconds = Math.Clamp(pos, 0, dur),
                    DurationSeconds = dur,
                    AlbumArt = _cachedArt ?? _defaultArt,
                    DominantColor = _cachedColor,
                    IsExternalSession = true
                };

                MediaUpdated?.Invoke(CurrentMedia);
            }
            else if (line.StartsWith("BT|"))
            {
                var parts = line.Split('|');
                if (parts.Length >= 3)
                {
                    string devName = parts[1].Trim();
                    int.TryParse(parts[2], out int battery);
                    if (battery <= 0) battery = 80;
                    if (!string.IsNullOrEmpty(devName))
                    {
                        BluetoothConnected?.Invoke(devName, battery);
                    }
                }
            }
        }

        private static BitmapSource? LoadThumbnailSafe(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0) return null;

                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 128;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        public void TogglePlayPause()
        {
            if (CurrentMedia.IsExternalSession)
            {
                if (!SendCommand("TOGGLE"))
                    NativeSystemService.SendMediaKeyPlayPause();
                CurrentMedia.IsPlaying = !CurrentMedia.IsPlaying;
            }
            else
            {
                NativeSystemService.SendMediaKeyPlayPause();
            }
            MediaUpdated?.Invoke(CurrentMedia);
        }

        public void NextTrack()
        {
            if (CurrentMedia.IsExternalSession)
            {
                if (!SendCommand("NEXT"))
                    NativeSystemService.SendMediaKeyNext();
            }
            else
            {
                NativeSystemService.SendMediaKeyNext();
            }
            MediaUpdated?.Invoke(CurrentMedia);
        }

        public void PreviousTrack()
        {
            if (CurrentMedia.IsExternalSession)
            {
                if (!SendCommand("PREV"))
                    NativeSystemService.SendMediaKeyPrev();
            }
            else
            {
                NativeSystemService.SendMediaKeyPrev();
            }
            MediaUpdated?.Invoke(CurrentMedia);
        }

        public void SeekTo(double seconds)
        {
            if (!CurrentMedia.IsExternalSession || CurrentMedia.DurationSeconds <= 0)
                return;

            seconds = Math.Clamp(seconds, 0, CurrentMedia.DurationSeconds);
            CurrentMedia.PositionSeconds = seconds;
            SendCommand("SEEK " + seconds.ToString("F1", CultureInfo.InvariantCulture));
            MediaUpdated?.Invoke(CurrentMedia);
        }

        public void TickDemoIfActive(double deltaSeconds)
        {
            // No dummy demo playback when idle
        }

        private bool SendCommand(string cmd)
        {
            try
            {
                if (_bridgeInput != null && _bridgeProcess != null && !_bridgeProcess.HasExited)
                {
                    _bridgeInput.WriteLine(cmd);
                    _bridgeInput.Flush();
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static string EnsureBridgeExecutable()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string localExe = Path.Combine(baseDir, "WinRtBridge.exe");
            if (File.Exists(localExe)) return localExe;

            string projectExe = Path.Combine(Directory.GetCurrentDirectory(), "WinRtBridge.exe");
            if (File.Exists(projectExe)) return projectExe;

            // Compile on the fly if needed
            string sourcePath = Path.Combine(baseDir, "Services", "WinRtBridge.cs");
            if (!File.Exists(sourcePath))
                sourcePath = Path.Combine(Directory.GetCurrentDirectory(), "Services", "WinRtBridge.cs");

            if (File.Exists(sourcePath))
            {
                try
                {
                    string csc = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe";
                    string fw = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319";
                    string winmd = @"C:\Windows\System32\WinMetadata";
                    string winrt = @"C:\Windows\Microsoft.Net\assembly\GAC_MSIL\System.Runtime.WindowsRuntime\v4.0_4.0.0.0__b77a5c561934e089\System.Runtime.WindowsRuntime.dll";

                    if (File.Exists(csc) && File.Exists(winrt))
                    {
                        string outExe = Path.Combine(Path.GetTempPath(), "DynamicIsland_WinRtBridge.exe");
                        var psi = new ProcessStartInfo
                        {
                            FileName = csc,
                            Arguments = $"/nologo /target:exe /out:\"{outExe}\" \"/r:{winrt}\" \"/r:{fw}\\System.Runtime.dll\" \"/r:{fw}\\System.Threading.Tasks.dll\" \"/r:{fw}\\System.Runtime.InteropServices.WindowsRuntime.dll\" \"/r:{winmd}\\Windows.Media.winmd\" \"/r:{winmd}\\Windows.Storage.winmd\" \"/r:{winmd}\\Windows.Foundation.winmd\" \"/r:{winmd}\\Windows.Devices.winmd\" \"{sourcePath}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(4000);
                        if (File.Exists(outExe)) return outExe;
                    }
                }
                catch { }
            }

            return "";
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                if (_bridgeProcess != null && !_bridgeProcess.HasExited)
                {
                    _bridgeProcess.Kill();
                }
            }
            catch { }
        }
    }
}
