using System.Text;
using OpenCvSharp;

Console.OutputEncoding = Encoding.UTF8;

const int brickPurchaseQuantity = 100;
const int questTarget = 10_000;
const string defaultRecording = "public/만리장성수리.mp4";
const string usage = "사용법: dotnet run -- [영상.mp4]";

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine(usage);
    return 0;
}
if (args.Length > 1 || (args.Length == 1 && args[0].StartsWith('-')))
{
    Console.Error.WriteLine(usage);
    return 2;
}

try
{
    string path = Path.GetFullPath(args.Length == 1 ? args[0] : defaultRecording);
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

        counter.Observe(screen, frame, TimeSpan.FromSeconds(seconds));
        frameIndex++;
    }
    while (capture.Read(frame) && !frame.Empty());

    counter.Complete();
    Console.WriteLine($"집계 벽돌 {counter.Consumed}개 소모 | 확인 보류 {counter.Uncertain}건 | 재구매 시점 {counter.Refills}회 | 분석 {frameIndex:N0}프레임");
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OpenCVException or
    TypeInitializationException or DllNotFoundException or BadImageFormatException)
{
    Console.Error.WriteLine(error.GetBaseException().Message);
    return 1;
}
