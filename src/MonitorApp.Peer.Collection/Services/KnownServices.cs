namespace MonitorApp.Peer.Collection.Services;

/// <summary>
/// A service people run that this computer can recognise by itself (D1 in the bill of work). <paramref name="Processes"/>
/// are process names without ".exe", compared ignoring case; <paramref name="Units"/> are systemd unit files whose presence
/// says the service is installed even while it is stopped.
/// </summary>
public sealed record KnownService(string Id, string Name, IReadOnlyList<string> Processes, IReadOnlyList<string> Units);

/// <summary>
/// The services recognised today. Data, not code per service: adding one is adding a row. Ids become signal ids
/// ("service.tailscale.running"), so they stay short, lower case and stable.
/// </summary>
public static class KnownServices
{
    public static IReadOnlyList<KnownService> All { get; } =
    [
        new("tailscale", "Tailscale", ["tailscaled", "tailscale-ipn"], ["tailscaled.service"]),
        new("syncthing", "Syncthing", ["syncthing"], ["syncthing@.service", "syncthing.service"]),
        new("ssh", "SSH server", ["sshd"], ["sshd.service", "ssh.service"]),
        new("docker", "Docker", ["dockerd", "com.docker.backend", "Docker Desktop"], ["docker.service"]),
        new("podman", "Podman", ["podman"], ["podman.service"]),
        new("samba", "Windows file sharing (Samba)", ["smbd"], ["smb.service", "smbd.service"]),
        new("nginx", "nginx web server", ["nginx"], ["nginx.service"]),
        new("caddy", "Caddy web server", ["caddy"], ["caddy.service"]),
        new("apache", "Apache web server", ["httpd", "apache2"], ["httpd.service", "apache2.service"]),
        new("jellyfin", "Jellyfin", ["jellyfin", "jellyfin.server"], ["jellyfin.service"]),
        new("plex", "Plex Media Server", ["Plex Media Server"], ["plexmediaserver.service"]),
        new("home-assistant", "Home Assistant", ["hass"], ["home-assistant.service"]),
        new("postgres", "PostgreSQL", ["postgres"], ["postgresql.service"]),
        new("mysql", "MySQL / MariaDB", ["mysqld", "mariadbd"], ["mysqld.service", "mariadb.service"]),
        new("ollama", "Ollama", ["ollama"], ["ollama.service"]),
        new("nextcloud", "Nextcloud desktop", ["nextcloud"], []),
        new("onedrive", "OneDrive", ["OneDrive"], []),
        new("dropbox", "Dropbox", ["dropbox"], []),
        new("steam", "Steam", ["steam", "steamwebhelper"], []),
        new("discord", "Discord", ["Discord"], []),
    ];
}
