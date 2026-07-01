# AGENTS.md — ArchSmarter Family Fabricator (FamFab)

Guidance for AI coding agents working in this repository. It assumes no prior
knowledge of the project. Written in English, matching the project's comments
and documentation.

## Project overview

FamFab (ArchSmarter **Fam|Fab** / Family Fabricator) is an open-source
**Autodesk Revit add-in** that generates parametric Revit families from images
using a vision LLM. The model **provider is selectable** — Anthropic **Claude**,
Google **Gemini**, or Moonshot **Kimi** — chosen in the Settings window.

The user flow, implemented in `CmdFamFab.cs`:

1. User opens Revit's **Family Editor** (the add-in refuses to run anywhere else).
2. User clicks the **Fabricate** button on the **ArchSmarter** ribbon tab, in the
   **Family Fabricator** panel.
3. In the **Generate** window the user picks one or more images (`.jpg/.jpeg/.png/.webp`),
   optionally sets a family name, a model, and extra text context.
4. The image(s) + an embedded "skill" system prompt + the JSON schema are sent to
   the selected provider, which returns a JSON family definition (schema version `0.1`).
5. The **Preview** window hosts a Three.js viewer in WebView2 for interactive 3D
   review and parameter editing; the user can also **Refine** the design with a
   follow-up prompt (another model call).
6. On **Generate Family**, the final JSON is executed by `FamilyGenerator` to
   build reference planes, parameters, subcategories, and geometry in the Family
   Editor, inside Revit transactions.

The user can also **Load JSON** directly in the Generate window to skip the API
call. Every API response, the final JSON, and the source image are logged to
`%AppData%\ArchSmarter\FamFab\Logs`.

This is a **BYOK (Bring Your Own Key)** tool: there is no backend, proxy, or
bundled key. Each provider's key is stored locally (one per provider), and
image/prompt data goes only to the selected provider's API host —
`api.anthropic.com`, `generativelanguage.googleapis.com`, or `api.moonshot.ai`.

Tech stack: **C# / .NET, WPF + WinForms interop, WebView2, Three.js, System.Text.Json**,
Revit API. Windows-only.

## Repository layout

```
/                                   Solution root
├── ArchSmarterFamFab.slnx          Solution (new XML .slnx format)
├── README.md                       End-user documentation
├── LICENSE                         MIT
├── .gitignore / .gitattributes     Standard Visual Studio ignores
└── ArchSmarterFamFab/              The single C# project
    ├── ArchSmarterFamFab.csproj    SDK-style, multi-target (Revit 2020–2027)
    ├── ArchSmarterFamFab.addin     Revit manifest (Type=Application → App.cs)
    ├── CLAUDE.md                   Older agent notes (see "Doc drift" below)
    ├── README.md                   "Future Improvements" fragment
    ├── App.cs                      IExternalApplication — ribbon setup
    ├── CmdFamFab.cs                Main command: image → API → preview → generate
    ├── CmdSetting.cs               Settings command
    ├── Data/
    │   ├── IFamilyModelClient.cs   Provider-agnostic client interface (generate + refine)
    │   ├── LlmClientFactory.cs     Builds the client for the selected provider
    │   ├── LlmProviders.cs         Provider constants + display/label helpers
    │   ├── LlmException.cs         Shared model-API exception (carries raw response)
    │   ├── LlmSupport.cs           Shared prompt fragments + JSON response cleaner
    │   ├── ClaudeClient.cs         Anthropic Messages API client
    │   ├── GeminiClient.cs         Google Generative Language API client
    │   ├── KimiClient.cs           Moonshot (OpenAI-compatible) chat client
    │   ├── FamilyGenerator.cs      JSON → Revit geometry executor (~1300 lines)
    │   ├── FamFabSettings.cs       Settings POCO (provider, per-provider keys/models)
    │   ├── FamFabSettingsManager.cs JSON persistence in %AppData%
    │   └── SkillResources.cs       Reads embedded SKILL.md / schema / viewer
    ├── UI/
    │   ├── GenerateWindow.xaml(.cs)  Image pick + API call + Load JSON
    │   ├── PreviewWindow.xaml(.cs)   WebView2 3D viewer + Refine + Generate
    │   ├── SettingsWindow.xaml(.cs)  API key + model entry
    │   ├── SettingsWindowViewModel.cs MVVM for settings
    │   └── FamFabStyles.xaml         Shared dark-theme ResourceDictionary
    ├── EmbeddedContent/            Compiled into the DLL as EmbeddedResource
    │   ├── SKILL.md                Claude system/skill prompt
    │   ├── family-schema.json      JSON Schema (draft-07) for family definitions
    │   └── revit-family-viewer.html Three.js viewer with parameter controls
    ├── Helpers/
    │   ├── GlobalUsing.cs          All global usings + type aliases
    │   ├── ButtonDataClass.cs      PushButtonData builder (icons, availability)
    │   ├── CommandAvailability.cs  IExternalCommandAvailability
    │   └── Utils.cs                Ribbon panel helpers
    ├── Properties/                 AssemblyInfo, Resources.resx/.Designer, Settings
    ├── Resources/                  PNG ribbon icons (16px / 32px)
    └── docs/reference/             Source-of-truth Claude Skill package (see below)
```

`docs/reference/` holds the original Claude "Skill" bundle the embedded content
was derived from: `revit-family-generator.skill`, an extracted
`revit-family-generator/` (SKILL.md, `references/family-schema.json`,
`references/example-cabinet.json`), and `scripts/validate_family.py`. The
reference `family-schema.json` is byte-identical to `EmbeddedContent/family-schema.json`;
the reference `SKILL.md` differs slightly from the embedded one. Treat these as
reference/design material, not runtime code.

## Build and run

Windows + a Revit install are required. There is no CLI test suite and no CI.

- **Solution/config model:** one SDK-style `.csproj` multi-targets Revit versions
  via **build configuration name**, not TargetFramework selection in the IDE.
  Configurations are `Debug R20`…`Debug R27` and `Release R20`…`Release R27`.
  - `R20`–`R24` → `net48`; `R25`/`R26` → `net8.0-windows`; `R27` → `net10.0-windows`.
  - Each config sets `RevitVersion` (2020–2027), a `REVIT20xx` compile constant,
    and pins `Revit_All_Main_Versions_API_x64` to `$(RevitVersion).*`.
  - `Debug R25` is the de-facto default; Revit 2025+ requires the .NET 8 SDK,
    Revit 2027 requires the .NET 10 SDK.
- **Build in Visual Studio:** open `ArchSmarterFamFab.slnx`, pick a config
  (e.g. `Debug R25`), build. The README refers to an `ArchSmarterFamFab.sln`;
  the repo actually ships the newer `.slnx`.
- **Build on the command line:** `dotnet build "ArchSmarterFamFab/ArchSmarterFamFab.csproj" -c "Debug R25"`
  (quote the configuration — it contains a space).
- **Post-build deploy (automatic):** copies the `.addin` and the built DLLs
  (plus any `runtimes/` for WebView2) to
  `%AppData%\Autodesk\REVIT\Addins\<RevitVersion>\ArchSmarterFamFab\`, so a
  successful build installs the add-in for that Revit version.
- **Debugging:** the project's `StartProgram` launches
  `%ProgramW6432%\Autodesk\Revit <version>\Revit.exe` with `/language ENG`, so
  F5 opens Revit with the add-in loaded.
- **Key project settings:** `x64`, `UseWPF` + `UseWindowsForms`, `ImplicitUsings`
  enabled, `Nullable` **disabled**, `LangVersion latest`. NuGet dependencies:
  `Revit_All_Main_Versions_API_x64` (compile-only, `PrivateAssets=All`),
  `Microsoft.Web.WebView2`, `System.Drawing.Common` (build/compile only).

## Architecture notes

- **Entry point:** `App` (`IExternalApplication`) creates the ribbon tab/panel and
  two `PushButtonData` from `CmdFamFab.GetButtonData()` / `CmdSetting.GetButtonData()`.
  Button availability comes from `Helpers.CommandAvailability` (enabled whenever a
  document is active).
- **Commands** are `IExternalCommand` with `[Transaction(TransactionMode.Manual)]`.
  `CmdFamFab` guards on `doc.IsFamilyDocument` and on a configured API key before
  opening any window. WPF windows are shown as modal dialogs (owner =
  Revit main window) and closed **before** the Revit transaction runs.
- **Model clients** implement `IFamilyModelClient` (`GenerateFamilyFromImageAsync`,
  `RefineFamilyAsync`); `LlmClientFactory.Create(provider, key, model)` picks the
  implementation from the stored provider. All share the embedded SKILL.md + schema
  system prompt (built in `LlmSupport.LlmPrompts`), send one or more images as base64, use
  `max_tokens/maxOutputTokens = 32768` and a 300s `HttpClient` timeout, then run
  `LlmSupport.LlmJson.CleanJsonResponse` (strip fences / extract the `{...}` object).
  Any failure throws `LlmException` (carrying the raw response JSON); a token-limit
  stop reason is surfaced as a truncation error. Wire differences per provider:
  - `ClaudeClient` → `POST api.anthropic.com/v1/messages`, headers `x-api-key` +
    `anthropic-version: 2023-06-01`; text at `content[].text`.
  - `GeminiClient` → `POST generativelanguage.googleapis.com/v1beta/models/{model}:generateContent`,
    header `x-goog-api-key`, `inline_data` image + `system_instruction`,
    `generationConfig.responseMimeType = application/json`; text at
    `candidates[0].content.parts[].text`.
  - `KimiClient` → `POST api.moonshot.ai/v1/chat/completions` (OpenAI-compatible),
    `Authorization: Bearer`, image as a base64 `image_url` data URL; text at
    `choices[0].message.content`.
- **`FamilyGenerator.Execute`** is the core executor. It parses JSON with
  `System.Text.Json`, requires `metadata.schema_version == "0.1"`, maps `units` to
  a `ForgeTypeId`, clears prior content in one transaction (preserving built-in
  parameters and reference planes from allow-lists), then in a second transaction
  creates parameters, reference planes, subcategories, and geometry. Supported
  geometry: **extrusion, sweep, blend** (a same-radius circular blend is built as an
  extrusion). Supported profiles: rectangle, circle, custom (blends approximate
  circles as polygons). Parametric expressions are strings evaluated by
  substituting parameter defaults and calling `DataTable.Compute`; all lengths go
  through `UnitUtils.ConvertToInternalUnits` (Revit's internal unit is decimal feet).
  Results/warnings are collected in `GenerationResult`.
- **Preview / WebView2:** `PreviewWindow` creates a `CoreWebView2` with a user-data
  folder under `%LocalAppData%\ArchSmarter\FamFab\WebView2`, loads the viewer via
  `NavigateToString`, injects a JS host bridge, and communicates with
  `ExecuteScriptAsync` (push family JSON in) and `WebMessageReceived` (pull edited
  JSON back). On Generate it reads `JSON.stringify(currentFamily)` from the page.
- **Settings/persistence:** `FamFabSettingsManager` reads/writes
  `%AppData%\ArchSmarter\FamFab\FamFab.json` (indented JSON, no naming policy),
  auto-creating defaults. It stores the active `Provider` plus a separate API key,
  selected model, and model list **per provider** (`Claude*`/`Gemini*`/`Moonshot*`),
  and its accessors (`GetApiKey`/`GetModelName`/`GetAvailableModels`) resolve against
  the current provider. `SettingsWindowViewModel` is INotifyPropertyChanged MVVM; the
  Settings window has a Provider picker that swaps the key box + model list, and the
  unbound `PasswordBox` is resynced via the VM's `ApiKeyRefreshed` event.

## Code style and conventions

- **C# naming:** PascalCase for types/methods/properties, camelCase for locals and
  parameters, `_camelCase` for private fields. One public class per file.
- **Namespaces** are block-scoped: `ArchSmarterFamFab`, `.Data`, `.UI`, `.Helpers`.
  Command classes are prefixed `Cmd` (`CmdFamFab`, `CmdSetting`).
- **Global usings** live in `Helpers/GlobalUsing.cs`, including disambiguating
  aliases (`TaskDialog = Autodesk.Revit.UI.TaskDialog`, `View = Autodesk.Revit.DB.View`,
  `Window = System.Windows.Window`, `Color = Autodesk.Revit.DB.Color`). Prefer adding
  a shared using there over repeating it per file.
- **JSON** is always `System.Text.Json` (no Newtonsoft). The family contract is
  schema version `0.1`; changes to the shape must stay consistent across
  `EmbeddedContent/family-schema.json`, `EmbeddedContent/SKILL.md`, and
  `FamilyGenerator`.
- **UI** is WPF with a custom borderless dark chrome; shared brushes/styles are in
  `UI/FamFabStyles.xaml` (merged into each window). Reuse those resources rather
  than hardcoding colors.
- **Revit API rules** (hold these when editing `FamilyGenerator` or commands):
  all model edits happen inside a `Transaction`; convert user units with
  `UnitUtils.ConvertToInternalUnits` (internal = feet); use
  `SpecTypeId` / `GroupTypeId` / `ForgeTypeId` (not deprecated enums); null-check
  parameters; store `ElementId`, not `Element`, across transactions; close WPF
  windows before starting a transaction; the add-in only operates on a Family
  document.

## Testing

- There is **no unit-test project and no CI** in this repository. Validation is
  primarily manual: build → load in Revit → generate a family, and use
  **Load JSON** in the Generate window to exercise the executor without spending
  API calls (feed a JSON from the logs folder or `docs/reference/.../example-cabinet.json`).
- `docs/reference/.../scripts/validate_family.py` validates a family JSON against
  the schema and adds semantic checks. It requires the `jsonschema` package (use an
  isolated virtualenv if you install it) and is run as
  `python validate_family.py <family_json_path> [schema_path]`. It is a reference
  utility, not wired into the build.
- When you change the family JSON contract, verify a round trip: schema →
  SKILL.md guidance → `FamilyGenerator` execution → a Revit family, and confirm
  `example-cabinet.json` still validates and generates.

## Security considerations

- **BYOK, plaintext keys.** Each provider's API key is stored unencrypted in
  `%AppData%\ArchSmarter\FamFab\FamFab.json` (`ClaudeApiKey` / `GeminiApiKey` /
  `MoonshotApiKey`). Never hardcode, log, or commit a key.
- **No backend.** Requests go only to the selected provider's public API host
  (`api.anthropic.com`, `generativelanguage.googleapis.com`, or `api.moonshot.ai`).
  Do not add a proxy or telemetry endpoint without an explicit decision — it changes
  the product's privacy stance.
- **Logs contain user data.** `%AppData%\ArchSmarter\FamFab\Logs` stores raw API
  responses and copies of source images. Keep this out of version control (it is
  under `%AppData%`, not the repo) and be careful when sharing logs.
- The key is masked in the Settings UI (`PasswordBox`) and only shown when the user
  toggles "Show". Preserve that behavior when touching `SettingsWindow`.

## Doc drift to be aware of

`ArchSmarterFamFab/CLAUDE.md` and the root `README.md` predate parts of the
current code. When they disagree with the source, trust the source. Known gaps:

- Command files are `CmdFamFab.cs` / `CmdSetting.cs` (CLAUDE.md calls them
  `FamFabCommand.cs` / `SettingsCommand.cs`); the ribbon button is titled
  **Fabricate** in the **Family Fabricator** panel.
- The executor supports **extrusion, sweep, and blend** (CLAUDE.md mentions only
  extrusions and sweeps).
- Build coverage is **Revit 2020–2027** (`R20`–`R27`), not `R20`–`R26`; `R27`
  targets `net10.0-windows`.
- There is a `GenerateWindow` in the flow (Generate → Preview → Settings windows),
  and the solution ships as `.slnx`, not `.sln`.
- The model layer is **multi-provider** (Anthropic Claude, Google Gemini, Moonshot
  Kimi) behind `IFamilyModelClient` + `LlmClientFactory`; older notes and the
  `CLAUDE.md`/`README.md` that say "Claude only" are stale. The family JSON contract
  is provider-independent, so the schema, SKILL.md, and `FamilyGenerator` are shared.
- If you change build configs, the deploy path, conventions, or the family
  contract, update this file and, where relevant, `README.md` / `CLAUDE.md`.
