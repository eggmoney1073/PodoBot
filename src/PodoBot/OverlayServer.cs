using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace PodoBot;

public sealed class OverlayServer : IAsyncDisposable
{
    public const int Port = 18766;
    public const string RouletteUrl = "http://localhost:18766/roulette";
    public const string TimerUrl = "http://localhost:18766/timer";
    public const string CallbackUrl = "http://localhost:18766/auth/callback";

    private static readonly TimeSpan RouletteReplayWindow = TimeSpan.FromSeconds(15);
    private readonly LocalDataStore _store;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();
    private readonly object _stateSync = new();
    private WebApplication? _app;
    private string? _lastRoulettePayload;
    private DateTime _lastRouletteAtUtc = DateTime.MinValue;
    private string? _timerStatePayload;
    private DateTime _timerStateExpiresUtc = DateTime.MinValue;

    public Func<string, string, Task>? AuthorizationHandler { get; set; }
    public event Action<string>? Log;

    public OverlayServer(LocalDataStore store) => _store = store;

    public async Task StartAsync()
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://localhost:{Port}");
        _app = builder.Build();

        _app.MapGet("/", () => Results.Text("PodoBot"));

        _app.MapGet("/auth/callback", async (HttpContext context) =>
        {
            var code = context.Request.Query["code"].ToString();
            var state = context.Request.Query["state"].ToString();

            try
            {
                if (AuthorizationHandler is null)
                    throw new InvalidOperationException("로그인 처리가 준비되지 않았습니다.");

                await AuthorizationHandler(code, state);
                return Results.Content("""
                    <!doctype html><meta charset="utf-8">
                    <body style="font-family:sans-serif;text-align:center;padding:70px">
                    <h1>치지직 연결 완료</h1>
                    <p>이 창을 닫고 PodoBot으로 돌아가세요.</p>
                    </body>
                    """, "text/html; charset=utf-8");
            }
            catch (Exception ex)
            {
                return Results.Content(
                    $"<!doctype html><meta charset=\"utf-8\"><pre>{System.Net.WebUtility.HtmlEncode(ex.Message)}</pre>",
                    "text/html; charset=utf-8",
                    statusCode: 500);
            }
        });

        _app.MapGet("/api/roulette", () => Results.Json(
            _store.Data.Roulettes.Where(x => x.Enabled).Select(x => new
            {
                id = x.Id,
                name = x.Name,
                trigger = x.Trigger,
                items = x.Items
                    .Where(i => i.ChancePercent > 0 && !string.IsNullOrWhiteSpace(i.Text))
                    .Select(i => new { text = i.Text, chance = i.ChancePercent, reroll = i.IsReroll })
                    .ToArray()
            }).ToArray()));

        _app.MapGet("/events", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.ContentType = "text/event-stream";

            var id = Guid.NewGuid();
            var channel = Channel.CreateUnbounded<string>();
            _clients[id] = channel;

            foreach (var replay in GetReplayPayloads())
                channel.Writer.TryWrite(replay);

            try
            {
                await foreach (var payload in channel.Reader.ReadAllAsync(context.RequestAborted))
                {
                    await context.Response.WriteAsync($"data: {payload}\n\n");
                    await context.Response.Body.FlushAsync();
                }
            }
            catch (OperationCanceledException) { }
            finally { _clients.TryRemove(id, out _); }
        });

        _app.MapGet("/roulette", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            return Results.Content(RouletteHtml, "text/html; charset=utf-8");
        });

        _app.MapGet("/timer", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            return Results.Content(TimerHtml, "text/html; charset=utf-8");
        });

        await _app.StartAsync();
        Log?.Invoke($"OBS 룰렛 준비 완료: {RouletteUrl}");
        Log?.Invoke($"OBS 타이머 준비 완료: {TimerUrl}");
    }

    public async Task PublishRouletteAsync(RouletteResult result)
    {
        var roulette = _store.Data.Roulettes.FirstOrDefault(x => x.Id == result.RouletteId);
        var items = roulette is null
            ? Array.Empty<object>()
            : roulette.Items
                .Where(x => x.ChancePercent > 0 && !string.IsNullOrWhiteSpace(x.Text))
                .Select(x => (object)new { text = x.Text, chance = x.ChancePercent, reroll = x.IsReroll })
                .ToArray();

        var json = JsonSerializer.Serialize(new
        {
            type = "roulette",
            eventId = Guid.NewGuid(),
            rouletteName = result.RouletteName,
            result = result.Text,
            index = result.Index,
            user = result.User,
            reroll = result.IsReroll,
            items
        });

        lock (_stateSync)
        {
            _lastRoulettePayload = json;
            _lastRouletteAtUtc = DateTime.UtcNow;
        }

        await BroadcastAsync(json);
    }

    public async Task PublishTimerStartAsync(string name, DateTime endsAtUtc, string finishMessage)
    {
        var endsAt = new DateTimeOffset(endsAtUtc).ToUnixTimeMilliseconds();
        var hideAt = new DateTimeOffset(endsAtUtc.AddSeconds(10)).ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(new { type = "timer_start", name, endsAt, hideAt, finishMessage });

        lock (_stateSync)
        {
            _timerStatePayload = json;
            _timerStateExpiresUtc = endsAtUtc.AddSeconds(10);
        }

        await BroadcastAsync(json);
    }

    public async Task PublishTimerCompleteAsync(string name, string finishMessage)
    {
        var hideAtUtc = DateTime.UtcNow.AddSeconds(10);
        var json = JsonSerializer.Serialize(new
        {
            type = "timer_complete",
            name,
            hideAt = new DateTimeOffset(hideAtUtc).ToUnixTimeMilliseconds(),
            finishMessage
        });

        lock (_stateSync)
        {
            _timerStatePayload = json;
            _timerStateExpiresUtc = hideAtUtc;
        }

        await BroadcastAsync(json);
    }

    public async Task PublishTimerCancelAsync()
    {
        var json = JsonSerializer.Serialize(new { type = "timer_cancel" });

        lock (_stateSync)
        {
            _timerStatePayload = null;
            _timerStateExpiresUtc = DateTime.MinValue;
        }

        await BroadcastAsync(json);
    }

    private async Task BroadcastAsync(string json)
    {
        foreach (var client in _clients.Values)
            await client.Writer.WriteAsync(json);
    }

    private List<string> GetReplayPayloads()
    {
        var result = new List<string>();

        lock (_stateSync)
        {
            var now = DateTime.UtcNow;

            if (_lastRoulettePayload is not null && now - _lastRouletteAtUtc <= RouletteReplayWindow)
                result.Add(_lastRoulettePayload);

            if (_timerStatePayload is not null && now <= _timerStateExpiresUtc)
                result.Add(_timerStatePayload);
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    private const string RouletteHtml = """
<!doctype html>
<html lang="ko"><head><meta charset="utf-8"><style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent;font-family:"Malgun Gothic",sans-serif}
#stage{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;opacity:0;transition:.2s}#stage.show{opacity:1}
#wrap{position:relative;width:min(76vw,76vh);aspect-ratio:1}#wheel{position:absolute;inset:0;border-radius:50%;border:9px solid white;box-sizing:border-box;box-shadow:0 12px 36px #0006;transition:transform 3.5s cubic-bezier(.1,.7,.05,1)}
#pointer{position:absolute;z-index:5;top:-5px;left:50%;transform:translateX(-50%);border-left:21px solid transparent;border-right:21px solid transparent;border-top:52px solid white}
#hub{position:absolute;z-index:4;left:50%;top:50%;transform:translate(-50%,-50%);width:108px;height:108px;border-radius:50%;background:#fff;display:flex;align-items:center;justify-content:center;font-weight:900;color:#6d4bd1;box-shadow:0 4px 16px #0004;text-align:center;padding:8px;box-sizing:border-box}
#result{position:absolute;left:50%;bottom:3%;transform:translateX(-50%) scale(.8);opacity:0;background:#171820ee;color:white;border-radius:18px;padding:14px 28px;font-weight:900;font-size:27px;white-space:nowrap;transition:.2s}#result.show{opacity:1;transform:translateX(-50%) scale(1)}
#previewBadge{position:absolute;top:18px;left:50%;transform:translateX(-50%);background:#171820cc;color:white;padding:8px 14px;border-radius:999px;font-size:14px;font-weight:700;display:none}body.preview #previewBadge{display:block}
</style></head><body>
<div id="previewBadge">미리보기 · OBS에서는 평소 투명</div><div id="stage"><div id="wrap"><div id="pointer"></div><div id="wheel"></div><div id="hub">PODO</div></div><div id="result"></div></div>
<script>
const stage=document.querySelector('#stage'),wheel=document.querySelector('#wheel'),result=document.querySelector('#result'),hub=document.querySelector('#hub');
const colors=['#8b6de3','#67c6c0','#f1a5bd','#f5cc74','#8eb8ed','#a7d88d','#f0a26e','#c39be8'];const preview=typeof window.obsstudio==='undefined';if(preview){document.body.classList.add('preview');stage.classList.add('show')}
let rotation=0,lastEventId='',resultTimer=null,hideTimer=null;
function clearTimers(){if(resultTimer!==null){clearTimeout(resultTimer);resultTimer=null}if(hideTimer!==null){clearTimeout(hideTimer);hideTimer=null}}
function paint(items){let a=0,p=[];for(let i=0;i<items.length;i++){const s=a;a+=+items[i].chance||0;p.push(`${colors[i%colors.length]} ${s*3.6}deg ${a*3.6}deg`)}wheel.style.background=items.length?`conic-gradient(${p.join(',')})`:'#ddd'}
function angle(items,index){let a=0;for(let i=0;i<index;i++)a+=+items[i].chance||0;return(a+(+items[index]?.chance||0)/2)*3.6}
function play(d){if(d.eventId&&d.eventId===lastEventId)return;lastEventId=d.eventId||'';clearTimers();paint(d.items||[]);hub.textContent=d.rouletteName||'PODO';stage.classList.add('show');result.classList.remove('show');const target=angle(d.items||[],d.index);rotation+=2160+(360-target)+(360-(rotation%360));wheel.style.transform=`rotate(${rotation}deg)`;resultTimer=setTimeout(()=>{result.textContent=d.reroll?`${d.user} → 한 번 더!`:`${d.user} → ${d.result}`;result.classList.add('show')},3500);hideTimer=setTimeout(()=>{result.classList.remove('show');if(!preview)stage.classList.remove('show')},6500)}
new EventSource('/events').onmessage=e=>{try{const d=JSON.parse(e.data);if(d.type==='roulette')play(d)}catch{}};
fetch('/api/roulette',{cache:'no-store'}).then(r=>r.json()).then(list=>{const first=list?.[0];if(first){paint(first.items||[]);hub.textContent=first.name||'PODO'}}).catch(()=>{});
</script></body></html>
""";

    private const string TimerHtml = """
<!doctype html>
<html lang="ko"><head><meta charset="utf-8"><style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent;font-family:"Segoe UI","Malgun Gothic",sans-serif}#stage{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;opacity:0;transition:.25s}#stage.show{opacity:1}
#card{background:#171820e8;color:white;border:2px solid #ffffff33;border-radius:26px;padding:24px 36px;min-width:360px;text-align:center;box-shadow:0 16px 50px #0007}#name{font-size:24px;font-weight:800;margin-bottom:8px}#time{font-size:64px;font-weight:900;letter-spacing:3px;font-variant-numeric:tabular-nums}#message{font-size:22px;font-weight:800;margin-top:10px;min-height:30px}
</style></head><body><div id="stage"><div id="card"><div id="name">타이머</div><div id="time">00:00:00</div><div id="message"></div></div></div>
<script>
const stage=document.querySelector('#stage'),nameEl=document.querySelector('#name'),timeEl=document.querySelector('#time'),messageEl=document.querySelector('#message');let tick=null,hide=null,endsAt=0,finishMessage='';
function format(ms){const total=Math.max(0,Math.ceil(ms/1000)),h=Math.floor(total/3600),m=Math.floor((total%3600)/60),s=total%60;return`${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}`}
function clearAll(){if(tick!==null){clearInterval(tick);tick=null}if(hide!==null){clearTimeout(hide);hide=null}}
function scheduleHide(hideAt){if(hide!==null)clearTimeout(hide);const wait=Math.max(0,(+hideAt||Date.now())-Date.now());hide=setTimeout(()=>stage.classList.remove('show'),wait)}
function start(d){clearAll();endsAt=+d.endsAt||Date.now();finishMessage=d.finishMessage||'';nameEl.textContent=d.name||'타이머';messageEl.textContent='';stage.classList.add('show');const update=()=>{const remain=endsAt-Date.now();timeEl.textContent=format(remain);if(remain<=0){if(tick!==null){clearInterval(tick);tick=null}timeEl.textContent='00:00:00';messageEl.textContent=finishMessage||'시간 종료';scheduleHide(d.hideAt||Date.now()+10000)}};update();tick=setInterval(update,250)}
function complete(d){if(tick!==null){clearInterval(tick);tick=null}if(hide!==null){clearTimeout(hide);hide=null}nameEl.textContent=d.name||'타이머';timeEl.textContent='00:00:00';messageEl.textContent=d.finishMessage||'시간 종료';stage.classList.add('show');scheduleHide(d.hideAt||Date.now()+10000)}
function cancel(){clearAll();stage.classList.remove('show')}
new EventSource('/events').onmessage=e=>{try{const d=JSON.parse(e.data);if(d.type==='timer_start')start(d);else if(d.type==='timer_complete')complete(d);else if(d.type==='timer_cancel')cancel()}catch{}};
</script></body></html>
""";
}
