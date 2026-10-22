# Companion component-management research

Reviewed 2026-10-19. Evidence is bound to the revisions below. This is documentation/licence triage, not a security audit, performance result, exhaustive transitive audit or incorporation approval. Public descriptions were evaluated as claims, not demonstrated capabilities. No component was installed or executed in this review.

A permissive source licence does not clear all dependencies, weights, proprietary DLLs or release archives. Copyleft is open source; specific copying/linking/adaptation/distribution still needs a compatible plan. Noncommercial/no-derivatives restrictions are not ordinary open-source permissions. Missing grants block implementation reuse under the present evidence. No implementation, binary, model or game content is incorporated by these notes.

## What is relevant to DXVK-Companion

DXVK-Companion owns component acquisition, compatibility decisions, reversible installation and diagnostics. [Neural-Companion](https://github.com/Tiflit/Neural-Companion) owns rendering inputs, enhancement integration and its own standalone experience. A future optional integration must preserve independent usability.

The source review supports the following design investigations, not implementation assignments or new feature guarantees:

- Record exact tested component combinations, acquisition origin and integrity hashes. Distinguish matching bytes from authenticated official provenance.
- Keep one explicit owner for each installed file and configuration entry. Removal and rollback must preserve other wrappers' files and existing backups.
- Keep requested settings distinct from effective settings, including reasons a provider or input is unavailable.
- Avoid silently replacing a tested pinned component through an external component's self-updater.
- Report the actual rendering and processing adapters, API, bitness and provider inputs. Generic “vendor supported” claims are insufficient.
- Prefer a read-only inventory/compatibility record before adding deployment of enhancement packages.

Existing safety/anti-cheat/restoration rules remain controlling. These notes do not expand official-DXVK acquisition work into third-party enhancement installation, select dependencies, change persisted formats or authorize device runs.

## Focused reference set

| Source and inspected revision | Licence evidence | Intended use and limits |
|---|---|---|
| [doitsujin/dxvk](https://github.com/doitsujin/dxvk/tree/2cb40186c43d617b5bd349a3756721aa4bd24b6d) · `2cb40186c43d617b5bd349a3756721aa4bd24b6d` | zlib/libpng. [LICENSE](https://github.com/doitsujin/dxvk/blob/2cb40186c43d617b5bd349a3756721aa4bd24b6d/LICENSE) | **Permissive candidate.** DXVK primary runtime; NC D3D-to-Vulkan route. Retain origin/notice and mark source changes; translation supplies no universal motion/camera semantics. |
| [al4xdev/dlss5-enabler](https://github.com/al4xdev/dlss5-enabler/tree/9eaf9a1fa71950de2f3ae7a9d3cd7e43a8a4808e) · `9eaf9a1fa71950de2f3ae7a9d3cd7e43a8a4808e` | MIT own code. [LICENSE](https://github.com/al4xdev/dlss5-enabler/blob/9eaf9a1fa71950de2f3ae7a9d3cd7e43a8a4808e/LICENSE) | **Permissive candidate.** DXVK transactional deployment concept reference. Installer does not clear downloaded components or validate every runtime combination. |
| [xt0n1-t3ch/DLSSync](https://github.com/xt0n1-t3ch/DLSSync/tree/903db98d89db789df651f6faef8b889d2af8b92b) · `903db98d89db789df651f6faef8b889d2af8b92b` | Apache-2.0; NOTICE. [LICENSE](https://github.com/xt0n1-t3ch/DLSSync/blob/903db98d89db789df651f6faef8b889d2af8b92b/LICENSE), [NOTICE](https://github.com/xt0n1-t3ch/DLSSync/blob/903db98d89db789df651f6faef8b889d2af8b92b/NOTICE) | **Permissive candidate.** DXVK signed-runtime inventory/rollback/privacy UX reference. Preserve notices; vendor signatures do not clear all distribution rights or prove operational safety. |
| [Optiscaler-Client/Optiscaler-Client](https://github.com/Optiscaler-Client/Optiscaler-Client/tree/f24058f6d0ad7dc9cf88d5fec426a8f8c98fc20f) · `f24058f6d0ad7dc9cf88d5fec426a8f8c98fc20f` | GPL-3.0-or-later stated in README; GPL text. [LICENSE](https://github.com/Optiscaler-Client/Optiscaler-Client/blob/f24058f6d0ad7dc9cf88d5fec426a8f8c98fc20f/LICENSE) | **Copyleft review.** DXVK deployment/discovery/settings reference. Unofficial client; downloaded components separate; public UI claims are not safety verification. |
| [optiscaler/OptiScaler](https://github.com/optiscaler/OptiScaler/tree/c2e44a8bc911010d7ed3e4fa63843185fbb55d03) · `c2e44a8bc911010d7ed3e4fa63843185fbb55d03` | GPLv3 text; exact version election per files. [LICENSE](https://github.com/optiscaler/OptiScaler/blob/c2e44a8bc911010d7ed3e4fa63843185fbb55d03/LICENSE) | **Copyleft review.** NC downstream provider router; DXVK optional component. Requires suitable temporal inputs; each API/output differs; no linking/adaptation approval. |
| [BarbatosAWLS/AeonSR](https://github.com/BarbatosAWLS/AeonSR/tree/1c4bbc79cf530a818969b608f914b9f7ff4756f8) · `1c4bbc79cf530a818969b608f914b9f7ff4756f8` | MIT own code. [LICENSE](https://github.com/BarbatosAWLS/AeonSR/blob/1c4bbc79cf530a818969b608f914b9f7ff4756f8/LICENSE), [THIRD-PARTY-NOTICES.md](https://github.com/BarbatosAWLS/AeonSR/blob/1c4bbc79cf530a818969b608f914b9f7ff4756f8/THIRD-PARTY-NOTICES.md) | **Permissive candidate.** NC missing-motion comparison; future DXVK optional package. Separate ReShade/vendor runtimes; normal AA does not reduce scene render cost; stock backends only. |
| [KillerPixelCrew/ReScaleFrame](https://github.com/KillerPixelCrew/ReScaleFrame/tree/267c0e5b1e4113ce3559cbfccac0872658eeefd0) · `267c0e5b1e4113ce3559cbfccac0872658eeefd0` | Mixed GPL-3.0-only / MIT Game SDK / retained GPL+Commons Clause. [LICENSE](https://github.com/KillerPixelCrew/ReScaleFrame/blob/267c0e5b1e4113ce3559cbfccac0872658eeefd0/LICENSE), [sdk/game/LICENSE](https://github.com/KillerPixelCrew/ReScaleFrame/blob/267c0e5b1e4113ce3559cbfccac0872658eeefd0/sdk/game/LICENSE), [runtime/managed/NOTICE.md](https://github.com/KillerPixelCrew/ReScaleFrame/blob/267c0e5b1e4113ce3559cbfccac0872658eeefd0/runtime/managed/NOTICE.md), [runtime/managed/notices/LICENSE-OPTIMUM-LEGACY-GPL-COMMONS](https://github.com/KillerPixelCrew/ReScaleFrame/blob/267c0e5b1e4113ce3559cbfccac0872658eeefd0/runtime/managed/notices/LICENSE-OPTIMUM-LEGACY-GPL-COMMONS) | **Mixed.** NC orchestration/contracts reference; conditional runtime. Managed Vulkan has restrictive retained terms; MIT SDK does not clear runtime. No direct managed reuse. |
| [GPUOpen-LibrariesAndSDKs/FidelityFX-SDK](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/tree/60f4ea81909200d8542eca14dccb2628b763a9a3) · `60f4ea81909200d8542eca14dccb2628b763a9a3` | Mixed binary-only default plus explicit MIT file list. [3rdpartynotice.md](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/3rdpartynotice.md), [docs/license.md](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/docs/license.md), [Kits/FidelityFX/docs/license.md](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/docs/license.md) | **Mixed.** FSR/optical flow/provider input reference. At this pin, three named signed SR/loader/FG DLLs are listed MIT exceptions. Never generalize to whole SDK/models. |

Use installer projects to compare documented inventory/recovery concepts; independently verify any proposed behaviour against Companion's transaction guarantees. Their names or README claims do not establish safe acquisition, complete rollback, provenance or compatibility. No installer implementation has been adopted.

## Licence findings that change selection

- **ReScaleFrame:** distinguish its GPL root, MIT Game SDK and retained managed-runtime GPL plus Commons Clause terms. The Game SDK is not a licence for the full runtime. VulkanStory also requires path-specific provenance review.
- **AMD FidelityFX SDK:** at the pinned revision above, the exception list in `docs/license.md` explicitly names the signed DX12 upscaler, loader and frame-generation DLL paths under MIT terms. This narrows the earlier default-grant concern; it does not clear every SDK/model artifact, future version or unverified package.
- **LumeniteFX, Luma-Framework, NeuralScreen, HelixSR and DXVK-Studio-CE:** restrictive terms make them unsuitable as assumed permissive implementation sources.
- **TuxScaling, OptiTux and bgfx-dlss5-nr:** no first-party licence grant was located in their complete inspected trees. Third-party MIT notices do not license the whole project.
- **Current lsfg-vk:** the [self-hosted licence](https://git.lsfg-vk.dev/lsfg-vk/plain/LICENSE.txt) is CC-BY-NC-ND-4.0. Historical MIT/GPL releases and forks require their own pinned review.
- **0ptiscaler4linux:** an MIT badge conflicts with the [inspected GPL-3.0-or-later grant](https://github.com/ind4skylivey/0ptiscaler4linux/blob/15a7a2310f5512ff6fcc0e15ba7db384a08e3217/LICENSE). Treat the actual grant as evidence.
- A wrapper's permissive source licence does not license proprietary downloads, neural weights or patched runtimes.

Exact exclusion evidence and broader rendering-source findings are recorded in Neural-Companion's [current source register](https://github.com/Tiflit/Neural-Companion/blob/main/docs/research/SOURCE-CANDIDATES.md). Its [source review rules](https://github.com/Tiflit/Neural-Companion/blob/main/docs/SOURCE-REVIEW.md) describe that project's adoption process; they do not replace DXVK-Companion's governing specification or workflow.

## Deferred or incompatible directions

Linux-only presentation/profile tools and offline video pipelines are deferred until a specific product gap justifies them. Multiple generic installers and similarly named Prism projects are not a dependency shortlist. Noncommercial/no-derivatives or missing grants block implementation reuse under the present evidence.

Neural-Companion's extracted/reconstructed/converted model routes remain outside its approved use-as-supplied boundary. They must not enter indirectly through Companion's installer. This is a project-scope finding, not a claim that their authors' own code is unlicensed.

No source establishes a universal temporal-input contract across arbitrary DXVK games. API translation does not recover every game's camera, depth, motion or UI semantics. Frame generation can increase displayed frame rate without speeding simulation or improving input response. Preserve those distinctions in any future package descriptions.

## Validation direction and review limits

Develop/debug on the development laptop; reserve the target gaming desktop for stable packaged acceptance. Record selected adapters to expose dual-GPU mistakes. Determine CPU/GPU limitations before attributing an enhancement's benefit. Device tests and game runs require their own approved execution scope and current host readiness.

The next rendering evidence belongs to Neural-Companion: a controlled capture/processing boundary, a documented unmodified provider control, then a separate missing-input comparison. See its [curated research](https://github.com/Tiflit/Neural-Companion/blob/main/docs/RESEARCH.md). No performance or B580 compatibility is certified by this review.

The wider review identified 83 sources; this page keeps the subset relevant to component management. Some older supplied audit attachments and two ancillary NVIDIA licence PDFs could not be read. Exact dependency/artifact review remains necessary before adoption. This documentation change preserves historical records, imports no external implementation, and neither assigns work to Gemini nor changes the active safety task or queued acquisition work.
