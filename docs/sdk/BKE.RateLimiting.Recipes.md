# Integration recipes

These are host integration designs for `BKE.RateLimiting` 0.1.0. The SDK returns typed decisions; the host owns authentication, authorization, HTTP status codes, logging, retries, and messaging. Assume a shared `IRateLimitStore` and `new BkeRateLimiter(store)`.

Each recorded acceptance consumes its own permit. If policy A allows and policy B throttles, A remains consumed; there is no cross-call rollback. Short-circuit at the first rejection to avoid consuming later permits unnecessarily. A changed configuration conflicts while active debt exists; a new `PolicyId` deliberately creates a fresh partition. Do not use these policies as a hard account lockout.

Snippets combine real SDK calls with host pseudocode. `KeyEncoding`, `HostDecision`, `QueueDecision`, response helpers, identity variables, and cancellation tokens belong to the example host; they are not SDK types. A composite encoder must be unambiguous and bounded, such as a length-prefixed tuple, and must not normalize input differently between requests. All numerical limits below are examples for the host to replace with its approved policy.

## ASP.NET login

After trusted proxy processing and once the host has an account identifier, evaluate an IP policy and a combined IP/account policy. Do not apply an unauthenticated account-only lock: an attacker could exhaust a victim's allowance from another address and deny the victim service.

```csharp
var ip = new RateLimitRequest(KeyEncoding.Join("login-ip", opaqueIp),
    RateLimitPolicy.FixedWindow("login-ip-v1", 30, TimeSpan.FromMinutes(1)));
var account = new RateLimitRequest(KeyEncoding.Join("login-account", opaqueIp, opaqueAccount),
    RateLimitPolicy.FixedWindow("login-account-v1", 10, TimeSpan.FromMinutes(1)));
var a = await limiter.EvaluateAsync(ip, context.RequestAborted);
if (a.Decision != RateLimitDecision.Allowed) return host.RejectRateLimited(a);
var b = await limiter.EvaluateAsync(account, context.RequestAborted);
if (b.Decision != RateLimitDecision.Allowed) return host.RejectRateLimited(b);
// The host now performs credential verification; the SDK never calls Identity.
```

The host must not trust arbitrary forwarding headers or reveal whether an account exists.

## Desktop service action

Use a stable subject/action key with an unambiguous, length-bounded host encoder:

```csharp
var request = new RateLimitRequest(KeyEncoding.Join("desktop-action", subjectId, "export"),
    RateLimitPolicy.TokenBucket("desktop-export-v1", 3, 1, TimeSpan.FromMinutes(1)));
var result = await limiter.EvaluateAsync(request, cancellationToken);
if (result.Decision == RateLimitDecision.Throttled)
    return DesktopActionOutcome.Delayed(result.RetryAfter);
if (result.Decision == RateLimitDecision.Blocked)
    return DesktopActionOutcome.Failed(result.Failure);
```

Share the store across limiter instances when quota is process-wide.

## Licensing Agent endpoint

This is a .NET host integration design; it does not imply that a Python Licensing Agent directly consumes this assembly:

```csharp
var product = new RateLimitRequest(KeyEncoding.Join("license-product", opaqueProduct),
    RateLimitPolicy.FixedWindow("license-product-v1", 20, TimeSpan.FromMinutes(1)));
var action = new RateLimitRequest(KeyEncoding.Join("license-action", opaqueProduct, actionName),
    RateLimitPolicy.TokenBucket("license-action-v1", 5, 1, TimeSpan.FromMinutes(1)));
var p = await limiter.EvaluateAsync(product, cancellationToken);
if (p.Decision != RateLimitDecision.Allowed) return HostDecision.RateLimited(p);
var q = await limiter.EvaluateAsync(action, cancellationToken);
if (q.Decision != RateLimitDecision.Allowed) return HostDecision.RateLimited(q);
```

Use opaque product/device identifiers; never place license secrets or raw device identifiers in keys or observations. A failure must never become a successful entitlement decision.

## Worker command endpoint

Evaluate before dequeuing or starting expensive work:

```csharp
var request = new RateLimitRequest(KeyEncoding.Join("worker-command", opaqueWorker, commandName),
    RateLimitPolicy.SlidingWindow("worker-command-v1", 100, TimeSpan.FromMinutes(1)));
var result = await limiter.EvaluateAsync(request, cancellationToken);
if (result.Decision == RateLimitDecision.Throttled) return QueueDecision.Reschedule(result.RetryAfter);
if (result.Decision == RateLimitDecision.Blocked) return QueueDecision.StopForHostReview(result.Failure);
return QueueDecision.Start;
```

The SDK does not own queue retries, rescheduling, or dead-letter state.

## Generic API operation

Compose independent principal, network, and action checks. Earlier permits remain consumed if a later check rejects:

```csharp
var checks = new[] {
    new RateLimitRequest(KeyEncoding.Join("principal", opaquePrincipal), RateLimitPolicy.FixedWindow("api-principal-v1", 1000, TimeSpan.FromMinutes(1))),
    new RateLimitRequest(KeyEncoding.Join("network", opaqueNetwork), RateLimitPolicy.FixedWindow("api-network-v1", 300, TimeSpan.FromMinutes(1))),
    new RateLimitRequest(KeyEncoding.Join("action", opaquePrincipal, actionName), RateLimitPolicy.TokenBucket("api-action-v1", 10, 2, TimeSpan.FromSeconds(1)))
};
foreach (var check in checks)
{
    var result = await limiter.EvaluateAsync(check, cancellationToken);
    if (result.Decision != RateLimitDecision.Allowed) return HostDecision.RateLimited(result);
}
// All checks admitted; the host now performs the authorized API operation.
```

For API keys, use a host-owned opaque identifier or keyed hash, never the raw secret. Composite encoding must be unambiguous. Avoid account lockout semantics.
