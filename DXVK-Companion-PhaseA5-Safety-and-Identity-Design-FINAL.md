# DXVK Companion — Phase A.5 Safety & Identity Design

**Status:** Approved design — implementation authorized against synthetic temporary directories only.

## 1. Purpose

Phase A.5 establishes the safety boundary between DXVK Companion's decision-making logic and any operation that can modify a real game installation.

It has two responsibilities:

1. A transactional file-management engine for Install, Update, Reapply, and Restore.
2. A deterministic official-DXVK identity mechanism.

Neither responsibility should be wired into automatic game management until its contracts and tests are established.

## 2. Safety Goals

The file engine must make the following guarantees:

- Companion never modifies a game file merely because the file has a familiar name.
- A file is managed only when Companion has explicitly established ownership.
- Existing files are not destroyed without first establishing a trustworthy restoration baseline.
- A multi-file DXVK operation is treated as one logical transaction.
- A partial failure must not leave an unrecorded mixture of old and new states.
- Verification occurs before an operation is considered committed.
- Restore returns every managed file to its recorded pre-Companion state.
- A file that did not exist before Companion was installed is removed on Restore.
- A file whose original state cannot be established is not silently deleted or restored.
- External changes are never silently overwritten.
- Operations are never performed while the relevant game is running.
- An interrupted operation must be detectable and recoverable after application restart.
- The safety engine fails closed: ambiguity results in no destructive action.

## 3. Ownership Model

Every target file participating in a transaction must have a `ManagedFileRecord`.

The record conceptually contains:

- installation-relative path;
- original existence state;
- original content identity;
- Companion backup reference when the original existed;
- current managed content identity;
- managed DXVK release identity when applicable;
- current observed state;
- ownership/management status.

### Ownership states

**Unmanaged**

Companion has no authority to modify or delete the file.

**Managed**

Companion has an established record for the file and is responsible for the state it created.

**AttentionRequired**

Companion cannot safely determine that it owns the current file state or cannot safely reconstruct the original state.

The engine must never promote an Unmanaged file directly to Managed merely because its filename matches a DXVK DLL.

## 4. Original Baseline

Before first modification of an existing file, the engine must establish:

- that the target exists;
- that it is accessible;
- its exact content hash;
- its size;
- its relevant metadata needed for safe restoration;
- a Companion-side backup stored outside the game directory;
- the backup's hash and size after copying.

For a target that does not exist, the baseline is explicitly:

`OriginalState = DidNotExist`.

That fact is what authorizes Restore to delete a Companion-created file later.

If the baseline cannot be established, the operation must stop before destructive modification.

## 5. Companion Backup Storage

Backups live in Companion's own storage.

They do not use `.bak` files in game directories as the V1 safety mechanism.

A backup should be addressable by a stable Companion-side identifier rather than inferred from a filename.

The game folder therefore remains free of Companion backup artifacts.

## 6. Transaction Scope

A transaction represents one logical user operation.

Examples:

- Install DXVK 2.x for a D3D11 x64 game;
- Update an existing managed D3D11 installation from version A to version B;
- Reapply the already-selected version after an authorized external change;
- Restore all files owned by the installation.

A transaction may contain multiple target files.

For D3D11, for example, current upstream DXVK documentation identifies `d3d11.dll` and `dxgi.dll` as the DLL requirements for that API. D3D10 requires `d3d10core.dll`, `d3d11.dll`, and `dxgi.dll`; D3D9 requires `d3d9.dll`; D3D8 requires `d3d8.dll` and `d3d9.dll`. The Companion V1 implementation scope initially remains D3D9/D3D10/D3D11. 

Source: DXVK README, current upstream documentation.

## 7. Transaction State Machine

```text
None
  |
  v
Planned
  |
  +---- validation/preparation failure ----> Aborted
  |
  v
Validated
  |
  v
Prepared
  |
  +---- preparation failure ----------------> Aborted
  |
  v
Applying
  |
  +---- failure/interruption ---------------> Recovering
  |
  v
Verifying
  |
  +---- success ----------------------------> Committed
  |
  +---- verification failure --------------> Recovering
                                             |
                                             +---- recovery success ----> FailedSafely
                                             |
                                             +---- recovery uncertain --> AttentionRequired
```

### Planned

The requested operation and exact target file set have been determined.

No game file has been changed.

### Validated

All pre-flight checks have passed:

- target paths are inside the intended installation;
- game is not running;
- source package/files exist;
- architecture is appropriate;
- target ownership/baseline requirements are satisfied;
- operation does not conflict with current external changes;
- required storage is available;
- no blocking safety condition exists.

### Prepared

All information needed for recovery is durable:

- transaction record exists;
- original backups have been captured where necessary;
- source package identity has been established;
- expected post-operation hashes are known.

No target file should yet be considered committed.

A failure in `Prepared` means no target-file modification should have occurred. The correct terminal state is therefore `Aborted`, not `Recovering`.

### Applying

Files are being replaced/created according to the transaction plan.

The engine records progress sufficiently to determine whether recovery is needed after interruption.

Any exception, process interruption, or unexpected condition during this phase must route to `Recovering`, because some subset of the target files may already have changed.

### Verifying

Every target is checked against its expected post-operation identity.

A transaction is not successful merely because copy operations returned without exceptions.

A verification failure routes to `Recovering`.

### Committed

All targets match the expected result.

Persistent state is updated only after verification succeeds.

### Aborted

Validation or preparation failed before any target file was modified.

The transaction is not committed, and no destructive recovery is required.

### Recovering

The engine attempts to reconstruct the state that existed immediately before the transaction.

### FailedSafely

Recovery completed and the game installation is back in its prior known state.

The transaction itself remains recorded as failed for diagnostics.

### AttentionRequired

The engine cannot prove that the prior state was successfully reconstructed.

This state must block further automatic modification until explicitly resolved.

## 8. Important Failure Rule

The engine must distinguish:

**Known rollback success**

from:

**Rollback attempted but not proven**

The second case is not a normal failure.

It is a safety condition requiring attention.

## 9. Detecting Unexpected Changes During a Transaction

Before modifying each target, the engine should compare the file with the state recorded during validation.

If an existing target changed after validation:

- abort the transaction;
- do not overwrite it;
- invalidate the transaction's prepared state;
- re-read current state;
- require the higher-level policy layer to decide whether to retry/reapply.

This prevents a race where a game installer, mod manager, user, or other process changes the same DLL between validation and replacement.

## 10. Restore Semantics

Restore is defined relative to the baseline captured before Companion's first modification.

For each managed file:

### Original existed

Restore the verified Companion backup.

### Original did not exist

Delete the Companion-managed file, but only after verifying that the file still matches the managed state or otherwise qualifies as a known Companion-owned current state.

### Original state unknown

Do not guess.

The installation remains `AttentionRequired`.

A user may later provide an explicit resolution path, but the safety engine must never infer whether the file should be deleted or restored.

## 11. External Changes

An external change means the observed file no longer matches the reference identity appropriate to the current transaction context.

The reference is transaction-specific:

### Fresh Install

Before the first Companion-managed Install, the target file's reference identity is the **original baseline hash** captured during validation.

If the target changes between validation and application, the Install must abort and re-evaluate the current state. It must not overwrite the new content.

### Update or Reapply

The reference identity is the **last committed Companion-managed content hash**.

If the target no longer matches that identity, the operation must not silently overwrite the current content.

### Restore

The reference identity is also the **last committed Companion-managed content hash** for each managed target.

Restore may additionally depend on the recorded original baseline and backup.

Examples of external changes:

- game update replaces a DLL;
- mod manager changes a DLL;
- user manually replaces a DLL;
- third-party wrapper changes a DLL;
- file is deleted.

The engine must report this state rather than silently restoring or replacing the current external content.

Higher-level policy may subsequently request:

`Reapply`

only after explicit authorization where required.

If an external change is confirmed as the new game baseline under the agreed game-update workflow, the baseline must be updated deliberately before a future destructive action relies on it.

## 12. Pending Transactions and Restart

The transaction record must survive application restart whenever an operation has passed `Prepared` or later.

On startup:

1. load the transaction record;
2. inspect each target;
3. determine whether the expected committed state, original state, or an unknown mixture is present;
4. recover or mark `AttentionRequired`;
5. never blindly repeat an incomplete transaction.

## 13. Path Safety

The engine must refuse targets that resolve outside the intended installation root.

Path handling must account for:

- absolute vs relative paths;
- `..` traversal;
- alternate separators;
- case-insensitive Windows paths;
- reparse points/junctions where relevant;
- path normalization before ownership decisions.

This is a hard safety boundary.

## 14. Official DXVK Identity

The V1 trust root is the official DXVK release archive obtained from the official DXVK GitHub repository/release system.

GitHub's release asset API exposes an asset `digest` field containing a SHA-256 digest, allowing a downloaded release asset to be checked against the digest published by GitHub before extraction.

The identity pipeline must support both modern assets with a GitHub-provided digest and older official assets where GitHub may return no digest.

```text
Official DXVK release metadata
        |
        v
Official release asset
        |
        +------------------------------+
        |                              |
        v                              v
GitHub asset digest present       digest == null
        |                              |
        v                              v
Verify downloaded bytes          Download from official release URL
        |                         over HTTPS and compute SHA-256
        |                              |
        |                              v
        |                       Store FirstDownloadHash
        |                              |
        +--------------+---------------+
                       |
                       v
              Expected archive/package structure
                       |
                       v
                 Exact DLL byte hashes
                       |
                       v
            Known release + architecture + DLL identity
```

### Verification method must remain explicit

The identity record must store which method established the archive's content identity:

- `GitHubDigest` — the downloaded bytes match the digest supplied by GitHub for that release asset.
- `FirstDownloadHash` — GitHub did not supply a digest, so Companion recorded the SHA-256 of the bytes obtained from the official release URL over HTTPS on first successful retrieval.

These methods must not be flattened into one indistinguishable `Verified = true` flag. `FirstDownloadHash` proves continuity from Companion's first successful retrieval; it does not provide the same provenance guarantee as a GitHub-published digest.

For either method, the archive must still be structurally validated and the exact extracted DLL hashes must be recorded.

If a later retrieval of an asset whose identity is already recorded produces different bytes, the operation must fail closed and surface an integrity conflict rather than silently replacing the stored identity.

Source: GitHub REST API documentation for release assets.

## 15. Three-Way DLL Classification

When Companion encounters a DLL in a game installation:

### Known official DXVK

The exact content hash matches a DLL extracted from a verified official DXVK release asset, with the expected architecture and filename.

Companion may identify its release and architecture confidently.

### Unknown / external

The filename suggests a possible DXVK-related file, but Companion cannot prove that its exact bytes correspond to a verified official release.

Companion must not claim that it is official DXVK.

### Not DXVK

The file does not match a known official DXVK DLL identity.

It remains external to Companion.

## 16. Why Windows File Version Is Not the Identity

The Windows PE file version/product version is not sufficient to identify a DXVK release.

Release identity should instead come from the verified official release asset and deterministic content identity.

A game-folder DLL must therefore be classified from known bytes, not from a convenient version-resource string.

## 17. Package and DLL Rules

The identity catalog must record, for each supported official release:

- release identifier/version;
- asset identifier/name;
- asset SHA-256 digest;
- architecture;
- exact DLL set;
- exact DLL SHA-256 for each managed DLL;
- any package-level requirements needed for extraction/validation.

The catalog should be generated or populated from verified official release metadata rather than hand-maintained guesses.

## 18. Architecture

Architecture must be treated independently from the release version.

The engine must never install a 32-bit DLL into a 64-bit target or vice versa.

Current upstream DXVK Windows documentation explicitly warns to use the DLL architecture corresponding to the application and states that Windows will not load a DLL of the wrong architecture. It also warns not to replace Windows system DLLs.

Source: DXVK Windows wiki.

## 19. API File Sets

The supported V1 deployment mapping should be explicit and centrally defined.

Initial scope:

| API | Required DXVK DLLs |
|---|---|
| D3D9 | `d3d9.dll` |
| D3D10 | `d3d10core.dll`, `d3d11.dll`, `dxgi.dll` |
| D3D11 | `d3d11.dll`, `dxgi.dll` |

D3D8 remains outside the V1 active-management scope.

## 20. No System DLL Modification

The engine must reject any target path under Windows system locations such as `System32` or `SysWOW64`.

DXVK upstream documentation explicitly warns not to replace Windows DLLs in those locations.

This is a hard safety rule, not an ordinary warning.

## 21. Windows-Specific Test Requirement

DXVK upstream states that Windows use is not officially supported by DXVK itself.

Therefore, DXVK Companion's Windows behavior must be validated by Companion's own tests and disposable Windows test installations rather than assuming upstream Linux/Wine behavior transfers directly.

This does not change the Companion product scope; it raises the testing requirement for the Windows implementation.

## 21A. Identity Metadata and Integrity Conflicts

The official-DXVK identity record must retain at least:

- release/version identifier;
- asset identifier/name;
- architecture;
- archive SHA-256;
- verification method (`GitHubDigest` or `FirstDownloadHash`);
- exact SHA-256 for each extracted managed DLL;
- source release URL;
- timestamp/record of first successful retrieval.

For an asset with no GitHub-provided digest, `FirstDownloadHash` must be treated as a weaker provenance state rather than as equivalent cryptographic provenance.

A subsequent retrieval with different bytes must never silently update the existing identity record. It must create an integrity conflict and fail closed.

## 22. Test Strategy

Phase A.5 tests must use temporary disposable directories and synthetic files.

No test may modify a real game installation.

### Transaction tests

- one-file install;
- multi-file install;
- update;
- reapply;
- restore existing file;
- restore newly created file by deleting it;
- missing source;
- inaccessible target;
- locked target;
- verification mismatch;
- failure during a later file in a multi-file transaction;
- successful rollback;
- uncertain rollback;
- application restart after interruption;
- external modification between validation and apply;
- path traversal attempt;
- target outside installation root.

### Identity tests

- verified official asset digest;
- asset with no GitHub digest uses `FirstDownloadHash` on first retrieval;
- wrong archive digest;
- corrupted archive;
- wrong architecture;
- known official DLL hash;
- same filename with unknown bytes;
- non-DXVK DLL;
- release/version mapping;
- exact DLL-set mapping;
- subsequent retrieval of a recorded asset with different bytes produces an integrity conflict rather than silently updating the stored identity.

## 23. Phase A.5 Acceptance Criteria

Phase A.5 is complete only when:

1. the transaction state machine is implemented and covered by automated tests;
2. multi-file partial failure is demonstrably recoverable or escalates to `AttentionRequired`;
3. Restore semantics are tested for both pre-existing and originally absent files;
4. external changes are detected without silent overwrite;
5. interrupted operations are detectable after restart;
6. target paths are confined to the intended installation;
7. official DXVK assets can be verified against their published digest, or via `FirstDownloadHash` when no GitHub digest is available;
8. official DLL identities can be deterministically associated with release/version/architecture;
9. wrong-architecture and unknown-DLL cases fail closed;
10. no Phase A.5 test modifies a real game installation.

## 24. Explicit Non-Goals

Phase A.5 does not yet:

- detect games;
- decide whether DXVK should be installed automatically;
- change real user game files;
- implement Automated mode;
- implement the final UI;
- support custom/nightly DXVK as official releases;
- manage V-Sync;
- modify Windows system DLLs.

## 25. Next Implementation Boundary

Only after this document is approved should implementation begin.

The first implementation should be the **transaction engine against synthetic temporary directories**, with the official-DXVK identity component developed alongside it.

The existing `DxvkInstaller` and `DxvkRollback` code should be treated as reference material for behavior, not as the safety boundary itself.

