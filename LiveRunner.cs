using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;

enum HotkeyEvent
{
    None,
    Started,
    Paused,
    Exit
}

sealed class HotkeyState
{
    private bool prevF11;
    private bool prevEnd;

    public bool Running { get; private set; }
    public bool ExitRequested { get; private set; }

    public HotkeyEvent Update(bool f11Down, bool endDown)
    {
        bool f11Edge = f11Down && !prevF11;
        bool endEdge = endDown && !prevEnd;
        prevF11 = f11Down;
        prevEnd = endDown;

        if (endEdge)
        {
            ExitRequested = true;
            Running = false;
            return HotkeyEvent.Exit;
        }

        if (f11Edge)
        {
            Running = !Running;
            return Running ? HotkeyEvent.Started : HotkeyEvent.Paused;
        }

        return HotkeyEvent.None;
    }

    public HotkeyEvent TriggerF11()
    {
        Running = !Running;
        return Running ? HotkeyEvent.Started : HotkeyEvent.Paused;
    }

    public HotkeyEvent TriggerExit()
    {
        ExitRequested = true;
        Running = false;
        return HotkeyEvent.Exit;
    }

    public void ForcePause() => Running = false;
}

sealed class HotkeyWatcher : IDisposable
{
    private const int VkEnd = 0x23;
    private const int VkF11 = 0x7A;
    private readonly Func<int, short> pollKey;
    private readonly CancellationTokenSource stopCts = new();
    private readonly Thread thread;
    private int pendingF11;
    private int pendingEnd;

    public HotkeyWatcher(Func<int, short> pollKey)
    {
        this.pollKey = pollKey;
        // Drain any stale key-tap latch accumulated before starting the watcher.
        _ = pollKey(VkF11);
        _ = pollKey(VkEnd);
        thread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "HotkeyWatcher"
        };
        thread.Start();
    }

    public bool HasPending =>
        Volatile.Read(ref pendingEnd) != 0 || Volatile.Read(ref pendingF11) != 0;

    public HotkeyEvent Poll(HotkeyState state)
    {
        if (Interlocked.Exchange(ref pendingEnd, 0) != 0)
            return state.TriggerExit();
        if (Interlocked.Exchange(ref pendingF11, 0) != 0)
            return state.TriggerF11();
        return HotkeyEvent.None;
    }

    public void SleepInterruptible(int totalMs, CancellationToken token)
    {
        int remaining = totalMs;
        while (remaining > 0 && !token.IsCancellationRequested && !HasPending)
        {
            int slice = Math.Min(remaining, 15);
            Thread.Sleep(slice);
            remaining -= slice;
        }
    }

    private void WatchLoop()
    {
        bool prevF11 = (pollKey(VkF11) & 0x8000) != 0;
        bool prevEnd = (pollKey(VkEnd) & 0x8000) != 0;
        long lastF11Ms = 0;
        long lastEndMs = 0;

        while (!stopCts.IsCancellationRequested)
        {
            short sF11 = pollKey(VkF11);
            short sEnd = pollKey(VkEnd);
            bool f11Down = (sF11 & 0x8000) != 0;
            bool f11Tapped = (sF11 & 0x0001) != 0;
            bool endDown = (sEnd & 0x8000) != 0;
            bool endTapped = (sEnd & 0x0001) != 0;
            long nowMs = Environment.TickCount64;

            if (((f11Down && !prevF11) || (!prevF11 && f11Tapped)) && nowMs - lastF11Ms >= 250)
            {
                lastF11Ms = nowMs;
                Interlocked.Exchange(ref pendingF11, 1);
            }
            if (((endDown && !prevEnd) || (!prevEnd && endTapped)) && nowMs - lastEndMs >= 250)
            {
                lastEndMs = nowMs;
                Interlocked.Exchange(ref pendingEnd, 1);
            }

            prevF11 = f11Down;
            prevEnd = endDown;
            Thread.Sleep(15);
        }
    }

    public void Dispose()
    {
        stopCts.Cancel();
        if (thread.IsAlive)
            thread.Join(200);
        stopCts.Dispose();
    }
}

sealed class LiveOptions
{
    public bool DryRun { get; init; }
    public bool Screen { get; init; }
    public string? WindowTitle { get; init; }
    public int PurchaseQuantity { get; init; } = 100;
}

static class LiveRunner
{
    private const int VkEnd = 0x23;
    private const int VkF11 = 0x7A;
    private static readonly string[] DefaultWindowKeywords = ["MapleStory Worlds", "신바람", "봉황", "바람"];

    public static int Run(LiveOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("실시간 화면 제어(--live)는 Windows에서만 지원됩니다.");
            return 1;
        }

        TryEnablePerMonitorDpiAwareness();

        using var log = new LiveLogger(Path.Combine(AppContext.BaseDirectory, "BonghwangWall.log"));
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var screen = new RepairScreen();
        using var hud = new HudReader();
        using var puzzles = new CharacterPuzzleReader("live");
        var vision = new WallVision();
        var counter = new RepairCounter(options.PurchaseQuantity);
        var pilot = new FieldPilot(new Random(), hud.SupportedWalls, options.PurchaseQuantity);
        var hotkey = new HotkeyState();
        using var hotkeyWatcher = new HotkeyWatcher(GetAsyncKeyState);
        var clock = Stopwatch.StartNew();

        using var raw = new Mat();
        using var frame = new Mat();

        nint targetWindow = 0;
        WindowCapturer? capturer = null;
        FieldInput? input = null;
        string? lastReason = null;
        FieldKey? lastHold = null;
        string? lastLoggedMap = null;
        TimeSpan lastHeartbeatAt = TimeSpan.MinValue;
        TimeSpan lastCaptureFailAt = TimeSpan.MinValue;
        string? lastKnownMap = null;
        int? lastKnownX = null;
        int? lastKnownY = null;
        TimeSpan lastKnownAt = TimeSpan.MinValue;
        TimeSpan unknownDialogSince = TimeSpan.MinValue;
        TimeSpan overlayPopupSince = TimeSpan.MinValue;
        bool loggedCaptureSize = false;
        bool warnedFocus = false;
        bool wasInBuy = false;
        bool puzzleFocusLogged = false;
        using var puzzleArchive = new PuzzleArchive(Path.Combine(AppContext.BaseDirectory, "허풍선기록"));

        log.Write("==================================================");
        log.Write("  봉황 만리장성 자동 진행");
        log.Write($"  모드: {(options.DryRun ? "드라이런(입력 안 함)" : "실제 입력")} | 벽돌 구매 단위: {options.PurchaseQuantity}개");
        log.Write($"  로그 파일: {log.LogPath}");
        log.Write("  F11 : 시작 / 일시정지");
        log.Write("  END : 즉시 종료");
        log.Write("  허풍선 자동 입력은 2회까지입니다. 두 번 실패하면 알림 후 일시정지합니다.");
        log.Write("  허풍선 영상과 결과는 exe 옆 허풍선기록 폴더에 남깁니다.");
        log.Write("==================================================");
        log.Write(MotherboardBeep.Probe());
        log.Write(AlertSound.EnableLoudOutput());
        log.Write("[대기 중] 게임 창을 띄운 뒤 F11 키를 눌러 시작하세요.");

        try
        {
            while (!cts.IsCancellationRequested)
            {
                HotkeyEvent ev = hotkeyWatcher.Poll(hotkey);

                if (ev == HotkeyEvent.Exit)
                {
                    log.Write("[종료] END 키가 눌려 프로그램을 종료합니다.");
                    break;
                }

                if (ev == HotkeyEvent.Started)
                {
                    if (!options.Screen)
                    {
                        targetWindow = FindTargetWindow(options.WindowTitle);
                        if (targetWindow == 0)
                        {
                            string hint = string.IsNullOrWhiteSpace(options.WindowTitle)
                                ? string.Join(", ", DefaultWindowKeywords)
                                : options.WindowTitle;
                            log.Write($"[대기 중] 대상 게임 창을 찾지 못했습니다. ({hint}) 게임 창을 띄운 뒤 F11을 다시 누르세요.");
                            hotkey.ForcePause();
                            Thread.Sleep(50);
                            continue;
                        }
                    }

                    input?.Dispose();
                    capturer?.Dispose();
                    capturer = new WindowCapturer(targetWindow);
                    input = new FieldInput(targetWindow, options.DryRun);
                    lastReason = null;
                    lastHold = null;
                    lastLoggedMap = null;
                    lastHeartbeatAt = TimeSpan.MinValue;
                    lastCaptureFailAt = TimeSpan.MinValue;
                    lastKnownMap = null;
                    lastKnownX = null;
                    lastKnownY = null;
                    lastKnownAt = TimeSpan.MinValue;
                    unknownDialogSince = TimeSpan.MinValue;
                    overlayPopupSince = TimeSpan.MinValue;
                    loggedCaptureSize = false;
                    warnedFocus = false;
                    puzzleFocusLogged = false;

                    string titleText = targetWindow == 0 ? "주 모니터 전체 화면" : GetWindowTitle(targetWindow);
                    string rectNote = targetWindow == 0 ? "" : $" | HWND=0x{targetWindow:X} {DescribeClientRect(targetWindow)}";
                    log.Write($"[실행 시작] 대상: {titleText}{rectNote} (일시정지: F11 / 종료: END)");
                }
                else if (ev == HotkeyEvent.Paused)
                {
                    input?.ReleaseAll();
                    lastReason = null;
                    lastHold = null;
                    unknownDialogSince = TimeSpan.MinValue;
                    overlayPopupSince = TimeSpan.MinValue;
                    log.Write("[일시정지] F11: 재개 | END: 종료");
                }

                if (!hotkey.Running)
                {
                    hotkeyWatcher.SleepInterruptible(50, cts.Token);
                    continue;
                }

                if (targetWindow != 0 && !IsWindow(targetWindow))
                {
                    input?.ReleaseAll();
                    hotkey.ForcePause();
                    log.Write("[대기 중] 대상 게임 창이 닫혀 일시정지합니다. 다시 띄운 뒤 F11을 누르세요.");
                    Thread.Sleep(100);
                    continue;
                }

                if (capturer is null || input is null || !capturer.TryCapture(raw) || raw.Empty())
                {
                    input?.ReleaseAll();
                    TimeSpan capNow = clock.Elapsed;
                    if (lastCaptureFailAt == TimeSpan.MinValue || capNow - lastCaptureFailAt >= TimeSpan.FromSeconds(1))
                    {
                        lastCaptureFailAt = capNow;
                        string rectInfo = targetWindow == 0 ? "Screen" : DescribeClientRect(targetWindow);
                        log.Write($"[캡처 실패] 화면 캡처에 실패했습니다. 대상:{rectInfo}");
                    }
                    Thread.Sleep(100);
                    continue;
                }

                if (!loggedCaptureSize)
                {
                    loggedCaptureSize = true;
                    string resizeNote = raw.Width == 1920 && raw.Height == 1080 ? "원본" : "1920x1080 리사이즈";
                    log.Write($"[캡처] 해상도 {raw.Width}x{raw.Height} ({resizeNote})");
                }

                Mat normalized = raw;
                if (raw.Width != 1920 || raw.Height != 1080)
                {
                    Cv2.Resize(raw, frame, new Size(1920, 1080));
                    normalized = frame;
                }

                TimeSpan now = clock.Elapsed;
                bool puzzleBusy = false;
                try
                {
                    RepairDialog dialog = screen.ReadDialog(normalized);
                    BrickStock stock = screen.ReadStock(normalized);
                    hud.Read(normalized, out string? map, out int? x, out int? y);
                    bool worldMap = hud.IsWorldMap(normalized);
                    bool returnConfirm = hud.IsReturnConfirmDialog(normalized);
                    SmithyDialogState smithyDialog = map == WallAtlas.Smith
                        ? hud.ReadSmithyDialog(normalized)
                        : SmithyDialogState.None;

                    puzzles.Observe(normalized, now);
                    bool puzzleOpen = puzzles.IsOpen;
                    string? puzzleRecord = puzzleArchive.Observe(normalized, now, puzzleOpen, puzzles.OpenAttempt, puzzles.Readings);
                    puzzleBusy = puzzleOpen || puzzleArchive.Capturing;
                    if (puzzleRecord is { Length: > 0 })
                        log.Write($"[{now:hh\\:mm\\:ss}] {puzzleRecord}");
                    if (puzzleArchive.JustOpened)
                    {
                        log.Write($"[{now:hh\\:mm\\:ss}] 허풍선 1회차 감지");
                        AlertSound.Play();
                    }
                    else if (puzzleArchive.JustRestarted)
                    {
                        string fresh = puzzleArchive.RestartReason is null ? "" : " | " + puzzleArchive.RestartReason;
                        log.Write($"[{now:hh\\:mm\\:ss}] 허풍선 {puzzleArchive.EpisodeAttempt}회차 | 새 미니게임 | 이전 글자는 사용하지 않음{fresh}");
                        AlertSound.Play();
                        if (!puzzleArchive.CanAutoType)
                        {
                            input.ReleaseAll();
                            hotkey.ForcePause();
                            log.Write($"[{now:hh\\:mm\\:ss}] [일시정지] 허풍선 2회 실패. 알림만 울리고 키 입력을 멈춥니다. 직접 입력한 뒤 F11로 재개하세요.");
                            continue;
                        }
                    }
                    if (puzzleOpen && puzzles.OpenError is string puzzleError && puzzleArchive.CanAutoType)
                    {
                        input.ReleaseAll();
                        puzzleArchive.Handoff("글자 인식 오류: " + puzzleError);
                        log.Write($"[{now:hh\\:mm\\:ss}] 허풍선 글자를 읽지 못해 자동 입력을 넘깁니다. {puzzleError}");
                        AlertSound.Play();
                    }

                    bool unknownDialog = !puzzleOpen
                        && !worldMap
                        && dialog == RepairDialog.None
                        && !returnConfirm
                        && map != WallAtlas.Smith
                        && (screen.IsDialogPanelOpen(normalized)
                            || (WallAtlas.IsWall(map) && hud.HasCenterParchmentDialog(normalized)));

                    if (unknownDialog)
                    {
                        if (unknownDialogSince == TimeSpan.MinValue)
                            unknownDialogSince = now;
                        else if (now - unknownDialogSince >= TimeSpan.FromMilliseconds(700))
                        {
                            input.ReleaseAll();
                            hotkey.ForcePause();
                            unknownDialogSince = TimeSpan.MinValue;
                            log.Write($"[{now:hh\\:mm\\:ss}] [자동 일시정지] 수리 창 외의 팝업/대화창이 감지되어 이동을 멈추고 일시정지합니다. 직접 처리한 뒤 게임 창에서 F11 키를 눌러 재개하세요.");
                            AlertSound.Play();
                            Thread.Sleep(100);
                            continue;
                        }
                    }
                    else
                    {
                        unknownDialogSince = TimeSpan.MinValue;
                    }

                    if (worldMap || dialog != RepairDialog.None || returnConfirm || smithyDialog != SmithyDialogState.None || unknownDialog || puzzleOpen || puzzleArchive.Capturing)
                    {
                        lastKnownMap = null;
                        lastKnownX = null;
                        lastKnownY = null;
                        lastKnownAt = TimeSpan.MinValue;
                    }
                    else if (map is not null && x is not null && y is not null)
                    {
                        lastKnownMap = map;
                        lastKnownX = x;
                        lastKnownY = y;
                        lastKnownAt = now;
                    }
                    else if (lastKnownAt != TimeSpan.MinValue && now - lastKnownAt <= TimeSpan.FromMilliseconds(600))
                    {
                        map ??= lastKnownMap;
                        x ??= lastKnownX;
                        y ??= lastKnownY;
                    }

                    int walkableCount = 0;
                    int blockedScanCount = 0;
                    bool largeOverlay = false;
                    if (map is not null
                        && WallAtlas.IsWall(map)
                        && x is int px
                        && y is int py
                        && !worldMap
                        && dialog == RepairDialog.None
                        && !returnConfirm
                        && !unknownDialog
                        && !puzzleOpen
                        && !puzzleArchive.Capturing)
                    {
                        List<TileScan> scans = vision.Scan(normalized, map, px, py);
                        largeOverlay = vision.HasLargeOverlay;
                        if (!largeOverlay)
                        {
                            pilot.ObserveTerrain(map, scans, now);
                            foreach (TileScan scan in scans)
                            {
                                if (scan.Walkable)
                                    walkableCount++;
                                else
                                    blockedScanCount++;
                            }
                        }
                    }

                    if (largeOverlay)
                    {
                        if (overlayPopupSince == TimeSpan.MinValue)
                            overlayPopupSince = now;
                        else if (now - overlayPopupSince >= TimeSpan.FromMilliseconds(500))
                        {
                            input.ReleaseAll();
                            hotkey.ForcePause();
                            overlayPopupSince = TimeSpan.MinValue;
                            log.Write($"[{now:hh\\:mm\\:ss}] [자동 일시정지] 화면 중앙에 미확인 팝업/허풍선 창({vision.LastOverlayReason})이 감지되어 이동을 즉시 멈추고 일시정지합니다. 직접 처리한 뒤 게임 창에서 F11 키를 눌러 재개하세요.");
                            AlertSound.Play();
                            Thread.Sleep(100);
                            continue;
                        }
                    }
                    else
                    {
                        overlayPopupSince = TimeSpan.MinValue;
                    }

                    counter.Observe(screen, normalized, now);
                    if (map is not null && !worldMap && dialog == RepairDialog.None && !returnConfirm && !unknownDialog && !largeOverlay && !puzzleOpen && !puzzleArchive.Capturing)
                        counter.ObserveStock(stock, now, allowAutoCancel: !pilot.IsSupplying || !pilot.InBuyPhase);

                    var view = new FieldView(
                        map,
                        x,
                        y,
                        counter.NeedsBricks,
                        UiBlocked: unknownDialog || largeOverlay,
                        now,
                        RepairDialogOpen: dialog != RepairDialog.None,
                        WorldMapOpen: worldMap,
                        SmithyDialog: smithyDialog,
                        Stock: stock,
                        ReturnConfirmOpen: returnConfirm,
                        PuzzleOpen: puzzleOpen || puzzleArchive.Capturing);

                    FieldCommand command = pilot.Decide(view);
                    if (wasInBuy && !pilot.InBuyPhase)
                        counter.MarkSupplied();
                    wasInBuy = pilot.InBuyPhase;

                    bool focused = targetWindow == 0 || GetForegroundWindow() == targetWindow;
                    if (!focused && !options.DryRun && !warnedFocus)
                    {
                        warnedFocus = true;
                        log.Write("[포커스 대기] 게임 창이 활성 상태가 아니라 키 입력을 잠시 보류합니다.");
                    }
                    else if (focused)
                    {
                        warnedFocus = false;
                    }

                    input.Apply(command);
                    if (puzzleOpen
                        && puzzleArchive.CanAutoType
                        && puzzles.TryCurrentAnswer(now, out string puzzleAnswer, out string puzzleDetail))
                    {
                        if (!focused && !options.DryRun)
                        {
                            if (now - puzzles.OpenedAt >= TimeSpan.FromSeconds(6))
                            {
                                input.ReleaseAll();
                                puzzleArchive.Handoff("게임 창이 비활성이라 자동 입력하지 못함. 판독 " + puzzleAnswer);
                                log.Write($"[{now:hh\\:mm\\:ss}] 허풍선 입력 전에 게임 창이 비활성입니다. 직접 입력해 주세요. {puzzleAnswer}");
                                AlertSound.Play();
                            }
                            else if (!puzzleFocusLogged)
                            {
                                puzzleFocusLogged = true;
                                log.Write($"[{now:hh\\:mm\\:ss}] 허풍선 {puzzleAnswer} | 게임 창이 비활성이라 입력을 기다립니다. {puzzleDetail}");
                            }
                        }
                        else
                        {
                            double readSeconds = (now - puzzles.OpenedAt).TotalSeconds;
                            log.Write($"[{now:hh\\:mm\\:ss}] 허풍선 {puzzleArchive.EpisodeAttempt}회차 입력 {puzzleAnswer} | 판독 {readSeconds:0.0}초 | {puzzleDetail}");
                            input.TypePuzzleAnswer(puzzleAnswer);
                            puzzleArchive.NoteAutoTyped(puzzleAnswer, puzzleDetail);
                            puzzleFocusLogged = false;
                        }
                    }
                    else if (puzzleOpen
                        && puzzleArchive.CanAutoType
                        && now - puzzles.OpenedAt >= TimeSpan.FromSeconds(6))
                    {
                        input.ReleaseAll();
                        puzzleArchive.Handoff("6초 안에 여섯 글자를 확정하지 못함");
                        log.Write($"[{now:hh\\:mm\\:ss}] 허풍선 글자를 6초 안에 확정하지 못했습니다. 직접 입력해 주세요.");
                        AlertSound.Play();
                    }
                    else if (!puzzleOpen)
                    {
                        puzzleFocusLogged = false;
                    }

                    bool shouldLog = command.Reason != lastReason
                        || command.Hold != lastHold
                        || command.Tap is not null
                        || command.Click is not null
                        || map != lastLoggedMap
                        || lastHeartbeatAt == TimeSpan.MinValue
                        || now - lastHeartbeatAt >= TimeSpan.FromSeconds(1);

                    if (shouldLog)
                    {
                        lastReason = command.Reason;
                        lastHold = command.Hold;
                        lastLoggedMap = map;
                        lastHeartbeatAt = now;
                        string coord = x is int cx && y is int cy ? $"{cx},{cy}" : "-";
                        string keyText = command.Click is ClickTarget clk
                            ? $"Click({clk.X},{clk.Y})"
                            : command.Tap is FieldKey tap
                                ? $"Tap:{tap}"
                                : command.Hold is FieldKey hold
                                    ? $"Hold:{hold}{(command.Dash ? "+Dash" : "")}"
                                    : "None";
                        string focusTag = focused || options.DryRun ? "" : " [비활성창]";
                        string scanTag = walkableCount + blockedScanCount > 0 ? $" | 길:{walkableCount}/벽:{blockedScanCount}" : "";
                        string adjTag = map is not null && WallAtlas.IsWall(map) && x is int wallX && y is int wallY
                            ? $" | 주변:{pilot.DescribeAdjacent(map, wallX, wallY, now)}"
                            : "";
                        string visionSkipTag = walkableCount + blockedScanCount == 0 && !string.IsNullOrEmpty(vision.LastSkipReason)
                            ? $" | 시야:{vision.LastSkipReason}"
                            : "";
                        string hudDiagTag = (map is null || x is null || y is null) && !worldMap && !string.IsNullOrEmpty(hud.LastDiagnostic)
                            ? $" | HUD:{hud.LastDiagnostic}"
                            : "";
                        log.Write($"[{now:hh\\:mm\\:ss}] {map ?? (worldMap ? "지역지도" : "미확인")} ({coord}) | {keyText} | 목표:{pilot.CurrentTargetSummary} | 재고:{stock} | 막힘:{pilot.LearnedBlockedCount}{adjTag}{scanTag}{visionSkipTag}{hudDiagTag}{focusTag} | {command.Reason}");
                    }
                }
                catch (Exception tickError)
                {
                    input.ReleaseAll();
                    log.WriteError($"[프레임 처리 예외] {tickError}");
                    Thread.Sleep(250);
                }

                hotkeyWatcher.SleepInterruptible(puzzleBusy ? 20 : 100, cts.Token);
            }
        }
        finally
        {
            input?.Dispose();
            capturer?.Dispose();
            counter.Complete();
        }

        log.Write($"실시간 종료 | 벽돌 {counter.Consumed}개 소모 | 재구매 {counter.Refills}회");
        return 0;
    }

    private static void TryEnablePerMonitorDpiAwareness()
    {
        try
        {
            _ = SetProcessDpiAwarenessContext(new nint(-4));
        }
        catch
        {
        }
    }

    private static nint FindTargetWindow(string? titleFilter)
    {
        nint consoleWindow = GetConsoleWindow();
        uint currentPid = (uint)Environment.ProcessId;

        bool IsCandidate(nint hWnd, out string title)
        {
            title = "";
            if (hWnd == 0 || hWnd == consoleWindow || !IsWindowVisible(hWnd))
                return false;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == currentPid)
                return false;
            title = GetWindowTitle(hWnd);
            return !string.IsNullOrWhiteSpace(title);
        }

        int MatchPriority(string title)
        {
            if (!string.IsNullOrWhiteSpace(titleFilter))
                return title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase) ? 0 : -1;

            for (int i = 0; i < DefaultWindowKeywords.Length; i++)
            {
                if (title.Contains(DefaultWindowKeywords[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        nint fg = GetForegroundWindow();
        if (IsCandidate(fg, out string fgTitle) && MatchPriority(fgTitle) >= 0)
            return fg;

        nint bestWindow = 0;
        int bestPriority = int.MaxValue;
        EnumWindows((hWnd, _) =>
        {
            if (!IsCandidate(hWnd, out string title))
                return true;

            int priority = MatchPriority(title);
            if (priority >= 0 && priority < bestPriority)
            {
                bestPriority = priority;
                bestWindow = hWnd;
                if (priority == 0)
                    return false;
            }
            return true;
        }, 0);
        return bestWindow;
    }

    private static string DescribeClientRect(nint hWnd)
    {
        if (hWnd == 0 || !IsWindow(hWnd))
            return "(창 없음)";
        if (IsIconic(hWnd))
            return "(최소화됨)";
        if (!GetClientRect(hWnd, out NativeRect rect))
            return "(영역 조회 실패)";
        var origin = new NativePoint(0, 0);
        if (!ClientToScreen(hWnd, ref origin))
            return $"{rect.Right - rect.Left}x{rect.Bottom - rect.Top}";
        return $"({origin.X},{origin.Y}) {rect.Right - rect.Left}x{rect.Bottom - rect.Top}";
    }

    private sealed class LiveLogger : IDisposable
    {
        private readonly StreamWriter? writer;

        public string LogPath { get; }

        public LiveLogger(string logPath)
        {
            LogPath = logPath;
            try
            {
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 5 * 1024 * 1024)
                {
                    string oldPath = logPath + ".old";
                    File.Move(logPath, oldPath, overwrite: true);
                }
                writer = new StreamWriter(logPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
            }
            catch
            {
                writer = null;
            }
        }

        public void Write(string line)
        {
            Console.WriteLine(line);
            WriteFile(line);
        }

        public void WriteError(string line)
        {
            Console.Error.WriteLine(line);
            WriteFile(line);
        }

        private void WriteFile(string line)
        {
            try
            {
                writer?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}");
            }
            catch
            {
            }
        }

        public void Dispose() => writer?.Dispose();
    }

    private static string GetWindowTitle(nint hWnd)
    {
        int length = GetWindowTextLengthW(hWnd);
        if (length <= 0)
            return "";
        var sb = new StringBuilder(length + 1);
        _ = GetWindowTextW(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private sealed class WindowCapturer : IDisposable
    {
        private const uint Srccopy = 0x00CC0020;
        private const uint DibRgbColors = 0;
        private const int SmCxScreen = 0;
        private const int SmCyScreen = 1;

        private readonly nint window;

        public WindowCapturer(nint window) => this.window = window;

        public bool TryCapture(Mat output)
        {
            int width;
            int height;
            int srcX;
            int srcY;
            if (window == 0)
            {
                width = GetSystemMetrics(SmCxScreen);
                height = GetSystemMetrics(SmCyScreen);
                srcX = 0;
                srcY = 0;
            }
            else
            {
                if (IsIconic(window) || !GetClientRect(window, out NativeRect rect))
                    return false;
                width = rect.Right - rect.Left;
                height = rect.Bottom - rect.Top;
                var origin = new NativePoint(0, 0);
                if (!ClientToScreen(window, ref origin))
                    return false;
                srcX = origin.X;
                srcY = origin.Y;
            }

            if (width <= 0 || height <= 0)
                return false;

            nint sourceDc = GetDC(0);
            if (sourceDc == 0)
                return false;

            nint memoryDc = 0;
            nint bitmap = 0;
            nint previous = 0;
            try
            {
                memoryDc = CreateCompatibleDC(sourceDc);
                if (memoryDc == 0)
                    return false;

                bitmap = CreateCompatibleBitmap(sourceDc, width, height);
                if (bitmap == 0)
                    return false;

                previous = SelectObject(memoryDc, bitmap);
                if (!BitBlt(memoryDc, 0, 0, width, height, sourceDc, srcX, srcY, Srccopy))
                    return false;

                output.Create(height, width, MatType.CV_8UC3);
                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = width,
                        Height = -height,
                        Planes = 1,
                        BitCount = 24,
                        Compression = 0
                    }
                };
                int copied = GetDIBits(memoryDc, bitmap, 0, (uint)height, output.Data, ref info, DibRgbColors);
                return copied == height;
            }
            finally
            {
                if (previous != 0)
                    SelectObject(memoryDc, previous);
                if (bitmap != 0)
                    DeleteObject(bitmap);
                if (memoryDc != 0)
                    DeleteDC(memoryDc);
                _ = ReleaseDC(0, sourceDc);
            }
        }

        public void Dispose()
        {
        }
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint destDc, int x, int y, int width, int height, nint srcDc, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, nint bits, ref BitmapInfo info, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }
}
