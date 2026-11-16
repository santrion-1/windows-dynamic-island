using System;
using System.IO;
using System.Text;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.Devices.Enumeration;

class Program
{
    static GlobalSystemMediaTransportControlsSessionManager _manager;

    static T WaitOp<T>(IAsyncOperation<T> op)
    {
        int waited = 0;
        while (op.Status == AsyncStatus.Started && waited < 3000)
        {
            Thread.Sleep(15);
            waited += 15;
        }
        if (op.Status == AsyncStatus.Completed)
            return op.GetResults();
        return default(T);
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            _manager = WaitOp(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERR|" + ex.Message);
            return;
        }

        bool once = args != null && args.Length > 0 && args[0] == "--once";
        if (!once)
        {
            Thread cmdThread = new Thread(ReadCommands) { IsBackground = true };
            cmdThread.Start();
        }

        string lastThumbKey = "";
        string thumbPath = Path.Combine(Path.GetTempPath(), "dynamic_island_thumb.png");
        string lastBtDevice = "";
        int tickCount = 0;

        while (true)
        {
            try
            {
                var session = _manager != null ? _manager.GetCurrentSession() : null;
                if (session != null)
                {
                    var props = WaitOp(session.TryGetMediaPropertiesAsync());
                    var playback = session.GetPlaybackInfo();
                    var timeline = session.GetTimelineProperties();

                    string title = props != null ? (props.Title ?? "") : "";
                    string artist = props != null ? (props.Artist ?? "") : "";
                    string album = props != null ? (props.AlbumTitle ?? "") : "";
                    bool isPlaying = playback != null &&
                        playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                    double posSec = timeline != null ? timeline.Position.TotalSeconds : 0;
                    double endSec = timeline != null ? timeline.EndTime.TotalSeconds : 0;
                    if (isPlaying && timeline != null && timeline.LastUpdatedTime != default(DateTimeOffset))
                    {
                        double elapsed = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds;
                        if (elapsed > 0 && elapsed < 3600)
                            posSec = Math.Min(endSec > 0 ? endSec : posSec + elapsed, posSec + elapsed);
                    }

                    string curThumbKey = title + "|" + artist;
                    bool hasThumb = false;
                    if (!string.IsNullOrEmpty(title) && curThumbKey != lastThumbKey && props != null && props.Thumbnail != null)
                    {
                        try
                        {
                            using (var stream = WaitOp(props.Thumbnail.OpenReadAsync()))
                            {
                                if (stream != null && stream.Size > 0)
                                {
                                    byte[] buf = new byte[stream.Size];
                                    using (var reader = new DataReader(stream))
                                    {
                                        WaitOp(reader.LoadAsync((uint)stream.Size));
                                        reader.ReadBytes(buf);
                                    }
                                    File.WriteAllBytes(thumbPath, buf);
                                    lastThumbKey = curThumbKey;
                                }
                            }
                        }
                        catch { }
                    }
                    hasThumb = File.Exists(thumbPath) && curThumbKey == lastThumbKey;

                    Console.WriteLine(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "MEDIA|{0}|{1}|{2}|{3}|{4:F1}|{5:F1}|{6}",
                        Escape(title), Escape(artist), Escape(album),
                        isPlaying ? "1" : "0", posSec, endSec,
                        hasThumb ? Escape(thumbPath) : ""));
                }
                else
                {
                    Console.WriteLine("MEDIA||||0|0|0|");
                }

                // Check connected Bluetooth devices every ~5 seconds
                if (tickCount % 6 == 0)
                {
                    try
                    {
                        string btSelector = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" AND System.Devices.Aep.IsConnected:=System.StructuredQueryType.Boolean#True";
                        var devices = WaitOp(DeviceInformation.FindAllAsync(btSelector, new string[] { "System.Devices.Aep.IsConnected" }));
                        if (devices != null && devices.Count > 0)
                        {
                            string devName = devices[0].Name ?? "";
                            if (!string.IsNullOrEmpty(devName) && devName != lastBtDevice)
                            {
                                lastBtDevice = devName;
                                Console.WriteLine("BT|" + Escape(devName) + "|85");
                            }
                        }
                        else
                        {
                            lastBtDevice = "";
                        }
                    }
                    catch { }
                }
            }
            catch
            {
                Console.WriteLine("MEDIA||||0|0|0|");
            }

            if (once) break;
            tickCount++;
            Thread.Sleep(800);
        }
    }

    static void ReadCommands()
    {
        while (true)
        {
            string line = Console.ReadLine();
            if (line == null) break;
            try
            {
                var session = _manager != null ? _manager.GetCurrentSession() : null;
                if (session == null) continue;
                line = line.Trim();
                if (line == "TOGGLE") WaitOp(session.TryTogglePlayPauseAsync());
                else if (line == "PLAY") WaitOp(session.TryPlayAsync());
                else if (line == "PAUSE") WaitOp(session.TryPauseAsync());
                else if (line == "NEXT") WaitOp(session.TrySkipNextAsync());
                else if (line == "PREV") WaitOp(session.TrySkipPreviousAsync());
                else if (line.StartsWith("SEEK "))
                {
                    double sec;
                    if (double.TryParse(line.Substring(5), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out sec))
                    {
                        long ticks = TimeSpan.FromSeconds(sec).Ticks;
                        WaitOp(session.TryChangePlaybackPositionAsync(ticks));
                    }
                }
            }
            catch { }
        }
    }

    static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("|", " ").Replace("\r", "").Replace("\n", " ");
    }
}
