#!/bin/bash
# Memasang/memperbarui relay Kontrol Lab di server Debian/Ubuntu. Jalankan sebagai root:
#   curl -fsSL https://raw.githubusercontent.com/ichalsmiler/kontrol-lab/main/relay/pasang-relay.sh | bash
# Port lain: ... | PORT=9000 bash
# Token dibuat sekali; pemasangan ulang tidak mengganti token supaya PC lab tidak perlu dipasang ulang.
set -euo pipefail
REPO_RAW=${REPO_RAW:-https://raw.githubusercontent.com/ichalsmiler/kontrol-lab/main}
DIR=/opt/kontrol-lab-relay
PORT=${PORT:-8790}

[ "$(id -u)" = 0 ] || { echo "Jalankan sebagai root."; exit 1; }

# Node.js 18+ diperlukan; Debian 12/13 menyediakannya di repositori bawaan.
if ! command -v node >/dev/null || [ "$(node -p 'process.versions.node.split(".")[0]')" -lt 18 ]; then
  echo "Memasang Node.js..."
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq nodejs curl >/dev/null
fi
command -v curl >/dev/null || { apt-get update -qq; DEBIAN_FRONTEND=noninteractive apt-get install -y -qq curl >/dev/null; }

install -d -m 755 "$DIR"
# Pakai relay.js di sebelah skrip bila ada (salinan lokal); bila dijalankan lewat curl, unduh dari GitHub.
HERE=$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || echo /nonexistent)
if [ -f "$HERE/relay.js" ]; then
  install -m 644 "$HERE/relay.js" "$DIR/relay.js"
else
  curl -fsSL "$REPO_RAW/relay/relay.js" -o "$DIR/relay.js.baru"
  node --check "$DIR/relay.js.baru"
  mv "$DIR/relay.js.baru" "$DIR/relay.js"
  chmod 644 "$DIR/relay.js"
fi

if [ ! -f "$DIR/config.json" ]; then
  ADMIN=$(head -c 24 /dev/urandom | base64 | tr -d '/+=' | cut -c1-32)
  CLIENT=$(head -c 24 /dev/urandom | base64 | tr -d '/+=' | cut -c1-32)
  printf '{\n  "port": %s,\n  "host": "0.0.0.0",\n  "adminToken": "%s",\n  "clientToken": "%s"\n}\n' "$PORT" "$ADMIN" "$CLIENT" > "$DIR/config.json"
fi
# Layanan berjalan sebagai pengguna sementara tanpa hak; config hanya perlu bisa dibaca.
chmod 644 "$DIR/config.json"
cat > /etc/systemd/system/kontrol-lab-relay.service <<EOF
[Unit]
Description=Relay Kontrol Lab (Exambro via internet)
After=network-online.target

[Service]
ExecStart=$(command -v node) $DIR/relay.js
WorkingDirectory=$DIR
DynamicUser=yes
# File kiriman Admin disimpan di /var/lib/kontrol-lab-relay/file (maks 5 file terbaru).
StateDirectory=kontrol-lab-relay
Restart=always
RestartSec=3
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable kontrol-lab-relay >/dev/null 2>&1
systemctl restart kontrol-lab-relay
sleep 1

REAL_PORT=$(node -p "require('$DIR/config.json').port")
if curl -fsS -m 5 "http://127.0.0.1:$REAL_PORT/api/ping" >/dev/null; then
  IP=$(hostname -I 2>/dev/null | awk '{print $1}')
  echo
  echo "Relay Kontrol Lab berjalan."
  echo "  Alamat relay (LAN) : http://${IP:-<IP-server>}:$REAL_PORT"
  echo "  Token admin        : $(node -p "require('$DIR/config.json').adminToken")"
  echo "  Token PC lab       : $(node -p "require('$DIR/config.json').clientToken")"
  echo "Isi ketiganya di KontrolLab-Admin.exe > Kontrol via Internet."
  echo "Dari luar jaringan: teruskan port TCP $REAL_PORT router ke server ini."
else
  echo "Relay gagal berjalan. Lihat: journalctl -u kontrol-lab-relay -n 30"
  exit 1
fi
