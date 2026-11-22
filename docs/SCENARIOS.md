<!-- Supplied by Wilson on 2026-11-22. Kept verbatim. Wilson's framing: "scenario examples of SOME but not all ... of what I expect from this app". `docs/ARCHITECTURE-MODEL.md` walks each scenario through the shared system model. -->

# Monitor App — Scenario Storyboard

These scenarios illustrate the intended breadth, behavior, and user experience of Monitor App. They should guide both architecture and UI design.

The application should be capable of assisting with real, complicated, interconnected problems without requiring a separately hard-coded workflow for every situation.

## Scenario 1: Designing a Speaker System Around Existing Equipment

I want to put together another speaker system, preferably using equipment I already own.

I create a project called "Dorm Speaker Setup."

The application knows what amplifiers, speakers, cables, and other equipment I already have in inventory. I can browse them visually, inspect their specifications, and drag or assign them into my proposed system.

I select an amplifier, but there's a complication: it needs to fit into a particular enclosed space with limited ventilation.

I enter the available dimensions or construct the surrounding enclosure visually.

The app compares the physical dimensions of the amplifier against the space. It examines ventilation requirements, heat dissipation, expected electrical consumption, and the orientation of its cooling system.

It might determine that the amplifier physically fits, but that its cooling arrangement is unsuitable for the enclosure.

Rather than simply displaying a warning, the interface could illustrate why: blocked ventilation paths, insufficient clearance, or areas where heat may accumulate.

I can experiment with turning the amplifier, repositioning it, adding ventilation, choosing a more efficient amplifier, or changing the enclosure.

Meanwhile, the app examines speaker impedance, amplifier ratings, power requirements, and connections.

If no equipment I already own is appropriate, I can search for suitable alternatives based on the actual requirements of the design.

The result is a complete system proposal, with physical layout, electrical connections, estimated costs, and a list of anything I still need.

Importantly, the app should distinguish a verified incompatibility from an engineering estimate or an unknown specification.

## Scenario 2: Designing an Equipment Rack From Scratch

I want to reorganize my server and audio equipment into a rack.

I open the physical organization workspace and create a new rack assembly.

I choose a standard 19-inch rack, specify its height and depth, and configure relevant structural details.

A visual model appears.

I can add my existing server, UPS, network switch, audio equipment, storage equipment, shelves, and cable-management hardware.

Each object has meaningful dimensions, ports, weight, mounting requirements, and other relevant properties.

As I move equipment around, the application evaluates the consequences.

A server may occupy only 2U but extend too far behind the rails.

A UPS may be better placed toward the bottom because of its weight.

A shelf might fit mechanically but obstruct access to rear connectors.

A piece of equipment may block another device's ventilation.

Power cables and data connections need space behind the rack.

I should be able to rotate the view, inspect the rear, temporarily hide equipment, and pull assemblies apart to understand how things fit together.

Connections can be drawn or routed between real ports on real components.

The app can estimate cable lengths, suggest routing corridors, and flag possible interference or clearance problems.

I can switch between physical, power, thermal, and signal-oriented visualizations of the same arrangement.

The application should also accommodate unusual equipment, custom mounting adapters, nonstandard dimensions, and incomplete specification data.

The finished design can produce a mounting plan, connection plan, bill of materials, and outstanding issues.

## Scenario 3: Building a Bass Pedalboard and Wireless Audio System

I want to put together a compact bass performance setup using my existing equipment.

I open a pedalboard workspace.

I place my bass signal source, wireless equipment, POD Go, IEM system, power supplies, and any additional effects into the design.

The board appears as a spatial arrangement, but I can also inspect its audio signal chain.

I connect the bass through its wireless receiver into the POD Go, then route the processed audio toward an IEM transmitter and receiver.

The application understands the difference between instrument-level signals, line-level outputs, headphone signals, balanced and unbalanced connections, mono and stereo configurations, and the appropriate connectors.

If I try to create a wireless arrangement that cannot deliver the processed audio I want, the application should explain the actual signal-routing problem.

I can experiment with equipment arrangements and see how much room cables and connectors need.

The application also maintains a model of the radio systems.

I can enter the frequencies or bands used by my wireless devices and see how they relate to one another.

Where supported by connected hardware or external data, the app could display measured RF activity, potential channel conflicts, or available operating channels.

It could warn about overlapping channels, possible interference, regional frequency restrictions, or incompatible wireless technologies.

The app should distinguish measured RF conditions from predictions.

The final result is a physical board layout, audio signal diagram, wireless frequency plan, power requirements, and any missing equipment.

## Scenario 4: Finding a Product When I Don't Know What Product I Need

I'm working on a project and discover that I need a component with particular capabilities.

I don't necessarily know the product category, manufacturer, or even whether an obvious commercial solution exists.

Instead of searching by product name, I describe the actual engineering need.

For example:

"I need a compact device that accepts this audio signal, provides two suitable outputs, has sufficiently low latency for live playing, preserves bass frequencies, and runs from my existing power system."

The application already knows the surrounding system, including available signal types, power sources, physical space, and existing equipment.

It turns those constraints into structured product requirements.

It can search permitted retailer sources, manufacturer specifications, catalogs, or available integrations.

Potential products appear with relevant specifications, prices, images, availability, and compatibility information.

I can compare candidates based on their ability to solve my actual problem.

A product might be inexpensive but fail the latency requirement.

Another might handle the audio correctly but require a different power supply.

Another might work electrically but not fit inside the available enclosure.

I can inspect why an option qualifies or fails, including which claims come from verified specifications and which are uncertain.

I select a suitable option and add it to my project as a planned component.

The application then updates the design, connections, budget, and purchasing requirements.

I can set a price or stock alert without leaving the project.

## Scenario 5: Exploring and Managing My Home Server System

I open Monitor App and see my actual computing environment.

My devices and services exist within a living representation of their logical relationships.

I can see my main server, secondary laptop node, network infrastructure, storage, and self-hosted services.

Rather than browsing a flat collection of monitoring tiles, I can follow relationships.

I select Syncthing.

The interface highlights the devices participating in synchronization, their relevant storage, and the network connections involved.

A transfer begins.

The connection becomes active, and I can follow the transfer visually from one device to another.

I follow it into the server and inspect the relevant storage subsystem.

The interface reveals drive activity, throughput, utilization, and any available hardware state.

I can then move back outward to see how this activity fits into the rest of the system.

I select the server and expand its architecture.

I see the physical hardware, storage organization, containers, software services, and related resources, each represented at an appropriate level of detail.

Some components have live telemetry; others exist because I entered them into inventory.

I can inspect or perform authorized operations on supported devices and services.

The system is not just displaying measurements. It is providing a navigable explanation of what exists, how it connects, and what it is doing.

## Scenario 6: Diagnosing a Problem by Replaying History

I receive a notification that a service has become unavailable.

I open the notification.

The application takes me to the affected part of my system and highlights the problem.

I can see the service, its host device, relevant networking, dependencies, and recent events.

Instead of manually searching through multiple unrelated charts, I move backward through time.

The schematic reconstructs the known historical state.

I discover that the network connection became unstable several minutes before the service stopped responding.

I continue backward and notice that another device or service changed state around the same time.

I can compare before and after, inspect exact event logs, and open detailed telemetry charts when necessary.

If the relevant data exists, the app can show whether a device restarted, lost power, changed addresses, disconnected from the overlay network, or exhausted a resource.

It should not invent causality simply because events occurred close together.

After identifying a likely issue, I can create an automation, initiate an authorized recovery action, or annotate the incident.

The historical view should apply to the broader system model, not just individual numeric measurements.

## Scenario 7: Building a Multi-Part Project Over Time

I decide to upgrade my home server.

I create a project with objectives, technical constraints, budget, and desired capabilities.

The project contains multiple assemblies and tasks: storage expansion, power distribution, networking, physical installation, and software configuration.

I add existing inventory, planned purchases, and equipment I haven't decided on yet.

I can visually inspect the proposed final system before purchasing anything.

When I investigate a particular component, I can see which requirements it satisfies and which other components depend on it.

A drive might require a compatible controller, cable, mounting arrangement, and sufficient power.

These dependencies contribute to the project plan and bill of materials.

I can compare design alternatives without destroying the original proposal.

I choose components, track prices, make purchases, mark deliveries, and assign hardware to assemblies.

As the build progresses, components change from proposed to acquired to installed.

Eventually, supported devices come online and begin reporting actual state.

The project doesn't disappear once installation is complete.

Its physical organization, documentation, component history, configuration, and monitoring continue to exist within the same workspace.

If I upgrade it again, I can work from the current system rather than rebuilding its representation from scratch.

## Scenario 8: Understanding a Complicated Connection

I have two devices I want to connect, but they use different interfaces.

I select an output port on one and an input port on the other.

The application recognizes the physical connectors and available signal specifications.

It evaluates whether the connection makes sense.

For example, both devices may use USB-C, but one might require DisplayPort Alt Mode that the other doesn't support.

Or two audio devices may have physically compatible 1/4-inch connectors but incompatible signal levels.

Or a power connector may fit while supplying an unsafe voltage or polarity.

The app can show the actual reason a connection is valid, invalid, or uncertain.

If an adapter or active converter is needed, I can insert one into the connection chain.

The application reevaluates the full path rather than assuming that matching plugs means compatibility.

I can inspect the cable, its endpoints, relevant protocols, signal direction, and supported capabilities.

The same connection exists in the schematic, the physical layout, and the equipment records.

This should rely on a general extensible connection model, not special logic written separately for every example.

## Scenario 9: A Problem That Crosses Physical and Digital Systems

My server has been behaving strangely.

Monitoring shows that its storage performance periodically collapses.

I inspect the storage subsystem and its historical activity.

I can see which drives, controller, services, and workloads are involved.

Then I switch to the physical organization of the server rack.

I notice that the affected equipment is in an area with poor ventilation and that the recorded temperatures rose during the same periods.

The application lets me explore a possible thermal explanation.

I can compare the timing of temperature changes and performance degradation, inspect the equipment's ventilation characteristics, and experiment with alternative rack arrangements.

For example, I move a heat-producing device elsewhere in the proposed layout and examine whether that creates better clearances and expected airflow.

The application could recommend checking a fan, relocating equipment, or improving airflow, but it should distinguish predicted improvements from measurements.

This scenario demonstrates why the monitoring, spatial design, inventory, and engineering systems should not be isolated.

A real operational problem can lead directly into a physical design investigation.

## Scenario 10: Moving Between Devices and Levels of Detail

I'm away from home and receive a low-battery notification from one of my devices.

I open the Android application.

The affected device is immediately identifiable in a compact representation of my system.

I can inspect its battery history, charging behavior, connectivity, and related automations.

If a supported control operation is available, I can execute it with appropriate authorization.

Later, I sit at my computer.

The larger interface exposes a much richer version of the same information, including full system relationships, historical comparisons, and relevant hardware properties.

I can explore the device's physical and logical relationships without having to reenter information.

On mobile, the interface prioritizes quick understanding, notifications, essential navigation, and practical actions.

On desktop, it can support extensive design and editing workflows.

The product should remain coherent across platforms without requiring every interface to have the same layout.

## Scenario 11: An Automation That Spans Projects and Live Systems

I'm planning a storage upgrade but don't want to purchase the components immediately.

The project already contains several acceptable drive options with specifications, prices, and retailer references.

I create a rule:

"Notify me when a compatible drive reaches my target price, provided I haven't already purchased enough drives for this project."

The rule is associated with the project rather than an unrelated shopping list.

Later, an eligible price change is detected.

The application checks the rule conditions and creates a notification.

I open it and immediately see the candidate drive, its price history, which project requires it, and how it compares with the other options.

After purchasing it, I update the project.

The inventory and remaining purchasing requirements change accordingly.

The rule no longer needs to treat the purchased quantity as outstanding.

Similarly, I should be able to make rules involving device status, service failures, power conditions, network changes, and other events.

The automation system should be generic, with understandable triggers, conditions, actions, and event history.

## Scenario 12: Using the Application Simply Because It's Interesting to Explore

Not every interaction has to be attached to an immediate problem.

Sometimes I just want to open the application and explore my systems.

I might select a network connection and watch its traffic.

I might expand a server to see its components and services, then pull the pieces apart into an exploded representation.

I could follow an audio signal from an instrument through a chain of equipment, showing the relevant processing and connections.

I could rotate an equipment rack, reveal the cabling behind it, and inspect how everything fits together.

I could rewind time and watch devices disconnect, reconnect, start services, or change state.

I could open a proposed project and see how its planned system would fit together, even though it hasn't been built yet.

There should be room for polished animations, tactile interactions, responsive visual effects, and imaginative presentation.

The application should sometimes feel closer to a carefully designed interactive simulation than a conventional administration tool.

However, it must always remain clear which information is measured, which is modeled, and which is only a visual representation.

The purpose is not to introduce arbitrary visual complexity. It is to make complicated systems intuitive, engaging, and satisfying to understand.

---

# What These Scenarios Have in Common

These scenarios are deliberately diverse. They include hardware engineering, audio, electronics, networking, software, project management, shopping, automation, and diagnostics.

The goal is not to implement twelve unrelated workflows.

It is to build sufficiently general foundations that these workflows emerge naturally from the same reusable capabilities.

A device is an object with properties and relationships.

An assembly contains objects and defines how they are physically organized.

A connection relates compatible or potentially incompatible interfaces.

A project describes intended changes and work.

An inventory records owned assets.

A product record provides sourcing and technical information.

An agent or integration supplies real-world observations.

An automation reacts to state or events.

A visualization exposes one or more of these relationships in an intuitive, interactive form.

**The UI should allow the user to move between these contexts without losing the identity of the objects they are working with.**

One object might be represented as a physical component, a node in a network, an element in a signal chain, a project requirement, or a monitored device.

Those are different perspectives on the same underlying system.

Most importantly, don't design everything around the assumption that a user opens the app to look at telemetry.

They might open it because they want to build something, solve a compatibility problem, organize equipment, research a purchase, understand an existing system, investigate a failure, or simply explore.

The resulting application should be capable of all of those experiences without feeling like twelve different programs glued together.
