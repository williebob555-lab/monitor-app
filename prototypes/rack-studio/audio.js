/* Signal Lab is an independent, design-only audio routing lens. Never plays or energizes hardware. */
(function () {
  'use strict';
  const $ = id => document.getElementById(id);
  const STORAGE = 'monitor-rack-signal-lab-v1';
  const initial = [
    { id: 'bass', name: 'Bass guitar', kind: 'INSTRUMENT', output: 'Instrument', connector: '6.35 mm TS', power: null, latency: 0 },
    { id: 'receiver', name: 'Wireless receiver', kind: 'RF / AUDIO', output: 'Instrument', connector: '6.35 mm TS', power: '5 V · 400 mA', latency: null },
    { id: 'tuner', name: 'Tuner', kind: 'EFFECT', output: 'Instrument', connector: '6.35 mm TS', power: '9 V DC · 100 mA', latency: 0 },
    { id: 'multi', name: 'Multi-effects', kind: 'DSP', output: 'Professional line', connector: '6.35 mm TRS', power: '9 V DC · 3000 mA', latency: 3 },
    { id: 'interface', name: 'Audio interface', kind: 'INTERFACE', output: 'Professional line', connector: '6.35 mm TRS', power: 'USB bus power', latency: null }
  ];
  const escapeHtml = s => String(s ?? '').replace(/[&<>"']/g, ch =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
  let stages;
  try {
    stages = JSON.parse(localStorage.getItem(STORAGE) || 'null');
    if (!Array.isArray(stages) || !stages.every(x => typeof x.name === 'string')) stages = structuredClone(initial);
  } catch (_) { stages = structuredClone(initial); }
  const persist = () => {
    try { localStorage.setItem(STORAGE, JSON.stringify(stages)); } catch (_) { /* local sandbox */ }
  };
  function audit() {
    const problems = [];
    for (let i = 0; i < stages.length - 1; i++) {
      const a = stages[i], b = stages[i + 1];
      if (a.output === 'Speaker' && b.output !== 'Speaker') {
        problems.push(['error','Speaker power into low-level equipment', a.name + ' output requires a speaker-rated input, not a line/instrument connection.']);
      } else if (a.output === 'Eurorack modular' && b.output !== 'Eurorack modular') {
        problems.push(['warning','Module attenuation may be required', a.name + ' signal level may overload the next input.']);
      } else if (a.output !== b.output) {
        problems.push(['warning','Gain staging is not established', a.name + ' (' + a.output + ') to ' + b.name + ' (' + b.output + ') needs level and headroom confirmation.']);
      }
      if (a.connector !== b.connector)
        problems.push(['unknown','Patch cable or adapter unverified', a.connector + ' to ' + b.connector + ': connector differences require a documented cable or adapter.']);
    }
    if (stages.some(s => s.latency === null))
      problems.push(['unknown','End-to-end latency unresolved','Some processing and wireless stages have no documented latency.']);
    if (stages.some(s => s.power?.includes('DC')))
      problems.push(['unknown','DC compatibility unresolved','Check supply voltage, barrel size, current, polarity, isolation, start-up demand and shared grounds.']);
    problems.push(['unknown','RF and EMI environment unresolved','Antenna placement, frequency coordination, noisy power converters and cable routes require measurement.']);
    problems.push(['unknown','Signal wiring not certified','Balancing, phantom power, connector gender, impedance and exact wiring still need evidence.']);
    return problems;
  }
  function render() {
    const graph = $('signalGraph'), diagnostics = $('signalFindings'), power = $('dcGraph');
    if (!graph || !diagnostics || !power) return;
    graph.innerHTML = '<div class="signal-route-head"><span>PATCH A / INSTRUMENT TO INTERFACE</span><span>LOGICAL PATH ONLY · NOT PHYSICAL WIRING</span></div>' +
      '<div class="signal-flow">' + stages.map((stage,i) => {
        const predecessor = i > 0 ? '<div class="signal-link"><span>→</span><small>CABLE ?</small></div>' : '';
        return predecessor + '<div class="signal-unit" data-stage="' + i + '"><div class="signal-unit-head">' +
          '<small>' + escapeHtml(stage.kind) + '</small><span class="signal-indicator">◉</span></div>' +
          '<strong>' + escapeHtml(stage.name) + '</strong><div class="signal-unit-spec">OUTPUT <b>' + escapeHtml(stage.output) + '</b></div>' +
          '<div class="signal-unit-spec">JACK <b>' + escapeHtml(stage.connector) + '</b></div>' +
          '<label class="signal-edit">LEVEL<select data-level="' + i + '">' +
          ['Instrument','Microphone','Consumer line','Professional line','Eurorack modular','Speaker'].map(level =>
            '<option value="' + escapeHtml(level) + '"' + (stage.output === level ? ' selected' : '') + '>' + escapeHtml(level) + '</option>').join('') +
          '</select></label></div>';
      }).join('') + '</div>';
    const findings = audit();
    diagnostics.innerHTML = '<div class="signal-audit-count">' +
      findings.length + ' considerations · ' + findings.filter(x => x[0] === 'error').length + ' hard conflicts</div>' +
      findings.map(([level,title,details]) => '<div class="signal-audit-row ' + level + '">' +
        '<span>' + (level === 'error' ? '✕' : level === 'warning' ? '!' : '?') + '</span><div><strong>' +
        escapeHtml(title) + '</strong><p>' + escapeHtml(details) + '</p></div></div>').join('');
    power.innerHTML = stages.filter(s => s.power).map(s =>
      '<div class="signal-power-row"><span class="signal-power-branch"></span><div><strong>' +
      escapeHtml(s.name) + '</strong><small>' + escapeHtml(s.power) + '</small></div><span class="signal-power-status">UNVERIFIED</span></div>'
    ).join('') + '<div class="signal-help">Power outputs are not assigned yet. Do not energize until voltage, polarity, connector dimensions, isolation and current limits are confirmed.</div>';
  }

  $('signalGraph')?.addEventListener('change', e => {
    const index = Number(e.target.dataset.level);
    if (!Number.isInteger(index) || !stages[index]) return;
    stages[index].output = e.target.value;
    persist(); render();
  });
  $('addAudioNode')?.addEventListener('click', () => {
    const nextId = Math.max(0, ...stages.map(x => Number(x.id.replace('extra-','')) || 0)) + 1;
    stages.splice(Math.max(0, stages.length - 1), 0, {
      id: 'extra-' + nextId, name: 'New signal stage',
      kind: 'UNSPECIFIED', output: 'Instrument', connector: '6.35 mm TS', power: null, latency: null
    });
    persist(); render();
  });

  window.MonitorRackAudio = { render };
  // No UI is shown at script initialization. Studio owns module selection.
})();
