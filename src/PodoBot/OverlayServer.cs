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
    public const string OverlayUrl = "http://localhost:18766/roulette";
    public const string CallbackUrl = "http://localhost:18766/auth/callback";

    private static readonly TimeSpan LateJoinReplayWindow =
        TimeSpan.FromSeconds(15);

    private readonly LocalDataStore _store;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();
    private readonly object _rouletteSync = new();

    private WebApplication? _app;
    private string? _lastRoulettePayload;
    private DateTime _lastRouletteAtUtc = DateTime.MinValue;

    public Func<string, string, Task>? AuthorizationHandler { get; set; }
    public event Action<string>? Log;

    public OverlayServer(LocalDataStore store)
    {
        _store = store;
    }

    public async Task StartAsync()
    {
        if (_app is not null)
            return;

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
                    throw new InvalidOperationException(
                        "로그인 처리가 준비되지 않았습니다.");

                await AuthorizationHandler(code, state);

                return Results.Content(
                    """
                    <!doctype html><meta charset="utf-8">
                    <body style="font-family:sans-serif;text-align:center;padding:70px">
                    <h1>치지직 연결 완료</h1>
                    <p>이 창을 닫고 PodoBot으로 돌아가세요.</p>
                    </body>
                    """,
                    "text/html; charset=utf-8");
            }
            catch (Exception ex)
            {
                return Results.Content(
                    $"<!doctype html><meta charset=\"utf-8\"><pre>{System.Net.WebUtility.HtmlEncode(ex.Message)}</pre>",
                    "text/html; charset=utf-8",
                    statusCode: 500);
            }
        });

        _app.MapGet(
            "/api/roulette",
            () => Results.Json(
                _store.Data.RouletteItems
                    .Where(x =>
                        x.ChancePercent > 0
                        && !string.IsNullOrWhiteSpace(x.Text))
                    .Select(x => new
                    {
                        text = x.Text,
                        chance = x.ChancePercent
                    })
                    .ToArray()));

        _app.MapGet("/events", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.ContentType = "text/event-stream";

            var id = Guid.NewGuid();
            var channel = Channel.CreateUnbounded<string>();
            _clients[id] = channel;

            var replay = GetRecentRoulettePayload();

            if (replay is not null)
                channel.Writer.TryWrite(replay);

            try
            {
                await foreach (
                    var payload
                    in channel.Reader.ReadAllAsync(
                        context.RequestAborted))
                {
                    await context.Response.WriteAsync(
                        $"data: {payload}\n\n");

                    await context.Response.Body.FlushAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _clients.TryRemove(id, out _);
            }
        });

        _app.MapGet("/roulette", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl =
                "no-store, no-cache, must-revalidate";

            return Results.Content(
                OverlayHtml,
                "text/html; charset=utf-8");
        });

        await _app.StartAsync();

        Log?.Invoke(
            $"OBS 오버레이 준비 완료: {OverlayUrl}");
    }

    public async Task PublishRouletteAsync(
        RouletteResult result)
    {
        var items = _store.Data.RouletteItems
            .Where(x =>
                x.ChancePercent > 0
                && !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => new
            {
                text = x.Text,
                chance = x.ChancePercent
            })
            .ToArray();

        var json = JsonSerializer.Serialize(new
        {
            type = "roulette",
            eventId = Guid.NewGuid(),
            result = result.Text,
            index = result.Index,
            user = result.User,
            items
        });

        lock (_rouletteSync)
        {
            _lastRoulettePayload = json;
            _lastRouletteAtUtc = DateTime.UtcNow;
        }

        foreach (var client in _clients.Values)
            await client.Writer.WriteAsync(json);
    }

    private string? GetRecentRoulettePayload()
    {
        lock (_rouletteSync)
        {
            if (_lastRoulettePayload is null)
                return null;

            if (DateTime.UtcNow - _lastRouletteAtUtc
                > LateJoinReplayWindow)
            {
                return null;
            }

            return _lastRoulettePayload;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
            return;

        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    private const string OverlayHtml = """
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent;font-family:"Malgun Gothic",sans-serif}
#stage{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;opacity:0;transition:.2s}
#stage.show{opacity:1}
#wrap{position:relative;width:min(76vw,76vh);aspect-ratio:1}
#wheel{position:absolute;inset:0;border-radius:50%;border:9px solid white;box-sizing:border-box;box-shadow:0 12px 36px #0006;transition:transform 3.5s cubic-bezier(.1,.7,.05,1)}
#pointer{position:absolute;z-index:5;top:-5px;left:50%;transform:translateX(-50%);border-left:21px solid transparent;border-right:21px solid transparent;border-top:52px solid white}
#hub{position:absolute;z-index:4;left:50%;top:50%;transform:translate(-50%,-50%);width:90px;height:90px;border-radius:50%;background:#fff;display:flex;align-items:center;justify-content:center;font-weight:900;color:#6d4bd1;box-shadow:0 4px 16px #0004}
#result{position:absolute;left:50%;bottom:3%;transform:translateX(-50%) scale(.8);opacity:0;background:#171820ee;color:white;border-radius:18px;padding:14px 28px;font-weight:900;font-size:27px;white-space:nowrap;transition:.2s}
#result.show{opacity:1;transform:translateX(-50%) scale(1)}
#previewBadge{position:absolute;top:18px;left:50%;transform:translateX(-50%);background:#171820cc;color:white;padding:8px 14px;border-radius:999px;font-size:14px;font-weight:700;display:none}
body.preview #previewBadge{display:block}
</style>
</head>
<body>
<div id="previewBadge">미리보기 · OBS에서는 평소 투명하게 표시됩니다</div>
<div id="stage">
  <div id="wrap">
    <div id="pointer"></div>
    <div id="wheel"></div>
    <div id="hub">PODO</div>
  </div>
  <div id="result"></div>
</div>
<script>
const stage=document.querySelector('#stage');
const wheel=document.querySelector('#wheel');
const result=document.querySelector('#result');
const colors=['#8b6de3','#67c6c0','#f1a5bd','#f5cc74','#8eb8ed','#a7d88d','#f0a26e','#c39be8'];

const params=new URLSearchParams(location.search);
const forcedPreview=params.get('preview')==='1';
const isObs=typeof window.obsstudio!=='undefined';
const preview=forcedPreview||!isObs;

if(preview){
  document.body.classList.add('preview');
  stage.classList.add('show');
}

let rotation=0;
let lastEventId='';
let resultTimer=null;
let hideTimer=null;

function paint(items){
  let a=0,p=[];

  for(let i=0;i<items.length;i++){
    const s=a;
    a+=+items[i].chance||0;
    p.push(`${colors[i%colors.length]} ${s*3.6}deg ${a*3.6}deg`);
  }

  wheel.style.background=
    items.length
      ? `conic-gradient(${p.join(',')})`
      : '#ddd';
}

function angle(items,index){
  let a=0;

  for(let i=0;i<index;i++)
    a+=+items[i].chance||0;

  return (a+(+items[index]?.chance||0)/2)*3.6;
}

function play(d){
  if(d.eventId&&d.eventId===lastEventId)
    return;

  lastEventId=d.eventId||'';

  if(resultTimer!==null){
    clearTimeout(resultTimer);
    resultTimer=null;
  }

  if(hideTimer!==null){
    clearTimeout(hideTimer);
    hideTimer=null;
  }

  paint(d.items||[]);
  stage.classList.add('show');
  result.classList.remove('show');

  const target=angle(d.items||[],d.index);
  const base=2160;

  rotation+=
    base
    +(360-target)
    +(360-(rotation%360));

  wheel.style.transform=
    `rotate(${rotation}deg)`;

  resultTimer=setTimeout(()=>{
    result.textContent=
      `${d.user} → ${d.result}`;

    result.classList.add('show');
  },3500);

  hideTimer=setTimeout(()=>{
    result.classList.remove('show');

    if(!preview)
      stage.classList.remove('show');
  },6500);
}

const events=new EventSource('/events');

events.onmessage=e=>{
  try{
    const d=JSON.parse(e.data);

    if(d.type==='roulette')
      play(d);
  }catch{}
};

fetch('/api/roulette',{cache:'no-store'})
  .then(r=>r.json())
  .then(paint)
  .catch(()=>paint([]));
</script>
</body>
</html>
""";
}
