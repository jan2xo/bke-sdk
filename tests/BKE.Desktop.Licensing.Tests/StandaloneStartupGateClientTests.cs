using System;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using BKE.Desktop.Licensing;
using Xunit;

namespace BKE.Desktop.Licensing.Tests;

public sealed class StandaloneStartupGateClientTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    private static StandaloneStartupGateResult Parse(string json, string expected = Id) =>
        BkeStandaloneStartupGateClient.ParseResponse(
            Encoding.UTF8.GetBytes(json), expected);

    private static string Response(bool allow, string reason = "authorized",
        string id = Id, string schema = BkeStandaloneStartupGateClient.Protocol) =>
        $$"""{"schema":"{{schema}}","request_id":"{{id}}","authorized":{{allow.ToString().ToLowerInvariant()}},"reason":"{{reason}}"}""";

    [Fact]
    public void CorrectCorrelatedAgentResponseHasOnlyTwoBusinessOutcomes()
    {
        var authorized = Parse(Response(true));
        Assert.Equal(StandaloneStartupGateStatus.Authorized, authorized.Status);
        Assert.True(authorized.Authorized);
        var denied = Parse(Response(false, "independent_credential_required"));
        Assert.Equal(StandaloneStartupGateStatus.Denied, denied.Status);
        Assert.False(denied.Authorized);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"schema":"bke.standalone-startup.v1","request_id":"0123456789abcdef0123456789abcdef","authorized":true,"reason":"authorized","license":"foo"}""")]
    [InlineData("""{"schema":"bke.standalone-startup.v1","request_id":"0123456789abcdef0123456789abcdef","authorized":true,"authorized":true,"reason":"authorized"}""")]
    [InlineData("""{"schema":"bke.standalone-startup.v1","request_id":"0123456789abcdef0123456789abcdef","authorized":"true","reason":"authorized"}""")]
    [InlineData("""{"schema":"bke.standalone-startup.v1","request_id":"0123456789abcdef0123456789abcdef","authorized":true,"reason":"authorized",}""")]
    public void MalformedExtraDuplicateOrAuthorityBearingResponsesDeny(string response)
    {
        Assert.Equal(StandaloneStartupGateStatus.InvalidResponse, Parse(response).Status);
    }

    [Fact]
    public void WrongCorrelationSchemaDecisionOrFakeSuccessDeny()
    {
        foreach (var reply in new[] {
            Response(true, id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            Response(true, schema: "bke.standalone-startup.v2"),
            Response(true, reason: "startup_replay"),
            Response(false, reason: "authorized"),
            Response(false, reason: "ADMINISTRATOR_ONLY"),
            Response(false, reason: ""),
            Response(true, reason: "authorized", id: "not-a-guid"),
        }) {
            Assert.Equal(StandaloneStartupGateStatus.InvalidResponse, Parse(reply).Status);
        }
    }

    [Fact]
    public void OversizeResponsesAndInvalidExpectedCorrelationDeny()
    {
        Assert.Equal(StandaloneStartupGateStatus.InvalidResponse,
            Parse(new string('x', 1025)).Status);
        Assert.Equal(StandaloneStartupGateStatus.InvalidResponse,
            Parse(Response(true), "invalid-id").Status);
    }

    [Fact]
    public async Task NonWindowsCannotEverAuthorizeProductStartup()
    {
        if (OperatingSystem.IsWindows()) return;
        var actual = await new BkeStandaloneStartupGateClient()
            .AuthorizeConnectedProcessAsync();
        Assert.Equal(StandaloneStartupGateStatus.UnsupportedPlatform, actual.Status);
        Assert.False(actual.Authorized);
    }

    [Fact]
    public async Task UnprivilegedFakeNamedPipeCannotGrantStartupOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        // No installed Agent or trusted SYSTEM process is used by this test.
        // Even a wire-perfect authorized response from this user-owned pipe
        // must not be trusted by the client.
        using var fake = new NamedPipeServerStream(
            BkeStandaloneStartupGateClient.PipeName,
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var lifetime = new System.Threading.CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var accepting = fake.WaitForConnectionAsync(lifetime.Token);
        var client = new BkeStandaloneStartupGateClient();
        var result = await client.AuthorizeConnectedProcessAsync(lifetime.Token);
        await accepting;
        Assert.NotEqual(StandaloneStartupGateStatus.Authorized, result.Status);
        Assert.Equal(StandaloneStartupGateStatus.UntrustedAgent, result.Status);
    }
}
