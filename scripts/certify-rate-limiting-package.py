#!/usr/bin/env python3
"""Inspect the produced NuGet and run an isolated public-API consumer in GitHub Actions."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

package = Path(sys.argv[1]).resolve()
output = Path(sys.argv[2]).resolve()
output.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(package) as archive:
    names = set(archive.namelist())
    required = {"lib/net10.0/BKE.RateLimiting.dll", "lib/net10.0/BKE.RateLimiting.xml",
                "BKE.RateLimiting.nuspec", "README.md", "LICENSE-BKE-PROPRIETARY.txt"}
    assert required <= names, f"Missing package entries: {required - names}"
    assert not any(name.startswith("lib/") and not name.startswith("lib/net10.0/") for name in names)
    nuspec = ET.fromstring(archive.read("BKE.RateLimiting.nuspec"))
    find = lambda tag: nuspec.find(".//{*}" + tag)
    assert find("id").text == "BKE.RateLimiting"
    assert find("version").text == "0.1.0"
    assert find("license").text == "LICENSE-BKE-PROPRIETARY.txt"
    assert find("readme").text == "README.md"
    assert not nuspec.findall(".//{*}dependency"), "Core must not require another NuGet package."
    entries = sorted(names)

digest = hashlib.sha256(package.read_bytes()).hexdigest()
(output / "BKE.RateLimiting.0.1.0.sha256").write_text(f"{digest}  {package.name}\n")
print(f"NUGET_SHA256={digest}", flush=True)
print(f"NUGET_BYTES={package.stat().st_size}", flush=True)

program = r'''
using BKE.RateLimiting;
using System.Collections.Immutable;

var clock = new ConsumerClock();
using var store = new InMemoryRateLimitStore(clock, maxPartitions: 8);
IRateLimiter limiter = new BkeRateLimiter(store, clock);
var policy = RateLimitPolicy.FixedWindow("consumer-v1", 1, TimeSpan.FromMinutes(1));
var request = new RateLimitRequest("consumer-private-key", policy);
var first = await limiter.EvaluateAsync(request);
Require(first.Decision == RateLimitDecision.Allowed && first.UsageRecorded, "first permit");
Require(first.Remaining == 0, "remaining after first");
var second = await limiter.EvaluateAsync(request);
Require(second.Decision == RateLimitDecision.Throttled && !second.UsageRecorded, "second throttled");
Require(second.RetryAfter == TimeSpan.FromMinutes(1), "retry after");
Require(second.ResetAt == clock.GetUtcNow().AddMinutes(1), "reset at");
clock.Advance(TimeSpan.FromMinutes(1));
Require((await limiter.EvaluateAsync(request)).Decision == RateLimitDecision.Allowed, "rollover");
Require(!request.ToString().Contains("consumer-private-key"), "request redaction");
Require(RateLimitCapability.Id == "bke.rate-limiting" && RateLimitCapability.ContractVersion == 1, "capability");

var openPolicy = RateLimitPolicy.FixedWindow("outage-v1", 1, TimeSpan.FromSeconds(1), RateLimitFailureMode.FailOpen);
var unavailable = await new BkeRateLimiter(new OfflineStore(clock), clock).EvaluateAsync(new("key", openPolicy));
Require(unavailable.Decision == RateLimitDecision.Allowed && !unavailable.UsageRecorded &&
        unavailable.Failure == RateLimitFailure.StoreUnavailable && unavailable.Remaining is null, "explicit fail open");
foreach (var reference in typeof(BkeRateLimiter).Assembly.GetReferencedAssemblies())
    Require(reference.Name is { } name && (name.StartsWith("System.", StringComparison.Ordinal) ||
        name is "System" or "netstandard" or "mscorlib"), "BCL-only dependency: " + reference.Name);
Console.WriteLine("BLANK_CONSUMER_PASS: package-only net10.0 public API, fixed window, deterministic rollover, explicit outage.");
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class ConsumerClock : TimeProvider
{
    private long elapsed;
    private readonly DateTimeOffset origin = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => elapsed;
    public override DateTimeOffset GetUtcNow() => origin.AddTicks(elapsed);
    public void Advance(TimeSpan amount) => elapsed += amount.Ticks;
}
sealed class OfflineStore(TimeProvider clock) : IRateLimitStore
{
    public Task<RateLimitStoreResult> ExecuteAsync(RateLimitStoreRequest request,
        Func<RateLimitStoreContext, RateLimitStoreTransition> transition, CancellationToken cancellationToken = default)
        => Task.FromResult(RateLimitStoreResult.Failed(RateLimitFailure.StoreUnavailable, clock.GetUtcNow()));
}
'''
logs = []
with tempfile.TemporaryDirectory(prefix="bke-rate-consumer-", dir=os.environ.get("RUNNER_TEMP")) as temp:
    consumer = Path(temp)
    (consumer / "Consumer.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
<ItemGroup><PackageReference Include="BKE.RateLimiting" Version="0.1.0" /></ItemGroup>
</Project>''')
    (consumer / "Program.cs").write_text(program)
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="generated-package-only", value=str(package.parent))
    config_file = consumer / "NuGet.Config"
    ET.ElementTree(config).write(config_file, encoding="unicode")
    environment = dict(os.environ)
    environment["NUGET_PACKAGES"] = str(consumer / "packages")
    commands = [
        ["dotnet", "restore", "Consumer.csproj", "--configfile", str(config_file), "--no-cache"],
        ["dotnet", "build", "Consumer.csproj", "-c", "Release", "--no-restore", "-warnaserror"],
        ["dotnet", "run", "--project", "Consumer.csproj", "-c", "Release", "--no-build", "--no-restore"],
    ]
    for command in commands:
        execution = subprocess.run(command, cwd=consumer, env=environment, text=True, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, check=False)
        print(execution.stdout, flush=True)
        logs.append("$ " + " ".join(command) + "\n" + execution.stdout)
        (output / "blank-consumer.log").write_text("\n".join(logs))
        if execution.returncode:
            raise SystemExit(execution.returncode)
    assets = json.loads((consumer / "obj/project.assets.json").read_text())
    assert set(assets["libraries"]) == {"BKE.RateLimiting/0.1.0"}, assets["libraries"]
    (output / "consumer-package-libraries.json").write_text(json.dumps(assets["libraries"], indent=2))
(output / "package-certification.json").write_text(json.dumps({
    "package": package.name, "sha256": digest, "bytes": package.stat().st_size,
    "entries": entries, "framework": "net10.0", "packageDependencies": [],
    "blankConsumer": "passed", "sourceSha": os.environ.get("GITHUB_SHA")
}, indent=2) + "\n")
