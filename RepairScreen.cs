using OpenCvSharp;

enum RepairDialog
{
    None,
    Crack,
    BrickUsed
}

enum BrickStock
{
    Unknown,
    Absent,
    One,
    Two,
    QuantityVisible
}

sealed class RepairScreen : IDisposable
{
    private const double DialogThreshold = 0.84;
    private const int TextThreshold = 95;
    private const int BrightTextThreshold = 112;

    private static readonly Rect PanelArea = new(636, 266, 102, 52);
    private static readonly Rect DialogArea = new(754, 345, 300, 58);
    private static readonly Rect InventoryRowArea = new(1500, 129, 120, 30);
    private static readonly Rect BrickNameCrop = new(68, 0, 52, 30);
    private static readonly Rect InventoryNameScanArea = new(1545, 110, 110, 550);
    private static readonly Rect InventoryInteriorArea = new(1500, 129, 240, 280);
    private static readonly Rect QuantityArea = new(1620, 129, 118, 30);

    private readonly Mat panel;
    private readonly Mat crack;
    private readonly Mat[] brickUsed;
    private readonly Mat brickRow;
    private readonly Mat brickName;
    private readonly Mat brickNameGray;
    private readonly Mat nextRow;
    private readonly Mat two;
    private readonly Mat? promptGray;
    private readonly Mat? promptMask;
    private readonly Mat? promptHalf;
    private readonly Mat crop = new();
    private readonly Mat inventoryMask = new();
    private readonly Mat match = new();
    private readonly Mat fullGray = new();
    private readonly Mat halfGray = new();
    private readonly Mat halfMatch = new();
    private readonly Mat promptRoiMask = new();
    private readonly Mat promptRoiMatch = new();

    public RepairScreen()
    {
        string templates = BundledFiles.TemplateDirectory;
        panel = LoadImage(Path.Combine(templates, "repair-panel.png"), PanelArea);
        crack = LoadMask(Path.Combine(templates, "crack.png"), DialogArea, TextThreshold, ThresholdTypes.BinaryInv);
        brickUsed =
        [
            LoadMask(Path.Combine(templates, "brick-broke.png"), DialogArea, TextThreshold, ThresholdTypes.BinaryInv),
            LoadMask(Path.Combine(templates, "repairing.png"), DialogArea, TextThreshold, ThresholdTypes.BinaryInv)
        ];
        Mat brickRowRaw = LoadImage(Path.Combine(templates, "brick-row.png"), InventoryRowArea);
        using (var grayNameSlice = new Mat(brickRowRaw, BrickNameCrop))
            brickNameGray = grayNameSlice.Clone();
        Cv2.Threshold(brickRowRaw, brickRowRaw, BrightTextThreshold, 255, ThresholdTypes.Binary);
        brickRow = brickRowRaw;
        using var nameSlice = new Mat(brickRow, BrickNameCrop);
        brickName = nameSlice.Clone();
        nextRow = LoadMask(Path.Combine(templates, "next-row.png"), InventoryRowArea, BrightTextThreshold, ThresholdTypes.Binary);
        two = LoadMask(Path.Combine(templates, "brick-count-two.png"), QuantityArea, BrightTextThreshold, ThresholdTypes.Binary);

        string promptPath = Path.Combine(templates, "character-prompt.png");
        if (File.Exists(promptPath))
        {
            using Mat promptRaw = Cv2.ImDecode(File.ReadAllBytes(promptPath), ImreadModes.Grayscale);
            if (!promptRaw.Empty() && promptRaw.Width >= 450 && promptRaw.Height >= 24)
            {
                using var banner = new Mat(promptRaw, new Rect(0, 0, 450, 24));
                promptGray = banner.Clone();
                promptMask = new Mat();
                Cv2.Threshold(promptGray, promptMask, TextThreshold, 255, ThresholdTypes.BinaryInv);
                promptHalf = new Mat();
                Cv2.Resize(promptGray, promptHalf, new Size(225, 12), 0, 0, InterpolationFlags.Area);
            }
        }
    }

    public bool IsVerificationPromptOpen(Mat frame)
    {
        if (promptGray is null || promptMask is null || promptHalf is null
            || frame.Width != 1920 || frame.Height != 1080)
        {
            return false;
        }

        Cv2.CvtColor(frame, fullGray, ColorConversionCodes.BGR2GRAY);
        Cv2.Resize(fullGray, halfGray, new Size(960, 540), 0, 0, InterpolationFlags.Area);
        Cv2.MatchTemplate(halfGray, promptHalf, halfMatch, TemplateMatchModes.CCoeffNormed);

        for (int peak = 0; peak < 3; peak++)
        {
            Cv2.MinMaxLoc(halfMatch, out _, out double coarseScore, out _, out Point coarseLoc);
            if (coarseScore < 0.42)
                break;

            int rx = Math.Clamp((coarseLoc.X * 2) - 24, 0, 1920 - promptGray.Width);
            int ry = Math.Clamp((coarseLoc.Y * 2) - 16, 0, 1080 - promptGray.Height);
            int rw = Math.Min(promptGray.Width + 48, 1920 - rx);
            int rh = Math.Min(promptGray.Height + 32, 1080 - ry);

            using var roi = new Mat(fullGray, new Rect(rx, ry, rw, rh));
            Cv2.MatchTemplate(roi, promptGray, promptRoiMatch, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(promptRoiMatch, out _, out double grayScore, out _, out _);
            if (grayScore >= 0.65)
                return true;

            Cv2.Threshold(roi, promptRoiMask, TextThreshold, 255, ThresholdTypes.BinaryInv);
            Cv2.MatchTemplate(promptRoiMask, promptMask, promptRoiMatch, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(promptRoiMatch, out _, out double maskScore, out _, out _);
            if (maskScore >= 0.65)
                return true;

            int sx = Math.Max(0, coarseLoc.X - 24);
            int sy = Math.Max(0, coarseLoc.Y - 8);
            int sw = Math.Min(halfMatch.Width - sx, 49);
            int sh = Math.Min(halfMatch.Height - sy, 17);
            if (sw > 0 && sh > 0)
            {
                using var patch = new Mat(halfMatch, new Rect(sx, sy, sw, sh));
                patch.SetTo(Scalar.All(-1.0));
            }
        }

        return false;
    }

    public bool IsDialogPanelOpen(Mat frame)
    {
        if (frame.Width != 1920 || frame.Height != 1080)
            return false;

        ReadArea(frame, PanelArea);
        return Similarity(crop, panel) >= 0.86;
    }

    public RepairDialog ReadDialog(Mat frame)
    {
        if (frame.Width != 1920 || frame.Height != 1080)
            throw new InvalidDataException("이 분석기는 제공 영상과 같은 1920×1080 화면 배치를 사용합니다.");

        ReadArea(frame, PanelArea);
        if (Similarity(crop, panel) < 0.86)
            return RepairDialog.None;

        ReadArea(frame, DialogArea);
        Cv2.Threshold(crop, crop, TextThreshold, 255, ThresholdTypes.BinaryInv);
        if (Similarity(crop, crack) >= DialogThreshold)
            return RepairDialog.Crack;
        foreach (Mat template in brickUsed)
        {
            if (Similarity(crop, template) >= DialogThreshold)
                return RepairDialog.BrickUsed;
        }
        return RepairDialog.None;
    }

    public bool IsInventoryVisible(Mat frame)
    {
        if (frame.Width != 1920 || frame.Height != 1080)
            return false;

        ReadArea(frame, InventoryInteriorArea);
        Cv2.Threshold(crop, inventoryMask, 25, 255, ThresholdTypes.BinaryInv);
        double darkRatio = Cv2.CountNonZero(inventoryMask) / (double)(InventoryInteriorArea.Width * InventoryInteriorArea.Height);
        return darkRatio is >= 0.45 and < 0.96;
    }

    public BrickStock ReadStock(Mat frame)
    {
        ReadArea(frame, InventoryNameScanArea);
        Cv2.MatchTemplate(crop, brickNameGray, match, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(match, out _, out double grayScore, out _, out Point grayLoc);

        int foundY = -1;
        if (grayScore >= 0.74)
        {
            foundY = grayLoc.Y;
        }
        else
        {
            Cv2.Threshold(crop, crop, BrightTextThreshold, 255, ThresholdTypes.Binary);
            Cv2.MatchTemplate(crop, brickName, match, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(match, out _, out double brickScore, out _, out Point brickLoc);
            if (brickScore >= 0.76)
                foundY = brickLoc.Y;
        }

        if (foundY >= 0)
        {
            int rowY = Math.Clamp(InventoryNameScanArea.Y + foundY, 0, 1080 - QuantityArea.Height);
            ReadArea(frame, new Rect(QuantityArea.X, rowY, QuantityArea.Width, QuantityArea.Height));
            Cv2.Threshold(crop, crop, BrightTextThreshold, 255, ThresholdTypes.Binary);
            int brightPixels = Cv2.CountNonZero(crop);
            if (brightPixels < 10)
                return BrickStock.One;
            if (Similarity(crop, two) >= 0.90)
                return BrickStock.Two;
            return BrickStock.QuantityVisible;
        }

        ReadArea(frame, InventoryRowArea);
        Cv2.Threshold(crop, crop, BrightTextThreshold, 255, ThresholdTypes.Binary);
        if (Similarity(crop, nextRow) >= 0.84 || IsInventoryVisible(frame))
            return BrickStock.Absent;
        return BrickStock.Unknown;
    }

    private void ReadArea(Mat frame, Rect area)
    {
        using var region = new Mat(frame, area);
        Cv2.CvtColor(region, crop, ColorConversionCodes.BGR2GRAY);
    }

    private double Similarity(Mat image, Mat template)
    {
        Cv2.MatchTemplate(image, template, match, TemplateMatchModes.CCoeffNormed);
        return match.At<float>(0, 0);
    }

    private static Mat LoadImage(string path, Rect area)
    {
        var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Grayscale);
        if (image.Empty() || image.Width != area.Width || image.Height != area.Height)
        {
            image.Dispose();
            throw new InvalidDataException($"{Path.GetFileName(path)} 이미지 크기는 {area.Width}×{area.Height}이어야 합니다.");
        }
        return image;
    }

    private static Mat LoadMask(string path, Rect area, int threshold, ThresholdTypes mode)
    {
        Mat image = LoadImage(path, area);
        Cv2.Threshold(image, image, threshold, 255, mode);
        int pixels = Cv2.CountNonZero(image);
        if (pixels == 0 || pixels == area.Width * area.Height)
        {
            image.Dispose();
            throw new InvalidDataException($"{Path.GetFileName(path)}에서 문구 형태를 읽을 수 없습니다.");
        }
        return image;
    }

    public void Dispose()
    {
        foreach (Mat template in brickUsed)
            template.Dispose();
        panel.Dispose();
        crack.Dispose();
        brickRow.Dispose();
        brickName.Dispose();
        brickNameGray.Dispose();
        nextRow.Dispose();
        two.Dispose();
        promptGray?.Dispose();
        promptMask?.Dispose();
        promptHalf?.Dispose();
        crop.Dispose();
        inventoryMask.Dispose();
        match.Dispose();
        fullGray.Dispose();
        halfGray.Dispose();
        halfMatch.Dispose();
        promptRoiMask.Dispose();
        promptRoiMatch.Dispose();
    }
}
