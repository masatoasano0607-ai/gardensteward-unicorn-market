using GardenSteward;
using System.Net;
using System.Text;
using System.Text.Json;

var json = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
if (args.Contains("--test")) { Checks.Run(); return; }
var cfg = JsonSerializer.Deserialize<CloudConfig>(File.ReadAllText("config.json"), json)!;
if (cfg.Market.World != "Unicorn") throw new Exception("Only Unicorn is permitted");
var started = DateTimeOffset.UtcNow;
var ids = cfg.Market.Plans.SelectMany(p => p.Routes.Select(r => r.SellItem).Concat(new[]{p.Crop,p.Seed,p.Soil}))
    .Concat(cfg.Retainers.Where(r=>r.Item.HasValue).Select(r=>r.Item!.Value))
    .Concat(cfg.Items.Keys.Select(uint.Parse)).Where(i => i > 0).Distinct().Order().ToArray();
var quotes = new Dictionary<uint, MarketQuote>();
var errors = new List<string>(); var attempts = new List<object>();
using var client = new MarketClient();
foreach (var batch in ids.Chunk(100))
{
    MarketFetch? result = null;
    for (int attempt=1; attempt<=3; attempt++)
    {
        var at=DateTimeOffset.UtcNow;
        result = await client.Fetch("Unicorn", batch, CancellationToken.None);
        attempts.Add(new { at, attempt, items=batch, result.Error, result.RateLimited });
        if (result.Error == null && batch.All(id=>result.Quotes.ContainsKey(id)) || result.RateLimited) break;
        if (attempt<3) await Task.Delay(TimeSpan.FromSeconds(attempt*5));
    }
    if (result!.Error != null) errors.Add(result.Error);
    foreach (var pair in result.Quotes) quotes[pair.Key]=pair.Value;
}
var now=DateTimeOffset.UtcNow;
string Name(uint id) => cfg.Items.GetValueOrDefault(id.ToString(), $"アイテム {id}");
var items=ids.Select(id => {
    var q=quotes.GetValueOrDefault(id);
    return new { id, name=Name(id), source=$"https://universalis.app/market/{id}", quote=q,
        fresh=q?.Fresh(now,cfg.Market.MaxAgeHours) ?? false,
        missing=q==null, historyScope="NQ / 過去7日以内の最大100件。全販売量ではなく取得できた標本",
        historyPossiblyTruncated=true,
        observedUnitsPerDay=q==null ? (decimal?)null : q.HistoryUnits/7m,
        issues=q==null ? new[]{"取得欠損"} : new[]{!q.Fresh(now,cfg.Market.MaxAgeHours)?"投稿が古い、未来日時、または日時不明":null,
            q.Minimum==null?"NQ出品なし":null,q.Median==null?"有効な販売履歴なし":null}.Where(s=>s!=null).ToArray() };
}).ToArray();
var gardens=cfg.Market.Plans.SelectMany(p=>p.Routes.Select(r=>new {
    crop=p.Crop,cropName=Name(p.Crop),sellItem=r.SellItem,sellName=Name(r.SellItem),plots=p.Plots,
    seed=new{item=p.Seed,name=Name(p.Seed),quantity=p.Plots},soil=new{item=p.Soil,name=Name(p.Soil),quantity=p.Plots},
    harvestPerPlot=p.HarvestPerPlot>0?(int?)p.HarvestPerPlot:null,hours=p.Hours>0?(int?)p.Hours:null,
    exchange=r,profitPerCycle=MarketMath.Profit(p,r,quotes.GetValueOrDefault(r.SellItem),quotes,cfg.Market,now),
    netRevenuePerCrop=MarketMath.PerCrop(r,quotes.GetValueOrDefault(r.SellItem),cfg.Market.SaleFeePercent,now,cfg.Market.MaxAgeHours),
    missingInputs=new[]{p.HarvestPerPlot<=0?"収穫数":null,p.Hours<=0?"栽培時間":null,
        MarketMath.Cost(p.ManualSeedCost,p.SeedCost,p.Seed,quotes,now,cfg.Market.MaxAgeHours)==null?"新鮮な種価格":null,
        MarketMath.Cost(p.ManualSoilCost,p.SoilCost,p.Soil,quotes,now,cfg.Market.MaxAgeHours)==null?"新鮮な土価格":null}.Where(x=>x!=null).ToArray(),
    note="1巡の試算。収穫数未設定・価格欠損・古い相場では利益は不明。交換比率は既存GardenSteward設定由来。手入れ頻度は未確認。"
})).ToArray();
var retainers=cfg.Retainers.Select(r=>new {r.Job,r.Count,r.Item,r.QuantityPerDispatch,r.DispatchesPerWeek,r.CostPerDispatch,
    profitPerRetainer=RetainerProfit(r,quotes,cfg.Market,now),
    totalProfit=RetainerProfit(r,quotes,cfg.Market,now)*r.Count,
    note="調達可能品・装備による数量・ベンチャースクリップ等の費用が未設定なら不明。人数などの個人設定は公開しません。" }).ToArray();
var report=new { schemaVersion=1,world="Unicorn",worldId=30,startedAt=started,fetchedAt=now,
    fetchedAtJst=now.ToOffset(TimeSpan.FromHours(9)),status=errors.Count>0||items.Any(i=>i.missing)?"incomplete":"fetched",
    worldVerified=quotes.Count>0,feePercent=cfg.Market.SaleFeePercent,errors,attempts,items,gardens,retainers,
    quality=new{requested=ids.Length,received=quotes.Count,freshItems=items.Count(i=>i.fresh),
        missingItems=items.Where(i=>i.missing).Select(i=>i.id).ToArray(),
        staleItems=items.Where(i=>!i.missing&&!i.fresh).Select(i=>i.id).ToArray(),
        gardenProfitAvailable=gardens.Count(g=>g.profitPerCycle.HasValue),
        retainerProfitAvailable=retainers.Count(r=>r.profitPerRetainer.HasValue)},
    weeklyRetainerProfit=retainers.All(r=>r.totalProfit.HasValue)?retainers.Sum(r=>r.totalProfit):(decimal?)null,
    warning="取得成功と相場の新鮮さは別です。利益は売却保証なし。0と不明を区別。投稿履歴は全取引を網羅しません。" };
Directory.CreateDirectory("site");
var payload=JsonSerializer.Serialize(report,json);
File.WriteAllText("site/latest.json",payload);
var compact=System.Text.Json.Nodes.JsonNode.Parse(payload)!;
foreach(var item in compact["items"]!.AsArray()) item?["quote"]?.AsObject().Remove("Sales");
File.WriteAllText("site/summary.json",compact.ToJsonString(json));
Directory.CreateDirectory("site/snapshots");
File.WriteAllText($"site/snapshots/{now:yyyyMMddTHHmmssZ}.json",payload);
string E(object? v)=>WebUtility.HtmlEncode(v?.ToString()??"不明");
var html=new StringBuilder("<!doctype html><html lang=ja><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Unicorn 相場レポート</title><style>body{font:16px system-ui;max-width:1200px;margin:32px auto;padding:0 20px;background:#f5f7fa;color:#172638}table{border-collapse:collapse;width:100%;background:white}td,th{padding:10px;border:1px solid #ddd;text-align:left}h1,h2{color:#174b65}.warn{background:#fff0c2;padding:16px}details{margin:16px 0}pre{white-space:pre-wrap;overflow-wrap:anywhere}</style><h1>FF14 Unicorn 相場レポート</h1>");
html.Append($"<p>取得日時（JST）: {E(report.fetchedAtJst)} / 状態: {E(report.status)} / Unicorn照合: {E(report.worldVerified)}</p><p><a href='latest.json'>JSONデータ</a></p><p class=warn>{E(report.warning)}<br>販売量は7日以内・最大100件のNQ履歴標本です。古い投稿価格は利益計算から除外します。</p>");
foreach(var error in errors) html.Append($"<p class=warn>{E(error)}</p>");
html.Append("<h2>相場・販売量</h2><table><tr><th>品名</th><th>最安値</th><th>販売中央値</th><th>標本販売個数 / 件数</th><th>投稿日時（UTC）</th><th>状態</th></tr>");
foreach(var i in items) html.Append($"<tr><td>{E(i.name)} ({i.id})</td><td>{E(i.quote?.Minimum)}</td><td>{E(i.quote?.Median)}</td><td>{E(i.quote?.HistoryUnits)} / {E(i.quote?.HistoryCount)}</td><td>{E(i.quote?.UploadMs>0?DateTimeOffset.FromUnixTimeMilliseconds(i.quote.UploadMs):null)}</td><td>{E(string.Join(" / ",i.issues))}</td></tr>");
html.Append("</table><h2>畑：必要素材と1巡の利益</h2><p>作物1個の税引後売上は交換比率を反映した参考値です。材料費を引いた利益ではありません。端数は1巡の利益計算で切り捨てます。</p><table><tr><th>作物 → 売却先</th><th>必要素材</th><th>作物1個の税引後売上 G</th><th>1巡の見込み利益 G</th><th>不足情報</th></tr>");
foreach(var g in gardens) html.Append($"<tr><td>{E(g.cropName)} → {E(g.sellName)}</td><td>{E(g.seed.name)} × {g.seed.quantity} / {E(g.soil.name)} × {g.soil.quantity}</td><td>{E(g.netRevenuePerCrop)}</td><td>{E(g.profitPerCycle)}</td><td>{E(string.Join(" / ",g.missingInputs))}</td></tr>");
html.Append("</table><p>収穫数は設定値を使用します。栽培時間・必要な手入れ頻度が未確認の案は、週2〜3回のログインでの運用を自動推薦しません。</p><h2>リテイナー</h2>");
foreach(var r in retainers) html.Append($"<p>{E(r.Job)} 人数: {E(r.Count)} / 1人週間利益: {E(r.profitPerRetainer)} / 合計: {E(r.totalProfit)}</p>");
html.Append($"<details><summary>取得・計算の詳細</summary><pre>{E(payload)}</pre></details></html>");
File.WriteAllText("site/index.html",html.ToString());
Console.WriteLine($"{report.status}: {quotes.Count}/{ids.Length} items; {errors.Count} errors; output=site");
if(errors.Count>0||items.Any(i=>i.missing)) Environment.ExitCode=2;

static decimal? RetainerProfit(Retainer r,IReadOnlyDictionary<uint,MarketQuote> q,MarketSettings s,DateTimeOffset now)
{
    if(r.Item==null||r.QuantityPerDispatch is not >0||r.DispatchesPerWeek is not >0||r.CostPerDispatch is not >=0) return null;
    var price=MarketMath.PerCrop(new(r.Item.Value,1,1),q.GetValueOrDefault(r.Item.Value),s.SaleFeePercent,now,s.MaxAgeHours);
    return (price*r.QuantityPerDispatch-r.CostPerDispatch)*r.DispatchesPerWeek;
}
public sealed class CloudConfig { public MarketSettings Market {get;set;}=new(); public Dictionary<string,string> Items {get;set;}=new(); public List<Retainer> Retainers {get;set;}=[]; }
public sealed class Retainer {public string Job {get;set;}="";public int? Count {get;set;} public uint? Item {get;set;} public int? QuantityPerDispatch {get;set;} public int? DispatchesPerWeek {get;set;} public decimal? CostPerDispatch {get;set;} }
