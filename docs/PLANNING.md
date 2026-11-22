# Planning workspace: racks, pedalboards, projects and connector knowledge (T301)

This is a **design-only, hardware-independent foundation**, deliberately separate from discovered or owned devices. A proposed component, a catalog product, a seller offer, and an installed physical instance are distinct. Nothing in this module auto-creates inventory, changes a device, or claims a physical installation is safe.

## Domain model

`src/MonitorApp.Planning` contains the pure .NET 10 planning model and testable validators:

- `Projects/ProjectScenario.cs` — named, revisioned build scenarios; parts, quantities, requirements, task dependencies and seller offer observations. Costs are grouped by currency, with unknown shipping/tax/prices kept unresolved. Choosing a retailer offer does not mark an item owned.
- `Racks/RackModels.cs`, `RackLayoutValidator.cs` and `RackPanelProjection.cs` — 19-inch, 10-inch, 23-inch, 21-inch Open Rack, Eurorack, custom, 500-series, 4U/5U modular, pedalboard and desktop audio surfaces. Explicit width, height, depth, rack U/HP, front/rear reserves, rail spacing, hole styles, nested subracks, cable bends, underside power clearance, pedal access, weight, heat, power and 3D collisions. Equal nominal panel widths do **not** imply compatible rails or fasteners. Missing dimensions generate unresolved findings.
- `Constraints/RackPhysicalConstraints.cs` — extensible, versioned rules for inlet/exhaust airflow, heat, cooling, electrical feed ratings, EMI/RF separation, cable length and bend radius, acoustic comfort and rack stability. Each rule distinguishes measured evidence, vendor specs, estimates and unknowns; a heuristic does **not** certify EMC, cooling, power safety or structural stability.
- `Audio/AudioPlan.cs` — distinct audio signal levels (instrument/mic/line/speaker/Eurorack), analog/digital/MIDI/CV connections, impedance/headroom, phantom power, balanced wiring, latency, and pedalboard DC voltage/polarity/current/isolation. Connector shape alone cannot establish level or wiring compatibility.
- `Connections/` — extensible `ConnectorCatalog` with well over 100 shape definitions across USB, display, power, network, audio, coax RF, storage, controls and internal headers. `ConnectionCompatibilityEvaluator` checks physical mating, source/sink direction, **confirmed** protocols, cable wiring, negotiated power, voltage, current, polarity and documented adapters. For example, USB-C does not imply Thunderbolt/DisplayPort alt mode; TRS MIDI Type A is not Type B; 5.5/2.1 mm and 5.5/2.5 mm DC barrels are not assumed interchangeable. All assessments are predictions from declared evidence, not instructions to energize hardware.

## Standalone interaction prototype

`prototypes/rack-studio/index.html` offers a local UI exploration without a backend, cloud service, server or device connection. It includes front/depth/thermal rack views, rack-format selection, a **separate top-down pedalboard surface**, editable part dimensions, nested Eurorack carrier, signal-chain and DC power review, project tasks, sourcing fixtures, undo/redo, keyboard placement, and import/export of a proposed design JSON file. The UI uses illustrative values and may intentionally show errors or unresolved warnings. The prototype is not integrated into the Avalonia app and is not currently an authoritative validator: the C# domain is designed to become that shared engine.

### Test entry point

```sh
dotnet test tests/MonitorApp.Planning.Tests/MonitorApp.Planning.Tests.csproj -c Release
```

No real hardware testing is needed to run these unit tests. Future integration must preserve the project/scenario revision and catalog/offer/observed-instance separation. When a provider API is added, the collector must retain explicit provenance, timestamps, authentication boundaries, rates, errors and currency; it must never silently substitute a price or assume availability.

### Deliberate limitations

Exact device mounting, tolerances, orientation, rail pattern, voltage, RF exposure, cable routing and thermal behavior are often unavailable without a vendor specification or measurement. Unknowns must remain visible. The connector catalog is **broad, not complete**; vendor-specific pinouts, gender, keyed variants and active-mode implementations require explicit new definitions, not automatic conversion. An adapter declaration alone never certifies downstream signaling, power or cable capabilities.

This folder has no hardware integration and does not modify Claude-owned node, UI, peer transport, alert or persistence modules. Integration is a separate board claim.
