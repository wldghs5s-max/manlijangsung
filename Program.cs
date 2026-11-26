using System.Text;
using OpenCvSharp;

Console.OutputEncoding = Encoding.UTF8;

const int brickPurchaseQuantity = 100;
const int questTarget = 10_000;
const string defaultRecording = "public/만리장성수리.mp4";
const string usage = "사용법: dotnet run -- [--verify] [--pilot] [영상.mp4]";

bool verify = false;
bool pilot = false;
string? recording = null;
foreach (string arg in args)
{
    if (arg is "--help" or "-h")
    {
        Console.WriteLine(usage);
        return 0;
    }
    if (arg == "--verify")
    {
        verify = true;
        continue;
    }
    if (arg == "--pilot")
    {
        pilot = true;
        continue;
    }
    if (arg.StartsWith('-') || recording is not null)
    {
        Console.Error.WriteLine(usage);
        return 2;
    }
    recording = arg;
}

try
{
    if (pilot)
        return RunPilot();

    string path = Path.GetFullPath(recording ?? defaultRecording);
    if (!File.Exists(path))
        throw new FileNotFoundException($"영상 파일을 찾을 수 없습니다. {path}");

    using var video = File.OpenRead(path);
    using var capture = new VideoCapture(video, VideoCaptureAPIs.FFMPEG, Array.Empty<int>());
    if (!capture.IsOpened())
        throw new InvalidDataException($"영상을 열 수 없습니다. {path}");

    using var frame = new Mat();
    if (!capture.Read(frame) || frame.Empty())
        throw new InvalidDataException("영상에서 첫 프레임을 읽을 수 없습니다.");

    using var screen = new RepairScreen();
    using var puzzles = new CharacterPuzzleReader(Path.GetFileName(path));
    var counter = new RepairCounter(brickPurchaseQuantity);
    Console.WriteLine($"영상 {Path.GetFileName(path)} | 퀘스트 목표 {questTarget:N0}회 | 벽돌 구매 단위 {brickPurchaseQuantity}개");

    double fps = capture.Fps;
    int frameIndex = 0;
    do
    {
        double seconds = capture.Get(VideoCaptureProperties.PosMsec) / 1000;
        if (!double.IsFinite(seconds) || seconds < 0 || (seconds == 0 && frameIndex > 0))
        {
            if (!double.IsFinite(fps) || fps <= 0)
                throw new InvalidDataException("영상의 재생 시각과 프레임 속도를 읽을 수 없습니다.");
            seconds = frameIndex / fps;
        }

        var timestamp = TimeSpan.FromSeconds(seconds);
        counter.Observe(screen, frame, timestamp);
        puzzles.Observe(frame, timestamp);
        frameIndex++;
    }
    while (capture.Read(frame) && !frame.Empty());

    counter.Complete();
    puzzles.Complete();
    Console.WriteLine($"집계 벽돌 {counter.Consumed}개 소모 | 확인 보류 {counter.Uncertain}건 | 재구매 시점 {counter.Refills}회 | 분석 {frameIndex:N0}프레임");
    return verify ? CompareReadings(puzzles.Readings) : 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OpenCVException or
    TypeInitializationException or DllNotFoundException or BadImageFormatException or InvalidOperationException)
{
    Console.Error.WriteLine(error.GetBaseException().Message);
    return 1;
}

static int RunPilot()
{
    Console.WriteLine("조종 리허설 | 만리장성 순찰, 벽돌 구매 이동, 풍신보");
    List<string> failures = FieldRehearsal.Run();
    using var hud = new HudReader();
    failures.AddRange(ExpectPlace(hud, "public/벽돌구매.mp4", TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(8),
        WallAtlas.Guknae, 108, "국내성 다리 부근"));
    failures.AddRange(ExpectPlace(hud, "public/벽돌구매.mp4", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(18),
        WallAtlas.Smith, 9, "털보네대장간"));
    failures.AddRange(ExpectPlace(hud, "public/만리장성수리.mp4", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4),
        WallAtlas.Jangan, null, "장안성"));
    failures.AddRange(ExpectPlace(hud, "public/만리장성수리.mp4", TimeSpan.FromSeconds(12.5), TimeSpan.FromSeconds(17),
        WallAtlas.Wall3, null, "만리장성3", true));
    foreach (string failure in failures)
        Console.WriteLine("실패 " + failure);
    if (failures.Count == 0)
        Console.WriteLine("리허설 통과 | 성문 이탈 없음 | 만리장성 맵 이동 | 풍신보 1초 | 비서 U 다음 M | 다리 경로 | 화면 좌표");
    return failures.Count == 0 ? 0 : 4;
}

static List<string> ExpectPlace(HudReader hud, string recording, TimeSpan from, TimeSpan to, string map, int? y, string label, bool requireCoordinate = false)
{
    string path = Path.GetFullPath(recording);
    if (!File.Exists(path))
        return [$"{label} 영상 없음"];

    using var video = File.OpenRead(path);
    using var capture = new VideoCapture(video, VideoCaptureAPIs.FFMPEG, Array.Empty<int>());
    using var frame = new Mat();
    bool found = false;
    string? saw = null;
    double fps = capture.Fps;
    int frameIndex = 0;
    while (capture.Read(frame) && !frame.Empty())
    {
        double seconds = capture.Get(VideoCaptureProperties.PosMsec) / 1000;
        if (!double.IsFinite(seconds) || seconds < 0 || (seconds == 0 && frameIndex > 0))
            seconds = fps > 0 ? frameIndex / fps : 0;
        frameIndex++;
        if (seconds < from.TotalSeconds)
            continue;
        if (seconds > to.TotalSeconds)
            break;
        hud.Read(frame, out string? readMap, out int? x, out int? readY);
        saw = $"{readMap ?? "지도없음"} {(x is int px && readY is int py ? $"{px},{py}" : "좌표없음")}";
        if (readMap == map && (y is null || readY == y) && (!requireCoordinate || x is not null))
        {
            found = true;
            Console.WriteLine($"화면 {label} | {saw} | {seconds:0.0}초");
            break;
        }
    }
    return found ? [] : [$"{label} 화면을 읽지 못했습니다. 마지막 {saw}"];
}

static int CompareReadings(IReadOnlyList<CharacterReading> readings)
{
    string answerPath = Path.Combine(Directory.GetCurrentDirectory(), "Verification", "character-answers.txt");
    var expected = new Dictionary<string, string>(StringComparer.Ordinal);
    if (File.Exists(answerPath))
    {
        foreach (string line in File.ReadLines(answerPath))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            string[] parts = trimmed.Split('\t', 2);
            if (parts.Length == 2)
                expected[parts[0]] = parts[1];
        }
    }

    if (readings.Count == 0)
    {
        Console.WriteLine("검증 문자 문제 없음");
        return 0;
    }

    int status = 0;
    foreach (CharacterReading reading in readings)
    {
        if (!expected.TryGetValue(reading.FileName, out string? answer))
        {
            Console.WriteLine($"검증 {reading.FileName} | 정답 기록 없음");
            status = 3;
            continue;
        }

        bool matched = reading.Candidate == answer;
        Console.WriteLine($"검증 {reading.FileName} | {reading.Attempt}회차 | 정답 {answer} | 판독 {reading.Candidate} | {(matched ? "일치" : "불일치")}");
        if (!matched)
            status = 3;
    }
    return status;
}
