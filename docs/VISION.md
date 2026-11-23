# Monitor App — Original Scope vs. Expanded Vision

> Product direction from Wilson (the owner), 2026-11-22, recorded verbatim so every agent builds toward it. Where this conflicts with older wording elsewhere, this is the target direction; `Monitor App Design Specification.txt` stays the authority for details it covers. This document states direction, not implementation status: a capability described here is not implemented until code and tests say so.

## 1. The Fundamental Change

Monitor App has undergone a significant expansion in scope. It is important that development reflects the full intended direction of the project, rather than treating newer features as disconnected additions to the original monitoring application.

Original concept: A self-hosted, cross-platform application for monitoring devices, networks, and software services, with historical telemetry, alerts, and limited remote management.

Expanded concept: A unified, self-hosted workspace for representing, designing, organizing, sourcing, monitoring, automating, and managing physical and digital systems.

The key conceptual distinction is:

* Original: Observe and manage an existing system.
* Expanded: Design a system, understand its components and relationships, plan its construction, acquire its parts, monitor its operation, and manage its evolution.

The expanded application is not simply a monitoring dashboard with a rack organizer added to it. These capabilities should share an underlying model of devices, components, connections, resources, projects, and relationships.

## 2. Original Scope — Device and Service Monitoring

The original Monitor App centered on a distributed monitoring architecture.

### Core capabilities

* Cross-platform agents running on Windows, Linux, Android, and other supported systems.
* A self-hosted coordination server.
* Connectivity over Tailscale, Headscale, or equivalent networking infrastructure.
* Device monitoring: battery charge, charging state, power, system utilization, temperature, network connectivity, and available hardware telemetry.
* Network monitoring: connected devices, interface status, addresses, and connection health.
* Device location reporting where supported and authorized.
* Software and service monitoring, initially including Syncthing and Tailscale/Headscale, with additional integrations such as Bitwarden and Pi-hole.
* Persistent background operation and automatic restart.
* Historical telemetry recording, inspection, and replay.
* Configurable alerts, notifications, and thresholds.
* Android widgets and notifications.
* Limited, explicitly authorized remote device/service controls.
* Minimal setup, automated enrollment/configuration, and little manual maintenance.

### Original architectural principles

The system should work without mandatory third-party cloud infrastructure, commercial dashboards, or paid services.

Local monitoring and collection should continue independently of the graphical interface, optional AI systems, or external data providers.

This monitoring capability remains essential. It is the operational foundation of the expanded application, not a feature being replaced.

## 3. Expanded Scope — Systems and Project Engineering

The expansion adds several major areas of functionality.

### A. General-purpose project planning

Monitor App should support planning projects involving multiple pieces of hardware, software, infrastructure, and other components.

Projects should be capable of representing:

* Hierarchical assemblies, subsystems, and individual parts.
* Dependencies between tasks and components.
* Required materials and bills of materials.
* Costs, budgets, and sourcing status.
* Alternatives, compatibility requirements, and design constraints.
* Installation, assembly, configuration, and maintenance tasks.
* Relationships between projects and resources already owned.
* Planned, acquired, installed, active, and retired components.

For example, a home server upgrade should be representable as a project involving storage drives, controllers, cables, power supplies, physical installation requirements, software configuration, and costs.

A project should not have to correspond to an already-running device.

### B. Inventory and hardware awareness

The system should maintain a reusable inventory of physical and digital assets.

Objects can include:

* Computers, servers, mobile devices, and networking equipment.
* Drives, expansion cards, power supplies, adapters, and accessories.
* Audio equipment, instruments, effects processors, interfaces, and wireless systems.
* Racks, cases, enclosures, shelves, and mounting hardware.
* Cables, connectors, splitters, adapters, converters, and power distribution equipment.
* Software installations, services, virtual machines, containers, and network resources.

Assets should have extensible properties, including dimensions, weight, electrical requirements, thermal characteristics, interfaces, costs, and current availability.

Ideally, a single asset can be referenced by several projects or views without duplicating its underlying record.

### C. Comprehensive physical organization and spatial design

A major new capability is a spatial editor for physically organizing equipment.

This must not be limited to conventional server racks.

Supported layouts should eventually encompass:

* Standard 19-inch equipment racks.
* Half-width and other rack formats.
* Eurorack modular synthesizer systems.
* Pedalboards and audio effect chains.
* Desktop arrangements and equipment shelves.
* Enclosures, cases, cabinets, and custom layouts.
* General physical assemblies with configurable measurements.

The spatial model needs more than a 2D list of equipment occupying rack units.

It should consider:

* Height, width, and depth.
* Mounting standards and hole spacing.
* Front/rear mounting positions.
* Rail placement and mounting depth.
* Equipment weight and distribution.
* Structural clearance and physical collisions.
* Airflow, ventilation, and thermal behavior.
* Heat generation and cooling requirements.
* Power distribution and consumption.
* Cable routing, length, bend radius, and clearance.
* Physical accessibility and maintenance space.
* Electromagnetic interference (EMI/EMF) concerns.
* Signal integrity and interference risks.
* Connections and wiring between mounted equipment.

These considerations should be modeled as useful engineering properties wherever practical, not treated as purely visual decorations.

The editor should eventually make it possible to identify potential physical incompatibilities and design problems before assembling the equipment.

### D. Universal connections and compatibility

An especially important expansion is a comprehensive model of connectors, interfaces, ports, and signals.

The application should aim to support as many relevant connector families as reasonably possible, including:

* USB variants, capabilities, data protocols, and power delivery.
* Video interfaces such as HDMI, DisplayPort, DVI, and VGA.
* Ethernet, fiber, and network equipment connections.
* AC and DC power connectors, including voltage, polarity, and current constraints.
* Audio connectors such as XLR, TS, TRS, TRRS, RCA, Speakon, and MIDI.
* Analog and digital audio standards.
* Instrument, line, microphone, speaker, and headphone signal levels.
* Modular synthesizer connections including Eurorack power, CV, gate, and audio.
* Computer expansion and storage interfaces.
* Wireless and logical connections where applicable.
* Adapters, converters, splitters, hubs, and intermediate devices.

Critically, physical connector compatibility is not the same as electrical, protocol, or functional compatibility.

The underlying model should distinguish connector shape, pinout, signal type, direction, operating limits, negotiated capabilities, and any necessary active conversion.

A cable that physically fits should not automatically be considered safe or functional.

The broader aim is to let users model connection chains across electrical, audio, computer, network, and other systems, and to surface compatibility problems or uncertainties.

The architecture should be extensible rather than relying on a small hard-coded connector list.

### E. Web-based component research and sourcing

Another significant addition is component sourcing and purchasing research.

The application should eventually support:

* Finding products through retailer APIs and permitted web scraping.
* Importing product information and specifications.
* Retrieving product images.
* Comparing prices between retailers.
* Tracking price history.
* Monitoring availability and stock status.
* Detecting relevant product changes.
* Creating configurable price alerts.
* Associating purchase options with project components and inventory.
* Comparing alternative parts based on requirements.
* Keeping a bill of materials synchronized with sourcing decisions.

For example, a user designing an equipment rack should be able to identify the hardware needed, review compatible candidates, compare retailers, and monitor purchase opportunities.

External product information should supplement the local application, not become a mandatory dependency for basic operation.

### F. Customizable automations and interactions

The original app already included the idea of notifications and alert thresholds.

The expansion generalizes this into a flexible rules and automation system.

Potential triggers include:

* Device disconnections.
* Low battery or power problems.
* Service failures.
* Excessive temperatures.
* Storage thresholds.
* Product price drops.
* Restocked components.
* Changes in system topology.
* Project deadlines or dependencies.
* Other measurable state changes or external events.

Actions may include notifications, historical annotations, state changes, authorized management commands, or updates to related project objects.

The goal is not a fixed collection of predetermined alerts. It is an extensible system for user-configurable behavior.

Automation must remain permissioned, observable, and safe.

### G. Distributed agents and optional intelligence

The original agent/server architecture remains, but its purpose broadens.

Lightweight device agents should handle basic telemetry, discovery, and supported control operations.

More capable machines may host additional coordination, analysis, or optional AI functionality.

The broader design may support AI-assisted interpretation of system information, project planning, compatibility reasoning, and sourcing.

However:

* AI must not be required for ordinary monitoring.
* Basic functionality must remain available offline.
* Low-power devices should not require expensive computation.
* External AI providers should never become mandatory.
* The underlying deterministic application model must remain useful without AI.

AI is an enhancement to the platform, not its foundation.

## 4. The UI Has Also Changed Fundamentally

The original concept could have been implemented as a relatively conventional monitoring dashboard.

That is no longer the intended design.

The preferred visual direction combines two complementary ideas:

Living schematic: A dynamic representation of equipment, services, connections, and ongoing activity.

Exploded architecture: The ability to inspect systems at multiple levels, revealing subsystems, internal relationships, and individual components.

A user should be able to move naturally between high-level systems and component-level detail.

For example:

A network overview → a particular server → its hardware components → a storage drive → its interfaces → its connection to a controller → its relationship to a larger project.

The UI should be highly interactive, visually distinctive, and enjoyable to explore.

Interaction does not always need to serve an immediate productivity purpose. Playful motion, transitions, physical representations, and visually compelling behavior are welcome when they contribute to a coherent experience.

However, the application should not degrade into an ordinary dashboard with excessive animation.

### Time-travel is a core feature

Historical state inspection and replay should be fundamental architectural capabilities.

The user should be able to explore how systems and relationships changed over time, not simply view graphs of past measurements.

This eventually extends to topology, component state, connectivity, and other recorded changes where historical information is available.

### UI customization

The application should have a strong, intentional design identity rather than letting arbitrary configuration determine every aspect of its appearance.

Constrained customization is desirable, but avoid reducing the product to a generic drag-and-drop dashboard or workspace.

The user has specifically favored the living schematic/exploded architecture combination over conventional instrument dashboards, atlas-style navigation, and focus/periphery approaches.

## 5. How These Capabilities Should Work Together

The important architectural change is that the expanded features should be integrated.

Consider a project to build an audio equipment rack.

The user should eventually be able to:

1. Create a project describing the desired rack and its requirements.
2. Add existing equipment from inventory and proposed equipment not yet purchased.
3. Arrange the equipment spatially, accounting for dimensions, mounting, cooling, power, and accessibility.
4. Define the audio, power, data, and control connections between components.
5. Identify missing adapters, cables, mounting hardware, or incompatible interfaces.
6. Generate a consolidated list of required parts.
7. Research suppliers and monitor prices.
8. Track purchases and mark components as installed.
9. Link supported installed equipment to actual devices, agents, or monitored services.
10. Observe the resulting live system through the same underlying representation.
11. Investigate historical changes, notifications, and problems.
12. Continue upgrading or reorganizing the system without discarding the original project model.

That is the intended long-term integration.

Design-time representation and runtime observation are two different capabilities operating on the same system model.

A proposed component can exist without being physically installed. An installed component can exist without reporting live telemetry. Monitoring data can later enrich an existing modeled asset without replacing its identity.

## 6. The New Architectural Requirements

The original architecture primarily needed devices, agents, services, metrics, events, and commands.

The expanded architecture requires a more general underlying representation.

Important concepts include:

* Entities: Devices, parts, services, projects, assemblies, locations, resources, and other objects.
* Relationships: Contains, connects to, depends on, powers, communicates with, mounted in, belongs to project, and similar relationships.
* Properties: Dimensions, capabilities, technical specifications, costs, status, and custom metadata.
* Connections: Typed ports, interfaces, cables, signals, and logical links.
* States: Proposed, owned, installed, online, offline, failed, retired, and related lifecycle states.
* Observations: Live and historical measurements collected from agents or integrations.
* Events: Changes in state, alerts, discoveries, and other notable occurrences.
* Rules: Conditions and actions governing automation.
* External references: Products, retailers, specifications, APIs, and sourcing records.

These need not be implemented as a single database table or an overly abstract framework. The important point is that they should be interoperable and extensible.

The architecture should prevent the rack editor, project planner, inventory manager, connection registry, sourcing engine, and monitoring system from evolving into completely isolated applications.

At the same time, each subsystem should have clear boundaries and be capable of useful independent operation.

## 7. Development Priorities and Scope Management

This expanded direction is the long-term product scope, not a claim that every capability already exists or should be implemented simultaneously.

The agreed direction is incremental:

### Stage 1 — Functional monitoring foundation

Deliver useful, reliable device/service monitoring with agents, server, history, alerts, and basic management.

### Stage 2 — Inventory and physical organization

Add meaningful asset representation, equipment inventory, rack occupancy, and connections between known components.

### Stage 3 — General-purpose design and planning

Expand physical layouts, project planning, spatial constraints, component relationships, compatibility, and bills of materials.

### Stage 4 — Research, optimization, and advanced intelligence

Integrate sourcing, price tracking, deeper engineering analysis, optional AI, optimization, and more advanced automation.

These stages are priorities rather than rigid prohibitions on parallel development. Independent features can progress concurrently as long as they respect shared architectural contracts.

Also distinguish implemented and tested capabilities from scaffolding, prototypes, partially integrated features, and long-term proposals.

Do not describe a feature as complete merely because the data structures or UI mockup exist.

## 8. Expectations for Continued Development

Future implementation should follow these principles:

1. Preserve the original monitoring mission. Do not sacrifice reliable telemetry and services in pursuit of the expanded capabilities.
2. Design for the expanded scope. Avoid short-term decisions that unnecessarily prevent the broader model from working.
3. Keep data interconnected. Inventory, projects, physical layouts, connectivity, monitoring, and sourcing should reference shared concepts.
4. Make the UI exceptional. The interface should be more than a functional collection of forms and conventional dashboards.
5. Support multiple physical disciplines. Do not assume the user only wants computer/server racks; audio, Eurorack, pedalboards, and general hardware organization are explicit parts of the scope.
6. Consider engineering constraints comprehensively. Physical fit is only one consideration; account for electrical, thermal, mechanical, signal, accessibility, and other relevant concerns.
7. Prefer extensibility. Connector types, hardware properties, monitoring integrations, and project structures should be expandable without fundamental redesign.
8. Maintain a local-first philosophy. External APIs and optional intelligence should augment, not control, the platform.
9. Support meaningful progression. The same system can move from concept to purchased parts, assembly, active operation, and later revision.
10. Keep implementation status accurate. A desired feature is not automatically an implemented feature.

## Final Product Definition

Monitor App is evolving into a self-hosted, cross-platform systems engineering and management environment that connects project planning, hardware inventory, physical design, interconnection modeling, sourcing, live monitoring, historical analysis, automation, and authorized control.

Its defining characteristic is continuity between the conceptual, physical, logical, and operational representations of a system.

The application should let a user understand not only what their systems are doing, but also what those systems consist of, how they fit together, why they are configured that way, what they cost, how they could be improved, and how they have changed over time.

This is the target direction for ongoing architectural and UI decisions. It is broader than the original monitoring app, but it should remain one cohesive product rather than a disconnected suite of tools.

## Clarifications

Wilson, 2026-11-22, verbatim:

You're getting closer, but I think you're still underestimating both the scope of the application and, especially, what that means for the UI.

Your description makes it sound like we're replacing a conventional monitoring dashboard with a much more elaborate monitoring schematic, then attaching a few project management features to it. That's not quite the vision.

Here are the important things you're missing or underemphasizing:

### 1. The app is as much about creating and manipulating systems as observing them

The user should not just navigate through a representation of existing devices. They should be able to construct, arrange, modify, connect, and experiment with systems that may not exist yet.

For example, I should be able to open an empty rack, populate it with equipment I own or am considering, rearrange it, wire everything together, examine constraints, and determine what I need to purchase.

The same applies to a pedalboard, Eurorack system, network setup, or any other supported assembly.

The UI must be an interactive design environment, not just a visualization.

### 2. The living schematic and exploded architecture are two complementary ideas

You've emphasized the schematic, but not the exploded architecture nearly enough.

I want to be able to peel apart a system visually, explore its subsystems, understand its internals, and manipulate its constituent parts.

This should have a strong spatial and visual identity. Zooming into a server and seeing its components isn't merely opening another list of hardware. Ideally, it feels like opening up the machine.

Likewise, physical organization shouldn't just be represented by boxes on a network map.

A rack should look and behave like a rack. A pedalboard should resemble a physical pedalboard. Equipment, connections, relative dimensions, and spatial relationships should actually matter.

And while the different views should feel interconnected, that doesn't mean every activity needs to be squeezed into a single infinite canvas.

### 3. You're underemphasizing how extensive the engineering functionality is

The physical organization tools are not meant to be simple equipment placement utilities.

They should eventually account for everything reasonably relevant to the system being designed:

* Mechanical: height, width, depth, clearances, mounting standards, weight, stability, and accessibility.
* Electrical: voltage, current, wattage, power distribution, grounding, and connector compatibility.
* Thermal: airflow, cooling, heat generation, and equipment placement.
* Signal: signal types, direction, electrical levels, EMI, interference, and conversion requirements.
* Connectivity: cable routing, physical ports, protocol compatibility, adapters, and connection chains.

And this applies to computer hardware, networking, audio equipment, Eurorack, pedalboards, and other systems.

An important requirement is an extensible library of connectors, protocols, and equipment characteristics, rather than a limited set of predetermined hardware types.

When I move something around or connect two components, the app should understand enough about those objects to respond intelligently.

### 4. Project planning and sourcing are much bigger than showing a parts list

Project planning is its own significant capability.

I want to be able to organize complicated projects with multiple assemblies, requirements, dependencies, alternative designs, existing inventory, proposed purchases, budgets, and implementation steps.

The sourcing functionality isn't just displaying a price beside a component. It involves retailer research, product discovery, specifications, images, price histories, availability, comparisons, and automated alerts.

Ideally, there's continuity between researching a component, deciding to use it, adding it to a design, purchasing it, installing it, and eventually monitoring it.

These should be natural operations within the UI, not disconnected pages with superficial links between them.

### 5. Automation and control are being largely forgotten

The application isn't exclusively observational.

I want user-configurable interactions and automations triggered by many different kinds of events.

Disconnected devices, low batteries, service failures, price changes, product availability, project conditions, and other events should all be usable as triggers.

Notifications are one action, but authorized device control, service management, and other configurable operations should be part of the model.

This needs to be visibly integrated into the app, so users can inspect what a rule does, why it triggered, and what happened.

### 6. The visual ambition is considerably higher than your description suggests

This is especially important.

I don't just want a polished schematic with nice icons and animated connections.

I've specifically expressed interest in an interface that can be playful, highly interactive, and almost game-like, even when some interactions exist partly because they're enjoyable.

I want to explore visually interesting ways for objects to respond, unfold, move, connect, separate, and reveal information.

The system should feel alive and tangible, rather than like a typical enterprise dashboard or a basic network diagram.

That doesn't mean sacrificing functionality for decoration. It means putting considerably more imagination into how users interact with the underlying information.

The living schematic is one part of that visual language, not the entire UI specification.

### 7. History is more than a slider replacing a tab

Time-travel should let me explore historical system states and relationships, including changes in connectivity, configuration, telemetry, and other recorded events.

But that doesn't eliminate the usefulness of detailed charts, event logs, comparisons, and historical analysis.

A time slider is one interaction for accessing history, not the complete feature.

### 8. The most important distinction: the app should have multiple ways to interact with the same underlying reality

Consider a single audio interface.

I might encounter it as:

* A device reporting live telemetry.
* A physical object mounted in a rack.
* A collection of ports, cables, and signals.
* A component in a larger audio system.
* An asset in my inventory.
* An item assigned to a project.
* A product with specifications, documentation, and purchase information.
* A historical object whose connections or state have changed.

These should all be different representations and interactions involving the same object, with appropriate transitions between them.

This is much more ambitious than organizing everything around a network topology view.

### What I want you to take away

The original app was primarily a device and service monitoring application.

The intended app is a visually rich, interactive environment for building, understanding, organizing, researching, operating, and evolving complex systems.

The monitoring functionality remains important, but it is now just one of several major capabilities.

Please use this distinction when making architectural decisions and designing the UI. I don't want us to finish a fancy dashboard and then discover that the rest of the application needs to be built around a completely different interaction model.

Also, I don't want to defer all progress toward this direction until the current fixes are complete. Independent work on the underlying models, project planning, component libraries, sourcing, and physical organization can proceed alongside maintenance as long as it doesn't conflict with the other agent's work.

The main thing I want to avoid is treating this expanded vision as a roadmap of additional screens. It should fundamentally change how we think about the application's structure and the interactions it enables.
