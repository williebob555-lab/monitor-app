#!/usr/bin/env bash
# Builds the local update-server fixture for the Ubuntu in-place update smoke.
# Usage: update-fixture.sh FIXTURE_DIR REPOSITORY SIGNING_KEY_FILE PUBLIC_KEY_FILE VERSION:DEB [VERSION:DEB ...]
# For each VERSION:DEB it creates FIXTURE_DIR/releases/VERSION/{monitorapp-node-ubuntu-x64-VERSION.deb,update-manifest.json}
# with a manifest signed by the REAL release tool (eng/release/MonitorApp.ReleaseTool) using the throwaway smoke key,
# whose artifact URLs point at https://127.0.0.1:8443/download/. It also creates a throwaway CA and a server certificate
# (SAN 127.0.0.1) in FIXTURE_DIR/tls. Nothing here weakens the node: the node still demands HTTPS and a valid chain, so the
# smoke installs the CA into the system store (update-ca-certificates), exactly what an operator would do.
# SIGNING_KEY_FILE holds the base64 PKCS#8 P-256 key, PUBLIC_KEY_FILE the base64 SPKI (the one baked into the packages).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
fixture="$1" repo="$2" key_file="$3" pub_file="$4"
shift 4
base_url="${UPDATE_BASE_URL:-https://127.0.0.1:8443/download/}"
key_id="${UPDATE_KEY_ID:-smoke}"

mkdir -p "$fixture/tls" "$fixture/releases"
if [ ! -f "$fixture/tls/ca.crt" ]; then
    openssl ecparam -name prime256v1 -genkey -noout -out "$fixture/tls/ca.key"
    openssl req -x509 -new -key "$fixture/tls/ca.key" -sha256 -days 2 -subj "/CN=Monitor App smoke test CA" \
        -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" -out "$fixture/tls/ca.crt"
    openssl ecparam -name prime256v1 -genkey -noout -out "$fixture/tls/server.key"
    openssl req -new -key "$fixture/tls/server.key" -subj "/CN=127.0.0.1" -out "$fixture/tls/server.csr"
    printf 'subjectAltName=IP:127.0.0.1,DNS:localhost\nbasicConstraints=CA:FALSE\nextendedKeyUsage=serverAuth\nkeyUsage=digitalSignature\n' > "$fixture/tls/server.ext"
    openssl x509 -req -in "$fixture/tls/server.csr" -CA "$fixture/tls/ca.crt" -CAkey "$fixture/tls/ca.key" -CAcreateserial \
        -sha256 -days 2 -extfile "$fixture/tls/server.ext" -out "$fixture/tls/server.crt"
fi

tool_dir="$fixture/tool"
dotnet build "$root/eng/release/MonitorApp.ReleaseTool/MonitorApp.ReleaseTool.csproj" -c Release -o "$tool_dir" --nologo -v q >&2

for spec in "$@"; do
    version="${spec%%:*}"
    deb="${spec#*:}"
    dir="$fixture/releases/$version"
    mkdir -p "$dir/artifacts"
    cp "$deb" "$dir/artifacts/monitorapp-node-ubuntu-x64-$version.deb"
    MONITOR_UPDATE_SIGNING_KEY_PKCS8_BASE64="$(tr -d '\n' < "$key_file")" \
    MONITOR_UPDATE_SIGNING_KEY_ID="$key_id" \
    MONITOR_UPDATE_PUBLIC_KEY_SPKI_BASE64="$(tr -d '\n' < "$pub_file")" \
        dotnet "$tool_dir/MonitorApp.ReleaseTool.dll" create \
            --version "$version" --minimum-compatible-version 0.1.0 --channel preview \
            --published-at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
            --artifacts-dir "$dir/artifacts" --base-url "$base_url" --output "$dir/update-manifest.json"
    cp "$dir/artifacts/"* "$dir/"
    echo "fixture: $version ready ($(sha256sum "$dir/artifacts/monitorapp-node-ubuntu-x64-$version.deb" | cut -d' ' -f1))" >&2
done
printf '%s\n' "$repo" > "$fixture/repository"
