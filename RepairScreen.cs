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
    private static readonly Rect QuantityArea = new(1620, 129, 118, 30);

    private readonly Mat panel;
    private readonly Mat crack;
    private readonly Mat[] brickUsed;
    private readonly Mat brickRow;
    private readonly Mat nextRow;
    private readonly Mat two;
    private readonly Mat crop = new();
    private readonly Mat match = new();

    public RepairScreen()
    {
        string templates = Path.Combine(AppContext.BaseDirectory, "Templates");
        panel = LoadImage(Path.Combine(templates, "repair-panel.png"), PanelArea);
        crack = LoadMask(Path.Combine(templates, "crack.png"), DialogArea, TextThreshold, ThresholdTypes.BinaryInv);
        brickUsed =
        [
            LoadMask(Path.Combine(templates, "brick-broke.png"), DialogArea, TextThreshold, ThresholdTypes.BinaryInv),
            LoadMask(Path.Combine(templates, "repairing.png"), DialogArea, TextThreshold, ThresholdTypes.BinaryInv)
        ];
        brickRow = LoadMask(Path.Combine(templates, "brick-row.png"), InventoryRowArea, BrightTextThreshold, ThresholdTypes.Binary);
        nextRow = LoadMask(Path.Combine(templates, "next-row.png"), InventoryRowArea, BrightTextThreshold, ThresholdTypes.Binary);
        two = LoadMask(Path.Combine(templates, "brick-count-two.png"), QuantityArea, BrightTextThreshold, ThresholdTypes.Binary);
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

    public BrickStock ReadStock(Mat frame)
    {
        ReadArea(frame, InventoryRowArea);
        Cv2.Threshold(crop, crop, BrightTextThreshold, 255, ThresholdTypes.Binary);
        if (Similarity(crop, nextRow) >= 0.84)
            return BrickStock.Absent;
        if (Similarity(crop, brickRow) < 0.84)
            return BrickStock.Unknown;

        ReadArea(frame, QuantityArea);
        Cv2.Threshold(crop, crop, BrightTextThreshold, 255, ThresholdTypes.Binary);
        int brightPixels = Cv2.CountNonZero(crop);
        if (brightPixels < 10)
            return BrickStock.One;
        if (Similarity(crop, two) >= 0.90)
            return BrickStock.Two;
        return BrickStock.QuantityVisible;
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
        nextRow.Dispose();
        two.Dispose();
        crop.Dispose();
        match.Dispose();
    }
}
