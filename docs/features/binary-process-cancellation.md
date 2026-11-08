# Binary process cancellation ownership

`OwnedBinaryJob.RequestStop()` now marks stop requested immediately and schedules exactly one tracked cancellation task. Parent cancellation uses a bounded registration that only requests this task; it never invokes sink registrations synchronously. Timeout, output limit and sink failure use the same path.

`StopAllAsync` starts its deadline at entry. A blocked callback returns pending; retries retain ownership without creating duplicate callbacks. Cleanup requires direct child exit, both pipe/sink workers, and the cancellation task to finish before disposing the cancellation source or reporting `CleanupComplete`. Callback completion schedules a final release check after the execution task completes, including cases where both pipes finished earlier. Only the directly owned producer is stopped.

The stream remains caller-owned. Consumers must release their blocking callbacks; the runner cannot force arbitrary managed callback code to end. The internal producer token is not a public transport or user-device capability.

Offline evidence: `BinaryCancellationTests` uses real owned fake binary children, a separately owned foreign child, and explicit barriers for callback and sink release. It covers stop deadlines, parent cancellation, pipe exit with a blocked callback, repeated stop, late acknowledgment and pre-cancelled startup. No device commands run.
