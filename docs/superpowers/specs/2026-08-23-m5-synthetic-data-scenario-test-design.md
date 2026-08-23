# M5: Synthetic Data Generator + Scenario Test — Design

**חלק מ:** R&D Logs Platform — Milestone M5, the project's stated go/no-go acceptance milestone
**תאריך:** 2026-08-23
**מבוסס על:** [11-Test-Strategy.md](../../../מסמכי-אפיון/11-Test-Strategy.md) §3-5 (Scenario Test, False-Positive Test, Baseline Learning test), [06-מודל-אפליקציה.md](../../../מסמכי-אפיון/06-מודל-אפליקציה.md) §4 (RetailPulse/FieldOps hierarchies + the 6-scenario table), [12-תוכנית-עבודה-ואבני-דרך.md](../../../מסמכי-אפיון/12-תוכנית-עבודה-ואבני-דרך.md) §3 ("M5 is the real go/no-go — if it fails the first time, the fix is to recalibrate `08`'s thresholds, never to weaken the generator so the test passes")
**Follows:** M0–M4b, all merged to `main`, live-verified.

## 1. Goal

Everything built in M0–M4 is unproven at the level that matters: does the Analysis Engine actually detect the anomalies it was designed to detect, and does it stay quiet when nothing is wrong? M5 builds the one test that answers that — a `SyntheticDataGenerator` that produces 40 days of realistic history for RetailPulse and FieldOps, injects the 6 mandated anomaly scenarios on top of 35 quiet days, and an automated Scenario Test that asserts on the resulting Findings with the exact acceptance criteria the Test Strategy doc specifies.

## 2. Scope decisions locked before this design started

- **Test shape:** a new `tests/SyntheticDataGenerator` class library (pure generation logic — quiet-day traffic curves + 6 scenario injectors) driven by new xUnit tests in `LogsPlatform.Tests`, not a standalone manually-run console tool. `dotnet test` must be able to run this as an automated gate.
- **Detection trigger:** tests resolve `AnalysisEngineTickRunner` directly from the test host's DI container and call `RunOneTickAsync()` explicitly — the same pattern M4a's own unit tests already use — rather than waiting on the real `AnalysisEngineBackgroundService`'s 5-minute `PeriodicTimer`. This keeps the test deterministic and fast; the timer/scope-per-tick wiring itself is already covered by M4a's `AnalysisEngineBackgroundServiceTests`.
- **False-positive seed count:** 3 seeds (not the spec's suggested 5) — a meaningful reduction in confidence-building runs while keeping the test's runtime reasonable for a solo dev's iteration loop. The 6-scenario acceptance test itself runs once, with its own fixed seed, since it doesn't need multi-seed repetition (its assertions are exact-value checks, not statistical ones).
- **Customer-Specific Anomaly's Operation:** `ConfirmOrder`, not the spec table's informal "RetailPulse" (no operation named). `CreateOrder` — the obvious reading — is actually a *Process* in `06`'s hierarchy, not a leaf Operation, and Findings can only scope to an Operation or ExceptionGroup. `ConfirmOrder` is the one Operation under `CreateOrder` not already claimed by another scenario (`ChargePayment`→Error Spike, `ReserveStock`→New Exception), keeping this scenario's data isolated from theirs.
- **New Exception scenario also validates `DownstreamFailureCorrelator`:** the Test Strategy doc's acceptance criteria (§3) separately require a Downstream-Failure Hypothesis check, which only `NewExceptionDetector`'s wiring (per M4b) can produce (it's the detector M4b gave real per-event trigger context to, unlike `RateAnomalyDetector`'s narrower ErrorSpike-only wiring). So the New Exception scenario's injected data includes a `CorrelationId`-linked downstream error event on a different Operation, not just the new-exception event itself.

## 3. Architecture

```
tests/SyntheticDataGenerator/           (new class library)
├── QuietDayGenerator.cs                — per-Operation hour-of-day traffic curves + seeded noise
├── ScenarioInjectors/
│   ├── ErrorSpikeInjector.cs
│   ├── PerformanceDegradationInjector.cs
│   ├── NewExceptionInjector.cs
│   ├── DeploymentAnomalyInjector.cs
│   ├── MissingActivityInjector.cs
│   └── CustomerAnomalyInjector.cs
├── SimulatedEvent.cs                   — a plain record the generator emits; converted to IngestEventRequest by the test layer
└── DomainFixture.cs                    — builds RetailPulse/FieldOps through the real Admin API (Applications→Environments→Modules→ScreenServices→Processes→Operations→Customers), matching 06 exactly

tests/LogsPlatform.Tests/Scenario/      (new test folder)
├── FalsePositiveTests.cs               — 3 seeds, quiet-only, asserts 0 Findings
└── ScenarioAcceptanceTests.cs          — quiet + 6 scenarios, asserts exactly 6 correct Findings
```

`SyntheticDataGenerator` has no dependency on `LogsPlatform.Web`/`Infrastructure` — it only knows `LogsPlatform.Domain` enums (`FindingType` etc., for the test assertions) and produces plain data. The test layer owns all HTTP/DI wiring, matching this project's established boundary (generation logic stays testable/reusable in isolation; only the test project touches `TestWebApplicationFactory`).

## 4. Data flow

1. `DomainFixture` creates both applications, their environments, full hierarchies, and (for RetailPulse) a handful of `Customer` rows — all through the real Admin API (`POST /api/v1/admin/applications/...`), never direct DB writes, so a schema/API drift would break this test the same way it'd break a real integration.
2. `QuietDayGenerator` produces `SimulatedEvent`s for both apps across 35 simulated days, ending "now minus 5 days" (so the 6-scenario injection window — days 36-40 — lands on the most recent 5 simulated days, ending at "now"). Traffic follows a seeded RNG: Poisson-ish per-hour counts around each Operation's configured curve, not a fixed formula, so `BaselineCalculator`'s stddev calculation sees real variance.
3. The 6 scenario injectors each produce `SimulatedEvent`s (and, for the Deployment scenario, a real `Deployment` row via the Admin API) targeting their exact Operation/ExceptionType/Customer and time window within days 36-40.
4. The test layer converts every `SimulatedEvent` into `IngestEventRequest` and POSTs them in batches (the ingestion endpoint already accepts arrays) through `TestWebApplicationFactory`'s real hosted app — never a direct `DbContext` write, per the Test Strategy doc's explicit requirement that this test exercise the full chain.
5. The test resolves `AnalysisEngineTickRunner` from the host's DI container (via `factory.Services.CreateScope()`) and calls `RunOneTickAsync()` once per `(Application, Environment)` pair — mirroring exactly what the real `AnalysisEngineBackgroundService` would eventually do on its own timer.
6. Assertions read back through `IFindingRepository`/`GetByIdAsync`, matching the acceptance criteria in `11`'s §3.

## 5. Error handling / edge cases

- **Timing:** all "day" offsets are computed relative to the test's own `DateTime.UtcNow` at generation time, not hardcoded calendar dates — so the test is stable across whenever it's actually run (the same fix applied ad hoc during M4b's live verification, now built into the generator itself).
- **Test isolation:** each test class gets its own `TestWebApplicationFactory` instance (already `EnsureDeleted`+`Migrate`s per M2's established convention), so quiet-only runs and the full-scenario run never share data.
- **Volume:** ~35 days × (RetailPulse: 3 always-on Operations + FieldOps: 2) at the given hourly curves is on the order of tens of thousands of events per app — batched ingestion (500-1000 events per HTTP call) keeps this practical; the Test Strategy doc explicitly deprioritizes true load-scale volume for V1.
- **Confidence check:** since 35 quiet days ≫ `MIN_SAMPLES`=14, every Baseline should have `SampleCount` in the 20s-30s range (subject to zero-count-day exclusion, per `BaselineCalculator`'s M4a behavior) — the acceptance test asserts `ConfidenceLevel.High` on all 6 Findings per `11`'s stated criterion, which only holds if `SampleCount>=14` for each; if a scenario's Operation ends up with too few active quiet-days (e.g. `MissingActivity`'s own target Operation, which by definition has *low* volume even on quiet days), the generator must guarantee at least ~20 active days for every scenario's Operation specifically, not just "some" volume.

## 6. Testing approach

This *is* the test suite — there's no meta-test layer above it. Two test classes as described in §3. `FalsePositiveTests` is the false-positive gate: 3 independent seeds, each a full fresh quiet-only run, 0 Findings expected in all 3. `ScenarioAcceptanceTests` is the real go/no-go: one fixed-seed run with all 6 scenarios injected, asserting exactly 6 total Findings (not ≥6 — extras would mean a threshold is too sensitive even on real anomalies) with the specific Type/Scope/Fact-value/Confidence/Evidence checks `11`'s §3 lists. Per `12`'s explicit instruction: if this fails on the first real run, the correct response is recalibrating `08`'s detection thresholds (already centralized as named constants per detector, from M4a) — not loosening the generator's scenarios until the test passes.

## 7. Out of scope for M5

Baseline-learning-in-isolation (`11`'s §5, a focused unit test with hand-picked known-mean/stddev samples) is a smaller, separate unit test — worth adding but not gating M5's go/no-go the way the Scenario/False-Positive tests do; can be folded into this milestone's plan as an additional task if time allows, or deferred as a quick follow-up. Security test matrix (`11`'s §6) is M6's scope, not M5's, per the milestone plan.
