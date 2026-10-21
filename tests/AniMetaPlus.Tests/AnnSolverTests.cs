using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Sources;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AniMetaPlus.Tests;

public class AnnSolverTests
{
    private static readonly DateTime _now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private const string Xml = "<ann><anime id=\"1\" name=\"X\"/></ann>";
    private const string ChallengePage = "<html><title>Security check - Anime News Network</title><script src=\"/cdn-cgi/challenge-platform/h/b\"></script></html>";

    // Shaped like FlareSolverr's answer to request.get; the page in it is Chrome's XML viewer.
    private const string SolvedAnswer = """
        {"status":"ok","message":"Challenge solved!","solution":{"status":200,"userAgent":"Mozilla/5.0 Chrome/148",
        "cookies":[{"name":"cf_clearance","value":"SECRET-CLEARANCE","domain":".animenewsnetwork.com","expiry":1822000000},
        {"name":"_ga","value":"GA1","domain":".animenewsnetwork.com","expiry":-1},
        {"name":"other","value":"nope","domain":".example.com","expiry":1822000000}],
        "response":"<html>viewer</html>"}}
        """;

    // Stands in for both ANN and FlareSolverr; the flow tests say what each answers.
    private sealed class Stub : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<(string? UserAgent, string? Cookie)> AnnCalls { get; } = [];

        public int SolverCalls { get; private set; }

        public Func<int, HttpResponseMessage> Ann { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Xml) };

        public Func<HttpResponseMessage>? Solver { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "cdn.animenewsnetwork.com")
            {
                AnnCalls.Add((request.Headers.TryGetValues("User-Agent", out var ua) ? string.Join(" ", ua) : null, request.Headers.TryGetValues("Cookie", out var cookie) ? cookie.First() : null));

                return Task.FromResult(Ann(AnnCalls.Count));
            }

            SolverCalls++;
            Requests.Add(request);

            return Task.FromResult((Solver ?? throw new InvalidOperationException("no solver"))());
        }
    }

    private sealed class Capture : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Rig
    {
        public Stub Handler { get; } = new();

        public Capture Log { get; } = new();

        public string? Stored { get; set; }

        public DateTime Now { get; set; } = _now;

        public AnnSolverSettings Settings { get; set; } = new("http://solver:8191", 90);

        public AnnSession Session { get; }

        public Rig(string? stored = null)
        {
            Stored = stored;
            Session = new AnnSession(
                new HttpClient(Handler),
                new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan },
                () => Settings,
                () => Stored,
                value => Stored = value,
                () => Now,
                "plugin-agent");
        }

        public Task<string> Fetch() => Session.Fetch("1", Log, CancellationToken.None);
    }

    private static HttpResponseMessage Challenge() => new(HttpStatusCode.Forbidden)
    {
        Headers = { { "cf-mitigated", "challenge" } },
        Content = new StringContent(ChallengePage)
    };

    private static HttpResponseMessage Solved() => new(HttpStatusCode.OK) { Content = new StringContent(SolvedAnswer) };

    [Theory]
    [InlineData(403, "challenge", null, true)]
    [InlineData(403, null, "<script src=\"/cdn-cgi/challenge-platform/x\">", true)]
    [InlineData(403, null, "<title>Security check - Anime News Network</title>", true)]
    [InlineData(403, null, "Forbidden", false)]
    [InlineData(403, "other", null, false)]
    [InlineData(200, "challenge", ChallengePage, false)]
    [InlineData(503, "challenge", ChallengePage, false)]
    public void RecognisesTheSecurityCheck(int status, string? mitigated, string? body, bool expected)
        => Assert.Equal(expected, AnnSession.IsChallenge(status, mitigated, body));

    [Theory]
    [InlineData(90, 90000)]
    [InlineData(5, 10000)]
    [InlineData(0, 10000)]
    [InlineData(9999, 600000)]
    public void SendsTheTimeoutSettingAsMaxTimeoutInMilliseconds(int seconds, int expected)
    {
        using var body = JsonDocument.Parse(AnnSession.SolverRequestBody("https://x/y?anime=1", seconds));

        Assert.Equal("request.get", body.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("https://x/y?anime=1", body.RootElement.GetProperty("url").GetString());
        Assert.Equal(expected, body.RootElement.GetProperty("maxTimeout").GetInt32());
    }

    [Fact]
    public void WaitsThirtySecondsLongerThanFlareSolverrIsGiven()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), AnnSession.SolverWait(90));
        Assert.Equal(TimeSpan.FromSeconds(40), AnnSession.SolverWait(1));
        Assert.Equal(TimeSpan.FromSeconds(630), AnnSession.SolverWait(5000));
    }

    [Theory]
    [InlineData("http://192.168.1.3:8191", "http://192.168.1.3:8191/v1")]
    [InlineData("http://192.168.1.3:8191/", "http://192.168.1.3:8191/v1")]
    [InlineData(" https://fs.example/v1 ", "https://fs.example/v1")]
    [InlineData("192.168.1.3:8191", null)]
    [InlineData("ftp://x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FindsFlareSolverrsApi(string? address, string? expected)
        => Assert.Equal(expected, AnnSession.SolverUri(address)?.ToString());

    [Fact]
    public void TakesTheCookiesAndUserAgentFromASolvedAnswer()
    {
        var (clearance, error) = AnnSession.ParseSolverAnswer(SolvedAnswer, _now);

        Assert.Null(error);
        Assert.NotNull(clearance);
        Assert.Equal("Mozilla/5.0 Chrome/148", clearance!.UserAgent);
        Assert.Equal(["cf_clearance", "_ga"], clearance.Cookies.Select(c => c.Name));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1822000000).UtcDateTime, clearance.ExpiresUtc);
        Assert.Equal(_now, clearance.SolvedUtc);
    }

    [Theory]
    [InlineData("""{"status":"error","message":"Error solving the challenge. Timeout after 60.0 seconds."}""", "Timeout after 60.0 seconds")]
    [InlineData("""{"status":"error"}""", "status error")]
    [InlineData("""{"status":"ok"}""", "no solution")]
    [InlineData("""{"status":"ok","solution":{"cookies":[{"name":"cf_clearance","value":"v"}]}}""", "no user agent")]
    [InlineData("""{"status":"ok","solution":{"userAgent":"ua","cookies":[{"name":"_ga","value":"v"}]}}""", "no cf_clearance")]
    [InlineData("""{"status":"ok","solution":{"userAgent":"ua"}}""", "no cf_clearance")]
    [InlineData("<html>502</html>", "not JSON")]
    [InlineData("[]", "not what it should be")]
    public void GivesWhatWentWrongWhenThereIsNoClearance(string json, string expected)
    {
        var (clearance, error) = AnnSession.ParseSolverAnswer(json, _now);

        Assert.Null(clearance);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void LeavesOutCookiesThatCouldBreakTheHeader()
    {
        var json = """{"status":"ok","solution":{"userAgent":"ua","cookies":[{"name":"cf_clearance","value":"ok"},{"name":"a","value":"x\r\nEvil: 1"},{"name":"b","value":"x; y=1"}]}}""";

        var (clearance, _) = AnnSession.ParseSolverAnswer(json, _now);

        Assert.Equal("cf_clearance=ok", AnnSession.CookieHeader(clearance!));
    }

    [Fact]
    public void KeepsAClearanceAsJsonAndReadsItBack()
    {
        var (clearance, _) = AnnSession.ParseSolverAnswer(SolvedAnswer, _now);

        var read = AnnSession.Deserialize(AnnSession.Serialize(clearance!));

        Assert.NotNull(read);
        Assert.Equal(clearance!.UserAgent, read!.UserAgent);
        Assert.Equal(clearance.ExpiresUtc, read.ExpiresUtc);
        Assert.Equal(clearance.SolvedUtc, read.SolvedUtc);
        Assert.Equal("cf_clearance=SECRET-CLEARANCE; _ga=GA1", AnnSession.CookieHeader(read));
        Assert.Null(AnnSession.Deserialize(null));
        Assert.Null(AnnSession.Deserialize(string.Empty));
        Assert.Null(AnnSession.Deserialize("{not json"));
        Assert.Null(AnnSession.Deserialize("""{"UserAgent":"","Cookies":[]}"""));
    }

    [Fact]
    public void ExpiresAtTheCookiesExpiry()
    {
        var clearance = new AnnClearance { ExpiresUtc = _now.AddDays(1) };

        Assert.False(AnnSession.IsExpired(clearance, _now));
        Assert.True(AnnSession.IsExpired(clearance, _now.AddDays(1)));
        Assert.False(AnnSession.IsExpired(new AnnClearance(), _now.AddYears(5)));
    }

    [Fact]
    public async Task SolvesAChallengeAndRepeatsTheRequestWithWhatItGot()
    {
        var rig = new Rig();
        rig.Handler.Ann = n => n == 1 ? Challenge() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Xml) };
        rig.Handler.Solver = Solved;

        var xml = await rig.Fetch();

        Assert.Equal(Xml, xml);
        Assert.Equal(1, rig.Handler.SolverCalls);
        Assert.Equal(("plugin-agent", null), rig.Handler.AnnCalls[0]);
        Assert.Equal(("Mozilla/5.0 Chrome/148", "cf_clearance=SECRET-CLEARANCE; _ga=GA1"), rig.Handler.AnnCalls[1]);
        Assert.Equal("http://solver:8191/v1", rig.Handler.Requests[0].RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, rig.Handler.Requests[0].Method);
        Assert.NotNull(rig.Stored);
        Assert.Contains("SECRET-CLEARANCE", rig.Stored);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Text.Contains("SECRET-CLEARANCE"));
    }

    [Fact]
    public async Task SendsTheStoredClearanceWithoutSolvingAgain()
    {
        var (clearance, _) = AnnSession.ParseSolverAnswer(SolvedAnswer, _now);
        var rig = new Rig(AnnSession.Serialize(clearance!));

        await rig.Fetch();
        await rig.Fetch();

        Assert.Equal(0, rig.Handler.SolverCalls);
        Assert.All(rig.Handler.AnnCalls, c => Assert.Equal(("Mozilla/5.0 Chrome/148", "cf_clearance=SECRET-CLEARANCE; _ga=GA1"), c));
    }

    [Fact]
    public async Task DropsAnExpiredStoredClearanceBeforeUsingIt()
    {
        var (clearance, _) = AnnSession.ParseSolverAnswer(SolvedAnswer, _now);
        var rig = new Rig(AnnSession.Serialize(clearance!)) { Now = DateTimeOffset.FromUnixTimeSeconds(1822000001).UtcDateTime };

        await rig.Fetch();

        Assert.Equal(("plugin-agent", null), rig.Handler.AnnCalls[0]);
        Assert.Equal(string.Empty, rig.Stored);
    }

    [Fact]
    public async Task DropsAClearanceThatIsChallengedAndSolvesAgain()
    {
        var (clearance, _) = AnnSession.ParseSolverAnswer(SolvedAnswer, _now);
        var rig = new Rig(AnnSession.Serialize(clearance!));
        rig.Handler.Ann = n => n == 1 ? Challenge() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Xml) };
        rig.Handler.Solver = Solved;

        await rig.Fetch();

        Assert.Equal(1, rig.Handler.SolverCalls);
        Assert.Equal(2, rig.Handler.AnnCalls.Count);
        Assert.NotNull(rig.Session.Clearance);
    }

    [Fact]
    public async Task FailsWithAWarningWhenTheCheckComesBackAfterTheSolve()
    {
        var rig = new Rig();
        rig.Handler.Ann = _ => Challenge();
        rig.Handler.Solver = Solved;

        await Assert.ThrowsAsync<HttpRequestException>(rig.Fetch);

        Assert.Equal(2, rig.Handler.AnnCalls.Count);
        Assert.Single(rig.Log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("still sends its security check"));
        Assert.Null(rig.Session.Clearance);
        Assert.Equal(string.Empty, rig.Stored);
    }

    [Fact]
    public async Task FailsAndSaysOnceWhenThereIsNoFlareSolverr()
    {
        var rig = new Rig { Settings = new AnnSolverSettings(string.Empty, 90) };
        rig.Handler.Ann = _ => Challenge();

        await Assert.ThrowsAsync<HttpRequestException>(rig.Fetch);
        await Assert.ThrowsAsync<HttpRequestException>(rig.Fetch);

        Assert.Equal(0, rig.Handler.SolverCalls);
        Assert.Single(rig.Log.Lines, l => l.Level == LogLevel.Information && l.Text.Contains("FlareSolverr address"));
    }

    [Fact]
    public async Task AsksFlareSolverrOnlyOnceInTenMinutes()
    {
        var rig = new Rig();
        rig.Handler.Ann = _ => Challenge();
        rig.Handler.Solver = () => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"status":"error","message":"nope"}""") };

        await Assert.ThrowsAsync<HttpRequestException>(rig.Fetch);
        rig.Now = _now.AddMinutes(9);
        await Assert.ThrowsAsync<HttpRequestException>(rig.Fetch);

        Assert.Equal(1, rig.Handler.SolverCalls);

        rig.Now = _now.AddMinutes(10).AddSeconds(1);
        await Assert.ThrowsAsync<HttpRequestException>(rig.Fetch);

        Assert.Equal(2, rig.Handler.SolverCalls);
        Assert.Contains(rig.Log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("nope"));
    }

    [Fact]
    public async Task FailsWithAWarningWhenFlareSolverrTimesOut()
    {
        var rig = new Rig();
        rig.Handler.Ann = _ => Challenge();
        rig.Handler.Solver = () => throw new TaskCanceledException("timed out");

        await Assert.ThrowsAsync<HttpRequestException>(rig.Fetch);

        Assert.Single(rig.Handler.AnnCalls);
        Assert.Single(rig.Log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("did not answer within 120 seconds"));
    }

    [Fact]
    public async Task FailsWithAWarningWhenFlareSolverrCannotBeReached()
    {
        var rig = new Rig();
        rig.Handler.Ann = _ => Challenge();
        rig.Handler.Solver = () => throw new HttpRequestException("Connection refused");

        await Assert.ThrowsAsync<HttpRequestException>(rig.Fetch);

        Assert.Single(rig.Log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("could not be reached") && l.Text.Contains("Connection refused"));
    }

    [Fact]
    public async Task TreatsOtherErrorsAsPlainFailures()
    {
        var rig = new Rig();
        rig.Handler.Ann = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Forbidden") };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(rig.Fetch);

        Assert.Contains("403", ex.Message);
        Assert.Equal(0, rig.Handler.SolverCalls);
        Assert.Empty(rig.Log.Lines);
    }
}
