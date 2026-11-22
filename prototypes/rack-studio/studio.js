'use strict';
/*
 * Rack Studio — a standalone interactive design prototype.
 * It reads no device telemetry and makes no network requests. Engineering calculations are
 * advisory only; the C# planning rules are the intended authoritative domain implementation.
 */
(function () {
  const STORAGE = 'monitor-rack-studio-v1';
  const $ = id => document.getElementById(id);
  const FORMAT = {
    '19': { label: '19″ EIA', width: 482.6 },
    '10': { label: '10″ EIA', width: 254 },
    '23': { label: '23″ EIA', width: 584.2 },
    '21': { label: '21″ Open Rack', width: 533.4 },
    euro: { label: 'Eurorack', width: 426.72 },
    custom: { label: 'Custom', width: 480 }
  };
  const CATALOG = [
    { id: 'sg250', name: 'Cisco SG250', subtitle: 'Managed switch · editable fixture', category: 'network', glyph: '≋', format: '19', u: 1, width: 440, depth: 242, rear: 90, watts: 25, heat: 25, weight: 3, airflow: 'unknown', source: 'assumed' },
    { id: 'ups', name: 'Rack UPS', subtitle: 'Battery backup · 2U fixture', category: 'ups', glyph: '▣', format: '19', u: 2, width: 440, depth: 405, rear: 70, watts: 36, heat: 36, weight: 20, airflow: 'passive', source: 'assumed' },
    { id: 'server', name: 'Storage server', subtitle: 'Chassis · 2U fixture', category: 'storage', glyph: '▤', format: '19', u: 2, width: 440, depth: 470, rear: 90, watts: 210, heat: 185, weight: 15, airflow: 'front-rear', source: 'assumed' },
    { id: 'patch', name: '24-port patch panel', subtitle: 'Network termination · 1U', category: 'network', glyph: '≡', format: '19', u: 1, width: 440, depth: 95, rear: 135, watts: 0, heat: 0, weight: 2, airflow: 'passive', source: 'assumed' },
    { id: 'pdu', name: 'Power strip / PDU', subtitle: 'Power distribution · 1U', category: 'ups', glyph: '⌁', format: '19', u: 1, width: 440, depth: 180, rear: 95, watts: 0, heat: 0, weight: 2, airflow: 'passive', source: 'assumed' },
    { id: 'shelf', name: 'Equipment shelf', subtitle: 'Shelf · 1U mounting', category: 'storage', glyph: '▰', format: '19', u: 1, width: 440, depth: 350, rear: 35, watts: 0, heat: 0, weight: 4, airflow: 'passive', source: 'assumed' },
    { id: 'euroCarrier', name: 'Eurorack carrier', subtitle: '19″ → 84HP · 3U adapter', category: 'audio', glyph: '▥', format: '19', u: 3, width: 440, depth: 110, rear: 100, watts: null, heat: null, weight: 3, airflow: 'unknown', source: 'assumed', createsZone: true },
    { id: 'eurModule', name: 'Eurorack module', subtitle: '3U · 8HP modular synthesizer', category: 'audio', glyph: '▦', format: 'euro', hp: 8, rows: 1, width: 40.64, depth: 55, rear: 5, watts: 4, heat: 4, weight: .3, airflow: 'passive', source: 'assumed' },
    { id: 'tenSwitch', name: '10″ network device', subtitle: 'Short-width · 1U fixture', category: 'network', glyph: '≋', format: '10', u: 1, width: 215, depth: 175, rear: 55, watts: 16, heat: 16, weight: 1.3, airflow: 'unknown', source: 'assumed' },
    { id: 'twentyThree', name: '23″ telecom panel', subtitle: 'Telecom rack · 1U fixture', category: 'network', glyph: '≡', format: '23', u: 1, width: 530, depth: 130, rear: 70, watts: 0, heat: 0, weight: 4, airflow: 'passive', source: 'assumed' },
    { id: 'blank', name: 'Blanking panel', subtitle: 'Thermal airflow separation · 1U', category: 'storage', glyph: '▬', format: '19', u: 1, width: 440, depth: 4, rear: 0, watts: 0, heat: 0, weight: 0.5, airflow: 'passive', source: 'assumed' }
  ];
  const TASKS = [
    { id: 't1', title: 'Measure rack cavity and rail pitch', status: 'done', depends: [] },
    { id: 't2', title: 'Plan rear cable service loops', status: 'active', depends: ['t1'] },
    { id: 't3', title: 'Verify airflow and inlet orientation', status: 'active', depends: ['t1'] },
    { id: 't4', title: 'Measure RF / AC cable separation', status: 'backlog', depends: ['t2'] },
    { id: 't5', title: 'Check UPS and circuit capacity', status: 'backlog', depends: ['t1'] },
    { id: 't6', title: 'Commit design into actual inventory', status: 'backlog', depends: ['t2', 't3', 't5'] }
  ];

  function itemFromAsset(assetId, zoneId, slot, id) {
    const c = CATALOG.find(x => x.id === assetId);
    if (!c) return null;
    return {
      id, zoneId, assetId, name: c.name, category: c.category, format: c.format,
      slot: slot || 1, leftHp: 1, u: c.u || 1, hp: c.hp || null, rows: c.rows || 1,
      width: c.width, depth: c.depth, rear: c.rear, front: 0,
      watts: c.watts, heat: c.heat, weight: c.weight, airflow: c.airflow,
      shielding: null, cableRoute: null, source: c.source
    };
  }
  function demoState() {
    const placements = [
      itemFromAsset('ups', 'main', 1, 'i-ups'),
      itemFromAsset('server', 'main', 3, 'i-server'),
      itemFromAsset('pdu', 'main', 6, 'i-pdu'),
      itemFromAsset('sg250', 'main', 8, 'i-switch'),
      itemFromAsset('patch', 'main', 9, 'i-patch'),
      itemFromAsset('euroCarrier', 'main', 10, 'i-carrier'),
      itemFromAsset('eurModule', 'zone-i-carrier', 1, 'i-module')
    ];
    placements[6].leftHp = 9;
    return {
      version: 1, scenarioId: 'homelab-rebuild-b', revision: 2,
      currentZoneId: 'main',
      zones: [
        { id: 'main', format: '19', units: 12, depth: 650, width: 482.6, reserve: 70, frontClearance: 30,
          maxWeight: 75, maxPower: 800, maxHeat: 350, hp: 84, rows: 2, rowPitch: 133.35, parent: null },
        { id: 'zone-i-carrier', format: 'euro', units: 3, depth: 80, width: 426.72, reserve: 8, frontClearance: 10,
          maxWeight: 8, maxPower: 100, maxHeat: 80, hp: 84, rows: 1, rowPitch: 133.35, parent: 'i-carrier' }
      ],
      placements,
      tasks: TASKS,
      decisions: [
        { name: 'Cooling path', detail: 'Front-to-rear flow may recirculate behind the rack.', status: 'NEEDS MEASUREMENT' },
        { name: 'Power continuity', detail: 'UPS load budget is not identical to battery runtime.', status: 'ASSUMPTION' },
        { name: 'Electromagnetic isolation', detail: 'RF, audio and high-current cables need route-level evidence.', status: 'UNRESOLVED' },
        { name: 'Mechanical stability', detail: 'Rack anchoring and center of mass need physical confirmation.', status: 'PHYSICAL CHECK' }
      ],
      offers: [
        { product: 'Rack UPS', seller: 'Example Retailer A', price: null, currency: 'USD', status: 'No current offer' },
        { product: 'Eurorack mounting rails', seller: 'Example Retailer B', price: 49.99, currency: 'USD', status: 'Illustrative only' },
        { product: 'Cable management bar', seller: 'Example Retailer C', price: 18.50, currency: 'USD', status: 'Illustrative only' }
      ]
    };
  }

  function clone(value) { return JSON.parse(JSON.stringify(value)); }
  function load() {
    try {
      const parsed = JSON.parse(localStorage.getItem(STORAGE) || 'null');
      if (parsed && parsed.version === 1 && Array.isArray(parsed.zones) &&
          Array.isArray(parsed.placements) && Array.isArray(parsed.tasks) &&
          parsed.zones.some(z => z.id === 'main')) return parsed;
    } catch (_) { /* privacy mode or corrupt local state */ }
    return demoState();
  }
  let state = load();
  let view = 'front';
  let tab = 'rack';
  let selected = null;
  let inspectRack = false;
  let zoom = 1;
  let undo = [];
  let redo = [];
  let search = '';
  let toastTimer = null;

  function h(value) {
    return String(value === null || value === undefined ? '—' : value)
      .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;').replaceAll("'", '&#39;');
  }
  function number(value, fallback) {
    if (value === '' || value === null || value === undefined) return fallback;
    const n = Number(value);
    return Number.isFinite(n) ? n : fallback;
  }
  function nice(value, digits) { return value == null ? '—' : Number(value).toFixed(digits === undefined ? 0 : digits); }
  function zone() { return state.zones.find(z => z.id === state.currentZoneId) || state.zones[0]; }
  function getItem(id) { return state.placements.find(p => p.id === id); }
  function id() { return 'item-' + Math.random().toString(36).slice(2, 10); }
  function save() { try { localStorage.setItem(STORAGE, JSON.stringify(state)); } catch (_) {} }
  function mutate(action) {
    const before = JSON.stringify(state);
    action();
    if (JSON.stringify(state) === before) return;
    undo.push(before); if (undo.length > 60) undo.shift();
    redo.length = 0;
    save(); render();
  }
  function restore(stack, opposite) {
    if (!stack.length) return;
    opposite.push(JSON.stringify(state));
    state = JSON.parse(stack.pop());
    selected = null;
    inspectRack = false;
    save(); render();
  }
  function toast(message) {
    const target = $('toast'); target.textContent = message; target.classList.add('shown');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => target.classList.remove('shown'), 3400);
  }

  function fitsFormat(asset, current) {
    return asset.format === current.format ||
      (asset.id === 'blank' && current.format === '19');
  }
  function footprint(p) { return zone().format === 'euro' ? (p.rows || 1) : (p.u || 1); }
  function position(p) { return zone().format === 'euro' ? p.leftHp : p.slot; }
  function slotLimit(z) { return z.format === 'euro' ? (z.rows || 1) : (z.units || 12); }
  function firstOpenSlot(z, asset) {
    const span = z.format === 'euro' ? (asset.rows || 1) : (asset.u || 1);
    const taken = state.placements.filter(p => p.zoneId === z.id);
    for (let n = 1; n <= slotLimit(z) - span + 1; n++) {
      const overlap = taken.some(p => n < p.slot + (z.format === 'euro' ? p.rows : p.u) &&
                                        n + span > p.slot);
      if (!overlap) return n;
    }
    return 1; // allow deliberate overlap; clearly report it
  }
  function firstOpenHp(z, asset, row) {
    const taken = state.placements.filter(p => p.zoneId === z.id && p.slot === row);
    for (let hp = 1; hp <= z.hp - (asset.hp || 8) + 1; hp++) {
      if (!taken.some(p => hp < p.leftHp + p.hp && hp + asset.hp > p.leftHp)) return hp;
    }
    return 1;
  }
  function addAsset(assetId, desiredSlot, desiredHp) {
    const asset = CATALOG.find(x => x.id === assetId), z = zone();
    if (!asset) return;
    if (!fitsFormat(asset, z)) {
      toast('Mount mismatch: ' + asset.format + ' needs its own mount, shelf or adapter.');
      return;
    }
    const newId = id();
    mutate(() => {
      const slot = desiredSlot || firstOpenSlot(z, asset);
      const next = itemFromAsset(assetId, z.id, slot, newId);
      if (z.format === 'euro') next.leftHp = desiredHp || firstOpenHp(z, asset, slot);
      state.placements.push(next);
      if (asset.createsZone) {
        state.zones.push({
          id: 'zone-' + newId, format: 'euro', units: 3, width: 426.72, depth: 80,
          reserve: 8, frontClearance: 8, maxWeight: 8, maxPower: 100, maxHeat: 80,
          hp: 84, rows: 1, rowPitch: 133.35, parent: newId
        });
      }
    });
    selected = newId; inspectRack = false; renderInspector(); showInspectorOnMobile();
    toast('Placed ' + asset.name + ' in proposed design.');
  }
  function removeSelected() {
    if (!selected) return;
    const removed = selected;
    mutate(() => {
      const nestedIds = state.zones.filter(z => z.parent === removed).map(z => z.id);
      state.placements = state.placements.filter(p => p.id !== removed && !nestedIds.includes(p.zoneId));
      state.zones = state.zones.filter(z => !nestedIds.includes(z.id));
    });
    selected = null; render();
    toast('Removed from design; actual inventory unchanged.');
  }
  function moveSelected(delta) {
    const item = getItem(selected);
    if (!item || item.zoneId !== zone().id) return;
    mutate(() => { item.slot = Math.max(1, Math.min(slotLimit(zone()) - footprint(item) + 1, item.slot + delta)); });
  }
  function openZone(id) {
    if (!state.zones.some(z => z.id === id)) return;
    state.currentZoneId = id; selected = null; inspectRack = false;
    save(); render(); toast('Entered nested mount. ↑ Return to enclosure from inspector.');
  }

  function evaluate(z) {
    const items = state.placements.filter(p => p.zoneId === z.id);
    const findings = [];
    const add = (level, category, title, message, itemId) =>
      findings.push({ level, category, title, message, itemId });
    const isEuro = z.format === 'euro';
    const max = slotLimit(z);
    const occupied = new Set();
    let watts = 0, heat = 0, weight = 0, unknownPower = 0, unknownHeat = 0, unknownWeight = 0;
    let maxDepth = 0;
    items.forEach((p, index) => {
      const span = isEuro ? p.rows : p.u;
      if (p.format !== z.format) add('error', 'Mounting', 'Incompatible standard', p.name + ' uses a different mounting interface.', p.id);
      if (!span || p.slot < 1 || p.slot + span - 1 > max) add('error', 'Geometry', 'Outside mounting grid', p.name + ' exceeds available U or row positions.', p.id);
      if (isEuro && (p.leftHp < 1 || p.leftHp + p.hp - 1 > z.hp)) add('error', 'Geometry', 'Outside HP grid', p.name + ' exceeds available HP.', p.id);
      if (!isEuro && p.width != null && p.width > z.width) add('error', 'Geometry', 'Panel too wide', p.name + ' exceeds measured rack width.', p.id);
      if (p.depth == null || p.rear == null) add('unresolved', 'Depth', 'Unknown body or cable relief', p.name + ' needs both depth and rear connector clearance.', p.id);
      else {
        const extent = p.depth + p.rear + (p.front || 0);
        maxDepth = Math.max(maxDepth, extent);
        if (extent > z.depth - z.reserve) add('error', 'Depth', 'Rear clearance conflict',
          p.name + ' extends ' + Math.round(extent - (z.depth - z.reserve)) + ' mm into reserved service space.', p.id);
      }
      if (p.front != null && p.front < -z.frontClearance) add('error', 'Depth', 'Front door conflict', p.name + ' protrudes beyond front clearance.', p.id);
      if (p.watts == null) unknownPower++; else watts += p.watts;
      if (p.heat == null) unknownHeat++; else heat += p.heat;
      if (p.weight == null) unknownWeight++; else weight += p.weight;
      if (p.airflow === 'unknown') add('unresolved', 'Airflow', 'Flow direction unknown', p.name + ' may draw or exhaust air into an obstructed path.', p.id);
      items.slice(index + 1).forEach(other => {
        const vertical = p.slot < other.slot + (isEuro ? other.rows : other.u) &&
                         p.slot + span > other.slot;
        const horizontal = !isEuro ||
          (p.leftHp < other.leftHp + other.hp && p.leftHp + p.hp > other.leftHp);
        if (vertical && horizontal) add('error', 'Geometry', 'Physical overlap', p.name + ' collides with ' + other.name + '.', p.id);
      });
      for (let n = p.slot; n < p.slot + span; n++) occupied.add(n);
    });
    if (unknownHeat) add('unresolved', 'Thermal', 'Heat model incomplete', unknownHeat + ' item(s) have no heat dissipation estimate.');
    if (unknownPower) add('unresolved', 'Electrical', 'Power model incomplete', unknownPower + ' item(s) have no consumption estimate.');
    if (unknownWeight) add('unresolved', 'Mechanical', 'Weight model incomplete', unknownWeight + ' item(s) have no weight estimate.');
    if (watts > z.maxPower) add('error', 'Electrical', 'Power budget exceeded', watts + ' W is more than the configured ' + z.maxPower + ' W allowance.');
    if (heat > z.maxHeat) add('warning', 'Thermal', 'Cooling capacity exceeded', heat + ' W exceeds the ' + z.maxHeat + ' W fixture heat-removal target.');
    if (weight > z.maxWeight) add('error', 'Mechanical', 'Weight budget exceeded', weight + ' kg exceeds ' + z.maxWeight + ' kg.');
    // Physical phenomena that cannot be qualified from a mounting diagram are surfaced explicitly.
    add('unresolved', 'EMI / RF', 'Coupling has not been characterized', 'No measured cable proximity, RF field strength or shielding/grounding evidence is available.');
    add('unresolved', 'Stability', 'Anchoring & floor loads unknown', 'Cabinet mass, footing, anchored stability and dynamic shelf loads need evaluation.');
    add('unresolved', 'Acoustics', 'Noise and vibration unknown', 'Combined fan noise, resonances and structural transmission require measurements.');
    add('unresolved', 'Thermal', 'No hot-spot measurement', 'A whole-rack heat allowance does not resolve inlet temperature, recirculation or pressure drops.');
    add('unresolved', 'Power', 'Circuit topology unverified', 'Fuse, breaker, grounding, conductor, PDU, UPS and power-factor constraints remain unverified.');
    return { items, findings, watts, heat, weight, occupied, maxDepth, unknownHeat, unknownPower };
  }

  function renderCatalog() {
    const z = zone();
    const filtered = CATALOG.filter(c =>
      (c.name + ' ' + c.subtitle).toLowerCase().includes(search.toLowerCase()));
    $('componentCount').textContent = String(filtered.length).padStart(2, '0');
    $('catalog').innerHTML = filtered.map(c => {
      const match = fitsFormat(c, z);
      return '<div class="asset-card" role="button" tabindex="0" draggable="true" data-asset="' + h(c.id) +
        '" aria-label="Place ' + h(c.name) + '">' +
        '<div class="asset-symbol"><span class="asset-glyph">' + h(c.glyph) + '</span></div>' +
        '<div class="asset-data"><strong>' + h(c.name) + '</strong><span>' +
        h(c.format === 'euro' ? (c.hp + 'HP · 3U') : ((c.u || 1) + 'U · ' + FORMAT[c.format].label)) +
        '</span></div><span class="asset-plus" title="' + (match ? 'Place component' : 'Requires adapter') +
        '">' + (match ? '+' : '↗') + '</span></div>';
    }).join('') || '<div class="library-empty">No matching components.</div>';
  }

  function renderRack() {
    const z = zone(), euro = z.format === 'euro', e = evaluate(z);
    $('rackFormat').value = z.format;
    $('rackHeight').style.display = euro ? 'none' : '';
    $('rackHeight').parentElement.style.display = euro ? 'none' : '';
    $('rackHeight').value = String(z.units);
    $('sceneViewName').textContent = view === 'front' ? 'FRONT ELEVATION' : view === 'side' ? 'DEPTH SECTION' : 'THERMAL ESTIMATE';
    document.querySelectorAll('[data-view]').forEach(b => {
      const on = b.dataset.view === view;
      b.classList.toggle('selected', on); b.setAttribute('aria-selected', String(on));
    });
    $('rackStage').style.transform = 'scale(' + zoom + ')';
    const unitPx = euro ? 132 : 40;
    const widthPx = euro ? Math.min(370, z.hp * 4.5) : 296;
    const heightPx = slotLimit(z) * unitPx;
    const widthScale = widthPx / z.width;
    let markup;
    if (view === 'side') {
      const maxDepth = z.depth;
      markup = '<div class="depth-shell" style="--reserve:' + (100 * z.reserve / maxDepth) + '%">' +
        '<div class="depth-ruler"></div><div class="depth-safe"></div><div class="depth-door"></div><div class="depth-back"></div>';
      e.items.forEach((p, i) => {
        const back = (p.depth == null ? 0 : p.depth) + (p.rear || 0) + (p.front || 0);
        const left = Math.max(0, 100 * (p.front || 0) / maxDepth);
        const width = Math.max(1, 100 * back / maxDepth);
        markup += '<div class="depth-block ' + (p.id === selected ? 'selected ' : '') +
          (back > maxDepth - z.reserve ? 'overrun' : '') + '" data-id="' + h(p.id) + '"' +
          ' style="--left:' + left + '%;--width:' + width + '%;--top:' + (9 + i * 11) +
          '%;--height:27px" title="' + h(p.name) + '">' + h(p.name) + '</div>';
      });
      markup += '<div class="depth-tag">HATCHED = RESERVED CABLE / SERVICE SPACE</div>' +
        '<div class="depth-scale"><span>0 mm</span><span>' + Math.round(maxDepth / 2) + ' mm</span><span>' + maxDepth + ' mm</span></div></div>';
    } else {
      const thermal = view === 'thermal';
      markup = '<div class="rack-shell ' + (thermal ? 'thermal' : '') + '" style="--rack-w:' + widthPx +
        'px;--rack-h:' + heightPx + 'px;--unit-h:' + unitPx + 'px">';
      markup += '<div class="rack-fill"></div>';
      for (let i = 1; i <= slotLimit(z); i++) {
        const b = (i - .5) * unitPx;
        markup += '<div class="rack-unit-label" style="bottom:' + b + 'px">' +
          (euro ? 'R' : 'U') + String(i).padStart(2, '0') + '</div>';
      }
      e.items.forEach(p => {
        const span = euro ? (p.rows || 1) : (p.u || 1);
        const left = euro ? ((p.leftHp - 1) * 5.08 * widthScale) : 0;
        const w = euro ? ((p.hp || 8) * 5.08 * widthScale) : widthPx;
        const b = (p.slot - 1) * unitPx;
        const ht = span * unitPx - 2;
        const conflicting = e.findings.some(f => f.itemId === p.id && f.level === 'error');
        const heatPercent = p.heat == null ? 15 : Math.max(10, Math.min(96, p.heat * .62));
        markup += '<div tabindex="0" role="button" draggable="true" data-id="' + h(p.id) + '"' +
          ' aria-label="Inspect ' + h(p.name) + '" class="rack-item ' + h(p.category) +
          (p.id === selected ? ' selected' : '') + (conflicting ? ' colliding' : '') +
          '" style="--l:' + left + 'px;--b:' + b + 'px;--w:' + w + 'px;--h:' + ht + 'px;--heat:' +
          heatPercent + '%"><span class="rack-screws"></span>' +
          (thermal ? '<span class="heat-bar"></span>' : '') +
          '<span class="device-label">' + h(p.name) + '</span>' +
          '<span class="device-meta">' + (thermal ? h(p.heat == null ? '? W' : p.heat + ' W') :
          (euro ? p.hp + 'HP' : p.u + 'U')) + '</span>' +
          '<span class="device-leds"><i></i><i></i></span></div>';
      });
      markup += '</div>';
      if (!e.items.length) markup += '<div class="empty-rack">EMPTY MOUNTING SPACE<br>CLICK A COMPONENT TO START</div>';
    }
    $('rackVisualization').innerHTML = markup;
    $('rackVisualization').dataset.zone = z.id;
    $('rackVisualization').dataset.unitPx = unitPx;
    $('rackVisualization').dataset.widthPx = widthPx;
    $('rackVisualization').dataset.heightPx = heightPx;
    const used = e.occupied.size;
    $('usedSlots').textContent = used;
    $('totalSlots').textContent = ' / ' + (euro ? z.rows + ' rows' : z.units + ' U');
    $('depthValue').textContent = e.items.length ? Math.round(e.maxDepth) : '—';
    $('depthNote').textContent = 'usable ' + (z.depth - z.reserve) + ' mm after reserve';
    $('powerValue').textContent = e.unknownPower ? '≥' + e.watts : e.watts;
    const blocks = Math.min(42, max);
    $('fillTrack').innerHTML = Array.from({ length: blocks }, (_, i) =>
      '<i class="' + (e.occupied.has(i + 1) ? 'used' : '') + '"></i>').join('');
    const severity = e.findings.filter(f => f.level === 'error').length;
    $('reviewHeadline').textContent = severity ? severity + ' physical conflict' + (severity > 1 ? 's' : '') :
      e.findings.length + ' considerations open';
    $('reviewCount').textContent = String(e.findings.length).padStart(2, '0');
    $('reviewCount').style.color = severity ? 'var(--red)' : 'var(--aqua)';
  }

  function field(label, prop, val, type, suffix) {
    const kind = type || 'number';
    const value = val === null || val === undefined ? '' : String(val);
    return '<label class="inspector-field"><span>' + h(label) + (suffix ? ' · ' + h(suffix) : '') + '</span>' +
      '<input type="' + kind + '" data-field="' + h(prop) + '" value="' + h(value) + '"' +
      (kind === 'number' ? ' step="any"' : '') + '></label>';
  }
  function selectField(label, prop, value, options) {
    return '<label class="inspector-field"><span>' + h(label) + '</span><select data-field="' + h(prop) + '">' +
      options.map(x => '<option value="' + h(x) + '"' + (value === x ? ' selected' : '') + '>' + h(x) + '</option>').join('') +
      '</select></label>';
  }
  function renderInspector() {
    const z = zone(), e = evaluate(z), item = !inspectRack && getItem(selected);
    let markup = '';
    if (tab === 'project') {
      $('inspectorTitle').textContent = 'Scenario details';
      markup = '<div class="inspector-section"><div class="section-kicker">PROJECT SCOPE</div><div class="badge-chip">◉ PROPOSED / NOT ACTUAL</div>' +
        '<p class="caption-copy">This project contains candidate products, parts, tasks and proposed rack geometry. No item becomes owned or installed just because it appears here.</p></div>' +
        '<div class="inspector-section"><div class="section-kicker">SCENARIO / A → B</div><p class="caption-copy">Revision B is editable and stored locally. Export preserves the design without altering Monitor App peer inventory.</p></div>' +
        '<div class="inspector-feature"><strong>Planning ahead, safely</strong><p>Unknown prices and unverified dimensions remain unknown. Your budget is not reduced to an invented zero.</p></div>';
    } else if (tab === 'sources') {
      $('inspectorTitle').textContent = 'Collection architecture';
      markup = '<div class="inspector-section"><div class="section-kicker">ADAPTER BOUNDARY</div><div class="badge-chip">◇ OFFLINE FIXTURE ONLY</div>' +
        '<p class="caption-copy">Each provider will have a separate permissioned adapter. Results keep source time, source quality, currency and offer identity. Rate limits and unavailable results are first-class statuses.</p></div>' +
        '<div class="inspector-section"><div class="section-kicker">DATA CATEGORIES</div>' +
        ['Product definition', 'Seller offer', 'Availability evidence', 'Price history', 'Owned instance'].map(x =>
          '<div class="pipeline-step"><span>' + h(x) + '</span><small>separate identity</small></div>').join('') + '</div>';
    } else if (item) {
      $('inspectorTitle').textContent = 'Equipment inspector';
      markup = '<div class="inspector-section"><div class="section-kicker">SELECTED / ' + h(item.id) + '</div><span class="badge-chip">✳ DESIGN PLACEHOLDER</span>' +
        field('Equipment label', 'name', item.name, 'text') +
        '<div class="field-grid">' +
        (z.format === 'euro' ? field('First HP', 'leftHp', item.leftHp) +
          field('HP width', 'hp', item.hp) : field('Bottom U', 'slot', item.slot) + field('Rack units', 'u', item.u)) +
        '</div>' +
        (z.format === 'euro' ? '<div class="field-grid">' + field('Row', 'slot', item.slot) + field('Rows tall', 'rows', item.rows) + '</div>' : '') +
        '<div class="field-grid">' + field('Body depth', 'depth', item.depth, 'number', 'mm') +
        field('Rear cable gap', 'rear', item.rear, 'number', 'mm') +
        field('Front offset', 'front', item.front, 'number', 'mm') +
        field('Width', 'width', item.width, 'number', 'mm') + '</div>' +
        '<div class="section-kicker">PHYSICAL / LOADS</div><div class="field-grid">' +
        field('Nominal power', 'watts', item.watts, 'number', 'W') +
        field('Heat generation', 'heat', item.heat, 'number', 'W') +
        field('Weight', 'weight', item.weight, 'number', 'kg') + '</div>' +
        selectField('Airflow orientation', 'airflow', item.airflow, ['unknown', 'front-rear', 'rear-front', 'side-side', 'bottom-top', 'passive']) +
        '<div class="inspector-actions"><button class="button button-subtle" id="moveUpButton">↑ Move</button>' +
        '<button class="button button-subtle" id="moveDownButton">↓ Move</button></div>' +
        '<div class="inspector-actions" style="margin-top:9px"><button class="button button-subtle" id="duplicateButton">⊕ Duplicate</button>' +
        '<button class="button button-subtle" id="removeButton">× Remove</button></div>' +
        (state.zones.some(x => x.parent === item.id) ? '<button class="button button-primary" style="width:100%;margin-top:11px" id="enterSubrackButton">↗ Enter embedded Eurorack →</button>' : '') +
        '</div>';
    } else {
      $('inspectorTitle').textContent = 'Rack parameters';
      markup = '<div class="inspector-section"><div class="section-kicker">ACTIVE MOUNTING PLANE</div>' +
        '<span class="badge-chip">◉ ' + h(FORMAT[z.format].label) + '</span>' +
        (z.parent ? '<button class="button button-subtle" style="width:100%;margin-bottom:15px" id="returnToParent">↑ Return to outer rack</button>' : '') +
        '<div class="field-grid">' + field('Usable depth', 'depth', z.depth, 'number', 'mm') +
        field('Rear reserve', 'reserve', z.reserve, 'number', 'mm') +
        field('Front clearance', 'frontClearance', z.frontClearance, 'number', 'mm') +
        field('Measured width', 'width', z.width, 'number', 'mm') + '</div>' +
        '<div class="section-kicker">DESIGN CAPACITIES</div><div class="field-grid">' +
        field('Power allowance', 'maxPower', z.maxPower, 'number', 'W') +
        field('Heat removal', 'maxHeat', z.maxHeat, 'number', 'W') +
        field('Weight limit', 'maxWeight', z.maxWeight, 'number', 'kg') + '</div>' +
        (z.format === 'euro' ? '<div class="field-grid">' + field('HP per row', 'hp', z.hp) +
          field('Row count', 'rows', z.rows) + field('Row pitch', 'rowPitch', z.rowPitch, 'number', 'mm') + '</div>' : '') +
        '<p class="caption-copy">These are design inputs, not manufacturer-verified specs. Front and rear mounting rails, hole patterns and extended drawer loads require additional validation.</p></div>';
    }
    if (tab === 'rack') {
      const pertinent = item ? e.findings.filter(f => f.itemId === item.id || !f.itemId) : e.findings;
      markup += '<div class="inspector-section"><div class="section-kicker">CONSTRAINT AUDIT / ' +
        String(pertinent.length).padStart(2, '0') + '</div><div class="rule-list">' +
        pertinent.map(f => '<div class="rule-row ' + (f.level === 'error' ? 'error' : f.level === 'warning' ? 'warning' : 'unresolved') +
          '"><span class="rule-icon">' + (f.level === 'error' ? '✕' : f.level === 'warning' ? '!' : '?') +
          '</span><div><strong>' + h(f.title) + '</strong><p>' + h(f.message) + '</p></div></div>').join('') +
        '</div></div>';
    }
    if (tab === 'rack' && !item && !inspectRack && !e.items.length) markup += '<div class="inspector-empty"><div class="empty-symbol">◇</div><h4>Build a system.</h4><p>Choose an asset or drop it into the rack to begin.</p></div>';
    $('inspectorContent').innerHTML = markup;
  }

  function renderProject() {
    const tasks = state.tasks;
    const done = tasks.filter(x => x.status === 'done').length;
    $('projectSummary').innerHTML = [
      ['PLANNED COMPONENTS', String(state.placements.length).padStart(2, '0'), 'design-only positions'],
      ['TASK PROGRESS', tasks.length ? Math.round(done / tasks.length * 100) + '%' : '0%', done + ' / ' + tasks.length + ' complete'],
      ['CURRENT SCENARIO', 'B / 02', 'reversible revision'],
      ['SOURCING STATUS', 'UNKNOWN', 'no authorized API connected']
    ].map(s => '<div class="project-stat"><span>' + s[0] + '</span><strong>' + h(s[1]) +
      '</strong><small>' + h(s[2]) + '</small></div>').join('');
    const columns = [
      ['backlog', 'TO DO'],
      ['active', 'IN MOTION'],
      ['done', 'COMPLETED']
    ];
    $('taskBoard').innerHTML = columns.map(([code, title]) => {
      const inCol = tasks.filter(t => t.status === code);
      return '<div class="task-column"><h4>' + title + ' <span>' + String(inCol.length).padStart(2, '0') +
        '</span></h4>' + inCol.map(t => '<button class="task-card" data-task="' + h(t.id) +
        '"><span class="task-mark">' + (code === 'done' ? '✓' : code === 'active' ? '◉' : '○') +
        '</span><strong>' + h(t.title) + '</strong><p>' +
        h(t.depends.length ? 'DEPENDS ON ' + t.depends.join(', ') : 'READY TO WORK') +
        '</p></button>').join('') + '</div>';
    }).join('');
    $('decisionGrid').innerHTML = state.decisions.map(x =>
      '<div class="decision-card"><span>' + h(x.status) + '</span><h4>' + h(x.name) +
      '</h4><p>' + h(x.detail) + '</p></div>').join('');
  }
  function renderSources() {
    $('offersPanel').innerHTML = '<div class="tiny-label">OFFER OBSERVATIONS / ILLUSTRATIVE</div>' +
      state.offers.map(o => '<div class="offer-row"><div><strong>' + h(o.product) + '</strong><small>' +
        h(o.seller) + ' · ' + h(o.status) + '</small></div>' +
        '<div class="offer-price">' + (o.price === null ? '—' : h(o.currency) + ' ' + o.price.toFixed(2)) +
        '<small>NO LIVE QUOTE</small></div></div>').join('') +
      '<div class="source-footer">Every price in this view is a fixture. Real collection must use approved provider APIs and preserve time, source, availability, tax and shipping uncertainty. The backend collector can be added without redesigning the workspace.</div>';
  }

  function render() {
    state.currentZoneId = zone().id;
    const label = tab === 'rack' ? 'RACK STUDIO' : tab === 'project' ? 'PROJECT MAP' : 'SOURCE SIGNALS';
    $('topLocation').textContent = label;
    ['rack', 'project', 'sources'].forEach(x => $(''+x+'Panel').classList.toggle('visible', tab === x));
    document.querySelectorAll('[data-tab]').forEach(b => b.classList.toggle('active', b.dataset.tab === tab));
    $('undoButton').disabled = !undo.length;
    $('redoButton').disabled = !redo.length;
    renderCatalog(); renderRack(); renderInspector(); renderProject(); renderSources();
  }
  function showInspectorOnMobile() {
    if (window.matchMedia('(max-width:1130px)').matches) document.querySelector('.inspector-pane').classList.add('open');
  }

  $('catalog').addEventListener('click', e => {
    const card = e.target.closest('[data-asset]');
    if (card) addAsset(card.dataset.asset);
  });
  $('catalog').addEventListener('keydown', e => {
    const card = e.target.closest('[data-asset]');
    if (card && (e.key === 'Enter' || e.key === ' ')) {
      e.preventDefault(); addAsset(card.dataset.asset);
    }
  });
  $('catalog').addEventListener('dragstart', e => {
    const card = e.target.closest('[data-asset]');
    if (card) e.dataTransfer.setData('application/x-monitor-asset', card.dataset.asset);
  });
  $('assetSearch').addEventListener('input', e => { search = e.target.value; renderCatalog(); });
  document.querySelectorAll('[data-tab]').forEach(b => b.addEventListener('click', () => {
    tab = b.dataset.tab; selected = null; inspectRack = true;
    document.querySelector('.inspector-pane').classList.remove('open'); render();
  }));
  document.querySelectorAll('[data-view]').forEach(b => b.addEventListener('click', () => {
    view = b.dataset.view; renderRack();
  }));
  $('rackVisualization').addEventListener('click', e => {
    const el = e.target.closest('[data-id]');
    selected = el ? el.dataset.id : null;
    inspectRack = !selected;
    renderRack(); renderInspector();
    if (selected) showInspectorOnMobile();
  });
  $('rackVisualization').addEventListener('dblclick', e => {
    const el = e.target.closest('[data-id]');
    if (el && state.zones.some(x => x.parent === el.dataset.id)) openZone('zone-' + el.dataset.id);
  });
  $('rackVisualization').addEventListener('dragstart', e => {
    const el = e.target.closest('[data-id]');
    if (el) e.dataTransfer.setData('application/x-monitor-placement', el.dataset.id);
  });
  $('canvasRegion').addEventListener('dragover', e => {
    e.preventDefault();
    $('canvasRegion').classList.add('drop-active');
  });
  $('canvasRegion').addEventListener('dragleave', e => {
    if (!e.currentTarget.contains(e.relatedTarget)) e.currentTarget.classList.remove('drop-active');
  });
  $('canvasRegion').addEventListener('drop', e => {
    e.preventDefault(); $('canvasRegion').classList.remove('drop-active');
    const asset = e.dataTransfer.getData('application/x-monitor-asset');
    const move = e.dataTransfer.getData('application/x-monitor-placement');
    const shell = $('rackVisualization').querySelector('.rack-shell');
    const z = zone();
    let slot = 1, hp = 1;
    if (shell && view !== 'side') {
      const rect = shell.getBoundingClientRect();
      const offsetFromBottom = rect.bottom - e.clientY;
      const unitPx = Number($('rackVisualization').dataset.unitPx) * zoom;
      slot = Math.max(1, Math.min(slotLimit(z), Math.floor(offsetFromBottom / unitPx) + 1));
      if (z.format === 'euro') {
        const hpPx = (Number($('rackVisualization').dataset.widthPx) / z.hp) * zoom;
        hp = Math.max(1, Math.min(z.hp, Math.floor((e.clientX - rect.left) / hpPx) + 1));
      }
    } else if (view === 'side') {
      toast('Switch to front elevation to position items on the mounting grid.');
      return;
    }
    if (asset) addAsset(asset, slot, hp);
    else if (move) {
      const p = getItem(move);
      if (p && p.zoneId === z.id) {
        mutate(() => { p.slot = slot; if (z.format === 'euro') p.leftHp = hp; });
        selected = move; inspectRack = false; renderInspector();
      }
    }
  });
  $('inspectorContent').addEventListener('change', e => {
    const field = e.target.dataset.field;
    if (!field) return;
    const target = getItem(selected);
    const obj = target && !inspectRack ? target : zone();
    if (!(field in obj)) return;
    const value = e.target.type === 'number'
      ? (e.target.value.trim() === '' ? null : number(e.target.value, null))
      : e.target.value;
    mutate(() => { obj[field] = value; });
  });
  $('inspectorContent').addEventListener('click', e => {
    if (e.target.closest('#returnToParent')) openZone('main');
    else if (e.target.closest('#enterSubrackButton') && selected) openZone('zone-' + selected);
    else if (e.target.closest('#moveUpButton')) moveSelected(1);
    else if (e.target.closest('#moveDownButton')) moveSelected(-1);
    else if (e.target.closest('#removeButton')) removeSelected();
    else if (e.target.closest('#duplicateButton') && selected) {
      const original = getItem(selected), nextId = id();
      mutate(() => {
        state.placements.push(Object.assign({}, original, { id: nextId, slot: original.slot + original.u }));
      });
      selected = nextId; renderInspector();
    }
  });
  $('configButton').addEventListener('click', () => {
    selected = null; inspectRack = true; renderInspector(); showInspectorOnMobile();
  });
  $('rackFormat').addEventListener('change', e => {
    const format = e.target.value, z = zone();
    if (!(format in FORMAT)) return;
    mutate(() => {
      z.format = format;
      z.width = FORMAT[format].width;
      if (format === 'euro') {
        z.hp = z.hp || 84; z.rows = z.rows || 2; z.rowPitch = z.rowPitch || 133.35;
      }
    });
    toast('Mount interface changed. Existing equipment is not silently converted.');
  });
  $('rackHeight').addEventListener('change', e => mutate(() => { zone().units = number(e.target.value, zone().units); }));
  $('zoomIn').addEventListener('click', () => { zoom = Math.min(1.5, Math.round((zoom + .1) * 10) / 10); $('zoomValue').textContent = Math.round(zoom * 100) + '%'; renderRack(); });
  $('zoomOut').addEventListener('click', () => { zoom = Math.max(.6, Math.round((zoom - .1) * 10) / 10); $('zoomValue').textContent = Math.round(zoom * 100) + '%'; renderRack(); });
  $('undoButton').addEventListener('click', () => restore(undo, redo));
  $('redoButton').addEventListener('click', () => restore(redo, undo));
  $('shortcutsButton').addEventListener('click', () => $('helpDialog').showModal());
  $('scenarioSwitch').addEventListener('click', () => toast('Scenario B / Revision 02 — exports preserve this proposal, not actual inventory.'));
  $('resetButton').addEventListener('click', () => {
    if (!window.confirm('Reset this local fixture workspace? Export first to keep changes.')) return;
    mutate(() => { state = demoState(); });
    selected = null; toast('Local demo restored. No actual inventory was affected.');
  });
  $('exportButton').addEventListener('click', () => {
    const payload = JSON.stringify(state, null, 2);
    const blob = new Blob([payload], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url; anchor.download = 'monitor-rack-design-r' + state.revision + '.json'; anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    toast('Proposed design exported as JSON.');
  });
  $('importInput').addEventListener('change', async e => {
    const file = e.target.files[0];
    if (!file) return;
    if (file.size > 1024 * 1024) { toast('Import refused: maximum 1 MB.'); return; }
    try {
      const incoming = JSON.parse(await file.text());
      if (incoming.version !== 1 || !Array.isArray(incoming.zones) ||
          !Array.isArray(incoming.placements) || !Array.isArray(incoming.tasks) ||
          !incoming.zones.some(z => z.id === 'main')) throw Error('Invalid design file');
      mutate(() => { state = incoming; });
      selected = null; inspectRack = true; tab = 'rack'; render();
      toast('Design imported. All data remains proposed.');
    } catch (_) { toast('Import failed: invalid rack design JSON.'); }
    e.target.value = '';
  });
  $('taskBoard').addEventListener('click', e => {
    const element = e.target.closest('[data-task]');
    if (!element) return;
    const task = state.tasks.find(t => t.id === element.dataset.task);
    if (!task) return;
    const next = task.status === 'backlog' ? 'active' : task.status === 'active' ? 'done' : 'backlog';
    if (next === 'done' && task.depends.some(dep => state.tasks.find(t => t.id === dep)?.status !== 'done')) {
      toast('Dependencies need to be completed before this task can close.');
      return;
    }
    mutate(() => { task.status = next; }); toast('Task moved to ' + next + '.');
  });
  $('addTaskButton').addEventListener('click', () => {
    const name = window.prompt('Name the new project task:');
    if (!name || !name.trim()) return;
    mutate(() => { state.tasks.push({ id: id(), title: name.trim().slice(0, 90), status: 'backlog', depends: [] }); });
  });
  document.addEventListener('keydown', e => {
    const editing = e.target.closest('input,textarea,select') || e.target.isContentEditable;
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z') {
      e.preventDefault(); restore(e.shiftKey ? redo : undo, e.shiftKey ? undo : redo); return;
    }
    if (!editing && e.key === '/') { e.preventDefault(); $('assetSearch').focus(); return; }
    if (editing || tab !== 'rack') return;
    if (e.key === 'ArrowUp') { e.preventDefault(); moveSelected(1); }
    if (e.key === 'ArrowDown') { e.preventDefault(); moveSelected(-1); }
    if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); removeSelected(); }
    if (e.key === 'Escape') {
      selected = null; inspectRack = true;
      document.querySelector('.inspector-pane').classList.remove('open');
      renderRack(); renderInspector();
    }
  });
  document.addEventListener('pointerdown', e => {
    const pane = document.querySelector('.inspector-pane');
    if (pane.classList.contains('open') && !pane.contains(e.target) &&
        !e.target.closest('#rackVisualization') && !e.target.closest('#configButton')) pane.classList.remove('open');
  });
  render();
})();
