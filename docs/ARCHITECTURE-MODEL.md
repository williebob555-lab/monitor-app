# The Shared System Model

Status: design accepted for implementation in T061. Section 12 says exactly what exists in code today and what is still a plan.

This document explains, in plain language, the one model that every part of Monitor App works on: monitoring, inventory, physical design (racks, pedalboards, Eurorack, enclosures, desks), connections and compatibility, projects, sourcing, automation, and history. It is written to be read by Wilson first and by developers (Claude and ChatGPT) second.

Read together with:

- `docs/SCENARIOS.md`: Wilson's twelve scenarios. They are the acceptance test for this model. Section 10 walks each one through it.
- `docs/ARCHITECTURE.md`: peers, pairing, replication and the monitoring data path. Nothing here changes those.
- `docs/PLANNING.md` (T301, ChatGPT): the rack, pedalboard, connector and project engines. Section 9 says how they sit on this model.
- `docs/UI_DESIGN.md`: the visual language. Section 8 here describes the interaction model the UI is built on.

---

## 1. The idea in one paragraph

There is one record for each real or planned thing, and many ways to look at and work with it. An audio interface is a single object. It can be a device sending live readings, a box bolted into rack position 12U, a set of ports with cables and signals, a part inside a larger audio system, an asset in inventory, a line in a project, a product with specs and prices, and a thing whose connections changed last month. Those are views of one object, never copies. Moving from one view to another keeps you on the same object. Every view can also change the object, because the app is a place to build and experiment, not only to watch. And underneath the views a general reasoning layer understands the objects well enough to check, derive, suggest and explain, so the app behaves like a collaborator rather than a filing cabinet (section 5).

## 2. What is wrong with the current shape (why this is needed)

Today the code has three separate ideas of "a thing":

1. Monitoring knows peers (by key) and observed devices (`observed_entities` in each peer's database).
2. `contracts/monitor/v1/inventory.proto` defines a universal object envelope with actual vs design state, but nothing stores or uses it yet.
3. T301 (ChatGPT, PR #12) has rack equipment, ports, project parts and offers, each with their own ids, kept in JSON files.

Left alone, these grow into separate applications that happen to share a window. This model is the common ground they all point at.

## 3. Building blocks

The model has a small number of general building blocks. Every scenario is built from them. There is no special code per scenario ("pedalboard workflow", "speaker workflow").

### 3.1 Objects

An **object** is anything worth naming: a server, a drive, a cable, a rack, a pedal, a Syncthing service, a room, a shelf, a project, a requirement, a product listing, a container, a VLAN.

Every object has:

- a permanent **id** that never changes, even if the name, location, owner or monitoring source changes;
- a **kind** from the kind library (for example `device.audio-interface`, `assembly.rack`, `part.drive`, `service.syncthing`, `space.enclosure`). Kinds are data, not code, so new kinds can be added without a rebuild. A kind may name a parent kind (`device.amplifier` is a `device`), so views and rules can work on broad families;
- a **name** and tags;
- a **lifecycle state** (section 3.6);
- **properties** (section 3.2);
- **ports** (section 3.3);
- which **plane** it lives on: the actual world, or a design (section 3.7).

Product, listing and physical item are different objects. "Focusrite Scarlett 18i20 3rd gen" is a product. "That model at a particular retailer for $499" is a listing (offer). "The one in my rack, serial X" is an item. An item points at its product; it inherits the product's specifications unless it overrides them; it points at the listing it was bought from. This keeps prices and specs on the product while history and condition stay on the thing you own.

### 3.2 Properties, and how sure we are

A **property** is a named fact about an object: height, weight, rated power, heat output, airflow direction, output impedance, battery capacity, price, frequency band, firmware version, anything.

- Property names come from the property library (`mechanical.height`, `electrical.power.max`, `thermal.heat-output`, `thermal.airflow`, `audio.output.level`, `rf.band`). Like kinds, the library is data and can be extended. Unknown names are allowed so a user can record anything, but library names are what the engines understand.
- Numbers always carry a **unit**. The model converts units; it never assumes them.
- Every value carries **evidence**: where it came from and how much to trust it. The levels are:
  - **Measured**: observed by a sensor, a monitoring adapter or a user's measurement, with a time.
  - **Specified**: from a manufacturer specification or datasheet, with a reference.
  - **Entered**: typed by the user with no stated source.
  - **Estimated**: calculated or derived by an engine (for example a predicted temperature).
  - **Unknown**: we know the property matters but have no value. Unknown is a real state, not a blank.
- An object can hold several values for the same property from different sources (a datasheet says 45 W, a meter measured 52 W). Views show the strongest one and let you see the others.

This is how the app keeps Wilson's rule from every scenario: always separate measured from modeled from unknown, and separate a verified incompatibility from an estimate.

### 3.3 Ports

A **port** is a place where something connects to an object: a USB-C socket, an XLR output, a 1/4-inch instrument input, an IEC power inlet, a DC barrel output on a pedal power supply, an Ethernet jack, a SATA connector on a drive, a Eurorack CV jack, an antenna connector, and also logical ports such as a network interface or a radio link.

A port records each layer of compatibility separately, because a plug that fits proves nothing else:

| Layer | Examples | Where it lives |
| --- | --- | --- |
| Physical | connector family and exact variant, plug or socket, keying, pin count, locking | connector library id + gender |
| Electrical | voltage, current, polarity, impedance, phantom power, wattage | port properties with evidence |
| Signal / protocol | USB 3.2, DisplayPort alt mode, line level vs instrument level vs mic level, balanced or unbalanced, MIDI, CV, Ethernet 1G | confirmed signal/protocol capability list |
| Direction | output, input, both | port direction |
| Position | front or rear face, location on the face | port placement (used by the physical views and cable length estimates) |

### 3.4 Connections

A **connection** links two ports. It can be direct, or go through a chain: device port → cable → adapter → cable → device port. Cables, adapters, splitters, DI boxes and converters are ordinary objects with their own ports. That is how "insert an adapter into the chain" works: it is just another object with two ports placed into the path, and the whole path is evaluated again.

A connection can be physical (a cable), wireless (a radio link between a transmitter and a receiver, which has a band and channel), or logical (Syncthing between two devices, a VLAN membership, a Tailscale overlay path). The kinds are different and are never merged: a physical LAN cable and a Tailscale path are separate connections.

### 3.5 Relationships, assemblies and placement

A **relationship** is a typed link between two objects: contains, mounted in, powered by, depends on, hosted on, instance of (item to product), offered as (product to listing), satisfies (part to requirement), candidate for, part of project, replaces.

Two relationships do most of the physical work:

- **Contains** builds the tree that the exploded view opens: rack contains server, server contains drive bay, drive bay contains drive. A pedalboard contains pedals. A Eurorack case contains rows that contain modules. A room contains a desk that contains a shelf. Any object can be an assembly.
- **Placement** says where a child sits inside its parent: position, rotation and which face (front, rear, top), plus the parent's mounting grid if it has one (rack units, Eurorack HP, 500-series slots, or free placement for pedalboards, shelves and enclosures). The engines need this for fit, collisions, weight distribution, clearance, airflow, cable length and access. The visual views need it to draw something that looks and behaves like the real thing.

### 3.6 Lifecycle

The lifecycle says where an object is in its life, separately from whether it is currently working:

`idea → candidate → planned → ordered → owned → installed → retired` (and `disposed`)

- Each move is a recorded event with a time and an optional note ("arrived damaged", "installed in rack A, 12U").
- Moves are checked: you can't install something you only plan, and retiring keeps the object and all its history.
- "Online", "offline" and "failed" are **not** lifecycle states. They come from live observations. An installed device can be offline; a planned device has no observations at all. Keeping these separate is what lets a proposed component exist with no telemetry, and an installed component exist with no agent.

### 3.7 Actual world and designs (experimenting without breaking anything)

- The **actual** plane is what really exists: what you own, where it is, how it is wired, what monitoring sees.
- A **design** belongs to a project and is a set of proposed changes laid over the actual world: add these objects, move that one, rewire these ports, choose this candidate.
- A project can have several **alternatives** (design A with the efficient amplifier, design B with more ventilation). Each one is its own layer, so comparing them never destroys the original proposal.
- Opening a design shows the actual world plus that design's changes. Editing in a design never changes the actual record.
- Making a design real is a series of explicit steps (order, receive, install, connect), each one an event. When an object is received, the planned placeholder becomes, or is linked to, a real item, keeping the decision history.
- When the project is finished it does not vanish. The objects it installed are part of the actual world, with their history pointing back to the project. The next upgrade starts a new design over the current actual world.

### 3.8 Observations (monitoring) and the bridge

Observations are the existing monitoring data: signed, origin-owned readings from peers and adapters (battery, temperature, throughput, service state, and so on). That pipeline does not change.

The **bridge** joins monitoring identity to model objects:

- Each peer and each observed device already has a stable monitoring id. The bridge gives it one model object, bound to that monitoring id. The binding is permanent; the monitoring id is never replaced.
- If the object already exists because the user entered it (the server was in inventory before the agent was installed), the user or an exact-identity match (serial number, chassis id) binds the existing object instead of creating a second one. An IP address is never enough, matching the rule in `docs/ARCHITECTURE.md`.
- Readings are fetched by monitoring id, so every view of the object can show live state and history without copying readings into the model.
- Monitoring can add **Measured** property values to the object (a measured power draw next to the datasheet value) without replacing what the user entered.

### 3.9 Events

An **event** is something that happened at a time: an object was added, a property changed, a cable was connected, a device went offline, a price dropped, a rule fired, a task was completed. Model changes, monitoring transitions and rule runs all go into one timeline. That timeline drives history (section 3.11), notifications and automation.

### 3.10 Requirements and products

A **requirement** is an object that describes a need instead of a product: "accepts an instrument-level signal, two outputs, latency under 3 ms, runs on 9 V centre-negative, fits in 120 x 70 x 50 mm". It is written with the same property and port vocabulary as everything else, so the app can check any candidate product against it and say which conditions pass, fail, or are unknown, and on what evidence.

Sourcing adds products, listings and price/stock readings. A price is just a reading on a listing, so price history, price alerts and charts reuse the same history and rule machinery as a battery level.

### 3.11 History and time travel

Every change to the model is stored as an event that is never edited or deleted. So the app can rebuild the model as it was at any moment: what existed, where it sat, what was connected to what, and what lifecycle state it was in. Combined with the existing as-of queries for readings, the whole system can be replayed.

Time travel is one way into history. Charts, event logs, before-and-after comparisons and side-by-side periods are others, and all read from the same records.

Replaying shows what happened in what order. It never claims one event caused another just because they were close together.

### 3.12 Rules and automation

A **rule** has a trigger, conditions, and actions, and can belong to a project, an object, or the whole system.

- Triggers are events or readings crossing thresholds: device disconnected, battery low, service failed, temperature high, price below target, back in stock, topology changed, task overdue.
- Conditions can ask about the model: "only if this project still needs more drives than it owns".
- Actions: notify, annotate history, change a lifecycle state or project field, or run an authorized control command. Control commands keep the existing permission and fingerprint rules: deny by default, fingerprint for high-risk actions.
- Every run is recorded: what triggered it, which conditions were checked with what values, what it did, and the result. The UI shows this on the rule and on the objects it touched.

The existing alert engine (`MonitorApp.Peer.Alerts`) is the first rule type and keeps working; it moves under this structure later rather than being rewritten now.

### 3.13 Findings (what the engines say)

Engines check designs and report **findings**. Each finding says what it is about (which objects, ports, or regions), how serious it is, and how certain:

- **Verified problem**: based on specified or measured values (the connector does not mate; 18 V into a 9 V-only input).
- **Likely problem (estimate)**: based on a calculation or heuristic (heat may build up behind this unit).
- **Unknown**: a value needed to decide is missing (the amplifier's airflow direction is not recorded).
- **OK as declared**: nothing found with the evidence available. This is never presented as a safety certificate.

Findings point at concrete things (a port, a face, a region of a rack) so views can show why, for example by drawing the blocked airflow path, instead of only listing warnings.

## 4. The libraries (extensible, not hard-coded)

Four libraries hold the vocabulary. All are versioned data, can be extended locally by the user, and later by importable packs:

| Library | Holds | Status |
| --- | --- | --- |
| Kinds | object kinds and their parent kinds, which properties and port templates they usually have | Starter set in T061 |
| Properties | property names, value type, allowed units, which engines use them | Starter set in T061 |
| Connectors | connector shapes and variants, what can physically mate, potential protocols | T301 `ConnectorCatalog` (120+ entries) becomes the shared library |
| Signals and protocols | signal levels, protocols, alt modes, power standards | Partly in T301 (`AudioPlan`, protocols in the connector catalog); to be unified |

## 5. The reasoning layer: the app as a collaborator

Wilson's goal is that the app works with you, not just stores what you type: it understands enough about the objects to notice problems, work out what is missing, propose fixes and explain itself. That is a first-class layer of the architecture, sitting between the model and every view. It is deterministic and algorithmic at its core. Small specific models (AI) may help organise data or turn a sentence into a structured requirement, but the layer works fully without them.

The rule that makes this possible: **no one-off code.** No "pedalboard check", no "speaker wizard". Every capability is a general mechanism applied to the vocabulary in the libraries. A new kind of equipment, connector or signal becomes understood by adding library data, not by writing a new feature.

The layer has five general mechanisms:

1. **Constraint evaluation.** Constraints are declared against properties, ports and placement ("input voltage range must contain supply voltage", "child depth plus rear clearance must not exceed rack depth", "intake face must not face a closed panel within N mm"). The engines (below) are bundles of such constraints for one discipline. Every result is a finding with an evidence level (section 3.13).
2. **Propagation along chains.** Values flow through connections and containment: a signal's level and format through each cable, adapter and device; power draw up through supplies and distribution; heat into the enclosing space; weight into the rack. A problem is found where it actually occurs in the chain, and the explanation shows the path.
3. **Requirement derivation.** From a design the layer works out what is needed but not present: an unconnected input that needs a source, a missing cable between two ports (with connector ends and a length estimate from port positions), a supply that must provide more current, mounting hardware for a non-standard unit. Each becomes a requirement object, so it can be researched, bought and tracked.
4. **Suggestion.** For a requirement or a failing finding, the layer searches what is available, in this order: things you already own, then products in the local catalog, then (optionally) external sources. Candidates are ranked by how many conditions they pass on what evidence, and each suggestion carries its reasoning ("passes level and connector; power draw unknown"). Suggestions are offered, never applied without the user.
5. **Explanation.** Every finding, derived requirement, suggestion and rule run keeps the trail of facts and constraints it came from, so any view can answer "why?". This is also what lets the UI draw reasons (the blocked airflow, the wrong-level jack) instead of listing warnings.

Where optional models (AI) fit: turning free text into a structured requirement ("compact, two outputs, low latency for bass"), extracting specifications from a datasheet or product page into properties (marked Imported with a source), suggesting kind and property names for unfamiliar equipment, and summarising long histories. Their output always enters the model as ordinary data with its source, and the deterministic layer checks it like anything else. Nothing depends on them; nothing they produce is trusted more than its evidence.

### 5.1 Engines

Engines are pure checks that read the model and return findings. Each owns one discipline and can be used alone:

- Mechanical: fit, depth, rail spacing, collisions, clearance, access, weight distribution, stability.
- Electrical: power budget, voltage, polarity, current, grounding.
- Thermal: heat load, airflow direction, blocked vents, cooling.
- Signal: level, impedance, balanced/unbalanced, mono/stereo, protocol and alt mode, conversion required.
- Connectivity: whole connection chains through cables and adapters.
- RF: band and channel overlap, intermodulation spacing, regional limits; measured RF readings shown separately from predictions.
- Project: dependencies, budget per currency, what still needs buying.

T301 already contains first versions of the mechanical, electrical, thermal, signal, connectivity and project engines. They move onto this model's ids (section 9). The RF engine is not started.

## 6. What a view needs from the model

Every view reads the same objects. This table is the contract between the model and the UI:

| View (lens) | What it shows | What it reads |
| --- | --- | --- |
| Living system | devices, services and logical links, live activity | objects, logical connections, bridge, live readings |
| Open-up (exploded) | an object pulled apart into its parts, then their parts | contains tree, placement, ports |
| Physical bench | rack, pedalboard, Eurorack case, enclosure, desk, drawn to scale; front, rear, top, rotate; overlays for power, heat, airflow, signal, RF | placement, dimensions, ports' positions, findings |
| Signal path | a signal followed from source to destination through every device and cable | ports, connections, signal layer |
| Project | goals, alternatives, tasks, requirements, what to buy, budget | design layers, requirements, lifecycle, products and listings |
| Inventory | everything owned, where it is, condition, where it is used | actual plane, lifecycle, placement |
| Research and sourcing | products, specs, prices, stock, comparisons against a requirement | requirements, products, listings, price readings |
| Rules | triggers, conditions, actions, run history | rules, events |
| History | the system at a past time, event logs, charts, comparisons | change log, readings |

## 7. How a change flows (example)

Dragging a pedal power supply onto a pedalboard in a design:

1. The view asks the model to place object X inside object Y at position P in design D.
2. The model checks the move is allowed (X exists, not retired, Y can contain things), records one event, and updates the design layer.
3. Engines interested in placement re-run for that assembly and return findings.
4. Every open view of the same objects updates: the board, the power view, the project's parts list, the inventory entry's "used in" list.
5. Undo is the reverse event; history keeps both.

## 8. Interaction model

This section is the structure the UI is built on. It is not a screen design; `docs/UI_DESIGN.md` and later visual work decide how it looks.

1. **Building, not only watching.** Every view where it makes sense is editable: add, drag, rotate, wire, split, swap, compare. Starting from an empty rack, board or enclosure is a first-class starting point, as is starting from what already exists.
2. **Same object, different lens.** Selecting an object and switching view keeps it selected and centred, animating from its old form to its new one (the box in the schematic becomes the 2U chassis in the rack becomes the pulled-apart chassis). Identity is never lost across a switch.
3. **Separate workspaces, connected.** Not everything is on one infinite canvas. Physical benches, the signal path, projects, research and rules are distinct workspaces, each suited to its job, linked by the shared selection and by "open this in..." moves.
4. **Opening things up.** The open-up view uses the contains tree and placement so expanding a server looks like opening the machine: parts separate along real axes, labels attach to real parts, and each part can be opened again.
5. **Physical fidelity.** Physical benches draw objects to scale from their dimensions and placement. When data is missing the view shows it as missing (an outline with a question mark), not as a guess dressed as fact.
6. **Explain, don't just warn.** Findings point at regions, ports and paths so a view can draw the reason: the blocked vent, the overlapping channels, the wrong signal level at a specific jack.
7. **Measured vs modeled vs drawn.** Every value and every visual effect knows its evidence level, so the UI can always mark what is measured, what is estimated, what is unknown, and what is only decoration.
8. **Play is allowed.** Motion, physics-like dragging, unfolding and live activity are welcome where they help understanding or are simply enjoyable, as long as rule 7 holds.
9. **Phone and desktop.** Both read the same model. The phone favours quick understanding and actions; the desktop supports full design and editing.

## 9. How T301 (ChatGPT's planning work) fits

T301's engines are good and should not be rewritten. The integration (a later step, coordinated through `docs/COLLABORATION.md`):

- `ConnectorCatalog` becomes the shared connector library. T061 does not write a second one. Port records store its connector ids.
- `ConnectionPort`, `RackEquipment`, `PlanPart` and similar records become views built from model objects, ports and placement, using model ids instead of their own.
- The rack, pedalboard, physical-constraint, audio and connection evaluators become engines that read the model and return findings with the evidence levels in section 3.13.
- T301's own JSON persistence is replaced by the model store, so a rack design and the inventory and monitoring records are the same objects.
- The standalone Rack Studio prototype stays a prototype until the Avalonia views exist.

## 10. The scenarios, walked through the model

Each scenario in `docs/SCENARIOS.md` uses only the building blocks above.

1. **Speaker system around existing equipment.** Project with a design layer. Owned amplifiers and speakers are actual objects placed into the design (not copied). The enclosure is an object of kind `space.enclosure` with dimensions, vents as ports or regions. Placement plus thermal properties feed the mechanical and thermal engines; impedance and power ratings on ports feed the electrical and signal engines. Findings carry evidence levels, so "fits but cooling unsuitable (estimate)" is distinct from "4 ohm load below the amplifier's rated minimum (specified)". Alternatives are separate design layers. If nothing owned fits, the shortfall becomes a requirement used for research. The parts list is "objects in the design not owned yet".
2. **Rack from scratch.** A rack object with a mounting grid; equipment placed with front/rear faces and depth. Engines report depth overrun, weight distribution, blocked vents, blocked rear access. Ports have positions, so connections can be drawn between real ports and cable lengths estimated. Overlays (power, thermal, signal) are lenses on the same placement. Incomplete specs show as Unknown findings. Mounting plan, connection plan, parts list and open issues are queries over the design.
3. **Bass pedalboard and wireless.** A pedalboard object with free placement. Bass, wireless pack, POD Go, IEM transmitter and receivers, and power supply are objects with ports carrying signal level, balanced/unbalanced, mono/stereo. Wireless links are connections with band and channel properties; the RF engine compares them. Measured RF readings from an adapter are Measured observations, separate from predictions. A broken routing ("the IEM only receives the dry signal") is a signal-path finding on specific ports.
4. **Product when I don't know what I need.** The need is a requirement object built from the surrounding design's ports, power and space. Candidate products are scored against it condition by condition with evidence. Choosing one adds it to the design as a planned object; a price or stock rule is created on that listing within the project.
5. **Home server exploration.** Monitored peers and services become objects through the bridge. Syncthing is a service object with logical connections to peers and to folders on storage objects. Live transfer readings animate those connections. Opening the server uses its contains tree; parts with bindings show live readings, parts entered by hand show their properties. Authorized actions use the existing control path.
6. **Replaying a problem.** The notification points at an object. History rebuilds model and readings at earlier times; the event timeline lists what changed. Nothing infers cause from timing. From there the user can create a rule, run an authorized action, or annotate.
7. **Project over time.** Project with assemblies (objects with contains), tasks, requirements and alternatives. Lifecycle moves (planned, ordered, owned, installed) are events. When devices come online the bridge binds them to the planned objects. After completion the objects stay in the actual world; the next upgrade is a new design over it.
8. **Complicated connection.** Two ports, a chain evaluated layer by layer (physical, electrical, signal, direction) using the connector and signal libraries. Inserting an adapter is adding an object to the chain. The same connection object appears in the schematic, the physical bench and both devices' records.
9. **Physical and digital crossover.** The server's storage readings and temperatures come through the bridge; its placement in the rack is model data on the same object. Switching from the history view to the physical bench keeps the selection. Moving equipment in a design layer re-runs the thermal engine; predicted improvements are Estimated findings, measured temperatures stay Measured.
10. **Phone and desktop.** Same objects and history on both; the phone's compact view and the desktop's full views are lenses.
11. **Project-scoped automation.** A rule owned by the project: trigger = price reading on any candidate listing below target; condition = owned quantity for that requirement below needed quantity. Marking drives as owned changes the condition result without editing the rule. Each run is recorded.
12. **Exploring for fun.** Every move in this scenario (watch a link, open a server, follow a signal, rotate a rack, rewind, open a proposed project) is one of the lenses above on the same objects, with evidence levels available to the UI so play never blurs measured and modeled.

## 11. Storage, sync and safety

- **Store.** Each device keeps the model in its own SQLite file (`model.db`), separate from the monitoring database. This keeps the monitoring path untouched and its schema version unchanged.
- **Change log first.** The store is a log of change events. The current model, a past model, and a design layer are all built by replaying it. At personal scale (thousands of objects) this is fast; indexes can be added later without changing the log.
- **Sync between devices (planned, needs Wilson's decision).** Every device should have the full model. The proposed approach reuses the existing pattern: each device signs its own changes as an origin-owned stream, peers replicate those streams, and every device replays all streams in a fixed order. When two devices edit the same field at the same time, the later edit wins and the conflict is shown, never silently lost. This changes what data peers share, so it is a trust decision and is not built in T061.
- **Safety.** Model data never grants permission to control anything. Control still goes through the existing control channel, grants and fingerprint presence. Findings are decision support, not certification to power equipment.
- **Local first.** Everything works offline. Sourcing and AI are optional add-ons that write into the same model with clear provenance.

## 12. What exists today vs what is planned

| Part | Status |
| --- | --- |
| This design | Written (T061) |
| Objects, kinds, properties with evidence and units, ports, connections, relationships, placement, lifecycle with checked moves, design layers, change log with as-of rebuild, local SQLite store | Implemented in T061 (`src/MonitorApp.Model`), with tests against real SQLite |
| Bridge from peers and observed devices to model objects | Implemented in T061; not yet wired into the running node or the UI |
| Starter kind and property libraries | Implemented in T061 (small; meant to grow) |
| Connector library and connection, rack, pedalboard, physical, audio and project engines | Exist in T301 (PR #12, not merged); move onto model ids in a follow-up |
| Signal/protocol library unification | Planned |
| RF engine | Planned |
| Reasoning layer (section 5): constraint evaluation, chain propagation, requirement derivation, suggestion, explanation | Planned; T301 engines are the first constraint bundles. Built after the model lands, on the model's ids |
| Rules beyond existing alerts; rule run history | Planned (existing alerts keep working) |
| Requirements matching and sourcing adapters | Planned |
| Sync of the model between devices | Planned; needs Wilson's trust decision |
| Views described in section 6 and 8 | Not started on this model; the UI thread owns the UI |
