using System.Runtime.InteropServices;
using OpenCvSharp;

readonly record struct TileScan(int X, int Y, bool Walkable, bool IsStaticTerrain);

sealed class WallVision
{
    private const int FrameWidth = 1920;
    private const int FrameHeight = 1080;
    private const int ViewLeft = 58;
    private const int ViewTop = 26;
    private const int ViewWidth = 1342;
    private const int ViewHeight = 812;
    private const int TileSize = 54;
    private const int PatchHalf = 18;

    private readonly byte[] frameBytes = new byte[FrameWidth * FrameHeight * 3];

    public string LastSkipReason { get; private set; } = "";
    public int LastEligibleCount { get; private set; }
    public int LastUnclassifiedCount { get; private set; }
    public double LastCenterUnclassifiedRatio { get; private set; }
    public bool HasLargeOverlay { get; private set; }
    public string LastOverlayReason { get; private set; } = "";

    public List<TileScan> Scan(Mat frame, string map, int playerX, int playerY)
    {
        LastSkipReason = "";
        LastEligibleCount = 0;
        LastUnclassifiedCount = 0;
        LastCenterUnclassifiedRatio = 0;
        HasLargeOverlay = false;
        LastOverlayReason = "";
        var results = new List<TileScan>();
        if (frame.Width != FrameWidth || frame.Height != FrameHeight || frame.Type() != MatType.CV_8UC3)
            return results;
        if (!WallAtlas.Maps.TryGetValue(map, out GameMap? area))
            return results;

        int stride = (int)frame.Step();
        if (stride == FrameWidth * 3)
        {
            Marshal.Copy(frame.Data, frameBytes, 0, frameBytes.Length);
        }
        else
        {
            for (int row = 0; row < FrameHeight; row++)
                Marshal.Copy(frame.Data + (row * stride), frameBytes, row * FrameWidth * 3, FrameWidth * 3);
        }

        double camX = Math.Clamp(
            (playerX * TileSize) + (TileSize / 2.0) - (ViewWidth / 2.0),
            0,
            Math.Max(0, (area.Width * TileSize) - ViewWidth));
        double camY = Math.Clamp(
            (playerY * TileSize) + (TileSize / 2.0) - (ViewHeight / 2.0),
            0,
            Math.Max(0, (area.Height * TileSize) - ViewHeight));

        int minTx = Math.Max(0, (int)Math.Floor(camX / TileSize));
        int maxTx = Math.Min(area.Width - 1, (int)Math.Ceiling((camX + ViewWidth) / TileSize));
        int minTy = Math.Max(0, (int)Math.Floor(camY / TileSize));
        int maxTy = Math.Min(area.Height - 1, (int)Math.Ceiling((camY + ViewHeight) / TileSize));

        int walkableCount = 0;
        int darkTileCount = 0;
        int flagTileCount = 0;
        int grassTileCount = 0;
        int brownTileCount = 0;
        int brownCenterCount = 0;
        int centerTotal = 0;
        int eligibleTiles = 0;
        int unclassifiedTiles = 0;
        int centerEligible = 0;
        int centerUnclassified = 0;
        int centerDark = 0;
        int outerEligible = 0;
        int outerDark = 0;
        long sumR = 0;
        long sumG = 0;
        long sumB = 0;
        int sampleTiles = 0;

        for (int ty = minTy; ty <= maxTy; ty++)
        {
            for (int tx = minTx; tx <= maxTx; tx++)
            {
                if (tx == playerX && ty == playerY)
                    continue;

                int cx = (int)Math.Round(ViewLeft + (tx * TileSize) + (TileSize / 2.0) - camX);
                int cy = (int)Math.Round(ViewTop + (ty * TileSize) + (TileSize / 2.0) - camY);

                int x0 = cx - PatchHalf;
                int x1 = cx + PatchHalf;
                int y0 = cy - PatchHalf;
                // 플레이어 바로 위 칸은 머리/무기 스프라이트가 하단을 가리므로 위쪽 절반만 본다.
                bool abovePlayer = tx == playerX && ty == playerY - 1;
                int y1 = abovePlayer ? cy : cy + PatchHalf;

                if (x0 < ViewLeft || y0 < 48 || x1 > 1400 || y1 > 836)
                    continue;
                if (x0 < 375 && y0 < 160)
                    continue; // 좌측 상단 버프 바
                if (x1 > 1230 && y0 < 255)
                    continue; // 우측 상단 미니맵

                bool inCenter = cx is >= 280 and <= 1220 && cy is >= 120 and <= 780;
                eligibleTiles++;
                if (inCenter)
                    centerEligible++;
                else
                    outerEligible++;

                if (TryClassifyPatch(
                    x0, y0, x1, y1, abovePlayer,
                    out bool walkable, out bool isStatic, out bool isDark, out bool isGrass, out bool isBrown, out bool isFlag,
                    out int avgR, out int avgG, out int avgB))
                {
                    results.Add(new TileScan(tx, ty, walkable, isStatic));
                    if (walkable)
                        walkableCount++;
                    else
                    {
                        if (isDark)
                        {
                            darkTileCount++;
                            if (inCenter)
                                centerDark++;
                            else
                                outerDark++;
                        }
                        if (isGrass)
                            grassTileCount++;
                        if (isBrown)
                            brownTileCount++;
                        if (isFlag)
                            flagTileCount++;
                    }
                    if (cx is >= 620 and <= 1220 && cy is >= 250 and <= 760)
                    {
                        centerTotal++;
                        if (!walkable && isBrown)
                            brownCenterCount++;
                    }
                    sumR += avgR;
                    sumG += avgG;
                    sumB += avgB;
                    sampleTiles++;
                }
                else
                {
                    unclassifiedTiles++;
                    if (inCenter)
                        centerUnclassified++;
                }
            }
        }

        bool isDarkTransition = eligibleTiles > 0 && darkTileCount > eligibleTiles * 0.55;
        double unclassifiedRatio = eligibleTiles > 0 ? (double)unclassifiedTiles / eligibleTiles : 0;
        double centerUnclassifiedRatio = centerEligible > 0 ? (double)centerUnclassified / centerEligible : 0;
        double centerDarkRatio = centerEligible > 0 ? (double)centerDark / centerEligible : 0;
        double outerDarkRatio = outerEligible > 0 ? (double)outerDark / outerEligible : 0;

        LastEligibleCount = eligibleTiles;
        LastUnclassifiedCount = unclassifiedTiles;
        LastCenterUnclassifiedRatio = centerUnclassifiedRatio;

        string avgRgb = sampleTiles > 0 ? $"{sumR / sampleTiles},{sumG / sampleTiles},{sumB / sampleTiles}" : "-";
        if (results.Count > 0 && darkTileCount > results.Count * 0.65)
        {
            LastSkipReason = $"암전({darkTileCount}/{results.Count})";
            return [];
        }
        if (!isDarkTransition)
        {
            if (eligibleTiles >= 200 && unclassifiedTiles >= 65 && unclassifiedRatio >= 0.21)
            {
                HasLargeOverlay = true;
                LastOverlayReason = $"미분류UI({unclassifiedTiles}/{eligibleTiles},{unclassifiedRatio:P0})";
            }
            else if (centerEligible >= 100 && centerUnclassified >= 48 && centerUnclassifiedRatio >= 0.28)
            {
                HasLargeOverlay = true;
                LastOverlayReason = $"중앙팝업UI({centerUnclassified}/{centerEligible},{centerUnclassifiedRatio:P0})";
            }
            else if (centerEligible >= 100 && outerEligible >= 40 && centerDark >= 48 && centerDarkRatio >= 0.28 && outerDarkRatio <= 0.20)
            {
                HasLargeOverlay = true;
                LastOverlayReason = $"중앙암부팝업({centerDark}/{centerEligible},외곽:{outerDark}/{outerEligible})";
            }
        }
        if (HasLargeOverlay)
        {
            LastSkipReason = $"팝업가림({LastOverlayReason})";
            return [];
        }
        if (centerTotal >= 12 && brownCenterCount > centerTotal * 0.60)
        {
            LastSkipReason = $"대화창({brownCenterCount}/{centerTotal})";
            return [];
        }
        if (flagTileCount > 60)
        {
            LastSkipReason = $"색감보류(깃발오인:{flagTileCount},rgb:{avgRgb})";
            return [];
        }
        if (results.Count >= 40 && walkableCount < Math.Max(20, results.Count * 0.12))
        {
            LastSkipReason = $"조명보류(길:{walkableCount}/풀:{grassTileCount}/갈:{brownTileCount}/기:{flagTileCount}/암:{darkTileCount},rgb:{avgRgb})";
            return [];
        }

        return results;
    }

    private bool TryClassifyPatch(
        int x0,
        int y0,
        int x1,
        int y1,
        bool ignoreDarkSprites,
        out bool walkable,
        out bool isStaticTerrain,
        out bool isDark,
        out bool isGrass,
        out bool isBrown,
        out bool isFlag,
        out int avgR,
        out int avgG,
        out int avgB)
    {
        isDark = false;
        isGrass = false;
        isBrown = false;
        isFlag = false;
        avgR = 0;
        avgG = 0;
        avgB = 0;
        int total = (x1 - x0) * (y1 - y0);
        if (total <= 0)
        {
            walkable = false;
            isStaticTerrain = false;
            return false;
        }

        int stone = 0;
        int grass = 0;
        int brown = 0;
        int flagBlue = 0;
        int flagRed = 0;
        int dark = 0;
        long totalR = 0;
        long totalG = 0;
        long totalB = 0;

        for (int y = y0; y < y1; y++)
        {
            int rowBase = y * FrameWidth * 3;
            for (int x = x0; x < x1; x++)
            {
                int off = rowBase + (x * 3);
                int b = frameBytes[off];
                int g = frameBytes[off + 1];
                int r = frameBytes[off + 2];
                totalR += r;
                totalG += g;
                totalB += b;

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));

                if (max < 35)
                {
                    dark++;
                    continue;
                }
                if (g > r + 15 && g > b + 20)
                {
                    grass++;
                    continue;
                }
                if (b > r + 22 && b > g + 10 && b > 105)
                {
                    flagBlue++;
                    continue;
                }
                if (r > b + 50 && r > g + 40 && r > 120 && g < 115)
                {
                    flagRed++;
                    continue;
                }
                if (r > b + 36 && r > g + 12 && g > b + 12 && r > 90)
                {
                    brown++;
                    continue;
                }
                if (max - min <= 34 && min >= 45 && max <= 220)
                    stone++;
            }
        }

        avgR = (int)(totalR / total);
        avgG = (int)(totalG / total);
        avgB = (int)(totalB / total);

        if (flagBlue > total * 0.05 || flagRed > total * 0.05)
        {
            walkable = false;
            isStaticTerrain = false;
            isFlag = true;
            return true;
        }

        if (grass > total * 0.25 || brown > total * 0.35)
        {
            walkable = false;
            isStaticTerrain = true;
            isGrass = grass > total * 0.25;
            isBrown = !isGrass;
            return true;
        }

        if (!ignoreDarkSprites && dark > total * 0.55)
        {
            walkable = false;
            isStaticTerrain = false;
            isDark = true;
            return true;
        }

        if (stone > total * 0.45)
        {
            walkable = true;
            isStaticTerrain = true;
            return true;
        }

        walkable = false;
        isStaticTerrain = false;
        return false;
    }
}
