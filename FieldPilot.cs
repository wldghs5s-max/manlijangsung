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
    Dash
}

sealed record FieldView(string? Map, int? X, int? Y, bool NeedsBricks, bool UiBlocked, TimeSpan Now);

sealed record FieldCommand(FieldKey? Hold, FieldKey? Tap, bool Dash, string Reason);

sealed class FieldPilot
{
    private static readonly TimeSpan TapGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DashGap = TimeSpan.FromSeconds(1);
    private static readonly (int X, int Y)[] SmithPath = [(16, 108), (40, 121), (55, 123), (59, 121)];
    private static readonly (int X, int Y) JanganGate = (115, 7);

    private readonly Random random;
    private string? waypointMap;
    private int waypointX;
    private int waypointY;
    private SupplyPhase phase = SupplyPhase.Patrol;
    private int supplyIssued;
    private TimeSpan supplyAt = TimeSpan.MinValue;
    private TimeSpan lastDash = TimeSpan.MinValue;

    public FieldPilot(Random random) => this.random = random;

    public FieldCommand Decide(FieldView view)
    {
        if (view.UiBlocked)
            return new FieldCommand(null, null, false, "대화가 열려 이동과 풍신보를 멈춤");

        if (phase == SupplyPhase.Patrol && view.NeedsBricks)
        {
            phase = SupplyPhase.ToGuknae;
            supplyIssued = 0;
            supplyAt = TimeSpan.MinValue;
        }

        if (phase != SupplyPhase.Patrol)
            return Supply(view);

        if (view.Map is not { } map || !WallAtlas.IsWall(map) || view.X is not int x || view.Y is not int y)
            return new FieldCommand(null, null, false, "만리장성 좌표를 확인한 뒤에 이동");

        return MoveOnWall(map, x, y, view.Now);
    }

    private FieldCommand Supply(FieldView view)
    {
        switch (phase)
        {
            case SupplyPhase.ToGuknae:
                if (supplyIssued == 0)
                    return Issue(view.Now, FieldKey.Secretary, "벽돌이 없어 국내성 비서를 연다");
                if (view.Now - supplyAt < TapGap)
                    return Wait("국내성 비서 선택 대기");
                if (supplyIssued == 1)
                    return Issue(view.Now, FieldKey.Guknae, "국내성 비서를 사용");
                if (view.Map == WallAtlas.Guknae)
                {
                    phase = SupplyPhase.BlinkWest;
                    supplyIssued = 0;
                    supplyAt = TimeSpan.MinValue;
                }
                return Wait("국내성 도착 대기");
            case SupplyPhase.BlinkWest:
                if (supplyIssued == 0)
                    return Issue(view.Now, FieldKey.Blink, "비영사천문으로 국내성 서쪽으로 이동");
                if (view.Now - supplyAt < TapGap)
                    return Wait("비영사천문 방향 대기");
                if (supplyIssued == 1)
                    return Issue(view.Now, FieldKey.Left, "비영사천문 방향을 서쪽으로");
                if (view.Map == WallAtlas.Guknae && view.X is int x && x <= 80 && view.Y is int)
                {
                    phase = SupplyPhase.WalkSmith;
                    supplyIssued = 0;
                }
                return Wait("국내성 서쪽 착지 대기");
            case SupplyPhase.WalkSmith:
                if (view.Map == WallAtlas.Smith)
                {
                    phase = SupplyPhase.Buy;
                    return Wait("털보네대장간 도착");
                }
                if (view.Map != WallAtlas.Guknae || view.X is not int sx || view.Y is not int sy)
                    return Wait("국내성 좌표 대기");
                (int tx, int ty) = NextSmithPoint(sx, sy);
                return HoldToward(sx, sy, tx, ty, view.Now, "털보네대장간으로 다리를 건너 이동");
            case SupplyPhase.Buy:
                if (view.Map == WallAtlas.Guknae)
                {
                    phase = SupplyPhase.ToJangan;
                    supplyIssued = 0;
                    supplyAt = TimeSpan.MinValue;
                    break;
                }
                return Wait("대장간에서 벽돌 100개 구매");
            case SupplyPhase.ToJangan:
                if (supplyIssued == 0)
                    return Issue(view.Now, FieldKey.Secretary, "장안성 비서를 연다");
                if (view.Now - supplyAt < TapGap)
                    return Wait("장안성 비서 선택 대기");
                if (supplyIssued == 1)
                    return Issue(view.Now, FieldKey.Jangan, "장안성 비서를 사용");
                if (view.Map == WallAtlas.Jangan)
                {
                    phase = SupplyPhase.BlinkNorth;
                    supplyIssued = 0;
                    supplyAt = TimeSpan.MinValue;
                }
                return Wait("장안성 도착 대기");
            case SupplyPhase.BlinkNorth:
                if (supplyIssued == 0)
                    return Issue(view.Now, FieldKey.Blink, "비영사천문으로 장안성 북쪽에 접근");
                if (view.Now - supplyAt < TapGap)
                    return Wait("비영사천문 방향 대기");
                if (supplyIssued == 1)
                    return Issue(view.Now, FieldKey.Up, "비영사천문 방향을 북쪽으로");
                phase = SupplyPhase.WalkGate;
                supplyIssued = 0;
                break;
            case SupplyPhase.WalkGate:
                if (WallAtlas.IsWall(view.Map))
                {
                    phase = SupplyPhase.Patrol;
                    waypointMap = null;
                    return Wait("만리장성에 들어와 순찰을 시작");
                }
                if (view.Map != WallAtlas.Jangan || view.X is not int jx || view.Y is not int jy)
                    return Wait("장안성 북문 좌표 대기");
                if (Math.Max(Math.Abs(jx - JanganGate.X), Math.Abs(jy - JanganGate.Y)) <= 3)
                    return Wait("지역 지도에서 장안성에 연결된 만리장성을 선택");
                return HoldToward(jx, jy, JanganGate.X, JanganGate.Y, view.Now, "장안성 북문으로 이동");
        }

        return Supply(view);
    }

    private FieldCommand MoveOnWall(string map, int x, int y, TimeSpan now)
    {
        if (WallAtlas.NearExit(map, x, y))
        {
            waypointMap = null;
            return HoldAway(map, x, y, now, "다른 성으로 나가는 성문이라 물러남");
        }

        if (!HasWaypoint(map, x, y))
            ChooseWaypoint(map);

        if (waypointMap != map)
        {
            MapPort hop = WallAtlas.Hop(map, waypointMap!);
            if (x == hop.X && y == hop.Y)
                return new FieldCommand(null, null, false, $"{hop.Destination}으로 이어진 성문에 도착");
            return HoldToward(x, y, hop.X, hop.Y, now, $"{hop.Destination}으로 이어진 성문으로 이동", map);
        }

        if (Math.Max(Math.Abs(waypointX - x), Math.Abs(waypointY - y)) <= 2)
        {
            waypointMap = null;
            ChooseWaypoint(map);
        }

        return HoldToward(x, y, waypointX, waypointY, now, $"{map} 안을 이동", map);
    }

    private bool HasWaypoint(string map, int x, int y)
    {
        if (waypointMap is null || !WallAtlas.IsWall(waypointMap))
            return false;
        if (waypointMap == map && (waypointX < 0 || Math.Max(Math.Abs(waypointX - x), Math.Abs(waypointY - y)) <= 2))
            return false;
        return true;
    }

    private void ChooseWaypoint(string map)
    {
        GameMap area = WallAtlas.Maps[map];
        string destination = random.Next(2) == 0 ? map : WallAtlas.Walls[random.Next(WallAtlas.Walls.Length)];
        if (destination != map)
        {
            waypointMap = destination;
            waypointX = -1;
            waypointY = -1;
            return;
        }

        for (int attempt = 0; attempt < 40; attempt++)
        {
            int tx = 2 + random.Next(Math.Max(1, area.Width - 4));
            int ty = 2 + random.Next(Math.Max(1, area.Height - 4));
            if (WallAtlas.NearExit(map, tx, ty))
                continue;
            waypointMap = map;
            waypointX = tx;
            waypointY = ty;
            return;
        }

        waypointMap = map;
        waypointX = area.Width / 2;
        waypointY = area.Height / 2;
    }

    private FieldCommand HoldToward(int x, int y, int targetX, int targetY, TimeSpan now, string reason, string? blockMap = null)
    {
        FieldKey? direction = ChooseDirection(x, y, targetX, targetY, blockMap);
        if (direction is null)
            return new FieldCommand(null, null, false, reason);
        bool dash = lastDash == TimeSpan.MinValue || now - lastDash >= DashGap;
        if (dash)
            lastDash = now;
        return new FieldCommand(direction, null, dash, reason);
    }

    private FieldCommand HoldAway(string map, int x, int y, TimeSpan now, string reason)
    {
        MapPort exit = WallAtlas.Maps[map].Ports.First(port => !port.StayOnWall
            && Math.Max(Math.Abs(port.X - x), Math.Abs(port.Y - y)) <= WallAtlas.ExitBuffer);
        int awayX = x + Math.Sign(x - exit.X);
        int awayY = y + Math.Sign(y - exit.Y);
        if (awayX == x && awayY == y)
            awayX = Math.Clamp(x + 1, 0, WallAtlas.Maps[map].Width - 1);
        return HoldToward(x, y, awayX, awayY, now, reason, map);
    }

    private static FieldKey? ChooseDirection(int x, int y, int targetX, int targetY, string? blockMap)
    {
        int dx = Math.Sign(targetX - x);
        int dy = Math.Sign(targetY - y);
        (int X, int Y)[] options = Math.Abs(targetX - x) >= Math.Abs(targetY - y)
            ? [(dx, 0), (0, dy), (-dy, 0), (0, -dx)]
            : [(0, dy), (dx, 0), (0, -dx), (-dy, 0)];
        foreach ((int stepX, int stepY) in options)
        {
            if (stepX == 0 && stepY == 0)
                continue;
            int nextX = x + stepX;
            int nextY = y + stepY;
            if (blockMap is not null && WallAtlas.NearExit(blockMap, nextX, nextY))
                continue;
            return ToKey(stepX, stepY);
        }
        return null;
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

    private static (int X, int Y) NextSmithPoint(int x, int y)
    {
        foreach ((int pointX, int pointY) in SmithPath)
        {
            if (Math.Max(Math.Abs(pointX - x), Math.Abs(pointY - y)) > 2)
                return (pointX, pointY);
        }
        return SmithPath[^1];
    }

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

static class FieldInput
{
    public static void Apply(FieldCommand command)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (command.Tap is FieldKey tap)
            Tap(tap);
        if (command.Dash)
            Tap(FieldKey.Dash);
    }

    private static void Tap(FieldKey key)
    {
        byte virtualKey = key switch
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
            _ => 0
        };
        if (virtualKey == 0)
            return;
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 2, UIntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scan, uint flags, UIntPtr extra);
}

static class FieldRehearsal
{
    public static List<string> Run()
    {
        var failures = new List<string>();
        CheckDialogStops(failures);
        CheckBrickRunStartsWithSecretary(failures);
        CheckSmithUsesBridge(failures);
        CheckPatrolStaysOnTheWall(failures);
        return failures;
    }

    private static void CheckDialogStops(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        FieldCommand command = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, false, true, TimeSpan.FromSeconds(3)));
        if (command.Hold is not null || command.Dash || command.Tap is not null)
            failures.Add("대화가 열린 동안 이동 키가 나갔습니다.");
    }

    private static void CheckBrickRunStartsWithSecretary(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        FieldCommand open = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.Zero));
        FieldCommand select = pilot.Decide(new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.FromMilliseconds(250)));
        if (open.Tap != FieldKey.Secretary || select.Tap != FieldKey.Guknae)
            failures.Add("벽돌이 떨어지면 국내성 비서 U 다음 M을 눌러야 합니다.");
    }

    private static void CheckSmithUsesBridge(List<string> failures)
    {
        var pilot = new FieldPilot(new Random(1));
        var view = new FieldView(WallAtlas.Wall3, 16, 46, true, false, TimeSpan.Zero);
        pilot.Decide(view);
        pilot.Decide(view with { Now = TimeSpan.FromMilliseconds(250) });
        pilot.Decide(view with { Map = WallAtlas.Guknae, X = 204, Y = 108, Now = TimeSpan.FromMilliseconds(500) });
        pilot.Decide(view with { Map = WallAtlas.Guknae, X = 204, Y = 108, Now = TimeSpan.FromMilliseconds(750) });
        pilot.Decide(view with { Map = WallAtlas.Guknae, X = 16, Y = 108, Now = TimeSpan.FromSeconds(1) });
        pilot.Decide(view with { Map = WallAtlas.Guknae, X = 16, Y = 108, Now = TimeSpan.FromSeconds(1.3) });
        FieldCommand walk = pilot.Decide(view with { Map = WallAtlas.Guknae, X = 16, Y = 108, Now = TimeSpan.FromSeconds(2) });
        if (walk.Hold != FieldKey.Right)
            failures.Add("국내성 서쪽 착지 뒤에는 영상에서 확인한 다리 방향으로 이동해야 합니다.");
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

    private static (int X, int Y) Move(int x, int y, FieldKey direction) => direction switch
    {
        FieldKey.Up => (x, y - 1),
        FieldKey.Down => (x, y + 1),
        FieldKey.Left => (x - 1, y),
        FieldKey.Right => (x + 1, y),
        _ => (x, y)
    };
}
