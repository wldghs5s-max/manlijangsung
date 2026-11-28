using OpenCvSharp;

sealed class SquirrelTracker : IDisposable
{
    private const double PromptThreshold = 0.82;
    private static readonly Rect SearchBand = new(400, 120, 720, 130);

    private readonly Mat promptGray;
    private readonly Mat gray = new();
    private readonly Mat match = new();
    private readonly Mat mask = new();
    private readonly Mat labels = new();
    private readonly Mat stats = new();
    private readonly Mat centroids = new();
    private readonly List<AcornTrack> tracks = [];

    private bool wasOpen;
    private int rowTop;
    private TimeSpan missingSince = TimeSpan.MinValue;
    private int clicks;
    private bool clickReady;
    private int clickX;
    private int clickY;

    public SquirrelTracker()
    {
        using var color = Cv2.ImRead(Path.Combine(BundledFiles.TemplateDirectory, "squirrel-prompt.png"), ImreadModes.Color);
        if (color.Empty())
            throw new FileNotFoundException("다람쥐 허풍선 안내 문구 템플릿이 없습니다.", "squirrel-prompt.png");
        promptGray = new Mat();
        Cv2.CvtColor(color, promptGray, ColorConversionCodes.BGR2GRAY);
    }

    public bool IsOpen { get; private set; }
    public bool JustOpened { get; private set; }
    public string Status { get; private set; } = "";

    public void Observe(Mat frame, TimeSpan now)
    {
        JustOpened = false;
        clickReady = false;
        if (frame.Width != 1920 || frame.Height != 1080 || frame.Type() != MatType.CV_8UC3)
        {
            Close();
            return;
        }

        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        bool prompt = MatchPrompt(out int promptX, out int promptY);
        if (!prompt)
        {
            if (wasOpen && missingSince == TimeSpan.MinValue)
                missingSince = now;
            if (!wasOpen || missingSince == TimeSpan.MinValue || now - missingSince >= TimeSpan.FromMilliseconds(400))
                Close();
            IsOpen = wasOpen && missingSince != TimeSpan.MinValue && now - missingSince < TimeSpan.FromMilliseconds(400);
            return;
        }

        missingSince = TimeSpan.MinValue;
        IsOpen = true;
        // 안내 문구 아래에서 250px는 섞이는 공중이다.
        // 다람쥐 두 줄은 그보다 아래에 있으므로 뒷열도 클릭할 수 있다.
        rowTop = promptY + 250;
        if (!wasOpen)
        {
            wasOpen = true;
            JustOpened = true;
            clicks = 0;
            tracks.Clear();
            Status = "다람쥐 야바위 시작";
        }

        if (clicks >= 2)
        {
            Status = "다람쥐 클릭 2회 후 대기";
            return;
        }

        // 바위와 나무의 주황은 화면이 열리는 동안 가만히 있다.
        // 그 중 가장 큰 덩어리를 고르면 앞열 바위를 1번 다람쥐로 누른다.
        // 실제로 자리를 옮긴 도토리만 따라가고, 뒷열에 멈춰도 클릭한다.
        List<AcornBlob> blobs = FindAcorns(frame, promptX, promptY);
        UpdateTracks(blobs, now);
        AcornTrack? settled = ChooseSettled(now);
        if (settled is null)
        {
            Status = DescribeFollow(now);
            return;
        }

        clickReady = true;
        clickX = (int)Math.Round(settled.X) - 10;
        clickY = (int)Math.Round(settled.Y) - 14;
        Status = $"도토리 정지 ({clickX},{clickY})";
    }

    public bool TryGetClick(out int x, out int y, out string detail)
    {
        x = clickX;
        y = clickY;
        detail = Status;
        return clickReady;
    }

    public void ConfirmClick()
    {
        clicks++;
        clickReady = false;
        foreach (AcornTrack track in tracks)
        {
            track.Shuffled = false;
            track.MaxSpan = 0;
            track.Recent.Clear();
            track.Recent.Add((track.Seen, track.X, track.Y));
        }
        Status = $"도토리 다람쥐 클릭 {clicks}회";
    }

    public void Dispose()
    {
        promptGray.Dispose();
        gray.Dispose();
        match.Dispose();
        mask.Dispose();
        labels.Dispose();
        stats.Dispose();
        centroids.Dispose();
    }

    private bool MatchPrompt(out int x, out int y)
    {
        x = 0;
        y = 0;
        using var band = new Mat(gray, SearchBand);
        if (band.Width < promptGray.Width || band.Height < promptGray.Height)
            return false;
        Cv2.MatchTemplate(band, promptGray, match, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(match, out _, out double score, out _, out Point loc);
        if (score < PromptThreshold)
            return false;
        x = SearchBand.X + loc.X;
        y = SearchBand.Y + loc.Y;
        return true;
    }

    private List<AcornBlob> FindAcorns(Mat frame, int promptX, int promptY)
    {
        var found = new List<AcornBlob>();
        var roi = new Rect(promptX - 130, promptY + 100, 725, 390);
        roi.X = Math.Clamp(roi.X, 0, frame.Width - 1);
        roi.Y = Math.Clamp(roi.Y, 0, frame.Height - 1);
        roi.Width = Math.Min(roi.Width, frame.Width - roi.X);
        roi.Height = Math.Min(roi.Height, frame.Height - roi.Y);
        using var view = new Mat(frame, roi);
        Cv2.InRange(view, new Scalar(0, 90, 185), new Scalar(145, 235, 255), mask);
        int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        for (int i = 1; i < count; i++)
        {
            int area = stats.At<int>(i, 4);
            int width = stats.At<int>(i, 2);
            int height = stats.At<int>(i, 3);
            if (area is < 12 or > 280 || width is < 6 or > 42 || height is < 6 or > 42)
                continue;
            double cx = centroids.At<double>(i, 0);
            double cy = centroids.At<double>(i, 1);
            int px = Math.Clamp((int)Math.Round(cx), 0, view.Width - 1);
            int py = Math.Clamp((int)Math.Round(cy), 0, view.Height - 1);
            Vec3b pixel = view.At<Vec3b>(py, px);
            int b = pixel.Item0;
            int g = pixel.Item1;
            int r = pixel.Item2;
            if (r < 185 || g is < 90 or > 235 || b > 145 || r - b < 80 || r - g < 8)
                continue;
            found.Add(new AcornBlob(roi.X + cx, roi.Y + cy));
        }
        return found;
    }

    private void UpdateTracks(List<AcornBlob> blobs, TimeSpan now)
    {
        var follower = new bool[tracks.Count];
        for (int i = 0; i < tracks.Count; i++)
            follower[i] = IsFollower(tracks[i], now);

        var trackUsed = new bool[tracks.Count];
        var blobUsed = new bool[blobs.Count];
        MatchTracks(blobs, follower, trackUsed, blobUsed, now, followers: false, radius: 34);
        MatchTracks(blobs, follower, trackUsed, blobUsed, now, followers: true, radius: 130);

        int looseTrack = -1;
        int looseCount = 0;
        for (int i = 0; i < follower.Length; i++)
        {
            if (follower[i] && !trackUsed[i])
            {
                looseTrack = i;
                looseCount++;
            }
        }
        if (looseCount == 1)
        {
            int bestBlob = -1;
            double bestDist = 240;
            for (int b = 0; b < blobs.Count; b++)
            {
                if (blobUsed[b] || NearBackground(blobs[b], follower))
                    continue;
                double dist = Distance(tracks[looseTrack].X, tracks[looseTrack].Y, blobs[b].X, blobs[b].Y);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestBlob = b;
                }
            }
            if (bestBlob >= 0)
            {
                Bind(tracks[looseTrack], blobs[bestBlob], now);
                trackUsed[looseTrack] = true;
                blobUsed[bestBlob] = true;
            }
        }

        for (int b = 0; b < blobs.Count; b++)
        {
            if (blobUsed[b])
                continue;
            var born = new AcornTrack
            {
                X = blobs[b].X,
                Y = blobs[b].Y,
                Seen = now,
                Born = now,
            };
            born.Recent.Add((now, blobs[b].X, blobs[b].Y));
            tracks.Add(born);
        }

        tracks.RemoveAll(track => now - track.Seen > TimeSpan.FromMilliseconds(260));
    }

    private void MatchTracks(
        List<AcornBlob> blobs,
        bool[] follower,
        bool[] trackUsed,
        bool[] blobUsed,
        TimeSpan now,
        bool followers,
        double radius)
    {
        var pairs = new List<(double Dist, int Track, int Blob)>();
        for (int t = 0; t < follower.Length; t++)
        {
            if (trackUsed[t] || follower[t] != followers)
                continue;
            for (int b = 0; b < blobs.Count; b++)
            {
                if (blobUsed[b])
                    continue;
                double dist = Distance(tracks[t].X, tracks[t].Y, blobs[b].X, blobs[b].Y);
                if (dist <= radius)
                    pairs.Add((dist, t, b));
            }
        }
        pairs.Sort((left, right) => left.Dist.CompareTo(right.Dist));
        foreach ((double _, int track, int blob) in pairs)
        {
            if (trackUsed[track] || blobUsed[blob])
                continue;
            Bind(tracks[track], blobs[blob], now);
            trackUsed[track] = true;
            blobUsed[blob] = true;
        }
    }

    private static void Bind(AcornTrack track, AcornBlob blob, TimeSpan now)
    {
        track.X = blob.X;
        track.Y = blob.Y;
        track.Seen = now;
        track.Recent.Add((now, blob.X, blob.Y));
        while (track.Recent.Count > 0 && now - track.Recent[0].Time > TimeSpan.FromSeconds(2))
            track.Recent.RemoveAt(0);
        foreach ((TimeSpan time, double x, double y) in track.Recent)
        {
            track.MaxSpan = Math.Max(track.MaxSpan, Distance(blob.X, blob.Y, x, y));
            if (!track.Shuffled
                && now - time >= TimeSpan.FromMilliseconds(350)
                && Distance(blob.X, blob.Y, x, y) >= 55)
                track.Shuffled = true;
        }
    }

    private bool NearBackground(AcornBlob blob, bool[] follower)
    {
        for (int i = 0; i < follower.Length; i++)
        {
            if (!follower[i] && Distance(tracks[i].X, tracks[i].Y, blob.X, blob.Y) <= 40)
                return true;
        }
        return false;
    }

    private static bool IsFollower(AcornTrack track, TimeSpan now) =>
        track.Shuffled || now - track.Born < TimeSpan.FromMilliseconds(700);

    private AcornTrack? ChooseSettled(TimeSpan now)
    {
        AcornTrack? best = null;
        foreach (AcornTrack track in tracks)
        {
            if (!track.Shuffled || track.Y < rowTop || !IsStable(track, now))
                continue;
            if (best is null || track.MaxSpan > best.MaxSpan)
                best = track;
        }
        return best;
    }

    private string DescribeFollow(TimeSpan now)
    {
        AcornTrack? best = null;
        foreach (AcornTrack track in tracks)
        {
            if (!track.Shuffled)
                continue;
            if (best is null || track.MaxSpan > best.MaxSpan)
                best = track;
        }
        if (best is null)
            return "도토리 추적 중";
        return $"추적 ({best.X:0},{best.Y:0}) 섞임=True 낮음={best.Y >= rowTop} 정지={IsStable(best, now)}";
    }

    private static bool IsStable(AcornTrack track, TimeSpan now)
    {
        if (track.Recent.Count == 0 || now - track.Seen > TimeSpan.FromMilliseconds(160))
            return false;
        TimeSpan oldest = now;
        foreach ((TimeSpan time, double x, double y) in track.Recent)
        {
            if (now - time > TimeSpan.FromMilliseconds(500))
                continue;
            if (Distance(track.X, track.Y, x, y) > 24)
                return false;
            if (time < oldest)
                oldest = time;
        }
        return now - oldest >= TimeSpan.FromMilliseconds(280);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2;
        double dy = y1 - y2;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private void Close()
    {
        IsOpen = false;
        wasOpen = false;
        clicks = 0;
        clickReady = false;
        missingSince = TimeSpan.MinValue;
        tracks.Clear();
        Status = "";
    }

    private sealed class AcornTrack
    {
        public double X;
        public double Y;
        public double MaxSpan;
        public TimeSpan Seen;
        public TimeSpan Born;
        public bool Shuffled;
        public readonly List<(TimeSpan Time, double X, double Y)> Recent = [];
    }

    private readonly struct AcornBlob(double x, double y)
    {
        public double X { get; } = x;
        public double Y { get; } = y;
    }

    public static void Check(List<string> failures)
    {
        string path = Path.GetFullPath("public/허풍선영상.mp4");
        if (!File.Exists(path))
        {
            failures.Add("다람쥐 허풍선 영상이 없습니다.");
            return;
        }

        using var tracker = new SquirrelTracker();
        using var video = File.OpenRead(path);
        using var capture = new VideoCapture(video, VideoCaptureAPIs.FFMPEG, []);
        using var frame = new Mat();
        if (!capture.IsOpened())
        {
            failures.Add("다람쥐 허풍선 영상을 열 수 없습니다.");
            return;
        }

        var clicks = new List<(double Time, int X, int Y)>();
        double fps = capture.Fps;
        int index = 0;
        bool opened = false;
        while (capture.Read(frame) && !frame.Empty())
        {
            double seconds = capture.Get(VideoCaptureProperties.PosMsec) / 1000;
            if (!double.IsFinite(seconds) || seconds < 0 || (seconds == 0 && index > 0))
                seconds = fps > 1 ? index / fps : index / 30.0;
            var now = TimeSpan.FromSeconds(seconds);
            tracker.Observe(frame, now);
            if (tracker.JustOpened)
                opened = true;
            if (tracker.TryGetClick(out int x, out int y, out _))
            {
                clicks.Add((seconds, x, y));
                tracker.ConfirmClick();
            }
            index++;
        }

        if (!opened)
            failures.Add("다람쥐 허풍선 안내 문구를 감지하지 못했습니다.");
        if (!clicks.Any(c => c.Time is > 28 and < 37 && c.X > 980 && c.Y > 400))
            failures.Add("돌길 다람쥐는 오른쪽 도토리 다람쥐를 클릭해야 합니다. actual=" + Format(clicks));
        if (!clicks.Any(c => c.Time is > 48 and < 56 && c.X < 560 && c.Y > 400))
            failures.Add("동굴 다람쥐는 왼쪽 도토리 다람쥐를 클릭해야 합니다. actual=" + Format(clicks));
        if (clicks.Any(c => c.Y < 400))
            failures.Add("섞이는 중 공중의 도토리를 클릭했습니다. actual=" + Format(clicks));

        string wall = Path.GetFullPath("public/만리장성수리.mp4");
        if (File.Exists(wall))
        {
            using var wallVideo = File.OpenRead(wall);
            using var wallCapture = new VideoCapture(wallVideo, VideoCaptureAPIs.FFMPEG, []);
            using var wallFrame = new Mat();
            if (wallCapture.IsOpened() && wallCapture.Read(wallFrame) && !wallFrame.Empty())
            {
                tracker.Observe(wallFrame, TimeSpan.FromSeconds(30));
                if (tracker.IsOpen)
                    failures.Add("성벽 화면을 다람쥐 허풍선으로 오인했습니다.");
            }
        }

        CheckGrassRows(failures);
    }

    private static void CheckGrassRows(List<string> failures)
    {
        string path = "/Users/mac/Downloads/봉황기록/다람쥐/20261128-150940.jpg";
        if (!File.Exists(path))
            return;
        using var still = Cv2.ImRead(path, ImreadModes.Color);
        if (still.Empty() || still.Width != 1920 || still.Height != 1080)
            return;

        using (var tracker = new SquirrelTracker())
        {
            bool opened = false;
            for (int frame = 0; frame < 40; frame++)
            {
                tracker.Observe(still, TimeSpan.FromMilliseconds(frame * 50));
                if (tracker.JustOpened)
                    opened = true;
                if (tracker.TryGetClick(out int x, out int y, out _))
                    failures.Add($"잔디 화면의 가만히 있는 바위를 도토리로 클릭했습니다. ({x},{y})");
            }
            if (!opened)
                failures.Add("잔디 다람쥐 화면에서 안내 문구를 찾지 못했습니다.");
        }

        // 15:09 화면에서 클릭된 (418,503)은 왼쪽 바위다. 도토리가 뒷열이나 오른쪽 다람쥐에서 멈춰도 그 바위를 누르면 안 된다.
        CheckLandsOn(still, failures, 980, 360, 844, 430, "뒷열");
        CheckLandsOn(still, failures, 760, 470, 1048, 436, "오른쪽 앞열");
    }

    private static void CheckLandsOn(Mat still, List<string> failures, int fromX, int fromY, int toX, int toY, string name)
    {
        using var tracker = new SquirrelTracker();
        var clicks = new List<(double Time, int X, int Y)>();
        for (int frame = 0; frame < 60; frame++)
        {
            var time = TimeSpan.FromMilliseconds(frame * 40);
            using var view = still.Clone();
            if (frame >= 20)
            {
                double blend = Math.Clamp((frame - 20) / 12.0, 0, 1);
                int x = (int)Math.Round(fromX + ((toX - fromX) * blend));
                int y = (int)Math.Round(fromY + ((toY - fromY) * blend));
                Cv2.Rectangle(view, new Rect(x - 8, y - 8, 16, 16), new Scalar(40, 160, 230), -1);
            }
            tracker.Observe(view, time);
            if (!tracker.TryGetClick(out int clickX, out int clickY, out _))
                continue;
            clicks.Add((time.TotalSeconds, clickX, clickY));
            tracker.ConfirmClick();
        }

        int expectX = toX - 10;
        int expectY = toY - 14;
        if (!clicks.Any(click => Math.Abs(click.X - expectX) <= 28 && Math.Abs(click.Y - expectY) <= 28 && click.Time >= 1.2))
            failures.Add($"잔디 {name} 도토리 다람쥐를 클릭해야 합니다. 기대=({expectX},{expectY}) actual={Format(clicks)}");
        if (clicks.Any(click => Math.Abs(click.X - 418) <= 45 && Math.Abs(click.Y - 503) <= 45))
            failures.Add($"잔디 {name} 도토리 대신 왼쪽 바위를 클릭했습니다. actual={Format(clicks)}");
    }

    private static string Format(List<(double Time, int X, int Y)> clicks) =>
        clicks.Count == 0
            ? "없음"
            : string.Join(", ", clicks.Select(c => $"{c.Time:0.0}s ({c.X},{c.Y})"));
}
