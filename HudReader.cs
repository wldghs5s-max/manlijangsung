using OpenCvSharp;

enum SmithyDialogState
{
    None,
    MainMenu,
    ItemList,
    QuantityPrompt
}

sealed class HudReader : IDisposable
{
    private static readonly Rect TitleArea = new(700, 14, 200, 32);
    private static readonly Rect WallTextBand = new(0, 0, 150, 16);
    private const int WallTextThreshold = 165;
    private static readonly Rect ReturnConfirmTextArea = new(692, 238, 308, 39);
    private static readonly Rect ReturnConfirmListArea = new(760, 460, 380, 220);
    private static readonly Rect InventoryInteriorArea = new(1500, 129, 240, 280);
    private static readonly Rect WorldMapCheckArea = new(300, 100, 1200, 800);
    private static readonly Rect CenterModalArea = new(600, 260, 580, 460);
    private static readonly Rect DialogTopBarArea = new(700, 290, 500, 12);
    private static readonly Rect DialogLowerButtonArea = new(720, 758, 120, 30);
    private static readonly Rect DialogMidButtonArea = new(720, 665, 120, 30);
    private static readonly Rect QuantityParchmentArea = new(880, 610, 160, 30);
    private static readonly Rect ItemListDarkArea = new(900, 520, 300, 140);
    private static readonly Rect[] DigitSlots =
    [
        new(1660, 1034, 16, 16),
        new(1680, 1034, 16, 16),
        new(1700, 1034, 16, 16),
        new(1720, 1034, 16, 16),
        new(1765, 1034, 16, 16),
        new(1785, 1034, 16, 16),
        new(1805, 1034, 16, 16),
        new(1825, 1034, 16, 16)
    ];

    private readonly Mat gray = new();
    private readonly Mat mask = new();
    private readonly Mat digitMask = new();
    private readonly Mat score = new();
    private readonly byte[] modalRowBuffer = new byte[CenterModalArea.Width * 3];
    private readonly (string Name, Mat Image)[] titles;
    private readonly (string Name, Mat Band)[] titleBands;
    private readonly (string Name, Mat Digit)[] wallDigits;
    private readonly Rect wallDigitArea;
    private readonly Mat returnConfirmMask;
    private readonly Mat[] digits;
    private readonly HashSet<string> supportedWalls = new(StringComparer.Ordinal);

    public IReadOnlySet<string> SupportedWalls => supportedWalls;
    public string LastDiagnostic { get; private set; } = "";

    public HudReader()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Templates", "hud");
        var loadedTitles = new List<(string Name, Mat Image)>
        {
            LoadTitle(root, "title-guknae.png", WallAtlas.Guknae),
            LoadTitle(root, "title-jangan.png", WallAtlas.Jangan),
            LoadTitle(root, "title-smith.png", WallAtlas.Smith),
            LoadTitle(root, "title-wall3.png", WallAtlas.Wall3)
        };
        TryLoadOptionalTitle(root, "title-wall1.png", WallAtlas.Wall1, loadedTitles);
        TryLoadOptionalTitle(root, "title-wall2.png", WallAtlas.Wall2, loadedTitles);
        TryLoadOptionalTitle(root, "title-wall4.png", WallAtlas.Wall4, loadedTitles);
        titles = [.. loadedTitles];
        titleBands = new (string Name, Mat Band)[titles.Length];
        for (int i = 0; i < titles.Length; i++)
        {
            using var bandSlice = new Mat(titles[i].Image, WallTextBand);
            titleBands[i] = (titles[i].Name, bandSlice.Clone());
        }

        var wallTitles = new List<(string Name, Mat Image)>();
        foreach ((string name, Mat image) in titles)
        {
            if (WallAtlas.IsWall(name))
            {
                supportedWalls.Add(name);
                wallTitles.Add((name, image));
            }
        }

        if (wallTitles.Count >= 2)
        {
            wallDigitArea = ComputeWallDigitArea(wallTitles);
            wallDigits = new (string Name, Mat Digit)[wallTitles.Count];
            for (int i = 0; i < wallTitles.Count; i++)
            {
                using var slice = new Mat(wallTitles[i].Image, wallDigitArea);
                wallDigits[i] = (wallTitles[i].Name, slice.Clone());
            }
        }
        else
        {
            wallDigitArea = WallTextBand;
            wallDigits = [];
        }

        digits = new Mat[10];
        for (int i = 0; i < digits.Length; i++)
        {
            digits[i] = LoadGray(Path.Combine(root, $"digit-{i}.png"));
            if (digits[i].Width != 16 || digits[i].Height != 16)
                throw new InvalidDataException($"digit-{i}.png 크기가 좌표 숫자와 다릅니다.");
        }

        returnConfirmMask = LoadGray(Path.Combine(root, "return-confirm.png"));
        if (returnConfirmMask.Width != ReturnConfirmTextArea.Width || returnConfirmMask.Height != ReturnConfirmTextArea.Height)
            throw new InvalidDataException("return-confirm.png 크기가 귀환 확인 문구 영역과 다릅니다.");
        Cv2.Threshold(returnConfirmMask, returnConfirmMask, 95, 255, ThresholdTypes.BinaryInv);
    }

    public void Read(Mat frame, out string? map, out int? x, out int? y)
    {
        map = null;
        x = null;
        y = null;
        LastDiagnostic = "";
        if (frame.Width != 1920 || frame.Height != 1080)
        {
            LastDiagnostic = $"해상도불일치({frame.Width}x{frame.Height})";
            return;
        }

        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        map = ReadTitle();
        if (TryCoordinate(out int readX, out int readY))
        {
            x = readX;
            y = readY;
        }
    }

    public bool HasCenterParchmentDialog(Mat frame)
    {
        if (frame.Width != 1920 || frame.Height != 1080 || frame.Type() != MatType.CV_8UC3)
            return false;

        int stride = (int)frame.Step();
        int rowBytes = CenterModalArea.Width * 3;
        int parchCount = 0;
        int totalSamples = 0;

        for (int dy = 0; dy < CenterModalArea.Height; dy += 2)
        {
            nint rowPtr = frame.Data + ((CenterModalArea.Y + dy) * stride) + (CenterModalArea.X * 3);
            System.Runtime.InteropServices.Marshal.Copy(rowPtr, modalRowBuffer, 0, rowBytes);
            for (int dx = 0; dx < CenterModalArea.Width; dx += 2)
            {
                int off = dx * 3;
                int b = modalRowBuffer[off];
                int g = modalRowBuffer[off + 1];
                int r = modalRowBuffer[off + 2];
                if (r >= 145 && g >= 95 && r > g + 15 && g > b + 20 && (r - b) >= 50)
                    parchCount++;
                totalSamples++;
            }
        }

        return totalSamples > 0 && (double)parchCount / totalSamples >= 0.25;
    }

    public bool IsWorldMap(Mat frame)
    {
        if (frame.Width != 1920 || frame.Height != 1080)
            return false;

        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        if (ReadTitle() is not null || TryCoordinate(out _, out _))
            return false;

        using var inv = new Mat(gray, InventoryInteriorArea);
        Cv2.Threshold(inv, mask, 25, 255, ThresholdTypes.BinaryInv);
        double invDarkRatio = Cv2.CountNonZero(mask) / (double)(InventoryInteriorArea.Width * InventoryInteriorArea.Height);
        if (invDarkRatio >= 0.25)
            return false;

        using var world = new Mat(gray, WorldMapCheckArea);
        return Cv2.Mean(world).Val0 >= 40;
    }

    public SmithyDialogState ReadSmithyDialog(Mat frame)
    {
        if (frame.Width != 1920 || frame.Height != 1080)
            return SmithyDialogState.None;

        Scalar topBar = MeanColor(frame, DialogTopBarArea);
        Scalar lowerBar = MeanColor(frame, DialogLowerButtonArea);
        if (topBar.Val2 >= 140 && lowerBar.Val2 >= 100)
        {
            using var listGray = new Mat();
            using var listCrop = new Mat(frame, ItemListDarkArea);
            Cv2.CvtColor(listCrop, listGray, ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(listGray, mask, 45, 255, ThresholdTypes.BinaryInv);
            double listDarkRatio = Cv2.CountNonZero(mask) / (double)(ItemListDarkArea.Width * ItemListDarkArea.Height);
            return listDarkRatio >= 0.85 ? SmithyDialogState.ItemList : SmithyDialogState.MainMenu;
        }

        if (topBar.Val2 < 100 && lowerBar.Val2 < 50)
        {
            Scalar qtyParchment = MeanColor(frame, QuantityParchmentArea);
            Scalar midBar = MeanColor(frame, DialogMidButtonArea);
            if (qtyParchment.Val2 >= 140 && midBar.Val2 >= 120)
                return SmithyDialogState.QuantityPrompt;
        }

        return SmithyDialogState.None;
    }

    public bool IsReturnConfirmDialog(Mat frame)
    {
        if (frame.Width != 1920 || frame.Height != 1080)
            return false;

        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        using var listCrop = new Mat(gray, ReturnConfirmListArea);
        Cv2.Threshold(listCrop, mask, 45, 255, ThresholdTypes.BinaryInv);
        double listDarkRatio = Cv2.CountNonZero(mask) / (double)(ReturnConfirmListArea.Width * ReturnConfirmListArea.Height);
        if (listDarkRatio < 0.75)
            return false;

        using var textCrop = new Mat(gray, ReturnConfirmTextArea);
        Cv2.Threshold(textCrop, mask, 95, 255, ThresholdTypes.BinaryInv);
        return Match(mask, returnConfirmMask) >= 0.55;
    }

    public void Dispose()
    {
        gray.Dispose();
        mask.Dispose();
        digitMask.Dispose();
        score.Dispose();
        returnConfirmMask.Dispose();
        foreach ((string _, Mat image) in titles)
            image.Dispose();
        foreach ((string _, Mat band) in titleBands)
            band.Dispose();
        foreach ((string _, Mat wallDigit) in wallDigits)
            wallDigit.Dispose();
        foreach (Mat digit in digits)
            digit.Dispose();
    }

    private string? ReadTitle()
    {
        var rawBand = new Rect(TitleArea.X + WallTextBand.X, TitleArea.Y + WallTextBand.Y, WallTextBand.Width, WallTextBand.Height);
        Rect bandSearch = ExpandClamped(rawBand, 4, gray.Width, gray.Height);
        using var bandRegion = new Mat(gray, bandSearch);

        string? bestWallName = null;
        double bestWallScore = 0;
        Point bestWallLoc = default;
        string? bestNonWallName = null;
        double bestNonWallScore = 0;
        double secondNonWallScore = 0;

        foreach ((string name, Mat bandTemplate) in titleBands)
        {
            double value = MatchMax(bandRegion, bandTemplate, out Point loc);
            if (WallAtlas.IsWall(name))
            {
                if (value > bestWallScore)
                {
                    bestWallScore = value;
                    bestWallName = name;
                    bestWallLoc = loc;
                }
            }
            else if (value > bestNonWallScore)
            {
                secondNonWallScore = bestNonWallScore;
                bestNonWallScore = value;
                bestNonWallName = name;
            }
            else if (value > secondNonWallScore)
            {
                secondNonWallScore = value;
            }
        }

        if (bestNonWallScore >= 0.80 && bestNonWallScore - Math.Max(secondNonWallScore, bestWallScore) >= 0.12)
            return bestNonWallName;

        if (bestWallScore >= 0.80 && bestWallScore - bestNonWallScore >= 0.12)
        {
            if (wallDigits.Length <= 1)
                return bestWallName;

            var matchedDigit = new Rect(
                bandSearch.X + bestWallLoc.X + wallDigitArea.X,
                bandSearch.Y + bestWallLoc.Y + wallDigitArea.Y,
                wallDigitArea.Width,
                wallDigitArea.Height);
            Rect digitSearch = ExpandClamped(matchedDigit, 2, gray.Width, gray.Height);
            using var digitSlice = new Mat(gray, digitSearch);

            string? digitBestName = null;
            double digitBest = 0;
            double digitSecond = 0;
            foreach ((string name, Mat template) in wallDigits)
            {
                double value = MatchMax(digitSlice, template);
                if (value > digitBest)
                {
                    digitSecond = digitBest;
                    digitBest = value;
                    digitBestName = name;
                }
                else if (value > digitSecond)
                {
                    digitSecond = value;
                }
            }

            if (digitBest >= 0.80 && digitBest - digitSecond >= 0.08)
                return digitBestName;

            LastDiagnostic = $"성벽숫자:{digitBestName ?? "-"}({digitBest:0.00}/{digitSecond:0.00})";
            return null;
        }

        string topName = bestWallScore >= bestNonWallScore ? (bestWallName ?? "-") : (bestNonWallName ?? "-");
        double topScore = Math.Max(bestWallScore, bestNonWallScore);
        double runnerUp = bestWallScore >= bestNonWallScore ? bestNonWallScore : Math.Max(secondNonWallScore, bestWallScore);
        LastDiagnostic = $"제목:{topName}({topScore:0.00}/{runnerUp:0.00})";
        return null;
    }

    private bool TryCoordinate(out int x, out int y)
    {
        x = 0;
        y = 0;
        int value = 0;
        for (int i = 0; i < DigitSlots.Length; i++)
        {
            if (i == 4)
                x = value;
            if (!TryDigit(DigitSlots[i], i, out int digit))
                return false;
            value = i == 4 ? digit : (value * 10) + digit;
        }
        y = value;
        return true;
    }

    private bool TryDigit(Rect area, int slotIndex, out int digit)
    {
        digit = 0;
        Rect search = ExpandClamped(area, 3, gray.Width, gray.Height);
        using var region = new Mat(gray, search);
        int best = -1;
        double scoreBest = 0;
        double scoreSecond = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            double value = MatchMax(region, digits[i]);
            if (value > scoreBest)
            {
                scoreSecond = scoreBest;
                scoreBest = value;
                best = i;
            }
            else if (value > scoreSecond)
            {
                scoreSecond = value;
            }
        }
        if (best < 0 || (scoreBest < 0.75 && !(scoreBest >= 0.62 && scoreBest - scoreSecond >= 0.08)))
        {
            string coordDiag = $"좌표{slotIndex}:{best}({scoreBest:0.00}/{scoreSecond:0.00})";
            LastDiagnostic = string.IsNullOrEmpty(LastDiagnostic) ? coordDiag : $"{LastDiagnostic},{coordDiag}";
            return false;
        }
        digit = best;
        return true;
    }

    private double Match(Mat image, Mat template)
    {
        Cv2.MatchTemplate(image, template, score, TemplateMatchModes.CCoeffNormed);
        return score.At<float>(0, 0);
    }

    private double MatchMax(Mat searchRegion, Mat template) =>
        MatchMax(searchRegion, template, out _);

    private double MatchMax(Mat searchRegion, Mat template, out Point maxLoc)
    {
        Cv2.MatchTemplate(searchRegion, template, score, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(score, out _, out double maxVal, out _, out maxLoc);
        return maxVal;
    }

    private static Rect ExpandClamped(Rect area, int padding, int maxWidth, int maxHeight)
    {
        int x = Math.Max(0, area.X - padding);
        int y = Math.Max(0, area.Y - padding);
        int right = Math.Min(maxWidth, area.X + area.Width + padding);
        int bottom = Math.Min(maxHeight, area.Y + area.Height + padding);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static Rect ComputeWallDigitArea(IReadOnlyList<(string Name, Mat Image)> wallTitles)
    {
        var binaries = new Mat[wallTitles.Count];
        using var unionDiff = new Mat(WallTextBand.Height, WallTextBand.Width, MatType.CV_8UC1, Scalar.All(0));
        using var diff = new Mat();
        try
        {
            for (int i = 0; i < wallTitles.Count; i++)
            {
                using var band = new Mat(wallTitles[i].Image, WallTextBand);
                binaries[i] = new Mat();
                Cv2.Threshold(band, binaries[i], WallTextThreshold, 255, ThresholdTypes.Binary);
            }

            for (int i = 0; i < binaries.Length; i++)
            {
                for (int j = i + 1; j < binaries.Length; j++)
                {
                    Cv2.Absdiff(binaries[i], binaries[j], diff);
                    Cv2.BitwiseOr(unionDiff, diff, unionDiff);
                }
            }

            Rect box = Cv2.BoundingRect(unionDiff);
            if (box.Width <= 0 || box.Height <= 0)
                return WallTextBand;

            int x = Math.Max(0, box.X - 2);
            int y = Math.Max(0, box.Y - 2);
            int width = Math.Min(WallTextBand.Width - x, box.Width + 4);
            int height = Math.Min(WallTextBand.Height - y, box.Height + 4);
            return new Rect(x, y, width, height);
        }
        finally
        {
            foreach (Mat? item in binaries)
                item?.Dispose();
        }
    }

    private static Scalar MeanColor(Mat frame, Rect area)
    {
        using var crop = new Mat(frame, area);
        return Cv2.Mean(crop);
    }

    private static void TryLoadOptionalTitle(string root, string file, string name, List<(string Name, Mat Image)> target)
    {
        string path = Path.Combine(root, file);
        if (File.Exists(path))
            target.Add(LoadTitle(root, file, name));
    }

    private static (string, Mat) LoadTitle(string root, string file, string name)
    {
        Mat image = LoadGray(Path.Combine(root, file));
        if (image.Width != TitleArea.Width || image.Height != TitleArea.Height)
            throw new InvalidDataException($"{file} 크기가 지도 이름 영역과 다릅니다.");
        return (name, image);
    }

    private static Mat LoadGray(string path) =>
        Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Grayscale);
}
