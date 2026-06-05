using FraudApi;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
});

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

var app = builder.Build();

KnnIndex? index = null;
var indexPath = Environment.GetEnvironmentVariable("INDEX_PATH") ?? "/data/references.i16.bin";

_ = Task.Run(() =>
{
    while (!File.Exists(indexPath)) Thread.Sleep(100);
    var idx = new KnnIndex(indexPath);
    Volatile.Write(ref index, idx);
    Console.WriteLine($"[ready] index mapped: {idx.Count} reference vectors");
});

app.Run(async context =>
{
    try
    {
        var path = context.Request.Path.Value;

        if (path == "/ready")
        {
            context.Response.StatusCode = Volatile.Read(ref index) is null ? 503 : 200;
            return;
        }

        if (path == "/fraud-score")
        {
            var idx = Volatile.Read(ref index);
            if (idx is null)
            {
                context.Response.StatusCode = 503;
                return;
            }

            var req = await context.Request.ReadFromJsonAsync(AppJsonContext.Default.FraudRequest);
            if (req is null)
            {
                context.Response.StatusCode = 400;
                return;
            }

            Span<short> query = stackalloc short[Vectorizer.Pad];
            Vectorizer.Quantize(req, query);

            double score = idx.Score(query);

            // Bypass extremo da Camada de Serialização
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";

            string scoreStr = score.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string resp = score < 0.6
                ? $"{{\"approved\":true,\"fraud_score\":{scoreStr}}}"
                : $"{{\"approved\":false,\"fraud_score\":{scoreStr}}}";

            await context.Response.WriteAsync(resp);
            return;
        }

        context.Response.StatusCode = 404;
    }
    catch (Exception)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"approved\":true,\"fraud_score\":0.0}");
    }
});

app.Run();