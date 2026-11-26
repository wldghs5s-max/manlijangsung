sealed record MapPort(int X, int Y, string Destination, bool StayOnWall, FieldKey? Outward = null, int? LandX = null, int? LandY = null);

sealed class GameMap
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required MapPort[] Ports { get; init; }
}

static class WallAtlas
{
    public const int ExitBuffer = 2;
    public const int EdgeBuffer = 3;
    public const string Wall1 = "만리장성1";
    public const string Wall2 = "만리장성2";
    public const string Wall3 = "만리장성3";
    public const string Wall4 = "만리장성4";
    public const string Guknae = "국내성";
    public const string Jangan = "장안성";
    public const string Smith = "털보네대장간";

    public static readonly string[] Walls = [Wall1, Wall2, Wall3, Wall4];

    // 칸 크기는 클바지지 지도의 픽셀 크기를 48로 나눈 값이다.
    // 성벽 간 연결 성문 좌표와 도착 좌표는 실제 주행 영상(3↔4, 3↔2, 2↔1)에서 확인한 값이다.
    public static readonly IReadOnlyDictionary<string, GameMap> Maps = new Dictionary<string, GameMap>
    {
        [Wall1] = Map("만리장성1", 100, 30,
            Port(0, 7, Wall2, true, FieldKey.Left, 27, 17),
            Port(99, 7, "세계지도", false),
            Port(11, 29, "세계지도", false)),
        [Wall2] = Map("만리장성2", 30, 100,
            Port(29, 20, Wall1, true, FieldKey.Right, 1, 7),
            Port(8, 99, Wall3, true, FieldKey.Down, 46, 2),
            Port(7, 0, "세계지도", false)),
        [Wall3] = Map("만리장성3", 60, 50,
            Port(46, 0, Wall2, true, FieldKey.Up, 8, 97),
            Port(7, 0, Wall4, true, FieldKey.Up, 85, 27),
            Port(11, 49, "세계지도", false),
            Port(16, 49, "세계지도", false)),
        [Wall4] = Map("만리장성4", 100, 30,
            Port(85, 29, Wall3, true, FieldKey.Down, 7, 2),
            Port(0, 12, "세계지도", false))
    };

    public static bool IsWall(string? name) => name is Wall1 or Wall2 or Wall3 or Wall4;

    public static MapPort? WallPort(string map, int x, int y) =>
        Maps[map].Ports.FirstOrDefault(port => port.StayOnWall && port.X == x && port.Y == y);

    public static bool NearExit(string map, int x, int y) =>
        NearBlockedPort(map, x, y, null);

    public static bool NearBlockedPort(string map, int x, int y, IReadOnlySet<string>? allowedWalls)
    {
        foreach (MapPort port in Maps[map].Ports)
        {
            if (!IsBlockedPort(port, allowedWalls))
                continue;
            if (Math.Max(Math.Abs(port.X - x), Math.Abs(port.Y - y)) <= ExitBuffer)
                return true;
        }
        return false;
    }

    public static bool IsInAllowedWallCorridor(string map, int x, int y, IReadOnlySet<string>? allowedWalls)
    {
        foreach (MapPort port in Maps[map].Ports)
        {
            if (!port.StayOnWall)
                continue;
            if (allowedWalls is not null && !allowedWalls.Contains(port.Destination))
                continue;
            if (Math.Max(Math.Abs(port.X - x), Math.Abs(port.Y - y)) <= 4)
                return true;
        }
        return false;
    }

    public static bool IsForbiddenPatrolTile(string map, int x, int y, IReadOnlySet<string>? allowedWalls)
    {
        if (NearBlockedPort(map, x, y, allowedWalls))
            return true;
        if (WallPort(map, x, y) is not null)
            return true;

        GameMap area = Maps[map];
        if (x < EdgeBuffer || x >= area.Width - EdgeBuffer || y < EdgeBuffer || y >= area.Height - EdgeBuffer)
            return !IsInAllowedWallCorridor(map, x, y, allowedWalls);

        return false;
    }

    public static int SafetyDistance(string map, int x, int y, IReadOnlySet<string>? allowedWalls)
    {
        int portDist = DistanceToNearestBlockedPort(map, x, y, allowedWalls).Distance;
        if (IsInAllowedWallCorridor(map, x, y, allowedWalls))
            return portDist;

        GameMap area = Maps[map];
        int edgeDist = Math.Min(Math.Min(x, area.Width - 1 - x), Math.Min(y, area.Height - 1 - y));
        return Math.Min(portDist, edgeDist);
    }

    public static (int Distance, MapPort? Port) DistanceToNearestBlockedPort(
        string map,
        int x,
        int y,
        IReadOnlySet<string>? allowedWalls)
    {
        int bestDistance = int.MaxValue;
        MapPort? bestPort = null;
        foreach (MapPort port in Maps[map].Ports)
        {
            if (!IsBlockedPort(port, allowedWalls))
                continue;
            int distance = Math.Max(Math.Abs(port.X - x), Math.Abs(port.Y - y));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestPort = port;
            }
        }
        return (bestDistance, bestPort);
    }

    public static FieldKey? OutwardDirection(string map, MapPort port)
    {
        if (port.Outward is FieldKey outward)
            return outward;

        GameMap area = Maps[map];
        if (port.Y <= 0)
            return FieldKey.Up;
        if (port.Y >= area.Height - 1)
            return FieldKey.Down;
        if (port.X <= 0)
            return FieldKey.Left;
        if (port.X >= area.Width - 1)
            return FieldKey.Right;
        return null;
    }

    private static bool IsBlockedPort(MapPort port, IReadOnlySet<string>? allowedWalls) =>
        !port.StayOnWall || (allowedWalls is not null && !allowedWalls.Contains(port.Destination));

    public static MapPort Hop(string from, string to)
    {
        int current = Array.IndexOf(Walls, from);
        int goal = Array.IndexOf(Walls, to);
        string next = Walls[current + Math.Sign(goal - current)];
        return Maps[from].Ports.First(port => port.Destination == next);
    }

    public static (string Map, int X, int Y) Land(string from, int x, int y)
    {
        MapPort door = Maps[from].Ports.First(port => port.StayOnWall && port.X == x && port.Y == y);
        if (door.LandX is int landX && door.LandY is int landY)
            return (door.Destination, landX, landY);

        MapPort returning = Maps[door.Destination].Ports.First(port => port.Destination == from);
        GameMap arrival = Maps[door.Destination];
        int inwardX = Math.Clamp(returning.X + Math.Sign((arrival.Width / 2) - returning.X) * 2, 0, arrival.Width - 1);
        int inwardY = Math.Clamp(returning.Y + Math.Sign((arrival.Height / 2) - returning.Y) * 2, 0, arrival.Height - 1);
        if (NearExit(door.Destination, inwardX, inwardY))
            return (door.Destination, returning.X, returning.Y);
        return (door.Destination, inwardX, inwardY);
    }

    private static GameMap Map(string name, int width, int height, params MapPort[] ports) =>
        new() { Name = name, Width = width, Height = height, Ports = ports };

    private static MapPort Port(int x, int y, string destination, bool stay, FieldKey? outward = null, int? landX = null, int? landY = null) =>
        new(x, y, destination, stay, outward, landX, landY);
}
