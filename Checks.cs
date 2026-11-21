using GardenSteward;
public static class Checks
{
    public static void Run()
    {
        int n=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);n++;}
        var now=DateTimeOffset.FromUnixTimeSeconds(1800000000);
        string Body(string world="Unicorn",int id=30)=>$$"""{"worldName":"{{world}}","worldID":{{id}},"itemID":18,"lastUploadTime":1800000000000,"listings":[{"hq":false,"quantity":4,"pricePerUnit":100},{"hq":true,"quantity":5,"pricePerUnit":1}],"recentHistory":[{"hq":false,"quantity":3,"pricePerUnit":120,"timestamp":1799999999},{"hq":false,"quantity":7,"pricePerUnit":140,"timestamp":1799999998},{"hq":false,"quantity":99,"pricePerUnit":1,"timestamp":1800000001},{"hq":false,"quantity":99,"pricePerUnit":1,"timestamp":1700000000}]}""";
        var q=MarketResponse.Parse(Body(),new uint[]{18},"Unicorn",now)[18];
        Check(q.Minimum==100&&q.Median==130&&q.HistoryUnits==10&&q.HistoryCount==2,"NQ / window / future / units");
        Check(q.SaleReference==100,"conservative reference");
        Check(q.Sales.Count==2&&q.WeightedSalePrice==134,"sanitized sale history / weighted price");
        foreach(var b in new[]{Body("Other"),Body(id:31)}){bool rejected=false;try{MarketResponse.Parse(b,new uint[]{18},"Unicorn",now);}catch(FormatException){rejected=true;}Check(rejected,"world rejection");}
        Check(!q.Fresh(now.AddDays(2),24),"stale rejected");
        var settings=new MarketSettings();var quotes=new Dictionary<uint,MarketQuote>{{18,q}};
        var plan=new MarketPlan{Plots=8,HarvestPerPlot=3,ManualSeedCost=true,SeedCost=10,ManualSoilCost=true,SoilCost=20};
        Check(MarketMath.Profit(plan,new(18,5,2),q,quotes,settings,now)==520,"whole exchange batches / fee / materials");
        plan.HarvestPerPlot=0;
        Check(MarketMath.Profit(plan,new(18,1,1),q,quotes,settings,now)==null,"unknown yield remains unknown");
        plan.HarvestPerPlot=3;plan.ManualSeedCost=false;plan.Seed=999;
        Check(MarketMath.Profit(plan,new(18,1,1),q,quotes,settings,now)==null,"missing material remains unknown");
        Check(MarketResponse.Parse(Body(),new uint[]{19},"Unicorn",now).Count==0,"unrequested item ignored");
        foreach(var code in new[]{System.Net.HttpStatusCode.TooManyRequests,System.Net.HttpStatusCode.Forbidden})
        {
            using var client=new MarketClient(new FakeHttp(code));
            var result=client.Fetch("Unicorn",new uint[]{18},default).GetAwaiter().GetResult();
            Check(result.Error!=null&&result.Quotes.Count==0&&result.RateLimited==(code==System.Net.HttpStatusCode.TooManyRequests),"HTTP error has no invented quotes: "+code);
        }
        Console.WriteLine($"{n} checks passed");
    }
    private sealed class FakeHttp(System.Net.HttpStatusCode status):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken c)=>Task.FromResult(new HttpResponseMessage(status));
    }
}
