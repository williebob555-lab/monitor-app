using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.LocalApi;

/// <summary>
/// Owner-operated sections of the loopback administration page (T056): alerts with acknowledge, per-peer alert-rule editor,
/// control grants, recent peer-state and update history, pinned incidents, and update status with approve. Every value that
/// originates from a peer (names, ids, alert text, reasons) is HTML-encoded here. Mutations call the same loopback endpoints a
/// script would, with the page's antiforgery token in the X-CSRF-TOKEN header; actions the API gates behind desktop presence
/// stay gated and the page only reports the refusal.
/// </summary>
internal static class LocalApiAdminSections
{
    private const int MaximumAlerts = 100;
    private const int MaximumHistoryRows = 50;
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);

    public static async Task<string> RenderAsync(
        IServiceProvider services, PeerStore peers, IPeerClock clock, HtmlEncoder html, CancellationToken token)
    {
        var now = clock.UtcNow.ToUniversalTime();
        var trusted = await peers.ReadTrustedPeersAsync(token).ConfigureAwait(false);
        var names = trusted.ToDictionary(item => item.PeerId, item => item.DisplayName ?? item.PeerId, StringComparer.Ordinal);
        var local = await peers.ReadLocalIdentityAsync(token).ConfigureAwait(false);
        var builder = new StringBuilder();
        builder.Append(await AlertsAsync(services, now, html, token).ConfigureAwait(false));
        builder.Append(await RulesAsync(services, peers, trusted, local?.PeerId, names, html, token).ConfigureAwait(false));
        builder.Append(await GrantsAsync(services, trusted, names, html, token).ConfigureAwait(false));
        builder.Append(await RecentHistoryAsync(services, peers, names, now, html, token).ConfigureAwait(false));
        builder.Append(await PinsAsync(peers, now, html, token).ConfigureAwait(false));
        builder.Append(Updates(services, html));
        return builder.ToString();
    }

    private static async Task<string> AlertsAsync(IServiceProvider services, DateTimeOffset now, HtmlEncoder html, CancellationToken token)
    {
        var section = new StringBuilder("<section aria-labelledby=\"alerts\"><h2 id=\"alerts\">Alerts</h2>");
        if (services.GetService<AlertStore>() is not { } store)
        {
            return section.Append("<p>Alerting is not enabled on this node.</p></section>").ToString();
        }

        var found = await store.ReadActiveAndRecentAsync(now - RecentWindow, MaximumAlerts, token).ConfigureAwait(false);
        var acknowledgements = (await store.ReadOperatorAcknowledgementsAsync(token).ConfigureAwait(false))
            .ToDictionary(item => item.AlertId, StringComparer.Ordinal);
        var escalations = await store.ReadEscalationsAsync(token).ConfigureAwait(false);
        var active = found.Where(item => item.ResolvedAt is null).ToList();
        var resolvedCount = found.Count - active.Count;
        if (found.Count == 0)
        {
            return section.Append("<p>No alerts in the last 7 days.</p></section>").ToString();
        }

        // A short grouped summary first (what kinds of problems, how many); the full list stays one click away.
        section.Append(CultureInfo.InvariantCulture, $"<p><strong>{(active.Count == 0 ? "Nothing needs attention." : $"{active.Count} need attention.")}</strong> {resolvedCount} cleared in the last 7 days.</p>");
        if (active.Count > 0)
        {
            section.Append("<ul>");
            foreach (var group in active.GroupBy(item => item.RuleId, StringComparer.Ordinal).OrderByDescending(item => item.Max(alert => alert.Severity)).ThenByDescending(item => item.Count()))
            {
                var first = group.First();
                section.Append(CultureInfo.InvariantCulture,
                    $"<li><strong>{html.Encode(GroupLabel(group.Key, first.Title))}</strong>: {group.Count()} ({html.Encode(first.Severity.ToString().ToLowerInvariant())})</li>");
            }

            section.Append("</ul>");
        }

        section.Append(CultureInfo.InvariantCulture, $"<details><summary>Show all {found.Count} alerts</summary>");
        section.Append("<p>Acknowledging records this desktop as the acknowledger; the first acknowledgement is kept.</p>");
        section.Append("<div class=\"scroll\"><table><caption class=\"sr\">Alerts</caption><thead><tr><th scope=\"col\">Severity</th><th scope=\"col\">State</th><th scope=\"col\">Alert</th><th scope=\"col\">Subject</th><th scope=\"col\">Raised (UTC)</th><th scope=\"col\">Acknowledgement</th></tr></thead><tbody>");
        foreach (var alert in active.Concat(found.Where(item => item.ResolvedAt is not null)))
        {
            string ack;
            if (acknowledgements.TryGetValue(alert.AlertId, out var recorded))
            {
                ack = $"Acknowledged by {html.Encode(recorded.By)} at {Utc(recorded.At)}";
            }
            else
            {
                ack = Action("ack", $"/api/v1/alerts/{Uri.EscapeDataString(alert.AlertId)}/acknowledge", "Acknowledge", html,
                    $"Acknowledge alert {alert.Title}");
            }

            var escalated = escalations.ContainsKey(alert.AlertId) ? " <strong>(escalated: unacknowledged too long)</strong>" : string.Empty;
            section.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{html.Encode(alert.Severity.ToString())}</td><td>{html.Encode(alert.State.ToString())}</td><td><strong>{html.Encode(alert.Title)}</strong><br>{html.Encode(alert.Detail)}</td><td>{html.Encode(alert.SubjectId)}<br><small>origin {html.Encode(alert.OriginPeerId)}</small></td><td>{Utc(alert.RaisedAt)}</td><td>{ack}{escalated}</td></tr>");
        }

        return section.Append("</tbody></table></div></details></section>").ToString();
    }

    private static string GroupLabel(string ruleId, string fallback) => ruleId switch
    {
        AlertRulesConfig.ObservationUnavailableRuleId => "Readings that could not be collected",
        AlertRulesConfig.PeerSilentRuleId => "Devices not heard from",
        AlertRuleCatalog.CpuRuleId => "Processor busy",
        AlertRuleCatalog.MemoryRuleId => "Memory nearly full",
        AlertRuleCatalog.DiskRuleId => "Disk nearly full",
        _ => fallback,
    };

    private static async Task<string> RulesAsync(
        IServiceProvider services,
        PeerStore peers,
        IReadOnlyList<TrustedPeer> trusted,
        string? localPeerId,
        Dictionary<string, string> names,
        HtmlEncoder html,
        CancellationToken token)
    {
        var section = new StringBuilder("<section aria-labelledby=\"rules\"><h2 id=\"rules\">Alert rules</h2>");
        if (services.GetService<AlertStore>() is not { } store || localPeerId is null)
        {
            return section.Append("<p>Alert rules are not available on this node.</p></section>").ToString();
        }

        var active = trusted.Where(item => item.TrustState == PeerTrustState.Active).Select(item => item.PeerId);
        var overrides = await store.ReadRuleOverridesAsync(token).ConfigureAwait(false);
        var rules = AlertRuleCatalog.Describe(services.GetService<AlertRulesConfig>() ?? AlertRulesConfig.Default, localPeerId, active, overrides);
        section.Append("<p>Override a rule's threshold or switch it off for one peer. Values outside the accepted range are refused by the node and the reason is shown under the form. Signal rules apply to this peer; <code>peer.silent</code> is set per trusted peer.</p>");
        foreach (var group in rules.GroupBy(item => item.SubjectPeerId, StringComparer.Ordinal))
        {
            var label = group.Key == localPeerId ? "This peer" : names.GetValueOrDefault(group.Key, group.Key);
            var url = $"/api/v1/alerts/rules/{Uri.EscapeDataString(group.Key)}";
            section.Append(CultureInfo.InvariantCulture,
                $"<form data-admin=\"rules\" data-url=\"{html.Encode(url)}\"><fieldset><legend>Rules for {html.Encode(label)} ({html.Encode(group.Key)})</legend><div class=\"scroll\"><table><caption class=\"sr\">Alert rules for {html.Encode(label)}</caption><thead><tr><th scope=\"col\">Rule</th><th scope=\"col\">Enabled</th><th scope=\"col\">Threshold</th><th scope=\"col\">Default</th></tr></thead><tbody>");
            var index = 0;
            foreach (var rule in group)
            {
                var id = $"rule-{html.Encode(group.Key)}-{index++}";
                section.Append(CultureInfo.InvariantCulture,
                    $"<tr data-rule=\"{html.Encode(rule.RuleId)}\"><td>{html.Encode(rule.Title)}<br><code>{html.Encode(rule.RuleId)}</code></td><td><input type=\"checkbox\" data-field=\"enabled\" aria-label=\"Enabled: {html.Encode(rule.RuleId)} for {html.Encode(label)}\"{(rule.Enabled ? " checked" : string.Empty)}></td><td><input type=\"number\" data-field=\"threshold\" id=\"{id}\" aria-label=\"Threshold in {html.Encode(rule.Unit)}: {html.Encode(rule.RuleId)} for {html.Encode(label)}\" min=\"{Number(rule.Minimum)}\" max=\"{Number(rule.Maximum)}\" step=\"any\" value=\"{Number(rule.Threshold)}\"> {html.Encode(rule.Unit)} <small>(allowed {Number(rule.Minimum)} to {Number(rule.Maximum)})</small></td><td>{Number(rule.DefaultThreshold)} {html.Encode(rule.Unit)}{(rule.Overridden ? " <em>(overridden)</em>" : string.Empty)}</td></tr>");
            }

            section.Append("</tbody></table></div><p><button type=\"submit\">Save rules</button></p><p class=\"err\" role=\"alert\" data-message></p></fieldset></form>");
        }

        return section.Append("</section>").ToString();
    }

    private static async Task<string> GrantsAsync(
        IServiceProvider services,
        IReadOnlyList<TrustedPeer> trusted,
        Dictionary<string, string> names,
        HtmlEncoder html,
        CancellationToken token)
    {
        var section = new StringBuilder("<section aria-labelledby=\"grants\"><h2 id=\"grants\">Remote control grants</h2>");
        if (services.GetService<ControlService>() is not { } control)
        {
            return section.Append("<p>Remote control is not enabled on this node.</p></section>").ToString();
        }

        var grants = (await control.Store.ReadGrantsAsync(token).ConfigureAwait(false)).ToDictionary(item => item.PeerId, StringComparer.Ordinal);
        var enrollments = (await control.Store.ReadEnrollmentsAsync(token).ConfigureAwait(false))
            .Where(item => item.State == PresenceEnrollmentState.Pending).ToArray();
        section.Append("<p>Which trusted peers may issue control commands. Everything is denied by default. Granting any permission needs desktop presence verification when it is enabled; this page cannot supply it and reports the refusal. Revoking never needs presence.</p>");
        var peerIds = trusted.Where(item => item.TrustState == PeerTrustState.Active).Select(item => item.PeerId)
            .Union(grants.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (peerIds.Length == 0)
        {
            section.Append("<p>No trusted peers.</p>");
        }

        foreach (var peerId in peerIds)
        {
            grants.TryGetValue(peerId, out var grant);
            var label = names.GetValueOrDefault(peerId, peerId);
            var url = $"/api/v1/control/grants/{Uri.EscapeDataString(peerId)}";
            section.Append(
                $"<form data-admin=\"grant\" data-url=\"{html.Encode(url)}\"><fieldset><legend>{html.Encode(label)} (<code>{html.Encode(peerId)}</code>){(grant?.UpdatedAt is { } at ? $", updated {Utc(at)}" : ", no grant recorded")}</legend>" +
                $"<label><input type=\"checkbox\" data-field=\"monitoringControls\"{Checked(grant?.MonitoringControls)}> Monitoring controls</label> " +
                $"<label><input type=\"checkbox\" data-field=\"pairingApproval\"{Checked(grant?.PairingApproval)}> Pairing approval</label> " +
                $"<label><input type=\"checkbox\" data-field=\"updateApproval\"{Checked(grant?.UpdateApproval)}> Update approval</label> " +
                "<button type=\"submit\">Save grant</button> <button type=\"submit\" data-revoke>Revoke all</button><p class=\"err\" role=\"alert\" data-message></p></fieldset></form>");
        }

        section.Append("<h3>Pending presence-key enrollments</h3>");
        if (enrollments.Length == 0)
        {
            section.Append("<p>None pending.</p>");
        }
        else
        {
            section.Append("<div class=\"scroll\"><table><caption class=\"sr\">Pending presence enrollments</caption><thead><tr><th scope=\"col\">Peer</th><th scope=\"col\">Key fingerprint (SHA-256)</th><th scope=\"col\">Requested (UTC)</th><th scope=\"col\">Decision</th></tr></thead><tbody>");
            foreach (var item in enrollments)
            {
                var basePath = $"/api/v1/control/presence-enrollments/{Uri.EscapeDataString(item.EnrollmentId)}";
                section.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{html.Encode(names.GetValueOrDefault(item.PeerId, item.PeerId))}<br><small>{html.Encode(item.PeerId)}</small></td><td><code>{html.Encode(Convert.ToHexString(item.Fingerprint))}</code></td><td>{Utc(item.RequestedAt)}</td><td>{Action("post", basePath + "/approve", "Approve", html, "Approve presence enrollment for " + item.PeerId)} {Action("post", basePath + "/reject", "Reject", html, "Reject presence enrollment for " + item.PeerId)}<span class=\"err\" role=\"alert\" data-message></span></td></tr>");
            }

            section.Append("</tbody></table></div>");
        }

        return section.Append("</section>").ToString();
    }

    private static async Task<string> RecentHistoryAsync(
        IServiceProvider services, PeerStore peers, Dictionary<string, string> names, DateTimeOffset now, HtmlEncoder html, CancellationToken token)
    {
        var section = new StringBuilder("<section aria-labelledby=\"recent\"><h2 id=\"recent\">Recent activity</h2><p>Peer connectivity changes and update install outcomes from the last 7 days, newest first (up to 50 each).</p>");
        var states = (await peers.ReadPeerStateTransitionsAsync(now - RecentWindow, now, 5000, token).ConfigureAwait(false))
            .Reverse().Take(MaximumHistoryRows).ToArray();
        section.Append("<h3>Peer connectivity</h3>");
        if (states.Length == 0)
        {
            section.Append("<p>No peer state changes recorded.</p>");
        }
        else
        {
            section.Append("<div class=\"scroll\"><table><caption class=\"sr\">Peer state changes</caption><thead><tr><th scope=\"col\">When (UTC)</th><th scope=\"col\">Peer</th><th scope=\"col\">State</th><th scope=\"col\">Reason</th></tr></thead><tbody>");
            foreach (var item in states)
            {
                section.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{Utc(item.At)}</td><td>{html.Encode(names.GetValueOrDefault(item.PeerId, item.PeerId))}<br><small>{html.Encode(item.PeerId)}</small></td><td>{html.Encode(item.State.ToString())}</td><td>{html.Encode(item.Reason ?? string.Empty)}</td></tr>");
            }

            section.Append("</tbody></table></div>");
        }

        section.Append("<h3>Update installs</h3>");
        var updates = services.GetService<ILocalUpdateController>() is ILocalUpdateHistory history
            ? history.GetUpdateHistory(now - RecentWindow, now).OrderByDescending(item => item.At).Take(MaximumHistoryRows).ToArray()
            : [];
        if (updates.Length == 0)
        {
            section.Append("<p>No update installs recorded.</p>");
        }
        else
        {
            section.Append("<div class=\"scroll\"><table><caption class=\"sr\">Update install outcomes</caption><thead><tr><th scope=\"col\">When (UTC)</th><th scope=\"col\">Version</th><th scope=\"col\">Outcome</th><th scope=\"col\">Detail</th></tr></thead><tbody>");
            foreach (var item in updates)
            {
                section.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{Utc(item.At)}</td><td>{html.Encode(item.Version)}</td><td>{html.Encode(item.Outcome)}</td><td>{html.Encode(item.Detail ?? string.Empty)}</td></tr>");
            }

            section.Append("</tbody></table></div>");
        }

        return section.Append("</section>").ToString();
    }

    private static async Task<string> PinsAsync(PeerStore peers, DateTimeOffset now, HtmlEncoder html, CancellationToken token)
    {
        var pinned = await peers.ReadPinnedIncidentsAsync(token).ConfigureAwait(false);
        var section = new StringBuilder("<section aria-labelledby=\"pins\"><h2 id=\"pins\">Pinned incidents</h2><p>A pinned time window keeps its raw observations out of retention pruning until it is unpinned. Unpinning only makes the window prunable on the next retention pass; pruned data is never restored.</p>");
        if (pinned.Count == 0)
        {
            section.Append("<p>No pinned incidents.</p>");
        }
        else
        {
            section.Append("<div class=\"scroll\"><table><caption class=\"sr\">Pinned incidents</caption><thead><tr><th scope=\"col\">Label</th><th scope=\"col\">From (UTC)</th><th scope=\"col\">To (UTC)</th><th scope=\"col\">Pinned (UTC)</th><th scope=\"col\">Action</th></tr></thead><tbody>");
            foreach (var item in pinned)
            {
                section.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{html.Encode(item.Label)}</td><td>{Utc(item.From)}</td><td>{Utc(item.To)}</td><td>{Utc(item.CreatedAt)}</td><td>{Action("post", $"/api/v1/incidents/{Uri.EscapeDataString(item.IncidentId)}/unpin", "Unpin", html, "Unpin " + item.Label)}<span class=\"err\" role=\"alert\" data-message></span></td></tr>");
            }

            section.Append("</tbody></table></div>");
        }

        var to = now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var from = now.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        section.Append(CultureInfo.InvariantCulture,
            $"<form data-admin=\"pin\" data-url=\"/api/v1/incidents/pin\"><fieldset><legend>Pin a time window (UTC, at most 31 days)</legend><p><label for=\"pin-label\">Label (up to 200 characters)</label><br><input id=\"pin-label\" data-field=\"label\" required maxlength=\"200\"></p><p><label for=\"pin-from\">From (UTC)</label><br><input id=\"pin-from\" data-field=\"from\" type=\"datetime-local\" step=\"1\" required value=\"{from}\"></p><p><label for=\"pin-to\">To (UTC)</label><br><input id=\"pin-to\" data-field=\"to\" type=\"datetime-local\" step=\"1\" required value=\"{to}\"></p><button type=\"submit\">Pin window</button><p class=\"err\" role=\"alert\" data-message></p></fieldset></form></section>");
        return section.ToString();
    }

    private static string Updates(IServiceProvider services, HtmlEncoder html)
    {
        var section = new StringBuilder("<section aria-labelledby=\"updates\"><h2 id=\"updates\">Updates</h2>");
        if (services.GetService<ILocalUpdateController>() is not { } controller)
        {
            return section.Append("<p>Automatic updates are not enabled on this node.</p></section>").ToString();
        }

        var status = controller.GetStatus();
        section.Append(CultureInfo.InvariantCulture,
            $"<table><caption class=\"sr\">Update status</caption><tbody><tr><th scope=\"row\">Policy</th><td>{html.Encode(status.Policy)}</td></tr><tr><th scope=\"row\">Installed version</th><td>{html.Encode(status.InstalledVersion)}</td></tr><tr><th scope=\"row\">Stage</th><td>{html.Encode(status.Stage)}</td></tr><tr><th scope=\"row\">Target version</th><td>{html.Encode(status.TargetVersion ?? "none")}</td></tr><tr><th scope=\"row\">Detail</th><td>{html.Encode(status.Detail ?? string.Empty)}</td></tr><tr><th scope=\"row\">Last checked (UTC)</th><td>{(status.LastCheckedUtc is { } checkedAt ? Utc(checkedAt) : "never")}</td></tr><tr><th scope=\"row\">Last error</th><td>{html.Encode(status.LastError ?? "none")}</td></tr></tbody></table>");
        if (status.ApprovalPending)
        {
            section.Append(CultureInfo.InvariantCulture,
                $"<p>Version <strong>{html.Encode(status.TargetVersion ?? string.Empty)}</strong> is staged and waiting for approval{(status.TargetDigest is null ? string.Empty : $" (artifact SHA-256 <code>{html.Encode(status.TargetDigest)}</code>)")}. Approval needs desktop presence verification when it is enabled; this page cannot supply it and reports the refusal.</p><p>{Action("post", "/api/v1/update/approve", "Approve update", html, "Approve staged update " + status.TargetVersion)}<span class=\"err\" role=\"alert\" data-message></span></p>");
        }
        else
        {
            section.Append("<p>No staged update is awaiting approval.</p>");
        }

        return section.Append("</section>").ToString();
    }

    private static string Action(string kind, string url, string label, HtmlEncoder html, string accessibleName) =>
        $"<form data-admin=\"{kind}\" data-url=\"{html.Encode(url)}\"><button type=\"submit\" aria-label=\"{html.Encode(accessibleName)}\">{html.Encode(label)}</button><span class=\"err\" role=\"alert\" data-message></span></form>";

    private static string Checked(bool? value) => value == true ? " checked" : string.Empty;

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Behavior for the sections above: DOM text APIs only; posts JSON to this host with the antiforgery header.</summary>
    public const string Script = """
        (function () {
          'use strict';
          var csrf = document.querySelector('main').getAttribute('data-csrf');
          function field(root, name) { return root.querySelector('[data-field="' + name + '"]'); }
          function message(form, text, ok) {
            var target = form.querySelector('[data-message]');
            if (!target) { return; }
            target.textContent = text;
            target.className = ok ? 'ok' : 'err';
          }
          function explain(status, body) {
            var code = body && body.error;
            if (code === 'presence_required' || code === 'presence_invalid' || code === 'presence_not_enrolled') {
              return 'Refused: this action needs desktop presence verification (' + code + '). Verify in the desktop client; this page cannot supply it.';
            }
            if (code) { return 'Refused: ' + code; }
            if (status === 403) { return 'Refused: the request was rejected (reload the page to refresh the security token).'; }
            return 'Request failed with status ' + status + '.';
          }
          function utc(value) { return value ? new Date(value + 'Z').toISOString() : null; }
          function build(form) {
            var kind = form.getAttribute('data-admin');
            if (kind === 'rules') {
              var overrides = [];
              form.querySelectorAll('tr[data-rule]').forEach(function (row) {
                var raw = field(row, 'threshold').value.trim();
                overrides.push({ ruleId: row.getAttribute('data-rule'), enabled: field(row, 'enabled').checked, threshold: raw === '' ? null : Number(raw) });
              });
              return { overrides: overrides };
            }
            if (kind === 'grant') {
              var revoke = form.revoking === true;
              return {
                monitoringControls: !revoke && field(form, 'monitoringControls').checked,
                pairingApproval: !revoke && field(form, 'pairingApproval').checked,
                updateApproval: !revoke && field(form, 'updateApproval').checked
              };
            }
            if (kind === 'pin') {
              return { label: field(form, 'label').value, from: utc(field(form, 'from').value), to: utc(field(form, 'to').value) };
            }
            return {};
          }
          document.addEventListener('submit', function (event) {
            var form = event.target;
            if (!form.hasAttribute || !form.hasAttribute('data-admin')) { return; }
            event.preventDefault();
            var kind = form.getAttribute('data-admin');
            form.revoking = !!(event.submitter && event.submitter.hasAttribute('data-revoke'));
            var body;
            try { body = build(form); } catch (e) { message(form, 'Check the values: ' + e.message, false); return; }
            message(form, 'Working...', true);
            fetch(form.getAttribute('data-url'), {
              method: 'POST',
              credentials: 'same-origin',
              headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'X-CSRF-TOKEN': csrf },
              body: JSON.stringify(body)
            }).then(function (response) {
              return response.json().catch(function () { return {}; }).then(function (data) {
                if (!response.ok) { message(form, explain(response.status, data), false); return; }
                if (kind === 'rules') { message(form, 'Saved.', true); return; }
                window.location.reload();
              });
            }).catch(function (e) { message(form, 'Request failed: ' + e.message, false); });
          });
        })();
        """;
}
