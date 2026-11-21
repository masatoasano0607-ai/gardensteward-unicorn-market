using System.Text.Json;

namespace GardenSteward;

public sealed class MarketSettings
{
    public bool Monitor { get; set; } = true;
    public string World { get; set; } = "Unicorn";
    public int IntervalMinutes { get; set; } = 10;
    public int MaxAgeHours { get; set; } = 24;
    public int SaleFeePercent { get; set; } = 5;
    public List<MarketPlan> Plans { get; set; } = [];
}
public sealed class MarketPlan
{
    public uint Crop { get; set; }
    public uint Seed { get; set; }
    public uint Soil { get; set; }
    public int HarvestPerPlot { get; set; }
    public int Hours { get; set; }
    public int Plots { get; set; } = 8;
    public bool ManualSeedCost { get; set; }
    public int SeedCost { get; set; }
    public bool ManualSoilCost { get; set; }
    public int SoilCost { get; set; }
    public int OtherCostPerPlot { get; set; }
    public List<MarketRoute> Routes { get; set; } = [];
}
public sealed record MarketRoute(uint SellItem, int CropCost, int ReceiveCount);
public sealed record MarketQuote(uint Item, long UploadMs, decimal? Minimum, decimal? Median,
    int HistoryCount, long HistoryUnits, long LastSaleSeconds)
{
    public List<Sale> Sales { get; init; } = [];
    public decimal? WeightedSalePrice => HistoryUnits > 0 ? Sales.Sum(s => (decimal)s.PricePerUnit * s.Quantity) / HistoryUnits : null;
    public bool Fresh(DateTimeOffset now, int hours) => UploadMs > 0 && UploadMs <= now.ToUnixTimeMilliseconds() + 300000 &&
        now.ToUnixTimeMilliseconds() - UploadMs <= Math.Clamp(hours, 1, 168) * 3600000L;
    public decimal? SaleReference => Minimum is > 0 && Median is > 0 ? Math.Min(Minimum.Value, Median.Value) : null;
}
public sealed record Sale(long Timestamp, long Quantity, long PricePerUnit);
public static class MarketMath
{
    public static decimal? PerCrop(MarketRoute route, MarketQuote? quote, int fee, DateTimeOffset now, int maxAge) =>
        route.CropCost > 0 && route.ReceiveCount > 0 && quote != null && quote.Fresh(now, maxAge) && fee is >= 0 and <= 100
            ? quote.SaleReference * route.ReceiveCount / route.CropCost * (100 - fee) / 100 : null;
    public static decimal? Cost(bool manual, int amount, uint item, IReadOnlyDictionary<uint, MarketQuote> quotes, DateTimeOffset now, int maxAge) =>
        manual ? (amount >= 0 ? amount : null) : quotes.TryGetValue(item, out var q) && q.Fresh(now, maxAge) ? q.Minimum : null;
    public static decimal? Profit(MarketPlan plan, MarketRoute route, MarketQuote? quote,
        IReadOnlyDictionary<uint, MarketQuote> quotes, MarketSettings settings, DateTimeOffset now)
    {
        if (plan.HarvestPerPlot <= 0 || plan.Plots is < 1 or > 8 || route.CropCost <= 0 || route.ReceiveCount <= 0 || plan.OtherCostPerPlot < 0) return null;
        if (PerCrop(route, quote, settings.SaleFeePercent, now, settings.MaxAgeHours) == null) return null;
        var seed = Cost(plan.ManualSeedCost, plan.SeedCost, plan.Seed, quotes, now, settings.MaxAgeHours);
        var soil = Cost(plan.ManualSoilCost, plan.SoilCost, plan.Soil, quotes, now, settings.MaxAgeHours);
        // Exchanges use whole batches; leftover crops are not counted as sold.
        var sold = ((long)plan.HarvestPerPlot * plan.Plots / route.CropCost) * route.ReceiveCount;
        return sold * quote!.SaleReference * (100 - settings.SaleFeePercent) / 100 -
            plan.Plots * (seed + soil + plan.OtherCostPerPlot);
    }
}
public static class MarketResponse
{
    public static Dictionary<uint, MarketQuote> Parse(string json, IReadOnlyCollection<uint> requested, string world, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (world == "Unicorn" && (Number(root, "worldID") != 30 || root.TryGetProperty("dcName", out _) || root.TryGetProperty("regionName", out _)))
            throw new FormatException("Unicorn単独のworldID=30を確認できません。");
        if (!root.TryGetProperty("worldName", out var w) || !string.Equals(w.GetString(), world, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("取得したワールドが指定と一致しません。");
        var result = new Dictionary<uint, MarketQuote>();
        if (root.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateObject()) Add(item.Value);
        }
        else Add(root);
        return result;
        void Add(JsonElement value)
        {
            if (value.TryGetProperty("worldID", out _) && Number(value, "worldID") != 30)
                throw new FormatException("アイテムのワールドがUnicornと一致しません。");
            var id = (uint)Number(value, "itemID");
            if (!requested.Contains(id)) return;
            var listing = new List<decimal>();
            if (value.TryGetProperty("listings", out var ls))
                foreach (var entry in ls.EnumerateArray())
                    if (!Hq(entry) && Number(entry, "quantity") > 0 && Number(entry, "pricePerUnit") > 0)
                        listing.Add(Number(entry, "pricePerUnit"));
            var prices = new List<decimal>(); var sales = new List<Sale>(); long units = 0, lastSale = 0;
            if (value.TryGetProperty("recentHistory", out var history))
                foreach (var entry in history.EnumerateArray())
                {
                    var stamp = Number(entry, "timestamp"); var qty = Number(entry, "quantity"); var price = Number(entry, "pricePerUnit");
                    if (Hq(entry) || qty <= 0 || price <= 0 || stamp > now.ToUnixTimeSeconds() || stamp < now.AddDays(-7).ToUnixTimeSeconds()) continue;
                    prices.Add(price); sales.Add(new(stamp,qty,price)); units += qty; lastSale = Math.Max(lastSale, stamp);
                }
            prices.Sort();
            decimal? median = prices.Count == 0 ? null : prices.Count % 2 == 1 ? prices[prices.Count / 2] :
                (prices[prices.Count / 2 - 1] + prices[prices.Count / 2]) / 2;
            result[id] = new(id, Number(value, "lastUploadTime"), listing.Count > 0 ? listing.Min() : null, median, prices.Count, units, lastSale) { Sales = sales };
        }
    }
    private static bool Hq(JsonElement e) => e.TryGetProperty("hq", out var h) && h.ValueKind == JsonValueKind.True;
    private static long Number(JsonElement e, string name) => e.TryGetProperty(name, out var n) && n.TryGetInt64(out var value) ? value : 0;
}

public static class MarketExchange
{
    public static MarketRoute? Single(uint crop, IEnumerable<(uint Item, uint Count, ushort Collectability, byte Type)> costs,
        IEnumerable<(uint Item, uint Count, bool Hq)> rewards)
    {
        var c = costs.Where(x => x.Count > 0).ToArray();
        // Empty receive slots can carry a default count of 1 in SpecialShop.
        var r = rewards.Where(x => x.Item != 0 && x.Count > 0).ToArray();
        if (c.Length != 1 || r.Length != 1 || c[0].Item != crop || crop == 0 || c[0].Collectability != 0 ||
            c[0].Type != 0 || r[0].Hq || c[0].Count > int.MaxValue || r[0].Count > int.MaxValue) return null;
        return new(r[0].Item, (int)c[0].Count, (int)r[0].Count);
    }
}
