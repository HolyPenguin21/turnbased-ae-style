# AI displacement, heavy deployment and Base details

**Goal:** value control of a hex even when a hero escapes; admit useful expensive hand units before discretionary movement; contain long Base descriptions.

**Architecture:** retain capture/kill odds as a separate combat query. All territorial AI combat queries use the existing sequential fight simulation without multiplying by capture odds. Immediate ActiveDefence can deny extractors, but stronghold capture stays Attack's responsibility. Reuse the existing card candidate, spendability, deployment and observation owners for one opening heavy-unit opportunity. Share the Army detail viewport helper with Base.

**Constraints:** preserve actor claims, garrison floors, current movement, all co-occupants, summons, critical defence, construction/continuation/strike reserves. No change to actual combat or initiative rules. Actual affordable AP includes initiative bonuses.

- [x] Regression tests: hero escape versus hex control, mixed defenders, hero-only scout eligibility, extractor admission, heavy deployment safety and affordability.
- [x] Reuse sequential fight evaluation for territorial admission and all related projections.
- [x] Allow immediate extractor denial and a free solo scout against a non-fighting hero contact, including provisioning checks.
- [x] Evaluate one direct 7+ AP hand unit at first unblocked strategic admission, using existing ranking and legality; rebuild demands after deployment.
- [x] Reuse Army detail scrolling for Base's second description; reset when selection changes.
- [x] Differential compile/test checks against 782a5e1d; bottom-up review; record editor-only limits.

- [x] Independent bottom-up/horizontal audit: scope the scout exception to its executor, protect fresh construction, include deferred opening in observation/result and refresh its demand inputs.
- [x] Reproduce audit findings with failing checks, then confirm corrected outcomes; update the stage-order contract for earlier observation.

Verification: 16 new checks covered across the comparative harness and the qualified-Mathf arithmetic adapter; 0 behavioural regressions. Detailed counts, native failures and outstanding Unity checks are recorded in the investigation report.

- [x] Integrate current master dd8407bc weather changes: normalise weather in the shared hex-control entrypoint; 2 weather regression cases, 35 targeted passes, 0 regressions against current master.
