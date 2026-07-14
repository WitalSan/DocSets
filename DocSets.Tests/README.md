# DocSets tests

The MSTest project intentionally compiles the testable production sources directly. This keeps the suite independent from the Visual Studio extension host and allows it to run without `devenv.exe`. The Visual Studio MSTest adapter makes all tests available in Visual Studio Test Explorer.

Run from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\run-tests.ps1
```

The command returns a non-zero exit code when any test fails. Tests can also be run and debugged from Visual Studio Test Explorer. Covered areas include:

- workspace and solution-local serialization;
- readable ID migration and tree notifications;
- tree copy/move/flatten algorithms;
- History, Pin, and Recent automatic groups;
- undo/redo stacks;
- accordion ordering and expansion persistence;
- properties panel single/multi-selection behavior;
- Roslyn code highlighting and selection preservation;
- embedded icon loading and scaling.

Visual Studio automation, Roslyn workspace integration, filesystem tracking, and actual extension-host lifecycle still require integration tests running inside a VS experimental instance.
