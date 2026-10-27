# Windows D3D10 managed payload — proposal #259

**Status: NON-NORMATIVE; owner decision pending.** This document records a recommendation and a future implementation contract. Merging it does not adopt its policy, change either specification, change production behavior, or authorize host execution. Base inspected: `20ff411ca560be448712cc3a249351cbc9ba7438`.

## Recommendation and evidence boundary

Recommend a **three-file managed payload** for fresh deployments and explicitly requested updates/reapplies in the qualified **official v3.1.1, Windows x64, DX10** case: `d3d10core.dll`, `d3d11.dll`, `dxgi.dll`, all from the same validated selected release and architecture. This is a manager policy for keeping the supplied DXVK components together, **not a claim that Windows always requires the core DLL**. Do not activate it until the owner adopts the decision and a coherent implementation/test task is approved.

Three distinct facts support that recommendation:

| Evidence | Established fact | Limit |
|---|---|---|
| [Pinned upstream README](https://github.com/doitsujin/dxvk/blob/b1a1c99ab52b687cf950d62c88bc2fa316b41663/README.md#dll-dependencies) | The D3D10 dependency inventory lists the three files | Wine-oriented installation guidance is not a universal Windows loader contract |
| [Matrix-v4](https://github.com/Tiflit/DXVK-Companion/issues/252#issuecomment-6353765990) | Both two- and three-file configured fixtures created D3D10 and D3D10.1 devices; the candidate D3D10 process reported the staged DXVK core | One host, x64/v3.1.1, controlled settings; no rendering/default-game acceptance |
| Inspected Companion code | Acquisition retains the core; production deploys two files; existing-DXVK assessment omits the core | Source inspection, not an executed apply/adopt/restore regression |

The [verification record](D3D10-DEPLOYMENT-VERIFICATION.md#completed-matrix-v4--2026-10-22) preserves exact source/executable/payload identities, all nine outcomes and historical stops. Native controls also succeeded. The two-file D3D10 case reported the **system** core; the three-file case reported the **fixture** core. D3D10.1 reported no core in either fixture. Per-call DXVK D3D11 logs recorded internal initialization at feature levels 10.0/10.1. Module presence and logs do not prove every internal call owner or useful rendering.

## Product options and tradeoff

| Option | Benefit | Cost / unresolved boundary |
|---|---|---|
| Keep two files as the managed default | Least new ownership; preserves current behavior and passed this Windows comparison | Relies on the observed system-core path for D3D10; externally supplied DXVK cores still need recognition, conflict and update rules |
| **Manage three files for the qualified case — recommended** | Includes the supplied DXVK core, whose loading was observed; one selected-version payload can be updated and restored coherently | One more file must pass eligibility, baseline, transaction, ownership and external-change guards; safe legacy transitions need implementation |

The recommendation chooses a controlled managed payload, not a proven performance or compatibility improvement. Evidence does not justify a `>=3.1.1` rule, every official release, x32, default settings or every game. Outside the qualified case, this proposal grants no new support or policy change. Existing DX9/DX11 behavior and unassigned release/architecture behavior must remain unchanged by the first bounded implementation; their current behavior must not be relabeled as newly certified. Further expansion needs its own source/evidence decision.

No per-game exception database is needed merely to select this managed payload. A future game-specific loader exception needs evidence and a separate compatibility decision; it must not silently override source/ownership guards.

## Keep three sets distinct

1. **Desired deployment set:** the files selected by an adopted API/release/architecture policy for an explicit mutation.
2. **Observed assessment set:** files actually found and identified, including a core in an external two-/three-file or mixed installation. Detection is evidence, not ownership.
3. **Persisted ownership/restore set:** exact root-relative files recorded by completed operations. A changed default must never manufacture historical ownership.

Do not make all three sets equal by changing one DLL array. A valid legacy two-file assessment is not proof a core exists; a three-file policy is not permission to delete an unrecorded core during restore.

## Proposed coherent implementation boundary

The rules below are proposed acceptance criteria, not claims about current code or executed tests.

### Selected release and eligibility

- Share the desired-payload resolution across version availability/listing, install and reapply. It must consume the actual selected release identity and demonstrated architecture, not API alone, a directory name alone, or a source chosen independently by each caller.
- Apply the proposed three-file rule only to the qualified v3.1.1/x64/DX10 selection. Reject a requested tag/source disagreement, unknown architecture, missing/corrupt/wrong-architecture member or mixed selected payload before game/backup/ownership writes. Do not fall back to a different cached version or silently drop the core.
- Preserve the existing source-root/path, PE, exclusion, process-activity, ownership-health and persistence safeguards. Recheck selected bytes at the mutation boundary using the existing guarded source/transaction mechanisms. A locally matching hash is not an upstream signature; no stronger authentication guarantee is adopted here.
- Do not reinterpret an unversioned legacy directory as v3.1.1. A proposed three-file mutation that cannot establish its selected context must refuse with an actionable explanation. Unassigned legacy/other-release policies need explicit disposition in the implementation contract; they cannot be silently converted or disabled through an API-only mapping edit.

### Apply, reapply and a two-to-three transition

- A fresh qualified operation plans and journals all three files together, with the same failure/undo/persistence boundary as the existing managed payload. No partial deployment may be presented as success.
- Existing files require positive pre-operation identity and the normal verified backup/baseline rules. An absent core records absence only after establishing absence. Unknown or externally changed state is not guessed away because the selected source is official.
- An old two-file ownership record stays two-file until an explicit successful apply/update/reapply plans the additional core. Capture that core's actual pre-operation state at this transition; preserve the original baselines of the already managed wrappers.
- If a core is already owned, never update its associated wrappers to a different managed release while silently leaving a mismatched core. Resolve a coherent supported transition or refuse it. Shared directories and sibling profiles must preserve installation-wide compatibility and ownership rules.
- Failure must recover the exact immediate pre-operation bytes/file set and metadata, rather than replacing them with an older native baseline. A failed transition must not leave a new core record or erase useful recovery evidence.

### Recognition and adoption

- Assess the exact core basename with the existing file-identity/metadata categories, including core-only, matching official set, native/third-party, unknown/custom, modified and conflicting-version cases. A matching name or PE product string alone cannot establish an official release.
- A simple global scan-list addition is insufficient: pin DX9/DX11/shared-directory behavior so an unrelated core does not become owned, deleted, or a new required payload for those APIs.
- Known matching two-file layouts may retain the existing explicit adoption path as legacy layouts: adopt only the positively identified files actually present, under the existing adoption/restoration semantics. Do not mint ownership for an absent core or call the result a completed three-file deployment. A later explicit reapply can request the guarded transition above.
- A positively identified three-file layout can be adopted coherently only with matching release/architecture evidence for all adopted members. Unknown, modified, mixed-version or changed-after-assessment files must not be silently adopted or overwritten. Revalidate immediately before committing ownership.
- Adoption cannot reconstruct lost original files. Preserve the established adoption baseline semantics and explain their limits; a hash match is not proof of what existed before external DXVK installation. Any change to those semantics needs a separate explicit owner decision, not a side effect of adding core detection.

### Restart, restore and recovery

- Reconstructed services must consume durable root-relative ownership records. Restore an old two-file installation according to those records and leave an unrecorded core untouched. Do not derive restore scope from today's desired payload table or current source availability.
- A newly managed core with a verified pre-existing baseline restores that baseline; a core whose absence was recorded is removed only while the expected managed identity still matches. Unknown baseline/ownership, changed target or changed/missing required backup must follow existing conservative refusal/recovery behavior.
- A three-file operation's persistence failure, committed/uncertain save outcome and later recovery must preserve the existing durable ownership/AttentionRequired guarantees. No success-only in-memory record or schema bump is assumed necessary or authorized by this proposal.
- Preserve independent configuration, unrelated files/backups, sibling installation paths and historical failure records. Source availability is not permission to invent restore data.

## Inspected integration points

| Code | Why the implementation must account for it |
|---|---|
| `DxvkCompatibility.GetRequiredDlls` | Currently API-only; DX10 returns two files |
| `DxvkLocalReleaseLibrary.IsVersionAvailable/GetAvailableVersions` | Uses that API-only list for selected-source eligibility/listing |
| `DxvkInstaller` install/reapply paths | Each consumes the list before creating file transactions |
| `ExistingDxvkDetector.RelevantDlls` and identity matching | Core is currently absent from assessment; recognition must remain distinct from desired deployment |
| `DxvkInstaller.AdoptExisting` | Iterates assessment files to persist ownership; cannot invent a missing member or its history |
| Existing transaction, ownership store and restore paths | Must preserve old records and native/absent baselines across restart and failure |

These are integration findings, not new failure reproductions. Implementation may use a shared resolver or equivalent internal representation; this document does not freeze a public API, schema, dependency, UI or final allowed-path list. The future assigned task must cover the actual affected callers/tests together.

## Required regression plan — NOT EXECUTED here

| Case group | Required observable result |
|---|---|
| Qualified fresh apply / reapply | Exactly three same-release/x64 targets; durable records; exact native-existing and absent-core restore after service reconstruction |
| Missing/corrupt/wrong-architecture core or tag/source disagreement | Availability/listing and mutation agree; refusal before game/backup/ownership changes; no alternate-source fallback |
| Legacy two-file record and unversioned cache | Restart/restore honors recorded scope; unrecorded core preserved; unknown selected context is not guessed |
| Explicit two-to-three transition | Existing wrapper baselines preserved; core baseline captured once; failed operation restores immediate prior bytes/file set and metadata |
| Core-aware assessment / adoption | Known two-/three-file, core-only, native, unknown/custom, conflicting, modified and assessment-to-commit changes handled conservatively; absent file never adopted |
| External target/backup changes and persistence failures | No destructive success claim; existing identity refusal, durable unresolved health and recovery evidence retained |
| DX9/DX11, other contexts and shared/nested paths | No accidental core requirement/ownership or unsupported API expansion; sibling/unrelated files and configuration preserved |

Use synthetic versioned release/game/store fixtures and the real installer/library/detector/store/transaction boundaries where possible. Changing the DX10 assertion from two filenames to three is necessary only after adoption and is not sufficient evidence. Inspect/adapt the existing compatibility, local release, persisted lifecycle, ownership persistence, restore-target/backup and shared-directory tests; retain old two-file regression coverage. Existing hosted CI can execute approved synthetic tests. Any real package/device/game test requires a separate finite profile and current readiness.

## Exact owner decision and next handoff

1. Review/merge this **evidence and proposal** without implying adoption.
2. Explicitly adopt, revise or reject the recommended three-file managed payload for the qualified v3.1.1/x64/DX10 case, with the legacy ownership, recognition/adoption and restore boundaries above. A plain merge of this proposal is not that decision.
3. Codex prepares a bounded implementation Issue covering normative section13/safety-supplement wording and the coherent affected production/test paths. Normative wording must distinguish a managed-policy choice from universal Windows loader necessity and keep unqualified cases explicit. Human approval precedes those behavior/specification changes; human alone merges.

No source/specification/ownership/restore change, local build/test/app/device/helper run, configuration write, acquisition, cleanup or external advisory transmission is activated here. All prior local profiles remain completed/stopped.
