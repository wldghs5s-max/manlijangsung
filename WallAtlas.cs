sealed record MapPort(int X, int Y, string Destination, bool StayOnWall);

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
    public const string Wall1 = "만리장성1";
    public const string Wall2 = "만리장성2";
    public const string Wall3 = "만리장성3";
    public const string Wall4 = "만리장성4";
    public const string Guknae = "국내성";
    public const string Jangan = "장안성";
    public const string Smith = "털보네대장간";

    public static readonly string[] Walls = [Wall1, Wall2, Wall3, Wall4];

    // 칸 크기는 클바지지 지도의 픽셀 크기를 48로 나눈 값이다.
    // 성문 좌표도 같은 지도의 출구다. 세계지도로 나가는 성문은 다른 성으로 이어진다.
    public static readonly IReadOnlyDictionary<string, GameMap> Maps = new Dictionary<string, GameMap>
    {
        [Wall1] = Map("만리장성1", 100, 30,
            Port(0, 3, Wall2, true),
            Port(99, 7, "세계지도", false),
            Port(11, 29, "세계지도", false)),
        [Wall2] = Map("만리장성2", 30, 100,
            Port(29, 9, Wall1, true),
            Port(5, 99, Wall3, true),
            Port(7, 0, "세계지도", false)),
        [Wall3] = Map("만리장성3", 60, 50,
            Port(43, 0, Wall2, true),
            Port(5, 0, Wall4, true),
            Port(11, 49, "세계지도", false)),
        [Wall4] = Map("만리장성4", 100, 30,
            Port(83, 29, Wall3, true),
            Port(0, 12, "세계지도", false))
    };

    public static bool IsWall(string? name) => name is Wall1 or Wall2 or Wall3 or Wall4;

    public static MapPort? WallPort(string map, int x, int y) =>
        Maps[map].Ports.FirstOrDefault(port => port.StayOnWall && port.X == x && port.Y == y);

    public static bool NearExit(string map, int x, int y)
    {
        foreach (MapPort port in Maps[map].Ports)
        {
            if (port.StayOnWall)
                continue;
            if (Math.Max(Math.Abs(port.X - x), Math.Abs(port.Y - y)) <= ExitBuffer)
                return true;
        }
        return false;
    }

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

    private static MapPort Port(int x, int y, string destination, bool stay) => new(x, y, destination, stay);
}
