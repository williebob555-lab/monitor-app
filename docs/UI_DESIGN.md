# Monitor App UI Architecture and Design Specification

Status: CONFIRMED USER DIRECTION (provided by Wilson on 2026-11-22; sections 1-3 kept close to his wording, later sections lightly condensed without changing meaning). This document governs the visual layer and client UI. Where it overlaps the product specification (`Monitor App Design Specification.txt`), it confirms and refines the visual sections (5, 6, 8, 9, 16, 17). The backend architecture is preserved; the visual layer is a projection of shared object identity and relationships.

## 1. Fundamental design philosophy

Monitor App is a self-hosted, interactive technological workspace that represents the user's devices, software, infrastructure, physical possessions, projects, planned purchases, and the relationships between them.

The interface should feel like a combination of:

- An interactive engineering schematic.
- An exploded technical illustration.
- A living simulation of interconnected systems.
- A physical equipment and component workspace.
- A technical investigation environment.
- A sophisticated sandbox game interface.
- A time machine for the user's technological ecosystem.

It should emphatically NOT feel like:

- A conventional system administration dashboard.
- A collection of rectangular metric cards.
- A generic Material Design application.
- A business intelligence dashboard.
- A project management website centered exclusively around tables and Kanban boards.
- An endlessly customizable collection of interchangeable widgets.
- A chatbot with supporting graphical features.

The interface itself is the primary means of understanding, exploring, and interacting with the system. AI supplements the interface, not replaces it. The application should be enjoyable enough to explore that a user might open it simply to watch the activity of their equipment, inspect the structure of their infrastructure, or experiment with potential changes. Some interactions may exist almost entirely for visual enjoyment. This is intentional.

## 2. Overarching visual identity

Technical, sophisticated, spatial, information-rich, and playful without becoming cartoonish: a modern engineering workstation crossed with the interface of a well-designed systems-oriented video game.

Visual characteristics:

- Dark graphite, charcoal, and near-black foundational colors.
- Subtle differentiation between overlapping surfaces.
- Thin, deliberate outlines and connection paths.
- Selective use of translucent surfaces.
- Carefully controlled accent colors.
- Fine spatial grids, alignment guides, and technical markings where appropriate.
- Small, legible technical annotations.
- Strong hierarchy between primary objects and supporting information.
- Restrained use of shadows and illumination.
- A mixture of ordinary readable typography and monospace technical values.

Visual depth without excessive glassmorphism, neon gradients, or decorative effects. A light appearance may eventually be supported, but the identity must not depend on light cards on a white background.

### Semantic color system

- Green: healthy, connected, verified operational.
- Blue or cyan: active information transfer and network relationships.
- Amber: warning, degraded condition, uncertainty, or pending action.
- Red: fault, disconnection, failed operation, or destructive operation.
- Violet: proposed, simulated, or planned changes.
- Muted gray: inactive, historical, unavailable, or unselected information.

Do not color every object by status. Most objects keep a neutral identity; status appears through localized indicators, outlines, connection paths, or annotations.

## 3. Application shell

Desktop: a persistent shell around a large, immersive central workspace.

- **Global navigation rail (left)**, compact, collapsible to icons: Ecosystem, Physical workspace, Inventory, Projects, Sourcing, Alerts and automation, Activity and history, Search and AI assistance, Settings. These are perspectives on the same objects, not separate apps; an object keeps its identity across modes and navigation preserves context.
- **Contextual toolbar (top, thin)**: breadcrumbs, current workspace/object name, global search, view and relationship filters, editing mode, layout controls, history controls, contextual actions. Infrequent operations go in context menus and inspectors.
- **Main workspace (center)**: schematic, exploded object, physical arrangement, project assembly, or purpose-built analytical view; not constrained by permanent chrome.
- **Contextual inspector (right, collapsible)**: content depends on the selected type (device, service, component, connection, project, offer, event, automation).
- **Timeline (bottom, contextual)**: chronological events and historical reconstruction; expandable, collapsible, usable across modes.

## 4. The living ecosystem schematic

The flagship interface: an interactive spatial network of objects and relationships, not merely a topology diagram. It represents devices, operating systems, services, applications, storage, network connections, power, physical connections, synchronization, dependencies, ownership/containment, projects, planned additions, and relevant external services.

- **Object representation**: recognizable stylized forms per category (laptop with display and keyboard silhouette; server with racks/drive bays; switch with ports; storage as drives; phone as compact vertical; battery with visible charge; services as distinct containers/icons; networks as infrastructure or bounded regions). 2D/2.5D vector is preferred over an incomplete 3D engine.
- **Object information**: at ecosystem scale show only the most meaningful facts (laptop: name, connectivity, battery, activity; server: name, availability, significant workloads). Everything else via hover, selection, inspection, or zoom. No miniature dashboard on every object.
- **Spatial arrangement**: free placement, optional automatic arrangement as a starting point, persistent positions, moving groups, visual grouping, collapsible groups, alignment assistance, pan and zoom, fit-to-content and focus-on-selection. New telemetry must never rearrange objects.

## 5. Connections and living activity

Connections are first-class interactive objects representing real relationships: physical cable, wireless, VPN/overlay, power, file sync, software dependency, service communication, containment, planned/hypothetical. Distinguish types with restrained line style, endpoint symbols, color, and animation; route to avoid overlapping objects where practical.

Real activity animates the schematic (pulses during sync, directional flow, intensity approx. transfer rate, illuminated active paths, changed appearance when disconnected, sleeping/offline transitions). Animations must never imply activity the telemetry does not support; with only aggregate throughput, do not invent destinations.

Selecting a connection shows type, endpoints, status, known bandwidth, activity, recent events, history, diagnostics, and relevant configuration or controls.

## 6. Semantic zoom and exploded architecture

Zoom changes the information shown. Ecosystem scale: devices, infrastructure, groups, connections. Device scale: processor, memory, storage, network interfaces, power, host OS, containers, services, peripherals, with external lines landing on the relevant interface. Component/software scale: drives/partitions, filesystems, pools, containers and volumes, interfaces and addresses, sync folders and peers, service dependencies and configuration. The hierarchy must reflect modeled or discovered data only; never fabricate structure.

Opening an object is a coherent spatial transition (chassis opens, components move apart; host expands to its services; group unfolds); closing reassembles it. Return via zoom, breadcrumbs, or explicit navigation. Continuity of identity and hierarchy matters more than animation complexity.

## 7. Physical workspace

A complementary perspective of physical organization: rack, desk, portable devices, shelf, electronics assembly, equipment case, multiple locations. Objects keep identity across views. Rack view: units, occupants, free space, mounting, modeled cables, power distribution, planned additions; inspect a device from its position; drag planned equipment in without implying installation. Distinguish known measurements, approximate illustrations, and unverified fit. Not a CAD program.

## 8. Relationship layers and visual perspectives

Layers: network, power, storage, synchronization, software dependencies, physical containment, location, project membership, planned modifications. Show, hide, emphasize, and combine without destroying the arrangement. Prefer progressive emphasis/de-emphasis over hiding.

## 9. Contextual inspector

Conventional controls where they are best. Object-specific layouts, not one generic property table:

- Device: overview, live telemetry, hardware, software, relationships, configuration, history, actions.
- Service: state, host, dependencies, metrics, logs/events, configuration, controls.
- Component: identification, specs, parent equipment, compatibility, ownership/procurement, related projects, documentation.
- Product: imagery, normalized specs, offers, price history, availability, compatibility, project requirements, alternatives, sources.

## 10. Time machine and historical replay

Foundational. Timeline: scrub, pick date/time, zoom intervals, play/pause, speed, jump to events, immediate return to live. Event markers/bands: disconnections, failures, battery changes, config changes, hardware additions, sync events, price changes, project modifications, automation runs. In history, objects and states change per recorded data (devices reappear/disappear); show the examined timestamp and that it is not live; distinguish recorded, missing, and reconstructed estimates; never fabricate continuous activity between samples. Later: compare two moments, with changes emphasized in the schematic. History is observational; commands must not execute while viewing history.

## 11. Inventory interface

Structured browsing, editing, comparison, identification: tables, grids, lists, images are appropriate. Purpose-built category views. Entries link to physical, logical, and project context. States (owned, installed, unallocated, planned, ordered, no longer owned) are states/relationships of one object, not separate inventories. Global search, filters, sorting, bulk operations, contextual inspection. Discovered, manual, imported, and inferred data coexist with provenance.

## 12. Project workspace

Projects are an extension of the ecosystem: components, requirements, assemblies, equipment, relationships, budgets, decisions; nested hierarchy with structured editing and, where appropriate, a spatial assembly workspace reusing schematic concepts. Assign existing inventory directly. Unresolved requirements appear as outlined placeholders that open a requirement inspector with sourcing. Planned items: dashed outlines, translucent bodies, violet accents, explicit labels; never masquerade as existing; reconcilable with inventory on completion. Also: bill of materials (a well-designed table), estimated/actual/remaining cost, dependencies, missing components, alternatives, compatibility, purchase status, notes.

## 13. Sourcing and procurement

Product vs. retailer offer are distinct objects (price, shipping, availability, condition, seller, URL, last checked). Visual product browsing, side-by-side comparison with category-appropriate specs (no universal layout). Price history as conventional charts with annotated events. Monitoring rules on offers, products, categories, or requirements. Clearly mark missing shipping/taxes, stale prices, uncertain data; distinguish exact from estimates.

## 14. Alerts and automation interface

Event-condition-action rules readable as sentences ("WHEN the laptop becomes disconnected IF it remains disconnected for more than five minutes THEN send a push notification"). Structured editor for trigger, target, conditions, actions, timing, repetition, cooldowns, destinations, enabled state; connected blocks acceptable but no general programming environment. Create rules contextually from inspectors with the object and trigger prefilled. Show active/disabled rules, last execution, history, failures, actions, related objects; surface automation activity in the schematic and timeline.

## 15. AI integration

Contextual capability, never the dominant identity or a required intermediary. Uses: search, explanation, investigation, requirements analysis, product comparison, compatibility, automation suggestions, telemetry interpretation, planning. Selected object passes as context; responses reference real objects and navigate to them; distinguish observed facts from AI interpretation. AI proposals appear as planned objects to inspect before adopting; never silently confirmed. Distributed local/server models without manual model choice per interaction. Monitoring, collection, and manual controls work without AI.

## 16. Search and command interface

Global search across devices, services, components, connections, offers, projects, automations, historical events, documentation, with previews and direct navigation. Command overlay ("Show offline devices", "Open server storage", "Highlight all Syncthing connections", "Find unallocated SSDs", "Show yesterday's disconnections", "Create a price alert"). Natural language may supplement; common operations stay in ordinary controls.

## 17. Notification and activity interface

Notifications are meaningful events tied to their objects: selecting one focuses the object, highlights the connection, opens diagnostics, and moves to the timestamp. Severity distinctions and grouping; distinguish informational, warnings, faults, user alerts, automation results, suggested actions. Activity history and notifications differ; not every event interrupts.

## 18. Interaction design and visual enjoyment

Physicality and responsiveness: magnetic alignment, connections adapting as objects move, exploded spreading and reassembly, smooth camera between related entities, subtle activity on real links, highlighting propagating through dependencies, gentle live/historical transitions, interactive highlighting in physical illustrations. Integrated with the information architecture; no detached decoration. Active but not exhausting: avoid constant dramatic motion, bouncing controls, excessive glow, elaborate transitions for routine operations. Provide reduced motion.

Exploration (inspect, move, expand, play history, experiment with planned arrangements, highlight) is always safe. Operational actions (restart, configure, delete, remote commands) are clearly distinct from exploration.

## 19. Platform-specific adaptation

Coherent identity without shrinking the desktop UI.

- Desktop: primary for spatial editing, planning, investigation, comparison; large workspace, mouse/keyboard, inspectors, resizable panels, shortcuts, multi-select, wheel zoom.
- Tablet: substantial spatial exploration; touch controls, pinch zoom, direct movement, collapsible inspectors, stylus.
- Phone: ecosystem status, notifications, device inspection, price monitoring, quick controls, search, project info, accessible schematic exploration. Inspector becomes a bottom sheet or dedicated screen. Full schematic explorable; dense desktop controls are not compressed.
- Android widgets: device battery/connectivity, service availability, infrastructure warnings, sync status, watched prices, procurement progress; each links back to its object.

## 20. Information integrity

Visually distinguish observed, discovered, manual, imported, estimated, inferred, proposed, stale, and unavailable information without letting it dominate. A silent device must not stay healthy because its last reading was healthy; an unrefreshed price is not newly verified; proposed is not installed; estimated compatibility is not confirmed. Prefer explicit timestamps and uncertainty indicators.

## 21. Customization boundaries

Intentionally designed specialized interfaces, not a configurable dashboard framework. User controls: arrangement, positions, visible layers, grouping, saved perspectives, filters, alert behavior, selected monitoring info, notification, animation, and theme preferences. Developers decide presentation per object type (a switch deserves a switch representation, a BOM a BOM, a rack a rack).

## 22. Implementation architecture expectations

Preserve the existing architecture and working functionality; do not rewrite the backend for visuals. Ground the visual layer in shared object identity and relationships. Reusable concepts (adapted to existing primitives, not duplicated): entity renderer, entity inspector, connection renderer, relationship layer, spatial workspace, semantic zoom controller, object selection model, timeline controller, historical state provider, planned-state overlay, contextual command system, purpose-built comparison/analytical views. Consistent identity, state, and actions across modes; no separate artificial data models per screen.

Performance: telemetry updates never rebuild the visual tree or reset the camera; incremental updates, viewport culling, detail reduction at distance, efficient connection rendering, reasonable animation rates, buffered telemetry visualization, separation of persistent layout and volatile state. Replay avoids unnecessary re-rendering.

## 23. Suggested implementation sequence

- A Foundation: shell, design tokens, navigation, shared selection, contextual inspector, spatial workspace, persistent layout.
- B Living schematic: distinct device representations, typed connections, pan/zoom, placement, live status, activity visualization, relationship filters. (Defines the identity.)
- C Exploded architecture: expandable devices, internal components and services, hierarchical navigation, semantic zoom, expansion/reassembly.
- D Historical exploration: timeline, event markers, state selection, reconstruction, playback, clear live/historical modes.
- E Physical workspace and inventory.
- F Project planning and sourcing.
- G Automation and contextual intelligence.

A recommendation for UI development, not a requirement to discard or reorder backend work.

## 24. Acceptance criteria

1. Open the app and immediately understand the structure and condition of the ecosystem.
2. Observe genuine activity through visual relationships.
3. Approach a device and reveal its internal structure without losing spatial context.
4. Inspect detail without leaving the workspace.
5. Move back in time and see the ecosystem change per recorded history.
6. Examine equipment from logical and physical perspectives.
7. Navigate between inventory and planned projects naturally.
8. Experiment with additions without changing real infrastructure.
9. Source components and compare offers in context.
10. Create monitoring and automation rules directly from objects.
11. Receive notifications that lead back to the cause.
12. Perform all essential operations without AI.
13. Enjoy exploring even with no urgent task.

The ecosystem should feel like a coherent, inspectable, evolving world. Do not settle for a dashboard as an approximation of that vision.

## Visual references (loose inspiration)

Screenshots provided by Wilson on 2026-11-22 as loose inspiration, not to copy (images kept out of the repo):

- Node editor: game-engine node editor. Dark graphite panels, node cards with typed input/output pins, colored typed wires routed between pins, docked component tree and details panel.
- Network designer: network designer canvas. Device cards with an image and key values, port-to-port orthogonal wiring with direction arrows, dashed group regions, zoom control, properties panel.
- Facility planner: 3D facility/rack planner. Physical workspace with capacity coloring (green/amber/red) and a fit-check tooltip (rack space, power, cooling, weight).
- Exploded server: isometric exploded server. Lid lifted, CPU/heatsinks, RAM sticks, board, drives sliding out with status LEDs.
