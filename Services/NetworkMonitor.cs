using System.Net.NetworkInformation;

namespace DevShell.Services;

/// <summary>
/// Samples the interface counters on a timer and derives up/down rates plus session
/// totals for the activity widget.
/// </summary>
internal sealed class NetworkMonitor
{
    private const int HistoryLength = 48;

    private readonly double[] _download = new double[HistoryLength];
    private readonly double[] _upload = new double[HistoryLength];
    private int _cursor;
    private int _samples;

    private long _lastReceived = -1;
    private long _lastSent = -1;
    private long _lastSampleTicks;
    private bool _baselineTaken;

    public long DownloadBytesPerSecond { get; private set; }

    public long UploadBytesPerSecond { get; private set; }

    public long SessionReceived { get; private set; }

    public long SessionSent { get; private set; }

    public double PeakBytesPerSecond { get; private set; }

    /// <summary>Oldest-to-newest download samples (bytes/second).</summary>
    public double[] DownloadHistory
    {
        get
        {
            var ordered = new double[Math.Max(_samples, 1)];
            for (var i = 0; i < ordered.Length; i++)
            {
                ordered[i] = _download[(_cursor - ordered.Length + i + HistoryLength * 2) % HistoryLength];
            }

            return ordered;
        }
    }

    public double[] UploadHistory
    {
        get
        {
            var ordered = new double[Math.Max(_samples, 1)];
            for (var i = 0; i < ordered.Length; i++)
            {
                ordered[i] = _upload[(_cursor - ordered.Length + i + HistoryLength * 2) % HistoryLength];
            }

            return ordered;
        }
    }

    public void Sample()
    {
        try
        {
            long received = 0;
            long sent = 0;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPInterfaceStatistics stats;
                try
                {
                    stats = nic.GetIPStatistics();
                }
                catch
                {
                    continue;
                }

                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }

            var now = DateTime.UtcNow.Ticks;
            if (!_baselineTaken)
            {
                _baselineTaken = true;
                _lastReceived = received;
                _lastSent = sent;
                _lastSampleTicks = now;
                return;
            }

            var elapsedSeconds = (now - _lastSampleTicks) / (double)TimeSpan.TicksPerSecond;
            if (elapsedSeconds <= 0.05)
            {
                return;
            }

            var deltaReceived = received - _lastReceived;
            var deltaSent = sent - _lastSent;

            // counters reset when an adapter reconnects
            if (deltaReceived < 0)
            {
                deltaReceived = 0;
            }

            if (deltaSent < 0)
            {
                deltaSent = 0;
            }

            _lastReceived = received;
            _lastSent = sent;
            _lastSampleTicks = now;

            SessionReceived += deltaReceived;
            SessionSent += deltaSent;

            DownloadBytesPerSecond = (long)(deltaReceived / elapsedSeconds);
            UploadBytesPerSecond = (long)(deltaSent / elapsedSeconds);
            PeakBytesPerSecond = Math.Max(PeakBytesPerSecond, Math.Max(DownloadBytesPerSecond, UploadBytesPerSecond));

            _download[_cursor] = DownloadBytesPerSecond;
            _upload[_cursor] = UploadBytesPerSecond;
            _cursor = (_cursor + 1) % HistoryLength;
            if (_samples < HistoryLength)
            {
                _samples++;
            }
        }
        catch (Exception ex)
        {
            App.Log("NetworkMonitor.Sample", ex);
        }
    }

    /// <summary>
    /// A throughput in the activity monitor's current unit. With <paramref name="bits"/> it is quoted
    /// the way a link is - decimal multiples of bits - and otherwise as byte sizes in multiples of
    /// 1024, which is how the bar has always shown them.
    /// </summary>
    public static string FormatRate(long bytesPerSecond, bool bits = false)
    {
        if (bytesPerSecond < 0)
        {
            bytesPerSecond = 0;
        }

        if (bits)
        {
            var bitsPerSecond = bytesPerSecond * 8d;

            if (bitsPerSecond < 1000)
            {
                return $"{bitsPerSecond:0} bit/s";
            }

            var kilobit = bitsPerSecond / 1000d;
            if (kilobit < 1000)
            {
                return $"{kilobit:0.#} Kbit/s";
            }

            var megabit = kilobit / 1000d;
            return megabit < 1000 ? $"{megabit:0.##} Mbit/s" : $"{megabit / 1000d:0.##} Gbit/s";
        }

        if (bytesPerSecond < 1024)
        {
            return $"{bytesPerSecond} B/s";
        }

        double value = bytesPerSecond / 1024d;
        if (value < 1024)
        {
            return $"{value:0.#} KB/s";
        }

        value /= 1024d;
        if (value < 1024)
        {
            return $"{value:0.##} MB/s";
        }

        return $"{value / 1024d:0.##} GB/s";
    }

    public static string FormatVolume(long bytes) => SearchHit.FormatSize(bytes);
}
