# Runtime provenance

Imported from `Devolutions/IronRDP`, commit
`66bfd60ed3e6663188980259377b2da5f4c9a9aa`, directory
`.github/actions/openai-agent`, on 2026-10-20.
Source: https://github.com/Devolutions/IronRDP/tree/66bfd60ed3e6663188980259377b2da5f4c9a9aa/.github/actions/openai-agent

The upstream workspace declares `MIT OR Apache-2.0`; both upstream root license
texts are retained here as `LICENSE-MIT` and `LICENSE-APACHE`. Bundled dependencies'
notices remain in `dist/licenses.txt`.

All 126 upstream runtime tests, action inputs/outputs, and runtime
behavior are retained. Local changes remove a provider-specific explanatory
comment and place test workspaces beneath the action directory instead of the
OS temporary directory. Five upstream integration tests depended on IronRDP's
external PR-policy tree; these now use self-contained, test-only review
schemas/validators in `test/fixtures/review`, with language-neutral reviewer
identities and C# example paths. Historical budget assertions remain fixtures,
not shipping provider configuration. The fixtures test runtime semantic repair
and log privacy, not UniGetUI's actual publication policy.

The bundle is rebuilt from these sources. No Rust/RDP prompts, protocol corpus,
production policy, provider defaults, or credentials are imported. `INTENT.md`
describes the generic harness only: inherited classifier/reviewer normalizer
field rules and whole-workflow retry assumptions were removed because those
are caller policy, not runtime requirements.

Use only from trusted immutable workflow code. Configurations, prompts, schemas,
validator/normalizer modules, and prompt-context are **trusted executable policy**,
not PR-head evidence. The action itself does not implement an enablement gate:
callers must gate all invocations before supplying provider credentials.

Validation (Node 24, after `npm ci --ignore-scripts`):
`npm test && npm run build`. Tests mock providers; no provider credentials needed.
See `action.yml`, `src/config.js`, and `INTENT.md` for the unchanged invocation and
configuration contracts.

Verified on Windows on 2026-10-20 with Node 24.22.0, obtained using npm because
the host only had Node 22.23.2:

```powershell
# From .github\actions\openai-agent
$node24 = 'C:\Users\mamoreau\AppData\Local\npm-cache\_npx\387698761821791d\node_modules\node\bin\node.exe'
$npmCli = 'C:\Program Files\nodejs\node_modules\npm\bin\npm-cli.js'
$env:PATH = "$(Split-Path $node24);$env:PATH"
& $node24 $npmCli ci --ignore-scripts
& $node24 $npmCli audit
& $node24 $npmCli test
& $node24 $npmCli run build
```

All commands exited 0: clean install passed, audit found zero vulnerabilities,
126 tests passed (zero skipped/failed), and bundle build passed. Repeated builds,
including after the clean install, produced identical `dist/index.js` SHA-256:
`C1826EF4FCA62ECCB58C386F48CAE82AA7396F561F36ED8885DADB1FC4318F9B`.
The action contract and both upstream root licenses remain byte-identical to
the read-only pinned source. Ubuntu execution remains a CI coverage gap.

## Surgical dependency security update

The original import's audit reported two high-severity affected dependency
packages. Exact overrides now pin patched versions within their existing
consumers' compatible ranges: `fast-uri` 3.1.6 → 3.1.8 (`ajv` allows `^3.0.1`)
and `undici` 6.28.0 → 6.28.1 (`@actions/http-client` allows `^6.23.0`; OpenAI's
optional peer range also accepts it). Direct dependency versions are unchanged.
`npm install --ignore-scripts` regenerated the lockfile; comparing it against
upstream confirms only those two package records changed. The bundle and
dependency licenses were regenerated with the checked-in build script.

Resolved advisory IDs reported by `npm audit --json`:

| Package | Advisory | Reported severity |
| --- | --- | --- |
| fast-uri | GHSA-qw65-cvwx-89v3 | high |
| fast-uri | GHSA-58mr-gqgx-xq4g | high |
| fast-uri | GHSA-hrr3-gc8f-f4qj | moderate |
| undici | GHSA-3wwx-pv8p-q78v | moderate |
| undici | GHSA-r53p-7pc4-xj5r | low |
| undici | GHSA-rfgv-xxqx-mfg5 | high |

`npm audit` after the update, including development dependencies, reports zero
vulnerabilities. This is an audit snapshot, not a guarantee against future
advisories.
