# ASP.NET Core adapter design

`BKE.RateLimiting.AspNetCore` is a future host adapter, not part of the HTTP-free core. This is a design only; no HTTP implementation or runtime certification is claimed. It should accept a consumer-selected key extractor, call `IRateLimiter`, and let the host select rejection behavior. The adapter must not embed identity or licensing rules.

Key extractors can produce an IP key, authenticated principal key, API-key key, route/resource key, or a composite key. Extractors should return a typed missing-key result when the required identity is absent. `Retry-After` can be rendered from `RateLimitResult.RetryAfter`; `ResetAt` and remaining headers are optional and must be omitted when unknown.

Client IP resolution is host infrastructure. The adapter must never trust arbitrary `X-Forwarded-For`. A host may resolve the address only after configuring a trusted proxy list and known forwarding behavior. If no trusted proxy is configured, use the directly connected peer address or a host-defined safer fallback.

The middleware/filter should run after authentication when principal keys are needed, and before the protected operation. It should map `Throttled` and `Blocked` to the host's chosen status and body, preserve cancellation, and avoid logging raw keys or storage exception text. A storage `Allowed` in fail-open mode carries a typed failure and unknown count fields; it must not be rendered as a fully measured success.
