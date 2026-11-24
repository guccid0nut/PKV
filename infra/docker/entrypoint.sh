#!/bin/sh
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

set -e

export TERM=xterm

assert_writable_dir() {
    target_dir="$1"
    mkdir -p "$target_dir"
    if [ ! -w "$target_dir" ]; then
        cur_uid=$(id -u)
        cur_gid=$(id -g)
        owner_meta=$(stat -c '%u:%g (%a)' "$target_dir" 2>/dev/null || echo "unknown")
        echo "[Entrypoint][FATAL] Runtime directory '$target_dir' is NOT writable by container user ${cur_uid}:${cur_gid} (current owner/mode=${owner_meta})." >&2
        echo "[Entrypoint][FATAL] Fix: run via 'docker compose -f infra/docker/docker-compose.stack.yml up -d' (runs volume-init) or migrate volume ownership: docker run --rm --user 0:0 -v kongor-arcade-runtime:/vol debian:bookworm-slim chown -R ${cur_uid}:${cur_gid} /vol" >&2
        exit 1
    fi
    return 0
}

sync_bot_case_aliases() {
    for botdir in /app/run-server/override/bots /app/run-server/game/bots; do
        if [ -d "$botdir" ]; then
            [ -f "$botdir/behaviorlib.lua" ] && cp -f "$botdir/behaviorlib.lua" "$botdir/behaviorLib.lua"
            [ -f "$botdir/eventslib.lua" ] && cp -f "$botdir/eventslib.lua" "$botdir/eventsLib.lua"
            [ -f "$botdir/shoppinglib.lua" ] && cp -f "$botdir/shoppinglib.lua" "$botdir/shoppingLib.lua"
            [ -f "$botdir/itembehaviors.lua" ] && cp -f "$botdir/itembehaviors.lua" "$botdir/itemBehaviors.lua"
            [ -f "$botdir/illusionlib.lua" ] && cp -f "$botdir/illusionlib.lua" "$botdir/illusionLib.lua"
        fi
    done
    return 0
}

sync_maps_and_mechanics_delta() {
    if [ -d "/app/server/game/maps" ]; then
        mkdir -p /app/run-server/game/maps
        for mf in /app/server/game/maps/*.s2z; do
            if [ -e "$mf" ]; then
                mname="${mf##*/}"
                msrc_sz=$(stat -c%s "$mf" 2>/dev/null || echo 1)
                mdst_sz=$(stat -c%s "/app/run-server/game/maps/$mname" 2>/dev/null || echo 0)
                if [ "$msrc_sz" != "$mdst_sz" ] || [ "$mf" -nt "/app/run-server/game/maps/$mname" ] || [ -L "/app/run-server/game/maps/$mname" ]; then
                    echo "[Entrypoint] Syncing map archive $mname ($mdst_sz -> $msrc_sz bytes)..."
                    rm -f "/app/run-server/game/maps/$mname"
                    cp -f "$mf" "/app/run-server/game/maps/$mname"
                fi
            fi
        done
    fi
    cp -f /app/server/game/*.gamemechanics /app/run-server/game/ 2>/dev/null || true
    cp -f /app/server/game/*.cfg /app/run-server/game/ 2>/dev/null || true
    return 0
}

sync_native_guard() {
    if [ -f "/app/libkongor_native_guard.so" ]; then
        echo "[Entrypoint] Syncing freshly-built Linux Native Guard from /app to /app/run-server..."
        mkdir -p /app/run-server/compatibility
        cp -f /app/libkongor_native_guard.so /app/run-server/libkongor_native_guard.so
        cp -f /app/libkongor_native_guard.so /app/run-server/libk2_native_guard.so 2>/dev/null || true
        cp -f /app/libkongor_native_guard.so /app/run-server/compatibility/libkongor_native_guard.so 2>/dev/null || true
        cp -f /app/libkongor_native_guard.so /app/run-server/compatibility/libk2_native_guard.so 2>/dev/null || true
    elif [ -f "/app/server/linux-x64/libkongor_native_guard.so" ]; then
        echo "[Entrypoint] Syncing Linux Native Guard from /app/server/linux-x64 fallback to /app/run-server..."
        mkdir -p /app/run-server/compatibility
        cp -f /app/server/linux-x64/libkongor_native_guard.so /app/run-server/libkongor_native_guard.so
        cp -f /app/server/linux-x64/libkongor_native_guard.so /app/run-server/libk2_native_guard.so 2>/dev/null || true
        cp -f /app/server/linux-x64/libkongor_native_guard.so /app/run-server/compatibility/libkongor_native_guard.so 2>/dev/null || true
        cp -f /app/server/linux-x64/libkongor_native_guard.so /app/run-server/compatibility/libk2_native_guard.so 2>/dev/null || true
    fi
    return 0
}

# Verify mounted /opt/hon/config/server[/logs] writability for non-root uid=10001 K2 workers (dedicated & arcade)
if [ -d "/opt/hon/config" ]; then
    assert_writable_dir "/opt/hon/config"
    assert_writable_dir "/opt/hon/config/server"
    assert_writable_dir "/opt/hon/config/server/logs"
    echo "[Entrypoint] Verified /opt/hon/config/server and /opt/hon/config/server/logs are writable by uid=$(id -u)."
fi

# If /app/server exists (mounted volume from host games/server)
if [ -d "/app/server/linux-x64" ]; then
    assert_writable_dir "/app/run-server"
    # Fast-path: If container was built with pre-baked assets at /app/run-server, skip full binary/s2z copy loop
    if { [ "$COMPEL_PREBAKED_IMAGE" = "true" ] || [ "$COMPEL_PREBAKED_IMAGE" = "1" ]; } && [ -f "/app/run-server/hon-x86_64-server" ]; then
        echo "[Entrypoint] Pre-baked runtime image detected (COMPEL_PREBAKED_IMAGE=true). Syncing map/override deltas (<100ms startup)."
        sync_maps_and_mechanics_delta
        if [ -d "/app/server/override" ]; then
            rm -rf /app/run-server/override
            mkdir -p /app/run-server/override
            (cd /app/server/override && find . \( -name "*.entity" -o -name "*.lua" -o -name "*.mdf" -o -name "*.cfg" -o -name "*.txt" -o -name "*.xml" -o -name "*.str" -o -name "*.effect" -o -name "*.material" -o -name "*.clip" -o -name "*.gamemechanics" -o -name "*.upgrades" -o -name "*.bot" -o -name "*.botmetadata" \) | tar -cf - -T - | tar -xf - -C /app/run-server/override/)
        fi
        sync_bot_case_aliases
        sync_native_guard
        export COMPEL_INSTALLATION_DIRECTORY=/app/run-server
    elif [ -f "/app/run-server/hon-x86_64-server" ] && [ "$COMPEL_FORCE_SYNC" != "true" ] && [ ! "/app/server/linux-x64/hon-x86_64-server" -nt "/app/run-server/hon-x86_64-server" ]; then
        echo "[Entrypoint] Dedicated server runtime at /app/run-server is up-to-date. Skipping redundant asset copy (<200ms startup)."
        sync_maps_and_mechanics_delta
        # Refresh server override gameplay logic even on fast-path
        if [ -d "/app/server/override" ]; then
            echo "[Entrypoint] Refreshing server override gameplay logic on fast-path..."
            rm -rf /app/run-server/override
            mkdir -p /app/run-server/override
            (cd /app/server/override && find . \( -name "*.entity" -o -name "*.lua" -o -name "*.mdf" -o -name "*.cfg" -o -name "*.txt" -o -name "*.xml" -o -name "*.str" -o -name "*.effect" -o -name "*.material" -o -name "*.clip" -o -name "*.gamemechanics" -o -name "*.upgrades" -o -name "*.bot" -o -name "*.botmetadata" \) | tar -cf - -T - | tar -xf - -C /app/run-server/override/)
        fi
        sync_bot_case_aliases
        if [ -d "/app/server/linux-x64/compatibility" ]; then
            echo "[Entrypoint] Syncing compatibility shims to /app/run-server/compatibility..."
            mkdir -p /app/run-server/compatibility
            cp -f /app/server/linux-x64/compatibility/* /app/run-server/compatibility/ 2>/dev/null || true
        fi
        sync_native_guard
        if [ -f "/app/libhon-ping-shim.so" ]; then
            mkdir -p /app/run-server/compatibility
            cp -f /app/libhon-ping-shim.so /app/run-server/compatibility/libhon-ping-shim.so
            cp -f /app/libhon-ping-shim.so /app/run-server/libhon-ping-shim.so 2>/dev/null || true
        fi
        # Remove stale 2015 libstdc++.so.6 / libgcc_s.so.1 so modern Debian Bookworm system libs (libxml2/libicuuc) resolve GLIBCXX_3.4.30 cleanly
        rm -f /app/run-server/libs-x86_64/libstdc++.so.6 /app/run-server/libs-x86_64/libgcc_s.so.1 2>/dev/null || true
        export COMPEL_INSTALLATION_DIRECTORY=/app/run-server
    else
        echo "[Entrypoint] Initializing dedicated server runtime at /app/run-server..."
        mkdir -p /app/run-server
        # Remove any existing symlinks inside /app/run-server so everything is guaranteed to be a real file on ext4
        find /app/run-server -type l -delete 2>/dev/null || true

    # Copy linux-x64 binary structure (including game/ *.so libraries)
    cp -rn /app/server/linux-x64/* /app/run-server/
    # Force copy updated libraries and binaries so host binary patches always propagate
    cp -f /app/server/linux-x64/*.so /app/run-server/ 2>/dev/null || true
    cp -f /app/server/linux-x64/game/*.so /app/run-server/game/ 2>/dev/null || true
    cp -f /app/server/linux-x64/hon-x86_64-server /app/run-server/ 2>/dev/null || true
    if [ -d "/app/server/linux-x64/compatibility" ]; then
        mkdir -p /app/run-server/compatibility
        cp -f /app/server/linux-x64/compatibility/* /app/run-server/compatibility/ 2>/dev/null || true
    fi
    if [ -f "/app/libhon-ping-shim.so" ]; then
        mkdir -p /app/run-server/compatibility
        cp -f /app/libhon-ping-shim.so /app/run-server/compatibility/libhon-ping-shim.so
        cp -f /app/libhon-ping-shim.so /app/run-server/libhon-ping-shim.so 2>/dev/null || true
    fi
    
    # Copy base assets (no symlinks)
    if [ -L "/app/run-server/base" ]; then
        rm -rf /app/run-server/base
    fi
    mkdir -p /app/run-server/base
    if [ -d "/app/server/base" ]; then
        cp -rn /app/server/base/* /app/run-server/base/ 2>/dev/null || true
    fi
    
    # Copy game resource archives and configs (no symlinks)
    mkdir -p /app/run-server/game
    for f in /app/server/game/*.s2z; do
        if [ -e "$f" ]; then
            fname="${f##*/}"
            src_sz=$(stat -c%s "$f" 2>/dev/null || stat -f%z "$f" 2>/dev/null || echo 1)
            dst_sz=$(stat -c%s "/app/run-server/game/$fname" 2>/dev/null || stat -f%z "/app/run-server/game/$fname" 2>/dev/null || echo 0)
            if [ "$src_sz" != "$dst_sz" ] || [ -L "/app/run-server/game/$fname" ]; then
                echo "[Entrypoint] Syncing $fname (size changed: $dst_sz -> $src_sz)..."
                rm -f "/app/run-server/game/$fname" 2>/dev/null || true
                cp -f "$f" "/app/run-server/game/$fname" 2>/dev/null || true
            fi
        fi
    done
    cp -f /app/server/game/*.cfg /app/run-server/game/ 2>/dev/null || true
    cp -f /app/server/game/*.resources /app/run-server/game/ 2>/dev/null || true
    
    # Copy game content packages (no symlinks)
    if [ -d "/app/server/game/content" ]; then
        echo "[Entrypoint] Copying game content packages..."
        if [ -L "/app/run-server/game/content" ]; then
            rm -rf /app/run-server/game/content
        fi
        mkdir -p /app/run-server/game/content
        cp -rn /app/server/game/content/* /app/run-server/game/content/ 2>/dev/null || true
    fi

    # Safely copy map archives (~50 MB) directly into runtime directory (force overwrite so updates are applied; skip untracked *.bak*)
    if [ -d "/app/server/game/maps" ]; then
        mkdir -p /app/run-server/game/maps
        for mf in /app/server/game/maps/*.s2z; do
            [ -e "$mf" ] && cp -f "$mf" /app/run-server/game/maps/
        done
    fi

    # Safely copy bot scripts if present
    if [ -d "/app/server/game/bots" ]; then
        mkdir -p /app/run-server/game/bots
        cp -rn /app/server/game/bots/* /app/run-server/game/bots/
    fi
    if [ -d "/app/server/game/teambot" ]; then
        mkdir -p /app/run-server/game/teambot
        cp -rn /app/server/game/teambot/* /app/run-server/game/teambot/
    fi

    # Copy server heroes directory (no symlinks) for instant local ext4 access
    rm -rf /app/run-server/game/heroes 2>/dev/null || true
    if [ -d "/app/server/game/heroes" ]; then
        echo "[Entrypoint] Copying server heroes directory..."
        mkdir -p /app/run-server/game/heroes
        (cd /app/server/game/heroes && find . \( -name "*.entity" -o -name "*.lua" -o -name "*.mdf" -o -name "*.cfg" -o -name "*.txt" -o -name "*.xml" \) | tar -cf - -T - 2>/dev/null | tar -xf - -C /app/run-server/game/heroes/ 2>/dev/null || true)
    fi

    # Copy server entity directories (buildings, npcs, stringtables, common, shared, core, world, items, triggers, effects, ui)
    for d in buildings npcs stringtables common shared core world items triggers effects ui; do
        if [ -d "/app/server/game/$d" ]; then
            echo "[Entrypoint] Copying server $d directory..."
            rm -rf "/app/run-server/game/$d" 2>/dev/null || true
            mkdir -p "/app/run-server/game/$d"
            (cd "/app/server/game/$d" && find . \( -name "*.entity" -o -name "*.lua" -o -name "*.mdf" -o -name "*.cfg" -o -name "*.txt" -o -name "*.xml" -o -name "*.str" -o -name "*.effect" -o -name "*.material" -o -name "*.clip" -o -name "*.model" -o -name "*.smdl" \) | tar -cf - -T - 2>/dev/null | tar -xf - -C "/app/run-server/game/$d/" 2>/dev/null || true)
        fi
    done

    # Copy server override directory (gameplay logic files)
    rm -rf /app/run-server/override 2>/dev/null || true
    if [ -d "/app/server/override" ]; then
        echo "[Entrypoint] Copying server override gameplay logic..."
        mkdir -p /app/run-server/override
        (cd /app/server/override && find . \( -name "*.entity" -o -name "*.lua" -o -name "*.mdf" -o -name "*.cfg" -o -name "*.txt" -o -name "*.xml" -o -name "*.str" -o -name "*.effect" -o -name "*.material" -o -name "*.clip" -o -name "*.gamemechanics" -o -name "*.upgrades" -o -name "*.bot" -o -name "*.botmetadata" \) | tar -cf - -T - 2>/dev/null | tar -xf - -C /app/run-server/override/ 2>/dev/null || true)
    fi
    sync_bot_case_aliases

    # Purge any stale /app/run-server/shared directory so shared assets resolve strictly under game/shared (/shared/...)
    rm -rf /app/run-server/shared 2>/dev/null || true

    # Copy root game overrides and manifests
    cp -f /app/server/game/*.gamemechanics /app/run-server/game/ 2>/dev/null || true
    cp -f /app/server/game/*.upgrades /app/run-server/game/ 2>/dev/null || true
    cp -f /app/server/game/checksums /app/run-server/game/ 2>/dev/null || true
    cp -f /app/server/game/*.xml /app/run-server/game/ 2>/dev/null || true
    cp -f /app/server/game/*.lua /app/run-server/game/ 2>/dev/null || true

    # Copy Linux Native Guard to runtime directory
    sync_native_guard
    # Remove stale 2015 libstdc++.so.6 / libgcc_s.so.1 so modern Debian Bookworm system libs (libxml2/libicuuc) resolve GLIBCXX_3.4.30 cleanly
    rm -f /app/run-server/libs-x86_64/libstdc++.so.6 /app/run-server/libs-x86_64/libgcc_s.so.1 2>/dev/null || true
    
    # Ensure strictly ZERO symlinks exist anywhere in /app/run-server
    find /app/run-server -type l -delete 2>/dev/null || true
    echo "[Entrypoint] Local ext4 storage verification: files=$(find /app/run-server -type f | wc -l), symlinks=$(find /app/run-server -type l | wc -l)"
    
    export COMPEL_INSTALLATION_DIRECTORY=/app/run-server
    fi
elif [ -d "/app/server" ]; then
    export COMPEL_INSTALLATION_DIRECTORY=/app/server
fi

# Re-link external RL runtime weight volume without disturbing /app/run-server/override refresh
if [ -d "/app/server-rl-runtime" ] && [ -w "/app/run-server/override" ]; then
    mkdir -p /app/run-server/override/bots
    rm -rf /app/run-server/override/bots/rl_runtime 2>/dev/null || true
    ln -sfn /app/server-rl-runtime /app/run-server/override/bots/rl_runtime
    echo "[Entrypoint] Symlinked RL runtime directory (/app/run-server/override/bots/rl_runtime -> /app/server-rl-runtime)."
fi

# Forward local loopback chat server ports to internal Compose chat-server or host.docker.internal
CHAT_FWD_HOST="${CHAT_SERVER_INTERNAL_HOST:-host.docker.internal}"
if getent hosts "$CHAT_FWD_HOST" >/dev/null 2>&1; then
    echo "[Entrypoint] Forwarding 127.0.0.1:11032-11033 to ${CHAT_FWD_HOST}..."
    socat TCP-LISTEN:11032,fork,reuseaddr "TCP:${CHAT_FWD_HOST}:11032" &
    socat TCP-LISTEN:11033,fork,reuseaddr "TCP:${CHAT_FWD_HOST}:11033" &
elif getent hosts host.docker.internal >/dev/null 2>&1; then
    echo "[Entrypoint] Forwarding 127.0.0.1:11032-11033 to host.docker.internal (fallback)..."
    socat TCP-LISTEN:11032,fork,reuseaddr TCP:host.docker.internal:11032 &
    socat TCP-LISTEN:11033,fork,reuseaddr TCP:host.docker.internal:11033 &
fi

# Forward local loopback master-server/CDN replay upload port (127.0.0.1:55555) to internal master-server or host.docker.internal
if getent hosts master-server >/dev/null 2>&1; then
    echo "[Entrypoint] Forwarding 127.0.0.1:55555 to master-server:55555 (replay upload & CDN bridge)..."
    socat TCP-LISTEN:55555,fork,reuseaddr TCP:master-server:55555 &
elif getent hosts host.docker.internal >/dev/null 2>&1; then
    echo "[Entrypoint] Forwarding 127.0.0.1:55555 to host.docker.internal:55555 (fallback)..."
    socat TCP-LISTEN:55555,fork,reuseaddr TCP:host.docker.internal:55555 &
fi

# Linux Native Guard (/app/run-server/libkongor_native_guard.so & libk2_native_guard.so) is injected
# exclusively into hon-x86_64-server child workers by COMPEL (MatchServerManagerSupervisor) so
# CoreCLR's SIGSEGV handler in /app/COMPEL is never disturbed.
if [ -f "${COMPEL_INSTALLATION_DIRECTORY:-/app/run-server}/libkongor_native_guard.so" ]; then
    echo "[Entrypoint] Linux Native Guard staged at ${COMPEL_INSTALLATION_DIRECTORY:-/app/run-server}/libkongor_native_guard.so (supervised worker injection ready)."
fi

exec /app/COMPEL "$@"
