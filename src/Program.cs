using Microsoft.AspNetCore.Server.Kestrel.Core;

// ---------------------------------------------------------------------------
// Rinha de Backend 2026 - Detecção de fraude por busca vetorial
// API .NET 9 NativeAOT, hot path sem alocação, kNN brute-force SIMD exato.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateSlimBuilder(args);

// Zero logging no hot path (cada log = syscall + alocação).
builder.Logging.ClearProviders();

builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;                       // header inútil, menos bytes
    o.Limits.MaxRequestBodySize = 64 * 1024;         // payloads são < 1 KB
    o.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http1); // sem custo HTTP/2
});

var app = builder.Build();

// Store vetorial 100% em memória. Carregado em background; /ready só fica 2xx
// quando o dataset terminou de carregar.
var dir = Environment.GetEnvironmentVariable("RESOURCES_DIR") ?? "/app";
var store = new FraudStore();
_ = Task.Run(() =>
{
    try { store.Load(Path.Combine(dir, "references.json")); }
    catch (Exception ex) { Console.Error.WriteLine("LOAD FAILED: " + ex); }
});

var fraudPath = new PathString("/fraud-score");
var readyPath = new PathString("/ready");

// Dispatch manual por path: mais barato que o roteamento por endpoints.
app.Run(async ctx =>
{
    var path = ctx.Request.Path;
    if (path.Equals(fraudPath, StringComparison.Ordinal))
    {
        await Handlers.Fraud(ctx, store);
    }
    else if (path.Equals(readyPath, StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = store.Ready
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;
    }
    else
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }
});

await app.RunAsync();
