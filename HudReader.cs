using OpenCvSharp;

sealed class HudReader : IDisposable
{
    private static readonly Rect TitleArea = new(700, 14, 200, 32);
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
    private readonly Mat score = new();
    private readonly (string Name, Mat Image)[] titles;
    private readonly Mat[] digits;

    public HudReader()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Templates", "hud");
        titles =
        [
            LoadTitle(root, "title-guknae.png", WallAtlas.Guknae),
            LoadTitle(root, "title-jangan.png", WallAtlas.Jangan),
            LoadTitle(root, "title-smith.png", WallAtlas.Smith),
            LoadTitle(root, "title-wall3.png", WallAtlas.Wall3)
        ];
        digits = new Mat[10];
        for (int i = 0; i < digits.Length; i++)
        {
            digits[i] = LoadGray(Path.Combine(root, $"digit-{i}.png"));
            if (digits[i].Width != 16 || digits[i].Height != 16)
                throw new InvalidDataException($"digit-{i}.png 크기가 좌표 숫자와 다릅니다.");
        }
    }

    public void Read(Mat frame, out string? map, out int? x, out int? y)
    {
        map = null;
        x = null;
        y = null;
        if (frame.Width != 1920 || frame.Height != 1080)
            return;

        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        map = ReadTitle();
        if (TryCoordinate(out int readX, out int readY))
        {
            x = readX;
            y = readY;
        }
    }

    public void Dispose()
    {
        gray.Dispose();
        score.Dispose();
        foreach ((string _, Mat image) in titles)
            image.Dispose();
        foreach (Mat digit in digits)
            digit.Dispose();
    }

    private string? ReadTitle()
    {
        using var region = new Mat(gray, TitleArea);
        string? bestName = null;
        double best = 0;
        double second = 0;
        foreach ((string name, Mat image) in titles)
        {
            double value = Match(region, image);
            if (value > best)
            {
                second = best;
                best = value;
                bestName = name;
            }
            else if (value > second)
            {
                second = value;
            }
        }
        return best >= 0.90 && best - second >= 0.12 ? bestName : null;
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
            if (!TryDigit(DigitSlots[i], out int digit))
                return false;
            value = i == 4 ? digit : (value * 10) + digit;
        }
        y = value;
        return true;
    }

    private bool TryDigit(Rect area, out int digit)
    {
        digit = 0;
        using var region = new Mat(gray, area);
        int best = -1;
        double scoreBest = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            double value = Match(region, digits[i]);
            if (value > scoreBest)
            {
                scoreBest = value;
                best = i;
            }
        }
        if (best < 0 || scoreBest < 0.75)
            return false;
        digit = best;
        return true;
    }

    private double Match(Mat image, Mat template)
    {
        Cv2.MatchTemplate(image, template, score, TemplateMatchModes.CCoeffNormed);
        return score.At<float>(0, 0);
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
