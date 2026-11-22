Name:           monitor-app
Version:        %{?pkg_version}%{!?pkg_version:0.0.0}
Release:        1%{?dist}
Summary:        Monitor App peer node (always-on service)
License:        LicenseRef-MonitorApp-Proprietary
BuildArch:      x86_64
%{?systemd_requires}
Requires(pre):  shadow-utils
AutoReqProv:    no
# Self-contained .NET publish; no runtime dependency beyond the base system (ICU/OpenSSL for .NET).
Requires:       libicu
Requires:       openssl-libs

%global debug_package %{nil}
%global __strip /bin/true

%description
Peer node for Monitor App. Runs as the unprivileged monitor-app user under systemd with state in
/var/lib/monitor-app. Not enabled or started on install. Untested on Fedora/RHEL/SELinux in this
repository; see docs/TESTING.md.

# Build with eng/packaging/linux/build-packages.sh, which stages the publish tree in
# %{_sourcedir}/stage (opt/, etc/, usr/lib/systemd/system, usr/lib/sysusers.d) before rpmbuild -bb.
%install
cp -a %{_sourcedir}/stage/. %{buildroot}/

%pre
getent group monitor-app >/dev/null || groupadd -r monitor-app
getent passwd monitor-app >/dev/null || \
    useradd -r -g monitor-app -d /var/lib/monitor-app -M -s /sbin/nologin -c "Monitor App peer node" monitor-app
exit 0

%post
install -d -m 0700 -o monitor-app -g monitor-app /var/lib/monitor-app
%systemd_post monitor-app.service

%preun
%systemd_preun monitor-app.service

%postun
%systemd_postun monitor-app.service
# Package removal keeps /var/lib/monitor-app (identity and data) and the monitor-app user.
# Delete them manually for a purge: rm -rf /var/lib/monitor-app /etc/monitor-app; userdel monitor-app

%files
%dir /opt/monitor-app
/opt/monitor-app/*
%config(noreplace) /etc/monitor-app/node.conf
/usr/lib/systemd/system/monitor-app.service
/usr/lib/sysusers.d/monitor-app.conf
