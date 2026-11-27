using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

sealed class PuzzleLedger
{
    public const int AutoTypeLimit = 2;
    private static readonly TimeSpan Tail = TimeSpan.FromSeconds(1.2);

    private readonly List<PuzzleAttemptRecord> attempts = [];
    private readonly List<string> reasons = [];
    private int boundReaderAttempt;
    private int failsAfterHandoff;
    private bool pendingClose;
    private bool typedThisAttempt;
    private TimeSpan closeAt;
    private string actor = "없음";
    private string submitted = "";
    private string detail = "";

    public bool Active { get; private set; }
    public bool HandedToUser { get; private set; }
    public int EpisodeAttempt { get; private set; }
    public int AutoTyped { get; private set; }
    public bool JustOpened { get; private set; }
    public bool JustRestarted { get; private set; }
    public string? RestartReason { get; private set; }
    public bool CanAutoType => Active && !HandedToUser && AutoTyped < AutoTypeLimit && !typedThisAttempt;
    public IReadOnlyList<PuzzleAttemptRecord> Attempts => attempts;
    public IReadOnlyList<string> Reasons => reasons;

    public void OnFrame(bool open, int readerAttempt, TimeSpan now, string? closedOcr, string? closedDetail)
    {
        JustOpened = false;
        JustRestarted = false;
        RestartReason = null;
        if (open && !Active)
        {
            Active = true;
            HandedToUser = false;
            EpisodeAttempt = 1;
            AutoTyped = 0;
            boundReaderAttempt = readerAttempt;
            failsAfterHandoff = 0;
            pendingClose = false;
            typedThisAttempt = false;
            actor = "없음";
            submitted = "";
            detail = "";
            attempts.Clear();
            reasons.Clear();
            JustOpened = true;
            return;
        }

        if (!Active)
            return;

        if (open && readerAttempt != boundReaderAttempt)
        {
            string reason = typedThisAttempt && actor == "자동"
                ? "자동 입력 뒤 실패 횟수가 바뀌어 글자가 교체됨"
                : HandedToUser
                    ? "사용자 처리 중 실패 횟수가 바뀌어 글자가 교체됨"
                    : "입력 전에 실패 횟수가 바뀌어 새 문제가 시작됨";
            if (HandedToUser)
                failsAfterHandoff++;
            attempts.Add(new PuzzleAttemptRecord(EpisodeAttempt, actor, submitted, closedOcr ?? "", closedDetail ?? "", "실패", reason));
            reasons.Add($"{EpisodeAttempt}회차 {reason}");
            boundReaderAttempt = readerAttempt;
            EpisodeAttempt++;
            typedThisAttempt = false;
            actor = HandedToUser ? "사용자" : "없음";
            submitted = "";
            detail = "";
            JustRestarted = true;
            RestartReason = reason;
            if (!HandedToUser && AutoTyped >= AutoTypeLimit)
                Handoff("자동 입력 2회가 모두 실패해서 다음 기회는 직접 처리");
        }

        if (!open && !pendingClose)
        {
            pendingClose = true;
            closeAt = now;
        }
        else if (open && pendingClose)
        {
            pendingClose = false;
        }
    }

    public void NoteAutoTyped(string answer, string readDetail)
    {
        if (!CanAutoType)
            return;
        AutoTyped++;
        typedThisAttempt = true;
        actor = "자동";
        submitted = answer;
        detail = readDetail;
        reasons.Add($"{EpisodeAttempt}회차 자동 입력 {answer}");
    }

    public void Handoff(string reason)
    {
        if (!Active || HandedToUser)
            return;
        HandedToUser = true;
        actor = "사용자";
        reasons.Add(reason);
    }

    public bool TryFinish(bool open, TimeSpan now, out string outcome, out string outcomeDetail)
    {
        outcome = "";
        outcomeDetail = "";
        if (!Active || open || !pendingClose || now - closeAt < Tail)
            return false;
        Conclude(out outcome, out outcomeDetail);
        Active = false;
        return true;
    }

    public void ForceFinish(out string outcome, out string outcomeDetail)
    {
        if (!Active)
        {
            outcome = "";
            outcomeDetail = "";
            return;
        }
        Conclude(out outcome, out outcomeDetail);
        Active = false;
    }

    private void Conclude(out string outcome, out string outcomeDetail)
    {
        if (HandedToUser && failsAfterHandoff == 0)
        {
            outcome = "사용자마무리";
            outcomeDetail = "자동 입력을 넘긴 뒤 실패 횟수가 더 늘지 않고 문제 창이 닫힘";
        }
        else if (HandedToUser)
        {
            outcome = "실패";
            outcomeDetail = $"사용자 처리 이후 실패 횟수가 {failsAfterHandoff}번 더 늘고 문제 창이 닫힘";
        }
        else if (AutoTyped > 0 && typedThisAttempt)
        {
            outcome = "2회내성공";
            outcomeDetail = $"자동 입력 {AutoTyped}회 안에 문제 창이 닫힘";
        }
        else
        {
            outcome = "입력전종료";
            outcomeDetail = "자동 입력 전에 문제 창이 닫힘";
        }

        string attemptResult = outcome switch
        {
            "2회내성공" or "사용자마무리" => "성공",
            "실패" => "실패",
            _ => "종료"
        };
        attempts.Add(new PuzzleAttemptRecord(EpisodeAttempt, actor, submitted, "", detail, attemptResult, outcomeDetail));
    }
}

sealed record PuzzleAttemptRecord(
    [property: JsonPropertyName("회차")] int Number,
    [property: JsonPropertyName("입력주체")] string Actor,
    [property: JsonPropertyName("입력")] string Submitted,
    [property: JsonPropertyName("마감판독")] string Ocr,
    [property: JsonPropertyName("판독상세")] string Detail,
    [property: JsonPropertyName("결과")] string Result,
    [property: JsonPropertyName("원인")] string Reason);

sealed class PuzzleArchive : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions JsonLineOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string root;
    private readonly PuzzleLedger ledger = new();
    private readonly List<double> frameTimes = [];
    private VideoWriter? writer;
    private string? tempVideo;
    private string? eventDir;
    private string? eventId;
    private DateTimeOffset startedAt;
    private bool saved;
    private int seenReadings;

    public PuzzleArchive(string root) => this.root = root;

    public bool Capturing => ledger.Active;
    public bool CanAutoType => ledger.CanAutoType;
    public bool JustOpened => ledger.JustOpened;
    public bool JustRestarted => ledger.JustRestarted;
    public string? RestartReason => ledger.RestartReason;
    public int EpisodeAttempt => ledger.EpisodeAttempt;

    public string? Observe(Mat frame, TimeSpan now, bool open, int readerAttempt, IReadOnlyList<CharacterReading> readings)
    {
        string? closedOcr = null;
        string? closedDetail = null;
        if (readings.Count > seenReadings && readings.Count > 0)
        {
            CharacterReading reading = readings[^1];
            closedOcr = reading.Candidate;
            closedDetail = reading.Detail;
            seenReadings = readings.Count;
        }

        ledger.OnFrame(open, readerAttempt, now, closedOcr, closedDetail);
        if (ledger.JustOpened)
            StartFiles();
        if (ledger.Active && !frame.Empty())
            WriteFrame(frame, now);
        if (!ledger.TryFinish(open, now, out string outcome, out string outcomeDetail))
            return null;
        return Save(outcome, outcomeDetail);
    }

    public void NoteAutoTyped(string answer, string detail) => ledger.NoteAutoTyped(answer, detail);

    public void Handoff(string reason) => ledger.Handoff(reason);

    public void Dispose()
    {
        if (ledger.Active)
        {
            ledger.ForceFinish(out string outcome, out string detail);
            Save(outcome, detail);
        }
        ReleaseWriter();
    }

    private void StartFiles()
    {
        saved = false;
        frameTimes.Clear();
        startedAt = DateTimeOffset.Now;
        eventId = startedAt.ToString("yyyyMMdd-HHmmss");
        eventDir = Path.Combine(root, eventId);
        Directory.CreateDirectory(eventDir);
        tempVideo = Path.Combine(Path.GetTempPath(), "BonghwangWall", eventId + ".avi");
        Directory.CreateDirectory(Path.GetDirectoryName(tempVideo)!);
        ReleaseWriter();
        writer = new VideoWriter(tempVideo, VideoWriter.FourCC('M', 'J', 'P', 'G'), 8, new Size(1920, 1080), true);
        if (!writer.IsOpened())
            ReleaseWriter();
    }

    private void WriteFrame(Mat frame, TimeSpan now)
    {
        frameTimes.Add(now.TotalSeconds);
        if (writer is not null && writer.IsOpened() && frame.Width == 1920 && frame.Height == 1080)
        {
            writer.Write(frame);
            return;
        }

        if (eventDir is null || !Cv2.ImEncode(".jpg", frame, out byte[] jpeg))
            return;
        File.WriteAllBytes(Path.Combine(eventDir, $"frame-{frameTimes.Count:D4}.jpg"), jpeg);
    }

    private string Save(string outcome, string outcomeDetail)
    {
        if (saved || eventDir is null || eventId is null)
            return "";
        saved = true;
        ReleaseWriter();
        string videoName = "clip.avi";
        string videoPath = Path.Combine(eventDir, videoName);
        bool hasVideo = false;
        if (tempVideo is not null && File.Exists(tempVideo) && new FileInfo(tempVideo).Length > 0)
        {
            File.Move(tempVideo, videoPath, overwrite: true);
            hasVideo = true;
        }

        var record = new PuzzleEventRecord(
            eventId,
            startedAt,
            DateTimeOffset.Now,
            outcome,
            outcomeDetail,
            ledger.AutoTyped,
            PuzzleLedger.AutoTypeLimit,
            hasVideo ? Path.Combine("허풍선기록", eventId, videoName) : "",
            frameTimes.Count,
            frameTimes,
            ledger.Attempts.ToArray(),
            ledger.Reasons.ToArray());
        File.WriteAllText(Path.Combine(eventDir, "event.json"), JsonSerializer.Serialize(record, JsonOptions));
        Directory.CreateDirectory(root);
        File.AppendAllText(Path.Combine(root, "events.jsonl"), JsonSerializer.Serialize(record, JsonLineOptions) + Environment.NewLine);
        return $"허풍선 기록 {eventId} | {outcome} | {outcomeDetail} | 영상 {(hasVideo ? videoPath : "저장 실패, 프레임 사진으로 남김")}";
    }

    private void ReleaseWriter()
    {
        writer?.Release();
        writer?.Dispose();
        writer = null;
    }
}

sealed record PuzzleEventRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("시작")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("종료")] DateTimeOffset EndedAt,
    [property: JsonPropertyName("결과")] string Outcome,
    [property: JsonPropertyName("결과설명")] string OutcomeDetail,
    [property: JsonPropertyName("자동입력횟수")] int AutoTyped,
    [property: JsonPropertyName("자동입력한도")] int AutoLimit,
    [property: JsonPropertyName("영상")] string Video,
    [property: JsonPropertyName("프레임수")] int FrameCount,
    [property: JsonPropertyName("프레임시각")] IReadOnlyList<double> FrameTimes,
    [property: JsonPropertyName("회차")] IReadOnlyList<PuzzleAttemptRecord> Attempts,
    [property: JsonPropertyName("로그")] IReadOnlyList<string> Reasons);
