using System.Diagnostics;
using Atalaia.Shared;

namespace Atalaia.Agent;

/// <summary>
/// Mede latência, download e upload. Usa poucos recursos de propósito: 4 conexões, no máximo ~6 s e ~150 MB por sentido.
/// Alvo "server": o próprio painel (endpoints /api/agent/speedtest/*, com o token). Alvo "internet": endpoints públicos da
/// Cloudflare, FIXOS aqui no código: o servidor só escolhe entre os dois nomes, nunca manda um endereço.
/// </summary>
public sealed class SpeedTester(IHttpClientFactory httpFactory, AgentState state)
{
    private const int Connections = 4;
    private const int PingSamples = 8;                          // a 1ª só aquece a conexão (TLS) e é descartada
    private static readonly TimeSpan PhaseTime = TimeSpan.FromSeconds(6);
    private long PhaseByteCap => Math.Clamp(state.Config?.SpeedtestMaxMb ?? 150, 10, 1000) * 1_000_000L;   // por sentido: numa rede de 1 Gbps o teste acaba por aqui, não por tempo
    private const long DownloadRequest = 25_000_000;
    private const int UploadChunk = 512 * 1024;                 // pequeno o bastante para passar por proxies com limite de corpo

    private sealed record Endpoints(Uri Ping, Func<long, Uri> Down, Uri Up, string? Token);

    private static Endpoints For(string target, AgentOptions opt)
    {
        if (target == SpeedTestTargets.Internet)
        {
            var cf = new Uri("https://speed.cloudflare.com/");
            return new Endpoints(new Uri(cf, "__down?bytes=0"), n => new Uri(cf, $"__down?bytes={n}"), new Uri(cf, "__up"), null);
        }
        var root = new Uri(opt.ServerUrl.TrimEnd('/') + "/");
        return new Endpoints(new Uri(root, "api/agent/speedtest/ping"), n => new Uri(root, $"api/agent/speedtest/down?bytes={n}"), new Uri(root, "api/agent/speedtest/up"), opt.EnrollToken);
    }

    public async Task<SpeedTestResult> RunAsync(string target, AgentOptions opt, CancellationToken ct)
    {
        if (target == SpeedTestTargets.Server && string.IsNullOrWhiteSpace(opt.ServerUrl)) throw new InvalidOperationException("ServerUrl não configurado");
        var ep = For(target, opt);
        using var http = httpFactory.CreateClient("server");
        var started = Stopwatch.StartNew();

        var (latency, jitter) = await PingAsync(http, ep, ct);
        var (downBytes, downSecs) = await TransferAsync(http, ep, upload: false, ct);
        var (upBytes, upSecs) = await TransferAsync(http, ep, upload: true, ct);

        if (downBytes == 0 && upBytes == 0) throw new InvalidOperationException("Nenhum dado trafegou: sem conexão com o destino do teste.");
        return new SpeedTestResult(target, Mbps(downBytes, downSecs), Mbps(upBytes, upSecs), Math.Round(latency, 1), Math.Round(jitter, 1), downBytes, upBytes, Math.Round(started.Elapsed.TotalSeconds, 1));
    }

    private static double Mbps(long bytes, double seconds) => seconds <= 0 ? 0 : Math.Round(bytes * 8 / seconds / 1_000_000, 1);

    private static HttpRequestMessage Req(HttpMethod m, Uri uri, Endpoints ep)
    {
        var r = new HttpRequestMessage(m, uri);
        if (ep.Token is not null) r.Headers.Add("X-Enroll-Token", ep.Token);
        return r;
    }

    private static async Task<(double Median, double Jitter)> PingAsync(HttpClient http, Endpoints ep, CancellationToken ct)
    {
        var samples = new List<double>();
        for (var i = 0; i < PingSamples; i++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var res = await http.SendAsync(Req(HttpMethod.Get, ep.Ping, ep), HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                if (i > 0) samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (HttpRequestException) when (i < PingSamples - 1) { /* uma perda isolada não invalida o teste */ }
            catch (HttpRequestException ex) when (samples.Count == 0) { throw new InvalidOperationException("Sem resposta do destino do teste: " + ex.Message); }
            await Task.Delay(100, ct);
        }
        if (samples.Count == 0) throw new InvalidOperationException("Sem resposta do destino do teste.");
        samples.Sort();
        var median = samples[samples.Count / 2];
        var jitter = samples.Count < 2 ? 0 : Enumerable.Range(1, samples.Count - 1).Average(i => Math.Abs(samples[i] - samples[i - 1]));   // dispersão entre amostras ordenadas
        return (median, jitter);
    }

    /// <summary>Várias conexões em paralelo até acabar o tempo ou o limite de bytes. Devolve bytes e segundos medidos.</summary>
    private async Task<(long Bytes, double Seconds)> TransferAsync(HttpClient http, Endpoints ep, bool upload, CancellationToken ct)
    {
        long total = 0;
        var cap = PhaseByteCap;
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phase.CancelAfter(PhaseTime);
        var sw = Stopwatch.StartNew();

        async Task Worker()
        {
            var chunk = upload ? new byte[UploadChunk] : null;
            var buffer = new byte[64 * 1024];
            try
            {
                while (!phase.IsCancellationRequested && Interlocked.Read(ref total) < cap)
                {
                    if (upload)
                    {
                        using var req = Req(HttpMethod.Post, ep.Up, ep);
                        req.Content = new ByteArrayContent(chunk!);
                        req.Content.Headers.ContentType = new("application/octet-stream");
                        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, phase.Token);
                        res.EnsureSuccessStatusCode();
                        Interlocked.Add(ref total, chunk!.Length);   // só conta pedaço enviado e confirmado
                    }
                    else
                    {
                        using var req = Req(HttpMethod.Get, ep.Down(DownloadRequest), ep);
                        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, phase.Token);
                        res.EnsureSuccessStatusCode();
                        await using var body = await res.Content.ReadAsStreamAsync(phase.Token);
                        int n;
                        while ((n = await body.ReadAsync(buffer, phase.Token)) > 0)
                        {
                            Interlocked.Add(ref total, n);
                            if (Interlocked.Read(ref total) >= cap) break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* acabou o tempo desta fase: esperado */ }
            catch (HttpRequestException) { /* esta conexão falhou; as outras seguem e o total reflete o que passou */ }
        }

        await Task.WhenAll(Enumerable.Range(0, Connections).Select(_ => Worker()));
        ct.ThrowIfCancellationRequested();
        return (Interlocked.Read(ref total), sw.Elapsed.TotalSeconds);
    }
}
