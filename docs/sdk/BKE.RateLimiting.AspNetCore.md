# ASP.NET Core adapter design

`BKE.RateLimiting.AspNetCore` is a future host adapter, not part of the HTTP-free core. This is design-only; no HTTP implementation or runtime certification is claimed. It accepts a host key extractor, calls `IRateLimiter`, and leaves rejection behavior to the host.

Configure trusted forwarded-header processing before using `HttpContext.Connection.RemoteIpAddress`; never read arbitrary `X-Forwarded-For` directly. See Microsoft's [proxy and load-balancer guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0). Without a trusted proxy configuration, use the directly connected peer or a safer host fallback.

Run IP limiting after trusted forwarding, principal limiting after authentication, and action/resource limiting after route selection. A missing required key is a typed host decision: do not silently substitute an empty/shared key or raw header. The host decides whether to reject, challenge, or proceed without that limiter.

Key shapes are host-owned: `KeyEncoding.Join("ip", opaqueIp)`, `KeyEncoding.Join("principal", opaqueSubject)`, `KeyEncoding.Join("api-key", keyedHashOfSecret)`, `KeyEncoding.Join("resource", routeTemplate, operation)`, or an unambiguous composite. API keys must never contain the raw secret.

Map `Throttled` and `Blocked` to host-selected status and response bodies; HTTP status codes are not core policy. For a known positive `RetryAfter`, the host may round up to whole seconds with a minimum of one and emit `Retry-After`; omit it when unknown. Omit remaining/reset headers when unknown.

Pass `HttpContext.RequestAborted` to `EvaluateAsync`. Cancellation propagates and must not fail open. A fail-open `Allowed` carries `StoreUnavailable` and unknown usage fields, so it must not be presented as measured capacity. Avoid logging raw keys, API secrets, addresses, or storage exception text. The host owns safe telemetry and final handling of capacity, conflict, and indeterminate-commit outcomes.
