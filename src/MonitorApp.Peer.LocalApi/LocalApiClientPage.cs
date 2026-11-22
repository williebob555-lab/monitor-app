namespace MonitorApp.Peer.LocalApi;

/// <summary>
/// Static markup and script for the plain local data client (current state, history, as-of). The script uses only
/// DOM text APIs, loads nothing from the network except this host's own JSON endpoints, and runs under a nonce.
/// </summary>
internal static class LocalApiClientPage
{
    public const string Sections = """
        <section aria-labelledby="current"><h2 id="current">Current state</h2>
        <p>Latest stored observation for every subject and signal. Unavailable, denied, error and stale observations are shown as states, never as zero.</p>
        <p><button type="button" id="current-refresh">Refresh current state</button> <span id="current-status" role="status"></span></p>
        <div class="scroll"><table><caption class="sr">Current state per subject and signal</caption><thead><tr><th scope="col">Subject</th><th scope="col">Signal</th><th scope="col">Value or state</th><th scope="col">Freshness</th><th scope="col">Quality</th><th scope="col">Observed (UTC)</th><th scope="col">Age</th><th scope="col">Origin</th></tr></thead><tbody id="current-rows"></tbody></table></div></section>
        <section aria-labelledby="history"><h2 id="history">History</h2>
        <form id="history-form"><fieldset><legend>Choose a series and time range (all times are UTC)</legend>
        <p><label for="history-subject">Subject</label><br><select id="history-subject"><option value="">All subjects</option></select></p>
        <p><label for="history-signal">Signal</label><br><select id="history-signal"><option value="">All signals</option></select></p>
        <p><label for="history-range">Time range</label><br><select id="history-range"><option value="1h">Last 1 hour</option><option value="24h" selected>Last 24 hours</option><option value="7d">Last 7 days</option><option value="custom">Custom (maximum 31 days)</option></select></p>
        <p><label for="history-from">Custom from (UTC)</label><br><input id="history-from" type="datetime-local" step="1" disabled></p>
        <p><label for="history-to">Custom to, exclusive (UTC)</label><br><input id="history-to" type="datetime-local" step="1" disabled></p>
        <button type="submit">Show history</button></fieldset></form>
        <p id="history-status" role="status"></p>
        <div class="scroll"><table><caption class="sr">History observations ordered by observed time</caption><thead><tr><th scope="col">Observed (UTC)</th><th scope="col">Subject</th><th scope="col">Signal</th><th scope="col">Value or state</th><th scope="col">Freshness at receipt</th><th scope="col">Quality</th><th scope="col">Source</th><th scope="col">Origin and sequence</th><th scope="col">Provenance</th></tr></thead><tbody id="history-rows"></tbody></table></div>
        <p><button type="button" id="history-more" hidden>Load more rows</button></p></section>
        <section aria-labelledby="asof"><h2 id="asof">State as of a time</h2>
        <p>Reconstructs the latest observation at or before the chosen time for each subject and signal, with its age at that time. Only observations within the lookback are considered.</p>
        <form id="asof-form"><p><label for="asof-time">As-of time (UTC)</label><br><input id="asof-time" type="datetime-local" step="1" required></p>
        <p><label for="asof-lookback">Lookback</label><br><select id="asof-lookback"><option value="3600">1 hour</option><option value="86400" selected>24 hours</option><option value="604800">7 days</option></select></p>
        <button type="submit">Reconstruct state</button></form>
        <p id="asof-status" role="status"></p>
        <div class="scroll"><table><caption class="sr">Reconstructed state at the chosen time</caption><thead><tr><th scope="col">Subject</th><th scope="col">Signal</th><th scope="col">Value or state</th><th scope="col">Freshness at that time</th><th scope="col">Quality</th><th scope="col">Observed (UTC)</th><th scope="col">Age at that time</th><th scope="col">Origin</th></tr></thead><tbody id="asof-rows"></tbody></table></div></section>
        """;

    public const string Script = """
        (function () {
          'use strict';
          var $ = function (id) { return document.getElementById(id); };
          var catalog = { subjects: [] };
          var historyCursor = null;
          var historyPoints = [];

          function td(text, tag) {
            var cell = document.createElement(tag || 'td');
            cell.textContent = text;
            return cell;
          }
          function setStatus(id, text) { $(id).textContent = text; }
          function fetchJson(url) {
            return fetch(url, { headers: { Accept: 'application/json' }, credentials: 'same-origin' }).then(function (r) {
              return r.json().catch(function () { return {}; }).then(function (body) {
                if (!r.ok) { throw new Error(body.error || ('Request failed with status ' + r.status)); }
                return body;
              });
            });
          }
          function formatAge(seconds) {
            if (seconds < 90) { return Math.round(seconds) + ' s'; }
            if (seconds < 5400) { return Math.round(seconds / 60) + ' min'; }
            if (seconds < 172800) { return (seconds / 3600).toFixed(1) + ' h'; }
            return (seconds / 86400).toFixed(1) + ' d';
          }
          function valueText(p) {
            if (p.value === null || p.value === undefined) {
              return 'State: ' + p.freshness + ' (' + p.quality + ')' + (p.detail ? ' - ' + p.detail : '');
            }
            var v = p.value;
            var text = v.kind === 'number' ? String(v.number) : v.kind === 'boolean' ? String(v.boolean) : String(v.text);
            return text + (p.unit ? ' ' + p.unit : '');
          }
          function origin(p) { return p.originPeerId + ' / ' + p.originStreamId + ' #' + p.sequence; }
          function fillStateTable(bodyId, points, ageHeader) {
            var body = $(bodyId);
            body.replaceChildren();
            points.forEach(function (p) {
              var row = document.createElement('tr');
              row.append(td(p.subjectEntityId), td(p.signalId), td(valueText(p)), td(p.freshness), td(p.quality),
                td(p.observedAt), td(formatAge(p.ageSeconds)), td(origin(p)));
              body.append(row);
            });
          }
          function loadCurrent() {
            setStatus('current-status', 'Loading...');
            return fetchJson('/api/v1/state?lookbackSeconds=2678400').then(function (r) {
              fillStateTable('current-rows', r.points);
              setStatus('current-status', r.points.length + ' signals as of ' + r.asOf + (r.truncated ? ' (truncated; narrow the query)' : ''));
            }).catch(function (e) { setStatus('current-status', 'Error: ' + e.message); });
          }
          function loadCatalog() {
            return fetchJson('/api/v1/catalog').then(function (c) {
              catalog = c;
              var select = $('history-subject');
              c.subjects.forEach(function (s) {
                var option = document.createElement('option');
                option.value = s.subjectEntityId; option.textContent = s.subjectEntityId;
                select.append(option);
              });
              refreshSignals();
            }).catch(function (e) { setStatus('history-status', 'Catalog error: ' + e.message); });
          }
          function refreshSignals() {
            var subject = $('history-subject').value;
            var select = $('history-signal');
            select.replaceChildren();
            var all = document.createElement('option');
            all.value = ''; all.textContent = 'All signals';
            select.append(all);
            var seen = {};
            catalog.subjects.forEach(function (s) {
              if (subject && s.subjectEntityId !== subject) { return; }
              s.signals.forEach(function (g) {
                if (seen[g.signalId]) { return; }
                seen[g.signalId] = true;
                var option = document.createElement('option');
                option.value = g.signalId;
                option.textContent = g.signalId + (g.unit ? ' (' + g.unit + ')' : '');
                select.append(option);
              });
            });
          }
          function pad(n) { return String(n).padStart(2, '0'); }
          function toLocalInputUtc(date) {
            return date.getUTCFullYear() + '-' + pad(date.getUTCMonth() + 1) + '-' + pad(date.getUTCDate()) + 'T' +
              pad(date.getUTCHours()) + ':' + pad(date.getUTCMinutes()) + ':' + pad(date.getUTCSeconds());
          }
          function fromInputUtc(value) { return value ? new Date(value + 'Z') : null; }
          function historyWindow() {
            var range = $('history-range').value;
            var now = new Date();
            if (range === 'custom') {
              return { from: fromInputUtc($('history-from').value), to: fromInputUtc($('history-to').value) };
            }
            var ms = range === '1h' ? 3600000 : range === '7d' ? 604800000 : 86400000;
            return { from: new Date(now.getTime() - ms), to: new Date(now.getTime() + 1000) };
          }
          function runHistory(append) {
            var w = historyWindow();
            if (!w.from || !w.to || isNaN(w.from) || isNaN(w.to)) { setStatus('history-status', 'Enter both a from and a to time.'); return; }
            var q = new URLSearchParams({ from: w.from.toISOString(), to: w.to.toISOString(), limit: '1000' });
            if ($('history-subject').value) { q.set('subjectEntityId', $('history-subject').value); }
            if ($('history-signal').value) { q.set('signalId', $('history-signal').value); }
            if (append && historyCursor) { q.set('cursor', historyCursor); } else { historyPoints = []; }
            setStatus('history-status', 'Loading...');
            fetchJson('/api/v1/history?' + q.toString()).then(function (r) {
              historyPoints = historyPoints.concat(r.points);
              historyCursor = r.truncated ? r.nextCursor : null;
              $('history-more').hidden = !historyCursor;
              renderHistory();
              setStatus('history-status', historyPoints.length + ' observations loaded' +
                (r.truncated ? '; more rows exist, use Load more rows.' : '; the window is complete.'));
            }).catch(function (e) { setStatus('history-status', 'Error: ' + e.message); });
          }
          function renderHistory() {
            var body = $('history-rows');
            body.replaceChildren();
            historyPoints.forEach(function (p) {
              var row = document.createElement('tr');
              row.append(td(p.observedAt), td(p.subjectEntityId), td(p.signalId), td(valueText(p)), td(p.freshness),
                td(p.quality), td(p.source), td(origin(p)), td(p.provenance));
              body.append(row);
            });
          }
          function runAsOf(event) {
            event.preventDefault();
            var at = fromInputUtc($('asof-time').value);
            if (!at || isNaN(at)) { setStatus('asof-status', 'Enter an as-of time.'); return; }
            setStatus('asof-status', 'Loading...');
            fetchJson('/api/v1/state?asOf=' + encodeURIComponent(at.toISOString()) + '&lookbackSeconds=' + $('asof-lookback').value).then(function (r) {
              fillStateTable('asof-rows', r.points);
              setStatus('asof-status', r.points.length + ' signals reconstructed as of ' + r.asOf + (r.truncated ? ' (truncated; narrow the query)' : ''));
            }).catch(function (e) { setStatus('asof-status', 'Error: ' + e.message); });
          }

          $('current-refresh').addEventListener('click', loadCurrent);
          $('history-subject').addEventListener('change', refreshSignals);
          $('history-range').addEventListener('change', function () {
            var custom = $('history-range').value === 'custom';
            $('history-from').disabled = !custom; $('history-to').disabled = !custom;
          });
          $('history-form').addEventListener('submit', function (e) { e.preventDefault(); runHistory(false); });
          $('history-more').addEventListener('click', function () { runHistory(true); });
          $('asof-form').addEventListener('submit', runAsOf);
          $('asof-time').value = toLocalInputUtc(new Date());
          $('history-to').value = toLocalInputUtc(new Date());
          $('history-from').value = toLocalInputUtc(new Date(Date.now() - 86400000));
          loadCatalog();
          loadCurrent();
        })();
        """;
}
