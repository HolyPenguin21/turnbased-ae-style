# AI V2 TaskScore Inspector

Read-only HTML dashboard for the current checked-out Unity AI V2 TaskScore implementation.

## Run

Double-click:

```text
Tools\TaskScoreDashboard\run.bat
```

or:

```text
python Tools/TaskScoreDashboard/server.py
```

The page opens at `http://127.0.0.1:8765/`.

## Source of truth

No balance values are copied into the HTML. Every reload rescans:

- `Assets/Scripts/Ai/V2/Evaluation/TaskScore.cs`
- `Assets/Scripts/Ai/V2/Foundation/AiConfigV2.TaskScore.cs`
- `Assets/Scripts/Ai/V2/**/*.cs` for actual usage locations

The Unity C# implementation remains the only source of truth.

## Current scope

Read-only only. The dashboard provides:

- current categories, Fold sign and converter constants;
- row search, filters, sorting and column visibility;
- filters by axis/task/usage state;
- exact source usages with file, line, method and excerpt;
- all TaskScore config parameters;
- architecture notes for unused slots/parameters and raw slots.

The scanner is intentionally conservative and diagnostic; it is not a C# compiler and does not invent semantic mappings that are not visible in source.
