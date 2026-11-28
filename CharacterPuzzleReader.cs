using System.Diagnostics;
using System.Globalization;
using System.Text;
#if WINDOWS10_0_19041_0_OR_GREATER
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
#endif
using OpenCvSharp;

sealed class CharacterPuzzleReader : IDisposable
{
    private const double PanelThreshold = 0.80;
    private const double FailSuffixThreshold = 0.90;
    private const double FailDigitThreshold = 0.75;
    private const int FailDigitConfirmations = 3;
    private const double SquareCornerRatio = 1.15;
    private const double RoundCornerRatio = 0.80;
    private const int JongseongIeung = 21;
    private const int JongseongMieum = 16;
    private static readonly TimeSpan SampleGap = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CloseGap = TimeSpan.FromMilliseconds(400);
    private static readonly Rect PromptArea = new(499, 842, 450, 46);
    private static readonly Rect GlyphArea = new(352, 870, 382, 92);
    private static readonly Rect FailWatchArea = new(900, 870, 70, 40);
    private static readonly Rect FailSuffixWindow = new(21, 5, 32, 30);
    private static readonly Rect FailDigitWindow = new(5, 5, 24, 30);
    private static readonly Rect FailDigitCrop = new(8, 8, 18, 24);
    private static readonly double[] SlotX = [0.078, 0.205, 0.328, 0.455, 0.582, 0.735];

    private readonly Mat prompt;
    private readonly Mat failSuffix;
    private readonly Mat gray = new();
    private readonly Mat score = new();
    private readonly string glyphPath;
    private Mat? failDigit;
    private Mat? lastGlyph;
    private int settledRun;
    private IGlyphSession? session;
    private string? ocrError;
    private int readFailures;
    private int failMismatches;
    private int attempt;
    private SlotAccumulator[] slots = NewSlots();
    private bool open;
    private TimeSpan detectedAt;
    private TimeSpan readFrom;
    private TimeSpan readTo;
    private TimeSpan lastSeen;
    private TimeSpan lastSample = TimeSpan.MinValue;
    private int samples;

    public CharacterPuzzleReader(string fileName)
    {
        FileName = fileName;
        string template = Path.Combine(BundledFiles.TemplateDirectory, "character-prompt.png");
        prompt = Cv2.ImDecode(File.ReadAllBytes(template), ImreadModes.Grayscale);
        if (prompt.Empty() || prompt.Width != PromptArea.Width || prompt.Height != PromptArea.Height)
        {
            prompt.Dispose();
            throw new InvalidDataException("character-prompt.png 크기가 문자 문제 안내문 영역과 다릅니다.");
        }

        string suffixPath = Path.Combine(BundledFiles.TemplateDirectory, "fail-count-suffix.png");
        failSuffix = Cv2.ImDecode(File.ReadAllBytes(suffixPath), ImreadModes.Grayscale);
        if (failSuffix.Empty() || failSuffix.Width != 26 || failSuffix.Height != 24)
        {
            prompt.Dispose();
            failSuffix.Dispose();
            throw new InvalidDataException("fail-count-suffix.png 크기가 실패 횟수 /3 영역과 다릅니다.");
        }

        glyphPath = Path.Combine(Path.GetTempPath(), "BonghwangWall", "glyph.png");
        Directory.CreateDirectory(Path.GetDirectoryName(glyphPath)!);
    }

    public string FileName { get; }
    public List<CharacterReading> Readings { get; } = [];
    public bool IsOpen => open;
    public int OpenAttempt => open ? attempt : 0;
    public string? OpenError => open ? ocrError : null;
    public TimeSpan OpenedAt => detectedAt;

    public bool TryCurrentAnswer(TimeSpan now, out string candidate, out string detail)
    {
        candidate = "";
        detail = "";
        if (!open || ocrError is not null)
            return false;

        if (samples < 4 || slots.Any(slot => !slot.IsConfident()))
            return false;
        if (slots.Any(slot => slot.NeedsJongSample()) && samples < 8)
            return false;

        CharacterReading reading = BuildReading();
        detail = reading.Detail;
        if (reading.Candidate.Length != 6 || reading.Candidate.Contains('?'))
            return false;

        candidate = reading.Candidate;
        return true;
    }

    public void Observe(Mat frame, TimeSpan time)
    {
        if (frame.Width != 1920 || frame.Height != 1080)
            return;

        bool visible = PanelScore(frame) >= PanelThreshold;
        if (visible)
        {
            if (!open)
                Begin(time);

            DigitWatch state = WatchDigit(frame);
            if (state == DigitWatch.Restart)
            {
                if (samples > 0)
                    Close();
                Begin(time);
                state = WatchDigit(frame);
            }

            lastSeen = time;
            if (state == DigitWatch.Stable && time - lastSample >= SampleGap)
            {
                lastSample = time;
                if (!GlyphSettled(frame))
                    return;
                ReadGlyphs(frame);
                readTo = time;
                samples++;
            }
            return;
        }

        if (open && time - lastSeen >= CloseGap)
            Close();
    }

    public void Complete()
    {
        if (open)
            Close();
    }

    public void Dispose()
    {
        session?.Dispose();
        lastGlyph?.Dispose();
        prompt.Dispose();
        failSuffix.Dispose();
        failDigit?.Dispose();
        gray.Dispose();
        score.Dispose();
    }

    private void Begin(TimeSpan time)
    {
        attempt++;
        open = true;
        detectedAt = time;
        readFrom = time;
        readTo = time;
        lastSample = time - SampleGap;
        samples = 0;
        slots = NewSlots();
        settledRun = 0;
        lastGlyph?.Dispose();
        lastGlyph = null;
        ocrError = null;
        readFailures = 0;
        ClearDigit();
        if (attempt > 1)
        {
            Console.WriteLine(
                $"영상 {FileName} | 문자 허풍선 {attempt}회차 시작 {Format(time)} | 새 미니게임 | 이전 글자는 사용하지 않음");
        }
    }

    private void Close()
    {
        open = false;
        ClearDigit();
        CharacterReading reading = BuildReading();
        Readings.Add(reading);
        Console.WriteLine(
            $"영상 {FileName} | 문자 허풍선 {reading.Attempt}회차 감지 {Format(reading.DetectedAt)} | 판독 구간 {Format(reading.ReadFrom)}–{Format(reading.ReadTo)}");
        Console.WriteLine($"문자 후보 {reading.Candidate} | {reading.Detail}");
    }

    private DigitWatch WatchDigit(Mat frame)
    {
        using var region = new Mat(frame, FailWatchArea);
        Cv2.CvtColor(region, gray, ColorConversionCodes.BGR2GRAY);
        if (Match(gray, failSuffix, FailSuffixWindow) < FailSuffixThreshold)
        {
            failMismatches = 0;
            return DigitWatch.Ignore;
        }

        if (failDigit is null)
        {
            using var crop = new Mat(gray, FailDigitCrop);
            failDigit = crop.Clone();
            failMismatches = 0;
            return DigitWatch.Stable;
        }

        if (Match(gray, failDigit, FailDigitWindow) >= FailDigitThreshold)
        {
            failMismatches = 0;
            return DigitWatch.Stable;
        }

        failMismatches++;
        return failMismatches >= FailDigitConfirmations ? DigitWatch.Restart : DigitWatch.Ignore;
    }

    private void ClearDigit()
    {
        failDigit?.Dispose();
        failDigit = null;
        failMismatches = 0;
    }

    private double Match(Mat image, Mat template, Rect window)
    {
        using var region = new Mat(image, window);
        Cv2.MatchTemplate(region, template, score, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(score, out _, out double max, out _, out _);
        return max;
    }

    private CharacterReading BuildReading()
    {
        var chars = new char[slots.Length];
        var notes = new List<string>();
        int weakest = int.MaxValue;
        for (int i = 0; i < slots.Length; i++)
        {
            chars[i] = slots[i].Decide(out string note, out int margin);
            notes.Add($"자리{i + 1} {note}");
            weakest = Math.Min(weakest, margin);
        }

        string marginText = weakest == int.MaxValue ? "표 없음" : $"가장 작은 표차 {weakest}";
        string detail = $"{marginText} | 표본 {samples}프레임 | " + string.Join(" | ", notes);
        if (ocrError is not null)
            detail += $" | 인식 오류 {ocrError}";
        return new CharacterReading(FileName, attempt, detectedAt, readFrom, readTo, new string(chars), detail);
    }

    private bool GlyphSettled(Mat frame)
    {
        using var region = new Mat(frame, GlyphArea);
        using var current = new Mat();
        Cv2.CvtColor(region, current, ColorConversionCodes.BGR2GRAY);
        if (lastGlyph is null || lastGlyph.Width != current.Width || lastGlyph.Height != current.Height)
        {
            lastGlyph?.Dispose();
            lastGlyph = current.Clone();
            settledRun = 0;
            return false;
        }

        using var diff = new Mat();
        Cv2.Absdiff(current, lastGlyph, diff);
        double mean = Cv2.Mean(diff).Val0;
        lastGlyph.Dispose();
        lastGlyph = current.Clone();
        if (mean > 8.5)
        {
            settledRun = 0;
            return false;
        }

        settledRun++;
        return settledRun >= 2;
    }

    private void ReadGlyphs(Mat frame)
    {
        if (ocrError is not null)
            return;
        using Mat enhanced = Enhance(frame);
        if (!Cv2.ImEncode(".png", enhanced, out byte[] png) || png.Length < 32)
        {
            ocrError = "글자 영역 이미지를 저장하지 못했습니다.";
            return;
        }
        File.WriteAllBytes(glyphPath, png);
        IEnumerable<(string Glyph, double X, double Y)> found;
        try
        {
            session ??= OpenSession();
            found = session.Read(glyphPath);
        }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException or IOException)
        {
            readFailures++;
            if (readFailures >= 5)
                ocrError = error.Message;
            return;
        }

        foreach ((string glyph, double x, double y) in found)
        {
            if (glyph.Length != 1 || !IsSyllable(glyph[0]) || y < 0.40 || y > 0.62)
                continue;

            int slot = NearestSlot(x);
            if (slot < 0)
                continue;

            double? ratio = CornerRatio(enhanced, x);
            slots[slot].Add(glyph[0], ratio, GlyphClarity(enhanced, x));
        }
    }

    private static int NearestSlot(double x)
    {
        int best = 0;
        double distance = Math.Abs(SlotX[0] - x);
        for (int i = 1; i < SlotX.Length; i++)
        {
            double candidate = Math.Abs(SlotX[i] - x);
            if (candidate < distance)
            {
                distance = candidate;
                best = i;
            }
        }
        return distance <= 0.08 ? best : -1;
    }

    private Mat Enhance(Mat frame)
    {
        using var region = new Mat(frame, GlyphArea);
        using var luma = new Mat();
        Cv2.CvtColor(region, luma, ColorConversionCodes.BGR2GRAY);
        using var background = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(13, 13));
        Cv2.Dilate(luma, background, kernel);
        using var luma32 = new Mat();
        using var background32 = new Mat();
        using var delta = new Mat();
        luma.ConvertTo(luma32, MatType.CV_32FC1);
        background.ConvertTo(background32, MatType.CV_32FC1);
        Cv2.Subtract(background32, luma32, delta);

        float high = Percentile(delta, 0.992f);
        if (high < 8)
            high = 8;
        using var ceiling = new Mat(delta.Size(), MatType.CV_32FC1, new Scalar(high));
        using var ink = new Mat();
        Cv2.Subtract(ceiling, delta, ink);
        Cv2.Divide(ink, ceiling, ink);
        using var gray8 = new Mat();
        ink.ConvertTo(gray8, MatType.CV_8UC1, 255);

        using var scaled = new Mat();
        Cv2.Resize(gray8, scaled, new Size(gray8.Width * 3, gray8.Height * 3), 0, 0, InterpolationFlags.Lanczos4);
        var bordered = new Mat();
        Cv2.CopyMakeBorder(scaled, bordered, 24, 24, 24, 24, BorderTypes.Constant, Scalar.All(255));
        Cv2.MeanStdDev(bordered, out Scalar mean, out _);
        bordered.ConvertTo(bordered, MatType.CV_8UC1, 1.4, mean.Val0 * (1 - 1.4));
        return bordered;
    }

    private double PanelScore(Mat frame)
    {
        using var region = new Mat(frame, PromptArea);
        Cv2.CvtColor(region, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.MatchTemplate(gray, prompt, score, TemplateMatchModes.CCoeffNormed);
        return score.At<float>(0, 0);
    }

    private static float Percentile(Mat values, float percentile)
    {
        int length = values.Rows * values.Cols;
        var data = new float[length];
        MarshalCopy(values, data);
        Array.Sort(data);
        int index = (int)Math.Clamp(Math.Round((length - 1) * percentile), 0, length - 1);
        return data[index];
    }

    private static void MarshalCopy(Mat values, float[] data)
    {
        System.Runtime.InteropServices.Marshal.Copy(values.Data, data, 0, data.Length);
    }

    private static double GlyphClarity(Mat image, double normalizedX)
    {
        int center = (int)Math.Round(normalizedX * image.Width);
        int half = (int)Math.Round(image.Width * 0.045);
        int x0 = Math.Clamp(center - half, 0, image.Width - 1);
        int x1 = Math.Clamp(center + half, x0 + 1, image.Width);
        int y0 = (int)(image.Height * 0.25);
        int y1 = Math.Clamp((int)(image.Height * 0.80), 1, image.Height);
        int width = x1 - x0;
        int height = y1 - y0;
        int top = height, bottom = -1, left = width, right = -1, marks = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (image.At<byte>(y0 + y, x0 + x) >= 120)
                    continue;
                marks++;
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
        }
        if (marks < 80 || bottom < top)
            return 0;
        int boxHeight = bottom - top + 1;
        int boxWidth = right - left + 1;
        if (boxHeight < 28 || boxWidth < 12 || boxWidth > boxHeight * 1.15)
            return 0;

        double sum = 0;
        int count = boxWidth * boxHeight;
        int solid = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                byte pixel = image.At<byte>(y0 + y, x0 + x);
                sum += pixel;
                if (pixel < 80)
                    solid++;
            }
        }
        double mean = sum / count;
        double variance = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                double delta = image.At<byte>(y0 + y, x0 + x) - mean;
                variance += delta * delta;
            }
        }
        return solid * Math.Sqrt(variance / count);
    }

    private static double? CornerRatio(Mat image, double normalizedX)
    {
        int center = (int)Math.Round(normalizedX * image.Width);
        int half = (int)Math.Round(image.Width * 0.055);
        int x0 = Math.Clamp(center - half, 0, image.Width - 1);
        int x1 = Math.Clamp(center + half, x0 + 1, image.Width);
        int y0 = (int)(image.Height * 0.18);
        int y1 = (int)(image.Height * 0.86);
        int width = x1 - x0;
        int height = y1 - y0;
        if (width < 8 || height < 8)
            return null;

        var ink = new bool[height, width];
        int marks = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (image.At<byte>(y0 + y, x0 + x) < 130)
                {
                    ink[y, x] = true;
                    marks++;
                }
            }
        }
        if (marks < 30)
            return null;

        RemoveSpecks(ink);
        if (!Tight(ink, out ink))
            return null;
        int top = (int)(ink.GetLength(0) * 0.58);
        var bottom = Slice(ink, top, ink.GetLength(0), 0, ink.GetLength(1));
        if (!Tight(bottom, out bottom))
            return null;

        const int size = 36;
        var small = new bool[size, size];
        int sourceHeight = bottom.GetLength(0);
        int sourceWidth = bottom.GetLength(1);
        for (int y = 0; y < size; y++)
        {
            int sy = Math.Min(sourceHeight - 1, y * sourceHeight / size);
            for (int x = 0; x < size; x++)
            {
                int sx = Math.Min(sourceWidth - 1, x * sourceWidth / size);
                small[y, x] = bottom[sy, sx];
            }
        }

        const int k = 7;
        double corners = (Patch(small, 0, k, 0, k) + Patch(small, 0, k, size - k, size)
            + Patch(small, size - k, size, 0, k) + Patch(small, size - k, size, size - k, size)) / 4;
        double mids = (Patch(small, 0, k, 14, 22) + Patch(small, size - k, size, 14, 22)
            + Patch(small, 14, 22, 0, k) + Patch(small, 14, 22, size - k, size)) / 4;
        return corners / (mids + 0.001);
    }

    private static void RemoveSpecks(bool[,] ink)
    {
        int height = ink.GetLength(0);
        int width = ink.GetLength(1);
        var keep = new bool[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!ink[y, x])
                    continue;
                int neighbors = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height)
                        continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        int nx = x + dx;
                        if (nx >= 0 && nx < width && ink[ny, nx])
                            neighbors++;
                    }
                }
                keep[y, x] = neighbors >= 2;
            }
        }
        Array.Copy(keep, ink, keep.Length);
    }

    private static bool Tight(bool[,] ink, out bool[,] cropped)
    {
        int height = ink.GetLength(0);
        int width = ink.GetLength(1);
        int top = height, bottom = -1, left = width, right = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!ink[y, x])
                    continue;
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
        }
        if (bottom < top || right - left < 5 || bottom - top < 5)
        {
            cropped = ink;
            return false;
        }
        cropped = Slice(ink, top, bottom + 1, left, right + 1);
        return true;
    }

    private static bool[,] Slice(bool[,] ink, int y0, int y1, int x0, int x1)
    {
        var slice = new bool[y1 - y0, x1 - x0];
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
                slice[y - y0, x - x0] = ink[y, x];
        }
        return slice;
    }

    private static double Patch(bool[,] ink, int y0, int y1, int x0, int x1)
    {
        int marks = 0;
        int total = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                total++;
                if (ink[y, x])
                    marks++;
            }
        }
        return total == 0 ? 0 : (double)marks / total;
    }

    private static bool IsSyllable(char ch) => ch is >= '\uAC00' and <= '\uD7A3';

    private static string Format(TimeSpan time) => time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private static SlotAccumulator[] NewSlots() => [new(), new(), new(), new(), new(), new()];

    private sealed class SlotAccumulator
    {
        private readonly Dictionary<char, int> counts = [];
        private readonly Dictionary<char, double> weights = [];
        private readonly Dictionary<char, List<double>> ratios = [];

        public void Add(char glyph, double? ratio, double clarity)
        {
            counts[glyph] = counts.GetValueOrDefault(glyph) + 1;
            if (clarity > 0)
                weights[glyph] = weights.GetValueOrDefault(glyph) + clarity;
            if (ratio is not double value)
                return;
            if (!ratios.TryGetValue(glyph, out List<double>? list))
                ratios[glyph] = list = [];
            list.Add(value);
        }

        public bool IsConfident()
        {
            if (!TryPick(out char winner, out char[] ordered) || !IsSyllable(winner))
                return false;
            int winnerVotes = counts[winner];
            int runnerUpVotes = ordered.Length > 1 ? counts[ordered[1]] : 0;
            return winnerVotes >= 3 && winnerVotes - runnerUpVotes >= 2;
        }

        public bool NeedsJongSample()
        {
            if (!TryPick(out char winner, out _) || !IsSyllable(winner))
                return false;
            int jong = (winner - 0xAC00) % 28;
            if (jong != JongseongIeung && jong != JongseongMieum)
                return false;
            return !ratios.TryGetValue(winner, out List<double>? samples) || samples.Count < 8;
        }

        public char Decide(out string note, out int margin)
        {
            if (!TryPick(out char winner, out char[] ordered))
            {
                note = "표 없음";
                margin = 0;
                return '?';
            }
            int votes = counts.Values.Sum();
            int winnerVotes = counts[winner];
            int runnerUpVotes = ordered.Length > 1 ? counts[ordered[1]] : 0;
            margin = winnerVotes - runnerUpVotes;
            string alternate = ordered.Length > 1 ? $" 다음 {ordered[1]} {runnerUpVotes}" : "";
            string selection = weights.Count > 0 && counts.OrderByDescending(pair => pair.Value).First().Key != winner
                ? " 선명프레임우선"
                : "";
            note = $"{winner} {winnerVotes}/{votes} 차{margin}{alternate}{selection}";
            if (!IsSyllable(winner))
                return winner;

            int jong = (winner - 0xAC00) % 28;
            if (jong != JongseongIeung && jong != JongseongMieum)
                return winner;
            if (!ratios.TryGetValue(winner, out List<double>? samples) || samples.Count < 8)
            {
                note += " 종성표본부족";
                return winner;
            }

            double lower = Percentile(samples, 0.25);
            double upper = Percentile(samples, 0.75);
            if (lower >= SquareCornerRatio && jong == JongseongIeung)
            {
                char corrected = WithJongseong(winner, JongseongMieum);
                note += $" 종성보정 {winner}→{corrected} 모서리비 p25 {lower:0.00}";
                return corrected;
            }
            if (upper <= RoundCornerRatio && jong == JongseongMieum)
            {
                char corrected = WithJongseong(winner, JongseongIeung);
                note += $" 종성보정 {winner}→{corrected} 모서리비 p75 {upper:0.00}";
                return corrected;
            }

            note += $" 종성유지 모서리비 p25 {lower:0.00}/p75 {upper:0.00}";
            return winner;
        }

        private bool TryPick(out char winner, out char[] ordered)
        {
            if (counts.Count == 0)
            {
                winner = '?';
                ordered = [];
                return false;
            }

            ordered = weights.Count > 0
                ? weights.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToArray()
                : counts.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToArray();
            winner = ordered[0];
            return true;
        }

        private static char WithJongseong(char glyph, int jong)
        {
            int code = glyph - 0xAC00;
            int cho = code / 588;
            int jung = code % 588 / 28;
            return (char)(0xAC00 + (cho * 588) + (jung * 28) + jong);
        }

        private static double Percentile(List<double> values, double percentile)
        {
            double[] sorted = values.OrderBy(value => value).ToArray();
            double position = (sorted.Length - 1) * percentile;
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper)
                return sorted[lower];
            double weight = position - lower;
            return (sorted[lower] * (1 - weight)) + (sorted[upper] * weight);
        }
    }

    private IGlyphSession OpenSession() =>
        OperatingSystem.IsWindows() ? new WindowsGlyphSession() : new VisionSession();

    private interface IGlyphSession : IDisposable
    {
        IEnumerable<(string Glyph, double X, double Y)> Read(string imagePath);
    }

    private sealed class WindowsGlyphSession : IGlyphSession
    {
#if WINDOWS10_0_19041_0_OR_GREATER
        private readonly OcrEngine engine;

        public WindowsGlyphSession()
        {
            Language? language = OcrEngine.AvailableRecognizerLanguages
                .FirstOrDefault(item => item.LanguageTag.StartsWith("ko", StringComparison.OrdinalIgnoreCase));
            engine = language is null
                ? throw new InvalidOperationException("Windows에 한국어 광학 문자 인식이 없습니다. 설정 > 시간 및 언어 > 언어 및 지역에서 한국어 옵션의 광학 문자 인식을 설치하세요.")
                : OcrEngine.TryCreateFromLanguage(language)
                    ?? throw new InvalidOperationException("Windows 한국어 문자 인식기를 시작하지 못했습니다.");
        }

        public IEnumerable<(string Glyph, double X, double Y)> Read(string imagePath) =>
            ReadAsync(imagePath).GetAwaiter().GetResult();

        public void Dispose()
        {
        }

        private async Task<List<(string Glyph, double X, double Y)>> ReadAsync(string imagePath)
        {
            await using FileStream file = File.OpenRead(imagePath);
            using var random = file.AsRandomAccessStream();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random);
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            OcrResult result = await engine.RecognizeAsync(bitmap);
            double width = bitmap.PixelWidth;
            double height = bitmap.PixelHeight;
            var found = new List<(string, double, double)>();
            foreach (OcrLine line in result.Lines)
            {
                foreach (OcrWord word in line.Words)
                {
                    var syllables = new List<char>();
                    foreach (char character in word.Text)
                    {
                        if (IsSyllable(character))
                            syllables.Add(character);
                    }
                    if (syllables.Count == 0 || width <= 0 || height <= 0)
                        continue;

                    Windows.Foundation.Rect rect = word.BoundingRect;
                    for (int i = 0; i < syllables.Count; i++)
                    {
                        double slice = rect.Width / syllables.Count;
                        double midX = (rect.X + (slice * i) + (slice / 2)) / width;
                        double midY = 1 - ((rect.Y + (rect.Height / 2)) / height);
                        found.Add((syllables[i].ToString(), midX, midY));
                    }
                }
            }
            return found;
        }
#else
        public WindowsGlyphSession() =>
            throw new InvalidOperationException("Windows 글자 인식은 Windows에서 빌드한 프로그램에서만 동작합니다.");

        public IEnumerable<(string Glyph, double X, double Y)> Read(string imagePath) =>
            throw new InvalidOperationException("Windows 글자 인식은 Windows에서 빌드한 프로그램에서만 동작합니다.");

        public void Dispose()
        {
        }
#endif
    }

    private sealed class VisionSession : IGlyphSession
    {
        private readonly Process process;
        private readonly StreamWriter input;
        private readonly StreamReader output;

        public VisionSession()
        {
            string binary = EnsureBinary();
            var start = new ProcessStartInfo(binary)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8
            };
            process = Process.Start(start) ?? throw new InvalidOperationException("문자 인식 보조 프로그램을 시작하지 못했습니다.");
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            input = process.StandardInput;
            output = process.StandardOutput;
            input.AutoFlush = true;
        }

        public IEnumerable<(string Glyph, double X, double Y)> Read(string imagePath)
        {
            input.WriteLine(imagePath);
            var glyphs = new List<(string, double, double)>();
            string? failure = null;
            while (output.ReadLine() is string line)
            {
                if (line == "END")
                {
                    if (failure is not null)
                        throw new InvalidOperationException(failure);
                    return glyphs;
                }
                if (line.StartsWith("ERR ", StringComparison.Ordinal))
                    failure = line[4..];
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 && parts[0] == "CH"
                    && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                {
                    glyphs.Add((parts[1], x, y));
                }
            }
            throw new InvalidOperationException("문자 인식 보조 프로그램이 응답 없이 끝났습니다.");
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    input.WriteLine("EXIT");
                    process.WaitForExit(1000);
                }
            }
            catch (IOException)
            {
            }
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.Dispose();
        }

        private static string EnsureBinary()
        {
            string root = Directory.GetCurrentDirectory();
            string source = Path.Combine(root, "tools", "HangulVision.swift");
            string binary = Path.Combine(root, ".work", "tools", "hangul-vision");
            if (!File.Exists(source))
                throw new FileNotFoundException("문자 인식 보조 소스 tools/HangulVision.swift 가 없습니다.", source);
            if (File.Exists(binary) && File.GetLastWriteTimeUtc(binary) >= File.GetLastWriteTimeUtc(source))
                return binary;

            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            var compile = Process.Start(new ProcessStartInfo("swiftc", $"-O -o \"{binary}\" \"{source}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("swiftc를 시작하지 못했습니다.");
            string error = compile.StandardError.ReadToEnd();
            compile.WaitForExit();
            if (compile.ExitCode != 0)
                throw new InvalidOperationException($"문자 인식 보조 프로그램 컴파일에 실패했습니다. {error}");
            return binary;
        }
    }
}

sealed record CharacterReading(string FileName, int Attempt, TimeSpan DetectedAt, TimeSpan ReadFrom, TimeSpan ReadTo, string Candidate, string Detail);

enum DigitWatch { Stable, Ignore, Restart }
