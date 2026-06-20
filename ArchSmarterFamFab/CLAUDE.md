# Project: FamFab (FamilyFabricator) — Revit Add-in

## Overview

FamFab is a Revit add-in that generates parametric families from images. The user picks a photo of a real-world object (furniture, fixture, equipment), the add-in sends it to the Claude API with a structured skill prompt, receives a validated JSON family definition, displays an interactive 3D preview with editable parameters in a WebView2 panel, and then executes the JSON to create the parametric family in the Revit Family Editor.

## User Flow (v0.1)

1. User opens Family Editor with the appropriate family template
2. Clicks **FamFab** ribbon button (ArchSmarter tab > FamFab panel)
3. Picks an image file (jpg, png, webp)
4. API call sends image + skill prompt to Claude, receives JSON
5. JSON is validated against the family schema
6. WebView2 preview window opens with 3D visualization and parameter controls
7. User reviews geometry, adjusts parameters (controls update JSON in real time)
8. User clicks **Generate**
9. Updated JSON is passed to the executor
10. Family geometry is created in the Family Editor

## Project Structure

```
ArchSmarterFamFab/
├── App.cs                          # IExternalApplication - ribbon setup
├── FamFabCommand.cs                # Main command: image → API → preview → generate
├── SettingsCommand.cs              # Settings command: API key + model config
├── Data/
│   ├── ClaudeClient.cs             # Claude API HTTP client (image + prompt → JSON)
│   ├── FamilyGenerator.cs          # JSON → Revit geometry executor
│   ├── FamFabSettings.cs           # Settings POCO
│   ├── FamFabSettingsManager.cs    # JSON file persistence (AppData)
│   └── SkillResources.cs           # Reads embedded SKILL.md, schema, viewer HTML
├── UI/
│   ├── PreviewWindow.xaml(.cs)     # WebView2 host for 3D viewer + Generate button
│   ├── SettingsWindow.xaml(.cs)    # API key entry, model selection
│   └── SettingsWindowViewModel.cs  # MVVM for settings
├── EmbeddedContent/
│   ├── SKILL.md                    # Skill prompt sent to Claude as system message
│   ├── family-schema.json          # JSON Schema for family definitions
│   └── revit-family-viewer.html    # Three.js 3D viewer with parameter controls
├── Helpers/                        # Ribbon, icons, command availability
├── Properties/                     # Assembly info, embedded icon resources
└── Resources/                      # PNG button icons (16x16, 32x32)
```

## Key Architecture Decisions

- **BYOK (Bring Your Own Key)**: API key stored as plain text JSON in `%AppData%\ArchSmarter\FamFab\FamFab.json` (same pattern as Charrette/ReRender)
- **Embedded resources**: Skill prompt, schema, and viewer HTML are compiled into the DLL as embedded resources
- **WebView2**: The 3D preview uses Microsoft.Web.WebView2.Wpf to host the Three.js viewer. Communication is via `NavigateToString()` for initial load and `ExecuteScriptAsync()` / `WebMessageReceived` for bidirectional data flow
- **Family executor**: Adapted from CreateContent's CmdFamilyGenerator. Supports extrusions and sweeps with parametric expressions
- **Single transaction**: The executor clears existing content in one transaction, then creates all new content in a second transaction

## Build System

- Multi-configuration build: `Debug R25` (default), supports R20 through R26
- R20-R24: net48, R25-R26: net8.0-windows
- NuGet: `Revit_All_Main_Versions_API_x64`, `Microsoft.Web.WebView2`, `System.Drawing.Common`
- Post-build copies DLL, .addin, and WebView2 native runtimes to `%AppData%\Autodesk\REVIT\Addins\[RevitVersion]\ArchSmarterFamFab\`

## C# Conventions

- PascalCase for classes, methods, properties
- camelCase for locals and parameters
- Prefix private fields with `_`
- Global usings in `Helpers/GlobalUsing.cs`
- One class per file

## Revit API Rules

- All model modifications require a `Transaction`
- Internal units are decimal feet — convert with `UnitUtils.ConvertToInternalUnits()`
- Use `SpecTypeId` and `GroupTypeId` for parameter creation (not deprecated enums)
- Null-check Parameters before reading
- Store `ElementId` not `Element` across transactions
- Close WPF windows before starting Revit transactions
- The add-in only works in the Family Editor (`doc.IsFamilyDocument`)
