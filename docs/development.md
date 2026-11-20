# Development guide

## Supported Power Platform surfaces

For now, the connectors support use in **Power Automate**, **workflows**, and **Copilot Studio**. Canvas apps have not yet been tested and are not currently considered a supported surface.

## Prerequisites

- PowerShell 7 (`pwsh`)
- .NET SDK 10 (see `global.json`)
- Node.js 22 (for `swagger-cli`)
- Power Platform CLI (`pac`)

## Source layout

The repository contains one solution per connector. `scripts/EnvironmentConfig.ps1` lists them in `$script:Solutions`, together with each connector name and test project.

| Solution | Connector | Tests |
| --- | --- | --- |
| `JevCustomConnector` | `mto_jevv1connector` (TypeSafe Jev v1) | `tests/JevConnector.Tests` |
| `MicrosoftDecisionCustomConnector` | `mto_msdecision1connector` (Microsoft-Decision-1; actions say 1.1) | `tests/MicrosoftDecisionConnector.Tests` |

Each solution has the same layout:

- `solutions/<Solution>/src/Other/Solution.xml`: solution identity, version, and the connector root component (type 372).
- `solutions/<Solution>/src/Connectors/<connector>*`:
  - `.xml`: connector metadata and the `scriptoperations` list. Every operation is routed to custom code.
  - `_openapidefinition.json`: the Swagger 2.0 contract and its dynamic extensions.
  - `_customcodeblobcontent.csx`: all runtime logic. Keep it under 1 MB, with no `using` directives, and use only the namespaces that custom code supports.
  - `_connectionparameters.json`, `_policytemplateinstances.json`, `_iconblob.Png`.

The two `.csx` files are independent copies. Custom code must be a single self-contained file, and keeping each one complete lets `Sync-FromDevelopment.ps1` round-trip portal edits. Most of the logic (question rows, validation, and schema generation) is the same in both, so apply a fix to both files when it concerns shared behavior.

### Microsoft-Decision-1.1 specifics

The Microsoft-Decision-1.1 decision API accepts the same `state` and `questions` as Jev and returns the same `answers`. The connector differs from the Jev connector as follows:

- **Endpoint:** each Foundry resource has its own host. A `dynamichosturl` policy routes requests to the `endpoint` connection parameter. The script keeps only the scheme, host, and port and sets the path to `/providers/microsoft/v1/systemone`, so a pasted target URI or project endpoint also works.
- **Deployment:** the request `model` is the deployment name. A `setheader` policy copies the `deployment` connection parameter into the `x-connector-deployment` header, and the script moves it into `model`. The header is not forwarded to Foundry. Policies run before custom code, which was verified on DEV.
- **Auth:** the API key is sent in the `api-key` header. Entra ID is not supported yet.
- **Operations:** there is no models endpoint on the data plane, so `ListModels` and `GetModelChoices` do not exist.
- **Limits:** a choice question needs 2-255 options. Jev accepts 1 option.
- **Images:** the `images` input (rows of `name` and `content`, or a native `assets` object) becomes the native `assets` object. The script reads the format and pixel size from the PNG or JPEG header, without decoding the image. It enforces the API's limits locally: still PNG or JPEG, 280-1568 pixels per side, at most 16 MiB each. The decision API rejects a request that references only some of its images (`assets are never referenced`), so the script appends `image:<name>` to text state for every image that no string in the state or questions references. It does not change JSON state; that case is an `unreferenced_image` error.
- **Display name:** the connector's `displayname` in `mto_msdecision1connector.xml` stays `Microsoft-Decision-1`. A solution import fails with *The connector display name attribute cannot be changed* when it differs from an installed connector. The OpenAPI title, action summaries, and descriptions say 1.1.
- **Double-encoded content:** Power Automate base64-encodes text that a flow passes to the `format: byte` image content, so base64 text from an expression arrives encoded twice. A binary value, such as `base64ToBinary()` or **Get file content** output, arrives once as a `$content` object. When the decoded bytes are base64 text that hides a PNG or JPEG, the script decodes once more.
- **Image reference limit:** the service allows at most 8 image references per question prompt and counts repeated references. Its count can be higher than the number of references in the request, especially with very short image names, so the connector does not check it and passes the service's 400 `invalid_image` through.

### Microsoft-Decision-1.1 MCP server

`mcp/microsoft-decision` is a Python stdio MCP server, built on the official `mcp` SDK (`MCPServer`), that calls the same decision API as the connector. It is not part of a solution and cannot reuse the `.csx`, so it repeats the connector's endpoint normalization (scheme, host, and port only) and validation limits. Its one tool, `evaluate`, takes the native `questions` object; the question-row format exists only for the Power Automate designer. Keep the limits in both places in sync, including the image rules in `images.py`, which mirror the `.csx`: header inspection, size and dimension limits, name rules, and image references. Only the double-encoding fallback is connector-specific, because only Power Automate adds the second base64 layer. Image tests in both projects use the real files in `tests/assets`. See its [README](../mcp/microsoft-decision/README.md) for configuration.

## Tests

```bash
dotnet test tests/JevConnector.Tests
dotnet test tests/MicrosoftDecisionConnector.Tests
uv run --directory mcp/microsoft-decision pytest
```

Each connector has a `*.Script` project that compiles the exact `.csx` against the shared `ScriptBase` stub in `tests/Shared` for `netstandard2.0`. Both scripts declare a class named `Script`, so each needs its own projects. Its global usings cover only the supported namespaces, so a reference to an unsupported API fails the build. The tests use a fake `IScriptContext` that records outbound requests. They cover:

- Request mapping.
- Validation limits.
- Auth normalization.
- Error passthrough.
- Schema generation with no outbound calls.
- Contract consistency between `scriptoperations`, the Swagger operation IDs, and the script's operation router.

## Scripts

All scripts take `-Solution <name>[,<name>]`. Without it, they work on every solution; `Import-Solution.ps1` needs `-Solution` or `-SolutionFile`.

| Script | Purpose |
| --- | --- |
| `Validate-Solution.ps1` | Runs these checks: files present; JSON and XML well formed; sizes; name lengths; identity consistency; Swagger 2.0 with no unsupported keywords; unique operation IDs; dynamic-extension targets exist; `scriptoperations` matches; `swagger-cli validate`; `dotnet test`; pack. Use `-SkipTests` or `-SkipPack` to skip steps. |
| `Build-Solution.ps1` | `-PackageType Both\|Managed\|Unmanaged`, `-Version x.y.z.w` (applied to a temp copy), `-OutputDirectory` (default `artifacts`). |
| `Import-Solution.ps1` | Imports `-SolutionFile`, or `artifacts/<Solution>_managed.zip` for `-Solution`, with `pac solution import --publish-changes --async`. Unmanaged packages require `-AllowUnmanaged`. |
| `Push-ToDevelopment.ps1` | Validates, builds an unmanaged package, and imports it into DEV. |
| `Sync-FromDevelopment.ps1` | Runs `pac solution sync` into a staging copy, normalizes JSON formatting, replaces `src`, and shows the diff. Requires a clean working tree unless you pass `-AllowDirty`. |

### Environment configuration

The DEV environment URL is resolved in this order:

1. The `-Environment` parameter.
2. The `POWER_PLATFORM_DEV_ENVIRONMENT_URL` process environment variable.
3. `.env` in the repository root (copy it from `.env.example`).

Only HTTPS URLs are accepted. Authenticate first with `pac auth create --environment <url>`.

### DEV loop

1. Run `pwsh ./scripts/Push-ToDevelopment.ps1 -Solution <Solution>`.
2. If needed, edit the connector in the maker portal. Custom code you edit in the portal should be copied back into the `.csx` file.
3. Run `pwsh ./scripts/Sync-FromDevelopment.ps1 -Solution <Solution>`, review the diff, run the tests, and commit.

## CI

`.github/workflows/validate.yml` runs on pull requests, on pushes to `main`, and on manual dispatch. A matrix job runs once per solution. It:

1. Installs .NET, Node, and the Power Platform CLI.
2. Runs validation and the tests for the solution.
3. Stamps the solution version as `major.minor.(build + run number).attempt`, using the solution's own `Solution.xml`.
4. Uploads the managed and unmanaged packages as an artifact.

A separate `mcp` job runs the MCP server tests with uv. The `Validate, test, and package` check requires both jobs to succeed.

On pushes to `main`, a separate `release` job publishes one GitHub release, tagged `release-<run number>.<attempt>`, with the versioned managed package of every solution. Only that job has write access to repository contents; pull request runs are read-only.

## Live verification

### Jev: verified on a DEV environment (2026-10-03)

| Check | Result |
| --- | --- |
| `Push-ToDevelopment.ps1` import and publish | Passed |
| `Sync-FromDevelopment.ps1` round trip | Passed. Only platform normalization changed: BOMs, the lowercased publisher name, and the re-encoded icon. |
| Connection with a plain key and with a `Bearer `-prefixed key | Both connected, and both called TypeSafe successfully. |
| Wrong key | TypeSafe's 401 `authentication_error` was passed through unchanged. |
| `ListModels`, `GetModelChoices` (`/_connector/model-choices`) | 200 |
| `GetOutputSchema`, using the full body or the `questions` only | 200 with identical schemas. Works even when the key is invalid, so no outbound call is made. |
| `Evaluate` with question rows | 200. The live response matches the generated schema exactly. `x-typesafe-request-id` is returned. |
| `Evaluate` with native questions and a JSON state | 200 |
| `EvaluateJson` | 200. `responseJson` parses. |
| Invalid questions (duplicate question ID) | 400 `duplicate_question_id`. The schema endpoint falls back to the generic schema. |
| Power Automate flow: Button trigger, then Evaluate, then Compose of `answers.is_urgent.noul`, `category.choice`, `severity.score` | Run succeeded: `0.97`, `engineering`, `3.0` |

To reproduce these checks:

1. Get tokens with Azure CLI for the tenant that hosts your DEV environment.
2. Create a connection with `PUT https://api.powerapps.com/providers/Microsoft.PowerApps/apis/<api>/connections/<name>`.
3. Call `<primaryRuntimeUrl><connection>/<path>` with a token for the `https://apihub.azure.com` audience. Leave out the `/v1` base path, for example `/systemone` or `/_connector/output-schema`.

### Microsoft-Decision-1: verified on a DEV environment (2026-10-23)

| Check | Result |
| --- | --- |
| `Push-ToDevelopment.ps1 -Solution MicrosoftDecisionCustomConnector` import and publish | Passed |
| `Sync-FromDevelopment.ps1` round trip | Passed. Only platform normalization changed: PascalCase policy keys (`TemplateId`, `Title`, `Parameters`), the re-encoded icon, and the organization version. |
| Policies run before custom code | Confirmed: the script received the rewritten host and the `x-connector-deployment` header. |
| Endpoint as `https://<resource>.services.ai.azure.com`, as `https://<resource>.cognitiveservices.azure.com/`, and as the full target URI | All three returned 200. |
| API key with surrounding spaces | Trimmed and accepted. |
| Wrong key | Foundry's 401 was passed through unchanged. |
| Unknown deployment | Foundry's 404 `DeploymentNotFound` was passed through unchanged. |
| Choice question with one option | 400 `missing_choice_options` from the connector, with no call to Foundry. |
| `GetOutputSchema` | 200 with the question-specific schema. |
| `Evaluate` with question rows | 200. `apim-request-id` and `x-request-id` are returned. |
| `EvaluateJson` | 200. `responseJson` parses. |
| Power Automate flow: HTTP trigger, then Evaluate, then Compose of `answers.is_urgent.noul`, `category.choice`, `severity.score` | Run succeeded: `0.95`, `engineering`, `2.40` |

The checks used the same API calls as the Jev checks. The connection parameters are `endpoint`, `deployment`, and `api_key`.

### Microsoft-Decision-1.1: verified on a DEV environment (2026-11-20)

Connector imported with `Push-ToDevelopment.ps1` into a DEV environment, with a new connection to a `Microsoft-Decision-1.1` deployment. The calls went through the connector runtime, so the policies and the custom code ran.

| Check | Result |
| --- | --- |
| Import with the connector display name changed to `Microsoft-Decision-1.1` | Failed: *The connector display name attribute cannot be changed.* The `displayname` stays `Microsoft-Decision-1`. |
| `Evaluate` with text question rows | 200 from `microsoft-decision-1.1`, same response shape as Microsoft-Decision-1 |
| `GetOutputSchema` with images in the body | 200, question-specific schema |
| Image rows (base64), unreferenced image appended to state | 200: cat 0.995, dog 0.0002 |
| Two images referenced in choice option descriptions | 200, selected the astronaut picture with 0.999 |
| `$content` file object and `data:` URI content | 200 |
| Native `assets` object with `format: jpg` and JSON state | 200 |
| `EvaluateJson` with an image | 200, `responseJson` parses |
| 200x200 PNG | 400 `invalid_image_dimensions` from the connector, no Foundry call |
| JSON state with an unreferenced image | 400 `unreferenced_image` |
| Four images across four questions | 200 in 8 s, 2.6 MB request |
| 4.7 MB PNG (6.3 MB request) | 200 in 16 s; custom code did not time out |
| 7.4 MB and 16 MiB PNGs (9.8 MB and 22.3 MB requests) | Passed the platform and the custom code, then 429 `RateLimitReached` from Foundry. Direct calls behaved the same: Foundry reserves about 0.25 tokens per request byte against the 2.25M tokens-per-minute quota. |
| Power Automate flow: `base64ToBinary()` image, base64 text image, undersized image | Run succeeded. Binary and text content both worked after the double-encoding fix: space 0.86, same scene 0.995, subject `astronaut`. The undersized image returned 400 `invalid_image_dimensions` (330x262) to the flow. `body('Evaluate_with_Microsoft-Decision-1.1')` works in expressions. |
| Manual designer checks (see below), including an **Images** row bound to **Get file content** | Passed |

Wikimedia returns 429 to the Power Automate HTTP action, so the flow embeds small thumbnails of the stock images instead of downloading them.

### Manual designer checks

The designer resolves the dynamic schema client-side, so these checks run in the browser. All passed on DEV on 2026-11-20:

- [x] In the Power Automate designer, add **Evaluate with Jev** with noul, choice, and score rows. Confirm that dynamic tokens appear for each question and option. This shows that the `body/questions` reference resolves for `x-ms-dynamic-properties`. (An earlier version referenced the object `body/definition`, which the designer flattens into leaf parameters, so it failed with *Parameter "body/definition" cannot be found*.)
- [x] Switch **Questions** to the entire-array input and confirm the action falls back to the generic outputs.
- [x] Copilot Studio: add the connector to a workflow, call `Evaluate`, and confirm the expected answers are available to subsequent steps.
- [x] Repeat the checks above for **Evaluate with Microsoft-Decision-1.1**, and confirm that the connection dialog shows the endpoint, deployment name, and API key fields.
- [x] In the designer, add **Evaluate with Microsoft-Decision-1.1** with an **Images** row bound to **File content** from OneDrive or SharePoint **Get file content**, and check the default action name the designer generates.

Canvas apps are intentionally excluded from the verification checklist because they have not yet been tested.
