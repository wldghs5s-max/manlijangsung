using System.Runtime.InteropServices;
using OpenCvSharp;

enum FieldKey
{
    Up,
    Down,
    Left,
    Right,
    Secretary,
    Guknae,
    Jangan,
    Blink,
    Dash,
    Enter,
    Escape,
    Backspace,
    Digit0,
    Digit1,
    Digit2,
    Digit3,
    Digit4,
    Digit5,
    Digit6,
    Digit7,
    Digit8,
    Digit9
}

sealed record ClickTarget(int X, int Y, bool DoubleClick = false);

sealed record FieldView(
    string? Map,
    int? X,
    int? Y,
    bool NeedsBricks,
    bool UiBlocked,
    TimeSpan Now,
    bool RepairDialogOpen = false,
    bool WorldMapOpen = false,
    SmithyDialogState SmithyDialog = SmithyDialogState.None,
    BrickStock Stock = BrickStock.Unknown,
    bool ReturnConfirmOpen = false,
    bool PuzzleOpen = false);

sealed record FieldCommand(
    FieldKey? Hold,
    FieldKey? Tap,
    bool Dash,
    string Reason,
    ClickTarget? Click = null);

sealed class FieldPilot
{
    private static readonly TimeSpan TapGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan NumberGap = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DashGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DialogTapGap = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan RetryTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan StuckStepThreshold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DetourDuration = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ShortBlockTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LongBlockTtl = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan UnreachableTargetTtl = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan CellFreshWindow = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan MaxWallSweepTime = TimeSpan.FromSeconds(65);
    private const int CellSize = 4;
    private const int TargetFreshCellsPerVisit = 48;

    private static readonly (int X, int Y)[] SmithPath =
    [
        (16, 108),
        (40, 108),
        (40, 122),
        (53, 123),
        (59, 123)
    ];

    private static readonly (int X, int Y) JanganGate = (115, 7);
    private static readonly ClickTarget ReturnYesClick = new(700, 424);
    private static readonly ClickTarget SmithNpcClick = new(785, 226, DoubleClick: true);
    private static readonly ClickTarget SmithBuyButtonClick = new(960, 606);
    private static readonly ClickTarget SmithBrickRowClick = new(800, 464);
    private static readonly ClickTarget SmithListOkClick = new(786, 773);

    private readonly Random random;
    private readonly HashSet<string>? allowedWalls;
    private readonly string[] patrolWalls;
    private readonly int purchaseQuantity;
    private readonly Dictionary<(string Map, int X, int Y), TimeSpan> visitedTiles = new();
    private readonly Dictionary<(string Map, int Cx, int Cy), TimeSpan> visitedCells = new();
    private readonly Dictionary<(string Map, int Cx, int Cy), TimeSpan> unreachableCells = new();
    private readonly Dictionary<(string Map, int X, int Y), (TimeSpan Until, int Hits)> blockedTiles = new();
    private readonly HashSet<(string Map, int X, int Y)> knownWalkable = new();
    private readonly Dictionary<(string Map, int X, int Y), TimeSpan> visualBlocked = new();
    private readonly HashSet<(string Map, int X, int Y)> visualStaticBlocks = new();
    private readonly Dictionary<(string Map, int X, int Y), int> visualWalkableStreak = new();
    private readonly Dictionary<string, TimeSpan> lastVisualClearAt = new();
    private readonly Dictionary<string, TimeSpan> visualSuppressUntil = new();

    private string? waypointMap;
    private int waypointX;
    private int waypointY;
    private int waypointBlockedHits;
    private SupplyPhase phase = SupplyPhase.Patrol;
    private int sweepDirection = 1;
    private string? currentWallMap;
    private TimeSpan wallEnteredAt = TimeSpan.MinValue;
    private int freshCellsOnWall;
    private FieldKey? lastHeldDirection;
    private TimeSpan lastDirectionChangeAt = TimeSpan.MinValue;
    private FieldKey? lastBlockedDir;
    private FieldKey? escapeDirection;
    private TimeSpan escapeUntil = TimeSpan.MinValue;
    private (int X, int Y) escapeOrigin;
    private int escapeCycleIndex;
    private (string Map, int X, int Y)? lastEscapeAnchor;
    private readonly Queue<(TimeSpan At, string Map, int X, int Y)> recentWallSteps = new();
    private int supplyIssued;
    private TimeSpan supplyAt = TimeSpan.MinValue;
    private TimeSpan lastDash = TimeSpan.MinValue;
    private TimeSpan lastDialogTap = TimeSpan.MinValue;

    private int smithWaypointIndex;
    private int buyStep;
    private TimeSpan buyStepAt = TimeSpan.MinValue;
    private TimeSpan buyCloseAt = TimeSpan.MinValue;
    private SmithyDialogState lastBuyDialog;
    private int qtyDigitsSent;
    private bool purchaseSubmitted;
    private readonly int[] buyFallbackQuantities;
    private int buyAttemptIndex;
    private int backspacesRemaining;
    private TimeSpan buySubmitAt = TimeSpan.MinValue;
    private bool buyAborted;
    private bool quantityPromptDirty;
    private TimeSpan lastDoorStepAt = TimeSpan.MinValue;

    private int worldMapStep;
    private TimeSpan worldMapAt = TimeSpan.MinValue;
    private int returnConfirmStep;
    private TimeSpan returnConfirmAt = TimeSpan.MinValue;

    private string? lastMap;
    private int? lastX;
    private int? lastY;
    private (string Map, int X, int Y, TimeSpan At)? lastBumpedTile;
    private TimeSpan lastMovedAt = TimeSpan.MinValue;
    private TimeSpan detourUntil = TimeSpan.MinValue;
    private FieldKey? detourDirection;
    private int stuckCount;

    public FieldPilot(Random random, IReadOnlyCollection<string>? allowedWalls = null, int purchaseQuantity = 100)
    {
        this.random = random;
        this.purchaseQuantity = Math.Max(1, purchaseQuantity);
        buyFallbackQuantities = new[]
        {
            this.purchaseQuantity,
            (int)(this.purchaseQuantity * 0.8),
            (int)(this.purchaseQuantity * 0.5),
            20,
            10
        }
        .Where(q => q > 0 && q <= this.purchaseQuantity)
        .Distinct()
        .ToArray();
        if (buyFallbackQuantities.Length == 0)
            buyFallbackQuantities = [this.purchaseQuantity];
        if (allowedWalls is { Count: > 0 })
        {
            var filtered = allowedWalls
                .Where(WallAtlas.IsWall)
                .OrderBy(name => Array.IndexOf(WallAtlas.Walls, name))
                .ToArray();
            if (filtered.Length > 0)
            {
                this.allowedWalls = new HashSet<string>(filtered, StringComparer.Ordinal);
                patrolWalls = filtered;
                return;
            }
        }
        patrolWalls = WallAtlas.Walls;
    }

    public bool IsSupplying => phase != SupplyPhase.Patrol;
    public bool InBuyPhase => phase == SupplyPhase.Buy;
    public int LearnedBlockedCount => blockedTiles.Count + visualBlocked.Count;

    public string CurrentTargetSummary
    {
        get
        {
            if (phase != SupplyPhase.Patrol)
                return $"보급:{phase}";
            if (escapeDirection is FieldKey esc)
                return $"탈출:{esc}";
            if (waypointMap is not null)
                return waypointMap == currentWallMap && waypointX >= 0
                    ? $"{waypointMap}({waypointX},{waypointY})"
                    : $"{waypointMap}(성문)";
            return "목표없음";
        }
    }

    public string DescribeAdjacent(string map, int x, int y, TimeSpan now)
    {
        if (!WallAtlas.Maps.TryGetValue(map, out GameMap? area))
            return "-";

        string Cell(int nx, int ny)
        {
            if (nx < 0 || nx >= area.Width || ny < 0 || ny >= area.Height)
                return "B";
            if (blockedTiles.TryGetValue((map, nx, ny), out var entry) && entry.Until > now)
                return "P";
            if (visualBlocked.TryGetValue((map, nx, ny), out TimeSpan visUntil) && visUntil > now)
                return "V";
            if (WallAtlas.IsForbiddenPatrolTile(map, nx, ny, allowedWalls))
                return "F";
            return "O";
        }

        return $"U:{Cell(x, y - 1)} D:{Cell(x, y + 1)} L:{Cell(x - 1, y)} R:{Cell(x + 1, y)}";
    }

    public void ObserveTerrain(string map, IReadOnlyList<TileScan> scans, TimeSpan now)
    {
        if (!WallAtlas.IsWall(map))
            return;

        foreach (TileScan scan in scans)
        {
            if (lastMap == map && lastX == scan.X && lastY == scan.Y)
                continue;

            var key = (map, scan.X, scan.Y);
            if (scan.Walkable)
            {
                if (visualStaticBlocks.Contains(key)
                    && visualBlocked.TryGetValue(key, out TimeSpan vUntil)
                    && vUntil > now)
                {
                    int streak = visualWalkableStreak.TryGetValue(key, out int prev) ? prev + 1 : 1;
                    visualWalkableStreak[key] = streak;
                    if (streak < 2)
                        continue;
                }

                visualBlocked.Remove(key);
                visualStaticBlocks.Remove(key);
                visualWalkableStreak.Remove(key);
                if (!(blockedTiles.TryGetValue(key, out var bumped) && bumped.Until > now))
                    knownWalkable.Add(key);
                continue;
            }

            if (WallAtlas.WallPort(map, scan.X, scan.Y) is not null)
                continue;
            if (visualSuppressUntil.TryGetValue(map, out TimeSpan suppressUntil) && now < suppressUntil)
                continue;

            visualWalkableStreak.Remove(key);
            if (scan.IsStaticTerrain)
            {
                knownWalkable.Remove(key);
                visualStaticBlocks.Add(key);
            }
            else
            {
                visualStaticBlocks.Remove(key);
            }
            TimeSpan ttl = scan.IsStaticTerrain ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(20);
            visualBlocked[key] = now + ttl;
        }
    }

    public FieldCommand Decide(FieldView view)
    {
        FieldCommand command = DecideCore(view);
        if (command.Hold != lastHeldDirection)
        {
            lastHeldDirection = command.Hold;
            lastDirectionChangeAt = view.Now;
        }
        return command;
    }

    private FieldCommand DecideCore(FieldView view)
    {
        if (view.PuzzleOpen)
        {
            ResetStuck(view.Now);
            return Wait("허풍선 글자를 읽는 중");
        }

        if (view.UiBlocked)
        {
            ResetStuck(view.Now);
            return Wait("외부 UI가 열려 이동과 풍신보를 멈춤");
        }

        if (view.RepairDialogOpen)
        {
            ResetStuck(view.Now);
            if (lastDialogTap == TimeSpan.MinValue || view.Now - lastDialogTap >= DialogTapGap)
            {
                lastDialogTap = view.Now;
                return new FieldCommand(null, FieldKey.Enter, false, "수리 대화창을 다음으로 넘김");
            }
            return Wait("수리 대화창 처리 대기");
        }

        if (phase == SupplyPhase.Patrol && view.NeedsBricks)
        {
            phase = SupplyPhase.ToGuknae;
            supplyIssued = 0;
            supplyAt = TimeSpan.MinValue;
            returnConfirmStep = 0;
            returnConfirmAt = TimeSpan.MinValue;
            ResetStuck(view.Now);
        }

        if (phase == SupplyPhase.ToGuknae && !view.NeedsBricks)
        {
            phase = SupplyPhase.Patrol;
            supplyIssued = 0;
            returnConfirmStep = 0;
        }

        if (phase != SupplyPhase.Patrol)
            return Supply(view);

        if (view.WorldMapOpen)
        {
            if (worldMapAt == TimeSpan.MinValue || view.Now - worldMapAt >= TimeSpan.FromMilliseconds(1500))
            {
                worldMapAt = view.Now;
                return new FieldCommand(null, FieldKey.Enter, false, "지역 지도 이탈 감지 → 만리장성 재입장");
            }
            return Wait("만리장성 재입장 대기");
        }

        if (view.Map is not { } map || !WallAtlas.IsWall(map) || view.X is not int x || view.Y is not int y)
            return Wait("만리장성 좌표를 확인한 뒤에 이동");

        return MoveOnWall(map, x, y, view.Now);
    }

    private FieldCommand Supply(FieldView view)
    {
        switch (phase)
        {
            case SupplyPhase.ToGuknae:
                if (view.Map == WallAtlas.Guknae)
                {
                    phase = SupplyPhase.BlinkWest;
                    supplyIssued = 0;
                    supplyAt = TimeSpan.MinValue;
                    returnConfirmStep = 0;
                    break;
                }
                if (view.ReturnConfirmOpen)
                    return DecideReturnConfirm(view.Now);
                returnConfirmStep = 0;
                if (supplyIssued == 0)
                    return Issue(view.Now, FieldKey.Secretary, "벽돌이 없어 국내성 비서를 연다");
                if (view.Now - supplyAt < TapGap)
                    return Wait("국내성 비서 선택 대기");
                if (supplyIssued == 1)
                    return Issue(view.Now, FieldKey.Guknae, "국내성 비서를 사용");
                if (view.Now - supplyAt >= RetryTimeout)
                {
                    supplyIssued = 0;
                    break;
                }
                return Wait("국내성 도착 대기");

            case SupplyPhase.BlinkWest:
                if (view.Map == WallAtlas.Guknae && view.X is int bx && bx <= 40 && view.Y is int by && by is >= 90 and <= 130 && supplyIssued >= 3)
                {
                    phase = SupplyPhase.WalkSmith;
                    supplyIssued = 0;
                    smithWaypointIndex = 0;
                    ResetStuck(view.Now);
                    break;
                }
                if (supplyIssued == 0)
                    return Issue(view.Now, FieldKey.Blink, "비영사천문으로 국내성 서문으로 이동");
                if (supplyIssued == 1)
                {
                    if (view.Now - supplyAt < TapGap)
                        return Wait("비영사천문 서문 번호 대기");
                    return Issue(view.Now, FieldKey.Digit2, "비영사천문 서문(2) 선택");
                }
                if (supplyIssued == 2)
                {
                    if (view.Now - supplyAt < NumberGap)
                        return Wait("비영사천문 확인 대기");
                    return Issue(view.Now, FieldKey.Enter, "비영사천문 서문 이동 확정");
                }
                if (view.Now - supplyAt >= RetryTimeout)
                {
                    supplyIssued = 0;
                    break;
                }
                return Wait("국내성 서쪽 착지 대기");

            case SupplyPhase.WalkSmith:
                if (view.Map == WallAtlas.Smith)
                {
                    phase = SupplyPhase.Buy;
                    buyStep = 0;
                    buyStepAt = TimeSpan.MinValue;
                    buyCloseAt = TimeSpan.MinValue;
                    lastBuyDialog = SmithyDialogState.None;
                    qtyDigitsSent = 0;
                    purchaseSubmitted = false;
                    buyAttemptIndex = 0;
                    backspacesRemaining = 0;
                    buySubmitAt = TimeSpan.MinValue;
                    buyAborted = false;
                    quantityPromptDirty = false;
                    ResetStuck(view.Now);
                    return Wait("털보네대장간 도착");
                }
                if (view.Map != WallAtlas.Guknae || view.X is not int sx || view.Y is not int sy)
                    return Wait("국내성 좌표 대기");

                while (smithWaypointIndex < SmithPath.Length - 1)
                {
                    (int wx, int wy) = SmithPath[smithWaypointIndex];
                    if (Math.Max(Math.Abs(wx - sx), Math.Abs(wy - sy)) > 2)
                        break;
                    smithWaypointIndex++;
                }
                if (sx >= 48 && sy >= 115 && smithWaypointIndex < SmithPath.Length - 1)
                    smithWaypointIndex = SmithPath.Length - 1;
                else if (sx >= 38 && sy >= 118 && smithWaypointIndex < 3)
                    smithWaypointIndex = 3;

                // 1) Directly below (1~3 tiles) or at the door threshold: dash straight Up into the door!
                if (sx == 59 && sy is >= 120 and <= 124)
                    return HoldKey(FieldKey.Up, view.Now, "털보네대장간 문으로 풍신보 입장", allowDash: true);

                // 2) Never walk horizontally along the solid building wall line (y <= 121) near the smithy.
                //    Drop down onto the open street (y = 122..124) first using MoveTowardWithStuck so any NPC obstacle still detours cleanly.
                if (sx is >= 50 and <= 66 && sy <= 121 && sx != 59)
                    return MoveTowardWithStuck(view.Map, sx, sy, sx, 123, view.Now, "대장간 건물 벽에서 아래쪽 도로로 이동", blockMap: null, allowDash: false);

                (int tx, int ty) = SmithPath[smithWaypointIndex];
                int doorDist = Math.Abs(sx - 59) + Math.Abs(sy - 123);
                bool allowSmithDash = doorDist > 8;
                if (smithWaypointIndex == SmithPath.Length - 1
                    && sy is >= 122 and <= 124
                    && Math.Abs(sx - 59) <= 4
                    && sx != 59)
                {
                    if (lastMap != view.Map || lastX != sx || lastY != sy)
                    {
                        lastMap = view.Map;
                        lastX = sx;
                        lastY = sy;
                        lastMovedAt = view.Now;
                    }
                    else if (lastMovedAt != TimeSpan.MinValue && view.Now - lastMovedAt >= TimeSpan.FromMilliseconds(1200))
                    {
                        return MoveTowardWithStuck(view.Map, sx, sy, 59, sy == 122 ? 123 : 124, view.Now, "대장간 입구 아래 우회 정렬", blockMap: null, allowDash: false);
                    }

                    if (lastDoorStepAt == TimeSpan.MinValue || view.Now - lastDoorStepAt >= TimeSpan.FromMilliseconds(180))
                    {
                        lastDoorStepAt = view.Now;
                        FieldKey stepKey = sx < 59 ? FieldKey.Right : FieldKey.Left;
                        return new FieldCommand(null, stepKey, false, "털보네대장간 입구 아래 정밀 정렬");
                    }
                    return Wait("털보네대장간 입구 정렬 대기");
                }

                return MoveTowardWithStuck(view.Map, sx, sy, tx, ty, view.Now, "털보네대장간 입구 아래로 이동", blockMap: null, allowDash: allowSmithDash);

            case SupplyPhase.Buy:
                if (view.Map == WallAtlas.Guknae)
                {
                    phase = SupplyPhase.ToJangan;
                    supplyIssued = 0;
                    supplyAt = TimeSpan.MinValue;
                    ResetStuck(view.Now);
                    break;
                }
                if (view.Map != WallAtlas.Smith)
                    return Wait("털보네대장간 화면 대기");
                return DecideSmithyBuy(view);

            case SupplyPhase.ToJangan:
                if (view.Map == WallAtlas.Jangan)
                {
                    phase = SupplyPhase.BlinkNorth;
                    supplyIssued = 0;
                    supplyAt = TimeSpan.MinValue;
                    returnConfirmStep = 0;
                    break;
                }
                if (view.ReturnConfirmOpen)
                    return DecideReturnConfirm(view.Now);
                returnConfirmStep = 0;
                if (supplyIssued == 0)
                    return Issue(view.Now, FieldKey.Secretary, "장안성 비서를 연다");
                if (view.Now - supplyAt < TapGap)
                    return Wait("장안성 비서 선택 대기");
                if (supplyIssued == 1)
                    return Issue(view.Now, FieldKey.Jangan, "장안성 비서를 사용");
                if (view.Now - supplyAt >= RetryTimeout)
                {
                    supplyIssued = 0;
                    break;
                }
                return Wait("장안성 도착 대기");

            case SupplyPhase.BlinkNorth:
                if (view.Map == WallAtlas.Jangan && view.Y is int ny && ny <= 25 && supplyIssued >= 3)
                {
                    phase = SupplyPhase.WalkGate;
                    supplyIssued = 0;
                    worldMapStep = 0;
                    worldMapAt = TimeSpan.MinValue;
                    ResetStuck(view.Now);
                    break;
                }
                if (supplyIssued == 0)
                    return Issue(view.Now, FieldKey.Blink, "비영사천문으로 장안성 북문에 접근");
                if (supplyIssued == 1)
                {
                    if (view.Now - supplyAt < TapGap)
                        return Wait("비영사천문 북문 번호 대기");
                    return Issue(view.Now, FieldKey.Digit4, "비영사천문 북문(4) 선택");
                }
                if (supplyIssued == 2)
                {
                    if (view.Now - supplyAt < NumberGap)
                        return Wait("비영사천문 확인 대기");
                    return Issue(view.Now, FieldKey.Enter, "비영사천문 북문 이동 확정");
                }
                if (view.Now - supplyAt >= RetryTimeout)
                {
                    supplyIssued = 0;
                    break;
                }
                return Wait("장안성 북쪽 착지 대기");

            case SupplyPhase.WalkGate:
                if (WallAtlas.IsWall(view.Map))
                {
                    phase = SupplyPhase.Patrol;
                    waypointMap = null;
                    worldMapStep = 0;
                    ResetStuck(view.Now);
                    return view.X is int px && view.Y is int py
                        ? MoveOnWall(view.Map!, px, py, view.Now)
                        : Wait("만리장성에 들어와 순찰을 시작");
                }
                if (view.WorldMapOpen)
                    return DecideWorldMap(view.Now);

                worldMapStep = 0;
                if (view.Map != WallAtlas.Jangan || view.X is not int jx || view.Y is not int jy)
                    return Wait("장안성 북문 좌표 대기");
                bool allowGateDash = Math.Abs(jx - JanganGate.X) + Math.Abs(jy - JanganGate.Y) > 10;
                if (Math.Abs(jx - JanganGate.X) > 1 || jy > 10)
                    return MoveTowardWithStuck(view.Map, jx, jy, JanganGate.X, Math.Min(jy, JanganGate.Y), view.Now, "장안성 북문으로 이동", blockMap: null, allowDash: allowGateDash);
                return HoldKey(FieldKey.Up, view.Now, "장안성 북문 밖 지역 지도로 이동", allowDash: false);
        }

        return Supply(view);
    }

    private FieldCommand DecideSmithyBuy(FieldView view)
    {
        bool hasBricks = buyAborted || !view.NeedsBricks || (purchaseSubmitted && view.Stock is BrickStock.QuantityVisible or BrickStock.Two or BrickStock.One);
        if (hasBricks)
        {
            if (view.SmithyDialog != SmithyDialogState.None)
            {
                if (buyCloseAt == TimeSpan.MinValue || view.Now - buyCloseAt >= TimeSpan.FromMilliseconds(500))
                {
                    buyCloseAt = view.Now;
                    return new FieldCommand(null, FieldKey.Escape, false, "구매 완료 후 상점 대화창을 닫음");
                }
                return Wait("상점 대화창 종료 대기");
            }
            return HoldKey(FieldKey.Down, view.Now, "벽돌 구매 후 대장간 밖으로 이동", allowDash: false);
        }

        if (purchaseSubmitted
            && view.SmithyDialog == SmithyDialogState.None
            && buySubmitAt != TimeSpan.MinValue
            && view.Now - buySubmitAt < TimeSpan.FromMilliseconds(1500))
        {
            return Wait("구매 결과 확인 대기");
        }

        if (view.SmithyDialog != lastBuyDialog)
        {
            SmithyDialogState previousDialog = lastBuyDialog;
            lastBuyDialog = view.SmithyDialog;
            buyStep = 0;
            buyStepAt = TimeSpan.MinValue;
            bool hadPartialInput = qtyDigitsSent > 0 || purchaseSubmitted;
            qtyDigitsSent = 0;
            if (previousDialog == SmithyDialogState.QuantityPrompt && purchaseSubmitted)
            {
                purchaseSubmitted = false;
                quantityPromptDirty = true;
                buyAttemptIndex++;
                backspacesRemaining = 6;
                if (buyAttemptIndex >= buyFallbackQuantities.Length)
                {
                    buyAborted = true;
                    return Wait("구매 한도 도달로 대장간 이탈 준비");
                }
            }
            else if (previousDialog == SmithyDialogState.QuantityPrompt && hadPartialInput)
            {
                quantityPromptDirty = true;
            }
            if (view.SmithyDialog == SmithyDialogState.QuantityPrompt && (buyAttemptIndex > 0 || quantityPromptDirty))
                backspacesRemaining = 6;
        }

        switch (view.SmithyDialog)
        {
            case SmithyDialogState.None:
                if (buyStepAt == TimeSpan.MinValue || view.Now - buyStepAt >= TimeSpan.FromSeconds(2))
                {
                    buyStepAt = view.Now;
                    return new FieldCommand(null, null, false, "대장간 상인 클릭", SmithNpcClick);
                }
                return Wait("대장간 상인 대화창 대기");

            case SmithyDialogState.MainMenu:
                if (buyStepAt == TimeSpan.MinValue || view.Now - buyStepAt >= TimeSpan.FromMilliseconds(1200))
                {
                    buyStepAt = view.Now;
                    return new FieldCommand(null, null, false, "물건 사기 선택", SmithBuyButtonClick);
                }
                return Wait("물건 사기 목록 대기");

            case SmithyDialogState.ItemList:
                if (buyStep == 0 || view.Now - buyStepAt >= TimeSpan.FromMilliseconds(1500))
                {
                    buyStep = 1;
                    buyStepAt = view.Now;
                    return new FieldCommand(null, null, false, "판매 목록에서 벽돌 선택", SmithBrickRowClick);
                }
                if (buyStep == 1 && view.Now - buyStepAt >= TimeSpan.FromMilliseconds(200))
                {
                    buyStep = 2;
                    buyStepAt = view.Now;
                    return new FieldCommand(null, null, false, "벽돌 선택 확인 클릭", SmithListOkClick);
                }
                return Wait("수량 입력창 대기");

            case SmithyDialogState.QuantityPrompt:
                if (backspacesRemaining > 0)
                {
                    if (buyStepAt == TimeSpan.MinValue || view.Now - buyStepAt >= TimeSpan.FromMilliseconds(80))
                    {
                        backspacesRemaining--;
                        if (backspacesRemaining == 0)
                            quantityPromptDirty = false;
                        buyStepAt = view.Now;
                        return new FieldCommand(null, FieldKey.Backspace, false, "구매 수량 입력칸 초기화");
                    }
                    return Wait("구매 수량 입력칸 초기화 대기");
                }

                int targetQty = buyFallbackQuantities[Math.Min(buyAttemptIndex, buyFallbackQuantities.Length - 1)];
                string digits = targetQty.ToString();
                if (qtyDigitsSent < digits.Length)
                {
                    if (buyStepAt == TimeSpan.MinValue || view.Now - buyStepAt >= TimeSpan.FromMilliseconds(120))
                    {
                        FieldKey key = ToDigitKey(digits[qtyDigitsSent]);
                        qtyDigitsSent++;
                        quantityPromptDirty = true;
                        buyStepAt = view.Now;
                        return new FieldCommand(null, key, false, $"구매 수량 {targetQty} 입력 중");
                    }
                    return Wait("구매 수량 숫자 입력 대기");
                }
                if (!purchaseSubmitted && view.Now - buyStepAt >= TimeSpan.FromMilliseconds(120))
                {
                    purchaseSubmitted = true;
                    buySubmitAt = view.Now;
                    buyStepAt = view.Now;
                    return new FieldCommand(null, FieldKey.Enter, false, $"벽돌 {targetQty}개 구매 확정");
                }
                if (purchaseSubmitted && view.Now - buyStepAt >= TimeSpan.FromSeconds(2))
                {
                    buyAttemptIndex++;
                    if (buyAttemptIndex >= buyFallbackQuantities.Length)
                    {
                        buyAborted = true;
                        return Wait("구매 한도 도달로 대장간 이탈 준비");
                    }
                    qtyDigitsSent = 0;
                    purchaseSubmitted = false;
                    backspacesRemaining = 6;
                    buyStepAt = TimeSpan.MinValue;
                    return Wait($"구매가 확정되지 않아 {buyFallbackQuantities[buyAttemptIndex]}개로 재입력");
                }
                return Wait("벽돌 구매 완료 대기");

            default:
                return Wait("대장간 대화 상태 대기");
        }
    }

    private FieldCommand DecideReturnConfirm(TimeSpan now)
    {
        supplyAt = now;
        if (returnConfirmStep == 0 || now - returnConfirmAt >= TimeSpan.FromMilliseconds(1500))
        {
            returnConfirmStep = 1;
            returnConfirmAt = now;
            return new FieldCommand(null, null, false, "귀환 확인창에서 예 선택", ReturnYesClick);
        }
        if (returnConfirmStep == 1 && now - returnConfirmAt >= TimeSpan.FromMilliseconds(200))
        {
            returnConfirmStep = 2;
            returnConfirmAt = now;
            return new FieldCommand(null, FieldKey.Enter, false, "귀환 확인창에서 Enter로 이동 확정");
        }
        return Wait("마을 귀환 이동 대기");
    }

    private FieldCommand DecideWorldMap(TimeSpan now)
    {
        if (worldMapStep == 0 || now - worldMapAt >= TimeSpan.FromMilliseconds(2500))
        {
            worldMapStep = 1;
            worldMapAt = now;
            return new FieldCommand(null, FieldKey.Up, false, "지역 지도에서 장안성 위 만리장성으로 이동");
        }
        if (worldMapStep == 1 && now - worldMapAt >= TimeSpan.FromMilliseconds(800))
        {
            worldMapStep = 2;
            worldMapAt = now;
            return new FieldCommand(null, FieldKey.Enter, false, "지역 지도에서 만리장성 입장");
        }
        return Wait("지역 지도 만리장성 입장 대기");
    }

    private FieldCommand MoveOnWall(string map, int x, int y, TimeSpan now)
    {
        if (currentWallMap != map)
        {
            currentWallMap = map;
            wallEnteredAt = now;
            freshCellsOnWall = 0;
            waypointBlockedHits = 0;
            escapeDirection = null;
            escapeUntil = TimeSpan.MinValue;
            escapeCycleIndex = 0;
            lastEscapeAnchor = null;
            recentWallSteps.Clear();
            if (waypointMap == map)
            {
                waypointX = -1;
                waypointY = -1;
            }
        }

        DetectWallStuck(map, x, y, now);
        RecordVisit(map, x, y, now);
        bool safeDash = !WallAtlas.IsNearWorldExit(map, x, y, 12, allowedWalls);

        if (WallAtlas.IsForbiddenPatrolTile(map, x, y, allowedWalls) && WallAtlas.WallPort(map, x, y) is null)
        {
            waypointMap = null;
            escapeDirection = null;
            return HoldAway(map, x, y, now, "성문이나 성벽 외곽 안전 구역에서 안쪽으로 이동", allowDash: false);
        }

        if (escapeDirection is FieldKey escKey && now < escapeUntil)
        {
            (int ex, int ey) = StepDelta(escKey);
            int nx = x + ex;
            int ny = y + ey;
            GameMap area = WallAtlas.Maps[map];
            bool escaped = Math.Abs(x - escapeOrigin.X) + Math.Abs(y - escapeOrigin.Y) >= 6;
            bool invalidNext = nx < 0 || nx >= area.Width || ny < 0 || ny >= area.Height
                || IsBlocked(map, nx, ny, now)
                || WallAtlas.IsForbiddenPatrolTile(map, nx, ny, allowedWalls);
            if (!escaped && !invalidNext)
                return HoldKey(escKey, now, $"{map} 깃발·벽 구간 수직 탈출", allowDash: safeDash);

            escapeDirection = null;
            escapeUntil = TimeSpan.MinValue;
        }

        if (patrolWalls.Length > 1
            && (waypointMap is null || waypointMap == map)
            && (freshCellsOnWall >= TargetFreshCellsPerVisit
                || (wallEnteredAt != TimeSpan.MinValue && now - wallEnteredAt >= MaxWallSweepTime)))
        {
            PickNextSweepWall(map);
        }

        if (!HasWaypoint(map, x, y, now))
            ChooseUnvisitedWaypoint(map, x, y, now);

        if (waypointMap != map)
        {
            visualSuppressUntil.Remove(map);
            MapPort hop = WallAtlas.Hop(map, waypointMap!);
            if (x == hop.X && y == hop.Y)
            {
                FieldKey? outward = WallAtlas.OutwardDirection(map, hop);
                return outward is FieldKey key
                    ? HoldKey(key, now, $"{hop.Destination}으로 이어진 성문을 통과", allowDash: false)
                    : Wait($"{hop.Destination}으로 이어진 성문에 도착");
            }

            FieldKey? portalStep = FindNextStepBfs(map, x, y, hop.X, hop.Y, now, map, allowGoalOnPort: true);
            if (portalStep is null)
            {
                ClearVisualBlocksOnMap(map);
                portalStep = FindNextStepBfs(map, x, y, hop.X, hop.Y, now, map, allowGoalOnPort: true);
            }
            if (portalStep is null)
            {
                ClearBlocksNear(map, x, y, 2, now);
                portalStep = FindNextStepBfs(map, x, y, hop.X, hop.Y, now, map, allowGoalOnPort: true);
            }
            if (portalStep is null)
            {
                ClearBlocksOnMap(map, now);
                portalStep = FindNextStepBfs(map, x, y, hop.X, hop.Y, now, map, allowGoalOnPort: true);
            }
            return portalStep is FieldKey nextStep
                ? HoldKey(nextStep, now, $"{hop.Destination}으로 이어진 성문으로 이동", allowDash: safeDash)
                : HoldToward(map, x, y, hop.X, hop.Y, now, $"{hop.Destination}으로 이어진 성문으로 이동", map, allowDash: safeDash);
        }

        FieldKey? step = FindNextStepBfs(map, x, y, waypointX, waypointY, now, map, allowGoalOnPort: false);
        if (step is null)
        {
            MarkBlocked(map, waypointX, waypointY, now, UnreachableTargetTtl);
            unreachableCells[(map, waypointX / CellSize, waypointY / CellSize)] = now + UnreachableTargetTtl;
            waypointMap = null;
            ChooseUnvisitedWaypoint(map, x, y, now);
            if (waypointMap != map)
                return MoveOnWall(map, x, y, now);

            step = FindNextStepBfs(map, x, y, waypointX, waypointY, now, map, allowGoalOnPort: false);
            if (step is null && TryClearVisualBlocksOnMap(map, now))
            {
                step = FindNextStepBfs(map, x, y, waypointX, waypointY, now, map, allowGoalOnPort: false);
            }
            if (step is null)
            {
                ClearBlocksNear(map, x, y, 2, now);
                step = FindNextStepBfs(map, x, y, waypointX, waypointY, now, map, allowGoalOnPort: false);
            }
        }

        return step is FieldKey moveKey
            ? StabilizePatrolDirection(map, x, y, moveKey, now, $"{map} 미방문 구역 순찰", allowDash: safeDash)
            : HoldToward(map, x, y, waypointX, waypointY, now, $"{map} 안을 이동", map, allowDash: safeDash);
    }

    private bool HasWaypoint(string map, int x, int y, TimeSpan now)
    {
        if (waypointMap is null || !WallAtlas.IsWall(waypointMap))
            return false;
        if (allowedWalls is not null && !allowedWalls.Contains(waypointMap))
            return false;
        if (waypointMap == map)
        {
            if (waypointX < 0 || Math.Max(Math.Abs(waypointX - x), Math.Abs(waypointY - y)) <= 2)
                return false;
            if (visitedCells.TryGetValue((map, waypointX / CellSize, waypointY / CellSize), out TimeSpan cellVisit)
                && now - cellVisit < TimeSpan.FromSeconds(15))
            {
                return false;
            }
        }
        return true;
    }

    private void PickNextSweepWall(string map)
    {
        int currentIndex = Array.IndexOf(patrolWalls, map);
        if (currentIndex < 0)
        {
            waypointMap = patrolWalls[0];
            waypointX = -1;
            waypointY = -1;
            return;
        }

        int nextIndex = currentIndex + sweepDirection;
        if (nextIndex < 0 || nextIndex >= patrolWalls.Length)
        {
            sweepDirection = -sweepDirection;
            nextIndex = currentIndex + sweepDirection;
        }

        waypointMap = patrolWalls[Math.Clamp(nextIndex, 0, patrolWalls.Length - 1)];
        waypointX = -1;
        waypointY = -1;
    }

    private void ChooseUnvisitedWaypoint(string map, int startX, int startY, TimeSpan now)
    {
        GameMap area = WallAtlas.Maps[map];
        int width = area.Width;
        int height = area.Height;

        (int PortX, int PortY)? nextPort = null;
        if (patrolWalls.Length > 1)
        {
            int idx = Array.IndexOf(patrolWalls, map);
            if (idx >= 0)
            {
                int dir = idx + sweepDirection < 0 || idx + sweepDirection >= patrolWalls.Length
                    ? -sweepDirection
                    : sweepDirection;
                string nextWall = patrolWalls[Math.Clamp(idx + dir, 0, patrolWalls.Length - 1)];
                if (nextWall != map)
                {
                    MapPort port = WallAtlas.Hop(map, nextWall);
                    nextPort = (port.X, port.Y);
                }
            }
        }

        int cellCols = (width + CellSize - 1) / CellSize;
        int cellRows = (height + CellSize - 1) / CellSize;
        var bestCellTile = new (int X, int Y, int Dist)[cellCols * cellRows];

        (int BestX, int BestY, bool AnyUnvisited) FindBestWaypoint()
        {
            int[] dist = ComputeBfsDistances(map, startX, startY, now, map);
            Array.Fill(bestCellTile, (-1, -1, int.MaxValue));

            for (int ty = 1; ty < height - 1; ty++)
            {
                for (int tx = 1; tx < width - 1; tx++)
                {
                    if (WallAtlas.IsForbiddenPatrolTile(map, tx, ty, allowedWalls)
                        || WallAtlas.IsInAllowedWallCorridor(map, tx, ty, allowedWalls))
                    {
                        continue;
                    }
                    int d = dist[(ty * width) + tx];
                    if (d < 3)
                        continue;
                    int cellIdx = ((ty / CellSize) * cellCols) + (tx / CellSize);
                    if (d < bestCellTile[cellIdx].Dist)
                        bestCellTile[cellIdx] = (tx, ty, d);
                }
            }

            bool deadEndWall = WallAtlas.Maps[map].Ports.Count(port => port.StayOnWall) <= 1;
            bool anyUnvisitedCell = false;
            double bestScore = double.NegativeInfinity;
            int bestX = -1;
            int bestY = -1;

            for (int cy = 0; cy < cellRows; cy++)
            {
                for (int cx = 0; cx < cellCols; cx++)
                {
                    (int tx, int ty, int d) = bestCellTile[(cy * cellCols) + cx];
                    if (tx < 0)
                        continue;
                    if (unreachableCells.TryGetValue((map, cx, cy), out TimeSpan unreachableUntil) && unreachableUntil > now)
                        continue;

                    bool visited = visitedCells.TryGetValue((map, cx, cy), out TimeSpan lastVisit);
                    bool freshCell = !visited || now - lastVisit >= CellFreshWindow;
                    if (freshCell)
                        anyUnvisitedCell = true;

                    double freshness = !visited
                        ? 1_000_000.0
                        : Math.Min(500_000.0, (now - lastVisit).TotalSeconds * 500.0);
                    double walkableBonus = knownWalkable.Contains((map, tx, ty)) ? 150.0 : 0.0;
                    double distPenalty = d * 6.0;
                    double momentumBonus = lastHeldDirection switch
                    {
                        FieldKey.Right when tx > startX => 18.0,
                        FieldKey.Left when tx < startX => 18.0,
                        FieldKey.Down when ty > startY => 18.0,
                        FieldKey.Up when ty < startY => 18.0,
                        _ => 0.0
                    };
                    double openSideBonus = lastBlockedDir switch
                    {
                        FieldKey.Left when tx >= startX + 3 => 30.0,
                        FieldKey.Right when tx <= startX - 3 => 30.0,
                        FieldKey.Up when ty >= startY + 3 => 30.0,
                        FieldKey.Down when ty <= startY - 3 => 30.0,
                        _ => 0.0
                    };
                    double sweepBias = 0.0;
                    if (nextPort is (int px, int py))
                    {
                        int portDist = Math.Abs(tx - px) + Math.Abs(ty - py);
                        if (!deadEndWall)
                            sweepBias = -0.15 * portDist;
                        else if (freshCellsOnWall < TargetFreshCellsPerVisit / 2)
                            sweepBias = 1.5 * portDist;
                        else
                            sweepBias = -0.8 * portDist;
                    }
                    double score = freshness + walkableBonus - distPenalty + momentumBonus + openSideBonus + sweepBias + (random.NextDouble() * 0.5);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestX = tx;
                        bestY = ty;
                    }
                }
            }

            return (bestX, bestY, anyUnvisitedCell);
        }

        var pick = FindBestWaypoint();
        if (pick.BestX < 0 && visualBlocked.Keys.Any(k => k.Map == map) && TryClearVisualBlocksOnMap(map, now))
        {
            pick = FindBestWaypoint();
        }

        if (!pick.AnyUnvisited && patrolWalls.Length > 1 && (pick.BestX >= 0 || freshCellsOnWall >= TargetFreshCellsPerVisit))
        {
            PickNextSweepWall(map);
            return;
        }

        if (pick.BestX >= 0)
        {
            waypointMap = map;
            waypointX = pick.BestX;
            waypointY = pick.BestY;
            waypointBlockedHits = 0;
            return;
        }

        if (patrolWalls.Length > 1 && freshCellsOnWall >= TargetFreshCellsPerVisit)
        {
            PickNextSweepWall(map);
            return;
        }

        ClearBlocksOnMap(map, now);
        waypointMap = map;
        waypointX = Math.Clamp(width / 2, WallAtlas.EdgeBuffer, width - 1 - WallAtlas.EdgeBuffer);
        waypointY = Math.Clamp(height / 2, WallAtlas.EdgeBuffer, height - 1 - WallAtlas.EdgeBuffer);
    }

    private void DetectWallStuck(string map, int x, int y, TimeSpan now)
    {
        recentWallSteps.Enqueue((now, map, x, y));
        while (recentWallSteps.Count > 0 && now - recentWallSteps.Peek().At > TimeSpan.FromSeconds(5))
            recentWallSteps.Dequeue();

        if (recentWallSteps.Count >= 10
            && now - recentWallSteps.Peek().At >= TimeSpan.FromMilliseconds(3200)
            && recentWallSteps.All(item => item.Map == map))
        {
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            var distinct = new HashSet<(int X, int Y)>();
            foreach (var item in recentWallSteps)
            {
                if (item.X < minX) minX = item.X;
                if (item.X > maxX) maxX = item.X;
                if (item.Y < minY) minY = item.Y;
                if (item.Y > maxY) maxY = item.Y;
                distinct.Add((item.X, item.Y));
            }

            bool verticalStrip = (maxX - minX) <= 1 && (maxY - minY) <= 8;
            bool horizontalStrip = (maxY - minY) <= 1 && (maxX - minX) <= 8;
            bool smallBox = (maxX - minX) <= 3 && (maxY - minY) <= 3;
            bool fewTiles = distinct.Count <= 4;

            if (verticalStrip || horizontalStrip || smallBox || fewTiles)
            {
                if (distinct.Count <= 1 && lastHeldDirection is FieldKey stuckDir)
                {
                    (int sdx, int sdy) = StepDelta(stuckDir);
                    if (sdx != 0 || sdy != 0)
                    {
                        MarkBlocked(map, x + sdx, y + sdy, now);
                        lastBlockedDir = stuckDir;
                    }
                }

                if (waypointMap == map && waypointX >= 0 && waypointY >= 0)
                {
                    unreachableCells[(map, waypointX / CellSize, waypointY / CellSize)] = now + UnreachableTargetTtl;
                    waypointMap = null;
                    waypointBlockedHits = 0;
                }

                if (lastEscapeAnchor is { } anc
                    && anc.Map == map
                    && Math.Abs(anc.X - x) + Math.Abs(anc.Y - y) <= 12)
                {
                    escapeCycleIndex++;
                }
                else
                {
                    escapeCycleIndex = 0;
                    lastEscapeAnchor = (map, x, y);
                }

                escapeDirection = ChooseEscapeDirection(map, x, y, now, verticalStrip, horizontalStrip, lastHeldDirection, escapeCycleIndex);
                if (escapeDirection is not null)
                {
                    escapeUntil = now + TimeSpan.FromMilliseconds(2400);
                    escapeOrigin = (x, y);
                }

                recentWallSteps.Clear();
                lastMovedAt = now;
                return;
            }
        }

        if (lastMap != map || lastX != x || lastY != y)
        {
            lastMap = map;
            lastX = x;
            lastY = y;
            lastMovedAt = now;
            return;
        }

        if (lastHeldDirection is not FieldKey held || lastMovedAt == TimeSpan.MinValue || now - lastMovedAt < StuckStepThreshold)
            return;

        (int stepX, int stepY) = StepDelta(held);
        if (stepX == 0 && stepY == 0)
            return;

        bool pushingPortal = waypointMap is not null
            && waypointMap != map
            && WallAtlas.Hop(map, waypointMap) is MapPort hop
            && hop.X == x
            && hop.Y == y;

        if (!pushingPortal)
        {
            int bx = x + stepX;
            int by = y + stepY;
            MarkBlocked(map, bx, by, now);
            lastBlockedDir = held;
            waypointBlockedHits++;
            if (escapeDirection == held)
            {
                escapeDirection = null;
                escapeUntil = TimeSpan.MinValue;
            }
            if (waypointMap == map && ((bx == waypointX && by == waypointY) || waypointBlockedHits >= 2))
            {
                if (waypointX >= 0 && waypointY >= 0)
                    unreachableCells[(map, waypointX / CellSize, waypointY / CellSize)] = now + UnreachableTargetTtl;
                waypointMap = null;
                waypointBlockedHits = 0;
            }
        }
        lastMovedAt = now;
    }

    private FieldKey? ChooseEscapeDirection(
        string map,
        int x,
        int y,
        TimeSpan now,
        bool verticalStrip,
        bool horizontalStrip,
        FieldKey? avoidDirection,
        int cycleIndex)
    {
        GameMap area = WallAtlas.Maps[map];
        FieldKey[] priority;
        if (cycleIndex >= 1)
        {
            FieldKey centerH = x > area.Width / 2 ? FieldKey.Left : FieldKey.Right;
            FieldKey outerH = centerH == FieldKey.Left ? FieldKey.Right : FieldKey.Left;
            FieldKey centerV = y > area.Height / 2 ? FieldKey.Up : FieldKey.Down;
            FieldKey outerV = centerV == FieldKey.Up ? FieldKey.Down : FieldKey.Up;
            FieldKey[] ring = [centerH, centerV, outerH, outerV];
            int shift = (cycleIndex - 1) % ring.Length;
            priority =
            [
                ring[shift],
                ring[(shift + 1) % ring.Length],
                ring[(shift + 2) % ring.Length],
                ring[(shift + 3) % ring.Length]
            ];
        }
        else
        {
            priority = verticalStrip
                ? [FieldKey.Right, FieldKey.Left, FieldKey.Down, FieldKey.Up]
                : horizontalStrip
                    ? [FieldKey.Down, FieldKey.Up, FieldKey.Right, FieldKey.Left]
                    : [FieldKey.Right, FieldKey.Left, FieldKey.Down, FieldKey.Up];
        }

        FieldKey? PickBest(bool allowAvoid)
        {
            FieldKey? bestKey = null;
            int bestDepth = -1;
            TimeSpan oldestVisit = TimeSpan.MaxValue;

            for (int i = 0; i < priority.Length; i++)
            {
                FieldKey candidate = priority[i];
                if (!allowAvoid && candidate == avoidDirection)
                    continue;
                (int dx, int dy) = StepDelta(candidate);
                int nx = x + dx;
                int ny = y + dy;
                if (nx < 0 || nx >= area.Width || ny < 0 || ny >= area.Height)
                    continue;
                if (IsBlocked(map, nx, ny, now) || WallAtlas.IsForbiddenPatrolTile(map, nx, ny, allowedWalls))
                    continue;

                int depth = 1;
                int nx2 = nx + dx;
                int ny2 = ny + dy;
                if (nx2 >= 0 && nx2 < area.Width && ny2 >= 0 && ny2 < area.Height
                    && !IsBlocked(map, nx2, ny2, now)
                    && !WallAtlas.IsForbiddenPatrolTile(map, nx2, ny2, allowedWalls))
                {
                    depth = 2;
                }

                TimeSpan visitedAt = visitedTiles.TryGetValue((map, nx, ny), out TimeSpan t) ? t : TimeSpan.MinValue;
                bool preferAxis = cycleIndex >= 1
                    ? i == 0
                    : (verticalStrip && candidate is FieldKey.Right or FieldKey.Left)
                        || (horizontalStrip && candidate is FieldKey.Down or FieldKey.Up);
                int scoreDepth = depth + (preferAxis ? 2 : 0);

                if (scoreDepth > bestDepth || (scoreDepth == bestDepth && visitedAt < oldestVisit))
                {
                    bestDepth = scoreDepth;
                    oldestVisit = visitedAt;
                    bestKey = candidate;
                }
            }

            return bestKey;
        }

        return PickBest(allowAvoid: false) ?? PickBest(allowAvoid: true);
    }

    private void RecordVisit(string map, int x, int y, TimeSpan now)
    {
        var key = (map, x, y);
        blockedTiles.Remove(key);
        visualBlocked.Remove(key);
        visualStaticBlocks.Remove(key);
        visualWalkableStreak.Remove(key);
        knownWalkable.Add(key);
        visitedTiles[key] = now;
        (int cx, int cy) = (x / CellSize, y / CellSize);
        if (!visitedCells.TryGetValue((map, cx, cy), out TimeSpan lastCell) || now - lastCell >= CellFreshWindow)
            freshCellsOnWall++;
        visitedCells[(map, cx, cy)] = now;
    }

    private void MarkBlocked(string map, int x, int y, TimeSpan now, TimeSpan? overrideTtl = null)
    {
        GameMap area = WallAtlas.Maps[map];
        if (x < 0 || x >= area.Width || y < 0 || y >= area.Height)
            return;

        knownWalkable.Remove((map, x, y));
        lastBumpedTile = (map, x, y, now);
        int hits = blockedTiles.TryGetValue((map, x, y), out var existing) ? existing.Hits + 1 : 1;
        TimeSpan ttl = overrideTtl ?? (hits >= 2 ? LongBlockTtl : ShortBlockTtl);
        blockedTiles[(map, x, y)] = (now + ttl, hits);
    }

    private bool IsBlocked(string map, int x, int y, TimeSpan now) =>
        (blockedTiles.TryGetValue((map, x, y), out var entry) && entry.Until > now)
        || (visualBlocked.TryGetValue((map, x, y), out TimeSpan visualUntil) && visualUntil > now);

    private void ClearVisualBlocksNear(string map, int x, int y, int radius)
    {
        foreach (var key in visualBlocked.Keys.Where(k => k.Map == map && Math.Max(Math.Abs(k.X - x), Math.Abs(k.Y - y)) <= radius).ToArray())
        {
            visualBlocked.Remove(key);
            visualStaticBlocks.Remove(key);
            visualWalkableStreak.Remove(key);
        }
    }

    private bool TryClearVisualBlocksOnMap(string map, TimeSpan now)
    {
        if (!visualBlocked.Keys.Any(k => k.Map == map))
            return false;
        if (lastVisualClearAt.TryGetValue(map, out TimeSpan lastClear)
            && now - lastClear < TimeSpan.FromSeconds(8))
        {
            return false;
        }
        lastVisualClearAt[map] = now;
        visualSuppressUntil[map] = now + TimeSpan.FromSeconds(4);
        ClearVisualBlocksOnMap(map);
        return true;
    }

    private FieldCommand StabilizePatrolDirection(string map, int x, int y, FieldKey candidate, TimeSpan now, string reason, bool allowDash = true)
    {
        if (lastHeldDirection is FieldKey prev
            && IsOpposite(prev, candidate)
            && lastDirectionChangeAt != TimeSpan.MinValue
            && now - lastDirectionChangeAt < TimeSpan.FromMilliseconds(350))
        {
            (int dx, int dy) = StepDelta(prev);
            int nx = x + dx;
            int ny = y + dy;
            GameMap area = WallAtlas.Maps[map];
            if (nx >= 0 && nx < area.Width && ny >= 0 && ny < area.Height
                && !IsBlocked(map, nx, ny, now)
                && !WallAtlas.IsForbiddenPatrolTile(map, nx, ny, allowedWalls))
            {
                return HoldKey(prev, now, reason, allowDash);
            }
        }
        return HoldKey(candidate, now, reason, allowDash);
    }

    private static bool IsOpposite(FieldKey a, FieldKey b) => (a, b) switch
    {
        (FieldKey.Up, FieldKey.Down) or (FieldKey.Down, FieldKey.Up) => true,
        (FieldKey.Left, FieldKey.Right) or (FieldKey.Right, FieldKey.Left) => true,
        _ => false
    };

    private void ClearVisualBlocksOnMap(string map)
    {
        foreach (var key in visualBlocked.Keys.Where(k => k.Map == map).ToArray())
        {
            visualBlocked.Remove(key);
            visualStaticBlocks.Remove(key);
            visualWalkableStreak.Remove(key);
        }
    }

    private bool ShouldKeepLastBumped((string Map, int X, int Y) key, TimeSpan now) =>
        lastBumpedTile is { } b && b.Map == key.Map && b.X == key.X && b.Y == key.Y && now - b.At <= TimeSpan.FromSeconds(5);

    private void ClearBlocksNear(string map, int x, int y, int radius, TimeSpan now)
    {
        foreach (var key in blockedTiles.Keys.Where(k => k.Map == map && Math.Max(Math.Abs(k.X - x), Math.Abs(k.Y - y)) <= radius).ToArray())
        {
            if (!ShouldKeepLastBumped(key, now))
                blockedTiles.Remove(key);
        }
        ClearVisualBlocksNear(map, x, y, radius);
    }

    private void ClearBlocksOnMap(string map, TimeSpan now)
    {
        foreach (var key in blockedTiles.Keys.Where(k => k.Map == map).ToArray())
        {
            if (!ShouldKeepLastBumped(key, now))
                blockedTiles.Remove(key);
        }
        ClearVisualBlocksOnMap(map);
    }

    private int[] ComputeBfsDistances(string map, int startX, int startY, TimeSpan now, string? blockPortMap)
    {
        GameMap area = WallAtlas.Maps[map];
        int width = area.Width;
        int height = area.Height;
        int[] dist = new int[width * height];
        Array.Fill(dist, -1);
        if (startX < 0 || startX >= width || startY < 0 || startY >= height)
            return dist;

        var queue = new Queue<int>();
        int startIdx = (startY * width) + startX;
        dist[startIdx] = 0;
        queue.Enqueue(startIdx);

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int cx = cur % width;
            int cy = cur / width;
            int nextDist = dist[cur] + 1;

            foreach ((int dx, int dy) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                    continue;
                int nIdx = (ny * width) + nx;
                if (dist[nIdx] >= 0)
                    continue;
                if (IsBlocked(map, nx, ny, now))
                    continue;
                if (blockPortMap is not null && WallAtlas.IsForbiddenPatrolTile(blockPortMap, nx, ny, allowedWalls))
                    continue;
                dist[nIdx] = nextDist;
                queue.Enqueue(nIdx);
            }
        }

        return dist;
    }

    private FieldKey? FindNextStepBfs(
        string map,
        int startX,
        int startY,
        int goalX,
        int goalY,
        TimeSpan now,
        string? blockPortMap,
        bool allowGoalOnPort)
    {
        if (startX == goalX && startY == goalY)
            return null;

        GameMap area = WallAtlas.Maps[map];
        int width = area.Width;
        int height = area.Height;
        if (startX < 0 || startX >= width || startY < 0 || startY >= height)
            return null;
        if (goalX < 0 || goalX >= width || goalY < 0 || goalY >= height)
            return null;

        bool[] visited = new bool[width * height];
        var firstStep = new FieldKey[width * height];
        var queue = new Queue<int>();
        int startIdx = (startY * width) + startX;
        visited[startIdx] = true;

        var startNeighbors = new (int Dx, int Dy, FieldKey Key, TimeSpan VisitedAt)[4];
        (int Dx, int Dy, FieldKey Key)[] dirs = [(0, -1, FieldKey.Up), (0, 1, FieldKey.Down), (-1, 0, FieldKey.Left), (1, 0, FieldKey.Right)];
        for (int i = 0; i < 4; i++)
        {
            int nx = startX + dirs[i].Dx;
            int ny = startY + dirs[i].Dy;
            TimeSpan visitedAt = visitedTiles.TryGetValue((map, nx, ny), out TimeSpan t) ? t : TimeSpan.MinValue;
            startNeighbors[i] = (dirs[i].Dx, dirs[i].Dy, dirs[i].Key, visitedAt);
        }
        Array.Sort(startNeighbors, (a, b) => a.VisitedAt.CompareTo(b.VisitedAt));

        foreach ((int dx, int dy, FieldKey key, _) in startNeighbors)
        {
            int nx = startX + dx;
            int ny = startY + dy;
            if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                continue;
            bool isGoal = nx == goalX && ny == goalY;
            if (!isGoal || !allowGoalOnPort)
            {
                if (IsBlocked(map, nx, ny, now))
                    continue;
                if (blockPortMap is not null && WallAtlas.IsForbiddenPatrolTile(blockPortMap, nx, ny, allowedWalls))
                    continue;
            }
            if (isGoal)
                return key;

            int nIdx = (ny * width) + nx;
            visited[nIdx] = true;
            firstStep[nIdx] = key;
            queue.Enqueue(nIdx);
        }

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int cx = cur % width;
            int cy = cur / width;
            FieldKey step = firstStep[cur];

            foreach ((int dx, int dy, _) in dirs)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                    continue;
                int nIdx = (ny * width) + nx;
                if (visited[nIdx])
                    continue;

                bool isGoal = nx == goalX && ny == goalY;
                if (!isGoal || !allowGoalOnPort)
                {
                    if (IsBlocked(map, nx, ny, now))
                        continue;
                    if (blockPortMap is not null && WallAtlas.IsForbiddenPatrolTile(blockPortMap, nx, ny, allowedWalls))
                        continue;
                }
                if (isGoal)
                    return step;

                visited[nIdx] = true;
                firstStep[nIdx] = step;
                queue.Enqueue(nIdx);
            }
        }

        return null;
    }

    private static (int StepX, int StepY) StepDelta(FieldKey key) => key switch
    {
        FieldKey.Up => (0, -1),
        FieldKey.Down => (0, 1),
        FieldKey.Left => (-1, 0),
        FieldKey.Right => (1, 0),
        _ => (0, 0)
    };

    private FieldCommand MoveTowardWithStuck(
        string map,
        int x,
        int y,
        int targetX,
        int targetY,
        TimeSpan now,
        string reason,
        string? blockMap,
        bool allowDash = true)
    {
        if (lastMap != map || lastX != x || lastY != y)
        {
            lastMap = map;
            lastX = x;
            lastY = y;
            lastMovedAt = now;
        }
        else if (detourUntil > now && detourDirection is FieldKey activeDetour && CanStep(map, x, y, activeDetour, blockMap))
        {
            return HoldKey(activeDetour, now, $"{reason} (장애물 우회 중)", allowDash);
        }
        else if (lastMovedAt != TimeSpan.MinValue && now - lastMovedAt >= StuckThreshold)
        {
            detourDirection = ChooseDetourDirection(map, x, y, targetX, targetY, blockMap, stuckCount);
            lastMovedAt = now;
            stuckCount++;
            if (detourDirection is FieldKey nextDetour)
            {
                detourUntil = now + DetourDuration;
                return HoldKey(nextDetour, now, $"{reason} (길막 감지 후 우회)", allowDash);
            }
        }

        return HoldToward(map, x, y, targetX, targetY, now, reason, blockMap, allowDash);
    }

    private void ResetStuck(TimeSpan now)
    {
        lastMovedAt = now;
        detourUntil = TimeSpan.MinValue;
        detourDirection = null;
        stuckCount = 0;
        escapeDirection = null;
        escapeUntil = TimeSpan.MinValue;
        escapeCycleIndex = 0;
        lastEscapeAnchor = null;
        lastDirectionChangeAt = TimeSpan.MinValue;
        recentWallSteps.Clear();
    }

    private FieldCommand HoldToward(string? map, int x, int y, int targetX, int targetY, TimeSpan now, string reason, string? blockMap, bool allowDash = true)
    {
        FieldKey? direction = ChooseDirection(map, x, y, targetX, targetY, blockMap, now);
        if (direction is null)
            return Wait(reason);
        return HoldKey(direction.Value, now, reason, allowDash);
    }

    private FieldCommand HoldKey(FieldKey direction, TimeSpan now, string reason, bool allowDash = true)
    {
        bool dash = allowDash && (lastDash == TimeSpan.MinValue || now - lastDash >= DashGap);
        if (dash)
            lastDash = now;
        return new FieldCommand(direction, null, dash, reason);
    }

    private FieldCommand HoldAway(string map, int x, int y, TimeSpan now, string reason, bool allowDash = true)
    {
        GameMap area = WallAtlas.Maps[map];
        int centerX = area.Width / 2;
        int centerY = area.Height / 2;
        FieldKey? bestKey = null;
        int bestPassable = -1;
        int bestPortDist = -1;
        int bestCenterDist = int.MaxValue;

        foreach ((int stepX, int stepY) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
        {
            int nextX = x + stepX;
            int nextY = y + stepY;
            if (nextX < 0 || nextX >= area.Width || nextY < 0 || nextY >= area.Height)
                continue;

            int passable = IsBlocked(map, nextX, nextY, now) ? 0 : 1;
            int portDist = WallAtlas.SafetyDistance(map, nextX, nextY, allowedWalls);
            int centerDist = Math.Abs(nextX - centerX) + Math.Abs(nextY - centerY);
            if (passable > bestPassable
                || (passable == bestPassable && (portDist > bestPortDist || (portDist == bestPortDist && centerDist < bestCenterDist))))
            {
                bestPassable = passable;
                bestPortDist = portDist;
                bestCenterDist = centerDist;
                bestKey = ToKey(stepX, stepY);
            }
        }

        return bestKey is FieldKey key ? HoldKey(key, now, reason, allowDash) : Wait(reason);
    }

    private FieldKey? ChooseDirection(string? map, int x, int y, int targetX, int targetY, string? blockMap, TimeSpan now)
    {
        int dx = Math.Sign(targetX - x);
        int dy = Math.Sign(targetY - y);
        (int X, int Y)[] options = Math.Abs(targetX - x) >= Math.Abs(targetY - y)
            ? [(dx, 0), (0, dy), (-dy, 0), (0, -dx)]
            : [(0, dy), (dx, 0), (0, -dx), (-dy, 0)];

        foreach (bool respectBlocked in new[] { true, false })
        {
            foreach ((int stepX, int stepY) in options)
            {
                if (stepX == 0 && stepY == 0)
                    continue;
                int nextX = x + stepX;
                int nextY = y + stepY;
                if (map is not null && WallAtlas.Maps.TryGetValue(map, out GameMap? area)
                    && (nextX < 0 || nextX >= area.Width || nextY < 0 || nextY >= area.Height))
                    continue;
                if (blockMap is not null && WallAtlas.NearBlockedPort(blockMap, nextX, nextY, allowedWalls))
                    continue;
                if (respectBlocked && map is not null
                    && blockedTiles.TryGetValue((map, nextX, nextY), out var entry)
                    && entry.Until > now)
                    continue;
                return ToKey(stepX, stepY);
            }
        }
        return null;
    }

    private FieldKey? ChooseDetourDirection(string map, int x, int y, int targetX, int targetY, string? blockMap, int attempt = 0)
    {
        int dx = Math.Sign(targetX - x);
        int dy = Math.Sign(targetY - y);
        (int X, int Y)[] candidates = Math.Abs(targetX - x) >= Math.Abs(targetY - y)
            ? [(0, dy == 0 ? 1 : dy), (0, dy == 0 ? -1 : -dy), (-dx, 0)]
            : [(dx == 0 ? 1 : dx, 0), (dx == 0 ? -1 : -dx, 0), (0, -dy)];
        var valid = new List<FieldKey>(candidates.Length);
        foreach ((int stepX, int stepY) in candidates)
        {
            if (stepX == 0 && stepY == 0)
                continue;
            if (ToKey(stepX, stepY) is FieldKey key && CanStep(map, x, y, key, blockMap))
                valid.Add(key);
        }
        return valid.Count == 0 ? null : valid[Math.Abs(attempt) % valid.Count];
    }

    private bool CanStep(string map, int x, int y, FieldKey key, string? blockMap)
    {
        (int stepX, int stepY) = key switch
        {
            FieldKey.Up => (0, -1),
            FieldKey.Down => (0, 1),
            FieldKey.Left => (-1, 0),
            FieldKey.Right => (1, 0),
            _ => (0, 0)
        };
        if (stepX == 0 && stepY == 0)
            return false;
        int nextX = x + stepX;
        int nextY = y + stepY;
        if (WallAtlas.Maps.TryGetValue(map, out GameMap? area)
            && (nextX < 0 || nextX >= area.Width || nextY < 0 || nextY >= area.Height))
            return false;
        return blockMap is null || !WallAtlas.NearBlockedPort(blockMap, nextX, nextY, allowedWalls);
    }

    private static FieldKey? ToKey(int stepX, int stepY)
    {
        if (stepY < 0)
            return FieldKey.Up;
        if (stepY > 0)
            return FieldKey.Down;
        if (stepX < 0)
            return FieldKey.Left;
        if (stepX > 0)
            return FieldKey.Right;
        return null;
    }

    private static FieldKey ToDigitKey(char digit) => digit switch
    {
        '0' => FieldKey.Digit0,
        '1' => FieldKey.Digit1,
        '2' => FieldKey.Digit2,
        '3' => FieldKey.Digit3,
        '4' => FieldKey.Digit4,
        '5' => FieldKey.Digit5,
        '6' => FieldKey.Digit6,
        '7' => FieldKey.Digit7,
        '8' => FieldKey.Digit8,
        '9' => FieldKey.Digit9,
        _ => throw new ArgumentOutOfRangeException(nameof(digit), digit, "숫자만 입력할 수 있습니다.")
    };

    private FieldCommand Issue(TimeSpan now, FieldKey key, string reason)
    {
        supplyIssued++;
        supplyAt = now;
        return new FieldCommand(null, key, false, reason);
    }

    private static FieldCommand Wait(string reason) => new(null, null, false, reason);

    private enum SupplyPhase
    {
        Patrol,
        ToGuknae,
        BlinkWest,
        WalkSmith,
        Buy,
        ToJangan,
        BlinkNorth,
        WalkGate
    }
}

sealed class FieldInput : IDisposable
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventFExtendedKey = 0x0001;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint MouseEventFLeftDown = 0x0002;
    private const uint MouseEventFLeftUp = 0x0004;

    private readonly nint targetWindow;
    private readonly bool dryRun;
    private long lastHoldRefreshMs;

    public FieldInput(nint targetWindow = 0, bool dryRun = false)
    {
        this.targetWindow = targetWindow;
        this.dryRun = dryRun;
    }

    public FieldKey? ActiveHold { get; private set; }

    public void Apply(FieldCommand command)
    {
        if (!OperatingSystem.IsWindows() || dryRun)
        {
            ActiveHold = command.Hold;
            return;
        }

        if (targetWindow != 0 && GetForegroundWindow() != targetWindow)
        {
            ReleaseAll();
            return;
        }

        if (command.Click is ClickTarget click)
        {
            ReleaseAll();
            PerformClick(click);
        }

        long nowMs = Environment.TickCount64;
        if (ActiveHold != command.Hold)
        {
            FieldKey? previous = ActiveHold;
            if (previous is FieldKey prevKey)
                SendKey(prevKey, keyUp: true);
            ActiveHold = command.Hold;
            if (command.Hold is FieldKey next)
            {
                if (previous is not null)
                    Thread.Sleep(15);
                SendKey(next, keyUp: false);
                lastHoldRefreshMs = nowMs;
            }
        }
        else if (command.Hold is FieldKey held && nowMs - lastHoldRefreshMs >= 250)
        {
            SendKey(held, keyUp: false);
            lastHoldRefreshMs = nowMs;
        }

        if (command.Tap is FieldKey tap)
            Tap(tap);
        if (command.Dash)
        {
            Tap(FieldKey.Dash);
            if (ActiveHold is FieldKey stillHeld)
            {
                SendKey(stillHeld, keyUp: false);
                lastHoldRefreshMs = Environment.TickCount64;
            }
        }
    }

    public void ReleaseAll()
    {
        if (ActiveHold is not FieldKey held)
            return;

        if (OperatingSystem.IsWindows() && !dryRun)
            SendKey(held, keyUp: true);
        ActiveHold = null;
    }

    public void TypePuzzleAnswer(string answer)
    {
        if (!OperatingSystem.IsWindows() || dryRun || answer.Length == 0)
            return;
        if (targetWindow != 0 && GetForegroundWindow() != targetWindow)
            return;

        ReleaseAll();
        PerformClick(PuzzleFieldClick);
        Thread.Sleep(60);
        for (int i = 0; i < 8; i++)
            TapVirtualKey(0x08);
        EnsureHangulIme();
        TypeDubeolsik(HangulKeyboard.ToDubeolsik(answer));
        CompleteHangulComposition();
        Thread.Sleep(80);
        PerformClick(PuzzleOkClick);
    }

    public void Dispose() => ReleaseAll();

    private void PerformClick(ClickTarget click)
    {
        int x = click.X;
        int y = click.Y;
        if (targetWindow != 0 && GetClientRect(targetWindow, out NativeRect rect))
        {
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width > 0 && height > 0)
            {
                x = (int)Math.Round(click.X * (width / 1920.0));
                y = (int)Math.Round(click.Y * (height / 1080.0));
            }
        }
        var point = new NativePoint(x, y);
        if (targetWindow != 0)
            ClientToScreen(targetWindow, ref point);
        SetCursorPos(point.X, point.Y);
        Thread.Sleep(25);
        SendMouseClick();
        if (click.DoubleClick)
        {
            Thread.Sleep(60);
            SendMouseClick();
        }
    }

    private static readonly ClickTarget PuzzleFieldClick = new(860, 932);
    private static readonly ClickTarget PuzzleOkClick = new(644, 987);

    private static void Tap(FieldKey key)
    {
        SendKey(key, keyUp: false);
        Thread.Sleep(25);
        SendKey(key, keyUp: true);
    }

    private static void TapVirtualKey(ushort vk)
    {
        SendVirtualKey(vk, keyUp: false);
        Thread.Sleep(16);
        SendVirtualKey(vk, keyUp: true);
        Thread.Sleep(16);
    }

    private static void TypeDubeolsik(string keys)
    {
        bool shiftDown = false;
        try
        {
            foreach (char ch in keys)
            {
                bool shift = char.IsUpper(ch);
                ushort vk = (ushort)char.ToUpperInvariant(ch);
                if (vk is < (ushort)'A' or > (ushort)'Z')
                    continue;
                if (shift && !shiftDown)
                {
                    SendVirtualKey(0x10, keyUp: false);
                    shiftDown = true;
                    Thread.Sleep(12);
                }
                else if (!shift && shiftDown)
                {
                    SendVirtualKey(0x10, keyUp: true);
                    shiftDown = false;
                    Thread.Sleep(12);
                }

                SendVirtualKey(vk, keyUp: false);
                Thread.Sleep(18);
                SendVirtualKey(vk, keyUp: true);
                Thread.Sleep(22);
            }
        }
        finally
        {
            if (shiftDown)
                SendVirtualKey(0x10, keyUp: true);
        }
    }

    private void EnsureHangulIme()
    {
        if (targetWindow == 0)
            return;
        nint context = ImmGetContext(targetWindow);
        if (context == 0)
            return;
        try
        {
            if (!ImmGetConversionStatus(context, out int conversion, out int sentence))
                return;
            const int hangul = 0x1;
            if ((conversion & hangul) == 0)
                ImmSetConversionStatus(context, conversion | hangul, sentence);
        }
        finally
        {
            ImmReleaseContext(targetWindow, context);
        }
    }

    private void CompleteHangulComposition()
    {
        if (targetWindow == 0)
            return;
        nint context = ImmGetContext(targetWindow);
        if (context == 0)
            return;
        try
        {
            ImmNotifyIME(context, 0x0015, 0x0001, 0);
        }
        finally
        {
            ImmReleaseContext(targetWindow, context);
        }
    }

    private static void SendKey(FieldKey key, bool keyUp)
    {
        ushort vk = VirtualKey(key);
        if (vk == 0)
            return;

        uint flags = (keyUp ? KeyEventFKeyUp : 0u) | (IsExtendedKey(key) ? KeyEventFExtendedKey : 0u);
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = vk,
                    ScanCode = (ushort)MapVirtualKeyW(vk, 0),
                    Flags = flags
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void SendVirtualKey(ushort vk, bool keyUp)
    {
        uint flags = keyUp ? KeyEventFKeyUp : 0u;
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = vk,
                    ScanCode = (ushort)MapVirtualKeyW(vk, 0),
                    Flags = flags
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void SendMouseClick()
    {
        Input[] inputs =
        [
            new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = MouseEventFLeftDown } } },
            new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = MouseEventFLeftUp } } }
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static bool IsExtendedKey(FieldKey key) =>
        key is FieldKey.Up or FieldKey.Down or FieldKey.Left or FieldKey.Right;

    private static ushort VirtualKey(FieldKey key) => key switch
    {
        FieldKey.Up => 0x26,
        FieldKey.Down => 0x28,
        FieldKey.Left => 0x25,
        FieldKey.Right => 0x27,
        FieldKey.Secretary => 0x55,
        FieldKey.Guknae => 0x4D,
        FieldKey.Jangan => 0x43,
        FieldKey.Blink => 0x69,
        FieldKey.Dash => 0x32,
        FieldKey.Enter => 0x0D,
        FieldKey.Escape => 0x1B,
        FieldKey.Backspace => 0x08,
        FieldKey.Digit0 => 0x30,
        FieldKey.Digit1 => 0x31,
        FieldKey.Digit2 => 0x32,
        FieldKey.Digit3 => 0x33,
        FieldKey.Digit4 => 0x34,
        FieldKey.Digit5 => 0x35,
        FieldKey.Digit6 => 0x36,
        FieldKey.Digit7 => 0x37,
        FieldKey.Digit8 => 0x38,
        FieldKey.Digit9 => 0x39,
        _ => 0
    };

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputs, Input[] data, int size);

    [DllImport("imm32.dll")]
    private static extern nint ImmGetContext(nint hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(nint hWnd, nint context);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetConversionStatus(nint context, out int conversion, out int sentence);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetConversionStatus(nint context, int conversion, int sentence);

    [DllImport("imm32.dll")]
    private static extern bool ImmNotifyIME(nint context, uint action, uint index, uint value);

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
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }
}

static class FieldRehearsal
{
    public static List<string> Run()
    {
        var failures = new List<string>();
        CheckDialogStops(failures);
        CheckPuzzleStopsMovement(failures);
        CheckHangulKeystrokes(failures);
        CheckPuzzleAutoLimit(failures);
        CheckRepairDialogTapsEnter(failures);
        CheckBrickRunStartsWithSecretary(failures);
        CheckReturnConfirmClicksYesThenEnter(failures);
        CheckBlinkUsesNumberAndEnter(failures);
        CheckSmithUsesBridge(failures);
        CheckSmithyBuyAndExit(failures);
        CheckJanganGateAndWorldMap(failures);
        CheckHoldAwayNeverDeadlocks(failures);
        CheckSupportedWallsRestriction(failures);
        CheckStuckDetour(failures);
        CheckObstacleBfsRoutesAroundFlag(failures);
        CheckCornerTrapEscapesCompletely(failures);
        CheckWall3FlagCorridorTrapEscapes(failures);
        CheckWallVisionGuidesWithoutBumping(failures);
        CheckWallVisionDetectsLargeOverlayPopup(failures);
        CheckVisionNeverErasesPhysicalBlock(failures);
        CheckPortalRouteNeverErasesPhysicalWallWhenVisionBlocked(failures);
        CheckVisionSkipGuardOnNightTint(failures);
        CheckPatrolWaypointAvoidsPortalCorridor(failures);
        CheckWall2BottleneckEscapesToPortal(failures);
        CheckCenterParchmentModalDetection(failures);
        CheckVerificationPromptDetection(failures);
        CheckEdgeMarginPreventsWorldMapExit(failures);
        CheckAllFourWallsReachable(failures);
        CheckWall2NorthExitNeverEntered(failures);
        CheckPatrolSweepsDeepIntoDeadEndWallWithoutBacktracking(failures);
        CheckSweepVisitsAllFourWallsAndPrefersUnvisited(failures);
        CheckPatrolStaysOnTheWall(failures);
        CheckFieldInputHoldRelease(failures);
        CheckHotkeys(failures);
        CheckRepairCounterIgnoresAbsentWithoutLowStock(failures);
        CheckSmithDoorApproachDisablesDashAndAlignsPrecisely(failures);
        CheckSmithyBuyStepsDownAndClearsOnRetry(failures);
        CheckSmithyBuyClearsAfterMidTypingFlicker(failures);
        return failures;
    }

    private static void CheckDialogStops(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        FieldCommand command = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, false, true, TimeSpan.FromSeconds(3)));
        if (command.Hold is not null || command.Dash || command.Tap is not null || command.Click is not null)
            failures.Add("대화가 열린 동안 이동 키가 나갔습니다.");
    }

    private static void CheckPuzzleStopsMovement(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        FieldCommand command = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, false, false, TimeSpan.FromSeconds(3), PuzzleOpen: true));
        if (command.Hold is not null || command.Dash || command.Tap is not null || command.Click is not null)
            failures.Add("허풍선이 열린 동안 이동 키가 나갔습니다.");
    }

    private static void CheckHangulKeystrokes(List<string> failures)
    {
        if (HangulKeyboard.ToDubeolsik("사다마사주람") != "tkekaktkwnfka")
            failures.Add("사다마사주람의 두벌식 자판 변환이 맞지 않습니다.");
        if (HangulKeyboard.ToDubeolsik("파성령연지청") != "vktjdfudduswlcjd")
            failures.Add("파성령연지청의 두벌식 자판 변환이 맞지 않습니다.");
        if (HangulKeyboard.ToDubeolsik("까") != "Rk" || HangulKeyboard.ToDubeolsik("의") != "dml")
            failures.Add("겹받침과 쌍자음 두벌식 변환이 맞지 않습니다.");
    }

    private static void CheckPuzzleAutoLimit(List<string> failures)
    {
        var ledger = new PuzzleLedger();
        var time = TimeSpan.Zero;
        ledger.OnFrame(true, 1, time, null, null);
        if (!ledger.CanAutoType)
            failures.Add("첫 회차에 자동 입력이 막혀 있습니다.");
        ledger.NoteAutoTyped("가나다라마바", "테스트");
        if (ledger.CanAutoType)
            failures.Add("같은 회차에 두 번 입력합니다.");
        ledger.OnFrame(true, 2, time += TimeSpan.FromSeconds(1), "가나다라마바", "실패판독");
        if (!ledger.CanAutoType)
            failures.Add("두 번째 회차 자동 입력이 막혀 있습니다.");
        ledger.NoteAutoTyped("사아자차카타", "테스트");
        ledger.OnFrame(true, 3, time += TimeSpan.FromSeconds(1), "사아자차카타", "실패판독");
        if (ledger.CanAutoType)
            failures.Add("세 번째 기회까지 자동 입력합니다.");
        if (!ledger.HandedToUser)
            failures.Add("두 번 실패 후 사용자에게 넘기지 않았습니다.");
        ledger.OnFrame(false, 0, time += TimeSpan.FromSeconds(1), null, null);
        if (!ledger.TryFinish(false, time + TimeSpan.FromSeconds(1.2), out string handed, out _) || handed != "사용자마무리")
            failures.Add("두 번 실패 후 직접 닫으면 사용자 마무리여야 합니다: " + handed);

        var success = new PuzzleLedger();
        success.OnFrame(true, 1, TimeSpan.Zero, null, null);
        success.NoteAutoTyped("사다마사주람", "테스트");
        success.OnFrame(false, 0, TimeSpan.FromSeconds(2), null, null);
        if (!success.TryFinish(false, TimeSpan.FromSeconds(3.2), out string won, out _) || won != "2회내성공")
            failures.Add("한 번 입력 후 창이 닫히면 2회 내 성공이어야 합니다: " + won);

        var gaveUp = new PuzzleLedger();
        gaveUp.OnFrame(true, 4, TimeSpan.Zero, null, null);
        gaveUp.Handoff("6초 안에 여섯 글자를 확정하지 못함");
        if (gaveUp.CanAutoType)
            failures.Add("확정 실패 후에도 자동 입력을 계속합니다.");
        gaveUp.OnFrame(false, 0, TimeSpan.FromSeconds(2), null, null);
        if (!gaveUp.TryFinish(false, TimeSpan.FromSeconds(3.2), out string manual, out _) || manual != "사용자마무리")
            failures.Add("직접 마무리 기록이 아닙니다: " + manual);

        var lost = new PuzzleLedger();
        lost.OnFrame(true, 1, TimeSpan.Zero, null, null);
        lost.Handoff("6초 안에 여섯 글자를 확정하지 못함");
        lost.OnFrame(true, 2, TimeSpan.FromSeconds(2), "미확정", "표 없음");
        lost.OnFrame(false, 0, TimeSpan.FromSeconds(4), null, null);
        if (!lost.TryFinish(false, TimeSpan.FromSeconds(5.2), out string failed, out _) || failed != "실패")
            failures.Add("사용자 처리 중 실패가 실패로 기록되지 않습니다: " + failed);
    }

    private static void CheckRepairDialogTapsEnter(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        FieldCommand first = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, false, false, TimeSpan.Zero, RepairDialogOpen: true));
        FieldCommand second = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, false, false, TimeSpan.FromMilliseconds(150), RepairDialogOpen: true));
        FieldCommand third = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, false, false, TimeSpan.FromMilliseconds(400), RepairDialogOpen: true));
        if (first.Hold is not null || first.Dash || first.Tap != FieldKey.Enter)
            failures.Add("수리 대화창이 열렸을 때 이동을 멈추고 Enter를 눌러야 합니다.");
        if (second.Tap is not null)
            failures.Add("수리 대화창 Enter 쿨타임이 지켜지지 않았습니다.");
        if (third.Tap != FieldKey.Enter)
            failures.Add("수리 대화창이 계속 열려 있으면 Enter를 다시 눌러야 합니다.");
    }

    private static void CheckBrickRunStartsWithSecretary(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        FieldCommand open = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.Zero));
        FieldCommand select = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.FromMilliseconds(250)));
        if (open.Tap != FieldKey.Secretary || select.Tap != FieldKey.Guknae)
            failures.Add("벽돌이 떨어지면 국내성 비서 U 다음 M을 눌러야 합니다.");

        FieldCommand retry = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.FromSeconds(5)));
        if (retry.Tap != FieldKey.Secretary)
            failures.Add("국내성 비서 응답이 없을 때 재시도하지 않았습니다.");
    }

    private static void CheckReturnConfirmClicksYesThenEnter(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.Zero));
        pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.FromMilliseconds(250)));

        FieldCommand clickYes = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.FromMilliseconds(500), ReturnConfirmOpen: true));
        FieldCommand pressEnter = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.FromMilliseconds(750), ReturnConfirmOpen: true));
        if (clickYes.Click is null || clickYes.Hold is not null || clickYes.Tap is not null)
            failures.Add("귀환 확인창이 뜨면 먼저 예를 클릭해야 합니다.");
        if (pressEnter.Tap != FieldKey.Enter)
            failures.Add("귀환 확인창에서 예를 클릭한 뒤 Enter를 눌러야 합니다.");

        FieldCommand arrived = pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, TimeSpan.FromSeconds(1)));
        if (arrived.Tap != FieldKey.Blink)
            failures.Add("귀환 후 국내성 도착 시 비영사천문 단계로 이어져야 합니다.");
    }

    private static void CheckBlinkUsesNumberAndEnter(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        var view = new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.Zero);
        pilot.Decide(view);
        pilot.Decide(view with { Now = TimeSpan.FromMilliseconds(250) });

        FieldCommand blink = pilot.Decide(view with { Map = WallAtlas.Guknae, X = 204, Y = 108, Now = TimeSpan.FromMilliseconds(500) });
        FieldCommand west = pilot.Decide(view with { Map = WallAtlas.Guknae, X = 204, Y = 108, Now = TimeSpan.FromMilliseconds(750) });
        FieldCommand confirm = pilot.Decide(view with { Map = WallAtlas.Guknae, X = 204, Y = 108, Now = TimeSpan.FromMilliseconds(950) });
        if (blink.Tap != FieldKey.Blink || west.Tap != FieldKey.Digit2 || confirm.Tap != FieldKey.Enter)
            failures.Add("국내성 서문 이동은 비영사천문(NumPad9) → 2 → Enter여야 합니다.");

        FieldCommand retry = pilot.Decide(view with { Map = WallAtlas.Guknae, X = 204, Y = 108, Now = TimeSpan.FromSeconds(5.5) });
        if (retry.Tap != FieldKey.Blink)
            failures.Add("비영사천문 서문 착지가 안 됐을 때 재시도하지 않았습니다.");
    }

    private static void CheckSmithUsesBridge(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        var view = new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.Zero);
        pilot.Decide(view);
        pilot.Decide(view with { Now = TimeSpan.FromMilliseconds(250) });
        pilot.Decide(view with { Map = WallAtlas.Guknae, X = 204, Y = 108, Now = TimeSpan.FromMilliseconds(500) });
        pilot.Decide(view with { Map = WallAtlas.Guknae, X = 204, Y = 108, Now = TimeSpan.FromMilliseconds(750) });
        pilot.Decide(view with { Map = WallAtlas.Guknae, X = 204, Y = 108, Now = TimeSpan.FromMilliseconds(950) });

        FieldCommand firstWalk = pilot.Decide(view with { Map = WallAtlas.Guknae, X = 16, Y = 108, Now = TimeSpan.FromSeconds(1.2) });
        if (firstWalk.Hold != FieldKey.Right)
            failures.Add("국내성 서쪽 착지 뒤에는 다리를 건너 오른쪽으로 이동해야 합니다.");

        FieldCommand south = pilot.Decide(view with { Map = WallAtlas.Guknae, X = 40, Y = 108, Now = TimeSpan.FromSeconds(1.5) });
        if (south.Hold != FieldKey.Down)
            failures.Add("다리 건너 (40,108) 이후에는 아래쪽 (40,121)로 내려가야 합니다.");

        pilot.Decide(view with { Map = WallAtlas.Guknae, X = 40, Y = 121, Now = TimeSpan.FromSeconds(1.8) });
        pilot.Decide(view with { Map = WallAtlas.Guknae, X = 53, Y = 122, Now = TimeSpan.FromSeconds(2.1) });
        FieldCommand enter = pilot.Decide(view with { Map = WallAtlas.Guknae, X = 59, Y = 121, Now = TimeSpan.FromSeconds(2.4) });
        if (enter.Hold != FieldKey.Up)
            failures.Add("털보네대장간 입구 (59,121)에서 위로 들어가야 합니다.");
    }

    private static void CheckSmithyBuyAndExit(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        var t = TimeSpan.Zero;
        void Step(FieldView view) => pilot.Decide(view);
        FieldCommand Decide(FieldView view) => pilot.Decide(view);

        Step(new FieldView(WallAtlas.Wall3, 16, 46, true, false, t));
        Step(new FieldView(WallAtlas.Wall3, 16, 46, true, false, t += TimeSpan.FromMilliseconds(250)));
        Step(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(250)));
        Step(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(250)));
        Step(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(200)));
        Step(new FieldView(WallAtlas.Guknae, 16, 108, true, false, t += TimeSpan.FromMilliseconds(200)));
        Step(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(200)));

        FieldCommand clickNpc = Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(200)));
        FieldCommand clickBuy = Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(200), SmithyDialog: SmithyDialogState.MainMenu));
        FieldCommand clickBrick = Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(200), SmithyDialog: SmithyDialogState.ItemList));
        FieldCommand clickOk = Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(250), SmithyDialog: SmithyDialogState.ItemList));
        if (clickNpc.Click is null || clickBuy.Click is null || clickBrick.Click is null || clickOk.Click is null)
            failures.Add("털보네대장간 NPC 클릭 및 물건 사기 선택이 누락됐습니다.");

        FieldCommand d1 = Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(200), SmithyDialog: SmithyDialogState.QuantityPrompt));
        FieldCommand d2 = Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(150), SmithyDialog: SmithyDialogState.QuantityPrompt));
        FieldCommand d3 = Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(150), SmithyDialog: SmithyDialogState.QuantityPrompt));
        FieldCommand confirm = Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(150), SmithyDialog: SmithyDialogState.QuantityPrompt));
        if (d1.Tap != FieldKey.Digit1 || d2.Tap != FieldKey.Digit0 || d3.Tap != FieldKey.Digit0 || confirm.Tap != FieldKey.Enter)
            failures.Add("수량 입력창에서 100과 Enter를 눌러야 합니다.");

        FieldCommand esc = Decide(new FieldView(WallAtlas.Smith, 5, 9, false, false, t += TimeSpan.FromMilliseconds(200), SmithyDialog: SmithyDialogState.MainMenu, Stock: BrickStock.QuantityVisible));
        FieldCommand exit = Decide(new FieldView(WallAtlas.Smith, 5, 9, false, false, t += TimeSpan.FromMilliseconds(200), SmithyDialog: SmithyDialogState.None, Stock: BrickStock.QuantityVisible));
        if (esc.Tap != FieldKey.Escape || exit.Hold != FieldKey.Down)
            failures.Add("구매 완료 후 Escape로 창을 닫고 아래로 나와야 합니다.");
    }

    private static void CheckJanganGateAndWorldMap(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        var t = TimeSpan.Zero;
        pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, t));
        pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 16, 108, true, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 59, 122, false, false, t += TimeSpan.FromMilliseconds(200)));

        pilot.Decide(new FieldView(WallAtlas.Guknae, 59, 122, false, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 59, 122, false, false, t += TimeSpan.FromMilliseconds(250)));
        FieldCommand blink = pilot.Decide(new FieldView(WallAtlas.Jangan, 112, 108, false, false, t += TimeSpan.FromMilliseconds(250)));
        FieldCommand north = pilot.Decide(new FieldView(WallAtlas.Jangan, 112, 108, false, false, t += TimeSpan.FromMilliseconds(250)));
        FieldCommand enter = pilot.Decide(new FieldView(WallAtlas.Jangan, 112, 108, false, false, t += TimeSpan.FromMilliseconds(200)));
        if (blink.Tap != FieldKey.Blink || north.Tap != FieldKey.Digit4 || enter.Tap != FieldKey.Enter)
            failures.Add("장안성 북문 비영사천문은 NumPad9 → 4 → Enter여야 합니다.");

        FieldCommand toGate = pilot.Decide(new FieldView(WallAtlas.Jangan, 115, 7, false, false, t += TimeSpan.FromMilliseconds(250)));
        if (toGate.Hold != FieldKey.Up)
            failures.Add("장안성 북문에서 위쪽 성문으로 걸어가야 합니다.");

        FieldCommand mapUp = pilot.Decide(new FieldView(null, null, null, false, false, t += TimeSpan.FromMilliseconds(250), WorldMapOpen: true));
        FieldCommand mapEnter = pilot.Decide(new FieldView(null, null, null, false, false, t += TimeSpan.FromMilliseconds(900), WorldMapOpen: true));
        if (mapUp.Tap != FieldKey.Up || mapEnter.Tap != FieldKey.Enter)
            failures.Add("지역 지도에서 Up 후 Enter로 만리장성에 들어가야 합니다.");

        FieldCommand resumed = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, false, false, t += TimeSpan.FromMilliseconds(300)));
        if (resumed.Hold is null)
            failures.Add("만리장성 복귀 후 순찰이 재개되지 않았습니다.");
    }

    private static void CheckHoldAwayNeverDeadlocks(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        int x = 11;
        int y = 48;
        var now = TimeSpan.Zero;
        for (int step = 0; step < 10; step++)
        {
            FieldCommand command = pilot.Decide(new FieldView(WallAtlas.Wall3, x, y, false, false, now));
            if (command.Hold is not FieldKey key)
            {
                failures.Add($"출구 인접 좌표 ({x},{y})에서 HoldAway가 정지했습니다.");
                return;
            }
            (x, y) = Move(x, y, key);
            if (!WallAtlas.NearExit(WallAtlas.Wall3, x, y))
                return;
            now += TimeSpan.FromMilliseconds(200);
        }
        failures.Add("출구 완충 구역을 10걸음 안에 벗어나지 못했습니다.");
    }

    private static void CheckSupportedWallsRestriction(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1), [WallAtlas.Wall3]);
        int x = 16;
        int y = 46;
        var now = TimeSpan.Zero;
        for (int step = 0; step < 500; step++)
        {
            FieldCommand command = pilot.Decide(new FieldView(WallAtlas.Wall3, x, y, false, false, now));
            if (command.Hold is FieldKey direction)
            {
                (int nextX, int nextY) = Move(x, y, direction);
                if (WallAtlas.NearBlockedPort(WallAtlas.Wall3, nextX, nextY, new HashSet<string> { WallAtlas.Wall3 }))
                {
                    failures.Add($"미지원 성벽 성문 근처 ({nextX},{nextY})에 진입했습니다.");
                    return;
                }
                (x, y) = (nextX, nextY);
            }
            now += TimeSpan.FromMilliseconds(200);
        }
    }

    private static void CheckStuckDetour(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1), [WallAtlas.Wall3]);
        FieldCommand initial = pilot.Decide(new FieldView(WallAtlas.Wall3, 25, 25, false, false, TimeSpan.Zero));
        FieldCommand stuck = pilot.Decide(new FieldView(WallAtlas.Wall3, 25, 25, false, false, TimeSpan.FromSeconds(2)));
        if (initial.Hold is null || stuck.Hold is null || stuck.Hold == initial.Hold)
            failures.Add("같은 좌표에 멈춰 있을 때 우회 방향을 선택하지 않았습니다.");
    }

    private static void CheckObstacleBfsRoutesAroundFlag(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1), [WallAtlas.Wall3]);
        var obstacle = new HashSet<(int X, int Y)> { (26, 24), (26, 25), (26, 26), (25, 24), (25, 26) };
        int x = 25;
        int y = 25;
        var visited = new HashSet<(int X, int Y)> { (x, y) };
        var now = TimeSpan.Zero;
        for (int step = 0; step < 30; step++)
        {
            FieldCommand command = pilot.Decide(new FieldView(WallAtlas.Wall3, x, y, false, false, now));
            if (command.Hold is FieldKey dir)
            {
                (int nx, int ny) = Move(x, y, dir);
                if (!obstacle.Contains((nx, ny)) && nx >= 0 && nx < 60 && ny >= 0 && ny < 50)
                {
                    x = nx;
                    y = ny;
                    visited.Add((x, y));
                }
            }
            now += TimeSpan.FromMilliseconds(200);
        }

        if (visited.Count < 8)
            failures.Add($"깃발/NPC 장애물 앞에서 우회하지 못하고 {visited.Count}칸만 이동했습니다.");
    }

    private static void CheckCornerTrapEscapesCompletely(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1), [WallAtlas.Wall3]);
        var obstacle = new HashSet<(int X, int Y)> { (25, 24), (26, 25), (24, 25) };
        int x = 25;
        int y = 25;
        var now = TimeSpan.Zero;
        for (int step = 0; step < 30; step++)
        {
            FieldCommand command = pilot.Decide(new FieldView(WallAtlas.Wall3, x, y, false, false, now));
            if (command.Hold is FieldKey dir)
            {
                (int nx, int ny) = Move(x, y, dir);
                if (!obstacle.Contains((nx, ny)) && nx >= 0 && nx < 60 && ny >= 0 && ny < 50)
                {
                    x = nx;
                    y = ny;
                }
            }
            now += TimeSpan.FromMilliseconds(200);
        }

        if (y <= 25)
            failures.Add($"3면 막힘 코너(25,25)에서 아래쪽 출구로 탈출하지 못했습니다. 위치: ({x},{y})");
    }

    private static void CheckWall3FlagCorridorTrapEscapes(List<string> failures)
    {
        static bool IsBlockedLayout(int tx, int ty)
        {
            if (tx <= 42 && ty <= 10)
                return true; // 왼쪽 잔디 벽
            if ((tx == 43 && ty == 2) || (tx == 48 && ty == 2))
                return true; // 위쪽 파란 깃발
            if ((tx == 43 && ty == 7) || (tx == 48 && ty == 7))
                return true; // 아래쪽 빨간 깃발
            if (tx >= 47 && ty >= 9)
                return true; // 우하단 성벽 모서리
            return false;
        }

        // 1) 만리장성3 내부 순찰 중 (43,4) 깃발 통로 탈출
        var patrolPilot = new FieldPilot(new Random(1), [WallAtlas.Wall3]);
        int x = 43;
        int y = 4;
        var visited = new HashSet<(int X, int Y)> { (x, y) };
        var now = TimeSpan.Zero;
        for (int step = 0; step < 60; step++)
        {
            FieldCommand cmd = patrolPilot.Decide(new FieldView(WallAtlas.Wall3, x, y, false, false, now));
            if (cmd.Hold is FieldKey dir)
            {
                (int nx, int ny) = Move(x, y, dir);
                if (!IsBlockedLayout(nx, ny) && nx >= 0 && nx < 60 && ny >= 0 && ny < 50)
                {
                    x = nx;
                    y = ny;
                    visited.Add((x, y));
                }
            }
            now += TimeSpan.FromMilliseconds(200);
        }

        if (!visited.Any(p => p.X >= 45 || p.Y >= 10) || visited.Count < 15)
            failures.Add($"만리장성3 (43,4) 깃발 통로에서 탈출하지 못했습니다. 마지막({x},{y}), 방문 {visited.Count}칸");

        // 2) 만리장성4(7,0)로 이동하려는 상황에서도 깃발 아래로 돌아서 탈출
        var hopPilot = new FieldPilot(new Random(1));
        x = 43;
        y = 4;
        var hopVisited = new HashSet<(int X, int Y)> { (x, y) };
        now = TimeSpan.Zero;
        for (int step = 0; step < 120; step++)
        {
            FieldCommand cmd = hopPilot.Decide(new FieldView(WallAtlas.Wall3, x, y, false, false, now));
            if (cmd.Hold is FieldKey dir)
            {
                (int nx, int ny) = Move(x, y, dir);
                if (!IsBlockedLayout(nx, ny) && nx >= 0 && nx < 60 && ny >= 0 && ny < 50)
                {
                    x = nx;
                    y = ny;
                    hopVisited.Add((x, y));
                }
            }
            now += TimeSpan.FromMilliseconds(200);
        }

        if (!hopVisited.Any(p => p.X >= 45 || p.Y >= 10))
            failures.Add($"깃발 통로(43,4) 우회 경로를 찾지 못했습니다. 마지막({x},{y})");
    }

    private static void CheckWallVisionGuidesWithoutBumping(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1), [WallAtlas.Wall3]);
        var scans = new List<TileScan>();
        for (int ty = 1; ty <= 10; ty++)
        {
            for (int tx = 38; tx <= 50; tx++)
            {
                if (tx == 43 && ty == 4)
                    continue;
                bool blocked = (tx <= 41 && ty <= 10)
                    || ((tx == 43 || tx == 48) && (ty == 2 || ty == 3 || ty == 7 || ty == 8));
                scans.Add(new TileScan(tx, ty, Walkable: !blocked, IsStaticTerrain: tx <= 41));
            }
        }
        pilot.ObserveTerrain(WallAtlas.Wall3, scans, TimeSpan.Zero);

        FieldCommand first = pilot.Decide(new FieldView(WallAtlas.Wall3, 43, 4, false, false, TimeSpan.Zero));
        if (first.Hold is not FieldKey dir)
        {
            failures.Add("화면 지형 인식 직후 이동 방향을 고르지 못했습니다.");
            return;
        }
        (int nx, int ny) = Move(43, 4, dir);
        if ((nx <= 41) || (nx == 43 && ny == 3))
            failures.Add($"화면 지형 인식을 줬는데도 막힌 칸 ({nx},{ny}) 방향으로 진입했습니다.");
    }

    private static void CheckVisionNeverErasesPhysicalBlock(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1), [WallAtlas.Wall3]);
        FieldCommand first = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 10, false, false, TimeSpan.Zero));
        if (first.Hold is not FieldKey dir1)
        {
            failures.Add("충돌 유지 테스트에서 초기 이동 방향을 고르지 못했습니다.");
            return;
        }
        (int bx, int by) = Move(16, 10, dir1);

        // 600ms 동안 제자리에 머물러 (bx, by)를 물리적 막힘(Hits=1)으로 학습
        pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 10, false, false, TimeSpan.FromMilliseconds(600)));

        // 화면 인식이 해당 칸을 통행 가능으로 보고해도 물리적 막힘은 지워지면 안 된다.
        pilot.ObserveTerrain(
            WallAtlas.Wall3,
            [new TileScan(bx, by, Walkable: true, IsStaticTerrain: true)],
            TimeSpan.FromMilliseconds(700));

        FieldCommand after = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 10, false, false, TimeSpan.FromMilliseconds(700)));
        if (after.Hold is not FieldKey nextDir || nextDir == dir1)
            failures.Add($"화면 인식이 물리적 막힘 칸 ({bx},{by})를 지워 같은 막힌 방향({dir1})으로 재돌진했습니다. 선택: {after.Hold}");
    }

    private static void CheckPortalRouteNeverErasesPhysicalWallWhenVisionBlocked(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1), [WallAtlas.Wall2, WallAtlas.Wall3]);
        int x = 13;
        int y = 3;
        var now = TimeSpan.Zero;

        // 0틱: 만리장성3 진입, 66초 후 MaxWallSweepTime 만료로 만리장성2 성문(46,0) 목표로 전환
        pilot.Decide(new FieldView(WallAtlas.Wall3, x, y, false, false, now));
        now = TimeSpan.FromSeconds(66);

        var heavyVisualBlocks = new List<TileScan>();
        for (int ty = 0; ty < 22; ty++)
        {
            for (int tx = 5; tx < 25; tx++)
            {
                if (tx == x && ty == y)
                    continue;
                heavyVisualBlocks.Add(new TileScan(tx, ty, Walkable: false, IsStaticTerrain: true));
            }
        }

        for (int step = 0; step < 40; step++)
        {
            pilot.ObserveTerrain(WallAtlas.Wall3, heavyVisualBlocks, now);
            FieldCommand cmd = pilot.Decide(new FieldView(WallAtlas.Wall3, x, y, false, false, now));
            if (cmd.Hold is FieldKey dir)
            {
                (int nx, int ny) = Move(x, y, dir);
                bool physicalWall = nx == 14 && ny <= 8;
                if (!physicalWall && nx >= 0 && nx < 60 && ny >= 0 && ny < 50)
                {
                    x = nx;
                    y = ny;
                }
            }
            now += TimeSpan.FromMilliseconds(200);
        }

        if (x < 16)
            failures.Add($"성문 이동 중 시각 오인(200+칸) 때문에 (13,3) 물리 벽 학습이 지워져 우회하지 못했습니다. 위치: ({x},{y})");
    }

    private static void CheckVisionSkipGuardOnNightTint(List<string> failures)
    {
        var vision = new WallVision();
        using var nightFrame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(118, 85, 70));
        List<TileScan> scans = vision.Scan(nightFrame, WallAtlas.Wall3, 14, 25);
        if (scans.Count != 0 || string.IsNullOrEmpty(vision.LastSkipReason))
            failures.Add($"야간/청색 색감 화면에서 WallVision 안전 보류가 동작하지 않았습니다. count={scans.Count}, reason={vision.LastSkipReason}");
    }

    private static void CheckPatrolWaypointAvoidsPortalCorridor(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        FieldCommand cmd = pilot.Decide(new FieldView(WallAtlas.Wall4, 85, 27, false, false, TimeSpan.Zero));
        if (cmd.Hold == FieldKey.Down)
            failures.Add($"만리장성4 (85,27) 착지 직후 성문 복도 쪽(Down)을 목표로 잡아 재이탈했습니다. 목표: {pilot.CurrentTargetSummary}");
    }

    private static void CheckWall2BottleneckEscapesToPortal(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1), [WallAtlas.Wall1, WallAtlas.Wall2]);
        int x = 24;
        int y = 63;
        var now = TimeSpan.Zero;
        pilot.Decide(new FieldView(WallAtlas.Wall2, x, y, false, false, now));
        now = TimeSpan.FromSeconds(66);

        int minYReached = y;
        for (int step = 0; step < 160; step++)
        {
            FieldCommand cmd = pilot.Decide(new FieldView(WallAtlas.Wall2, x, y, false, false, now));
            if (cmd.Hold is FieldKey dir)
            {
                (int nx, int ny) = Move(x, y, dir);
                bool wall = (ny == 60 && nx >= 17) || (nx >= 26 && ny >= 55 && ny <= 66);
                if (!wall && nx >= 0 && nx < 30 && ny >= 0 && ny < 100)
                {
                    x = nx;
                    y = ny;
                    if (y < minYReached)
                        minYReached = y;
                }
            }
            now += TimeSpan.FromMilliseconds(200);
        }

        if (minYReached > 55)
            failures.Add($"만리장성2 (24,63) 병목 구간에서 왼쪽으로 돌아 북상하지 못했습니다. 최소 Y={minYReached}, 마지막=({x},{y})");
    }

    private static void CheckCenterParchmentModalDetection(List<string> failures)
    {
        using var hud = new HudReader();
        using var stoneFrame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(135, 140, 145));
        if (hud.HasCenterParchmentDialog(stoneFrame))
            failures.Add("평범한 돌바닥 화면에서 미확인 대화창이 오검출되었습니다.");

        // Sandstone watchtower fills the center area with warm parchment brick colors but has no dark modal header/list.
        using var towerFrame = stoneFrame.Clone();
        Cv2.Rectangle(towerFrame, new Rect(640, 280, 500, 420), new Scalar(80, 145, 190), -1);
        if (hud.HasCenterParchmentDialog(towerFrame))
            failures.Add("어두운 제목줄이 없는 사암 망루 배경에서 미확인 대화창이 오검출되었습니다.");

        using var modalFrame = towerFrame.Clone();
        Cv2.Rectangle(modalFrame, new Rect(660, 280, 480, 38), new Scalar(25, 35, 45), -1);
        if (!hud.HasCenterParchmentDialog(modalFrame))
            failures.Add("중앙 팝업 대화창이 떴는데 감지하지 못했습니다.");
    }

    private static void CheckEdgeMarginPreventsWorldMapExit(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1), [WallAtlas.Wall3]);
        FieldCommand escape = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 48, false, false, TimeSpan.Zero));
        if (escape.Hold != FieldKey.Up)
            failures.Add($"만리장성3 남쪽 출구 위(16,48)에서 안쪽(Up)으로 피하지 않았습니다. 선택: {escape.Hold}");

        FieldCommand recover = pilot.Decide(new FieldView(null, null, null, false, false, TimeSpan.FromSeconds(2), WorldMapOpen: true));
        if (recover.Tap != FieldKey.Enter)
            failures.Add("순찰 중 지역 지도로 빠졌을 때 Enter 재입장 복구가 실행되지 않았습니다.");
    }

    private static void CheckAllFourWallsReachable(List<string> failures)
    {
        foreach ((string from, string to) in new[]
        {
            (WallAtlas.Wall3, WallAtlas.Wall4),
            (WallAtlas.Wall4, WallAtlas.Wall3),
            (WallAtlas.Wall3, WallAtlas.Wall2),
            (WallAtlas.Wall2, WallAtlas.Wall3),
            (WallAtlas.Wall2, WallAtlas.Wall1),
            (WallAtlas.Wall1, WallAtlas.Wall2)
        })
        {
            MapPort hop = WallAtlas.Hop(from, to);
            FieldKey? outward = WallAtlas.OutwardDirection(from, hop);
            (string landedMap, int lx, int ly) = WallAtlas.Land(from, hop.X, hop.Y);
            if (outward is null || landedMap != to || WallAtlas.NearExit(landedMap, lx, ly))
                failures.Add($"성문 연결 {from} -> {to} 좌표 검증 실패");
        }
    }

    private static void CheckWall2NorthExitNeverEntered(List<string> failures)
    {
        if (!WallAtlas.IsForbiddenPatrolTile(WallAtlas.Wall2, 10, 5, null)
            || !WallAtlas.IsForbiddenPatrolTile(WallAtlas.Wall2, 7, 4, null))
        {
            failures.Add("만리장성2 북쪽 세계지도 출구(X=7..10, Y=0) 보호 구역이 충분히 넓지 않습니다.");
        }

        var pilot = new FieldPilot(new Random(1));
        int x = 10;
        int y = 8;
        int minY = y;
        var now = TimeSpan.Zero;
        for (int step = 0; step < 30; step++)
        {
            FieldCommand cmd = pilot.Decide(new FieldView(WallAtlas.Wall2, x, y, false, false, now));
            if (cmd.Hold is FieldKey dir)
            {
                (int nx, int ny) = Move(x, y, dir);
                GameMap area = WallAtlas.Maps[WallAtlas.Wall2];
                if (nx >= 0 && nx < area.Width && ny >= 0 && ny < area.Height)
                {
                    x = nx;
                    y = ny;
                    if (y < minY)
                        minY = y;
                }
            }
            now += TimeSpan.FromMilliseconds(200);
        }

        if (minY < 5 || y <= 8)
            failures.Add($"만리장성2 북쪽 출구 부근(10,8)에서 안전하게 안쪽으로 빠지지 못했습니다. minY={minY}, 마지막=({x},{y})");
    }

    private static void CheckPatrolSweepsDeepIntoDeadEndWallWithoutBacktracking(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        string map = WallAtlas.Wall1;
        int x = 1;
        int y = 7;
        int maxX = x;
        var distinctTiles = new HashSet<(int X, int Y)>();
        var now = TimeSpan.Zero;

        for (int step = 0; step < 300; step++)
        {
            if (WallAtlas.WallPort(map, x, y) is not null)
                break;
            distinctTiles.Add((x, y));
            if (x > maxX)
                maxX = x;

            FieldCommand cmd = pilot.Decide(new FieldView(map, x, y, false, false, now));
            if (cmd.Hold is FieldKey dir)
            {
                (int nx, int ny) = Move(x, y, dir);
                GameMap area = WallAtlas.Maps[map];
                if (nx >= 0 && nx < area.Width && ny >= 0 && ny < area.Height)
                {
                    if (WallAtlas.WallPort(map, nx, ny) is not null)
                        break;
                    x = nx;
                    y = ny;
                }
            }
            now += TimeSpan.FromMilliseconds(200);
        }

        if (maxX < 65 || distinctTiles.Count < 180)
            failures.Add($"만리장성1 깊숙한 미방문 구역으로 전진하지 못했습니다. maxX={maxX}, 방문={distinctTiles.Count}칸");
    }

    private static void CheckSweepVisitsAllFourWallsAndPrefersUnvisited(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        string map = WallAtlas.Wall3;
        int x = 16;
        int y = 46;
        var seenWalls = new HashSet<string>(StringComparer.Ordinal);
        var distinctTiles = new HashSet<(string Map, int X, int Y)>();
        var now = TimeSpan.Zero;
        for (int step = 0; step < 1500; step++)
        {
            if (WallAtlas.WallPort(map, x, y) is not null)
                (map, x, y) = WallAtlas.Land(map, x, y);
            seenWalls.Add(map);
            distinctTiles.Add((map, x, y));

            FieldCommand command = pilot.Decide(new FieldView(map, x, y, false, false, now));
            if (command.Hold is FieldKey direction)
            {
                (int nextX, int nextY) = Move(x, y, direction);
                if (WallAtlas.NearExit(map, nextX, nextY))
                {
                    failures.Add($"순회 중 출구 완충 구역({map} {nextX},{nextY})에 진입했습니다.");
                    return;
                }
                if (WallAtlas.WallPort(map, nextX, nextY) is not null)
                    (map, x, y) = WallAtlas.Land(map, nextX, nextY);
                else
                    (x, y) = (nextX, nextY);
            }
            now += TimeSpan.FromMilliseconds(200);
        }

        if (seenWalls.Count < 4)
            failures.Add($"1~4 전체 성벽을 순회하지 못했습니다. 방문: {string.Join(", ", seenWalls)}");
        if (distinctTiles.Count < 250)
            failures.Add($"미방문 구역 탐색 범위가 너무 좁습니다. 방문 타일 수: {distinctTiles.Count}");
    }

    private static void CheckPatrolStaysOnTheWall(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        string map = WallAtlas.Wall3;
        int x = 16;
        int y = 46;
        var seen = new HashSet<string>();
        int exitSteps = 0;
        int dashes = 0;
        TimeSpan previousDash = TimeSpan.MinValue;
        var now = TimeSpan.Zero;
        for (int step = 0; step < 500; step++)
        {
            if (WallAtlas.WallPort(map, x, y) is not null)
                (map, x, y) = WallAtlas.Land(map, x, y);
            seen.Add(map);
            FieldCommand command = pilot.Decide(new FieldView(map, x, y, false, false, now));
            if (command.Dash)
            {
                if (previousDash != TimeSpan.MinValue && now - previousDash < TimeSpan.FromSeconds(1))
                    failures.Add("풍신보 간격이 1초보다 짧습니다.");
                previousDash = now;
                dashes++;
            }
            if (command.Hold is FieldKey direction)
            {
                (int nextX, int nextY) = Move(x, y, direction);
                if (WallAtlas.NearExit(map, nextX, nextY))
                    exitSteps++;
                if (WallAtlas.WallPort(map, nextX, nextY) is not null)
                    (map, x, y) = WallAtlas.Land(map, nextX, nextY);
                else
                    (x, y) = (nextX, nextY);
            }
            now += TimeSpan.FromMilliseconds(200);
        }

        if (exitSteps > 0)
            failures.Add($"순찰이 다른 성으로 나가는 성문에 {exitSteps}번 들어갔습니다.");
        if (seen.Count < 2)
            failures.Add("순찰이 만리장성 맵을 넘나들지 않았습니다. " + string.Join(", ", seen));
        if (dashes < 10)
            failures.Add("이동 중 풍신보가 거의 눌리지 않았습니다.");
        if (seen.Any(name => !WallAtlas.IsWall(name)))
            failures.Add("순찰 중 만리장성이 아닌 맵으로 나갔습니다.");
    }

    private static void CheckFieldInputHoldRelease(List<string> failures)
    {
        using var input = new FieldInput(dryRun: true);
        input.Apply(new FieldCommand(FieldKey.Right, null, false, "오른쪽 이동"));
        if (input.ActiveHold != FieldKey.Right)
            failures.Add("FieldInput이 Hold 상태를 기록하지 않았습니다.");
        input.Apply(new FieldCommand(null, FieldKey.Enter, false, "대화 정지"));
        if (input.ActiveHold is not null)
            failures.Add("정지 명령 시 기존 Hold 키가 해제되지 않았습니다.");
    }

    private static void CheckHotkeys(List<string> failures)
    {
        var hotkeys = new HotkeyState();
        if (hotkeys.Running || hotkeys.ExitRequested)
            failures.Add("초기 핫키 상태는 대기(정지) 상태여야 합니다.");
        if (hotkeys.Update(true, false) != HotkeyEvent.Started || !hotkeys.Running)
            failures.Add("F11 키를 누르면 시작 상태로 전환되어야 합니다.");
        if (hotkeys.Update(true, false) != HotkeyEvent.None || !hotkeys.Running)
            failures.Add("F11 키를 누르고 있는 동안에는 중복 토글되지 않아야 합니다.");
        if (hotkeys.Update(false, false) != HotkeyEvent.None)
            failures.Add("키를 떼었을 때는 이벤트가 발생하지 않아야 합니다.");
        if (hotkeys.Update(true, false) != HotkeyEvent.Paused || hotkeys.Running)
            failures.Add("실행 중 F11 키를 다시 누르면 일시정지되어야 합니다.");
        if (hotkeys.TriggerF11() != HotkeyEvent.Started || !hotkeys.Running)
            failures.Add("HotkeyWatcher TriggerF11로 시작 상태로 전환되지 않았습니다.");
        if (hotkeys.TriggerF11() != HotkeyEvent.Paused || hotkeys.Running)
            failures.Add("HotkeyWatcher TriggerF11로 일시정지 상태로 전환되지 않았습니다.");
        if (hotkeys.Update(false, true) != HotkeyEvent.Exit || !hotkeys.ExitRequested || hotkeys.Running)
            failures.Add("END 키를 누르면 즉시 종료 상태로 전환되어야 합니다.");
        if (hotkeys.TriggerExit() != HotkeyEvent.Exit || !hotkeys.ExitRequested || hotkeys.Running)
            failures.Add("HotkeyWatcher TriggerExit가 종료 이벤트를 반환하지 않았습니다.");
    }

    private static void CheckVerificationPromptDetection(List<string> failures)
    {
        string promptPath = Path.Combine(BundledFiles.TemplateDirectory, "character-prompt.png");
        if (!File.Exists(promptPath))
        {
            failures.Add("character-prompt.png 템플릿을 찾을 수 없습니다.");
            return;
        }

        using var screen = new RepairScreen();
        using var blank = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(120, 125, 130));
        if (screen.IsVerificationPromptOpen(blank))
            failures.Add("허풍선 안내 문구가 없는 빈 화면에서 오검출되었습니다.");

        using var prompt = Cv2.ImRead(promptPath, ImreadModes.Color);
        if (prompt.Empty())
        {
            failures.Add("character-prompt.png를 불러오지 못했습니다.");
            return;
        }

        foreach ((int px, int py) in new[] { (640, 860), (220, 180) })
        {
            using var canvas = blank.Clone();
            using var dst = new Mat(canvas, new Rect(px, py, prompt.Width, prompt.Height));
            prompt.CopyTo(dst);
            if (!screen.IsVerificationPromptOpen(canvas))
                failures.Add($"({px},{py}) 위치의 허풍선 안내 문구를 감지하지 못했습니다.");
        }
    }

    private static void CheckRepairCounterIgnoresAbsentWithoutLowStock(List<string> failures)
    {
        var neverSeen = new RepairCounter(100);
        neverSeen.ObserveStock(BrickStock.Absent, TimeSpan.Zero);
        neverSeen.ObserveStock(BrickStock.Absent, TimeSpan.FromSeconds(5));
        if (neverSeen.NeedsBricks)
            failures.Add("재고가 한 번도 낮음(1~2개)으로 관측되지 않았는데 Absent만으로 보급을 시작했습니다.");

        var scrolledAway = new RepairCounter(100);
        scrolledAway.ObserveStock(BrickStock.QuantityVisible, TimeSpan.Zero);
        scrolledAway.ObserveStock(BrickStock.Absent, TimeSpan.FromSeconds(1));
        scrolledAway.ObserveStock(BrickStock.Absent, TimeSpan.FromSeconds(5));
        if (scrolledAway.NeedsBricks)
            failures.Add("벽돌 3개 이상(QuantityVisible) 상태에서 일시적 Absent로 보급을 시작했습니다.");

        var trulyDepleted = new RepairCounter(100);
        trulyDepleted.ObserveStock(BrickStock.One, TimeSpan.Zero);
        trulyDepleted.ObserveStock(BrickStock.Absent, TimeSpan.FromSeconds(1));
        trulyDepleted.ObserveStock(BrickStock.Absent, TimeSpan.FromSeconds(5));
        if (!trulyDepleted.NeedsBricks)
            failures.Add("벽돌 1개 관측 뒤 Absent가 3초 지속됐는데도 보급을 시작하지 않았습니다.");
    }

    private static void CheckSmithDoorApproachDisablesDashAndAlignsPrecisely(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        var t = TimeSpan.Zero;
        pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, t));
        pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 16, 108, true, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 40, 108, true, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 40, 121, true, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 53, 122, true, false, t += TimeSpan.FromMilliseconds(200)));

        FieldCommand wallStep = pilot.Decide(new FieldView(WallAtlas.Guknae, 55, 121, true, false, t += TimeSpan.FromSeconds(2)));
        if (wallStep.Hold != FieldKey.Down || wallStep.Dash)
            failures.Add($"대장간 건물 벽(55,121)에서 옆으로 비비지 말고 아래쪽 도로로 내려와야 합니다. actual={wallStep.Hold}/{wallStep.Tap}/Dash={wallStep.Dash}");

        FieldCommand alignStep = pilot.Decide(new FieldView(WallAtlas.Guknae, 57, 123, true, false, t += TimeSpan.FromSeconds(2)));
        if (alignStep.Tap != FieldKey.Right || alignStep.Hold is not null || alignStep.Dash)
            failures.Add($"대장간 입구 아래(57,123)에서 풍신보 없이 1칸씩 정렬해야 합니다. actual={alignStep.Hold}/{alignStep.Tap}/Dash={alignStep.Dash}");

        FieldCommand enterFromBelow = pilot.Decide(new FieldView(WallAtlas.Guknae, 59, 123, true, false, t += TimeSpan.FromMilliseconds(250)));
        if (enterFromBelow.Hold != FieldKey.Up || !enterFromBelow.Dash)
            failures.Add($"대장간 입구 아래(59,123)에서 위+풍신보로 들어가야 합니다. actual={enterFromBelow.Hold}/Dash={enterFromBelow.Dash}");

        FieldCommand enterAtDoor = pilot.Decide(new FieldView(WallAtlas.Guknae, 59, 121, true, false, t += TimeSpan.FromSeconds(1.1)));
        if (enterAtDoor.Hold != FieldKey.Up || !enterAtDoor.Dash)
            failures.Add($"대장간 문(59,121)에서 위+풍신보로 들어가야 합니다. actual={enterAtDoor.Hold}/Dash={enterAtDoor.Dash}");
    }

    private static void CheckSmithyBuyStepsDownAndClearsOnRetry(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        var t = TimeSpan.Zero;
        pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, t));
        pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 16, 108, true, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(200)));

        // Attempt 1: 100
        pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(200), SmithyDialog: SmithyDialogState.QuantityPrompt));
        pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(150), SmithyDialog: SmithyDialogState.QuantityPrompt));
        pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(150), SmithyDialog: SmithyDialogState.QuantityPrompt));
        FieldCommand enter1 = pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(150), SmithyDialog: SmithyDialogState.QuantityPrompt));
        if (enter1.Tap != FieldKey.Enter)
            failures.Add("1차 시도에서 Enter 확정이 나가지 않았습니다.");

        // Rejected after 2s: must transition to 80 and clear with Backspace before typing digits.
        pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromSeconds(2.1), SmithyDialog: SmithyDialogState.QuantityPrompt));
        var taps = new List<FieldKey>();
        for (int i = 0; i < 10; i++)
        {
            FieldCommand c = pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(150), SmithyDialog: SmithyDialogState.QuantityPrompt));
            if (c.Tap is FieldKey k)
                taps.Add(k);
        }
        int backspaces = taps.Count(k => k == FieldKey.Backspace);
        var typedDigits = taps.Where(k => k != FieldKey.Backspace).ToArray();
        if (backspaces < 6 || typedDigits.Length < 3 || typedDigits[0] != FieldKey.Digit8 || typedDigits[1] != FieldKey.Digit0 || typedDigits[2] != FieldKey.Enter)
            failures.Add($"재시도 시 Backspace 초기화 후 80+Enter를 입력해야 합니다. backspaces={backspaces}, taps={string.Join(",", taps)}");
    }

    private static void CheckSmithyBuyClearsAfterMidTypingFlicker(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        var t = TimeSpan.Zero;
        pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, t));
        pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(250)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 204, 108, true, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Guknae, 16, 108, true, false, t += TimeSpan.FromMilliseconds(200)));
        pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(200)));

        // Type first digit '1', then drop QuantityPrompt to None briefly (flicker), then return to QuantityPrompt.
        FieldCommand d1 = pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(200), SmithyDialog: SmithyDialogState.QuantityPrompt));
        if (d1.Tap != FieldKey.Digit1)
            failures.Add("첫 숫자 1 입력이 나가지 않았습니다.");
        pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(150), SmithyDialog: SmithyDialogState.None));

        var afterFlicker = new List<FieldKey>();
        for (int i = 0; i < 12; i++)
        {
            FieldCommand c = pilot.Decide(new FieldView(WallAtlas.Smith, 5, 9, true, false, t += TimeSpan.FromMilliseconds(150), SmithyDialog: SmithyDialogState.QuantityPrompt));
            if (c.Tap is FieldKey k)
                afterFlicker.Add(k);
        }
        int backspaces = afterFlicker.Count(k => k == FieldKey.Backspace);
        var digits = afterFlicker.Where(k => k != FieldKey.Backspace).ToArray();
        if (backspaces < 6 || digits.Length < 4 || digits[0] != FieldKey.Digit1 || digits[1] != FieldKey.Digit0 || digits[2] != FieldKey.Digit0 || digits[3] != FieldKey.Enter)
            failures.Add($"숫자 입력 중 깜박임 발생 시 Backspace 초기화 후 100+Enter를 입력해야 합니다. backspaces={backspaces}, taps={string.Join(",", afterFlicker)}");
    }

    private static void CheckWallVisionDetectsLargeOverlayPopup(List<string> failures)
    {
        var vision = new WallVision();
        using var cleanFrame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(150, 150, 150));
        List<TileScan> cleanScans = vision.Scan(cleanFrame, WallAtlas.Wall4, 50, 15);
        if (vision.HasLargeOverlay || cleanScans.Count < 280)
            failures.Add($"정상 성벽 화면에서 팝업 오탐 또는 스캔 부족: overlay={vision.HasLargeOverlay}, scans={cleanScans.Count}");

        using (var brightPopup = cleanFrame.Clone())
        {
            Cv2.Rectangle(brightPopup, new Rect(470, 230, 520, 380), new Scalar(235, 240, 245), -1);
            List<TileScan> scans = vision.Scan(brightPopup, WallAtlas.Wall4, 50, 15);
            if (!vision.HasLargeOverlay || scans.Count != 0)
                failures.Add($"중앙 밝은 미니게임 팝업을 감지하지 못했습니다. overlay={vision.HasLargeOverlay}, reason={vision.LastOverlayReason}, scans={scans.Count}");
        }

        using (var darkPopup = cleanFrame.Clone())
        {
            Cv2.Rectangle(darkPopup, new Rect(470, 230, 520, 380), new Scalar(18, 18, 22), -1);
            List<TileScan> scans = vision.Scan(darkPopup, WallAtlas.Wall4, 50, 15);
            if (!vision.HasLargeOverlay || scans.Count != 0)
                failures.Add($"중앙 어두운 모달 팝업을 감지하지 못했습니다. overlay={vision.HasLargeOverlay}, reason={vision.LastOverlayReason}, scans={scans.Count}");
        }

        using (var blackout = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(5, 5, 5)))
        {
            _ = vision.Scan(blackout, WallAtlas.Wall4, 50, 15);
            if (vision.HasLargeOverlay)
                failures.Add("맵 이동 암전 화면을 팝업으로 오탐했습니다.");
        }
    }

    private static (int X, int Y) Move(int x, int y, FieldKey direction) => direction switch
    {
        FieldKey.Up => (x, y - 1),
        FieldKey.Down => (x, y + 1),
        FieldKey.Left => (x - 1, y),
        FieldKey.Right => (x + 1, y),
        _ => (x, y)
    };
}
