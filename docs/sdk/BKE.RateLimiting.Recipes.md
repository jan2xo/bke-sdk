# Integration recipes

These examples are design recipes. They intentionally leave HTTP status codes, logging, and application authorization to the host.

## ASP.NET login

After trusted proxy resolution and before credential verification, evaluate a composite `login:ip+account` policy. If the account is not yet authenticated, derive an account identifier from the normalized application identity only if the application already owns that normalization. Apply a second per-IP policy. Do not use untrusted forwarding headers directly.

## Desktop service action

Use a stable product-owned subject key plus action name, for example `desktop-action:subject:export`. Share one store across limiter instances in the process. Return a typed throttled result to the UI/service boundary; the SDK does not display a message or persist an audit event.

## Licensing Agent endpoint

Compose independent policies for an authorized product identity, action (`activate`, `renew`, or `deactivate`), and any host-controlled network dimension. Keep license identifiers and raw device identifiers out of optional observations. A policy failure must not be converted into a successful entitlement decision.

## Worker command endpoint

Use a worker identity key and a command/resource key. Evaluate before dequeuing or starting expensive work. If the result is throttled, the worker may reschedule according to its queue policy; the SDK does not own retries or queue state.

## Generic API operation

Evaluate per-principal, per-IP, and per-action policies as separate calls when the business rule requires all three. A caller can reject if any result is throttled or blocked. Keep `PolicyId` stable and versioned so a configuration change creates an explicit policy conflict rather than silently resetting state.
