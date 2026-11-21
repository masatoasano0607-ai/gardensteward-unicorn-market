using System.Net;
using System.Net.Http;

namespace GardenSteward;

public sealed record MarketFetch(string World, Dictionary<uint, MarketQuote> Quotes, string? Error, bool RateLimited = false);
public sealed class MarketClient : IDisposable
{
    private readonly HttpClient http;
    public MarketClient(HttpMessageHandler? handler = null)
    {
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(25);
        http.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GardenSteward/0.6.0");
    }
    public async Task<MarketFetch> Fetch(string world, uint[] items, CancellationToken cancellation)
    {
        try
        {
            if (items.Length is < 1 or > 100 || items.Any(i => i == 0)) throw new FormatException("相場取得は1～100種類のアイテムが対象です。");
            var uri = $"https://universalis.app/api/v2/{Uri.EscapeDataString(world)}/{string.Join(',', items)}?listings=20&entries=100&hq=false&entriesWithin=604800";
            using var response = await http.GetAsync(uri, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new(world, [], "相場サービスの混雑により15分待って再取得します。", true);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return new(world, MarketResponse.Parse(json, items, world, DateTimeOffset.UtcNow), null);
        }
        catch (OperationCanceledException) { return new(world, [], "相場取得を中断、または時間切れになりました。"); }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or System.Text.Json.JsonException or InvalidOperationException or OverflowException)
        { return new(world, [], "相場を取得できませんでした：" + ex.Message); }
    }
    public void Dispose() => http.Dispose();
}
