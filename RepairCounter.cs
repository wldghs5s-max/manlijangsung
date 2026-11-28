using OpenCvSharp;

sealed class RepairCounter
{
    private static readonly TimeSpan DialogGap = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ResultWait = TimeSpan.FromSeconds(3);

    private readonly int purchaseQuantity;
    private RepairDialog candidate;
    private TimeSpan candidateSince;
    private RepairDialog reported;
    private TimeSpan lastSeen;
    private (TimeSpan Time, BrickStock Stock)? crack;
    private TimeSpan lastCrackSeen;
    private int consumedSinceSupply;
    private bool resultReported;
    private bool autoStockTriggered;
    private int visibleStockStreak;
    private BrickStock lastVisibleStock = BrickStock.Unknown;
    private TimeSpan absentSince = TimeSpan.MinValue;

    public RepairCounter(int purchaseQuantity) => this.purchaseQuantity = purchaseQuantity;

    public int Consumed { get; private set; }
    public int Uncertain { get; private set; }
    public int Refills { get; private set; }
    public bool NeedsBricks { get; private set; }

    public void Observe(RepairScreen screen, Mat frame, TimeSpan time)
    {
        RepairDialog dialog = screen.ReadDialog(frame);
        if (dialog == reported && dialog != RepairDialog.None)
        {
            lastSeen = time;
            if (dialog == RepairDialog.Crack)
                lastCrackSeen = time;
        }
        else if (time - lastSeen >= DialogGap)
        {
            reported = RepairDialog.None;
        }

        if (crack is not null && time - lastCrackSeen >= ResultWait)
            Complete();

        if (dialog != candidate)
        {
            candidate = dialog;
            candidateSince = time;
            return;
        }
        if (dialog == RepairDialog.None || dialog == reported)
            return;

        reported = dialog;
        lastSeen = time;
        if (dialog == RepairDialog.Crack)
        {
            Complete();
            BrickStock stock = screen.ReadStock(frame);
            if (stock is BrickStock.One or BrickStock.Two or BrickStock.QuantityVisible)
                lastVisibleStock = stock;
            crack = (candidateSince, stock);
            lastCrackSeen = time;
            resultReported = false;
            Write(candidateSince, "균열", "만리장성이 부서져 가고 있다는 안내", StockText(stock));
            return;
        }

        if (resultReported)
            return;

        BrickStock stockAfter = screen.ReadStock(frame);
        BrickStock priorVisibleStock = lastVisibleStock;
        if (stockAfter is BrickStock.One or BrickStock.Two or BrickStock.QuantityVisible)
            lastVisibleStock = stockAfter;
        string inventory = crack is { } observedCrack
            ? $"{StockText(observedCrack.Stock)} → {StockText(stockAfter)}"
            : StockText(stockAfter);
        crack = null;
        resultReported = true;
        Consumed++;
        consumedSinceSupply++;
        Write(candidateSince, $"벽돌 1개 소모 (누적 {Consumed}개)", "수리 대화에서 벽돌 사용 확인", inventory);

        bool depletedFromLow = stockAfter == BrickStock.Absent
            && priorVisibleStock is BrickStock.One or BrickStock.Two;
        if (!depletedFromLow && consumedSinceSupply < purchaseQuantity)
            return;

        string reason = depletedFromLow
            ? "소지품에서 벽돌 항목이 사라짐"
            : $"이번 구매분에서 벽돌 {consumedSinceSupply}개 소모";
        Refills++;
        consumedSinceSupply = 0;
        NeedsBricks = true;
        autoStockTriggered = false;
        Write(candidateSince, "재구매 시점", reason, $"국내성 털보네대장간에서 {purchaseQuantity}개 구매");
    }

    public void ObserveStock(BrickStock stock, TimeSpan time, bool allowAutoCancel = true)
    {
        if (stock == BrickStock.Absent)
        {
            visibleStockStreak = 0;
            if (lastVisibleStock is not (BrickStock.One or BrickStock.Two))
            {
                absentSince = TimeSpan.MinValue;
                return;
            }
            if (absentSince == TimeSpan.MinValue)
                absentSince = time;
            else if (time - absentSince >= TimeSpan.FromSeconds(3))
            {
                if (!NeedsBricks)
                {
                    NeedsBricks = true;
                    autoStockTriggered = true;
                }
            }
            return;
        }

        absentSince = TimeSpan.MinValue;
        if (stock is BrickStock.One or BrickStock.Two or BrickStock.QuantityVisible)
        {
            lastVisibleStock = stock;
            visibleStockStreak++;
            if (allowAutoCancel && autoStockTriggered && NeedsBricks && visibleStockStreak >= 2)
            {
                NeedsBricks = false;
                autoStockTriggered = false;
            }
        }
        else
        {
            visibleStockStreak = 0;
        }
    }

    public void MarkSupplied()
    {
        NeedsBricks = false;
        autoStockTriggered = false;
        visibleStockStreak = 0;
        lastVisibleStock = BrickStock.Unknown;
        consumedSinceSupply = 0;
        absentSince = TimeSpan.MinValue;
    }

    public void Complete()
    {
        if (crack is not { } pending)
            return;

        Uncertain++;
        Write(pending.Time, "확인 보류", "균열 안내 이후 벽돌 사용 문구를 확인하지 못함", StockText(pending.Stock));
        crack = null;
    }

    private static string StockText(BrickStock stock) => stock switch
    {
        BrickStock.Absent => "벽돌 0개 (항목 사라짐)",
        BrickStock.One => "벽돌 1개",
        BrickStock.Two => "벽돌 2개",
        BrickStock.QuantityVisible => "벽돌 수량 표기 있음",
        _ => "벽돌 수량 판독 불가"
    };

    private static void Write(TimeSpan time, string status, string evidence, string inventory) =>
        Console.WriteLine($"[{time:hh\\:mm\\:ss\\.fff}] {status} | {evidence} | {inventory}");
}
